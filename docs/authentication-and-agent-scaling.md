# Authentication placement and app registration strategy

This note covers two architectural questions:

1. Should APIM own the OAuth on-behalf-of (OBO) exchange?
2. How should app registrations be organized as more agents are added?

It describes the standard delegated-user flow in this repository. Recommendations for
larger deployments are explicitly distinguished from the current implementation; this
document does not change runtime behavior or provision registrations.

## Summary

- **Keep APIM as the authentication enforcement layer and the adapter as the
  delegated-token broker for the current architecture.**
- **Create registrations for independently trusted applications and API boundaries,
  not automatically for every agent or adapter replica.**
- **Prefer federated or certificate credentials over client secrets, and reduce
  consent friction with preauthorization rather than duplicate registrations.**

## 1. Authentication: APIM or the adapter?

### Current separation of responsibilities

```text
Browser
  | delegated token for the adapter API
  v
APIM
  | validates the token, requires access_as_user, applies rate limits
  | forwards the original Authorization header
  v
Adapter
  | validates the caller, selects the provider, performs OBO
  | obtains a resource-specific delegated token
  v
Foundry or Copilot Studio
```

The gateway behavior is implemented in the
[delegated operation policy](../infra/policies/citadel-delegated-operation.xml).
The shared `OboTokenBroker` in
[CopilotStudioInvoker.cs](../src/FoundryCopilotA2A.Adapter/CopilotStudioInvoker.cs)
uses MSAL for token acquisition.

| Responsibility | Recommended owner |
| --- | --- |
| User sign-in and acquisition of the initial API token | Frontend and Entra |
| Gateway token validation, required scopes, and rate limits | APIM |
| Application authorization and user-scoped conversation isolation | Adapter |
| OBO exchange for the selected downstream resource | Adapter |
| Native A2A connection authorization and consent | Foundry/Copilot Studio and Entra |

### Why keep the OBO exchange in the adapter?

**APIM's next hop is the adapter, not Power Platform.** If APIM exchanges the
incoming token for a Power Platform token and forwards that as the adapter's
`Authorization` header, the adapter should reject it: the audience is wrong.
The same applies to a Foundry token. Moving the exchange requires an explicit,
secured gateway-to-adapter contract that preserves the authenticated caller.
Disabling adapter validation or trusting caller-supplied identity headers is not
equivalent to today's design.

**The OBO client identity must match the incoming token's audience.** APIM could
perform the exchange using the existing backend registration's credentials.
An unrelated APIM registration cannot redeem a token issued for the adapter.
Changing the API identity would require updating callers' requested scopes and
grants, including frontend and native A2A connections.

**There are separate delegation steps.** A native chain is not one token flowing
unchanged through every component:

```text
Browser -> adapter OBO -> Foundry or Copilot Studio orchestrator
Orchestrator -> per-user OAuth connection -> adapter-audience token
Adapter -> OBO -> Power Platform -> Copilot Studio specialist
```

The entry exchange targets Foundry or Power Platform, depending on the selected
provider. A later specialist callback is a separate request and exchange. Moving
OBO to APIM does not eliminate the native OAuth connection's authorization,
consent, or reauthentication lifecycle.

**Token-management complexity moves rather than disappears.** APIM would take
responsibility for confidential-client credentials, user/resource-isolated
caching, expiry, consent and Conditional Access failures, and error propagation.
The adapter already has a shared MSAL client and its token cache.

**APIM is optional today.** The application supports direct adapter access and an
Azure-independent mock backend. A gateway-owned exchange would need an explicit
alternative for those paths rather than making local development depend on APIM.

Adding APIM's `authentication-managed-identity` policy is not an OBO implementation:
using the resulting application token in place of the delegated token changes the
identity semantics. Likewise, a stored OAuth connection in APIM credential manager
is not automatically an exchange of the incoming user's assertion. Credential
manager acquires and refreshes tokens for a stored connection; the attended
(user-delegated) mode still binds a connection to a user identity rather than
redeeming the caller's assertion the way OBO does.

### OBO protocol constraints that affect this design

These are documented Microsoft Entra constraints, not implementation choices. Each
one limits how far the exchange can be relocated or how registrations can be reused.

