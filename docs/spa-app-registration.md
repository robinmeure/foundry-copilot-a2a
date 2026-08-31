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

After signing in, inspect the access token sent to the adapter:

- `aud` is the backend Application ID URI or backend client ID.
- `tid` is the configured tenant.
- `oid` identifies the signed-in user.
- `scp` contains `access_as_user`.

The browser must never receive a Power Platform token or the backend secret. The adapter
validates the browser token and performs OBO for
`https://api.powerplatform.com/.default`.

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

## Microsoft references

- [Register a single-page application](https://learn.microsoft.com/entra/identity-platform/scenario-spa-app-registration)
- [Configure an application to expose a web API](https://learn.microsoft.com/entra/identity-platform/quickstart-configure-app-expose-web-apis)
- [Single-page application: call a web API](https://learn.microsoft.com/entra/identity-platform/scenario-spa-call-api)
- [OAuth 2.0 on-behalf-of flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
- [AADSTS500011: resource principal not found](https://learn.microsoft.com/troubleshoot/entra/entra-id/app-integration/error-code-aadsts500011-resource-principal-not-found)
