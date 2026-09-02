# Configure the frontend and backend app registrations

This reproduction uses two single-tenant Microsoft Entra app registrations with distinct
responsibilities:

```text
React SPA
  -> token for api://<backend-client-id>/access_as_user
  -> A2A adapter API
  -> OBO token for CopilotStudio.Copilots.Invoke
  -> Copilot Studio
```

| Registration | Purpose | Secret | Permissions |
| --- | --- | --- | --- |
| Frontend SPA | Signs in the user and requests the backend API scope | None | Delegated `access_as_user` on the backend API |
| Backend API | Validates the SPA token and performs OBO | Required by the adapter | Exposes `access_as_user`; delegated `CopilotStudio.Copilots.Invoke` |

Never put the backend client secret in the React application or a `VITE_*` variable. Vite
variables are compiled into browser-visible JavaScript.

## How the split works at runtime

The two registrations are not interchangeable halves of one identity. Each one holds exactly the
credential type its host can protect, and the boundary between them is the point where a
browser-held token becomes a Copilot Studio-usable one.

### The sequence

1. **Sign-in.** MSAL Browser runs authorization code with PKCE against the *frontend* client ID
   and the tenant authority. No secret is involved; a public client cannot have one that stays
   secret in downloaded JavaScript.
2. **Token A.** The SPA requests `api://<backend-client-id>/access_as_user`. Entra issues a token
   whose `aud` is the *backend* registration and whose `oid` identifies the signed-in user. The
   `appid`/`azp` claim identifies the frontend registration as the client that asked.
3. **Call.** The browser sends token A to the adapter as `Authorization: Bearer`. The adapter
   validates issuer, audience, tenant, and lifetime against its `Authentication:*` settings, all
   of which describe the backend registration. The requested `access_as_user` scope is also
   advertised in the agent card and appears in the delegated token's `scp` claim.
4. **Token B.** The adapter performs OBO: it presents token A as a user assertion, authenticates
   as the backend confidential client with `COPILOT_STUDIO_CLIENT_SECRET`, and requests
   `https://api.powerplatform.com/.default`. Entra returns a Power Platform token carrying the
   *same* `oid`.
5. **Invoke.** `Microsoft.Agents.CopilotStudio.Client` calls Copilot Studio with token B. The
   agent runs as the signed-in user, not as a service account.

```text
frontend client id ──asks for──> backend audience ──OBO──> Power Platform audience
      (public)                     (confidential)              (same user throughout)
```

### The invariant

**The application that the incoming token was issued *for* must be the application that performs
the OBO exchange.** Entra enforces this: an assertion whose `aud` belongs to another application
is rejected with `AADSTS500131`, *"Assertion audience does not match the Client app presenting
the assertion"*.

In adapter configuration that means these two must describe the same registration:

| Setting | Value | Registration |
| --- | --- | --- |
| `Authentication:Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | Backend |
| `Authentication:Audience` | `api://<backend-client-id>` | Backend |
| `CopilotStudio:TenantId` | `<tenant-id>` | Backend |
| `CopilotStudio:ClientId` | `<backend-client-id>` | Backend |
| `CopilotStudio:ClientSecret` | secret value from the environment | Backend |

`run-adapter` uses `--client-id <backend-client-id>` for both
`Authentication:Audience` and `CopilotStudio:ClientId`, `--tenant-id` for the authority and
Copilot Studio tenant, and the environment variable for the secret. The command never needs the
SPA ID. Splitting OBO into a third registration is not possible.

The frontend registration is deliberately absent from that table. The adapter's JWT middleware
validates the resource-side issuer, audience, and lifetime, not the client application's
`appid`/`azp`, so additional front ends (a second SPA, a desktop client, a test harness) only
need their own grant of `access_as_user`. No adapter setting and no extra secret changes.

### What each side must not have

| Anti-pattern | Why it fails |
| --- | --- |
| Client secret on the SPA registration | The secret ships in the bundle; a public client cannot protect it, and OBO would still have to run server-side |
| `CopilotStudio.Copilots.Invoke` granted to the SPA | The browser could request a Power Platform token usable within the user's permissions, bypassing the adapter's validation, replay protection, and tracing |
| SPA redirect URI on the backend registration | Lets a browser obtain tokens directly against the confidential app; remove it once the dedicated SPA works |
| Separate "OBO application" | Rejected by Entra with `AADSTS500131` — see the invariant above |
| `VITE_ADAPTER_API_CLIENT_ID` set to the SPA ID | Requests a scope on an application that exposes none; token acquisition fails, commonly with a resource-not-found or invalid-scope error |