| Constraint | Consequence here |
| --- | --- |
| The assertion's `aud` must be the application performing the exchange. | The adapter's API audience and OBO client must be the same registration. A mismatch fails with `AADSTS500131`. |
| OBO works only for user principals. | An app-only caller (for example a project managed identity) cannot produce same-user delegation; it fails with `AADSTS7000114`. |
| Applications with custom signing keys cannot act as the middle tier. | Do not configure SSO with a custom signing key on the adapter registration. |
| Middle-tier tokens must not be relayed back to clients. | Never return the Foundry or Power Platform token to the browser or to a calling agent. |

The adapter already inspects the caller's token before exchanging it: it verifies
that the token was issued for its own audience and detects app-only callers, whose
handling differs by provider. See
[CopilotStudioInvoker.cs](../src/FoundryCopilotA2A.Adapter/CopilotStudioInvoker.cs).

Relaying a middle-tier token onward increases interception risk and prevents token
binding, claim step-up (such as MFA or sign-in frequency), and device-based
policies from being satisfied correctly.

### When gateway-owned OBO can make sense

Centralizing OBO becomes attractive when many independently implemented backends
need a centrally operated delegation service with consistent credentials,
policies, and auditing. APIM can acquire onward tokens through custom policies,
including `send-request` to the identity provider.

Treat that as an identity-boundary redesign, with these requirements:

- Keep the OBO client identity aligned with the incoming API audience.
- Define how verified user context reaches the adapter without accepting spoofed
  headers or treating a downstream-resource token as an adapter API token.
- Secure gateway-to-backend communication and restrict direct backend access when
  gateway enforcement must be mandatory.
- Isolate token caches by the relevant user/assertion, tenant, client, resource,
  and scopes; honor expiry and never log credentials or bearer tokens.
- Preserve consent/Conditional Access error handling and streaming behavior.
- Retain the mock backend and deliberately support any required no-APIM path.

**Recommendation:** keep the current split unless centralized credential and
delegation management becomes a concrete requirement across multiple services.
Moving OBO alone is not a solution to native connection consent prompts.

### APIM hardening that applies to the current design

These practices are independent of where the OBO exchange runs.

- **Restrict direct backend ingress when the gateway must be authoritative.**
  Advertising an APIM URL is not an ingress restriction. A caller holding a valid
  delegated token can reach an unrestricted adapter origin directly and bypass
  gateway-specific rate limits and request validation. Enforce this with network
  restrictions such as private endpoints, VNet integration, or IP restrictions on
  the adapter host.
- **Deploy a web application firewall upstream** of APIM (Azure Front Door or
  Application Gateway) for defense in depth.
- **Consider `validate-azure-ad-token` for Entra-issued tokens.** The repository
  currently uses the generic `validate-jwt` policy with an OpenID configuration
  URL, which is valid. The Entra-specific policy expresses the same validation more
  directly and can additionally constrain **which client applications** may call an
  API through `client-application-ids` — useful when many frontends and native
  connections share one adapter API.
- **Check for expected permissions before making authorization decisions.** The
  gateway already requires the `scp` claim and the tenant; keep application-level
  authorization in the adapter rather than assuming gateway presence implies it.
- **Keep the caller's `Authorization` header intact** on this delegated path, and
  do not add backend authentication policies that replace the user's identity.

## 2. App registrations when scaling agents

### What is registered today?

The backend registration represents the **logical adapter API and its OBO client**,
not each individual agent or running adapter instance.

The shared tenant, client ID, and credential belong to `CopilotStudioOptions`.
Individual agents have IDs, endpoints, and routing configuration rather than their
own OBO credentials. See
[AdapterOptions.cs](../src/FoundryCopilotA2A.Adapter/AdapterOptions.cs).

| Component | Current registration arrangement |
| --- | --- |
| Chatbot frontend | Its own public SPA registration |
| Diagnostic console frontend | Its own public SPA registration |
| Adapter API and OBO exchanges | One shared confidential registration |
| APIM | No additional registration for this delegated-token path |
| Specialists behind the adapter | No additional registration merely because an agent is added |

