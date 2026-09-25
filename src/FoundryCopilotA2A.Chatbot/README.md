# Orchestrator Chat

A separately runnable React/Vite chatbot. It sends every turn to one configured Orchestrator
through the adapter's A2A `SendStreamingMessage` handler. It has no flow designer, agent picker,
specialist selector, catalog discovery, or network/trace panels.

The console and chatbot reuse the transport, endpoint validation, and consent helpers in
[`FoundryCopilotA2A.BrowserShared`](../FoundryCopilotA2A.BrowserShared). The existing console's
behavior and default trace collection are preserved.

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
conversations stay in memory and are cleared on sign-out, account changes, reload, sign-in
redirects, or New chat. After an interactive token renewal, resend the request explicitly.
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
```

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

The chatbot uses a compact navigation sidebar, a centered reading column, and a yellow
send control. On narrow screens the sidebar becomes a compact top bar. New chat, account
sign-in/sign-out, and Help & guidance remain available without adding console-only tools.
Help & guidance explains the keyboard shortcuts and session lifetime. Completed replies
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

Bounded Copilot Studio dynamic-plan `thought` values are displayed as persistent
`Thought: ...` entries under **Agent updates**, including when the turn fails. They remain
informative progress and are excluded from copied answers and follow-up conversation history.

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

Sources stay with successful responses in memory; failed/stopped turns clear partial citation
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
Elapsed time starts at submission. After 20 seconds without answer text, the UI explains
that no answer has arrived and Stop remains available; it only says "Still working" if the
agent has reported work. Provider progress is displayed separately as plain text. The live
region announces phase/progress changes, not every second or text fragment.

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
history are discarded. Artifact completion does not finish a task. Task completion shows
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
