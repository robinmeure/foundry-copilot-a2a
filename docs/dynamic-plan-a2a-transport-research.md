# Dynamic Plan Updates over A2A — Protocol Transport Research

> Implementation update: the transport and both frontend timelines described here have now
> been implemented. The sections below preserve the pre-implementation investigation.
> See the [current contract, limits and mock example](../src/FoundryCopilotA2A.BrowserShared/README.md#structured-plan-activity-v1).

**Scope:** Primary-source protocol research plus local SDK inspection and synthetic wire
tests for carrying Copilot Studio `DynamicPlanReceived`, `DynamicPlanStepTriggered`,
`DynamicPlanStepBindUpdate`, and `DynamicPlanStepFinished` events over A2A.

**Conclusion:** Use repeated non-terminal `TaskStatusUpdateEvent` messages containing an
optional human-readable text part plus a versioned structured data part. Keep answer artifacts
separate. The installed SDK supports this through a custom keyed `IAgentHandler`; the current
automatic Agent Framework handler does not expose a status-message passthrough.
Verified on both supported wire versions and both runtime routes: **93/93 focused tests passed**.
This is research, not an implemented production feature.

See also [the original event-coverage investigation](copilot-studio-streaming-event-research.md).

All example IDs and values are synthetic. The extension URI and envelope are a proposal,
not a standard A2A or Copilot Studio schema.

---

## 1. Sources (primary, fetched directly)

| # | Source | What it is |
|---|--------|------------|
| S1 | `https://raw.githubusercontent.com/a2aproject/A2A/v1.0.0/specification/a2a.proto` | **Canonical** v1.0.0 protocol definition (protobuf). JSON Schema (`a2a.json`) and JSON-RPC/REST/gRPC bodies are all generated/derived from this file. |
| S2 | `https://raw.githubusercontent.com/a2aproject/A2A/v1.0.0/docs/specification.md` | v1.0.0 prose specification (uses `{{ proto_to_table(...) }}` macros rendered from S1 at doc-build time; still contains hand-written JSON examples and the explicit v0.3→v1.0 migration table). |
| S3 | `https://raw.githubusercontent.com/a2aproject/A2A/v0.3.0/types/src/types.ts` | **Canonical** v0.3.0 type definitions (TypeScript), referenced by `--8<--` includes in the v0.3 spec doc. |
| S4 | `https://raw.githubusercontent.com/a2aproject/A2A/v0.3.0/docs/specification.md` | v0.3.0 prose specification (includes rendered from S3) and worked SSE examples. |
| S5 | `https://raw.githubusercontent.com/a2aproject/A2A/v0.3.0/specification/json/a2a.json` | v0.3.0 generated JSON Schema (consistent with S3; not separately quoted below because S3+S4 already give exact field names). |
| S6 | `https://github.com/a2aproject/A2A` (repo root README) | SDK list, versioning pointers, general project context. |
| S7 | [v0.3.0 extension guide](https://github.com/a2aproject/A2A/blob/v0.3.0/docs/topics/extensions.md) | Extension activation, acknowledgment, required extensions and version matching. |
| S8 | [v0.3.0 streaming guide](https://github.com/a2aproject/A2A/blob/v0.3.0/docs/topics/streaming-and-async.md) | `final:true` ends the current interaction phase, including an input-required pause. |

Version note: 1.0.0 is the latest tagged release; 0.3.0 is the prior minor still linked
as "Previous Version" from the live docs site (`https://a2a-protocol.org/latest/specification/`,
checked 2026‑10‑26). The two schemas are **not** wire-compatible field-for-field — see §4.

---

## 2. Exact field/enum names (verified, not inferred)

### 2.1 Role enum

| Semantic | v0.3.0 wire value (S3 `types.ts:698-702`) | v1.0.0 wire value (S1 `a2a.proto:245-252`) |
|---|---|---|
| Client → server | `"user"` (TS union literal) | `"ROLE_USER"` (protobuf enum `ROLE_USER = 1`, JSON-mapped to its name) |
| Server → client | `"agent"` | `"ROLE_AGENT"` (`ROLE_AGENT = 2`) |

v1.0.0 field is a `Role role = 4` enum on `Message`, serialized per **standard protobuf JSON
mapping** (enum value → its symbolic name string), confirmed by every worked example in S2
(e.g. S2:1066, 1296-1299, 1391, 1410-1411 all use `"role": "ROLE_USER"` / `"ROLE_AGENT"`).

### 2.2 TaskState enum

| Semantic | v0.3.0 (S3 `types.ts:650-669`, `enum TaskState`) | v1.0.0 (S1 `a2a.proto:187-209`, `enum TaskState`) |
|---|---|---|
| Submitted | `"submitted"` | `TASK_STATE_SUBMITTED` (`=1`) |
| Working | `"working"` | `TASK_STATE_WORKING` (`=2`) |
| Input required | `"input-required"` | `TASK_STATE_INPUT_REQUIRED` (`=6`) |
| Completed (terminal) | `"completed"` | `TASK_STATE_COMPLETED` (`=3`) |
| Failed (terminal) | `"failed"` | `TASK_STATE_FAILED` (`=4`) |
| Canceled (terminal) | `"canceled"` | `TASK_STATE_CANCELED` (`=5`) |
| Rejected (terminal) | `"rejected"` | `TASK_STATE_REJECTED` (`=7`) |
| Auth required | `"auth-required"` | `TASK_STATE_AUTH_REQUIRED` (`=8`) |
| Unknown | `"unknown"` | `TASK_STATE_UNSPECIFIED` (`=0`) |

So: `ROLE_AGENT/TASK_STATE_WORKING` (v1.0.0, protobuf-JSON enum
names) vs `agent/working` (v0.3.0, lowercase string literals) are **both correct** — they are
the same semantic value in two different, non-interchangeable schema generations. There is no
"agent/working" spelling in v1.0.0's JSON mapping and no `ROLE_AGENT`/`TASK_STATE_WORKING`
spelling in v0.3.0.

### 2.3 Part / DataPart shape

v0.3.0 (S3 `types.ts:802-810`, confirmed by S4 SSE examples at `v03spec.md:1457-1463`):

```ts
export interface DataPart extends PartBase {
  readonly kind: "data";   // discriminator, REQUIRED
  data: { [key: string]: any };   // MUST be a JSON object (map), not any scalar
}
```

v1.0.0 (S1 `a2a.proto:224-243`): `Part` is a single `oneof content { text | raw | url |
google.protobuf.Value data }` plus `metadata`, `filename`, `media_type`. There is **no `kind`
discriminator field** in v1.0.0 — member presence is the discriminator. `data` is
`google.protobuf.Value`, i.e. **any** JSON value (object, array, string, number, bool, null),
not restricted to an object as in v0.3.0. S2's explicit migration note (`v1spec.md:3468-3470`)
states this directly:

```
DataPart:
  Legacy (0.3): { "kind": "data", "data": {...} }
  Current (1.0): { "data": {...}, "mediaType": "application/json" }
```

**Practical consequence for DynamicPlan payloads:** since v0.3.0 requires `data` to be a JSON
object, always wrap the plan-event value in an object even if targeting only v1.0.0 — this keeps
one payload shape valid across both schema generations and avoids an "array/scalar data" edge
case that v0.3.0 clients cannot express.

### 2.4 TaskStatusUpdateEvent — exact shape and finality semantics

v0.3.0 (S3 `types.ts:514-535`):

```ts
export interface TaskStatusUpdateEvent {
  taskId: string;
  contextId: string;
  readonly kind: "status-update";   // discriminator, REQUIRED
  status: TaskStatus;                // { state, message?, timestamp? }
  final: boolean;                    // REQUIRED, not optional
  metadata?: { [key: string]: any };
}
```

`final` is a **required** boolean on every status-update event. For our plan-progress mapping,
use `"final": false` on repeated `working` updates. `final:true` ends the current streaming
interaction, not necessarily the task permanently: S8 explicitly includes an `input-required`
pause as well as terminal task states. The completed example in S4 closes the SSE connection
after its `final:true` event. Do not confuse stream closure with permanent task termination.

v1.0.0 (S1 `a2a.proto:296-307`):

```protobuf
message TaskStatusUpdateEvent {
  string task_id = 1 [REQUIRED];
  string context_id = 2 [REQUIRED];
  TaskStatus status = 3 [REQUIRED];
  google.protobuf.Struct metadata = 4;
}
```

**There is no `final` field in v1.0.0.** Task terminality is inferred from state: per S2
(`v1spec.md:212`, `v1spec.md:2949`): *"the stream MUST begin with the Task object, followed by
zero or more `TaskStatusUpdateEvent` or `TaskArtifactUpdateEvent` objects. The stream MUST close
when the task reaches a terminal state (e.g. completed, failed, canceled, rejected)."* So in
v1.0.0 a client must check `status.state` against the terminal-state set itself rather than read
a boolean flag. Interrupted states must be handled separately from permanent task completion.
On the wire, v1.0.0 status/artifact events are also wrapped one level deeper in a
`StreamResponse` oneof (`task | message | statusUpdate | artifactUpdate`, S1:775, S2:859-871) —
there is no top-level `"kind": "status-update"` discriminator either; the oneof member name
(`statusUpdate`) is the discriminator, matching the migration note at `v1spec.md:3472-3478`.

**Implication for repeated `working` events and a non-terminal "final plan":** both schema
generations explicitly allow multiple non-terminal status updates before completion, and neither
schema treats a "last plan" flag inside custom payload data as task completion — only
`state == completed|failed|canceled|rejected` makes the task terminal. A v0.3 `final:true`
by itself only ends the current streaming phase. An
`isFinalPlan: true` marker belongs in your own payload's semantics and must **not** be confused
with, or used to set, the protocol's own terminal-state transition.

---

## 3. Example payloads (synthetic data only)

Use a single stable "envelope" key so the structured payload is self-describing regardless of
how a consumer chooses to route it (dedicated extension, ad-hoc DataPart, or generic metadata
bag). Example envelope used below — **not** an official A2A or Copilot Studio schema, just a
proposed shape:

```json
{
  "schemaVersion": "1",
  "eventId": "evt-synthetic-0001",
  "sequence": 1,
  "name": "DynamicPlanReceived",
  "value": {
    "planIdentifier": "plan-synthetic-001",
    "stepId": null,
    "isFinalPlan": false,
    "arguments": { "search_query": "synthetic example query" }
  }
}
```

### 3.1 v0.3.0 — JSON-RPC SSE stream, `message/stream` result

```
data: {
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "taskId": "task-synthetic-aaa",
    "contextId": "ctx-synthetic-bbb",
    "kind": "status-update",
    "status": {
      "state": "working",
      "timestamp": "2026-10-26T10:00:00Z",
      "message": {
        "kind": "message",
        "messageId": "msg-synthetic-0001",
        "role": "agent",
        "taskId": "task-synthetic-aaa",
        "contextId": "ctx-synthetic-bbb",
        "parts": [
          {
            "kind": "text",
            "text": "Looking up relevant records..."
          },
          {
            "kind": "data",
            "data": {
              "urn:foundry-copilot-a2a:dynamic-plan:v1": {
                "schemaVersion": "1",
                "eventId": "evt-synthetic-0001",
                "sequence": 1,
                "name": "DynamicPlanReceived",
                "value": {
                  "planIdentifier": "plan-synthetic-001",
                  "stepId": null,
                  "isFinalPlan": false,
                  "arguments": { "search_query": "synthetic example query" }
                }
              }
            }
          }
        ]
      }
    },
    "final": false
  }
}
```

A later, still-non-terminal, repeated `working` event for `DynamicPlanReceived` with
`isFinalPlan: true`. In the supplied trace this arrives after the final answer message:

```
data: {
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "taskId": "task-synthetic-aaa",
    "contextId": "ctx-synthetic-bbb",
    "kind": "status-update",
    "status": {
      "state": "working",
      "timestamp": "2026-10-26T10:00:04Z",
      "message": {
        "kind": "message",
        "messageId": "msg-synthetic-0005",
        "role": "agent",
        "parts": [
          {
            "kind": "data",
            "data": {
              "urn:foundry-copilot-a2a:dynamic-plan:v1": {
                "schemaVersion": "1",
                "eventId": "evt-synthetic-0005",
                "sequence": 5,
                "name": "DynamicPlanReceived",
                "value": {
                  "planIdentifier": "plan-synthetic-001",
                  "steps": [],
                  "isFinalPlan": true
                }
              }
            }
          }
        ]
      }
    },
    "final": false
  }
}
```

Note `"final": false` here even though the payload says `isFinalPlan: true`. Forward this even
when the answer artifact was already sent; emit the real terminal event
(`"state": "completed", "final": true`) only when the overall invocation finishes.
The SSE examples are pretty-printed for readability; actual frames must use valid SSE
`data:` framing (the tests consume compact JSON on one `data:` line per event).

### 3.2 v1.0.0 — `StreamResponse` (JSON-RPC/REST streaming or webhook body, S2:859-871)

```json
{
  "statusUpdate": {
    "taskId": "task-synthetic-aaa",
    "contextId": "ctx-synthetic-bbb",
    "status": {
      "state": "TASK_STATE_WORKING",
      "timestamp": "2026-10-26T10:00:00Z",
      "message": {
        "messageId": "msg-synthetic-0001",
        "role": "ROLE_AGENT",
        "taskId": "task-synthetic-aaa",
        "contextId": "ctx-synthetic-bbb",
        "parts": [
          { "text": "Looking up relevant records..." },
          {
            "mediaType": "application/json",
            "data": {
              "urn:foundry-copilot-a2a:dynamic-plan:v1": {
                "schemaVersion": "1",
                "eventId": "evt-synthetic-0001",
                "sequence": 1,
                "name": "DynamicPlanReceived",
                "value": {
                  "planIdentifier": "plan-synthetic-001",
                  "stepId": null,
                  "isFinalPlan": false,
                  "arguments": { "search_query": "synthetic example query" }
                }
              }
            }
          }
        ]
      }
    }
  }
}
```

No `final` field exists at this level in v1.0.0 (§2.4); the stream simply continues with more
`statusUpdate`/`artifactUpdate` frames until `status.state` is one of the terminal
`TASK_STATE_*` values (S2:212).

---

## 4. Status `message.parts[].data` vs status `metadata` vs dedicated artifact vs reused answer artifact

| Option | Spec support | Pros | Cons / risk |
|---|---|---|---|
| **A. `status.message.parts[]` with a text `Part` + a `DataPart`/`data` `Part`** (recommended) | Directly modeled: `TaskStatus.message` is a full `Message` with a `parts` array (S3 `TaskStatus`, S1 `TaskStatus.message`); `Message.parts` explicitly supports mixed Text+Data parts in the same message (S2 §4.6.2 example literally mixes a text part and structured data via `metadata`, and the core `Part` union always allows multiple parts, S4 §6.5). | One message carries both the human-readable summary and the machine payload together, in order, per status event. Matches "optional human text" requirement directly. Works unchanged across repeated `working` events — no artifact bookkeeping (`append`/`lastChunk`) needed. | Payload is transient: not part of `Task.history` retrieval guarantees beyond what the server chooses to retain (`historyLength` semantics, S2 §3.2.4), and not independently addressable by `artifactId` later. |
| **B. `TaskStatusUpdateEvent.metadata` (or `Message.metadata`) keyed by extension/schema URI** | Explicitly the documented extension-data location: S2 §4.6.2 examples put extension payloads in `message.metadata[extensionUri]`, and both schemas define `metadata` as `Record<string, any>` / `google.protobuf.Struct` on `Message`, `TaskStatusUpdateEvent`, `Artifact`, `Task`. | Idiomatic for the formal extension mechanism (see §5) and keeps `parts` reserved for literally renderable content. Good fit if you register a real `AgentExtension`. | `metadata` is explicitly documented as free-form "additional context/parameters," not primary content — generic UIs are even less likely to surface it than a `DataPart`, since parts are the documented rendering surface (S4 §6.5: *"represents a distinct piece of content ... that **MUST** be one of TextPart/FilePart/DataPart"*) while metadata is explicitly auxiliary. |
| **C. Dedicated `TaskArtifactUpdateEvent` per plan event (separate `artifactId` from the final answer)** | Fully supported: `Artifact` has its own `artifactId`, `parts`, `metadata`, `extensions` (S1:280-294, S3 `Artifact`), with `append`/`lastChunk` (v0.3)/`last_chunk` (v1) controlling incremental assembly. | Gives each plan event (or the whole plan stream) a stable, independently retrievable identity in `Task.artifacts`, decoupled from the eventual answer. Good if plan events must be queryable after the fact via `tasks/get`. | Heavier: needs its own `artifactId` lifecycle and `append`/`last_chunk` bookkeeping; mixes "status" semantics (ephemeral progress) with "artifact" semantics (durable output) — spec frames artifacts as *task outputs* (S1 `a2a.proto:276`: *"Artifacts represent task outputs"*), which is a conceptual mismatch for transient plan-progress telemetry. |
| **D. Append plan events onto the same artifact used for the final answer** | Legal mixed-part content, but consumers need an application-specific contract to distinguish plan telemetry from the answer. | Fits the current automatic hosting path without replacing its handler. | Couples progress to answer-artifact buffering, assembly and rendering. Existing consumers would need changes anyway; changing message IDs can split answer artifacts. **Not recommended for transient progress.** |

**Recommendation:** Option **A** (status `message.parts`, text + data) for live plan telemetry,
with Option **C** reserved only if plan history must survive as a durable, independently
fetchable task artifact after the task completes. Do not use Option D.

---

## 5. AgentCard extension declaration/activation — 0.3 vs 1.0

### 5.1 Declaration (`AgentExtension`) — stable across versions

v0.3.0 (S3 `types.ts:43-57`):
```ts
export interface AgentExtension {
  uri: string;
  description?: string;
  required?: boolean;
  params?: { [key: string]: any };
}
```
v1.0.0 (S1 `a2a.proto:418-429`) — identical field set, now proto-typed:
```protobuf
message AgentExtension {
  string uri = 1;
  string description = 2;
  bool required = 3;
  google.protobuf.Struct params = 4;
}
```
Both are declared inside `AgentCard.capabilities.extensions[]` (S1:412, S3 `AgentCapabilities`).
`required` defaults to **false** in both (proto3 scalar default / TS optional field absent ⇒
falsy), meaning: unless explicitly set `true`, an extension is advisory/opt-in, not mandatory to
understand.

### 5.2 Activation / opt-in mechanism — version-specific headers

Activation exists in **both** versions. The v0.3 rules are in the dedicated extension guide
(S7), not just the main specification.

| Version | HTTP activation request |
|---|---|
| v0.3.0 | `X-A2A-Extensions: urn:foundry-copilot-a2a:dynamic-plan:v1` |
| v1.0.0 | `A2A-Extensions: urn:foundry-copilot-a2a:dynamic-plan:v1` |

- S7 says extensions should default to inactive. Clients request activation with
  `X-A2A-Extensions`; the response should identify activated extensions in the same header.
  Unsupported requests can be ignored. Required extensions that the client does not request
  should cause rejection. For an unsupported extension version, the agent SHOULD ignore the
  activation request and MUST NOT silently fall back to a different version.
- S2 section 3.2.6 uses `A2A-Extensions` as a standard service parameter: a comma-separated
  list of requested extension URIs, transmitted as an HTTP header for HTTP bindings.
  Section 4.6.3 says an unsupported extension version SHOULD be ignored unless required,
  in which case the agent MUST return an error.
- Therefore v1 did **not** introduce extension activation from nothing. Use the appropriate
  header for the negotiated protocol version and explicitly implement/test the handshake
  rather than assuming card advertisement alone activates the extension.

Recommended project behavior: ordinary clients still receive text status messages and answer
artifacts. Include the structured plan extension only when activated. `required:false` preserves
baseline interoperability; it is not permission to assume every client understands plan data.

### 5.3 Consumers ignoring optional/unknown data

A general forward-compatibility clause exists only in the fetched v1.0.0 text: *"Implementations
**SHOULD** ignore unrecognized fields in messages, allowing for forward compatibility as the
protocol evolves"* (S2:1277). No equivalent normative sentence was found in the fetched v0.3.0
spec text. Note the scope of this clause: it is about unrecognized **protocol fields** (schema
evolution), not a guarantee that a UI/consumer will silently skip rendering an unrecognized
`DataPart`/extension payload it doesn't understand — that is an application-level contract you
must build yourself (e.g., always pair structured data with a human-readable text `Part` so
plan-unaware consumers show something sane, which Option A in §4 already does).

### 5.4 URI / envelope / schema-versioning recommendation

- Use one stable, versioned URI as both (a) the `AgentExtension.uri` if formally registered, and
  (b) the top-level key inside the `DataPart`/`data` payload (as in §3's
  `urn:foundry-copilot-a2a:dynamic-plan:v1` example) — this lets a consumer that only inspects
  `message.metadata`/`parts[].data` keys recognize the payload even without doing extension
  negotiation.
- Per S2 §4.6.3: *"Extensions **SHOULD** include version information in their URI identifier
  ... A new URI **MUST** be created for breaking changes."* i.e. version the URI itself
  (`:v1`, `:v2`), don't rely solely on an internal `schemaVersion` field for breaking changes.
- Declare the extension with `required: false` in the `AgentCard`. Keep text-only behavior
  available to clients that do not activate it using their version's extension header.
  The examples in section 3 illustrate the activated representation; the synthetic tests
  prove transport serialization, not production negotiation.

---

## 6. Recommended mapping matrix

All events are carried as `status.message` on a `TaskStatusUpdateEvent` with
`status.state = working` (0.3) / `TASK_STATE_WORKING` (1.0), using Option A from §4. None of
these provider events should set `final:true` or terminate the stream themselves. Handle
overall task completion and any input/auth interruption separately, per section 2.4.

| DynamicPlan* event | `status.state` | repeats allowed | envelope `name` | envelope `value` (synthetic shape) | human text `Part` | ends task? |
|---|---|---|---|---|---|---|
| `DynamicPlanReceived` | working | yes | `"DynamicPlanReceived"` | `{ planIdentifier, steps, isFinalPlan, toolDefinitions?, toolKinds? }` | optional, e.g. "Planning next steps..." | no, including `isFinalPlan: true` after the answer |
| `DynamicPlanStepTriggered` | working | yes | `"DynamicPlanStepTriggered"` | `{ planIdentifier, stepId, taskDialogId, state, type? }` | optional, e.g. "Running step: search" | no |
| `DynamicPlanStepBindUpdate` | working | yes | `"DynamicPlanStepBindUpdate"` | `{ planIdentifier, stepId, taskDialogId, arguments?, autoFilledArguments? }` | optional/omit | no |
| `DynamicPlanStepFinished` | working | yes | `"DynamicPlanStepFinished"` | `{ planIdentifier, stepId, taskDialogId, state, observation?, ... }` | optional, e.g. "Step complete" | no; a completed step is not a completed A2A task |

These provider fields come from the supplied trace, not from the A2A schema. Preserve the
original full event name and provider state inside the envelope. Do not infer a stronger
provider contract (such as "no more events can ever arrive") from `isFinalPlan`. A2A task
completion is separate, and an artifact's `lastChunk` finishes that artifact, not the task.
Fields containing arguments, observations or thought text require the policy described below.

---

## 7. Open items / things intentionally not verified here

- Whether any specific host (Foundry runtime, Copilot Studio canvas, generic A2A client/UI)
  renders arbitrary structured `status.message` data out of the box: **not found in the fetched
  protocol-level sources** (the A2A spec and repo README only define the wire contract, not
  renderer behavior). Treat any such rendering as **unverified/unknown** and design the human
  text `Part` such that the UX is acceptable even if the structured `data` is never surfaced.
- Versioned extension negotiation has not been exercised against the installed SDK's endpoints
  in this research; the synthetic handler intentionally tests structured transport only.
- Repeated-artifact `append`/`last_chunk` edge cases (option D in §4) were intentionally not
  further chased here since Option A avoids them; this document does not re-litigate or overclaim
  specific "append=false" undefined-behavior claims from prior research.
- Native Foundry tool execution was not tested. Protocol compatibility does not establish
  that a model-mediated tool chain relays every intermediate status or renders the extension.

---

## 8. Repository feasibility (locally verified)

Evidence: installed package XML documentation and decompiled implementations from
`A2A` 1.0.0-preview2 and `Microsoft.Agents.AI.Hosting.A2A` 1.19.0-preview.260822.1, plus
synthetic HTTP tests using this repository's `A2AAdapterFactory`. No SDK upgrade is needed
for the demonstrated transport.

**Default handler limitation:** `A2AAgentHandler.StreamTaskUpdatesAsync` calls `Submit` then a
single `StartWork(null)`, funnels every `AgentResponseUpdate` through an internal
`ArtifactStreamWriter`, then completes. There is no status/progress hook in the registration
options (only `AgentRunMode`/`ServerOptions`). `ArtifactStreamWriter` buffers one update to decide
`lastChunk`, and groups contiguous `MessageId` values into one artifact (`append`); changing
`MessageId` mid-stream creates a new artifact rather than giving reliable independent control, so
feeding plan data through `IChatClient` content alone cannot emit true status events or keep plan
telemetry isolated from answer artifacts.

**Working seam:** `AddA2AServer`'s `CreateA2AServer` resolves a **keyed** `IAgentHandler` by
`agent.Name` before falling back to the default `A2AAgentHandler`. Registering a custom keyed
handler (`services.AddKeyedSingleton<IAgentHandler, PlanHandler>(AdapterConstants.AgentName)`) is
confirmed to work unmodified against existing router + per-agent routes and the existing
`MapA2AWithV03Compat` endpoints. A custom handler can construct its own `TaskUpdater(queue,
context.TaskId, context.ContextId)`, call `SubmitAsync`, then repeatedly call `StartWorkAsync` with
an agent `Message` containing a text `Part.FromText(...)` plus a data `Part.FromData(...)` carrying
the plan envelope, followed by `AddArtifactAsync` (answer, `lastChunk: true`) and `CompleteAsync`.
**Caveat:** `AddA2AServer` still wraps the task store in `IsolationKeyScopedTaskStore` when a
custom handler is registered. However, the default `AIHostAgent` and
`IsolationKeyScopedAgentSessionStore` construction is bypassed. A production handler must
preserve session/context isolation, request metadata, cancellation, error mapping, task
lifecycle and the existing idempotency behavior; the registration alone is not a complete feature.

**Validation performed:** 93/93 tests passed (89 pre-existing + 4 new synthetic wire-transport
tests) via `dotnet test
.\tests\FoundryCopilotA2A.Adapter.Tests\FoundryCopilotA2A.Adapter.Tests.csproj --configuration
Research --no-restore --filter
"FullyQualifiedName~DynamicPlanTransportProbeTests|FullyQualifiedName~CopilotStudioActivitySummaryTests|FullyQualifiedName~ConversationContinuityTests|FullyQualifiedName~CitationTests|FullyQualifiedName~A2AStreamingFinalTests|FullyQualifiedName~A2AAdapterTests.Stream"
--verbosity minimal`. Assertions covered: exact five-plan-event name/sequence order and nested
`value.arguments.search_query`, per-event `eventId`/`sequence`, `planIdentifier`/`stepId`,
`status.message` matching the event's `taskId`/`contextId`, a single answer artifact with exact
text and `lastChunk: true`, a `DynamicPlanReceived`/`isFinalPlan: true` event emitted **after** the answer
artifact but **before** task completion (confirming §6's "does not end the task" rule in a live
probe), v0.3 `final:false` on working events and `final:true` only at terminal `completed`, and
v1.0.0 `ROLE_AGENT`/`TASK_STATE_WORKING` with `data` as a direct object (no extra nesting level).
The temporary probe test file (`DynamicPlanTransportProbeTests.cs`) was moved to session storage
and removed from the repository; no production code was changed.

**Not yet covered (per parent):** multi-client extension negotiation end-to-end, cancellation and
failure-path replay for plan events, non-streaming (`message/send`) non-pollution, payload
size/privacy validation, and the adapter-layer surfaces that currently drop structured plan data
(`CopilotInvocationUpdate` has no typed plan-event slot yet; `A2AStreamAccumulator.ApplyStatus`
ignores non-terminal `working` status data; `BrowserShared/taskStatus.ts` reads only
`status.message` text, not `data`). These are implementation/integration gaps for follow-up work,
not protocol-contract questions.

## 9. Implementation surfaces and data policy

1. **Capture before text projection:** add a typed plan-event slot to
   [`CopilotInvocationUpdate`](../src/FoundryCopilotA2A.Adapter/Contracts/CopilotInvocation.cs).
   Capture every supported event independently of thought/summary extraction, text deduplication
   and the "answer started" suppression. Retain `DynamicPlanReceived` after the answer.
2. **Retain ordered events without answer pollution:** update the streaming content conversion
   and idempotency production in
   [`CopilotStudioAdapterChatClient.cs`](../src/FoundryCopilotA2A.Adapter/CopilotStudioAdapterChatClient.cs).
   Event-only updates must not be dropped or appended to the final answer string. Preserve
   stable event IDs and sequence across replay. Define bounded retention/snapshots explicitly;
   protocol status updates alone do not promise a durable full plan-event history.
3. **Route to status rather than artifacts:** register the custom keyed handler in
   [`Program.cs`](../src/FoundryCopilotA2A.Adapter/Program.cs). Recognize typed plan content,
   emit `StartWorkAsync(message)` immediately, and keep normal answer/citation/card artifacts
   unchanged. Preserve non-streaming behavior, sessions, error and cancellation semantics.
   Do not disguise already-executed Copilot Studio steps as new tool calls for the model.
4. **Advertise/negotiate consistently:** cover router and per-agent cards in both protocol
   versions. Browser CORS currently allows neither extension request header; explicitly allow
   negotiation headers and expose acknowledgment headers. Capture negotiation before background
   execution loses ambient HTTP context. Project the cached event stream for each caller's
   negotiated capabilities rather than replaying a representation selected by a prior caller.
5. **Consume separately from the answer:**
   [`taskStatus.ts`](../src/FoundryCopilotA2A.BrowserShared/taskStatus.ts) currently extracts
   status text but not data;
   [`a2aClient.ts`](../src/FoundryCopilotA2A.BrowserShared/a2aClient.ts) already calls
   `onTaskStatus`. Add a typed parser/callback and plan state keyed by originating agent,
   plan ID and step ID, with event-ID deduplication. Do not send plan text/data into answer,
   citation or card accumulators. Text-only clients may display summaries, but rendering is
   consumer-specific.
6. **Preserve controlled relays:**
   [`A2AStreamAccumulator.ApplyStatus`](../src/FoundryCopilotA2A.Adapter/ApiManagementA2AInvoker.cs)
   currently discards working statuses; it handles failures and completed-status fallback
   answers only. Add explicit typed progress forwarding for adapter-managed chains. This
   does not prove that a native Foundry tool/model-mediated chain preserves these events.

**Default payload policy:** use an allowlist for event ID/name, correlation IDs, sequence,
provider step state and safe display information. Do not forward the full activity envelope
(`from`, `recipient`, user identity or arbitrary channel data). Arguments and observations can
contain sensitive prompts, retrieved documents and large nested results; omit/redact by default
and make fuller payloads an explicit authorized policy. Do not expose provider thought text
by default. Record omitted/truncated fields explicitly, enforce payload limits and surface
malformed payload errors through the repository's standard diagnostics. Exact raw fidelity
and safe normalized progress are different contracts; choose and document the policy.

**Additional acceptance tests before implementation is complete:** negotiated/unnegotiated
clients; data-only and repeated events; post-answer final plans; stable idempotent replay;
unknown/invalid event payloads; payload size/redaction; cancellation and failures; answer,
citations and cards unchanged; non-streaming responses unpolluted; ordered browser/relay
consumption; status delivery before the next answer chunk rather than artifact buffering.