One frontend plus the adapter normally means **two runtime registrations**.
Both frontends plus the shared adapter normally means **three**. Adding another
20 specialists does not automatically increase that number.

The sample also reuses the backend registration for native OAuth connections,
including the Foundry identity-passthrough connection. That reuse is distinct from
the stronger client/API separation recommended below.

These counts describe the runtime design, not an inventory of a deployed tenant.
Optional administrative clients use separate registrations and are not included.
For example, connector-consent-bypass administration should not add administrative
permissions to the adapter runtime identity.

### Recommended granularity

Apply this arrangement per environment, keeping production identities separate
from development and test identities:

| Identity or component | Recommended granularity |
| --- | --- |
| Frontend SPA | One registration per distinct frontend application |
| Adapter API and OBO client | One registration per logical backend service with common ownership, permissions, and lifecycle |
| Adapter replicas | Reuse that logical service's registration |
| Native orchestrator OAuth client | Separate registration per independently trusted integration when isolating credentials and consent |
| Individual specialist | Separate registration only when it represents an independent API or OAuth-client security boundary |
| APIM | No extra registration merely to validate and forward delegated tokens; its infrastructure identity is a separate concern |

Ten specialists belonging to one co-owned service can reasonably share an adapter
registration. An independently operated payroll service should not inherit the
same identity and permissions simply because it uses the same adapter code.

Separate registrations and service boundaries when ownership, downstream
permissions, credential exposure, environment, or isolation requirements differ.
Sharing code does not make independently operated deployments one application.
Conversely, scaling one application to more replicas does not create new
application identities.

### Choose the credential type before multiplying registrations

Every confidential registration added is another credential to protect, rotate, and
audit. Microsoft's guidance ranks credential types in this order:

| Preference | Credential | Applicability here |
| --- | --- | --- |
| Best | Managed identity as a federated credential (workload identity federation) | Azure-hosted adapter; removes credential management entirely |
| Good | Certificate credential, stored in Key Vault | Where federation is unavailable |
| Discouraged | Client secret | The sample's default, for local-development simplicity |

The sample uses `COPILOT_STUDIO_CLIENT_SECRET` and stores it as a Key Vault
reference resolved by a managed identity in the deployed configuration. That
protects the secret at rest, but it is still a password credential.

**Decide this before adding registrations, not after.** Secret sprawl and rotation
failures scale with the number of confidential registrations, whereas federated
credentials do not. Note that a managed identity cannot replace the adapter
registration itself: the adapter must remain a registered API that exposes a scope
and performs OBO as that application.

Also avoid placing credentials on public client registrations. A frontend SPA must
never hold a secret.

### Reduce consent friction instead of duplicating registrations

Adding frontends and agents multiplies consent, not just registrations. Two Entra
mechanisms address this directly:

- **`knownClientApplications`** on the adapter API registration lets a client's
  consent prompt cover both the client-to-API grant and the API's downstream
  permissions in one experience, using the `.default` scope.
- **`preAuthorizedApplications`** lets the adapter API declare that specific client
  applications always receive named scopes, removing the consent prompt for that
  client-to-API grant.

Preauthorization is the cleaner option for a controlled set of first-party
frontends and native connections, because it grants only the named scopes.

Two cautions apply. Do not combine `.default` with other delegated scopes in the
same request; that fails with `AADSTS70011`. And neither mechanism grants the
adapter's own downstream permissions, so the backend-to-Power-Platform grant still
requires user or administrator consent. That asymmetry is why sign-in can succeed
while the OBO call fails with `AADSTS65001`.

### Redirect URIs, owners, and directory limits

These matter specifically because this design adds a Web redirect URI to the
backend registration for **each** native OAuth connection.

- Keep the redirect URI list small and reviewed; trim unused entries as connections
  are replaced. Use HTTPS, never wildcards, and only domains you own and monitor.
- Assign and maintain owners on every registration; avoid orphaned applications.
  A single application supports at most 100 owners, but keep the list small.
- Keep registrations single-tenant (`AzureADMyOrg`) unless multitenant access is an
  actual requirement.
