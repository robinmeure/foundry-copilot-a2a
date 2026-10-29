# Adaptive Cards over A2A: implementation plan

Date: 2026-10-23  
Status: **Phases 0-2 implemented behind a flag; Phase 3 CLI probe implemented, live/browser verification pending; Phases 4-5 gated**  
Implementation notes: the browser uses an in-house display-only renderer instead of the
`adaptivecards` package (no dependency, no actions, no remote requests). A card-only Copilot
Studio message promotes its fallback text to the answer even when the flag is off; card data is
still stripped.  
Basis: [reviewed feasibility research](adaptive-cards-a2a-research.md), repository
baseline `072dbbb`, and the independent **Opus 5.5** second opinion.

Current browser scope: Orchestrator Chat is the A2A card client, using the renderer in
ChatShared. The diagnostic flow-builder application has been removed. Knowledge Chat
continues to use the separate Knowledge API protocol; this plan does not add A2A cards there.

The lead and reviewer agree on the bounded approach below. The reviewer returned
**CONFIRMED WITH CORRECTIONS**; all six corrections were incorporated into the
research before this plan was written. That confirms feasibility, not live
Copilot Studio compatibility or native orchestrator support.

## 1. Scope and release boundaries

| Capability | Decision |
| --- | --- |
| Normal Adaptive Card attachments from a directly invoked Copilot Studio agent | Implement behind an off-by-default adapter feature flag; prove live delivery separately |
| A2A 1.0 and 0.3, streaming and nonstreaming | Support through the existing hosting/compatibility stack |
| Display in Orchestrator Chat | One shared, restricted, read-only renderer in ChatShared |
| Text-only/unaware clients | Continue to receive usable text without needing a renderer |
| Object-valued `Action.Submit` | Separate opt-in phase, only after a live SDK probe succeeds |
| `Action.Execute`, refresh, SSO/authentication cards, automatic actions | Not in the initial implementation |
| Native Foundry/Copilot Studio model-mediated delegation | Experimental compatibility investigation, not promised card pass-through |
| Horizontal scaling of interactive cards | Blocked until shared atomic action state exists |
| Mock backend | Must support deterministic local card scenarios without Azure |

Do not upgrade the A2A SDK, replace Agent Framework, introduce a file-upload
service, or add a generic action endpoint. The installed citation data-part
path already works across both wire versions. Do not migrate the current
string `AskQuestionAsync` overload on the mistaken premise that it is obsolete.

## 2. Proposed contract

### Output

Use a project-owned envelope keyed by the proposed URI
`urn:foundry-copilot-a2a:adaptive-cards:v1`. This is a **local contract**, not an
official A2A extension or a discovered ecosystem standard.

Each logical answer contains:

1. Its ordinary text, or a deterministic safe fallback when the provider
   returned only a valid card.
2. An optional A2A data part containing the versioned card envelope.
3. Existing citations, responder identity, and protected conversation continuity
   information without changing their meaning.

The envelope contains `schemaVersion` and an ordered `cards` collection. Each
card has an opaque stable `id`, `revision`, `contentType`, bounded validated
`content`, `fallbackText`, and provider/message provenance. The attachment
content type is `application/vnd.microsoft.card.adaptive`; it is a field within
the envelope, not a new A2A Part type or transport MIME requirement.

Use `Part.FromData(...).ToAIContent()`, exactly as
[AgentCitations](../src/FoundryCopilotA2A.Adapter/AgentCitations.cs) does. The 1.0
raw representation is converted to a 0.3 DataPart by the compatibility endpoint.
Do not route cards through the standalone legacy DataContent-to-FilePart converter.

For the first version, explicitly define this as **passive optional enrichment**:
advertise `required: false`, retain text, and require no extension activation
header. Emit cards only when the server feature is enabled. If later negotiation
is introduced, handle `A2A-Extensions` for 1.0 and `X-A2A-Extensions` for 0.3,
including acknowledgement, CORS and gateway forwarding; do not assume the names
are interchangeable.

### Identity, ordering and limits

