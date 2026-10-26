# Copilot Studio Activity Streaming → A2A Mapping — External Source Research

> Follow-up: structured DynamicPlan transport and both frontend activity timelines are now
> implemented; see the [current plan contract](../src/FoundryCopilotA2A.BrowserShared/README.md#structured-plan-activity-v1).
> Findings below describe the earlier research snapshot. The separate final-answer text
> suppression issue is not addressed by the plan-update implementation.

Scope: this document captures **only externally-verifiable primary-source facts** (official A2A
protocol specification, Microsoft Learn docs, and Microsoft first-party SDK reference docs) relevant
to the Copilot Studio → A2A streaming bridge trace under review. No repository implementation files
were inspected by this research pass (per task scope); the final section records the parent
session's own repository findings verbatim/attributed for completeness, and is **not** independently
verified here.

All sources were accessed 2026-10-26.

## Executive conclusion

**No: the adapter streams answer text and selected progress, but it does not
preserve every incoming event or its payload.** The most important functional
gap in this trace is that the final message is not identical to the streamed
text: it normalizes citation markers and formatting, yet its text is discarded.
This behavior was reproduced with sanitized characterization tests.

| Incoming item | Current A2A-facing behavior |
|---|---|
| Plain `typing` | One informative `Working...` update |
| `DynamicPlanReceived` before answer | Short planning summary; raw payload not preserved |
| `DynamicPlanStepTriggered` | Short running-step summary |
| `DynamicPlanStepBindUpdate` | No update for the supplied arguments-only shape |
| Informative `typing` | Text forwarded with project-specific `isInformative` metadata |
| `DynamicPlanStepFinished` | Short completion summary; search observations not forwarded |
| Streaming `typing` deltas | Text forwarded incrementally; sequence/chunk fields not preserved |
| Final `message` text | Suppressed after nonempty deltas for the same stream, even if different |
| Final citation entities | Selected source/reference fields survive in the citation extension |
| Final empty `DynamicPlanReceived` after answer | Suppressed |
| Upstream `event: end` | Not relayed verbatim; A2A has its own completion semantics |

The current browser client can interpret artifact replacement, but implementing
final-text reconciliation also requires changes to the server update model,
aggregation, replay, and hosting conversion. Simply forwarding the final text
as another delta would duplicate the answer.

Verification: **92 focused checks passed**, including three temporary synthetic
probes that characterize the gaps, not prove full support. No application code,
dependencies, or configuration were changed. No live Copilot Studio replay or
native Foundry consumer compatibility test was performed.

---

## 1. A2A protocol: streaming event model (what is standardized)

Primary sources:
- A2A spec, latest (v1.0.0 object model): https://a2a-protocol.org/latest/specification/
- A2A spec v0.3.0 (JSON-RPC/gRPC wire objects): https://a2a-protocol.org/v0.3.0/specification/
- A2A topic guide, streaming: https://a2a-protocol.org/latest/topics/streaming-and-async/
- A2A topic guide, task lifecycle: https://a2a-protocol.org/latest/topics/life-of-a-task/

### 1.1 Only two streaming delta event kinds exist

The A2A spec defines exactly two event types that a streaming/subscribed response may emit after
the initial `Task`/`Message`:

- **`TaskStatusUpdateEvent`** (`kind: "status-update"` in v0.3 JSON-RPC form) — fields: `taskId`,
  `contextId`, `status` (a `TaskStatus`, itself containing a `state` enum and an **optional
  `message`** field of type `Message`), `final` (v0.3) / implicit terminal detection (v1.0), and
  `metadata` (opaque object).
  (v1.0 spec §4.2.1; v0.3 spec §7.2.2 — "An event sent by the agent to notify the client of a
  change in a task's status.")

- **`TaskArtifactUpdateEvent`** (`kind: "artifact-update"`) — fields: `taskId`, `contextId`,
  `artifact` (an `Artifact` object: `artifactId`, `name`, `description`, `parts[]`, `metadata`,
  `extensions[]`), **`append`** (boolean, optional — *"If true, the content of this artifact should
  be appended to a previously sent artifact with the same ID"*), **`lastChunk`** (boolean, optional —
  *"If true, this is the final chunk of the artifact"*), and `metadata`.
  (v1.0 spec §4.2.2; v0.3 spec §7.2.3, verbatim TypeScript/`protobuf` definitions confirmed.)

Exact v0.3 JSON-RPC TypeScript definitions (quoted from spec):
```ts
export interface TaskArtifactUpdateEvent {
  taskId: string;
  contextId: string;
  readonly kind: "artifact-update";
  artifact: Artifact;
  /** If true, the content of this artifact should be appended to a previously sent artifact with the same ID. */
  append?: boolean;
  /** If true, this is the final chunk of the artifact. */
  lastChunk?: boolean;
  metadata?: { [key: string]: any; };
}
```

### 1.2 "Append" semantics are the *only* standardized concatenation mechanism — and only for Artifacts

- `append` only applies to **artifacts** (file/data/text outputs keyed by `artifactId`), not to
  **messages** or **status updates**. There is **no standardized "delta text" concept for
  `TaskStatus.message`** — each `TaskStatusUpdateEvent.status.message` is specified as a complete
  `Message` object (full `parts[]`), not an incremental fragment. The spec gives agents no obligation
  to chunk status/progress messages, and no `append`/replace flag exists on `TaskStatus.message` at
  all.
- For artifacts, the spec defines `append=true` as "should be appended … with the same ID" but
  **does not explicitly define behavior when `append` is `false`/absent for a **repeated**
  `artifactId`** (i.e., whether a second non-append chunk for the same `artifactId` *replaces* prior
  content or is an error). The spec text only states the positive append case; replacement/overwrite
  semantics for a reused `artifactId` with `append` omitted are **not explicitly specified** — this is
  a genuine spec gap, not something this research can resolve definitively from the primary text.
- **No field anywhere in the A2A object model asserts or requires that a final artifact/message's
  full text must equal the concatenation of previously streamed delta chunks.** The protocol is
  silent on this; an agent emitting a final, fully-formed `Message`/`Artifact` whose content differs
  from (e.g., is a cleaned-up/reconciled rewrite of) earlier partial text is **not a protocol
  violation** per anything found in the spec. This directly supports treating "final" as potentially
  **authoritative replacement text**, not merely a terminal marker confirming delta equality — but
  this is an inference from absence of a constraint, not an explicit statement endorsing
  rewrite-on-final; no primary source explicitly discusses "citation marker normalization on final"
  as a sanctioned pattern.

### 1.3 Stream termination has no payload-level "end" event

> "Stream Termination: When a task reaches a terminal or interrupted state (e.g., COMPLETED, FAILED,
> CANCELED, REJECTED, or INPUT_REQUIRED), the server closes the stream and sends no further updates."
> — https://a2a-protocol.org/latest/topics/streaming-and-async/

A2A SSE streams terminate via **HTTP connection close** after the terminal status event; the spec
defines **no explicit `event: end` / sentinel payload**. A literal `event:end` / `data:end` SSE frame
(as seen in the user trace) is **not an A2A protocol construct** — if present on the wire, it
originates from a different layer (e.g., the Direct Line/Copilot Studio transport, or a
bridge/adapter convention), not from the A2A specification itself. No official Microsoft or
a2a-protocol.org source documents an `event:end`/`data:end` SSE sentinel as part of either the A2A
wire format or (in the sources checked) the Direct Line 3.0 streaming/`endOfConversation` activity
docs.

For the repository's A2A 0.3 endpoint, the final status event also carries
`final: true`; connection closure is not a substitute for that protocol field.
The adapter's existing final-status middleware and focused tests cover this
compatibility requirement. Do not confuse this with the upstream SSE `end`
frame or the Copilot Studio final-plan event.

### 1.4 No standard "dynamic plan" / planning-progress event exists in A2A

Targeted searches of the A2A specification and topic guides (for "plan", "step", "DynamicPlan",
"observation", "search_results") returned **no matching standardized event, field, or extension
name**. The only sanctioned extension points for arbitrary progress/plan data are:
- `TaskStatusUpdateEvent.status.message` (a full `Message`, whose `Part`s may include a `data` /
  `DataPart` with arbitrary JSON — the generic mechanism for custom structured content), and
- the generic `metadata` object present on `TaskStatusUpdateEvent`, `TaskArtifactUpdateEvent`, and
  `Artifact`/`Message` themselves, and
- the spec's formal **extension** mechanism (`AgentCard`-declared extension URIs; `Message.extensions`
  / `Artifact.extensions` arrays of extension URI strings) — a vendor can declare and use a
  project-specific extension to carry plan/step data, but this is explicitly an **opt-in, declared
  extension**, not a predefined A2A concept.

