# Knowledge Chat

A separately runnable React/Vite chat frontend for the
[Knowledge API](../FoundryCopilotA2A.KnowledgeApi/README.md) — the **local orchestrator**
built on Microsoft Agent Framework. It talks only to the Knowledge API; the
[Orchestrator Chat](../FoundryCopilotA2A.Chatbot/README.md) is the Copilot Studio
(adapter/APIM) counterpart. Both apps share their conversation engine, persistence,
authentication, and chat layout through
[`@foundry-copilot-a2a/chat-shared`](../FoundryCopilotA2A.ChatShared/README.md).

"Local" describes orchestration, not offline inference: live mode still calls an
Azure-hosted model. Anonymous mock mode remains Azure-free against the mock Knowledge API.
Replies stream over the [AG-UI](https://docs.ag-ui.com/) protocol through the official
`@ag-ui/client` `HttpAgent` ([details](../FoundryCopilotA2A.KnowledgeApi/README.md#streaming-chat)).
Stopping or failing a reply discards its partial text, because streamed text is final only
when the run finishes.

## Behavior
stops dictation, and is unavailable while sending or reading a file. Local mode supports
PDF, DOCX, TXT, Markdown and CSV indexing, plus delegated SharePoint imports.
Clearing or deleting browser history does not delete indexed documents or backend dialogue.
Local orchestration separately retains bounded Agent Framework dialogue, keyed by tenant,
user and conversation ID: up to 12 successful turns / 24,000 characters, with sliding
`Knowledge:RetentionDays` expiry (7 days by default). It retains raw user prompts and
source-free assistant clarifications/general answers. Assistant responses using private
context are replaced in retained dialogue with a static re-retrieve placeholder, so private
sources must be retrieved again. New conversation IDs start fresh. Backend dialogue expires
independently of browser history, as backend documents do.

The single sidebar gear opens **Backend settings**, including conversation details.
It remains available on mobile.
Mail, Calendar, Microsoft To Do, Planner, Teams and
People are selected by default, alongside **APIM agents** when available. APIM agents
requires the Knowledge API's authenticated `GET /api/capabilities` response to contain
`a2a: true`; missing, false, loading and failed capabilities disable that source and omit
`a2a` from requests. `a2a` is optional for compatibility with older backends.
If specialists were excluded, a loading or unavailable agent catalog temporarily omits
APIM from requests rather than allowing excluded agents.
**Kamervragen** follows the same fail-closed selection behavior using the independent
`kamervragen: true` capability. It searches a shared, read-only parliamentary reference
index, not personal documents or an APIM agent. The chat shows search progress and numbered
citations; clearing the checkbox removes the tool on the next turn. Existing backends
without this capability continue to work.
Select all / Clear all operate on available sources only. These preferences allow tools;
the orchestrator decides which to invoke. No selection still allows document retrieval
and general model answers. The chat contract remains
`{ conversationId, text, sources: string[] }`.

### Confirm-before-write Microsoft 365 actions

`POST /api/chat` may return up to five proposed changes in `actions`
(mail drafts and reply drafts, mail read state, calendar events, To Do tasks/status,
Planner progress and **Save to OneDrive** for generated files).
**Nothing is written when a proposal is shown.** Each proposal renders as a
plain-text card under its reply with a summary, details (long bodies scroll), expiry and
**Confirm** / **Cancel**. Confirm calls `POST /api/actions/{id}/confirm` and Cancel calls
`POST /api/actions/{id}/cancel`, both with `{ conversationId }` and the Knowledge API token.
The client refuses IDs that are not 32 lowercase hex characters, drops malformed proposals,
caps details at 12 and only links to `https` results ("Open in Microsoft 365", new tab).
Cards move from pending to confirming/cancelling to completed, cancelled or failed; buttons
stay disabled after any outcome. Expired proposals cannot be confirmed locally, and
`action_not_found` explains that the proposal expired or was already used. Card state is
saved with the browser transcript, so pending cards survive reloads; a confirmation that was
interrupted by a reload is shown as failed rather than retried. Status changes are announced
politely to screen readers. The layout fixture's last chat shows two mocked proposals.

The settings drawer uses a native modal dialog for keyboard containment, inert background,
Escape dismissal and focus restoration. Its entrance respects reduced-motion preferences.
Orchestration and mock-session explanations live in this drawer. The workspace has no title bar or duplicate settings control.
The attachment icon opens **Local file / SharePoint**. Local file uses the native file chooser
with the Knowledge API's file limits. SharePoint opens
a styled, keyboard-contained modal: search by site name or paste an HTTPS SharePoint site URL,
choose a library, browse folders,
select one file and choose **Add to conversation**. Importing indexes the document immediately;
it does not send the draft. The modal stays open on errors and prevents dismissal while indexing.
Cancel/Escape otherwise returns focus to the attachment icon. No Graph token is requested by
the picker itself; it reuses the authenticated Knowledge API.
Indexed local files show **Upload to OneDrive** on the right of their row under **Chat documents**,
not as reply cards. Clicking the button is the explicit authorization to upload the original;
the row shows progress, an **Open in OneDrive** link on success, or an inline error with recovery
guidance. It works even if the answer fails. Originals are available for 60 minutes and one upload
attempt; after expiry, removal, restart or failure, attach the file again to upload.
SharePoint imports have no upload button. Saving still requires delegated `Files.ReadWrite` consent.
Pasting a site URL resolves that site and automatically opens its library list.
Use a site root (including `/sites/...` or `/teams/...`), not a document, page or sharing link.
Resolving the tenant root opens its own libraries, not a list of every site in the tenant.
Keyword search still lets you choose among matching sites and load additional results.
Local files appear inside the composer as document chips showing the filename, file type,
size and **Ready to add** state. Sending replaces this with **Indexing** until ingestion succeeds.
Indexed local files and SharePoint imports stay visible in **Chat documents** above the composer
with **Available for answers** and a removal control. This means available for retrieval when
relevant, not proof the document was cited in a particular answer. The list refreshes immediately
after ingestion, even while the answer is still streaming or if answering subsequently fails.
The former inline SharePoint browser is no longer shown. Only pending file activity
adds a status line below the composer. On mobile, Conversations starts collapsed and opens a separately scrolling
list (Escape closes it; selecting a chat returns focus to the composer). Desktop history
starts expanded. The transcript reserves a useful minimum height; unusually short screens,
expanded document controls and large text can scroll the page rather than flatten messages.

### Layout regression fixture

`tests/fixtures/layout.html` renders the real Knowledge Chat with **mocked authentication and APIs**,
eight saved conversations, and long replies. It never calls live services. From
this frontend directory, build and serve it separately from the production entry:

```text
node --input-type=module -e "import {build,preview} from 'vite'; await build({configFile:false,build:{outDir:'dist/layout-fixture',target:'esnext',rolldownOptions:{input:'tests/fixtures/layout.html'}}}); const server=await preview({configFile:false,build:{outDir:'dist/layout-fixture'},preview:{host:'127.0.0.1',port:4179,strictPort:true}}); server.printUrls()"
```

Open `http://127.0.0.1:4179/tests/fixtures/layout.html`. Check 1440×900, 390×844
and 390×667: transcript scrolls, the composer is reachable,
and no horizontal page overflow occurs. Opening mobile history must not shrink the
transcript; selecting a conversation closes history and focuses Message. Test Enter/Space
on Conversations, Escape from its list, and Tab/Shift+Tab/Escape in Backend settings.
The dialog must keep focus inside and return it to its gear opener when dismissed.
Check the attachment menu with mouse, Enter, arrow keys and Escape. Test
SharePoint search, site/library selection, folder navigation, pagination, a failed import,
successful import and document removal. The fixture accepts site search `empty` for no results
and `error` for an API error; **Cannot import.txt** exercises an import failure.
Paste `https://contoso.sharepoint.com/sites/Finance` to check automatic site selection and
library loading, or a URL ending in `/denied` to check an explicit lookup failure.
Verify Cancel
preserves the draft, a successful import doesn't send it, and mobile dialogs don't overflow.
Stop the preview with Ctrl+C.

Aspire configures the API URL. For standalone use, set `VITE_KNOWLEDGE_BASE_URL`; live
Entra mode also needs `VITE_KNOWLEDGE_API_CLIENT_ID` for the separate API registration.
The SPA requests that API's delegated scope, never a Graph token or client secret.
See the backend guide for consent, resource configuration, ingestion limits and mock mode.

## Authentication

Live mode (`VITE_KNOWLEDGE_CHAT_AUTH_MODE=entra`) signs in with MSAL redirects, like the
Orchestrator Chat, and requests only `api://<knowledge-api-client-id>/access_as_user`.
It may reuse the Orchestrator Chat's SPA registration; register this app's redirect URI too:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa-redirect --tenant-id <tenant-id> --client-id <spa-client-id> --redirect-uri http://localhost:5175/auth-redirect.html
```

Copy `.env.example` to `.env.local` and configure:

```text
VITE_KNOWLEDGE_BASE_URL=http://localhost:5100
VITE_KNOWLEDGE_CHAT_AUTH_MODE=entra
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<spa-client-id>
VITE_KNOWLEDGE_API_CLIENT_ID=<knowledge-api-client-id>
VITE_SPEECH_INPUT_ENABLED=false
```

`anonymous` mode is accepted only in development, with loopback page and API origins.
Conversation history is stored in browser local storage, scoped by account and Knowledge
API URL. History saved by the former combined chatbot (on port 5174) is not migrated,
because browser storage is per origin.

## Run

The chat apps and their shared core form one npm workspace at the repository root:

```text
npm ci
dotnet run --project src/FoundryCopilotA2A.Cli -- run-knowledge --backend Mock
npm run dev -w src/FoundryCopilotA2A.KnowledgeChat
```

Open `http://localhost:5175`. The Aspire AppHost starts it as `knowledge-chat`, anonymous
when `KnowledgeBackend` is `Mock`.

```text
npm test -w src/FoundryCopilotA2A.KnowledgeChat
npm run lint -w src/FoundryCopilotA2A.KnowledgeChat
npm run build -w src/FoundryCopilotA2A.KnowledgeChat
```