- Emit complete card objects, never partial JSON/token fragments.
- Scope identity to the original provider response/Activity and attachment
  position, not to display text. If an Activity lacks an ID, assign one once and
  retain it in the cached invocation updates.
- Same ID/revision/same payload is a duplicate; same ID/revision/different
  payload is an explicit conflict. A newer revision replaces that card, not the
  whole answer or another card.
- Keep artifact identity and `append`/replace semantics separate from card
  identity. Final snapshots and replay must not duplicate text, citations or cards.
- Proposed application limits, **not protocol limits**: 64 KiB UTF-8 JSON per
  card, five cards and 256 KiB card JSON per turn, depth 32, and 4,000 characters
  per fallback. Enforce both per-update and accumulated-turn limits and test
  the exact boundary plus one unit beyond it.
- Pin the renderer package during the contract spike. Record the supported
  schema ceiling and restricted feature list based on its verified behavior,
  not Teams' version guidance. Test the ceiling and the next unsupported version.

### Fallback and errors

Use a bounded card `fallbackText`, otherwise validated visible text fields in
stable order. Exclude hidden action data, input values, credentials, and provider
correlation fields. A valid card with no displayable content gets an explicit
notice that the agent returned content requiring unsupported interaction.

Normalize this before existing text-based success checks, handle issuance and
cache aggregation. Keep the nonempty-text invariant: citations-only,
progress-only, malformed-card-only and unrelated-data-only responses still fail.
Do not loosen every success check to accept arbitrary JSON.

Malformed recognized envelopes/cards produce explicit, sanitized errors.
Valid but unsupported versions/features produce a visible fallback/notice.
Do not silently present a dropped form as a completed business action.

## 3. Delivery sequence

### Phase 0 - Freeze the contract and establish local fixtures

**Depends on:** nothing beyond the agreed research.

1. Add small synthetic card fixtures: display-only, card-only, text plus card,
   two cards, late final card after streamed text, unsupported version,
   unsupported action, malformed card, and oversized/nested payloads.
2. Establish the exact renderer version and supported display subset, and pin
   any dependency in the consuming workspace manifest/lockfile. Prefer the official
   `adaptivecards` package with a small shared React/DOM wrapper; no new UI framework.
3. Add tests demonstrating the proposed keyed envelope follows the existing
   citation transport path in both A2A versions. Include router and target-specific
   endpoints, streaming/nonstreaming, replay and discovery.
4. Introduce one off-by-default backend gate, proposed
   `Adapter:AdaptiveCardsEnabled`. Feature-off behavior must remain unchanged.

**Exit:** the contract, limits and renderer version are recorded; local fixtures
prove the wire representation without requiring a live provider.

### Phase 1 - Preserve provider cards through the adapter

**Depends on:** Phase 0.

Implement small dedicated card extraction/validation helpers and typed optional
card collections on the existing invocation result/update records. Reuse the
pattern of [CopilotStudioCitations](../src/FoundryCopilotA2A.Adapter/CopilotStudioCitations.cs),
not a general-purpose content framework.

Relevant changes:

- [CopilotStudio activity handling](../src/FoundryCopilotA2A.Adapter/CopilotStudio):
  recognize normal Adaptive Card attachments, normalize fallback text, preserve
  cards on final activities even when repeated streamed text is suppressed, and
  issue continuity handles for normalized card-only answers. Keep OAuth and
  `connectors/connectionManagerCard` handling separate and intact.
- Account for card-bearing startup Activities currently consumed only for
  conversation ID/OAuth. Preserve supported presentation content deliberately,
  but never let an unrelated welcome card hide a failed subsequent user turn.
- Ensure a card validation exception cannot trigger the existing
  cached-conversation restart/replay path. Treat it like the existing
  non-restartable citation-format failure.
- [CopilotStudioAdapterChatClient.cs](../src/FoundryCopilotA2A.Adapter/CopilotStudioAdapterChatClient.cs):
  preserve card-only updates after normalization, serialize the envelope, and
  retain ordered card snapshots in streaming replay and nonstreaming aggregation.
  Preserve responder metadata and citations on the same answer.
