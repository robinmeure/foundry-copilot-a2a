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
- **If the gateway must be a trust boundary, use the API Hub pattern:** the hub
  gets its own audience and exchanges for the adapter's audience, rather than
  exchanging for a downstream resource the adapter would have to reject.

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

### Option: the API Hub pattern (gateway as its own audience)

There is a variant of gateway-owned delegation that does work cleanly. Instead of
the gateway exchanging for a *downstream* resource, the gateway becomes its own
OAuth resource and exchanges for the **adapter's** audience. Each hop then receives
a token minted for itself, so the audience invariant holds at every step.

```text
UI            token A   aud = api://<hub>        (user oid)
  v
API Hub       OBO: assertion = A, client = hub
              token B   aud = api://<adapter>    (same oid)
  v
Adapter       validates B, then performs its own OBO
              token C   aud = Power Platform or Foundry (same oid)
  v
Copilot Studio or Foundry
```

This is chained delegation: a middle tier may request further tokens for downstream
APIs on behalf of the same user. Contrast it with the design that does not work,
where the gateway exchanges for Power Platform and forwards a token whose audience
the adapter must reject.

**The adapter needs no code change.** It already verifies that the caller's token
was issued for its own audience and rejects app-only callers. A hub-issued token is
delegated, carries `scp`, and has the adapter's `aud`; only `azp`/`appid` differs,
and the adapter validates the resource side rather than the client application.

#### What the pattern buys, and what it does not

The browser only ever holds a hub-audience token, so a stolen frontend token cannot
be replayed directly against the adapter. That is genuine identity segmentation
rather than policy enforcement alone.

It is only complete if native A2A connections also target the hub. If a Foundry or
Copilot Studio connection keeps requesting the adapter scope directly,
adapter-audience tokens still exist outside the hub. Keep network-level ingress
restriction as the backstop in either case. The pattern also does not remove the
adapter's own OBO exchange or the native connection consent lifecycle.

#### App registration setup

This adds **one** registration. The adapter registration keeps its current role.

| Registration | Change |
| --- | --- |
| Frontend SPA | Request the hub scope instead of the adapter scope |
| **API Hub (new)** | Confidential client; exposes `api://<hub-id>/access_as_user`; holds delegated permission to the adapter API |
| Adapter API | Unchanged audience, scope, and downstream permissions |

Configure it in this order:

1. **Register the hub** as a single-tenant application. Set its Application ID URI
   to `api://<hub-client-id>` and expose a delegated scope named `access_as_user`.
   Do not configure a custom signing key: that would disqualify it as an OBO middle
   tier.
2. **Grant the hub delegated permission** to the adapter API's `access_as_user`
   scope, then consent to it. This is the grant that lets the hub's exchange
   succeed.
3. **Give the hub a credential without a secret.** Assign a user-assigned managed
   identity to the API Management instance, then add a **federated identity
   credential** on the hub registration with scenario *Managed Identity*, issuer
   `https://login.microsoftonline.com/<tenant-id>/v2.0`, subject set to the managed
   identity's **object (principal) ID**, and audience `api://AzureADTokenExchange`.
   APIM can then authenticate as the hub with no stored secret.
4. **Preauthorize the clients** to suppress avoidable prompts. On the hub
   registration, preauthorize the frontend SPA and any native OAuth client for
   `access_as_user`. On the adapter registration, preauthorize the hub.
5. **Register redirect URIs on the registration whose scope is requested.** An
   interactive consent callback belongs to the application that owns the requested
   scope. Once connections request the hub scope, the generated callbacks become
   Web redirects on the **hub** registration rather than the adapter's.

Three delegated grants now exist, and none implies another:

| Grant | Client | Resource |
| --- | --- | --- |
| 1 | Frontend SPA or native OAuth client | Hub `access_as_user` |
| 2 | API Hub | Adapter `access_as_user` |
| 3 | Adapter API | Power Platform `CopilotStudio.Copilots.Invoke`, optional Foundry `user_impersonation` |