- Request least-privilege permissions, and prefer delegated permissions over
  application permissions on this user-delegated path.

Directory limits reward consolidation at real boundaries rather than per-agent
registrations: an application manifest accepts at most 1,200 entries, and a
non-administrator can create no more than 250 directory resources, which includes
app registrations. Manage registrations as infrastructure-as-code once more than a
few exist, so grants, redirect URIs, and credentials stay reviewable.

### Separate native OAuth clients from the adapter as trust boundaries grow

For independently trusted integrations, separate these roles:

- **Native orchestrator OAuth client:** owns its callback URLs and credential;
  receives permission to request the adapter's `access_as_user` scope.
- **Adapter API/OBO client:** owns the API audience and downstream delegated
  permissions; retains its own credential.

This avoids giving a provider-managed OAuth connection the adapter's OBO
application credential. Multiple agents within the same trusted integration can
still use that integration's client registration.

```text
Frontend SPA or native OAuth client registration
  | requests api://<adapter-client-id>/access_as_user
  v
APIM -> adapter API registration
  | performs OBO as the adapter registration
  v
Foundry or Power Platform
```

The caller's client registration can differ from the adapter registration.
The incoming token's `aud` identifies the adapter API; `azp` or `appid` identifies
the client that obtained it. The adapter's API audience and OBO client identity
must correspond to the same application.

This recommendation separates the **initiating OAuth client** from the API. It
does not split the adapter API and its OBO identity into unrelated registrations.
Adopting it requires configuring the new client grants, callback URLs, credentials,
and native connections; it is not the sample's default registration workflow.

For example:

| Arrangement within one environment | Runtime registration count |
| --- | --- |
| Two frontends and one logical adapter, using the current native-client reuse pattern | 3 |
| Two frontends, one logical adapter, and one separately isolated native OAuth client | 4 |
| The previous arrangement with a second independently trusted native OAuth client | 5 |

Adding specialists or replicas within those existing boundaries does not by itself
increase the count. Native connection definitions and per-user authorizations may
still grow independently of the number of app registrations.

### Agent identities are a separate concern

- An **agent ID** identifies a Foundry or Copilot Studio agent resource.
- A platform-managed **agent identity** governs the agent's own access where
  supported.
- An **OAuth connection** represents connection configuration and potentially a
  user-specific authorization.
- The **adapter app registration** identifies the API callers access and the
  confidential client performing OBO.

These concepts do not require a one-to-one mapping.

A shared `access_as_user` scope is also not a per-agent authorization policy.
If different users should access different specialists, enforce that explicitly
at the adapter/gateway and downstream provider. Different agent routes alone do
not create isolation, and creating registrations without corresponding
authorization enforcement does not establish it either.

**Recommendation:** keep registrations few within one logical service, but
separate them at real ownership, permission, credential, and environment
boundaries rather than according to agent count.

## Further reading

### Repository guides

- [Frontend and backend registration setup](spa-app-registration.md)
- [APIM and local adapter topology](citadel-local.md)
- [Native A2A connection lifecycle and consent](copilot-studio-a2a-connections.md)
- [Operational CLI](../src/FoundryCopilotA2A.Cli/README.md)

### Microsoft documentation

- [Microsoft Entra OBO flow and audience requirements](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
- [APIM authentication and authorization patterns](https://learn.microsoft.com/azure/api-management/authentication-authorization-overview)
- [APIM credential manager](https://learn.microsoft.com/azure/api-management/credentials-overview)
- [Validate Microsoft Entra token policy](https://learn.microsoft.com/azure/api-management/validate-azure-ad-token-policy)
- [App registration and Zero Trust guidance](https://learn.microsoft.com/security/zero-trust/develop/app-registration)
- [Security best practices for application properties](https://learn.microsoft.com/entra/identity-platform/security-best-practices-for-app-registration)
- [Microsoft identity platform integration checklist](https://learn.microsoft.com/entra/identity-platform/identity-platform-integration-checklist)
- [Configure an application to trust a managed identity](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity)
- [Microsoft Entra service limits and restrictions](https://learn.microsoft.com/entra/identity/users/directory-service-limits-restrictions)
