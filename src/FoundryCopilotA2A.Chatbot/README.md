# Orchestrator Chat

A separately runnable React/Vite chatbot. By default it sends every turn to one configured Orchestrator
through the adapter's A2A `SendStreamingMessage` handler. It has no flow designer, agent picker,
specialist selector, catalog discovery, or network/trace panels.

The console and chatbot reuse the transport, endpoint validation, and consent helpers in
[`FoundryCopilotA2A.BrowserShared`](../FoundryCopilotA2A.BrowserShared). The existing console's
behavior and default trace collection are preserved.

## Online and local orchestration

The compact **Online / Local** selector in the sidebar chooses between two separate paths:

- **Online orchestrator — Copilot Studio** (default): messages and experimental attachments
  use the existing A2A adapter and Copilot Studio agent. Local source settings do not apply.
- **Local orchestrator — Knowledge API / Microsoft Agent Framework**: the separate
  [Knowledge API](../FoundryCopilotA2A.KnowledgeApi/README.md) orchestrates document retrieval
  and selected tools. “Local” describes orchestration, not offline inference: live mode
  still calls an Azure-hosted model. Anonymous mock mode remains Azure-free when both
  backends are configured as Mock. Local replies stream over the
  [AG-UI](https://docs.ag-ui.com/) protocol through the official `@ag-ui/client` `HttpAgent`
  ([details](../FoundryCopilotA2A.KnowledgeApi/README.md#streaming-chat)).

The modes retain separate browser histories. Switching clears the draft and pending file,
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
It remains available on mobile and when the Knowledge API is not configured.
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

In local mode, `POST /api/chat` may return up to three proposed changes in `actions`
(mail drafts and reply drafts, mail read state, calendar events, To Do tasks/status and
Planner progress). **Nothing is written when a proposal is shown.** Each proposal renders as a
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
politely to screen readers. The layout fixture's last local chat shows two mocked proposals.

The settings drawer uses a native modal dialog for keyboard containment, inert background,
Escape dismissal and focus restoration. Its entrance respects reduced-motion preferences.
Orchestration and mock-session explanations live in this drawer; the online/local choice
stays in navigation. The workspace has no title bar or duplicate settings control.
The attachment icon opens **Local file / SharePoint**. Local file uses the native file chooser
with the current mode's file limits. SharePoint is available only in Local mode and opens
a styled, keyboard-contained modal: search by site name or paste an HTTPS SharePoint site URL,
choose a library, browse folders,
select one file and choose **Add to conversation**. Importing indexes the document immediately;
it does not send the draft. The modal stays open on errors and prevents dismissal while indexing.
Cancel/Escape otherwise returns focus to the attachment icon. No Graph token is requested by
the picker itself; it reuses the authenticated Knowledge API.
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

`tests/fixtures/layout.html` renders the real chatbot with **mocked authentication and APIs**,
eight saved conversations per mode, and long replies. It never calls live services. From
this frontend directory, build and serve it separately from the production entry:

```text
node --input-type=module -e "import {build,preview} from 'vite'; await build({configFile:false,build:{outDir:'dist/layout-fixture',target:'esnext',rolldownOptions:{input:'tests/fixtures/layout.html'}}}); const server=await preview({configFile:false,build:{outDir:'dist/layout-fixture'},preview:{host:'127.0.0.1',port:4179,strictPort:true}}); server.printUrls()"
```

Open `http://127.0.0.1:4179/tests/fixtures/layout.html`. Check 1440×900, 390×844
and 390×667: transcript scrolls, both modes remain visible, the composer is reachable,
and no horizontal page overflow occurs. Opening mobile history must not shrink the
transcript; selecting a conversation closes history and focuses Message. Test Enter/Space
on Conversations, Escape from its list, and Tab/Shift+Tab/Escape in Backend settings.
The dialog must keep focus inside and return it to its gear opener when dismissed.
Check the attachment menu with mouse, Enter, arrow keys and Escape. In Local mode, test
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

## Authentication and OBO

Use a **dedicated SPA app registration** for this frontend, with a Single-page application
redirect URI of `http://localhost:5174/auth-redirect.html`. Do not use the backend confidential client as the SPA.
Request the existing backend's delegated `api://<backend-api-client-id>/access_as_user`
permission. No client secret belongs in this app.

For an existing SPA registration, add and verify its redirect from the repository root:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa-redirect --tenant-id <tenant-id> --client-id <chatbot-spa-client-id> --redirect-uri http://localhost:5174/auth-redirect.html
```

```text
Current user -> chatbot's dedicated SPA (MSAL, authorization code + PKCE)
            -> adapter A2A handler (Bearer token for backend API)
            -> server-side OBO -> Orchestrator
            -> native A2A delegation to specialists
```

The chatbot shares the console's MSAL configuration and delegated-scope helpers. It uses
`loginRedirect`, `acquireTokenSilent` with `acquireTokenRedirect` when interaction is required,
and `logoutRedirect`, not popups. The dedicated `auth-redirect.html` page implements the
MSAL v5 redirect bridge without loading React or starting another authentication flow.
The chatbot uses the current signed-in account. It never uses the developer's Azure CLI identity
or a service token to impersonate a user. Tokens are cached by MSAL in session storage;
conversation history is stored separately in browser local storage, scoped by tenant/account,
adapter endpoint and agent. Changing accounts or signing out hides the previous account's
history rather than deleting it. Reload and sign-in redirects preserve saved chats.
Before each protected request, the frontend asks MSAL for an access token for the current
account and that API's scope. A token with two minutes or less remaining (or no usable expiry)
triggers one forced silent renewal before any API request is sent. MSAL manages refresh tokens
internally; the application does not read or store them separately. Adapter and Knowledge API
tokens remain separate, and Knowledge API claims challenges are preserved. Renewal is on demand,
including the first request after an idle or suspended tab, rather than dependent on background timers.
If silent renewal fails, the error is surfaced and no stale bearer is sent.
After an interactive token renewal, resend the request explicitly.
Sending a request does not automatically retry it after an error or cancellation.

Copy `.env.example` to `.env.local` and configure:

```text
VITE_ADAPTER_BASE_URL=http://localhost:5099
VITE_GATEWAY_BASE_URL=
VITE_ORCHESTRATOR_AGENT_ID=orchestrator
VITE_ORCHESTRATOR_NAME=Orchestrator
VITE_CHATBOT_AUTH_MODE=entra
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<dedicated-chatbot-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<existing-backend-api-client-id>
VITE_SPEECH_INPUT_ENABLED=false
```

`VITE_SPEECH_INPUT_ENABLED=true` shows a microphone and dictation-language selector in the
composer (Aspire: `ChatbotSpeechInputEnabled`). It uses the browser's own speech recognition,
which in Edge and Chrome sends audio to the browser vendor's online service. Dictated text only
fills the message box; nothing is sent automatically. Recognition quality varies, so it is off
by default. Browsers without speech recognition never show the controls.

The agent ID must match a configured adapter agent. It is not inferred from a display name
or selected from the agent catalog. Native delegation remains the orchestrator's decision;
the chatbot does not send `X-A2A-Chain-Target` or directly invoke specialists.

With `VITE_GATEWAY_BASE_URL` set, all messages use that HTTPS API base URL. The chatbot never
falls back to a direct route on failure. Allow this frontend's exact origin in both adapter
and APIM CORS configuration. Gateway routing and the configured provider still require their
own native connections, delegated permissions, and consent.

When launched by this repository's Aspire AppHost, the chatbot inherits `GatewayBaseUrl`.
Set `ChatbotGatewayBaseUrl` only to override the chatbot independently; an explicit empty value
keeps the local direct-adapter route. The chatbot is the browser entry point and is never an
agent-to-agent target.

## Run

From this folder:

```text
npm ci
npm run dev
```

Open `http://localhost:5174` and sign in. The existing diagnostic console remains at
`http://localhost:5173`. The Aspire AppHost starts both frontends and the adapter API; see
the [repository guide](../../README.md) for AppHost configuration. A live run requires the
backend API's confidential credentials and Orchestrator connection configuration on the
server, not in browser environment variables.

For an explicit Azure-free development test, set `VITE_CHATBOT_AUTH_MODE=anonymous`,
`VITE_ORCHESTRATOR_AGENT_ID=mock`, and the direct loopback adapter URL. Start the mock API
from the repository root using the existing CLI:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- run-mock --allowed-origin http://localhost:5174
```

Anonymous mode is refused in production builds, on non-loopback browser/adapter endpoints,
and with a gateway configured. It is not an authentication fallback.

## Behavior and validation

### Experimental file input and browser conversations

The paperclip attaches one nonempty file, up to 5 MB: TXT, CSV, PDF, DOCX, PNG or JPEG.
The composer shows its filename and size, with a Remove action. Sending a file without text
uses the visible prompt "Please summarize the attached file." Messages retain the attachment's
metadata. File bytes are sent only on that turn, not replayed in text history or follow-ups.
Reselect the original file if you need to resend it after an error.

This is **experimental transport**, not a promise that the agent can analyze the file.
The prior authenticated SDK probe did not recover file content. Browser input uses A2A 1.0
inline `raw` parts; the adapter also accepts A2A 0.3 inline file parts, validates size/type/name,
includes file identity/content in replay conflict detection, and sends an Activity attachment
through SDK `ExecuteAsync`, including the existing OAuth-challenge retry. The adapter also
adds one short line after that turn's text so the agent ties a terse question to the file:
`(This is about the attached PDF document "name".)`. The user's words stay first and the hint
stays short, because Copilot Studio uses the message text to search the uploaded file; a long
instruction preamble stopped it from reading the file. History replay stays in front of the
message. File URLs and multiple
files are refused; the adapter does not fetch remote URLs or extract document contents locally.
Foundry/APIM agents reject this experimental input instead of silently discarding it. Native
delegation may still drop files. The versioned APIM JSON-validation policy remains unchanged at
**1 MB for the entire encoded JSON request**. Base64 overhead and message/history content mean
the usable file size through that gateway is below 768 KiB. The adapter's 5 MB file limit is
not a promise that the existing gateway accepts that size. Use a smaller nonsensitive test file
through APIM, or the authenticated direct-adapter route when appropriate; this feature does not
change or bypass gateway policy.

**Conversations** lists up to 30 chats created in this browser for the signed-in account,
endpoint and agent. Select a conversation to reopen its transcript and original A2A context;
**Conversation details** displays that context ID, not a Copilot Studio server conversation ID.
New chat saves the prior transcript and rotates context. Delete removes a local chat; **Help &
guidance > Clear saved conversations** removes this partition's saved chats. Neither action
deletes provider-side data. The oldest local chats are removed when the 30-chat limit is reached.

Messages, sources, responder attribution, activity updates and file metadata are saved.
Each chat also keeps the opaque Copilot Studio conversation handles returned by the adapter
(at most 8). They are sent back on the next turn, so a reopened chat or a restarted adapter
resumes the same Copilot Studio conversation instead of replaying history. They are dropped after
a failed turn.
Raw attachment bytes and access tokens are not saved by conversation history. Interrupted
messages reopen as failed, never as indefinitely streaming replies. Corrupt storage, quota
failures and unavailable storage are surfaced without overwriting unreadable history.
There is a 4 MB serialized text budget; delete older chats if saving fails.

Browser storage is not encrypted or a server authorization boundary; avoid sensitive chats on
shared devices. This list is not an API-backed enumeration of all user-owned Copilot Studio
conversations. The adapter's per-user provider-conversation mapping expires (30 minutes by
default) and is lost on restart. Reopening a browser transcript does not restore provider-side
file context or reupload files. A locally deleted chat can remain with the provider.

The chatbot uses a compact navigation sidebar, a centered reading column, and a yellow
send control. On narrow screens the sidebar becomes a compact top bar. New chat, account
sign-in/sign-out, and Help & guidance remain available without adding console-only tools.
Help & guidance explains the keyboard shortcuts and browser history lifetime. Completed replies
offer Copy response with visible success or clipboard-error feedback.

The rounded message composer grows with typed or pasted text, up to 240px or 30% of the
viewport height, then scrolls internally. It shrinks again when text is removed, sent, or
cleared, and adjusts when the viewport width changes. Short viewports use a smaller cap to
keep the send control visible. Its toolbar keeps the send/stop button
inside the input surface. Enter sends, Shift + Enter inserts a new line, and IME composition
does not submit. While a reply streams, Stop remains available and you can draft the next
message without sending it automatically. Stopping returns focus to the input.

Assistant replies render Markdown as they stream, including headings, emphasis, lists,
quotes, links, inline/fenced code, and GitHub-flavored tables, task lists, and strikethrough.
Wide tables and code blocks scroll independently on narrow screens. Raw HTML is ignored,
unsafe link protocols are blocked, and images are offered as links instead of loading
remote content automatically. Links open in a new tab without replacing the conversation.
User messages and progress remain plain text; Copy response and conversation history retain
the original Markdown source.

Copilot Studio `DynamicPlan*` events appear as a child timeline under the first progress update,
inside the existing update disclosure, rather than as a separate answer section. The summary
shows the activity count; expand an activity row to inspect bounded/redacted arguments and
observations. When no text progress was received, an "Agent activity" parent keeps the events
accessible. The disclosure is available during and after the answer,
including failed turns and post-answer final plans. Thought text is not forwarded. Plan details
are memory-only: they are excluded from copied answers, saved browser history and follow-up
prompts. See the [shared plan contract](../FoundryCopilotA2A.BrowserShared/README.md#structured-plan-activity-v1).
Send `show plan example` in mock mode to try it. Existing APIM gateways need the updated shared
CORS policy allowing the extension negotiation headers.

When a connector returns an authentication failure, the chatbot displays an allowlisted
**Open connection settings** action for the affected tool. The repair response remains visible
but is excluded from subsequent conversation history.

Successful replies with structured A2A citations show a **Sources** button next to Copy
response, with the number of distinct sources. The button expands or collapses an accessible
list below the answer; each reply's list starts collapsed and toggles independently.
Structured citations take precedence over the response-link fallback described below.
It displays external source titles, literal markers/locators/quotes, and the original source
agent separately from the responding orchestrator. Source values are escaped text; links
open with `noopener noreferrer`, and documents/images are never fetched automatically.
Sources without URLs or markers remain visible. No citations are inferred from Markdown.
Excerpts are provider-reported, not independently verified quotations; native Copilot Studio
citation abstracts can supply the wire `quote` field.

The shared client consumes the optional `urn:foundry-copilot-a2a:citations:v1` A2A data part
(`schemaVersion: "1"`), merging streaming deltas and late data-only final events without
erasing answer text. Text replacements reset citations. Unsupported versions, malformed
payloads, conflicting source IDs, unresolved references, or unsafe URLs explicitly fail the
turn. Limits are 100 sources / 200 references, 256 characters per ID, and 8192 per other field;
optional fields must be omitted, not null. See the [full wire contract](../FoundryCopilotA2A.BrowserShared/README.md).
Credential-bearing source query parameters (including signed URLs) are rejected, not stripped
or exposed as links; unrelated query parameters/fragments remain intact.

Sources stay with successful responses, including saved browser history; failed/stopped turns clear partial citation
state. Follow-up history remains text-only and never asserts browser-supplied provenance.
Managed orchestrators can drop structured metadata while summarizing tool results. If the
final response omits the extension, this UI cannot recover verified citations from prose;
upstream metadata preservation is required. No agent prompts or cloud settings are changed.

For successful replies without structured sources, the chatbot collects safe HTTP(S) Markdown
links into a numbered **Sources** list in first-appearance order, deduplicated by URL.
The panel explicitly labels these as **links from the response**, not structured or independently
verified citations, and does not invent originating-agent provenance. Raw URL labels become
compact `[1]` links; meaningful link titles are retained with a number. Opaque native citation
tokens in linked paragraphs are hidden in the display; unlinked tokens show "citation unavailable".
Code, images, unsafe/credential-bearing URLs and authorization requests are not collected.
This is presentation-only after completion: Copy response and follow-up history retain the
unaltered answer. Failed/stopped turns do not get fallback sources.

Replies render incrementally when the upstream agent actually supplies text chunks; a
final-only reply appears all at once without artificial typing delays. The answer area shows
event-driven sending, accepted, working, receiving, and finalizing labels. The composer has
a generic "Ask a question..." placeholder and no agent label; its controls stay right-aligned.
Following the [Fluent 2 wait UX](https://fluent2.microsoft.design/wait-ux) guidance, waiting
is a single status line (pulsing dot, shimmering text, elapsed time) that each provider
update or phase label replaces in place; `Responding agent:` headers are stripped and long
text is truncated. Once the turn ends, the updates collapse into one **N updates** line above
the answer that expands into the full trail. Elapsed time starts at submission. After 20
seconds without answer text, the line's tooltip and the live region explain that no answer has
arrived and Stop remains available; they only say "Still working" if the agent has reported
work. The live region announces line changes, not every second or text fragment. Animations
stop under reduced motion.

The latest `agentId` / `agentName` from A2A text-part, message/artifact, or task/status metadata
drives the response heading and working/receiving indicator. IDs are available as tooltips;
an ID without a name is used as the label. Repeated identities in token chunks are deduplicated.
Metadata-free events retain the last reported identity (or the configured agent initially).
The UI does not infer live handoffs from "Responding agent" prose or citation provenance.
Leading standalone "Responding agent: Name" paragraphs move into small "with Name" badges
beside the response heading, with a "Contributor named in the response" tooltip. Names matching
the displayed speaker are not repeated; duplicate contributors appear once. Code, quotes and
mentions later in the answer stay untouched. Streaming headers are converted only after their
paragraph is complete. This is display-only; copied text and history preserve the raw answer.
If the orchestrator only reports its own identity, inner specialists cannot drive live status.

Task failures, cancellation, rejection and required-input/authentication states are explicit
errors even after partial text. Partial text stays visible, but unsuccessful sources and
history are excluded from future requests; the failed local turn remains visible. Artifact completion does not finish a task. Task completion shows
"Finalizing response..." until the connection drains so late citations are retained; legacy
streams can still complete at EOF without a task-completed event.

The transcript follows new content only while the reader is within 64px of the bottom.
Scrolling up pauses following and reveals **Jump to latest**, which resumes it. Submitting
a new turn or starting a new chat also resumes following. Replies finishing do not steal
keyboard focus, and expanding Sources does not force a scroll.

Replies retain responder attribution. Follow-up turns keep the same A2A context and forward
only successful history, bounded by the shared handler's 20-turn limit. New chat rotates the
context and discards late updates; Stop aborts browser reception but the server may still
finish the delegated work. APIM consent responses offer the existing trusted consent link
and require the user to resend the request after authorization.

```text
npm test
npm run lint
npm run build
```

The Vite build outputs an independent `dist` directory. Public `VITE_*` values are baked into
the build; configure them before building. Anonymous local mode cannot be used in the
production preview.