Grant 3 is unchanged and still fails late with `AADSTS65001` when missing, because
sign-in and gateway validation succeed before the adapter's exchange runs.

#### Gateway policy

`authentication-managed-identity` supports `output-token-variable-name`, which is
what makes the secretless client assertion possible.

```xml
<validate-azure-ad-token tenant-id="{{entra-tenant-id}}"
                         output-token-variable-name="callerJwt">
  <audiences><audience>api://{{hub-client-id}}</audience></audiences>
  <required-claims>
    <claim name="scp" match="any" separator=" "><value>access_as_user</value></claim>
  </required-claims>
</validate-azure-ad-token>

<set-variable name="cacheKey" value="@{
    var jwt = (Jwt)context.Variables["callerJwt"];
    return $"obo:{jwt.Claims.GetValueOrDefault("tid","?")}:" +
           $"{jwt.Claims.GetValueOrDefault("oid","?")}:adapter";
}" />
<cache-lookup-value key="@((string)context.Variables["cacheKey"])"
                    variable-name="adapterToken" caching-type="internal" />

<choose>
  <when condition="@(!context.Variables.ContainsKey("adapterToken"))">
    <authentication-managed-identity resource="api://AzureADTokenExchange"
        client-id="{{apim-identity-client-id}}"
        output-token-variable-name="clientAssertion" ignore-error="false" />

    <send-request mode="new" response-variable-name="obo" timeout="20">
      <set-url>https://login.microsoftonline.com/{{entra-tenant-id}}/oauth2/v2.0/token</set-url>
      <set-method>POST</set-method>
      <set-header name="Content-Type" exists-action="override">
        <value>application/x-www-form-urlencoded</value>
      </set-header>
      <set-body>@{
        var caller = context.Request.Headers
            .GetValueOrDefault("Authorization","").Split(' ').Last();
        return "grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer"
          + "&client_id={{hub-client-id}}"
          + "&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
          + "&client_assertion=" + System.Net.WebUtility.UrlEncode((string)context.Variables["clientAssertion"])
          + "&assertion=" + System.Net.WebUtility.UrlEncode(caller)
          + "&scope=" + System.Net.WebUtility.UrlEncode("api://{{adapter-client-id}}/access_as_user")
          + "&requested_token_use=on_behalf_of";
      }</set-body>
    </send-request>

    <set-variable name="adapterToken" value="@(((IResponse)context.Variables["obo"])
        .Body.As<JObject>(preserveContent: true)["access_token"].ToString())" />
    <cache-store-value key="@((string)context.Variables["cacheKey"])"
                       value="@((string)context.Variables["adapterToken"])"
                       duration="@(((IResponse)context.Variables["obo"])
                           .Body.As<JObject>(preserveContent: true)["expires_in"].Value<int>() - 300)"
                       caching-type="internal" />
  </when>
</choose>

<set-header name="Authorization" exists-action="override">
  <value>@("Bearer " + (string)context.Variables["adapterToken"])</value>
</set-header>
```

Add an explicit non-200 branch on the `obo` response that returns 401 or 403 with
the Entra error, so consent and Conditional Access failures do not surface as 500s.

#### Operational requirements

- **Key the cache by user.** Include `oid` and `tid`. Keying only by scope would
  return one user's token to another. Prefer `caching-type="internal"` so tokens
  stay in gateway memory rather than an external cache.
- **Expire before the token does.** Derive the cache duration from `expires_in`
  minus a safety margin, and never cache refresh tokens.
- **Treat policy-edit rights as a security boundary.** Anyone who can edit APIM
  policies can use the managed identity to obtain and exfiltrate tokens. Restrict
  and audit policy changes.
- **Propagate claims challenges.** Conditional Access step-up cannot be satisfied
  at the gateway; return `WWW-Authenticate` to the client instead of swallowing it.
- **Preserve streaming.** Keep response buffering disabled so A2A SSE still flows.
- **Never log tokens** or write them to trace attributes.
- **Keep the no-gateway path working.** The adapter must remain directly runnable
  with its mock backend for local development.