**Conclusion: `DynamicPlanReceived` / `DynamicPlanStepTriggered` / `DynamicPlanStepBindUpdate` /
`DynamicPlanStepFinished` are not A2A-standard event names or valueTypes.** No primary source (A2A
spec, A2A topics, Microsoft Learn Copilot Studio docs, Microsoft Learn Agent Framework/Foundry docs)
documents these names. They are consistent with being a **Copilot Studio-internal/project-specific
Bot Framework Activity `valueType` extension** (i.e., carried as custom Bot Framework `Activity`
payloads over Direct Line/the Copilot Studio channel, upstream of any A2A bridging), not a published,
versioned public contract. This could not be confirmed either way from public Microsoft Learn
documentation — treat as **undocumented / internal surface**, subject to change without notice.

---

## 2. Microsoft Foundry native A2A client/consumer behavior

Primary sources:
- Agent Framework A2A integration (client-side `A2AAgent`): https://learn.microsoft.com/agent-framework/integrations/by-component/agent-services/a2a
- Foundry Agent Service — connect to A2A endpoint (tool/consumer side): https://learn.microsoft.com/azure/foundry/agents/how-to/tools/agent-to-agent
- Foundry Agent Service — A2A protocol version/tool table: same page as above

### 2.1 Foundry Agent Service's own A2A tool explicitly does **not** support streaming for Foundry targets

