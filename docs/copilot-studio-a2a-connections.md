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

## On-behalf-of (OBO) custom connectors

Copilot Studio can configure a Microsoft Entra ID custom connector with
**Enable on-behalf-of login**. With that setting, the connection service exchanges
the user's existing Copilot Studio sign-in token instead of starting an interactive
authorization-code flow. This requires a dedicated **connector app registration**
that:

- exposes its own delegated scope (for example `access_as_user`),
- preauthorizes the Microsoft Azure API Connections service principal
  (`fe053c5f-3692-4f14-aef2-ee34fc081cae`) on that scope,
- holds a delegated permission to the target API, and
- owns the connector's Web redirect URI and client credential.

Mapped to this project, the target API is the existing adapter registration and
the adapter is unchanged:

```text
Channel sign-in token (aud = connector app)
  -> connector app OBO -> token (aud = api://<backend-client-id>, scp = access_as_user, same oid)
  -> APIM and adapter validation, unchanged
  -> adapter OBO -> Power Platform or Foundry
```

This is chained delegation. The connector app is the separate native OAuth
client recommended in
[the app registration scaling guidance](./authentication-and-agent-scaling.md#separate-native-oauth-clients-from-the-adapter-as-trust-boundaries-grow).
The adapter validates the audience and delegated scope, not the calling client
(`azp`), so it accepts the connector-issued token.

### Applicability to native A2A connected agents

The documented OBO option applies to **custom connectors** and MCP servers that
use the Microsoft Entra ID identity provider. The native A2A connected-agent setup
offers only **None**, **API key**, and generic **OAuth 2.0**; it doesn't expose
the Entra ID provider or the on-behalf-of switch.

A2A connections are, however, stored as Dataverse custom connectors in the
environment. Converting that underlying connector to Entra ID with on-behalf-of
login **works in practice**, as validated below, but it is not a documented or
supported configuration. Treat it as experimental: a later edit from the A2A
connected-agent UI can revert the connector to generic OAuth 2.0, and product
changes can break it.

Exposing a specialist as a plain custom connector or MCP tool instead makes it a
tool action rather than a connected agent. A2A agent-card discovery, task
semantics, and streaming behavior don't carry over. The feature is specific to
Copilot Studio; Foundry `RemoteA2A` connections have their own lifecycle.

### Validated experiment: OBO on a native A2A connection

This experiment converted the connector behind one native A2A connected agent to
Microsoft Entra ID with on-behalf-of login and ran it side by side with the
existing generic OAuth 2.0 connection.

**Result:** a new, non-maker user found the OBO connection already **Connected**
in the orchestrator's connection manager, with no sign-in popup and no manual
**Connect** step. The orchestrator invoked the specialist through APIM as that
user, and the specialist answered. The maker-configured generic OAuth 2.0
connection on the same route, by contrast, requires each user to complete an
interactive sign-in once.

#### Tested flow

```text
Browser SPA (signed-in user)
  -> APIM /hub                         validates the delegated user token
  -> adapter /a2a/copilot-studio       validates, then OBO -> Power Platform
  -> Copilot Studio orchestrator       Direct Connect, as the user
  -> A2A connected agent (OBO connector)
       connection service: user's channel token -> connector app OBO
       -> token aud = api://<backend-client-id>, scp = access_as_user, same oid,
          azp = <connector-app-client-id>
  -> APIM /<api-path>/a2a-agents/<agent-id>/a2a
                                       validate-jwt on audience, per-user rate limit
  -> adapter /a2a-agents/<agent-id>/a2a
                                       validates audience and scope, then OBO
  -> Copilot Studio specialist         as the same user
```

The APIM policy and the adapter were **not changed**. Both validate the audience
and delegated scope, not the calling client (`azp`), so they accept a token
issued to the connector app exactly like one issued through the generic OAuth 2.0
connection.

#### Prerequisites

- The adapter backend registration with the `access_as_user` scope, and the APIM
  per-agent route already working with the generic OAuth 2.0 connection.
- An account that can create app registrations and grant tenant-wide admin
  consent, and a maker account for the orchestrator's environment.
- Power Platform CLI (`pac`) authenticated to that environment
  (`pac auth create --environment <environment-url>`).
- The Microsoft Azure API Connections service principal
  (`fe053c5f-3692-4f14-aef2-ee34fc081cae`) present in the tenant. It exists in
  most tenants that already use Power Platform connectors.

#### Setup

1. **Create the connector app registration** with the repository CLI. The
   `register-hub` command produces exactly the shape the connector needs: a
   single-tenant application that exposes its own `access_as_user` scope,
   preauthorizes the listed clients on it, and holds delegated permission to the
   adapter's `access_as_user`. Preauthorize the Azure API Connections service
   principal and request admin consent:

   ```powershell
   dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-hub `
     --api-client-id <backend-client-id> `
     --display-name <prefix>-<agent-id>-obo-connector `
     --preauthorize-client-ids fe053c5f-3692-4f14-aef2-ee34fc081cae `
     --admin-consent
   ```

   Use one connector registration per trusted integration, not per user. Record
   the new application (client) ID as `<connector-app-client-id>`.

2. **Preauthorize the connector app on the backend scope**, so the OBO exchange
   for `api://<backend-client-id>/access_as_user` needs no additional prompt:

   ```powershell
   dotnet run --project .\src\FoundryCopilotA2A.Cli -- preauthorize-client `
     --api-client-id <backend-client-id> `
     --client-id <connector-app-client-id>
   ```

3. **Confirm tenant-wide admin consent** for the connector app to the backend
   scope. Check the **API permissions** blade of the connector app for a green
   **Granted** status on `access_as_user`, or grant it there. This consent is
   essential: without it, the silent exchange fails and each user is sent to the
   connection manager with "I couldn't connect. Open connection manager to verify
   your credentials."

4. **Create a client secret** on the connector app in the Azure portal under
   **Certificates & secrets**. Copy the secret **Value**, not the **Secret ID**.
   Don't store it in the repository, user secrets, or chat. It's entered only into
   the connector in step 5 and step 9.

5. **Add the A2A connected agent** in the orchestrator (**Agents** > **Add an
   agent**, then choose the Agent2Agent option), side by side with
   any existing connection to the same specialist:

   | Field | Value |
   | --- | --- |
   | Endpoint URL | `https://<apim-name>.azure-api.net/<api-path>/a2a-agents/<agent-id>/a2a` |
   | Authentication | OAuth 2.0 |
   | Client ID | `<connector-app-client-id>` |
   | Client secret | the secret **Value** from step 4 |
   | Authorization URL | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/authorize` |
   | Token URL template | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | Refresh URL | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | Scopes | `api://<backend-client-id>/access_as_user offline_access` |

   Give it a distinct name (for example `<Specialist> OBO`) so the connector is
   easy to identify. Copilot Studio creates a Dataverse custom connector for it
   with `identityProvider: oauth2` and `redirectMode: GlobalPerConnector`.

6. **Find and download the underlying connector:**

   ```powershell
   pac connector list
   pac connector download --connector-id <connector-id> --outputDirectory <scratch-folder>
   ```

   Keep an unmodified copy of `apiProperties.json` for rollback. Work in a
   scratch folder outside the repository; the files contain environment-specific
   identifiers.

7. **Convert the connector to Entra ID with on-behalf-of login.** In
   `apiProperties.json`, replace
   `properties.connectionParameters.token.oAuthSettings` with the Entra ID shape
   the Learn article's **Enable on-behalf-of login** switch produces, keeping the
   existing `redirectMode` and `redirectUrl`:

   ```json
   {
     "identityProvider": "aad",
     "clientId": "<connector-app-client-id>",
     "scopes": [
       "api://<backend-client-id>/access_as_user offline_access"
     ],
     "redirectMode": "GlobalPerConnector",
     "redirectUrl": "<existing-redirect-url>",
     "properties": {
       "IsFirstParty": "False",
       "AzureActiveDirectoryResourceId": "api://<backend-client-id>",
       "IsOnbehalfofLoginSupported": true
     },
     "customParameters": {
       "LoginUri": { "value": "https://login.microsoftonline.com" },
       "TenantId": { "value": "<tenant-id>" },
       "ResourceUri": { "value": "api://<backend-client-id>" },
       "EnableOnbehalfOfLogin": { "value": "true" }
     }
   }
   ```

   Leave `apiDefinition.json` unchanged. The host, base path, and single
   `POST /<api-path>/a2a-agents/<agent-id>/a2a` operation stay the same.

8. **Apply the change.** `pac connector update` requires both files:

   ```powershell
   pac connector update --connector-id <connector-id> `
     --api-definition-file <scratch-folder>\apiDefinition.json `
     --api-properties-file <scratch-folder>\apiProperties.json
   ```

9. **Re-enter the client secret in Power Apps.** `pac connector update` has no
   secret parameter and clears the stored secret, so creating a connection now
   fails with `AADSTS7000215: Invalid client secret provided`. In
   [Power Apps](https://make.powerapps.com), select the environment, open
   **Custom connectors** (**More** > **Discover all** if it isn't pinned), select
   **Edit** on the connector, and open **2. Security**. Confirm **Identity
   provider** is **Microsoft Entra ID**, **Enable on-behalf-of login** is `true`,
   and the client ID, resource URL, and scope match step 7. Paste the secret
   **Value** into **Client secret** and select **Update connector**.

   The connector appears under **Custom connectors**, not under **Connections**.
   If it isn't listed, open it from **Solutions** > **Default Solution** >
   **Custom connectors**. Don't edit it from the A2A connected-agent UI in Copilot
   Studio, which offers only generic OAuth 2.0 and can revert the conversion.

10. **Verify the conversion persisted** by downloading the connector again and
    checking `oAuthSettings`. Expect `identityProvider: aad`,
    `IsOnbehalfofLoginSupported: true`, and `EnableOnbehalfOfLogin: "true"`. The
    platform sets `IsFirstParty` to `"True"` on save. That was observed and didn't
    affect the result.

11. **Register the connector's redirect URL** on the connector app. Use the
    `redirectUrl` from the downloaded `apiProperties.json`, which has the form
    `https://global.consent.azure-apim.net/redirect/<connector-name>-<suffix>`:

    ```powershell
    dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-web-redirect `
      --client-id <connector-app-client-id> `
      --redirect-uri <redirect-url>
    ```

    Repeat this if the connector is recreated, because the generated URL changes.

12. **Finish the connected agent in Copilot Studio.** On the OBO connected agent,
    create the maker connection (**Not connected** > **Create new connection**),
    set **Credentials to use** to **End user credentials**, disable any older
    connected agent that points at the same specialist so the orchestrator can't
    route to it, and **Publish** the orchestrator.

13. **Share the agents.** Share the orchestrator, and each Copilot Studio
    specialist that the adapter invokes as the user, with the target users or a
    security group, with permission to use rather than edit the agent. Otherwise
    the channel answers "You don't have access to talk to this bot, contact the
    owner."

#### Verification

Test as a **non-maker user** who has never used the agent. The maker's own
connection is created during setup, so testing as the maker proves nothing about
silent connection creation.

1. Sign in to the frontend as the test user, start a new conversation, and ask
   a question that routes to the specialist. A greeting such as "hi" may not
   invoke it.
2. Expect an answer with no sign-in card and no connection-manager link.
3. In the orchestrator's connection manager for that user, the OBO connection
   shows **Connected** without the user having selected **Connect**.
4. In the adapter traces, one orchestrator run
   (`POST /a2a/copilot-studio`) contains a child
   `POST /a2a-agents/<agent-id>/a2a` span with `execute_tool <specialist>` and a
   successful `entra.obo.acquire_token`. The adapter doesn't log the caller's
   `azp`. To prove the token came from the connector app, inspect the incoming
   token's `azp` in APIM diagnostics or temporarily log it in a development build.

#### Troubleshooting

| Symptom | Cause | Resolution |
| --- | --- | --- |
| `AADSTS7000215: Invalid client secret provided` when creating the connection | `pac connector update` cleared the secret, or the **Secret ID** was entered instead of the **Value** | Re-enter the secret **Value** in Power Apps (setup step 9) |
| The connector isn't in the **Connections** list | Connections are created only after the token exchange succeeds; the connector is a separate object | Open **Custom connectors**, or **Solutions** > **Default Solution** > **Custom connectors** |
| `connectorRequestFailure ... returned an HTTP error with code 404` | APIM returns 404 before the adapter, for example when the per-agent API's backend points at a Dev Tunnel that hosts no port | Call the APIM route with a valid token; if the adapter sees no request, fix the APIM API service URL |
| "You don't have access to talk to this bot, contact the owner." | The user isn't shared on the orchestrator | Share the orchestrator and specialists (setup step 13) |
| "I couldn't connect. Open connection manager to verify your credentials." and the connection shows **Not Connected** | The connector app has no tenant-wide admin consent to the backend scope, so the silent exchange fails | Grant admin consent (setup step 3) and retry in a new conversation |
| `AADSTS65001` in adapter logs | The backend's own grant to Power Platform `CopilotStudio.Copilots.Invoke` is missing | Grant admin consent on the backend registration |
| A sign-in card appears for the specialist | The connected agent still uses the generic OAuth 2.0 connector, or the conversion was reverted | Disable the old connected agent; re-download the connector and check step 10 |

#### Operational caveats

- **Unsupported configuration.** The A2A connected-agent UI doesn't expose Entra
  ID or on-behalf-of login. A product update, or saving the connected agent from
  that UI, can revert the connector to generic OAuth 2.0. Recheck with
  `pac connector download` after changes.
- **Secret management.** Rotate the connector app secret in Entra ID and then in
  Power Apps (**Custom connectors** > **Security**), not through `pac`. Consider
  the secret another confidential credential with its own expiry monitoring.
- **Per environment.** The connector, its redirect URL, and user connections are
  environment-scoped. Repeat steps 5 to 12 in each Power Platform environment.
- **Client restriction.** APIM and the adapter currently accept any client that
  obtained an `access_as_user` token. To pin this route to the connector app, add
  an `azp` claim check to the APIM `validate-jwt` policy for that API.
- **Consent card.** The agent consent card is governed separately by the
  [connector consent-card bypass](#connector-consent-card-bypass).

#### Rollback

1. Re-enable the original connected agent, delete the OBO connected agent in
   Copilot Studio, and publish. Delete the leftover custom connector in Power
   Apps if it remains.
2. Remove the connector app from the backend registration's preauthorized
   applications (**Expose an API** on the backend registration).
3. Delete the connector app registration:

   ```powershell
   dotnet run --project .\src\FoundryCopilotA2A.Cli -- delete-app --client-id <connector-app-client-id>
   ```

### What OBO removes and what it keeps

OBO removes the interactive sign-in and OAuth redirect when the channel already
has a Microsoft Entra-authenticated user. It doesn't remove:

- **The per-user token binding.** OBO is inherently per user; the binding is
  created silently rather than eliminated. The sizing above still applies, but
  users no longer perform the connection step.
- **The agent consent card.** Copilot Studio still asks the user to allow the
  agent to act on their behalf unless the
  [connector consent-card bypass](#connector-consent-card-bypass) is enabled.
- **Microsoft Entra consent for each grant.** Connector app to backend
  `access_as_user`, and backend to Power Platform `CopilotStudio.Copilots.Invoke`,
  are separate grants. A missing backend grant still fails with `AADSTS65001`.
- **Conditional Access, MFA, or step-up challenges.**
- **The need for an authenticated channel.** Anonymous or unauthenticated
  channels have no user token to exchange.

The documented setup also places a client secret on the connector app
registration. Treat it as another confidential credential to protect and rotate.

**Recommendation:** for native A2A connected agents, the supported path to a
transparent experience remains tenant-wide admin consent on both grants plus the
consent-card bypass on the orchestrator. Converting the A2A connector to OBO
removes the per-user connection step, as validated above, but it is unsupported
and must be reapplied whenever the connection is recreated. Use it for
experiments or when that operational risk is accepted. Either way, verify with a
new non-maker user that no sign-in or consent card appears and that the
specialist callback succeeds as that user.

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
- [Configure OBO authentication for custom connectors](https://learn.microsoft.com/microsoft-copilot-studio/advanced-custom-connector-on-behalf-of)
- [Power Platform CLI `pac copilot-studio`](https://learn.microsoft.com/power-platform/developer/cli/reference/copilot-studio)
- [Dataverse Copilot (`bot`) table](https://learn.microsoft.com/power-apps/developer/data-platform/reference/entities/bot)
- [Microsoft Entra refresh tokens](https://learn.microsoft.com/entra/identity-platform/refresh-tokens)
- [Microsoft Entra Conditional Access session lifetime](https://learn.microsoft.com/entra/identity/conditional-access/concept-session-lifetime)
- [Grant tenant-wide admin consent](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent)
- [Troubleshoot broken Power Platform connections](https://learn.microsoft.com/troubleshoot/power-platform/power-automate/connections/troubleshoot-broken-connections)
- [Local Citadel workflow](./citadel-local.md)
- [Application registration and delegated grants](./spa-app-registration.md)