### The two grants

There are two independent delegated grants, and granting one never grants the other:

| Grant | Client | Resource | Consented by |
| --- | --- | --- | --- |
| 1 | Frontend SPA | Backend `access_as_user` | The user or an administrator, before or during the first API-token request |
| 2 | Backend API | Power Platform `CopilotStudio.Copilots.Invoke` | The user via the `consent` command, or an administrator tenant-wide |

If grant 1 cannot be obtained, basic sign-in may still succeed, but acquisition of the backend
API token fails visibly in the browser. Grant 2 missing fails farther downstream: sign-in
succeeds, the adapter accepts the request, and only the OBO call fails, with `AADSTS65001`.
Always check the **API permissions > Status** column on both registrations rather than one.

Entra can also gather both grants in a single consent experience when the SPA is listed in the
backend registration's `knownClientApplications` and the client uses the documented `.default`
consent pattern. This repository does not rely on that, because the backend's Power Platform
grant is normally established once, out of band, by the `consent` command or an administrator.

## 1. Configure the backend API registration

The existing adapter registration can be retained as the backend registration.

On its **Overview** page:

1. Record the **Directory (tenant) ID**.
2. Record the backend **Application (client) ID**.
3. Confirm **Supported account types** is **Accounts in this organizational directory only**.

Under **Expose an API**:

1. Set **Application ID URI** to `api://<backend-client-id>`.
2. Add an enabled delegated scope named `access_as_user`.
3. Allow admins and users to consent for development, unless tenant policy requires
   administrator consent.

The resulting scope is:

```text
api://<backend-client-id>/access_as_user
```

In the manifest, set:

```json
{
  "api": {
    "requestedAccessTokenVersion": 2
  }
}
```

Under **API permissions**, add the Power Platform delegated permission
`CopilotStudio.Copilots.Invoke`. Grant administrator consent for the backend registration when
possible. Consent is granted to the application but the resulting OBO token still represents
the signed-in user.

For a per-user development grant, enable **Authentication > Allow public client flows** and
run:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- consent --tenant-id <tenant-id> --client-id <backend-client-id>
```

Under **Certificates & secrets**, create the backend client secret used for OBO. Store its
**Value**, not its ID, in a process-scoped environment variable:

```text
COPILOT_STUDIO_CLIENT_SECRET=<secret-value>
```

The backend registration does not need an SPA redirect URI. If it retains one from an earlier
shared-registration setup, remove it after the dedicated frontend registration works.

## 2. Create the frontend SPA registration

The project CLI can create a dedicated frontend registration against the existing backend:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa --api-client-id <backend-client-id>
```

The command:

- creates a single-tenant SPA registration;
- configures `http://localhost:5173` as its SPA redirect;
- adds delegated permission to the backend's existing `access_as_user` scope;
- creates its Enterprise application;
- creates no client secret.

Use `--redirect-uri <exact-origin>` for a different origin and `--admin-consent` only when the
signed-in operator is permitted to grant tenant-wide consent.

To configure it manually:

1. Create a new single-tenant app registration.
2. Open **Authentication > Add a platform > Single-page application**.
3. Add `http://localhost:5173`.
4. Open **API permissions > Add a permission > My APIs**.
5. Select the backend API registration.
6. Select **Delegated permissions > access_as_user**.
7. Grant consent according to tenant policy.

Do not create a client secret for the SPA. Do not enable implicit grant. MSAL Browser uses
authorization code flow with PKCE.

The redirect URI must exactly match `window.location.origin`, including scheme, hostname, and
port. This project pins Vite to port 5173 and fails startup if that port is occupied.

## 3. Configure the frontend

Copy `src/FoundryCopilotA2A.Web/.env.example` to `.env.local` and set:

```text
VITE_ENTRA_TENANT_ID=<directory-tenant-id>
VITE_ENTRA_CLIENT_ID=<frontend-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<backend-api-client-id>
VITE_ADAPTER_BASE_URL=http://localhost:5099
```

`VITE_ENTRA_CLIENT_ID` identifies the public SPA client. `VITE_ADAPTER_API_CLIENT_ID`
identifies the protected resource and forms the requested scope:

```text
api://<VITE_ADAPTER_API_CLIENT_ID>/access_as_user
```

Restart Vite after changing `.env.local`:

```text
npm run dev --prefix src/FoundryCopilotA2A.Web
```

## 4. Start the backend