> "Foundry agent targets support generally available A2A protocol version 1.0 and preview version
> 0.3. Version 1.0 uses JSON-RPC. **Only text modality is supported, and streaming responses aren't
> supported.**" — https://learn.microsoft.com/azure/foundry/agents/how-to/tools/agent-to-agent
> (Prerequisites section, referencing `.../enable-agent-to-agent-endpoint#limitations`)

This statement is scoped to the Foundry **A2A tool calling another Foundry agent target** (and
possibly more broadly any target reached through this managed tool path) — it explicitly says
streaming is unsupported there, and only text modality (not generic artifact/status-update
metadata, plans, citations, etc.) is carried. **This is a documented negative capability**, not
silence.

### 2.2 The generic `Microsoft.Agents.AI.A2A` client library (Agent Framework) surfaces text and a continuation token — no documented pass-through of arbitrary progress/plan metadata

Per the official code sample (https://learn.microsoft.com/agent-framework/integrations/by-component/agent-services/a2a,
"Streaming" section), the supported, documented client usage pattern is:

```csharp
await foreach (var update in agent.RunStreamingAsync("Write a short story about a robot."))
{
    if (!string.IsNullOrEmpty(update.Text))
    {
        Console.Write(update.Text);
    }
}
```

The only other documented per-update field surfaced to app code is `update.ContinuationToken` (for
background-response reconnection; see "Background Responses"/"Stream Reconnection" sections, same
page). **No documented API or sample shows the generic `A2AAgent`/Agent Framework client exposing
custom `TaskStatusUpdateEvent.metadata`, `Artifact.metadata`, plan/step progress, or citation
entities to the consuming application.** Whether such data is silently dropped, buffered, or
otherwise accessible via a lower-level escape hatch is **not stated** in the pages reviewed —
this is a genuine **documentation gap**, consistent with the task's framing that "state [is]
unknown where undocumented." No statement found (positive or negative) describes whether a generic/
native Foundry or Agent Framework A2A client renders arbitrary `metadata`/custom parts in any UI.

