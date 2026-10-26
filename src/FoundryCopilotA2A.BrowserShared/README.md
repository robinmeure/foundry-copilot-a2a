# Shared browser A2A client

Dependency-free TypeScript used by both browser apps. `sendMessage` keeps the existing
streaming request and text-only `ConversationTurn` history format. It also accepts a complete
JSON-RPC message response. Its result has optional `citations: CitationBundle`;
`onUpdate(answer, citations?)` receives the complete current answer and citation snapshot.
A callback may contain unchanged text and new citations.

`onAgent({ id?, name? })` reports changes to the responder's `agentId` / `agentName` metadata.
Text parts override message/artifact and task/status envelope metadata. Source-provenance
data and agent names embedded in answer prose are not used. Repeated identities across token
chunks are deduplicated; absent metadata does not reset the last reported identity.

## Conversation handles

Answer parts from a Copilot Studio agent may carry `metadata.copilotStudioConversation`, an
opaque handle issued by the adapter. `sendMessage` collects them into
`A2AExchange.conversationHandles`. Callers keep them per chat with `mergeConversationHandles`
(newest first, deduplicated, at most 8) and pass them back as `conversationHandles`. They are
sent as `params.message.metadata.copilotStudioConversations`. Clear them when the chat is reset
or a turn fails.

## Experimental inline file input

`sendMessage` accepts an optional `files` array containing one `{ name, mediaType, size, bytes }`
attachment, where `bytes` is base64. `inputFiles.ts` validates filename/type and the 5 MB decoded
size limit and reads browser-selected files without extracting their document content.
The request uses A2A 1.0 `{ raw, mediaType, filename }` parts alongside text.
Follow-up `ConversationTurn` history stays text-only; callers must deliberately select an
attachment on the turn that sends it. The adapter rejects malformed/oversized files, remote file
URLs, multiple files and unsupported providers. The repository's shared APIM policy remains
at a 1 MB total JSON request cap, so base64 files must be smaller than 768 KiB through that
gateway, with additional room for text/history. No gateway policy changes are included.

This advertises the adapter's experimental input transport, not verified file understanding
or guaranteed native-agent-to-agent file forwarding. Both text and file bytes participate in
adapter replay conflict detection. Outbound traces omit attachment payloads.

## Task lifecycle

`onTaskStatus({ state, message? })` reports task snapshots and status updates independently
of answer text and informative `onProgress` messages. It supports A2A 1.0 `result.task` /
`result.statusUpdate` and A2A 0.3 `kind: "task"` / `"status-update"` (including direct task
snapshots). `TASK_STATE_*` values normalize to lower-case, hyphenated state names.
An unknown/unspecified state is reported as `unknown`; malformed or unsupported states fail
explicitly. Status message text never becomes answer text, sources, or history.

Failed, canceled, rejected, input-required and auth-required tasks fail the request even
after partial answer text. The browser cancels reception rather than waiting indefinitely
for EOF on these states; it does not automatically retry or resume a required-action task.
Response/trace callbacks remain available for diagnostics before the task error is thrown.

Artifact `lastChunk` is not task completion. A completed task enters a finalizing phase while
the connection drains, retaining trailing citations. Successful legacy streams without a
completed event still finish at EOF. Completion with no answer remains an explicit error.
Text append/replacement behavior is unchanged; the client never simulates token streaming
when an upstream agent returns one final answer.

## Structured plan activity v1

`sendMessage` requests `urn:foundry-copilot-a2a:dynamic-plan:v1` through the
`A2A-Extensions` header. A2A 0.3 callers use `X-A2A-Extensions` instead. The adapter
acknowledges activation using the matching response header; unactivated callers still
receive text status summaries. Both adapter CORS and the shared APIM policy allow/expose
these headers. Existing gateways must receive the updated policy before using this frontend.

`onPlanUpdate(event)` receives validated, deduplicated plan events from
`status.message.parts[].data[extensionUri]`; `onTaskStatus` also includes optional
`planUpdates`. Both wire versions are supported. The envelope has `schemaVersion: "1"`,
`eventId`, `sequence`, the full `DynamicPlan*` name, `originatingAgent`, `value` and
`detailsFiltered`. Event identity is scoped by originating agent and plan ID; conflicting
replays and unsupported/malformed recognized payloads fail explicitly.

Both frontends nest the activity timeline under the first text progress update, independently
of answer text. A neutral "Agent activity" parent is used for plan-only streams.
Expandable activity rows keep details and filtering notices out of the main reading path.
Details render as escaped plain text (never HTML, links or remote fetches), with keyboard
access to bounded scrollable content. Expanding the chatbot's update disclosure reveals the
child activity timeline; stream updates do not reset the user's disclosure choices.
Late final-plan events remain visible after the answer. A completed step, `isFinalPlan`,
or an artifact's `lastChunk` does not complete the overall request.