- [Program.cs](../src/FoundryCopilotA2A.Adapter/Program.cs): advertise the
  optional envelope on every enabled discovery surface: router 1.0, converted/
  blended 0.3 aliases, and manually built target-specific 0.3 cards. Extend the
  current converter workaround; do not remove it or upgrade packages just for cards.
- Extend the existing mock with deterministic card scenarios. Mock output must
  say it is synthetic and must not imply a live provider was tested.

For [FoundryA2AInvoker](../src/FoundryCopilotA2A.Adapter/FoundryA2AInvoker.cs) and
[ApiManagementA2AInvoker](../src/FoundryCopilotA2A.Adapter/ApiManagementA2AInvoker.cs),
extend the shared response parser/stream accumulator to preserve **recognized,
validated envelopes if actually returned**, including artifact replacement and
final snapshots. Do not promote arbitrary JSON into cards, invent missing
provenance, or forward action authority from untrusted upstream metadata.
Preserving data at these boundaries does not prove the native provider emits it.

**Exit:** local mock cards and recognized upstream fixtures survive end to end;
feature-off, existing text/files/citations/authentication, and error behavior
remain covered.

### Phase 2 - Shared read-only rendering and safe history

**Depends on:** Phase 1 and the renderer selection from Phase 0.

1. Extend [BrowserShared/a2aClient.ts](../src/FoundryCopilotA2A.BrowserShared/a2aClient.ts)
   with typed card parsing, exchange snapshots and update notifications.
   Orchestrator Chat uses this shared transport.
2. Accumulate by artifact/message and card identity. Handle data-only updates,
   replacements, late finals, repeated snapshots and replay without resetting
   existing answer text/citations accidentally.
3. Add one shared renderer with `supportsInteractivity=false`. Enforce a
   recursive policy before parsing: no actions/selectAction, inputs, refresh,
   authentication, remote resources, or HTML. Plain card text is sufficient
   initially; do not introduce an HTML-producing Markdown hook. Unsupported
   features must yield a visible notice, not silent removal.
4. Wire the chatbot transport to the shared render surface:
   [Chatbot chatSession](../src/FoundryCopilotA2A.Chatbot/src/chatSession.ts) and
   [ReplyBody](../src/FoundryCopilotA2A.ChatShared/src/ReplyBody.tsx). Preserve existing Markdown,
   source attribution, consent/connection repair, progress and cancellation UX.
5. Keep card objects transient. Persist only safe fallback text through
   [conversationStore](../src/FoundryCopilotA2A.ChatShared/src/conversationStore.ts);
   restored chats must not restore forms or hidden data. Existing conversation
   handles remain under their current policy; future action handles do not.
6. Ensure trace capture and any request/response diagnostics use redacted
   diagnostic projections. Never mutate the actual transport payload to produce
   a diagnostic view. Card data and future submitted values are not safe just
   because their property names lack the words `token` or `secret`.

**Exit:** Orchestrator Chat renders the supported mock cards, shows honest fallbacks,
and perform zero action callbacks or external card-resource requests. Verify
keyboard/screen-reader reading order, narrow layout, theme contrast, safe
restoration and renderer cleanup with focused browser checks.

### Phase 3 - Verify live direct-provider display

**Depends on:** Phases 1-2. Requires user-authorized live credentials/configuration.