### 2.3 Summary of what is and isn't confirmed for Foundry-side A2A handling

| Question | Status |
|---|---|
| Does Foundry's A2A tool support streaming at all (to a Foundry target)? | **No** — explicitly documented as unsupported (§2.1). |
| Does the generic Agent Framework A2A client (consuming an arbitrary/non-Foundry A2A server) support streaming? | **Yes** — documented, via `RunStreamingAsync`/SSE (agent-services/a2a page, "Streaming" section). |
| Does that generic client expose `TaskStatusUpdateEvent`/`TaskArtifactUpdateEvent` metadata, plan/progress data, or citation entities to app code beyond `update.Text`/`ContinuationToken`? | **Not documented either way** — only `Text` and `ContinuationToken` appear in official samples. |
| Is there a documented, official mapping from Copilot Studio Bot Framework Activities (`DynamicPlan*`, `streamType`, `chunkType`) to A2A event types? | **No such mapping is published** in any source reviewed. |

---

## 3. Citation entity convention (`schema.org/Message`-style claim/appearance entities)

Primary sources:
- Teams AI content labels / citations (official, current): https://learn.microsoft.com/microsoftteams/platform/agents-in-teams/integrate-ai/ai-content-labels#citations
- Bot messages with AI-generated content (referenced by SDK docs as canonical source): https://learn.microsoft.com/microsoftteams/platform/bots/how-to/bot-messages-ai-generated-content
- `@microsoft/agents-activity` JS SDK reference — `addAIToActivity`, `ClientCitation`:
  https://learn.microsoft.com/javascript/api/@microsoft/agents-activity/
  https://learn.microsoft.com/javascript/api/@microsoft/agents-activity/clientcitation

### 3.1 The observed citation shape is the documented `ClientCitation` / AI-generated-content entity, not a custom Copilot Studio invention

The `ClientCitation` interface (current, GA, first-party SDK type) is documented as:

```ts
interface ClientCitation {
  '@type': 'Claim';
  position: number; // used to resolve in-text markers like [1]
  appearance: {
    '@type': 'DigitalDocument';
    abstract: string;         // short excerpt/summary shown in the citation popup
    encodingFormat?: 'application/vnd.microsoft.card.adaptive';
    image?: { '@type': 'ImageObject'; name: ClientCitationIconName };
    keywords?: string[];
    name: string;             // source title
    text?: string;            // full verbatim cited text (distinct from `abstract`)
    url?: string;
    usageInfo?: SensitivityUsageInfo;
  };
}
```
(Quoted/paraphrased field shapes from the `ClientCitation` reference page; `addAIToActivity`
example shows these exact property names in use, including `appearance.abstract` vs. the separate
`appearance.text` field.)

Confirms (externally) a specific, named field distinct from `abstract`: **`appearance.text`** is a
documented, first-party field intended to carry the fuller/verbatim cited text, separate from the
shorter `abstract` summary shown in the UI popup. This corroborates that `appearance.text` is not an
incidental/undocumented field — it is part of the official `ClientCitation` contract and has a
distinct documented purpose from `abstract`.

