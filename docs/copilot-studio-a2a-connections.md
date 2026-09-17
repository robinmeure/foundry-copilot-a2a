# Copilot Studio A2A connection lifecycle and consent

Last verified: 2026-09-16

This document describes the connection lifecycle for a native Copilot Studio
Agent2Agent (A2A) connected agent that uses OAuth 2.0 end-user authentication.
It applies to this project's Copilot Studio orchestrator -> APIM -> A2A adapter
path.

## Connection layers

The Copilot Studio configuration contains two related but distinct objects:

1. **A2A server definition** - the target URL, agent-card metadata, OAuth client
   configuration, authorization/token/refresh URLs, and requested scopes.
2. **User connection instance** - the OAuth token binding for one user and one
   A2A connection in one Power Platform environment.

The maker creates and publishes the server definition once. Each user establishes
their own connection instance when end-user authentication is enabled. A maker's
working connection does not authorize other users.

In this project, the connection requests:

```text
api://<backend-client-id>/access_as_user
offline_access
```

The delegated access token preserves the signed-in user through APIM and the
adapter. The adapter then uses OAuth on-behalf-of (OBO) to call Power Platform as
that user.

## Connection states

Copilot Studio documents the relevant states as follows:

| State | Meaning | Normal response |
| --- | --- | --- |
| `Connected` | The connection is active and usable. | None |
| `Not Connected` | No active connection is selected or available for the current identity. | Select or create a connection |
| `Expired` | The authentication credentials are no longer valid. | Reauthenticate |
| `Stale` | The recorded connection is no longer valid or usable, usually because it was dropped or timed out. | Review, repair, or replace the connection |

The authoring card and the published user-connections page can show different
views of the same connection type. For example, the published page can retain a
`Stale` user connection record while the authoring card reports that no usable
connection is currently selected.

`Stale` is a health result, not a separately documented Copilot Studio timer.
Copilot Studio can discover the condition when it next validates, refreshes, or
uses the connection, rather than at the exact moment the connection became
invalid.

## Token lifetime and reauthentication

There is no documented Copilot Studio A2A-specific time-to-live that forces a
healthy connection to be revalidated after a fixed interval.

For this project's Microsoft Entra OAuth configuration:

- Access tokens are short-lived and are refreshed without user interaction.
- `offline_access` allows the connection service to obtain a refresh token.
- Microsoft Entra refresh tokens default to 90 days in most scenarios.
- A successful refresh returns a new refresh token, allowing an actively used
  connection to remain valid beyond the original 90-day period.
- Conditional Access sign-in-frequency policies can require interactive
  authentication sooner.

The 90-day value is therefore not a requirement for every user to reconnect every
90 days. It is also not universal for every identity provider or authentication
method. A third-party OAuth provider, API key, certificate, or client secret can
have a different lifetime.

Reauthentication can be required when:

- The refresh token expires through extended inactivity.
- The user or an administrator revokes tokens or consent.
- Conditional Access, MFA, device-compliance, location, or Terms of Use
  requirements change.
- The user account is disabled, deleted, or otherwise loses access.
- The OAuth client ID, scopes, authorization URLs, callback, or target connection
  changes.
- The backend application credential expires or is rotated without updating the
  connection configuration.
- The A2A connection is deleted and recreated.
- The user moves to another Power Platform environment, where token bindings do
  not migrate.

An access-token expiration by itself should not prompt the user. The refresh-token
flow handles it unless token refresh fails or interactive authentication is
required.

## First use by a new user

For an end-user-authenticated A2A connected agent, first use follows this sequence:

1. The user signs in to the host or channel.
2. Copilot Studio determines that the user has no usable connection for the A2A
   target.
3. The user-connections experience shows `Not Connected`, either at conversation
   startup or when orchestration first attempts to use the target.
4. The user selects **Connect**.
5. Copilot Studio starts the configured OAuth authorization-code flow for
   `access_as_user` and `offline_access`.
6. Microsoft Entra authenticates the user and, when required, asks the user to
   consent to the delegated permissions.
7. The OAuth callback completes and the connection service associates the
   resulting token set with that user and A2A connection.
8. The user returns to the conversation and retries the request.
9. Copilot Studio calls the A2A endpoint with that user's access token. APIM and
   the adapter validate the delegated identity, and the adapter performs OBO for
   the downstream Power Platform call.

If the user declines or cannot complete authorization, the A2A target remains
unavailable to that user. Other agent capabilities that do not depend on the
connection can continue to work.

This is the default consent-card flow. When an administrator enables the
per-agent connector consent-card bypass described below, Copilot Studio
suppresses the conversational confirmation in steps 3 and 4. Interactive
Microsoft Entra authentication can still be required when no usable user token
exists or policy requires it.

## Consent model

This architecture has two independent delegated permission grants:

| Grant | Client | Resource | Consent options |
| --- | --- | --- | --- |
| A2A connection access | OAuth client used by the A2A connection | Backend `access_as_user` scope | Per-user consent or tenant-wide admin consent |
| Adapter OBO access | Backend API | Power Platform `CopilotStudio.Copilots.Invoke` | Per-user consent or tenant-wide admin consent |

Granting one does not grant the other. A user can successfully authenticate to the
backend and still receive `AADSTS65001` later if the backend-to-Power Platform
grant is missing.

### Per-user consent

A per-user delegated grant is sufficient for that user. It is appropriate for
development, a limited test, or a tenant where administrators intentionally allow
users to approve the requested permissions.

It is not sufficient for other users. Each additional user must either consent
for themselves or be covered by a tenant-wide grant.

### Tenant-wide admin consent

An authorized Microsoft Entra administrator can approve delegated permissions on
behalf of all users in the tenant. This normally removes the permission-acceptance
prompt for each user.

Tenant-wide consent does not:

- Create a single shared refresh token.
- Create every user's Copilot Studio connection instance.
- Remove the need to authenticate the user and issue a user-specific token.
- Give a user access to resources that the user is otherwise unauthorized to use.

Even with tenant-wide admin consent, each user still completes the connection flow
once so the platform can bind tokens to that user's identity. With an existing
Entra browser session, this can be a brief redirect with no permission prompt.

Tenant-wide consent is normally granted once per application, tenant, and
permission set. A scope change can require new consent.

## Connector consent-card bypass

Copilot Studio provides an administrator setting that bypasses the connector
consent cards normally shown when an agent first uses a connector on behalf of a
user. The setting is:

- Scoped to one Copilot Studio agent (`botid`) in one Power Platform environment.
- Applied to all connector consent cards presented by that agent.
- Effective for all users of that agent.
- Supported only for agents powered by the standard harness.

It is not a per-user or per-tool allowlist. If different user populations require
different confirmation behavior, use separate agents or leave the cards enabled.
The setting doesn't apply to GitHub Copilot-harness agents.

For this project's native Copilot Studio chain, enable the bypass on the
standard-harness **orchestrator**, because that agent owns and invokes the A2A
connections. Don't enable it on a specialist merely because that specialist is an
A2A target. A specialist needs its own setting only if it independently invokes
connectors for its users.

Microsoft's article describes the feature generically for connectors and doesn't
explicitly enumerate native A2A connected agents. Treat a successful setting
update as configuration evidence, not runtime proof: test the published
orchestrator with a new non-maker user and confirm both that no consent card
appears and that the specialist callback succeeds as that user.

### What the bypass does not remove

The documented switch is specifically a consent-card bypass. It doesn't state
that any of these security boundaries are disabled:

- Microsoft Entra sign-in and token issuance.
- Conditional Access, MFA, or device requirements.
- The user's authorization to the backend and downstream resources.
- OAuth token refresh, revocation, expiration, or stale-connection handling.
- The adapter's delegated JWT validation and OBO exchange.
- Foundry OAuth identity-passthrough consent, which has a separate lifecycle.

It also doesn't create one shared maker connection or convert calls to app-only
authentication. Keep the end-user OAuth and OBO design unchanged.

Connector consent-card bypass and tenant-wide Microsoft Entra permission consent
are independent controls. The former suppresses a Copilot Studio conversation
card for one agent; the latter grants an application's delegated scopes for the
tenant. Configure each only when its separate security review supports it.

### One-time administrator setup

Create a dedicated single-tenant Microsoft Entra public-client application for
this administrative operation. The project CLI creates or verifies the exact
least-privilege registration:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- `
  register-consent-bypass-app `
  --tenant-id <tenant-id>
```

Add `--admin-consent` only when an authorized tenant administrator intends to
grant the delegated permission tenant-wide. Otherwise the administrator who runs
the first get/set command completes any consent prompt required by tenant policy.

The resulting application:

1. Configure the native/mobile and desktop redirect URI `http://localhost`.
2. Add the delegated **Power Platform API** permission
   `CopilotStudio.AdminActions.Invoke`. The production Power Platform API
   application ID is `8578e004-a5c6-46e7-913e-12f58912df43`.
3. Complete the consent action required by tenant policy.
4. Assign each operator one supported Microsoft Entra role:
   **Power Platform Administrator** (least privilege of the documented roles),
   **AI Administrator**, or **Global Administrator**.

Keep this admin client separate from the adapter runtime registration. The
adapter doesn't need and shouldn't receive the administrative scope.

The environment ID is the Power Platform environment GUID. The bot ID is the
Dataverse Copilot table's primary key, `botid`. A modern Copilot Studio URL can
show a schema name such as `cr5c9_Orchestrator` instead of that GUID; in that
case, find the corresponding row in the Dataverse `bots` table and use its
`botid`.

