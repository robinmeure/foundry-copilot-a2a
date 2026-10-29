# Adaptive Cards over A2A: reviewed feasibility research

Last verified: 2026-10-23

Repository scope update: the diagnostic flow-builder application has been removed.
Browser-count and UI findings below describe the research baseline, not the current
repository. Orchestrator Chat now uses the shared read-only renderer in
[ChatShared](../src/FoundryCopilotA2A.ChatShared/README.md); Knowledge Chat uses a separate
Knowledge API protocol. Native A2A delegation remains in scope.

Scope: primary-source protocol/platform research plus inspection and focused
baseline tests of this repository. No application code or live provider was changed.
See the separate [implementation plan](adaptive-cards-a2a-implementation-plan.md)
for the agreed delivery stages and verification gates.
Pinned versions referenced throughout: `A2A.V0_3Compat 1.0.0-preview2`,
`Microsoft.Agents.AI 1.19.0`, `Microsoft.Agents.AI.Hosting`/`.Hosting.A2A.AspNetCore
1.19.0-preview.260822.1`, `Microsoft.Agents.CopilotStudio.Client 1.8.77`, .NET 10,
two React 19 browser clients with no Adaptive Cards rendering dependency.

A companion section, **"Repository findings (parent-agent inspection)"**, reports
this repository's current code behavior as supplied by a parent agent that
inspected the code directly; it is included verbatim-in-substance for a single
combined reference and is not independently re-verified by this document's author.

## Reviewed conclusion

**Feasible for this adapter and its own browser clients, with explicit limits.**
Use an optional project-owned JSON envelope in ordinary A2A data parts, retain a
deterministic text fallback, and add a shared, display-only renderer first.
The existing citation path already proves structured JSON survives the installed
1.0 and 0.3 endpoint stack; no SDK upgrade, new MIME type, or parallel transport
is required for that path.

Interactive `Action.Submit` is a subsequent, gated capability, not a consequence
of rendering a card. It requires a live same-conversation Copilot Studio SDK
probe and action-specific validation, authorization, replay protection, and
failure handling. `Action.Execute`, refresh, and automatic authentication are
outside the initial scope.

**Native Foundry/Copilot Studio orchestrator preservation and rendering remain
unverified.** Transparent gateway forwarding, direct adapter invocation, and a
model-mediated native chain are different compatibility claims.

### Independent Opus 5.5 second opinion

On 2026-10-23, an independent agent running **`claude-opus-5.5`** reviewed the
completed research and selected primary sources/local package metadata.
Its verdict was **CONFIRMED WITH CORRECTIONS** and it explicitly agreed with
the staged scope above once the corrections were made. The lead agent accepts
that scope; the corrections are incorporated into this revision:

1. Distinguished the standalone legacy content converter from the actual
   1.0-to-0.3 compatibility endpoint; data parts already work.
2. Distinguished `Action.Execute` schema introduction in 1.4 from Teams' 1.5 guidance.
3. Removed the incorrect claim that the repository's string `AskQuestionAsync`
   overload is obsolete in the pinned SDK.
4. Separated agent-side invoke handlers from client-to-Copilot Studio support.
5. Narrowed the extension search's negative result to the sources actually searched.
6. Corrected the version-specific extension headers.

Both assessments confirm **engineering feasibility of the bounded scope**, not
completed implementation or live interoperability. The review did not approve
or test an implementation that does not yet exist.

## How to read this document

Three axes are kept separate throughout, per the research brief:

1. **Transport** - what the A2A wire protocol (1.0 and 0.3) can carry in a
   `Message`/`Part`/`Artifact`, and what the Agent Card capability/extension
   negotiation mechanism looks like.
2. **Renderer** - what Adaptive Cards schema/host support (Teams, Outlook,
   Copilot Studio, a generic custom browser renderer) exists once a card
   payload has arrived somewhere.
3. **Action round-trip** - what happens when a user taps a button on a
   rendered card (`Action.Submit` vs `Action.Execute`/`adaptiveCard/action`
   invoke, and how that correlates back to a conversation/task).

Each claim below is tagged with a confidence level:
- **Verified (primary source)** - read directly from an official spec, Microsoft
  Learn doc, or first-party SDK source at the cited URL/path on 2026-10-23.
- **Verified (version-pinned source)** - read directly from SDK source at the
  exact pinned package version/git tag.
- **Inferred** - a reasonable conclusion from verified facts, explicitly marked.
- **Unverified / absence of documentation** - explicitly not found; absence is
  reported as uncertainty, not as proof of impossibility, per the research brief.

---
## 1. A2A wire shape: no native rich-card contract, in either 1.0 or 0.3

**Verified (primary source).** Both the A2A 1.0.0 and 0.3.0 official specifications
state modality-agnostic transport as a *guiding principle*, phrased identically
in both versions:

> "Modality Agnostic: Support exchange of diverse content types including text,
> audio/video (via file references), structured data/forms, and potentially
> embedded UI components (e.g., iframes referenced in parts)."

Source: <https://a2a-protocol.org/v1.0.0/specification/> and
<https://a2a-protocol.org/v0.3.0/specification/> (Section 1.2, "Guiding Principles"),
retrieved 2026-10-23.

This is the *only* place either spec mentions "UI components," and it frames
them as something referenced (e.g., an iframe URL carried in a file-like part),
not as a defined schema. **No Part variant, Artifact field, or MIME-type registry
entry named for Adaptive Cards (or cards in general) exists in either version.**

### 1.0 wire shape (`Part`)

**Verified (primary source).** A2A 1.0.0 redesigned `Part` as a single message
type with a `oneof` of exactly one of: `text` (string), `raw` (bytes, base64 in
JSON), `url` (string), or `data` (arbitrary JSON value), plus optional
`metadata`, `filename`, and `mediaType` (MIME type) fields.
Source: <https://a2a-protocol.org/v1.0.0/specification/> (Part/Message data
types section), retrieved 2026-10-23.

A card payload can be carried as JSON in `Part.data`. This research recommends
a project-owned, versioned key inside that JSON object so the same envelope
survives both protocol versions. Keep the provider attachment's
`application/vnd.microsoft.card.adaptive` content type inside the envelope;
do not invent a `+json` type or depend on a 1.0-only part media-type field.
File-shaped parts are possible transport alternatives, but unnecessary here.