The in-text marker convention — `[1]`-style numeric markers inserted into message text, each
resolved against a citation's `position` — is documented in the Teams "AI content labels" page
("In-text citations denote the citation numbers added to the agent message in the `[#]` format").
This confirms `[1]`-style bracketed numeric markers are the documented/expected **final, normalized**
in-text citation representation; no official source describes the private-use-area raw marker
tokens (`U+E200`/`U+E201`/`U+E202`-class characters) as a documented or sanctioned intermediate
representation — those are consistent with being an internal/raw model-output convention that must
be rewritten to `[#]` before being considered conformant with the documented citation UX, but **no
Microsoft Learn source explicitly describes or names this private-use-area raw marker format** (gap
— could not be verified from public docs).

### 3.2 Do not conflate claim IDs with sensitivity-label IDs

The "Sensitivity label" section of the same Teams doc documents `citation.usageInfo.@id` as a
required field "to enable the sensitivity label in the citation reference," confirming that
that nested sensitivity identifier has a documented purpose. It does **not**
establish the meaning or contractual status of the distinct claim-level
`citation.@id` used in the supplied trace. The adapter does not retain that
claim-level ID; its connection to the raw model citation tokens remains an
observation from this trace, not a verified public SDK contract.

---

## 4. Summary of verified vs. unverifiable claims

**Verified from primary sources:**
1. A2A `append`/`lastChunk` are artifact-only fields, with positive ("append=true means concatenate")
   but not fully specified negative ("append omitted for a repeat artifactId") semantics.
2. A2A has no standardized delta/chunking mechanism for `TaskStatus.message` or for `Message` content
   generally — only `Artifact` chunks are standardized as streamable/appendable.
3. A2A does not require or state that final content must equal the concatenation of prior deltas;
   nothing in the spec forbids an agent from sending reconciled/rewritten final text.
4. A2A stream termination is connection-close driven; no `event:end`/`data:end` sentinel is part of
   the A2A spec.
5. No `DynamicPlan*` event/valueType exists in the A2A spec, Microsoft Learn Copilot Studio docs, or
   Agent Framework/Foundry docs reviewed — treat as an undocumented, Copilot Studio-internal surface.
6. Foundry's own A2A tool (calling a Foundry A2A target) explicitly does not support streaming and
   only carries text — a documented negative capability.
7. The generic Agent Framework A2A client's documented, official usage surfaces only `update.Text`
   and `update.ContinuationToken` to app code; no documented exposure of custom metadata/progress to
   consuming code or UI.
8. The `[1]`-style bracketed citation marker and `ClientCitation.appearance.{abstract,text,name,url,
   usageInfo}` shape, including a distinct `appearance.text` field separate from `abstract`, are part
   of the official, current `ClientCitation`/AI-content-labels contract.

**Could not be verified from public primary sources (explicit gaps/uncertainty):**
- Whether any native/first-party A2A client (Foundry or otherwise) renders arbitrary
  `TaskStatusUpdateEvent.metadata`/`Artifact.metadata` progress or plan data in any UI — undocumented
  either way.
- Whether `append=false`/absent for a repeated `artifactId` is defined as replace, error, or
  undefined behavior — the spec text only states the positive append case.
- Any official/published mapping from Copilot Studio Bot Framework Activity streaming
  (`streamType`/`chunkType`/`streamId`/`streamSequence`, `DynamicPlan*` valueTypes) to A2A protocol
  objects — no such mapping document was found on Microsoft Learn or a2a-protocol.org.
  Correspondingly, no official source sanctions or forbids a bridge emitting a replacement/
  overwrite semantic for final text versus raw deltas; this is an implementation decision outside
  the scope of any spec reviewed.
- The precise origin/semantics of the raw private-use-area citation marker characters observed in
  model output before normalization to `[#]` — not documented in any Microsoft Learn or Teams/Bot
  Framework source reviewed.
- Whether `event:end`/`data:end` is a documented Direct Line 3.0 construct — the only related,
  confirmed Direct Line concept found is the separate `endOfConversation` **activity** (not an SSE
  sentinel), and that is explicitly noted as unsupported/dropped by some channels including Direct
  Line itself.

---

## 5. Repository findings (supplied by parent session — not independently verified in this pass)