#### Configuring native A2A OAuth connections against the hub

Both providers hold an OAuth client configuration per A2A connection. Pointing a
connection at the hub changes three things consistently: the **target URL**, the
**requested scope**, and the **registration that owns the callback**.

| Connection setting | Adapter-audience (today) | Hub-audience |
| --- | --- | --- |
| Target / A2A endpoint | Adapter or APIM adapter route | Hub API route |
| Scope | `api://<adapter-client-id>/access_as_user` `offline_access` | `api://<hub-client-id>/access_as_user` `offline_access` |
| OAuth client ID/secret | Backend registration | Native client registration, or the hub's own client |
| Callback registered on | Adapter registration | **Hub registration** |
| Authorization/token/refresh URLs | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/{authorize,token}` | Unchanged |

**Microsoft Foundry.** A project connection of category `RemoteA2A` with
`authType: OAuth2` carries `target`, `scopes`, `credentials.clientId`,
`credentials.clientSecret`, and the authorize/token/refresh URLs. To move it to the
hub, create a **new** connection whose `target` is the hub route and whose `scopes`
are the hub scope plus `offline_access`, then attach it as the agent's A2A tool.
Foundry's OAuth updates are not performed in place in this repository's tooling, so
use a new connection name rather than editing an existing one. Take the callback
URL that Foundry generates for the new connection and add it as a **Web** redirect
URI on the hub registration before authorizing. Foundry's `UserEntraToken` and
`ProjectManagedIdentity` modes are unaffected by this pattern, but a project managed
identity is app-only and cannot produce same-user delegation.

**Copilot Studio.** The A2A server definition holds the target URL and OAuth client
configuration; each user then establishes their own connection instance. Update the
server definition's target to the hub route and its requested scopes to the hub
scope plus `offline_access`, register the generated callback on the hub
registration, and republish. Because the scope and client configuration changed,
**existing user connections must be reauthorized** — a maker's working connection
does not authorize other users, and changed scopes invalidate the previous binding.
Expect connections to report `Not Connected`, `Expired`, or `Stale` until each user
reconnects.

For either provider, verify with a non-maker user. A successful maker test does not
establish same-user delegation. See
[the connection lifecycle guide](copilot-studio-a2a-connections.md) for states,
reauthentication triggers, and the consent model.

#### Migration order

1. Create the hub registration, its exposed scope, and its grant to the adapter API.
2. Add the federated identity credential binding APIM's managed identity to the hub.
3. Publish the hub API with the exchange policy and validate it with one frontend.
4. Repoint the frontends to the hub scope.
5. Create replacement native connections against the hub, register their callbacks
   on the hub registration, and have users reauthorize.
6. Restrict direct adapter ingress once no caller needs the adapter origin.
7. Remove obsolete redirect URIs and unused grants from the adapter registration.

**Recommendation:** adopt this pattern when the gateway is a real trust boundary,
when frontend tokens must not be adapter-usable, or when several backends need one
centrally operated delegation layer. Keep the simpler split otherwise: moving the
exchange alone does not remove native connection consent prompts.

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
| Any of the above with the [API Hub pattern](#option-the-api-hub-pattern-gateway-as-its-own-audience) | +1 for the hub |

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
- [Authenticate with managed identity policy](https://learn.microsoft.com/azure/api-management/authentication-managed-identity-policy)
- [Send request policy](https://learn.microsoft.com/azure/api-management/send-request-policy)
- [App registration and Zero Trust guidance](https://learn.microsoft.com/security/zero-trust/develop/app-registration)
- [Security best practices for application properties](https://learn.microsoft.com/entra/identity-platform/security-best-practices-for-app-registration)
- [Microsoft identity platform integration checklist](https://learn.microsoft.com/entra/identity-platform/identity-platform-integration-checklist)
- [Configure an application to trust a managed identity](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity)
- [Microsoft Entra service limits and restrictions](https://learn.microsoft.com/entra/identity/users/directory-service-limits-restrictions)