### 0.3 wire shape (`Part`/`DataPart`/`FilePart`)

**Verified (primary source).** A2A 0.3.0 used a discriminated union
`Part = TextPart | FilePart | DataPart`, each tagged with a `kind` literal.
`FilePart.file` is `FileWithBytes | FileWithUri` (mutually exclusive
`bytes`/`uri`, with `name`/`mimeType` from the `FileBase` base type).
`DataPart.data` is an arbitrary JSON object (`google.protobuf.Struct` in the
gRPC binding). **Unlike 1.0's `Part.data`, 0.3's `DataPart` has no `mimeType`
field of its own** - only `FilePart` carries a MIME type.
Source: <https://a2a-protocol.org/v0.3.0/specification/> (Part/TextPart/FilePart/
DataPart data types section), retrieved 2026-10-23.

### Agent Card capability/extension negotiation - schema identical across versions

**Verified (primary source).** `AgentCard.capabilities` is an
`AgentCapabilities` object: `{ streaming?, pushNotifications?,
stateTransitionHistory?, extensions?: AgentExtension[] }`. The `AgentExtension`
schema - `{ uri, description?, required?, params? }` - is **structurally
identical between 0.3.0 and 1.0.0**. Separately, `Message`/`Artifact` objects
each carry their own `extensions?: string[]` field (a list of extension URIs
that were used in/contributed to that specific message/artifact - a
usage-flagging mechanism, distinct from the AgentCard's capability-declaration
mechanism).
Source: <https://a2a-protocol.org/v1.0.0/specification/> and
<https://a2a-protocol.org/v0.3.0/specification/> (AgentCard/AgentCapabilities/
AgentExtension sections), retrieved 2026-10-23.

**Design implication (verified from spec, not a recommendation):** because the
`AgentExtension` schema is unchanged between versions, an "0.3 Agent Card
converter that drops extensions" (see Section 5 below) is necessarily an
**implementation-level bug in a specific SDK's conversion code**, not a
protocol-level incompatibility between 0.3 and 1.0 extension declarations.

### No Adaptive Card extension found in the searched registry and organization

**Verified (primary source), negative result.** The canonical A2A extension
governance page states: "Official extensions use the
`https://a2a-protocol.org/extensions/` URI prefix and are hosted under the
`a2aproject` organization with the `ext-` repository prefix (experimental
extensions use `experimental-ext-`)." The same page's "List of Example
Extensions" table lists exactly four entries: **Secure Passport**, **Hello
World / Timestamp**, **Traceability**, and **Agent Gateway Protocol (AGP)**.
None concern cards, rich UI, or Adaptive Cards.
Source: <https://a2a-protocol.org/latest/topics/extensions/> ("Extension
Governance" and "List of Example Extensions" sections), retrieved 2026-10-23.

This was cross-checked directly against the `a2aproject/a2a-samples` GitHub
repository's `extensions/` directory, which contains exactly four
subdirectories: `agp`, `secure-passport`, `timestamp`, `traceability` -
matching the table above, confirmed via the GitHub Contents API on 2026-10-23
(`https://api.github.com/repos/a2aproject/a2a-samples/contents/extensions`).
A listing of all public repositories under the `a2aproject` GitHub organization
(`https://api.github.com/orgs/a2aproject/repos`, retrieved 2026-10-23) likewise
contains no `ext-adaptive-card`/`ext-card`-style repository.

**Conclusion: no existing Adaptive Card-specific extension was identified in
these sources.** Search scope limitation: this checked the
A2A org's own governance-listed registry and its `a2aproject` GitHub
organization; it did not exhaustively scan all public GitHub repositories for
unofficial/unlisted third-party extensions, so "none found" reflects that
scope, not an absolute universal negative.

### Task states - `input-required`/`auth-required` are non-terminal

**Verified (primary source).** A2A task states include `TASK_STATE_INPUT_REQUIRED`
("agent requires additional user input to proceed... interrupted state") and
`TASK_STATE_AUTH_REQUIRED` ("authentication is required to proceed...
interrupted state") - both are explicitly non-terminal/interrupted states,
distinct from terminal states (`COMPLETED`, `FAILED`, `CANCELED`, `REJECTED`).
Multi-turn continuation re-uses the same `taskId`/`contextId` in a subsequent
message. Source: <https://a2a-protocol.org/v1.0.0/specification/> (TaskState
enum / task lifecycle sections), retrieved 2026-10-23.

### Extension/version negotiation headers

**Verified by the independent review against the versioned sources:**

- A2A 1.0 uses `A2A-Extensions` and `A2A-Version`; see
  <https://a2a-protocol.org/v1.0.0/specification/> section 14.2.
- A2A 0.3 uses `X-A2A-Extensions`; activated extensions SHOULD be echoed
  in responses. See <https://a2a-protocol.org/v0.3.0/topics/extensions/>.

Advertising an extension, activating it, and reporting its use are distinct.
`required: false` means unsupported clients are not required to implement it;
it does not automatically implement negotiation or a renderer. A project
contract may define passive enrichment with no activation requirement, as the
existing citations do. If activation is required, explicitly handle both
version-specific headers, response acknowledgement, CORS, and gateway forwarding.

---
## 2. Adaptive Cards action round-trip: `Action.Submit` vs `Action.Execute`

**Verified (primary source).** Prior to the Universal Action Model, hosts had
inconsistent action models: "Teams or bots used `Action.Submit`, an approach
which defers the actual communication model to the underlying channel" (i.e.
the host chooses how submitted input is delivered), while "Outlook used
`Action.Http`." Universal Actions introduce `Action.Execute`, which "works
across all hubs, including Teams and Outlook," uses the bot as the common
backend, and allows "an Adaptive Card [to] be returned as a response for an
`Action.Execute` triggered invoke request."
Source: <https://learn.microsoft.com/en-us/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/overview>,
retrieved 2026-10-23.