The following is the parent orchestrating session's own report of repository inspection and test
results. It is included here verbatim/attributed for completeness of the combined document, per the
parent's instruction. This research pass did not inspect repository source and cannot independently
corroborate these claims; they should be read as the parent's first-hand findings plus its own test
verification, not as output of this external-research task.

### Coverage of trace event kinds as currently handled (per parent inspection)

- Plain `typing` → one de-duplicated informative "Working..." message.
- `DynamicPlanReceived` → rendered as `"Planning: P:UniversalSearchTool"` (tool/plan name only, not
  raw fields); the final/empty plan payload that arrives **after** the answer is suppressed.
- `DynamicPlanStepTriggered` → rendered as `"Running P:UniversalSearchTool..."`; an empty "thought"
  field is ignored.
- `DynamicPlanStepBindUpdate` (arguments) → **no output at all** is currently produced, unless a
  non-empty generic "thought" is present.
- Informative `typing` → original text is preserved, marked `isInformative`.
- `DynamicPlanStepFinished` → rendered as `"Finished P:UniversalSearchTool"`; `observation`,
  `search_results`, `planUsedOutputs`, `executionTime`, and `cost` fields are **not relayed**.
- Streaming `typing` deltas → exact text is forwarded, concatenated per stream; leading
  spaces/newlines are preserved. `streamId` is recognized (with `activity.Id` as fallback for the
  first informative activity), but `streamSequence`/`chunkType`/other "streaminfo" fallback fields
  are **not parsed or exposed** — the current trace happens to be well-ordered `channelData` deltas,
  so this gap is latent (no gap-detection, de-duplication-by-sequence, or replay-by-sequence
  guarantee exists).

### Critical functional finding (per parent)

- The final message's raw text is suppressed **merely because its `streamId` was already seen in a
  `streamedIds` set — not because the final text is verified equal to the concatenated deltas**.
  Consequence: when the final message is a genuinely different, normalized/authoritative rewrite
  (e.g., raw private-use-area citation markers replaced by `[1]`-style markers, plus respacing), that
  authoritative text is discarded, and the user-visible answer retains the earlier raw/unnormalized
  delta text instead. This affects both the streaming path and non-streaming aggregation (which also
  collects deltas and is subject to the same suppression-by-identity-not-equality logic).
- Citations are extracted from the final activity **independently** of whether its text is
  suppressed, so citation metadata (source name/url, marker position `[1]`, `abstract`-as-`Quote`)
  survives even when the normalized final text does not. However, the citation entity's `@id` and
  `appearance.text` fields are currently **lost/not read** by the
  parent's inspected conversion logic, which uses `abstract` as the rendered "Quote" instead.
  Section 3.1 documents `appearance.text` separately from `abstract`; it does not
  establish a public contract for this trace's claim-level `@id`.
- The browser-side streaming client reportedly understands an `append:false` "replacement" signal in
  its own model, but the server-side ("invoker") update pipeline currently has no corresponding
  replacement operation — i.e., the two sides are not symmetric regarding replace-vs-append handling.
- No verbatim `event:end`/`data:end` SSE sentinel is itself consumed as app-level signal; stream
  completion/lifecycle is instead driven by the upstream SDK's stream completion and the adapter's
  own A2A "final" middleware layer. (This is consistent with the external finding in §1.3 that A2A
  itself defines no such sentinel — whatever `event:end`/`data:end` observed on the wire is upstream
  of, or orthogonal to, the A2A-facing layer.)

### Source locations cited by parent (repository-relative, not reviewed in this pass)