The adapter includes bounded, best-effort-redacted arguments and observations. It omits
thoughts, identity envelopes and sensitive-key fields, masks recognized credentials/email
addresses and strips URL query/fragment/credentials. Redaction is not a general DLP guarantee:
provider free text may still contain sensitive business content. Keep access restricted to
the authorized caller. `detailsFiltered` explicitly signals omitted/redacted/truncated fields.
Limits: 256 events per invocation, 16 KiB per envelope, 4096-character shared detail budget,
512 characters per detail string, 16 object members, 8 array items and nesting depth 5.
Exceeding event/envelope limits fails explicitly; detail limits truncate with a notice.

Plan details stay in memory for the open chat only. They are not copied with answers, stored
in browser history, or sent in follow-up model prompts. Ordinary status and artifact behavior
is unchanged. Adapter-managed APIM relays request and preserve the extension; native
model-mediated orchestration may discard intermediate events and is not guaranteed.

With the mock backend, send **`show plan example`** to exercise all four event types, filtered
details, an answer and a final plan arriving after it. No Azure resources are required.

## Structured citations v1

The canonical wire representation is an A2A data part alongside answer text:

```json
{
  "data": {
    "urn:foundry-copilot-a2a:citations:v1": {
      "schemaVersion": "1",
      "sources": [{
        "id": "source-1",
        "title": "Source document",
        "url": "https://example.com/document",
        "originatingAgent": { "id": "specialist", "name": "Original specialist" },
        "originatingMessageId": "source-message-1"
      }],
      "citations": [{
        "sourceId": "source-1",
        "marker": "[1]",
        "locator": "Page 2",
        "quote": "Literal excerpt."
      }]
    }
  }
}
```

A2A 0.3 uses the same data object with `"kind": "data"`. The same extension key in
`part.metadata` is accepted for compatibility. Unrelated data extensions are ignored.
Recognized malformed payloads, unsupported versions, conflicting source definitions, unknown
reference targets, and unsafe source URLs fail the response explicitly.

- `schemaVersion` must be the string `"1"`; `sources` and `citations` are required arrays.
- A source requires `id`, `title`, and `originatingAgent.id` / `.name`.
  `url` and `originatingMessageId` are optional.
- A reference requires `sourceId`; `marker`, `locator`, and `quote` are optional.
  Optional fields must be omitted, not `null`. Supplied strings must be nonblank.
- IDs, including `originatingMessageId`, are at most 256 characters; other strings are at
  most 8192 characters. A response permits 100 sources and 200 references.
- URLs must be absolute HTTP(S), with a host and no credentials, whitespace, control characters
  (including C0/C1 controls), or backslashes.
  The client never fetches, previews, or verifies source URLs.
- Credential-bearing query names are rejected after decoding, case-insensitively:
  `access_token`, `id_token`, `client_secret`, `token`, `sig`, `api_key`, `apikey`,
  `x-amz-signature`, and `x-goog-signature`. Invalid URLs fail without being echoed in the
  error or rewritten into invented, stripped links. Other query parameters/fragments remain intact.
- Sources without markers or references are valid. Markers are literal source metadata,
  not generated numbers; the browser does not parse arbitrary answer text into citations.
- Source IDs are stable within a turn. Identical definitions and identical reference tuples
  merge; reusing an ID for a different title, URL, or provenance fails.

Streaming data parts are **deltas**. Append events merge sources/references. Events containing
actual nonempty replacement answer text reset the current answer's citations before applying
their own metadata. Data-only events, including late final events with `append: false`, merge
without resetting the answer. Empty text parts are not replacements. References may target
sources received in earlier deltas; they must resolve in the current answer's merged bundle.
Progress/informative and task-status messages do not contribute final sources.

## UI and provenance boundary

Both apps show an accessible **Sources** list beneath each successful answer. The standalone
chatbot reveals it through a per-reply **Sources** toggle beside Copy response, with a count
of distinct sources; the diagnostic console shows its list directly. Each list includes the
original source title, literal marker/locator/quote, and originating agent label, separately
from the response author. Source links use `target="_blank"` and `rel="noopener noreferrer"`.
Source values render as escaped text, never raw HTML. Legacy text-only wire answers are unchanged.
The wire `quote` field is a reported source excerpt, not an independently verified quotation:
native Copilot Studio citation abstracts also map to this field.

Citation state stays with that response in memory. Failed or stopped turns are not promoted
to later history. Follow-up requests still send only successful user/assistant **text**, not
browser-asserted trusted source provenance. Inline markers in that text are not structured
citations for a future response.

Managed orchestrators may summarize tool output and omit A2A data/metadata. The browser can
only display structured citations actually returned by the final adapter response; it cannot
reconstruct verified provenance from prose or recover metadata lost upstream. Preserving it
through the orchestrator requires upstream runtime support, not changes to browser prompts.

Separately, the standalone chatbot offers a presentation-only fallback Sources list of safe
Markdown links when a successful answer has no structured sources. It labels them as links
from the response, assigns display numbers, and does not synthesize a `CitationBundle` or
agent provenance. The diagnostic console and the shared wire contract remain unchanged.

Wire/controller coverage runs with the Chatbot package's existing `npm test`; both browser
packages include server-rendered Sources tests. No shared React dependency is required.