**Verified (primary source) - schema versus host requirement.**
`Action.Execute` and the Universal Action Model were introduced in schema
**1.4**, according to the Adaptive Cards
[Universal Action Model](https://learn.microsoft.com/en-us/adaptive-cards/authoring-cards/universal-action-model)
and [renderer specification](https://learn.microsoft.com/adaptive-cards/rendering-cards/implement-a-renderer#actions).
Separately, the **Teams** guide recommends setting the card version to **1.5**.
That guide warns that if a Teams card sets `version` below 1.5 but still uses
`refresh`/`Action.Execute`: in Teams, "your card stops working. Card isn't
refreshed and `Action.Execute` doesn't render depending on the version of the
Teams client. To ensure maximum compatibility in Teams, define `Action.Execute`
with an `Action.Submit` in the fallback property."
Source: <https://learn.microsoft.com/en-us/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/work-with-universal-actions-for-adaptive-cards>,
retrieved 2026-10-23.

These are different scopes: Teams guidance does not redefine the schema
introduction version or establish the supported version of this custom browser
host. The host must declare and test its supported version and feature subset.
`Action.Submit` is host-defined; a `message` Activity with `Value` is a
candidate Copilot Studio mapping to verify, not a universal transport rule.

**Verified (primary source) - invoke shape.** `Action.Execute` triggers an
Invoke activity named `adaptiveCard/action`; "Whenever your bot returns a new
card as a result of processing an `Action.Execute`, the response must conform
to the response format." The bot handles this invoke and can respond
synchronously with an updated card (enabling "Sequential Workflow"/"User
Specific Views" via the card's `refresh` property, scoped by a `userIds` MRI
array).
Source: same Learn page as above, retrieved 2026-10-23.

**Implication for A2A specifically:** `adaptiveCard/action` is a **Bot
Framework/Teams channel-level Invoke activity concept**, not an A2A protocol
concept. Use an ordinary A2A send-message operation with a defined structured
action payload, not an invented A2A invoke RPC. A genuinely interrupted A2A
task may continue with its task/context identifiers. Alternatively, complete
each presentation turn and submit a new task in the same context/provider
conversation. Never reopen an already completed task or infer `input-required`
just because a card contains an input.

---

## 3. Microsoft.Agents SDK surface at the pinned version (1.8.77)

**Verified (version-pinned source).** At `Microsoft.Agents.Builder` **1.8.77**
(the pinned `Microsoft.Agents.CopilotStudio.Client` dependency generation),
`Microsoft.Agents.Builder.Compat.ActivityHandler` exposes:

```csharp
protected virtual Task<AdaptiveCardInvokeResponse> OnAdaptiveCardInvokeAsync(
    ITurnContext<IInvokeActivity> turnContext,
    AdaptiveCardInvokeValue invokeValue,
    CancellationToken cancellationToken)
```

dispatched when an inbound Invoke activity's `Name == "adaptiveCard/action"`.
This is **agent-side hosting plumbing**, not evidence that
`Microsoft.Agents.CopilotStudio.Client` can send an action to a Copilot Studio
conversation or receive a replacement card. Its presence is not a reason to add
the Builder package to this adapter. Source: [Microsoft.Agents.Builder
1.8.77](https://www.nuget.org/packages/Microsoft.Agents.Builder/1.8.77)
reference surface, with that distinction confirmed by the independent review.

**Verified (version-pinned source).** `Microsoft.Agents.Core.Models.Activity`
(package `Microsoft.Agents.Core` 1.8.77) exposes an `Attachments` property
(Bot-Framework-Activity-style attachment list) - confirming the underlying
Activity model at this SDK generation structurally supports attachments,
independent of whether this repository's own code currently surfaces them for
general Adaptive Card payloads (see Repository findings, Section 8 below, which
states it currently does not for anything other than a named
connector/`connectionManagerCard`).

**Verified against the locally installed 1.8.77 assembly by the Opus reviewer:**
only `CopilotClient.AskQuestionAsync(IActivity, CancellationToken)` has the
obsolete attribute, directing callers to `SendActivityAsync`.
The string/conversation-ID overload used by this repository is **not obsolete**;
neither are the inspected `ICopilotClient` methods. No general client migration
is required for this feature.

The existing file-input path already sends an Activity through `ExecuteAsync`.
That is useful prior art for a card-submit probe, but a successful file send does
not prove `Activity.Value`, action correlation, or `adaptiveCard/action` handling.
Those remain live verification gates for the pinned
[client package](https://www.nuget.org/packages/Microsoft.Agents.CopilotStudio.Client/1.8.77).

---
## 4. Protocol `DataPart` support vs. actual Agent Framework `AIContent`/`ChatResponseUpdate` bridge preservation

This section directly answers the explicit instruction to distinguish
*protocol-level* `DataPart`/`Part.data` support (Section 1, confirmed to exist as
generic JSON transport) from what the **Microsoft Agent Framework's A2A
bridge actually does** with that data once it reaches `AIContent`/
`ChatResponseUpdate` - these are two different, independently verifiable
layers, and they do **not** behave identically.

### 4.1 Where the bridge code lives (verified package/source provenance)

- `A2A` / `A2A.AspNetCore` / `A2A.V0_3` / `A2A.V0_3Compat` are published by the
  **`a2aproject/a2a-dotnet`** open-source A2A .NET SDK (Apache-2.0), a
  separate project from Microsoft's own packages, implementing the full A2A
  wire protocol plus a 0.3-compatibility shim.
- `Microsoft.Agents.AI.Hosting.A2A` / `.AspNetCore` are published by
  **`microsoft/agent-framework`** ("Microsoft Agent Framework"), which
  *depends on* the `a2a-dotnet` packages above.
- **Verified version-pinned alignment:** the published `.nuspec` for the exact
  pinned package `Microsoft.Agents.AI.Hosting.A2A.AspNetCore
  1.19.0-preview.260822.1` declares a dependency on `A2A` and `A2A.AspNetCore`
  at version **`1.0.0-preview2`** - i.e. the *exact same release* as the
  separately pinned `A2A.V0_3Compat 1.0.0-preview2`. This resolves an earlier
  concern (raised mid-session, before checking the pinned `.nuspec` directly)
  that current/latest NuGet metadata shows a newer dependency floor
  (`1.26.0-preview...`); that newer floor applies only to packages published
  *after* 1.19.0-preview.260822.1, and is irrelevant to the pinned version
  actually used here. Source:
  `https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai.hosting.a2a.aspnetcore/1.19.0-preview.260822.1/microsoft.agents.ai.hosting.a2a.aspnetcore.nuspec`,
  retrieved 2026-10-23.
- The conversion logic itself (`AIContent` <-> A2A `Part`) lives in
  **`a2a-dotnet`**, not in `agent-framework`: file
  `src/A2A/Extensions/AIContentExtensions.cs` (1.0 wire shape) and
  `src/A2A.V0_3/Extensions/AIContentExtensions.cs` (0.3 wire shape). Both were
  fetched **at the exact pinned git tag `v1.0.0-preview2`**
  (`https://raw.githubusercontent.com/a2aproject/a2a-dotnet/v1.0.0-preview2/...`),
  confirmed identical in substance to the current `main` branch for this
  specific conversion logic, retrieved 2026-10-23.
- `microsoft/agent-framework`'s own `Microsoft.Agents.AI.A2A` layer
  (`A2AAgent.cs`, `Extensions/A2AAIContentExtensions.cs`,
  `Extensions/A2AArtifactExtensions.cs`, `Extensions/ChatMessageExtensions.cs`)
  is a thin wrapper that calls into the above `a2a-dotnet` extension methods
  (`part.ToAIContent()`, `content.ToPart()`) - it does not re-implement or
  override the content-type mapping. Source: files fetched from
  `https://raw.githubusercontent.com/microsoft/agent-framework/main/dotnet/src/Microsoft.Agents.AI.A2A/...`,
  retrieved 2026-10-23.

### 4.2 What the bridge actually preserves - 1.0 direction

**Verified (version-pinned source), `src/A2A/Extensions/AIContentExtensions.cs`
at tag `v1.0.0-preview2`:**

- **Inbound (`Part.ToAIContent()`):** `part.Text` -> `TextContent`; `part.Url`
  -> `UriContent`; `part.Raw` (bytes) -> `DataContent` (preserving
  `part.MediaType` or defaulting to `application/octet-stream`); **`part.Data`
  (arbitrary JSON) -> `DataContent`**, serialized back to UTF-8 JSON bytes, with
  media type set to `part.MediaType` *if and only if* `IsJsonMediaType(mediaType)`
  is true (i.e. the media type is `application/json` or ends in `+json`),
  otherwise hardcoded to `application/json`. **This means a `DataPart`/`Part`
  tagged with a custom media type such as
  `application/vnd.microsoft.card.adaptive+json` would survive the bridge as
  an opaque `DataContent` carrying that exact media type** - the generic
  structured-data path is preserved end-to-end as data, it is simply never
  specially recognized, parsed, or rendered as a "card" by this bridge.
- **Outbound (`AIContent.ToPart()`):** only three `AIContent` subtypes are
  mapped at all: `TextContent` -> `Part.FromText`; `UriContent` -> `Part.FromUrl`;
  `DataContent` -> `Part.FromData(...)` (if JSON-ish media type) or
  `Part.FromRaw(...)` (otherwise). **Any other/custom `AIContent` subtype
  returns `null`** from `ToPart()`, and all call sites (`ChatMessageExtensions.ToA2AMessage`,
  etc.) explicitly filter out null results - i.e. an unrecognized content type
  is **silently dropped**, not an error.
- If `content.RawRepresentation` is already an A2A `Part`, that exact instance
  is returned unchanged (a pass-through fast path) - relevant if a card
  payload is round-tripped through the same process without ever being
  converted to a different `AIContent` subtype.

### 4.3 Standalone legacy converter versus this repository's compatibility endpoint

**Verified (version-pinned source), `src/A2A.V0_3/Extensions/AIContentExtensions.cs`
at tag `v1.0.0-preview2`:**

- **Inbound (`Part.ToAIContent()`):** `TextPart` -> `TextContent`; `FilePart`
  with a `Uri` -> `UriContent`; `FilePart` with `Bytes` -> `DataContent`
  (base64-decoded); **`DataPart.data` -> `DataContent`, with media type
  hardcoded to `"application/json"`** - unlike the 1.0 bridge, there is no
  conditional preservation of a custom media type, because 0.3's `DataPart`
  schema itself has no `mimeType` field to read from (confirmed in Section 1 above).
- That standalone legacy converter maps a generic outbound `DataContent` to a
  `FilePart`. **It is not the endpoint path this repository uses.**
- Here, `AgentCitations.ToContent` calls the **1.0**
  `Part.FromData(...).ToAIContent()`, which preserves the original part as
  `RawRepresentation`. The 1.0 outbound converter returns that part unchanged.
- `Program.cs` uses `MapA2AWithV03Compat`. Its
  `V03TypeConverter.ToV03Part` maps `PartContentCase.Data` directly to a
  **0.3 `DataPart`**. Verified at the pinned tag:
  <https://raw.githubusercontent.com/a2aproject/a2a-dotnet/v1.0.0-preview2/src/A2A.V0_3Compat/V03TypeConverter.cs>.
- The installed endpoint behavior is additionally demonstrated by the passing
  citation wire/replay tests in Section 8.8. No legacy-converter bypass,
  SDK upgrade, or file/base64 representation is needed for cards.
- A 1.0 `mediaType` field cannot survive as a 0.3 `DataPart` field.
  Discriminate the card through a key within the JSON data envelope instead.

### 4.4 Conclusion for this section

- **Protocol-level:** both 1.0 (`Part.data`) and 0.3 (`DataPart.data`) can
  carry arbitrary JSON, including a serialized Adaptive Card. Verified, Section 1.
- **Generic Agent-Framework-bridge-level:** the bridge *does* structurally
  preserve such JSON as an opaque `DataContent` on the way in (both versions),
  without ever inspecting, validating, or rendering it as a card - it is pure
  pass-through of bytes/JSON, not card-aware. The actual installed endpoint
  preserves keyed JSON data parts through both 1.0 and 0.3 using the citation
  path described above.
- **This repository's application layer is a separate, third layer,** on top
  of this generic bridge, and per
  the parent-agent's inspection (Section 8 below) currently discards non-text/
  non-citation `AIContent` before it would ever reach a client - i.e. even
  where the generic bridge *would* preserve a `DataContent`, this repository's
  own code does not currently forward it. This is an application-level gap,
  not a protocol or generic-bridge limitation.

---
## 5. A verified, version-pinned SDK bug: 0.3 Agent Card converter drops `extensions`

The user/parent-agent context references "a workaround for 0.3 SDK Agent Card
converter dropping extensions." This was independently verified against
primary source at the exact pinned version.

**Verified (version-pinned source).** `a2aproject/a2a-dotnet`,
`src/A2A.V0_3Compat/V03TypeConverter.cs`, method `ToV03AgentCard(A2A.AgentCard
v1Card)`, **at git tag `v1.0.0-preview2`** (the exact pinned
`A2A.V0_3Compat` release):

```csharp
Capabilities = new V03.AgentCapabilities
{
    Streaming = v1Card.Capabilities.Streaming ?? false,
    PushNotifications = v1Card.Capabilities.PushNotifications ?? false,
    // no Extensions assignment here at v1.0.0-preview2
},
```

Source: `https://raw.githubusercontent.com/a2aproject/a2a-dotnet/v1.0.0-preview2/src/A2A.V0_3Compat/V03TypeConverter.cs`
(lines ~38-43 of the fetched file), retrieved 2026-10-23. **This confirms,
from first-party source at the exact pinned tag, that `ToV03AgentCard` (used
to serve a 0.3-shaped Agent Card to 0.3 clients from a 1.0-native card) does
not copy `v1Card.Capabilities.Extensions` into the emitted 0.3
`AgentCapabilities` at all - the field is simply omitted, which the 0.3
JSON serializer then treats as an empty/absent `extensions` array. A 0.3
client reading that card has no way to discover any declared extension.**
`ToBlendedAgentCard` (the alternate blended-card helper in the same file)
internally calls `ToV03AgentCard` and therefore inherits the same gap.

**This bug was fixed in a later release.** The identical method at git tag
`v1.0.0-preview3` (published 2026-10-14, i.e. after `preview2`'s 2026-04-09
publish date) reads:

```csharp
Capabilities = new V03.AgentCapabilities
{
    Streaming = v1Card.Capabilities.Streaming ?? false,
    PushNotifications = v1Card.Capabilities.PushNotifications ?? false,
    Extensions = v1Card.Capabilities.Extensions?.Select(ToV03AgentExtension).ToList() ?? [],
},
```

Source: `https://raw.githubusercontent.com/a2aproject/a2a-dotnet/v1.0.0-preview3/src/A2A.V0_3Compat/V03TypeConverter.cs`
(line ~48 of the fetched file), retrieved 2026-10-23.

**This is a confirmed, version-pinned, root-cause-identified SDK bug in the
third-party `a2aproject/a2a-dotnet` package** (not in any Microsoft-authored
package, and not a protocol-level defect - see Section 1's note that the
`AgentExtension` schema itself is identical across versions). An upgrade from
`A2A.V0_3Compat 1.0.0-preview2` to `1.0.0-preview3` (or later) would pick up
the fix; this document does not assess compatibility risk of that upgrade
beyond this one fix, since a full diff of `preview2` -> `preview3` was out of
scope for this research pass.

---

## 6. Copilot Studio and Adaptive Cards

**Verified (primary source).** Copilot Studio's Message/Question node
authoring UI explicitly supports adding, among other rich components,
**Adaptive Cards** as a node content type, alongside "images," "videos,"
"basic cards," and "quick replies." ("Optionally, use the tools in the node's
menu bar to add: ... basic cards, Adaptive Cards, quick replies.")
Source: <https://learn.microsoft.com/en-us/microsoft-copilot-studio/authoring-send-message>,
retrieved 2026-10-23. This confirms Copilot Studio **can author and emit**
real Adaptive Card payloads from a topic's Message/Question node.

**Unverified / not established by this research:** whether a Copilot Studio
agent's Adaptive Card output, when that agent is consumed as a backend via
`Microsoft.Agents.CopilotStudio.Client` (as this repository does) or via a
downstream A2A/Foundry orchestrator, is preserved end-to-end as a renderable
card, or is instead reduced to its text-equivalent (or dropped) by the client
SDK, the adapter, or an intermediary orchestrator. This document found no
primary source describing that specific pass-through behavior in either
direction, and per the explicit instruction in the research brief, this
absence is reported as **unverified**, not as evidence that pass-through does
or does not happen.

---

## 7. Microsoft Foundry A2A tool - documented capabilities, and absence of Adaptive Card mentions

**Verified (primary source).** Microsoft Foundry Agent Service's A2A tool
documentation describes two tool generations:

| Tool definition | A2A protocol version | Status |
| --- | --- | --- |
| `a2a` (`A2ATool`/`A2AToolboxTool`) | 1.0 | Generally available (GA) |
| `a2a_preview` (`A2APreviewTool`/`A2APreviewToolboxTool`) | 0.3 | Preview |

Source: <https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/agent-to-agent>,
retrieved 2026-10-23. This directly parallels this repository's own dual
1.0/0.3 support.

The reviewed article covers connection configuration, authentication, SDK
registration/invocation, and toolbox usage. It does not establish an Adaptive
Card preservation, rendering, or action round-trip contract. References to an
A2A discovery Agent Card must not be treated as Adaptive Card UI support.

**Per the explicit research-brief instruction, this absence must be stated
as "undocumented," not as proof that Foundry's native orchestrator does or
does not preserve/render Adaptive Cards when brokering an A2A conversation.**
No claim is made here about Foundry orchestrator behavior beyond what this
page documents.

Likewise, the `microsoft-foundry` skill's own reference material for the
`a2a`/`a2a_preview` tool configuration shapes and the "Enable Incoming A2A"
workflow (read earlier in this research session, per task instructions, for
Foundry-specific questions) contains no mention of Adaptive Cards, attachments,
or rich content either - consistent with the Learn page finding above.

---
## 8. Repository findings (parent-agent inspection)

The following is this repository's current code behavior, as reported by a
parent agent that inspected the code directly against baseline commit
`072dbbb` (initially clean worktree). **This document's author did not
re-read this repository's source code independently** and reproduces these
findings for a single combined reference, with relative source links added.
All line numbers are approximate (`~`) as supplied by the parent agent.

### 8.1 Adapter-side text/citation-only plumbing

- [Invocation contracts](../src/FoundryCopilotA2A.Adapter/Contracts/CopilotInvocation.cs):
  `CopilotInvocationResult`/`Update` records (~17/29) only carry text,
  citations, identity, and conversation handle fields.
  `ExecuteTurnStreamAsync` (~466-620) ignores startup activities except
  conversation ID/OAuth; calls `AskQuestionAsync` for text and `ExecuteAsync`
  for files; `WithHandle` only issues after a nonempty final text;
  `emittedFinalText` gates success/OAuth retry.
  `ReadAnswerStreamAsync` (~675) only extracts text from `activity.Text` or a
  special connection-manager card - not from general attachments.
  `CopilotStudioAttachmentText` (~1473) extracts `TextBlock`s only from named
  connectors/`connectionManagerCard`, not from arbitrary Adaptive Card
  attachments (this extraction path and OAuth behavior should be kept
  separate from any new card handling).
  `CopilotStudioAnswerStream` (~1566) already preserves citations on a final
  activity whose repeated text is suppressed - a reusable pattern noted by
  the parent agent for handling late-arriving cards similarly.

- [`src/FoundryCopilotA2A.Adapter/AgentCitations.cs`](../src/FoundryCopilotA2A.Adapter/AgentCitations.cs)
  (~41): uses `Part.FromData(...).ToAIContent()` directly - i.e. this
  repository already has its own direct, manual `Part.FromData`/`ToAIContent`
  usage for citations, independent of the generic Agent-Framework bridge
  analyzed in Section 4 above. The parent agent's assessment: this pinned bridge
  usage need not be replaced for citation handling.

- [`src/FoundryCopilotA2A.Adapter/CopilotStudioAdapterChatClient.cs`](../src/FoundryCopilotA2A.Adapter/CopilotStudioAdapterChatClient.cs):
  `CreateResponseContents` (~236) emits `TextContent` plus citation data only.
  `IdempotencyStore.ProduceAsync` (~549) discards non-text/non-citation
  updates and rejects a no-text result. **All of this needs real card
  support, not just a UI renderer; optional card handling must not make
  citations-only/progress-only events spuriously "successful."**

### 8.2 Inbound request parsing / hashing

- [`src/FoundryCopilotA2A.Adapter/A2ARequestContextMiddleware.cs`](../src/FoundryCopilotA2A.Adapter/A2ARequestContextMiddleware.cs)
  (~70, ~448): extracts text/files/history/conversation handles - **not**
  card actions.
- [`src/FoundryCopilotA2A.Adapter/AdapterConstants.cs`](../src/FoundryCopilotA2A.Adapter/AdapterConstants.cs):
  `PayloadHash.Compute` (~56) hashes text/files only. Structured card-action
  submissions would need explicit validation plus action-aware canonical
  hashing and route/provider limits.

### 8.3 Conversation-handle and retry semantics (action-safety relevant)

- [`src/FoundryCopilotA2A.Adapter/CopilotConversationHandles.cs`](../src/FoundryCopilotA2A.Adapter/CopilotConversationHandles.cs):
  binds a protected, encrypted handle to caller+agent+context; `Resolve`
  ignores expired/foreign handles.
  `CopilotStudioInvoker.StreamCoreAsync` (~350-443) may restart a cached
  conversation once on an exception *before any emission* and replay text
  history. **A card/button action must not be allowed to fall back to a new
  conversation or auto-retry a potentially-already-applied action** - this is
  a stricter requirement than the existing streaming path's current
  behavior.
  `IdempotencyStore` is a bounded, process-local `MemoryCache` with a TTL;
  entries may be evicted on error; it is explicitly **not** a cross-restart or
  distributed exactly-once store. The browser's `sendMessage` generates a
  fresh `messageId` on every call, so action retries/dedup would need to
  explicitly reuse the same ID and add action-level claim/dedup - plain
  message-ID reuse is not sufficient for independent duplicate clicks.

### 8.4 Outbound/upstream invokers (text-only; none prove native card pass-through)

- [`src/FoundryCopilotA2A.Adapter/FoundryA2AInvoker.cs`](../src/FoundryCopilotA2A.Adapter/FoundryA2AInvoker.cs)
  (~65-128): sends text only, parses `A2AResponseText`, rejects blank text.
- [`src/FoundryCopilotA2A.Adapter/ApiManagementA2AInvoker.cs`](../src/FoundryCopilotA2A.Adapter/ApiManagementA2AInvoker.cs)
  (~100-180, ~290, ~517): sends text only, parses only text+citations for both
  SSE and non-streaming responses; generic JSON/card payloads are dropped.
  The parent agent notes the distinction between transparent APIM ingress and
  APIM-as-upstream-invoker usage - **neither configuration, as currently
  implemented, demonstrates that a native model-mediated orchestrator
  preserves cards**; this is consistent with Section 6/7's "unverified" conclusion
  above, from the repository's own code side.

### 8.5 Agent Card discovery surfaces and transport headers

- [`src/FoundryCopilotA2A.Adapter/Program.cs`](../src/FoundryCopilotA2A.Adapter/Program.cs):
  CORS explicit allowlist (~45) currently lacks any extension-negotiation
  header entries. All Agent Card discovery surfaces would need updating for
  any new extension: the manually built 0.3 target card (~400), the 1.0
  router card (~449), and the explicit 0.3 converter extension workaround
  (~499) - the last of which is this repository's own mitigation for the
  SDK bug independently confirmed in Section 5 above. Current declared output modes
  are `text/plain`+`application/json`; the Agent Card used for discovery is a
  protocol metadata document, not a UI Adaptive Card, and should not be
  conflated with one.
### 8.6 Browser clients (shared TypeScript, two React 19 front ends)

- Both clients share [`src/FoundryCopilotA2A.BrowserShared/a2aClient.ts`](../src/FoundryCopilotA2A.BrowserShared/a2aClient.ts)
  (not a per-client re-export). `A2AExchange` currently models text +
  citations + conversation handles only. `answerChunk` (~494) ignores unknown
  data; `sendMessage` (~328) rejects a submission with no text. The streaming
  accumulator is not currently keyed per-artifact, so multi-card updates would
  need a stable identity/dedup mechanism that does not overwrite accumulated
  text/citations.
- `taskStatus.ts` currently recognizes but **treats `input-required` and
  `auth-required` as errors**, not as a distinct "waiting for user" lifecycle
  state. [`A2AStreamingFinal.cs`](../src/FoundryCopilotA2A.Adapter/A2AStreamingFinal.cs)
  only rewrites terminal 0.3 states. The parent agent's assessment: any card
  action support needs an explicit lifecycle choice here, and must never
  infer a "waiting for input" state merely from the presence of a card, nor
  reopen an already-completed task.

### 8.7 UI persistence and redaction

- At the research baseline, the chatbot session, reply and persistence components
  supported text/citations only; the former diagnostic application had a separate
  render path. Neither client's package manifest then depended on an
  Adaptive Cards renderer library. The parent agent's recommendation (a
  recommendation, not a verified fact) is a shared validation/renderer and a
  sanitized, fallback-only persistence model initially - explicitly **not**
  persisting action handles, hidden data, or input values; history would
  remain text-based.
- [TraceSanitizer.cs](../src/FoundryCopilotA2A.Adapter/Tracing/TraceSanitizer.cs)
  (~594) redacts attachments and credential-like property names today, but
  **not** arbitrary `Activity.Value`/form-data shapes - so a typed card/action
  envelope would need its own explicit redaction rule, and the same applies to
  whatever browser-side diagnostic/exchange capture exists.

### 8.8 Empirical baseline (regression baseline only - not Adaptive Cards support)

The parent agent ran, against the clean baseline worktree, with no runtime
code modified and no live Azure calls or installs:

- `dotnet test tests\FoundryCopilotA2A.Adapter.Tests\FoundryCopilotA2A.Adapter.Tests.csproj --no-restore --filter FullyQualifiedName~CitationTests --verbosity minimal` -> **37 passed**, including a test exercising wire responses and replay carrying late sources without duplicating text, across 0.3/1.0 wire shapes, target/router cards, and streaming/non-streaming/replay/discovery paths.
- `node --experimental-strip-types --test src\FoundryCopilotA2A.Chatbot\tests\a2aCitations.test.mjs src\FoundryCopilotA2A.Chatbot\tests\conversationStore.test.mjs src\FoundryCopilotA2A.Chatbot\tests\taskStatus.test.mjs` -> **90 passed**.

These establish an existing structured-citation plumbing/regression baseline
only. They are **not** evidence of any implemented Adaptive Cards support, and
no runtime code was modified to produce them. Any future CLI-based live
probing should follow the repository's existing
`src/FoundryCopilotA2A.Cli` patterns (e.g. `SmokeTestCommands.cs`,
`CopilotFileUploadProbe.cs`) rather than ad hoc shell scripts, per the parent
agent's note.

---
## 9. Design recommendations (bounded - not an implementation plan)

These are high-level, conditional framings grounded in the verified facts
above. They are intentionally not sequenced, staffed, or scoped as an
implementation plan, per the research brief.

- **If** a card-like payload needs to traverse the adapter at all, **then**
  the recommended carrier is a keyed JSON envelope in `Part.data` (1.0) or
  `DataPart.data` (0.3), alongside ordinary text. It is not a new Part type.
- **If** a custom extension URI is introduced to declare "this agent may
  include an Adaptive Card payload alongside text," **then** it should follow
  the `a2a-protocol.org` extension governance model's spirit (URI-identified,
  declared in `AgentCapabilities.extensions`, with `required: false` so
  unaware clients are unaffected) - but no ready-made, officially sanctioned
  URI was found in the searched sources (Section 1); any proposed URI must be this project's own,
  clearly documented as non-standard.
- **If** such an extension is declared, **then** the known 0.3 Agent Card
  converter bug (Section 5) means any 0.3-facing Agent Card surface must keep using
  this repository's own existing extension-preservation workaround (or
  upgrade `A2A.V0_3Compat` past `preview2`) - otherwise 0.3 clients will never
  see the extension declared at all.
- **If** card payloads are to survive the generic Agent-Framework AIContent
  bridge at all, **then** per Section 4, generic `DataContent` pass-through already
  works for data arriving as `Part.data`/`DataPart.data` - the gap to close
  is in this repository's own application layer (Section 8.1), which currently
  discards non-text/non-citation `AIContent` before it ever reaches a client,
  not in the generic bridge itself.
- **If** `Action.Execute`/`adaptiveCard/action`-style round-trips are ever
  desired, **then** per Section 2 and 8.3, that is a strictly higher-risk feature
  than display-only cards: it requires a separately verified host/channel
  contract (schema introduction 1.4, with Teams-specific 1.5 guidance), an explicit
  no-restart/no-auto-retry invariant stricter than the existing streaming
  path, and action-level claim/dedup beyond simple message-ID reuse - none of
  which exist today.
- **Display-first, text-fallback** is the shape implied by the verified facts
  (cards as optional enrichment alongside the existing, load-bearing text
  response) rather than cards-as-primary-channel, because every success/error
  gate currently inspected by the parent agent (Section 8.1, 8.4, 8.6) is keyed on
  non-empty text, and changing that is out of scope for a research pass.

---

## 10. Remaining compatibility gates

Live provider gates are separate from local contract and host-version tests:

- Whether a normal message or startup Activity from the configured Copilot
  Studio agent exposes the full card attachment through SDK 1.8.77.
- Whether an object-valued `Action.Submit`, delivered as the agreed Activity
  shape in the exact original provider conversation, resumes the expected topic.
  Verify the returned business outcome, not just a successful HTTP response.
- Whether a Foundry-hosted agent's native (non-A2A, model-mediated)
  orchestration of a Copilot Studio or other sub-agent preserves, discards, or
  re-renders an Adaptive Card emitted by that sub-agent.
- Whether Copilot Studio's own native connected-agent orchestration (as
  opposed to this repository's direct `Microsoft.Agents.CopilotStudio.Client`
  usage) preserves an Adaptive Card end-to-end when the upstream agent is
  reached via A2A rather than a native Copilot Studio connector.
  (`docs/copilot-studio-a2a-connections.md` in this repository documents the
  native-connector OAuth/connection-lifecycle case, not card content
  preservation.)
  See: [`docs/copilot-studio-a2a-connections.md`](copilot-studio-a2a-connections.md).
- Which card schema/features the chosen browser renderer and configured
  provider emit/support. Declare a supported subset and verify unsupported
  versions fall back visibly; do not infer it from Teams' guidance.
- Whether APIM, when used as transparent ingress vs. as an upstream A2A
  invoker (Section 8.4), applies any content transformation to structured
  `data`/`DataPart` payloads beyond what this repository's own
  `ApiManagementA2AInvoker.cs` already does.

---
## 11. Confidence and limitations summary

| Area | Confidence | Basis |
| --- | --- | --- |
| A2A 1.0/0.3 `Part` schemas, no native card type | High | Verified, primary spec source, retrieved 2026-10-23 |
| `AgentExtension` schema identical across versions | High | Verified, primary spec source |
| No Adaptive Card extension identified in searched sources | Scope-limited | Official registry + `a2aproject` org, not an exhaustive ecosystem search |
| Submit is host-defined; Execute introduced in 1.4, Teams guidance 1.5 | High | Adaptive Cards and Teams docs, reconciled by independent review |
| Pinned SDK API surface | High for inspected members only | Assembly/source inspection; not proof of live action support |
| Actual endpoint preserves structured data in 1.0 and 0.3 | High | Pinned compatibility converter plus installed wire/replay tests |
| 0.3 Agent Card converter drops `extensions` at pinned version, fixed by `preview3` | High | Verified against exact pinned and next-release git tags |
| Copilot Studio can author Adaptive Cards | High | Verified, primary Learn doc |
| Copilot Studio/Foundry orchestrator card pass-through behavior | Not established (explicitly unverified) | No primary source found either way; reported as absence, not impossibility |
| Extension header names differ by version | High | Independent review of 1.0 spec and 0.3 extensions topic |
| Repository code behavior (Section 8) | Not independently re-verified by this document's author | Reported as supplied by parent-agent inspection against baseline `072dbbb` |

**General limitations:** the lead ran the focused existing tests in Section 8.8,
and the reviewer inspected the installed SDK metadata. No live Azure/Foundry/
Copilot Studio calls, card implementation, deployments, or dependency
installations were performed. Passing citation tests is not passing card tests.

## 12. Custom browser host responsibilities

The official [JavaScript SDK](https://learn.microsoft.com/adaptive-cards/sdk/rendering-cards/javascript/getting-started)
provides the `adaptivecards` package and renders card JSON into browser HTML.
Its [action documentation](https://learn.microsoft.com/adaptive-cards/sdk/rendering-cards/javascript/actions)
requires host handling through `onExecuteAction`, while several other action
types may execute automatically. Ignoring a Submit callback is therefore
**not** a sufficient display-only policy.

The [renderer specification](https://learn.microsoft.com/adaptive-cards/rendering-cards/implement-a-renderer)
requires `supportsInteractivity=false` to suppress actions, selectAction
targets, and inputs. It also requires unsupported schema versions to return
`fallbackText`, parse failures to reach the host, and warnings for dropped
unknown elements. [HostConfig](https://learn.microsoft.com/adaptive-cards/rendering-cards/host-config)
defaults interactivity to true, so the adapter UI must explicitly disable it.

**Design recommendation:** share a renderer wrapper and validated card model
between both React clients. Use a recursive host policy in addition to
HostConfig: no automatic actions, refresh, authentication, HTML, remote images,
media, icons, backgrounds, or uncontrolled links in the initial release.
Use plain-text card text initially rather than introducing an HTML-producing
Markdown callback. Show an explicit unsupported-interaction notice when inputs
or actions were omitted. This is a deliberately restricted host, not a claim
of supporting every Adaptive Cards feature.

Keep action data, input values, provider correlation, and future action handles
out of browser persistence, transcript replay, logs, and diagnostic exchanges.
Display-only history can retain the safe text fallback; restoring a conversation
must not restore actionable forms.

---

## Sources consulted (primary)

- A2A Protocol Specification, 1.0.0 - <https://a2a-protocol.org/v1.0.0/specification/>
- A2A Protocol Specification, 0.3.0 - <https://a2a-protocol.org/v0.3.0/specification/>
- A2A Extensions topic - <https://a2a-protocol.org/latest/topics/extensions/>
- A2A 0.3 Extensions topic - <https://a2a-protocol.org/v0.3.0/topics/extensions/>
- A2A Extension and Protocol Binding Governance - <https://a2a-protocol.org/latest/topics/extension-and-binding-governance/>
- `a2aproject/a2a-samples` GitHub repository, `extensions/` directory listing (GitHub Contents API)
- `a2aproject/a2a-dotnet` GitHub repository, tags `v1.0.0-preview1/2/3`, files `src/A2A/Extensions/AIContentExtensions.cs`, `src/A2A.V0_3/Extensions/AIContentExtensions.cs`, `src/A2A.V0_3Compat/V03TypeConverter.cs`
- `microsoft/agent-framework` GitHub repository, `dotnet/src/Microsoft.Agents.AI.A2A/` (`A2AAgent.cs`, `Extensions/A2AAIContentExtensions.cs`, `Extensions/A2AArtifactExtensions.cs`, `Extensions/ChatMessageExtensions.cs`)
- NuGet.org package metadata/`.nuspec` for `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` 1.19.0-preview.260822.1
- NuGet.org package reference source for `Microsoft.Agents.Builder` and `Microsoft.Agents.Core` 1.8.77
- Microsoft Learn: Universal Actions for Adaptive Cards - Overview - <https://learn.microsoft.com/en-us/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/overview>
- Microsoft Learn: Use Universal Actions for Adaptive Card - <https://learn.microsoft.com/en-us/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/work-with-universal-actions-for-adaptive-cards>
- Microsoft Learn: Adaptive Cards Universal Action Model - <https://learn.microsoft.com/en-us/adaptive-cards/authoring-cards/universal-action-model>
- Microsoft Learn: JavaScript rendering SDK - <https://learn.microsoft.com/adaptive-cards/sdk/rendering-cards/javascript/getting-started>
- Microsoft Learn: JavaScript action handling - <https://learn.microsoft.com/adaptive-cards/sdk/rendering-cards/javascript/actions>
- Microsoft Learn: Renderer specification - <https://learn.microsoft.com/adaptive-cards/rendering-cards/implement-a-renderer>
- Microsoft Learn: HostConfig - <https://learn.microsoft.com/adaptive-cards/rendering-cards/host-config>
- Microsoft Learn: Send a message (Copilot Studio authoring) - <https://learn.microsoft.com/en-us/microsoft-copilot-studio/authoring-send-message>
- Microsoft Learn: Connect to an A2A agent endpoint from Foundry Agent Service - <https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/agent-to-agent>
- `microsoft-foundry` skill reference docs (A2A tool configuration shapes, Enable Incoming A2A workflow) - read for Foundry-specific context per task instructions