- [Activity pipeline and progress](../src/FoundryCopilotA2A.Adapter/CopilotStudioInvoker.cs#L675)
- [Final-text suppression and stream classification](../src/FoundryCopilotA2A.Adapter/CopilotStudioInvoker.cs#L1623)
- [Provider citation conversion](../src/FoundryCopilotA2A.Adapter/CopilotStudioCitations.cs#L9)
- [A2A chat update conversion](../src/FoundryCopilotA2A.Adapter/CopilotStudioAdapterChatClient.cs#L56)
- [Citation extension](../src/FoundryCopilotA2A.Adapter/AgentCitations.cs#L31)
- [Browser stream consumption](../src/FoundryCopilotA2A.BrowserShared/a2aClient.ts#L498)
- [A2A 0.3 final-status compatibility](../src/FoundryCopilotA2A.Adapter/A2AStreamingFinal.cs#L15)

### Test verification performed by parent (reported, not re-run here)

- Build note: an initial Debug build failed with `MSB3027` due to pre-existing adapter process file
  locks (PID 41972); the app was not stopped, and the build was retried isolated under a
  `--configuration Research` build configuration.
- Test command run:
  ```
  dotnet test .\tests\FoundryCopilotA2A.Adapter.Tests\FoundryCopilotA2A.Adapter.Tests.csproj `
    --configuration Research --no-restore --verbosity minimal `
    --filter "FullyQualifiedName~StreamingResearchProbeTests|FullyQualifiedName~CopilotStudioActivitySummaryTests|FullyQualifiedName~ConversationContinuityTests|FullyQualifiedName~CitationTests|FullyQualifiedName~A2AStreamingFinalTests|FullyQualifiedName~A2AAdapterTests.Stream"
  ```
- Result: 92/92 passed (89 pre-existing tests + 3 synthetic "characterization probe" tests added
  temporarily by the parent). One probe initially failed because its fixture used an anonymous
  object (`Value`) rather than an SDK-like `JsonElement`; the fixture was corrected and all tests
  then passed.
- The three synthetic probes (per parent) characterized: (a) changed-final/raw-marker suppression
  with citation `abstract` preserved but not the full excerpt; (b) summaries/bind-update drop and
  final-plan drop; (c) the first informative activity lacking a `streamId`.
- The temporary probe source file was removed from the repository after validation; its content is
  retained only in the parent session's own files (`StreamingResearchProbeTests.cs`) as evidence —
  it was not committed, and no payload/identifiers were committed.
- Existing (pre-existing, unmodified) tests that the parent reports currently assume final-text ==
  delta-concatenation and therefore encode/lock in the suppression-by-identity behavior as "correct":
  `CopilotStudioActivitySummaryTests.StreamedAnswerIsForwardedAsDeltasWithoutRepeatingTheFinalMessage`
  and `CitationTests.LateFinalCitationsSurviveSuppressedDuplicateText`.
- Parent's own caveat: these passing characterization tests confirm the *existence* of the gap
  (current behavior matches what the tests assert), not that all event kinds/paths have equivalent
  test coverage, and not that the behavior is "correct" against any external spec (per §4 above, no
  external spec mandates either suppression-by-identity or equality-checking).
- Parent's stated recommendations (not implemented, no fixes requested or performed in this pass):
  1. Priority 1 — implement an explicit final-text **replacement** operation (not suppression-by-
     identity) across all relevant layers, with idempotency, so that an authoritative/normalized
     final message is not discarded merely because its `streamId` was seen before.
  2. Priority 2 — introduce an optional, versioned, redacted structured-event "data envelope" that
     preserves plan/step name and step-correlation metadata (tool name, step id) without blindly
     forwarding raw arguments/observations; keep "progress" (in-flight plan/step narration), "final
     plan" (post-answer plan payload), and "transport completion" (`event:end`/stream close) as three
     separate, non-conflated concerns.

---

*End of document. External research performed via a2a-protocol.org (A2A spec v1.0.0 and v0.3.0) and
Microsoft Learn (Agent Framework, Foundry Agent Service, Teams AI content labels, Bot Framework
entities/activities, `@microsoft/agents-activity` JS SDK reference). Repository findings in §5 are
attributed to the parent session and were not re-derived or verified by this research pass.*