### Read, enable, and disable

The project CLI opens the system browser for administrator authentication. The
access token is held only for the command process and isn't persisted by this
repository.

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- `
  get-connector-consent-bypass `
  --tenant-id <tenant-id> `
  --admin-client-id <consent-bypass-admin-client-id> `
  --environment-id <power-platform-environment-guid> `
  --bot-id <dataverse-bot-guid>
```

Enable the bypass:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- `
  set-connector-consent-bypass `
  --tenant-id <tenant-id> `
  --admin-client-id <consent-bypass-admin-client-id> `
  --environment-id <power-platform-environment-guid> `
  --bot-id <dataverse-bot-guid> `
  --enabled true
```

Restore consent cards:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- `
  set-connector-consent-bypass `
  --tenant-id <tenant-id> `
  --admin-client-id <consent-bypass-admin-client-id> `
  --environment-id <power-platform-environment-guid> `
  --bot-id <dataverse-bot-guid> `
  --enabled false
```

Run the read command after either update and test with a new non-maker user.
Neither command publishes the agent or changes its tool definitions.

The Power Platform API endpoint and the equivalent `pac copilot-studio
get-connector-consent-bypass` and `set-connector-consent-bypass` commands are
currently preview management surfaces.

## Expected production experience

In a stable production environment, connection setup should be a one-time
onboarding action:

> One initial OAuth connection per user, per A2A target connection, per Power
> Platform environment.

With connector consent-card bypass enabled, that user-specific authorization
can be transparent when Microsoft Entra can issue the token silently. It remains
user-specific and can still require interaction after revocation, expiration, or
a Conditional Access challenge.

Afterward:

- Access-token renewal is automatic.
- Normal conversations do not prompt the user repeatedly.
- Republishing the agent preserves the connection when the target connection
  reference and OAuth configuration remain unchanged.
- The user reconnects only after an exceptional expiration, revocation, policy
  change, or connection replacement.

For example, 500 users require 500 user token bindings for one A2A target. The
maker still configures only one shared server definition. If the agent has two
separate A2A target connections, each user can require two token bindings.

Development, test, and production environments have independent connections.
Publishing or importing configuration into another environment does not migrate
user OAuth tokens.

## Shared authentication alternative

Copilot Studio can use agent-author authentication for tools that are intended to
run under one shared identity. That removes per-user connection onboarding, but it
changes the security model:

- Every request runs as the shared identity.
- Per-user authorization and audit identity are lost.
- Resource access is determined by the shared account.

Do not use a maker connection or app-only token merely to bypass a per-user
connection problem in this project. The Citadel runtime expects a delegated
`access_as_user` token, and the adapter's OBO flow is intentionally based on the
signed-in user.

## Diagnosing production failures

The affected population is a useful first signal:

| Symptom | Most likely area |
| --- | --- |
| One user becomes stale | That user's token, consent, account state, permissions, or Conditional Access evaluation |
| All users become stale at approximately the same time | Shared OAuth client configuration, client secret, app registration, callback, scopes, connector definition, or policy |
| Calls fail but the connection stays connected | APIM, adapter, tunnel/backend routing, A2A protocol, or downstream service |
| A connection still targets an old URL | The provider connection was not retargeted; create or select the correct connection |

Using a stable APIM URL keeps a changing development tunnel behind the gateway
from changing the Copilot Studio connection target. If Copilot Studio points
directly to a temporary tunnel, a new tunnel URL can require a new or updated A2A
server definition and connection.

## References

- [Connect an agent available over A2A](https://learn.microsoft.com/microsoft-copilot-studio/add-agent-agent-to-agent)
- [Configure and manage Copilot Studio connections](https://learn.microsoft.com/microsoft-copilot-studio/authoring-connections)
- [Configure end-user authentication for tools](https://learn.microsoft.com/microsoft-copilot-studio/configure-enduser-authentication)
- [Bypass connector consent cards for an agent](https://learn.microsoft.com/microsoft-copilot-studio/admin-connector-consent-bypass)
- [Power Platform CLI `pac copilot-studio`](https://learn.microsoft.com/power-platform/developer/cli/reference/copilot-studio)
- [Dataverse Copilot (`bot`) table](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/bot)
- [Microsoft Entra refresh tokens](https://learn.microsoft.com/entra/identity-platform/refresh-tokens)
- [Microsoft Entra Conditional Access session lifetime](https://learn.microsoft.com/entra/identity/conditional-access/concept-session-lifetime)
- [Grant tenant-wide admin consent](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent)
- [Troubleshoot broken Power Platform connections](https://learn.microsoft.com/troubleshoot/power-platform/power-automate/connections/troubleshoot-broken-connections)
- [Local Citadel workflow](./citadel-local.md)
- [Application registration and delegated grants](./spa-app-registration.md)