**Implementation:** the CLI now provides
[`test-adaptive-cards`](../src/FoundryCopilotA2A.Cli/README.md#verifying-adaptive-card-delivery).
It supports direct SDK verification and/or the four A2A protocol/response combinations,
with optional SDK-to-wire content comparison. Authentication reuses the file-probe helper.
Live execution, both browser checks and any configured APIM checks remain pending; this
phase is not complete merely because the command and synthetic tests exist.

The operational probe lives in
[FoundryCopilotA2A.Cli](../src/FoundryCopilotA2A.Cli), using the established
[file probe](../src/FoundryCopilotA2A.Cli/CopilotFileUploadProbe.cs) and
[smoke command](../src/FoundryCopilotA2A.Cli/SmokeTestCommands.cs) patterns.
Register/help/document the command in the CLI; do not add shell scripts.

Use a synthetic published topic that deliberately emits a known display card,
not an LLM request hoping for card JSON. Check:

1. Direct SDK: an actual Activity attachment has the expected content type,
   card structure and synthetic marker.
2. Full adapter: the same fixture appears in the expected JSON envelope with
   usable fallback in 1.0/0.3 and streaming/nonstreaming.
3. Both browsers: the card is rendered, not merely printed as JSON.
4. Transparent APIM ingress, if configured: repeat the wire assertions and
   distinguish gateway transformation/limits from adapter behavior.

Print safe assertions/counts, not tokens, card data or environment identifiers.
A 200 response or a text-only echo is not a passing card test. Until these checks
pass, label live card delivery **experimental/unverified**, while mock support
may be described as verified.

### Phase 4 - Optional, direct Copilot Studio `Action.Submit`

**Depends on:** Phases 1-3 and a separate live action probe. Keep disabled by default.

First use the CLI to prove the pinned SDK can submit a known object-valued form
through the original provider conversation and obtain the expected synthetic
business result. The candidate is a message Activity with `Value` sent using
`ExecuteAsync`; finalize its exact text/value/reply correlation from the probe.
The agent-side `OnAdaptiveCardInvokeAsync` hook is not proof. If the probe fails,
stop at read-only cards rather than silently converting the submission to prose.

After that gate:

- Define a separate versioned action request inside an ordinary A2A data part.
  Allow exactly one supported action, require context and message IDs, and reject
  mixed files/actions, forged correlations and unsupported providers/routes.
  Update [request metadata parsing](../src/FoundryCopilotA2A.Adapter/A2ARequestContextMiddleware.cs),
  [routing](../src/FoundryCopilotA2A.Adapter/RoutingAgentInvoker.cs), and
  [request hashing](../src/FoundryCopilotA2A.Adapter/AdapterConstants.cs) together.
- Issue an opaque protected action handle bound to caller/tenant, configured
  agent, A2A context, original provider conversation, card ID/revision, action
  ID, expiry and an atomic claim identifier. Bind to the original conversation,
  not whichever conversation handle happens to be newest in the browser.
- Keep allowed input names/types/bounds and original action data server-side.
  Merge accepted inputs using the validated schema; clients may not replace
  routing fields, hidden action data, activity type, endpoint or provider identity.
  Initially exclude string-valued Submit and channel-specific overrides such
  as Teams `msteams` action data.
- Hash the normalized action identity, correlation and input JSON canonically
  across protocol shapes and object-property order. Same message ID with changed
  values is a conflict. Retrying the same logical submission reuses its message ID.
- Add action-level atomic claims in addition to message idempotency: a different
  message ID or second browser tab must not apply the same one-shot action twice.
  Serialize conflicting submissions for a provider conversation.
- The initial pending-action store may be bounded/process-local for explicitly
  single-instance use. Validate live store membership even if a protected handle
  decrypts. Restart, eviction or expiry makes the action unavailable, never new.
  Retain consumed/ambiguous-outcome records until the corresponding handle expires;
  pressure may invalidate actions but may not make consumed actions executable.
- No conversation restart, transcript replay or automatic retry after dispatch.
  Audit the existing OAuth retry path and HTTP resilience handlers as well as
  the explicit SDK loop. Preserve the original action shape if authentication is
  proven to have failed before dispatch; otherwise report an unknown outcome.
- Do not claim exactly-once provider side effects. On a disconnect/timeout after
  dispatch, report an ambiguous outcome and do not issue a fresh action. A client
  disconnect does not establish that the provider stopped. Poll/replay local
  recorded results only when available, without resubmitting the provider action.
- Extend Orchestrator Chat with validated inputs, a visible pending/submitted/
  unavailable/unknown-outcome state, and duplicate-click prevention. Do not
  persist form inputs or action handles; use safe text labels in the transcript.
- Add explicit redaction for Activity `Value`, keyed action envelopes, hidden
  data and action handles in
  [TraceSanitizer](../src/FoundryCopilotA2A.Adapter/Tracing/TraceSanitizer.cs),
  metadata `ToString`, logs and browser diagnostic views.

**Lifecycle choice:** each presentation and submission is a separate A2A turn/
task in the same context and exact provider conversation. Complete the
presentation task; do not reopen it. This does not assert that the business
workflow is complete. True `input-required` task suspension/resumption is a
separate future feature: the current browser treats it as an error and the
0.3 final-status middleware only handles terminal states.

**Exit:** live same-conversation outcome proof plus concurrency, replay,
cross-user/agent/context, stale-card, restart, timeout, failed-auth and redaction
tests pass. Direct-only scope is enforced. Multi-replica actions remain disabled
until a shared atomic claim store is designed and tested.

### Phase 5 - Native-chain investigation, not an initial release dependency

**Depends on:** verified direct transport; requires separately authorized native
provider configuration.

Run the same deterministic card fixture through Foundry-native and
Copilot-Studio-native A2A delegation separately. Assert original card JSON,
identity, provenance and fallback at each observable boundary, not a paraphrase
or model-generated replacement. Test APIM-as-upstream independently from
transparent ingress.

If a native orchestrator drops or rewrites the envelope, report that route as
text-fallback-only. Do not use an LLM to reconstruct card JSON, infer action
authority from copied text, bypass provider-managed routing/authentication, or
retarget a native connection. Interactive relaying requires its own explicit
trusted correlation/delegation design and live evidence.

## 4. Required validation matrix

| Area | Required cases |
| --- | --- |
| Wire | 1.0/0.3 x router/target route x streaming/nonstreaming; feature off/on; unaware text client |
| Content | Text only, text+card, card-only normalized fallback, multiple cards, citations+cards, startup card, no usable answer |
| Streaming | Late card after duplicate final text; data-only update; append/replace; final snapshot; stable IDs; conflicting revisions |
| Replay | Same request/response cards once; changed action payload conflict; simultaneous subscribers; request disconnect |
| Bounds | UTF-8 bytes at/beyond each limit; cumulative size/count; nesting; unsupported version/type; malformed recognized envelope |
| Host safety | Nested actions/selectAction, hidden inputs, refresh/auth, HTML, unsafe links, remote image/icon/background/media blocked |
| Existing behavior | Text, uploaded files, citation provenance, OAuth/connection repair, responder identity, failure/progress/cancellation |
| Persistence/telemetry | Restored fallback only; no input/action data or handles in saved chat, replayed history, traces or diagnostics |
| Submit gate | Expected live business result in original conversation; tampering; stale revision; duplicate click/new message ID; concurrency; unknown outcome; restart fail-closed |
| Route claims | Direct SDK, full adapter, Orchestrator Chat, transparent APIM, APIM invoker and each native orchestrator reported separately |

Use the existing xUnit and Node test infrastructure. Start with the affected
adapter test classes and browser parser/session/persistence tests; add focused
card counterparts. Run the chat workspace builds and linters for the renderer work.
Use browser checks for DOM behavior and accessibility that Node model tests
cannot establish. Run wider suites when shared parser/runtime changes warrant it.

The research baseline was **37 passing citation adapter tests** and **90 passing
browser citation/persistence/task-state tests**. Those results justify reusing
the current transport; they are not card implementation or live-provider tests.

## 5. Completion, documentation and rollback

- Update the main README, Orchestrator Chat and shared-client READMEs, CLI help/README, and this
  research/plan status as features actually land. Distinguish supported feature
  subsets, mock verification and each live route's evidence.
- Keep provider identifiers and credentials in existing environment/configuration
  workflows, never in fixtures or committed probe output.
- Backend display and later submit gates default off. Disabling structured card
  emission restores the prior text/file/citation path; invalidated actions never
  execute after disable/re-enable.
- Suggested implementation slices: (1) contract+mock+adapter tests, (2) the
  read-only client+safe persistence/diagnostics, (3) CLI/live display verification,
  (4) separately gated direct Submit. Do not block useful read-only support on
  unsupported native orchestration or Universal Actions.

**Definition of done for the first release:** Orchestrator Chat displays
validated mock cards over the unchanged A2A endpoints, fallback/error/limit and
regression tests pass, and no card action or remote resource executes. Claim
live direct Copilot Studio support only after Phase 3 passes. Interactive and
native-chain support require their own later gates.