Use the backend registration's tenant ID, client ID, and secret:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- run-adapter --tenant-id <tenant-id> --client-id <backend-client-id> --direct-connect-url "<copilot-studio-url>"
```

The CLI allows `http://localhost:5173` through CORS by default. For another frontend origin,
pass `--allowed-origin <exact-origin>`.

## 5. Validate the token chain

After signing in, inspect the access token sent to the adapter. Every claim below is what tells
the two registrations apart:

| Claim | Expected value | What it proves |
| --- | --- | --- |
| `aud` | `api://<backend-client-id>` or the bare backend client ID | The token is for the backend registration, so OBO can redeem it |
| `appid` / `azp` | The **frontend** SPA client ID for a web-console call | The public client obtained the token; the split is working |
| `tid` | The configured tenant | Cross-tenant callers are rejected |
| `oid` | The signed-in user's object ID | Identifies the user represented by this delegated token |
| `scp` | Contains `access_as_user` | A delegated token — app-only tokens do not have `scp` |

For a web-console call, matching `aud` and `appid`/`azp` GUIDs indicate that the browser is still
using one shared registration instead of the intended split.

The browser must never receive a Power Platform token or the backend secret. The adapter
validates the browser token and performs OBO for
`https://api.powerplatform.com/.default`. The resulting token keeps the same `oid` and never
leaves the adapter process.

## Troubleshooting

### `AADSTS500011`

If Entra reports that `api://<backend-client-id>` was not found:

1. Confirm `VITE_ADAPTER_API_CLIENT_ID` is the backend Application (client) ID, not the SPA ID.
2. Confirm the backend registration exists in `VITE_ENTRA_TENANT_ID`.
3. Confirm its Application ID URI is exactly `api://<backend-client-id>`.
4. Confirm its Enterprise application exists in the same tenant.
5. Confirm `access_as_user` is enabled.

This error occurs during token acquisition, before the browser calls the adapter.

### Redirect URI mismatch

Use `http://localhost:5173` consistently. `http://127.0.0.1:5173`,
`http://localhost:5137`, and `https://localhost:5173` are different redirect URIs.

### Consent or OBO failure

There are two separate delegated grants:

1. Frontend SPA -> backend `access_as_user`
2. Backend API -> Power Platform `CopilotStudio.Copilots.Invoke`

Granting one does not grant the other. Check the **Status** column under **API permissions**
for both registrations.

### `AADSTS500131`

*"Assertion audience does not match the Client app presenting the assertion."*

The adapter accepted a token issued for one application and tried to redeem it as another. The
audience it validates and the client it performs OBO with must be the same registration:

1. Confirm `Authentication:Audience` is `api://<backend-client-id>`.
2. Confirm `CopilotStudio:ClientId` is that same `<backend-client-id>`.
3. Confirm `VITE_ADAPTER_API_CLIENT_ID` is the backend ID, so the browser requests that audience.

`run-adapter --client-id <backend-client-id>` sets both adapter values from one argument, so this
usually means the adapter was started from hand-written configuration, or with the SPA client ID.

### `AADSTS65001` at the OBO call

*"The user or administrator has not consented to use the application with ID ..."*

Sign-in worked and the adapter's token validation worked, so this is grant 2, not grant 1: the
backend registration has no delegated grant for `CopilotStudio.Copilots.Invoke` for this user.
Grant it per user, which is enough for the on-behalf-of exchange:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- consent --tenant-id <tenant-id> --client-id <backend-client-id>
```

Pass the **backend** client ID. Consenting the SPA registration again does not help.

### The browser signs in but every adapter call returns 401

Token acquisition and token validation are different registrations' concerns. A successful
sign-in only proves the frontend registration works. Decode the bearer token and compare its
`aud` with `Authentication:Audience` and its `tid` with the tenant in
`Authentication:Authority`; a mismatch there is the usual cause.

## Microsoft references

- [Register a single-page application](https://learn.microsoft.com/entra/identity-platform/scenario-spa-app-registration)
- [Configure an application to expose a web API](https://learn.microsoft.com/entra/identity-platform/quickstart-configure-app-expose-web-apis)
- [Single-page application: call a web API](https://learn.microsoft.com/entra/identity-platform/scenario-spa-call-api)
- [OAuth 2.0 on-behalf-of flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
- [AADSTS500011: resource principal not found](https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/error-code-aadsts500011-resource-principal-not-found)
- [Microsoft Entra authentication and authorization error codes](https://learn.microsoft.com/entra/identity-platform/reference-error-codes)
