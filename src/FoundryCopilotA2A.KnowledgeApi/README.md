# Local knowledge orchestrator

Separate .NET 10 backend using **Microsoft Agent Framework** to orchestrate document search,
delegated **Microsoft Graph SDK** tools, **Kamervragen search**, and approved **APIM A2A knowledge agents**. The model
chooses tools according to intent, answers general questions directly, or asks for clarification.
It does not call licensed Copilot retrieval/chat or Work IQ APIs.

## Code organization

Namespaces follow folders. Each top-level contract, interface and implementation has its
own named file; small private helper types remain nested with their owner.
The project-scoped [.editorconfig](.editorconfig) keeps four-space indentation and
enables unused-import cleanup through `dotnet format`.

| Folder | Responsibility |
| --- | --- |
| [Contracts](Contracts) | Requests, responses, documents, citations, chat and Graph data |
| [Controllers](Controllers) | HTTP input/output, caller resolution and streaming endpoints |
| [Interfaces](Interfaces) | Replaceable knowledge, model, Graph, retrieval and persistence modules |
| [Services](Services) | Knowledge coordination, chat, retrieval, Graph, A2A, user context and document processing |
| [Stores](Stores) | Document metadata, indexed passages, conversation state and pending actions |
| [Authentication](Authentication) | Caller scopes, delegated tokens, access checks and continuation tokens |
| [BackgroundServices](BackgroundServices) | Credential warm-up and retention cleanup |
| [Infrastructure](Infrastructure) | Azure/model clients and bounded or deterministic chat clients |
| [Streaming](Streaming) | AG-UI run input mapping and SSE events, progress descriptions and incremental citation formatting |
| [Middleware](Middleware) | Client-safe errors and authentication/retry response signals |
| [Extensions](Extensions) | Startup composition, module registrations, authentication, CORS and HTTP clients |

[Program.cs](Program.cs) composes configuration, DI and the ordered request pipeline.
Settings live in [appsettings.json](appsettings.json), with optional local settings and
non-empty environment overrides. Consumers inject `IConfiguration` directly; there are no
typed options classes or custom URL/index-name format validators. Startup retains the
backend, Development-only mock mode, upload/retention bounds and A2A authorization rules.
[AddKnowledgeApi](Extensions/KnowledgeApiExtensions.cs) registers HTTP contracts and modules,
including multipart limits and JSON settings. Shared service defaults are registered before
named clients remove retries and sensitive request logging.

Consumers inject interfaces rather than concrete implementations. Registrations in
[KnowledgeServiceCollectionExtensions.cs](Extensions/KnowledgeServiceCollectionExtensions.cs)
alias each interface to the existing singleton, so replacing an interface changes every
consumer without creating independent stores or lock pools. The
[dependency-injection tests](../../tests/FoundryCopilotA2A.KnowledgeApi.Tests/DependencyInjectionTests.cs)
exercise substitutions with concrete registrations that deliberately throw if used.

Live and mock orchestration are separate partial files:
[ResponsesCoordinator.cs](Services/Chat/ResponsesCoordinator.cs) and
[ResponsesCoordinator.Mock.cs](Services/Chat/ResponsesCoordinator.Mock.cs). They intentionally
share evidence limits and formatting rather than duplicating those rules. The deterministic
[MockChatClient](Infrastructure/MockChatClient.cs) and shared
[AgentTurnRunner](Services/Chat/AgentTurnRunner.cs) are separate types.
Mock mode still needs no Azure resources. Routes, JSON shapes, authorization rules,
and retention behavior are unchanged by the refactor. `POST /api/chat/stream` now
uses the [AG-UI](https://docs.ag-ui.com/) event protocol ([Streaming chat](#streaming-chat)).

## Use it

The existing Aspire AppHost includes the `knowledge` resource and passes its URL to the
dedicated [Knowledge Chat](../FoundryCopilotA2A.KnowledgeChat/README.md) at
`http://localhost:5175`. It talks only to this API; the Orchestrator Chat at 5174 talks only to
Copilot Studio. "Local" means application-owned orchestration, not offline inference: Azure
mode still calls the configured Azure OpenAI model. The diagnostic A2A console is unchanged.

- Upload a document with the composer; the backend indexes it before answering.
- Open **Documents & SharePoint** to find a site, library, folder and file, then import it.
- All six Microsoft 365 sources are selected by default; uncheck sources to withhold their
  tools from the model. Selection permits a tool, rather than forcing a call on every turn.
- APIM agents are selected by default when the API reports A2A support. Live access requires
  the explicit read-only agent allowlist and delegated consent described below.
- Kamervragen is selected by default when available. It searches a separately configured,
  shared parliamentary reference index, not the user's uploaded documents.
- Remove indexed documents in Documents & SharePoint. Clearing browser chat history does
  **not** delete backend documents or retained server-side dialogue.
- Each chat app keeps its own browser history. Local questions
  retain bounded dialogue for follow-up questions but rebuild evidence on each turn.
  Previous private-context answers are not replayed as evidence.

For standalone backend development, use the operational CLI:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-knowledge --backend Mock
```

The CLI allows the Knowledge Chat origin `http://localhost:5175` by default.
Configure `VITE_KNOWLEDGE_BASE_URL` on a standalone Knowledge Chat. In Entra mode also configure
`VITE_KNOWLEDGE_API_CLIENT_ID` with the API registration's client ID (which may be shared
with the adapter/API Hub).

## Document retrieval modes

The local agent chooses a retrieval strategy using tool instructions; no separate decision
model is required or called. Ask, for example, "Summarize the entire report", "List every
deadline in the contract", or "Compare these two documents". The available-document catalog
includes exact IDs and chunk counts. Ambiguous document references should prompt clarification.

`search_documents` remains the top-eight hybrid search across conversation documents.
The new `retrieve_documents` tool accepts `mode`, the full `query`, and 1-4 exact `documentIds`:

| Mode | Behavior |
| --- | --- |
| `focused` | Top-eight hybrid results per selected document |
| `contextual` | Matching chunks plus one preceding/following chunk, deduplicated and ordered per document |
| `whole_document` | Every extracted chunk, in original document order, for summaries |
| `exhaustive` | Every extracted chunk; batch analysis extracts individual matching findings rather than a generic summary |
| `comparison` | Every extracted chunk from each of 2-4 selected documents, preserving document attribution |

Full reads page through the existing index without embeddings or a relevance cutoff.
They validate every expected chunk ID against committed metadata and restore numeric chunk
order. No Search schema migration or reindex is needed. Missing, duplicate, expired or
unauthorized chunks cause an explicit error, not a successful partial read.
All paths retain conversation scope, user ID, document allowlists and expiry filters.
SharePoint imports are reauthorized before retrieval and after analysis; metadata is
rechecked before analysis and before results return to the answering model.

Small reads send original text. Larger reads use ordered batches, kept separate per
document, through the existing Azure OpenAI deployment (`store: false`, no tools).
Both search results and batch inputs include explicit `pageNumber` and `passageNumber`
metadata alongside the original `locator`. Derived findings retain these fields on each
supporting source. Page numbers come from the extraction page locators, including already
indexed documents; no reindex is required. Passage numbers are chunk sequence numbers,
not section numbers. Unknown locations remain null; section numbers are not inferred.
Batch analysis returns structured findings whose supporting chunk IDs are validated before
the server assigns citations to the original passages. The main agent synthesizes these
findings, deduplicates repeated findings and streams the answer. Progress reports document
reads and batch counts. Tool results include expected/read chunk counts and whether evidence
was synthesized. Complete chunk coverage is **not** a guarantee of perfect model extraction
or coverage of images/text omitted by document extraction.

Bounds are explicit: 2,000 document evidence chunks per turn (separate from the ordinary
80-passage tool budget), 48,000 serialized UTF-8 bytes per analysis input batch, and 96,000
serialized bytes of document tool output per turn. These conservative byte limits are
not tokenizer measurements. Batch output is capped at 6,000 tokens; document-enabled final
answers at 12,000 tokens. The existing four-minute turn deadline still applies.
Oversized findings, incomplete model responses and missing chunks fail explicitly; the
tool never silently discards later sections to fit a context limit. Narrow the question
or select fewer documents when a limit is reached.

Mock storage supports all retrieval modes without Azure. Mock batch processing preserves
the input text rather than pretending to produce model summaries, so large mock reads can
hit the output budget. The normal mock chat remains a deterministic fixture, not an intent
classifier. Contract tests exercise real framework tool selection and analysis using
simulated model responses.

## Configuration and identity

Resource URLs supplied during development are in ignored `appsettings.Local.json` files in
this project and the AppHost. They are deliberately not committed. The AppHost exposes
`openai-endpoint`, `chat-deployment`, `embedding-deployment`, `search-endpoint`,
`search-index`, `document-intelligence-endpoint`, `speech-endpoint`, and `speech-voice`
parameters. Defaults for model/deployment names are in tracked configuration; endpoint
values must come from local configuration or the deployment environment.

Standalone configuration:

| Setting | Purpose |
| --- | --- |
| `Knowledge:Backend` | `Mock` (default, Development + loopback only) or `Azure` |
| `OpenAI:Endpoint` | Azure OpenAI resource root |
| `OpenAI:ChatDeployment` | `gpt-6.1-sol`; called using `/openai/v1/responses`, `store: false` |
| `OpenAI:EmbeddingDeployment` | `text-embedding-3-large` |
| `OpenAI:EmbeddingDimensions` | `3072`; must match the Search index |
| `Search:Endpoint`, `Search:Index` | Dedicated Search service and `asrlocalrag` index |
| `DocumentIntelligence:Endpoint`, `DocumentIntelligence:ModelId` | Resource root and `prebuilt-read` |
| `Authentication:TenantId`, `Authentication:ClientId` | Single-tenant API registration; may be shared with the adapter/API Hub |
| `Authentication:ClientSecret` | OBO credential; environment/secret store only, never source |
| `Knowledge:AllowedOrigins` | Explicit frontend origins |
| `Knowledge:DataDirectory` | Private persistent, single-instance metadata volume |
| `Knowledge:RetentionDays` | 7 by default; document lifetime and sliding dialogue-history expiry |
| `A2A:Enabled` | Enable APIM knowledge in Azure mode; default `false` |
| `A2A:AdapterBaseUrl` | Trusted adapter base URL; HTTPS, or loopback HTTP in Development |
| `A2A:AuthenticationMode` | `OnBehalfOf` (default) for a separate API audience, or explicit `SameAudience` for a shared registration |
| `A2A:DelegatedScope` | Adapter permission for `OnBehalfOf`, e.g. `api://<adapter-client-id>/access_as_user`; leave empty for `SameAudience` |
| `A2A:AllowedAgentIds` | 1-20 distinct IDs from the adapter's APIM catalog, approved for read-only knowledge |
| `Kamervragen:Enabled` | Enable the shared parliamentary search tool in Azure mode; default `false` |
| `Kamervragen:Endpoint`, `Kamervragen:Index` | Azure AI Search resource root and existing `kamervragen-openai` index |
| `Speech:Endpoint`, `Speech:Voice` | Reserved configuration (`nl-NL-FennaNeural`); no speech synthesis route/UI is implemented |

Configure tenant, API audience and service endpoints before using Azure mode. Configuration
values are passed to the SDKs and HTTP clients without custom URL or index-name checks;
configuration and service failures are reported rather than replaced with mock data.
For Aspire set `KnowledgeBackend` to
`Azure` and supply `Parameters:knowledge-tenant-id`, `Parameters:knowledge-client-id` and
the secret parameter `Parameters:knowledge-client-secret`. Reuse the adapter registration
when both services form the same logical API, or configure a separate registration and
its required consent. These instructions do not provision or grant tenant permissions.

### Shared Kamervragen search

[KamervragenSkill.cs](Services/Retrieval/KamervragenSkill.cs) adds the Agent Framework function
`search_kamervragen`, exposed only when the `kamervragen` source is selected and the
backend reports `kamervragen: true`. It is a runtime skill, not a VS Code customization.
It supports a natural-language query and optional exact filters for `thema`, `ministerie`,
`partij` (a member of `partijen`), `documentNummer`, and inclusive UTC publication dates
`fromDate`/`toDate` (`yyyy-MM-dd`). Filter values are validated and escaped, not accepted as
raw OData. Results are a top-eight sample, never exhaustive.

For Aspire, enable `KnowledgeKamervragen:Enabled` and set
`KnowledgeKamervragen:Endpoint` and `KnowledgeKamervragen:Index` in the AppHost's ignored
local settings. Standalone configuration uses the `Kamervragen:*` names above. This
configuration is separate from `Search:*`; it never changes, indexes, or deletes anything
in either the parliamentary index or the personal-document index.

The existing index must provide the supplied Dutch metadata schema, `semantic` semantic
configuration, `freshness` scoring profile, and the `contentVector` field with its configured
Azure OpenAI vectorizer. Hybrid requests combine text search with `kind: text` vector
queries (`k: 50`) and semantic ranking. Azure AI Search uses the field's vectorizer and
1,024-dimensional schema; the API does **not** reuse the upload index's 3,072-dimensional
embeddings or call the upload embedding deployment.
The stable `2024-07-01` Search API rejects `queryLanguage`; Dutch text analysis is configured
on the index fields (`nl.microsoft`), and semantic ranking detects the query language.
For a read-only live replay with detailed upstream errors, use the operational CLI's
`test-knowledge-search --endpoint <search-root> --index kamervragen-openai --request-file <json-path>`.
The command uses Azure CLI authentication, prints result counts rather than documents, and
returns a nonzero exit code on Search errors. Error details may echo request values; use
non-sensitive queries for diagnostics.

Authentication uses the backend's existing Azure credential with **Search Index Data
Reader** access. The Search service's vectorizer must independently have access to its
embedding deployment (for managed identity, **Cognitive Services OpenAI User**). No API
keys or Graph/OBO tokens are sent to Search. Azure/RBAC/vectorization errors are surfaced;
live failures never fall back to mock data.

**Access boundary:** this index has no user/tenant ACL fields. Enable it only for a corpus
approved for all authenticated users of this Knowledge API; it is not user-security-trimmed.
Uploaded and SharePoint-imported documents retain their existing user/conversation filters.
Passages get server-assigned citations with subject, publication date, and document number
when present. Blob paths and storage URLs are intentionally omitted; no public URL is
invented. Text and metadata are bounded, untrusted evidence, and re-retrieved on follow-ups.

Mock mode advertises the same selectable source, emits clearly labeled synthetic evidence
and streaming statuses, and requires no Azure resources.

### API and Graph delegated access

The API requires an Entra v2 token with its client ID as audience, `tid`, `oid` and delegated
`access_as_user` scope. The SPA requests `api://<knowledge-client-id>/access_as_user`, not a
Graph token. The backend performs OBO to Graph and propagates consent/claims challenges.
The frontend acquires a new token for a claims challenge and retries once. There is no
app-only Graph fallback.

Required registration configuration before enabling real Graph:

- Expose `access_as_user` and grant/preauthorize the Knowledge Chat SPA.
- Add and approve the chosen delegated Graph permissions. SharePoint browsing/import
  requires `Sites.Read.All` and file download access (typically `Files.Read.All`).
- Mail uses `Mail.ReadWrite` (read plus confirmed drafts and read state), calendar uses
  `Calendars.ReadWrite`, both task sources use `Tasks.ReadWrite`, and Teams message search
  uses `Chat.Read` plus `ChannelMessage.Read.All`. With only the `.Read` variants, reads
  still work and confirmed write-backs fail with a 403 `graph_error`. Graph tokens are
  requested with `https://graph.microsoft.com/.default`, so the consented registration
  permissions apply without code changes.
  People search requires `People.Read`. The user context (see below) reads `/me` with
  `User.Read` and `/me/mailboxSettings` with `MailboxSettings.Read`; without them the chat
  falls back to the browser time zone. Requested app permissions are not proof of consent:
  verify the delegated grant and acquire a fresh token after granting consent.
- Request only approved capabilities. Teams search is not a complete channel/chat crawler;
  search results may be limited to the user's own messages. A Teams search 403 can fall
  back to a bounded list of recent chat previews, not full channel/chat search.
  Activity evidence is transient, never written to the index.

Azure service calls use cached credentials (Azure CLI first in Development, default Azure
credential chain when hosted). Startup warms the model and Search tokens; near-expiry tokens
refresh on demand. Graph and cross-audience A2A delegated tokens use a separate MSAL OBO cache, scoped by
user assertion and resource permissions. The identity needs Azure OpenAI inference permissions, Search Index
Data Contributor, and Cognitive Services User for Document Intelligence. No service keys are
used. The host operator needs Search Service Contributor to create the index.

## Agent Framework orchestration and Graph tools

[GraphSkill.cs](Services/Graph/GraphSkill.cs) supplies instructions and strict function schemas to the
Agent Framework function pipeline backed by the Azure OpenAI Responses API. It is a backend
runtime module, not a VS Code agent skill. Graph routes use typed `GraphServiceClient`
builders and models; download redirects are handled separately to keep credentials off
preauthenticated file URLs.
Only explicitly enabled sources expose tools to the model.

| Tool | Delegated Graph route | Purpose |
| --- | --- | --- |
| `graph_list_recent_mail` | `GET /me/mailFolders/inbox/messages?$select=...&$orderby=receivedDateTime desc&$top=6` | Newest inbox messages, optionally unread only |
| `graph_search_mail` | `GET /me/messages?$search=...` | Search message previews |
| `graph_list_calendar` | `GET /me/calendarView?$select=id,subject,start,end,isAllDay,location,webLink,isCancelled,onlineMeeting,onlineMeetingUrl&$orderby=start/dateTime&$top=50&startDateTime=...Z&endDateTime=...Z` | Occurrences in an explicit range of at most 31 days, converted to UTC for the query and returned in the user's time zone |
| `graph_list_todo_lists` | `GET /me/todo/lists` | Discover personal task lists, including `defaultList` and `flaggedEmails` |
| `graph_list_todo_tasks` | `GET /me/todo/lists/{listId}/tasks` | Read tasks in a discovered or user-supplied list |
| `graph_list_my_planner_tasks` | `GET /me/planner/tasks` | Read assigned Planner tasks and discover plan IDs |
| `graph_list_plan_tasks` | `GET /planner/plans/{planId}/tasks` | Read tasks in an accessible known plan |
| `graph_search_teams` | `POST /search/query` | Search Teams messages; limited chat-preview fallback on 403 |
| `graph_search_people` | `POST /search/query` | Search colleagues |

Read tools use the signed-in user's OBO token. There are no application permissions,
arbitrary URL tools, or licensed retrieval/chat API calls.

The To Do routes send no query options because `GET /me/todo/lists` rejects `$top` and
`$select` with HTTP 400. Graph cannot order To Do or Planner tasks, so the API orders the
first page itself: open tasks first, then by due date, at most 25 items. To Do stores a
due date as local midnight in UTC (`2026-10-04T22:00:00Z` is 5 October in Amsterdam), so
each task also gets a `dueDate` (`yyyy-MM-dd`) in the user's time zone. Calendar
`location` is reduced to its display name and `onlineMeeting` to a validated `joinUrl`.

### Confirmed write-back

[GraphActions.cs](Services/Graph/GraphActions.cs) adds `graph_propose_*` tools for enabled sources. The
model can only **propose** a change; it never writes. Each proposal is validated, enriched
with trusted display data from Graph (for example the real message subject or task title),
and staged in [GraphActionStore.cs](Stores/GraphActionStore.cs). The chat response returns it in
`actions` and the UI renders a confirmation card. Graph is written only when the user
clicks **Confirm**. Content from mail, Teams, documents or A2A agents therefore cannot
trigger writes by prompt injection.

| Tool | Graph write on confirm | Notes |
| --- | --- | --- |
| `graph_propose_mail_draft` | `POST /me/messages` | Saved to Drafts, never sent |
| `graph_propose_mail_reply_draft` | `POST /me/messages/{id}/createReply` | Reply draft, never sent |
| `graph_propose_mail_read_state` | `PATCH /me/messages/{id}` | Mark read or unread |
| `graph_propose_calendar_event` | `POST /me/events` | Times normalised to UTC, attendees receive invites |
| `graph_propose_todo_task` | `POST /me/todo/lists/{listId}/tasks` | Optional body and due date |
| `graph_propose_todo_task_status` | `PATCH /me/todo/lists/{listId}/tasks/{taskId}` | Status change |
| `graph_propose_planner_task_progress` | `PATCH /planner/tasks/{taskId}` | Reads the ETag first and sends `If-Match` |

Pending actions are single-use, bound to the signed-in user and conversation, and expire
after 15 minutes (at most 10 per conversation, at most three proposals per turn). They are
held in memory and lost when the service restarts. Deletes and mail sending are not
supported. Confirmation outcomes are shown on the card but are not written into the
conversation history.
Teams and people are model-selected tools, not eager prefetch sources. SharePoint
browsing/import remains separate from activity tools.

The model can discover lists and then query tasks in the same turn.
[ResponsesCoordinator.cs](Services/Chat/ResponsesCoordinator.cs) creates a turn-local `ChatClientAgent`
with `FunctionInvokingChatClient` and `InMemoryChatHistoryProvider`. The framework replays
function calls, encrypted reasoning and outputs within that turn with `store: false`.
Only the bounded dialogue projection described below persists across requests; no model
response ID or service-side conversation ID is reused.
[BoundedChatClient.cs](Infrastructure/BoundedChatClient.cs) validates tool names, call IDs and budgets before
execution. Each turn has a four-minute deadline,
at most six Responses rounds, eight tool calls, and 80 ordinary evidence passages.
Document retrieval has the separate bounded read/analysis path described above; its internal
batch model calls do not consume the main agent's six-round budget. Graph tool results
contain at most ten records from the first page; they are not exhaustive. Text is capped
and treated as untrusted evidence. Tools supply explicit `[[cite:n]]` markers, validated
against the current evidence and rendered as `[n]` for the unchanged browser contract.
Ordinary brackets such as `items[0]`, arrays, and Markdown links are not mistaken for citations.
Invalid arguments, access failures and throttling are explicit errors,
not successful empty searches. Source warnings are returned to the UI.

Mock mode exercises the tools with synthetic fixtures, including To Do discovery followed
by a task query, without any Azure resources. Live Graph access still requires the
registration and delegated consent described above. People search preserves bounded
display name, job, department, location and email fields even when Graph returns an empty
search summary. Agent Framework emits
OpenTelemetry agent spans into Aspire without opting into sensitive prompt/response logging.

### User context: profile, time zone and current time

[UserContext.cs](Contracts/Chat/UserContext.cs) gives every local orchestrator turn the signed-in user's
default profile and the current date and time, so the model can answer "what's on my
calendar tomorrow?" or "what day is it?" without asking.

- **Profile**: display name, given name, email, job title, department, office and preferred
  language from `GET /me`. Working hours, date format and time format come from
  `GET /me/mailboxSettings`. Values are cleaned to single lines of at most 120 characters
  and labelled as data, not instructions.
- **Time zone**, first match wins: the Outlook mailbox time zone, then the browser's IANA
  zone sent as `timeZone` in the chat request, then UTC. The instructions name the source,
  the current UTC offset and the next daylight-saving change.
- **Current time**: the local weekday, date and time with its ISO offset, plus UTC.
  It is computed on every turn, never cached.

Both reads are best-effort and are cached per user for one hour (two minutes after a
failure). A missing permission or consent logs a warning, never fails the chat and
never leaks a `WWW-Authenticate` challenge to the client. Calendar reads send
`Prefer: outlook.timezone="<zone>"`, so Graph returns event times in the user's zone.
Proposed To Do due dates use the same zone. Mock mode uses a synthetic profile and the
browser zone, and answers date or time questions without a model call.

## Conversation history

[ConversationHistory.cs](Stores/ConversationHistory.cs) uses Agent Framework's public
`CreateSessionAsync`, `DeserializeSessionAsync` and `SerializeSessionAsync` APIs with
`InMemoryChatHistoryProvider`. [ConversationHistoryStore.cs](Stores/ConversationHistoryStore.cs)
atomically persists a projected session under the server-derived tenant/user/conversation
scope in `Knowledge:DataDirectory`; the client never supplies the stored messages or user ID.

- Keep at most **12 complete user/assistant turn pairs** and **24,000 text characters**.
  Assistant dialogue is capped at 8,000 characters; oldest whole pairs are dropped first.
- Preserve the user's original question and source-free general answers/clarifications.
  A turn that has tools, initial evidence/warnings, or document names in its model context
  stores a fixed re-retrieval reminder instead of its assistant answer. This is intentionally
  conservative, including a general answer made while private document names were available.
- Never persist enriched prompts, source/tool results, citations from private replies,
  reasoning, response IDs, tokens, or remote A2A sessions. Follow-up private questions must
  retrieve authorized evidence again using the **current** source selection.
- Serialize same-conversation turns with bounded striped locks. Commit only after a
  successful, validated answer; failed/canceled turns leave the prior session unchanged.
  History read/write failures are explicit errors, not silent conversation resets.
- Sessions survive API restarts on a durable volume and expire after
  `Knowledge:RetentionDays` without a successful turn (default seven days).
  Expired history is excluded immediately and deleted by the five-minute cleanup sweep.
  A new conversation ID starts fresh. Browser deletion/clearing is browser-only; retained
  server dialogue expires independently.
- Mock mode follows the same framework persistence path with a deterministic local
  `IChatClient`, without making a model or Azure call.

The private volume contains user-authored text and source-free replies. Restrict its
filesystem access and protect/encrypt the volume and backups. This is deliberately a
**single-instance** store, not a multi-replica distributed lock or session database.

## APIM agents as knowledge

[A2aKnowledgeService.cs](Services/A2A/A2aKnowledgeService.cs) reuses the adapter's existing
`/api/agents` catalog and APIM discovery/cache. It exposes two model tools:

- `list_a2a_agents`: list only supported `apiManagement` entries in `A2A:AllowedAgentIds`.
- `ask_a2a_agent`: invoke an approved specialist through the adapter's A2A endpoint using
  the Agent Framework A2A client. The adapter resolves the actual APIM route.

The model never supplies a URL, credential or HTTP header. Each invocation gets a fresh
A2A session. Choose the authentication policy explicitly:

- **`OnBehalfOf`**: exchange the incoming API assertion for the adapter's configured
  delegated scope when it has a different audience. Configure and consent that permission.
- **`SameAudience`**: when the Knowledge API and adapter/API Hub share one registration,
  reuse the token saved by the successful JWT bearer authentication handler. The API checks
  the validated audience against its configured client ID, delegated `access_as_user` scope,
  and matching tenant/user. It does not decode an unvalidated header or perform another
  token exchange. Set `A2A:AuthenticationMode` to `SameAudience` and leave
  `A2A:DelegatedScope` empty. This hop needs no separate consent to the same API.

The trusted adapter/APIM destination must validate that same intended audience and
delegated caller. Other downstream resources (Graph, Power Platform, separate API audiences)
still require their own tokens and consent. No app-only fallback or automatic downgrade is
used. Catalog lookups use public discovery; runtime calls remain delegated.

For Aspire, configure `KnowledgeA2A:Enabled`, `KnowledgeA2A:AuthenticationMode`,
`KnowledgeA2A:DelegatedScope` (only for `OnBehalfOf`), and
`KnowledgeA2A:AllowedAgentIds` in ignored local configuration. AppHost supplies the adapter
endpoint and startup dependency. Keep the existing `ApiManagementDiscovery` configuration
on the adapter. Standalone deployments use the `A2A:*` settings above.

**Only allowlist knowledge-only specialists.** A prompt asking for read-only answers is not
an authorization boundary: enforce read-only tools/permissions on each remote agent and
APIM policy. Do not allowlist an orchestrator that routes back to this API. A specialist
receives only its focused question, not uploaded files, accumulated passages or chat history.
Its answer is treated as untrusted evidence and is not indexed. Server-assigned citations
preserve the specialist's identity; nested remote citation numbers/URLs are not trusted as
local citations. Calls have a 90-second deadline, text is capped at 12,000 characters, and
incomplete/background tasks fail explicitly rather than being presented as completed answers.
Access and service failures are reported, never replaced with synthetic live results.

Mock mode provides a clearly labeled synthetic APIM specialist without contacting Azure,
MSAL, the adapter or an external agent. The A2A integration package and the Responses SDK
surface are preview APIs; package versions are pinned in the project file.

### Per-agent selection

The allowlist is the administrator's ceiling; the user can narrow it per turn.
`GET /api/agents` (authenticated) returns the allowlisted agents that the adapter catalog
currently reports as supported `apiManagement` entries, as `{agents:[{id,displayName}]}`.
The adapter already caches APIM discovery, so this API adds no cache of its own.

A chat request may add `agents: ["<id>", ...]` next to `sources`:

- It only applies when `sources` contains `"a2a"`; otherwise it is ignored.
- Omitted or `null` means every approved agent, which keeps older clients working.
- An empty list, more than 20 entries, blank or oversized IDs, or any ID outside
  `A2A:AllowedAgentIds` is rejected with `400 invalid_agent`. IDs match case-insensitively.
- `list_a2a_agents` returns only the selected agents. `ask_a2a_agent` refuses an unapproved
  ID with `403 a2a_agent_not_allowed` and an approved but unselected one with
  `403 a2a_agent_not_selected`, before any catalog or remote call.

The Knowledge Chat settings drawer lists these agents under **APIM agents**, all
checked by default (including agents that appear later). Unchecking the last agent turns the
APIM source off; checking one while the source is off turns it on with only that agent. If the
list cannot be loaded, the request omits `agents` and the server uses every approved agent.

## Search schema

The API checks the existing index and refuses missing or incompatible fields/dimensions.
It never silently replaces a populated index.

The schema is in [search-index.json](search-index.json). Preview creation through the CLI:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- create-knowledge-index `
  --endpoint <https-search-resource-root> --index asrlocalrag
```

Add `--apply` only after reviewing the schema. This uses the Azure CLI's signed-in identity,
creates only an absent index, and verifies it. Existing indexes are never overwritten.
Do not create/replace an index without the resource owner's approval.

Search uses hybrid lexical/vector retrieval with a server-derived hash of tenant, user
and conversation, explicit `userId` security trimming (`userId eq '<caller-oid>'`),
committed document IDs, and expiry filtering. SharePoint documents are
reauthorized and ETags checked before retrieval. Revoked or changed originals block the
request. Citations point to authorized passages, not model-created URLs.

## Ingestion and current limits

- Device and SharePoint imports share one pipeline. Files are bounded to 20 MiB;
  formats are PDF, DOCX, UTF-8 TXT/Markdown/CSV.
- PDF/DOCX bytes go to Document Intelligence `prebuilt-read` (`2024-11-30`).
  The API polls with a four-minute budget, respects polling `Retry-After`, and deletes the
  successful analysis result after reading it. Failed/cancelled service operations may
  remain under the service's retention policy; no access tokens/download URLs are stored.
- **Read is text/OCR extraction, not layout/table reconstruction.** This was explicitly
  selected instead of `prebuilt-layout`. Table cell structure and reading order in complex
  layouts are not guaranteed. No automatic table-preservation claim is made.
- Chunks are page-local, newline-aware, at most 2,400 UTF-8 bytes (roughly 600 English
  tokens; the byte bound also limits worst-case tokenizer input). There is no overlap or
  structural CSV/table processing yet. More than 300 extracted pages or 2,000 chunks fails.
  The page cap is checked after analysis, so over-limit files can still incur analysis cost.
- At most 30 ready documents per conversation and two concurrent ingestion operations.
- Imports are synchronous with a ten-minute budget. Pending metadata is written before
  indexing; a version becomes searchable only after all indexing results succeed.
  Failed/pending versions are never returned. Abandoned pending versions are reclaimed
  after one hour; expired and deleted records are swept every five minutes.
  Transport failures and non-shutdown timeouts are logged, preserve metadata for the next
  sweep, and do not stop the API or prevent cleanup of other documents.
- Metadata and mock passages persist on disk; Azure passages/vectors persist in Search.
  Mount `Knowledge:DataDirectory` on durable private storage. This design supports one
  WebAPI instance, not shared/multi-replica ingestion.
- Mock mode uses deterministic synthetic PDF/DOCX extraction and fixture Graph results;
  it never pretends to contact Document Intelligence, Graph, Search, or the model.
- No malware scanner, durable job queue, automatic reimport, sensitivity-label integration,
  custom site allowlist, production per-user rate limiter, or durable OBO token cache is
  implemented. These remain production rollout gates in the
  [architecture plan](../../docs/graph-workiq-rag-plan.md).

The application does not retain a source-file archive. Imports are size-bounded, but
ASP.NET multipart buffering can use temporary disk files during the request; protect the
host's temporary storage as well as the metadata volume.
Document Intelligence has its own processing/retention boundary. Approve regions, content
copying and provider processing before sending real documents. Originals with rights
restrictions must not be imported without an approved enforcement policy.

## HTTP surface

All routes except health/capabilities require delegated API auth in Azure mode.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/capabilities` | Mode, formats, limits, extraction model, Graph and A2A availability |
| GET | `/api/agents` | `{agents:[{id,displayName}]}`: approved APIM agents currently in the adapter catalog; empty when A2A is disabled |
| GET/POST | `/api/conversations/{uuid}/documents` | List/import one multipart `file` |
| DELETE | `/api/conversations/{uuid}/documents/{id}` | Tombstone first, delete all chunk keys, remove metadata |
| POST | `/api/conversations/{uuid}/sharepoint` | Import authorized `{driveId,itemId}` |
| GET | `/api/graph/sites?query=...` | Search accessible sites by keywords or resolve an HTTPS SharePoint site URL |
| GET | `/api/graph/drives?parent=<site>` | List libraries |
| GET | `/api/graph/items?drive=<drive>&parent=<folder>` | Browse root/folder |
| POST | `/api/chat` | `{conversationId,text,sources?:["mail","calendar","todo","planner","teams","people","a2a"],agents?:["<approved id>"],timeZone?:"Europe/Amsterdam"}`; returns proposed `actions` |
| POST | `/api/chat/stream` | AG-UI `RunAgentInput`; streams progress and answer text as AG-UI server-sent events (below) |
| POST | `/api/actions/{id}/confirm` | `{conversationId}`; executes a pending action once, `404 action_not_found` if missing, expired or another scope |
| POST | `/api/actions/{id}/cancel` | `{conversationId}`; discards a pending action |

Picker responses contain `items` and an optional protected `nextPage` token. Pass the same
query, drive and parent with `nextPage`; tokens expire and are bound to caller and query.
An HTTPS `*.sharepoint.com` site URL in the site query is resolved by hostname and
server-relative path through Graph, rather than submitted as search text. Root URLs and
paths such as `/sites/Finance` or `/teams/Operations` are supported. The single site is
returned in `items` with `resolvedSiteId`, so the picker selects it and loads its libraries.
It has no continuation; a root site lookup does not enumerate every site in the tenant.
Keywords keep the existing paginated search behavior; blank input searches with `*`.

Site URLs must not contain credentials, non-default ports, query strings or fragments.
Page (`.aspx`) and sharing links are not site URLs. Invalid URLs return
`400 invalid_site_url`; direct lookups never fall back to search after a Graph failure.
Only the validated hostname/path become Graph resource identifiers. The delegated token
is sent to Graph, never to a supplied site URL. Caller-supplied request destinations and
continuation URLs are not accepted. Mock mode supports the same lookup response without Azure.

Errors use Problem Details with a code in `title`, readable `detail`, and trace ID.
Partial activity coverage is returned as `warnings` and displayed in the reply's activity.

### Streaming chat

`POST /api/chat/stream` speaks the [AG-UI](https://docs.ag-ui.com/) protocol, so any AG-UI
client can consume it. The Knowledge Chat uses the official `@ag-ui/client` `HttpAgent`; the server
uses the `AGUI.Abstractions` event types. The JSON `/api/chat` endpoint is unchanged.

The request body is an AG-UI `RunAgentInput`:

| Field | Use |
| --- | --- |
| `threadId` | The conversation UUID |
| `runId` | Echoed in `RUN_STARTED`/`RUN_FINISHED`; a new ID is generated when it is missing |
| `messages` | Only the **last** message is read. It must be a `user` message with text content or `text` parts; image, audio, document or binary parts are rejected |
| `forwardedProps` | Optional `{sources?,agents?,timeZone?}` with the same meaning as on `/api/chat` |

`tools`, `context`, `state` and earlier `messages` are ignored: conversation history is kept
on the server and the client cannot add tools. A request that cannot be mapped fails with
Problem Details before the stream starts (`invalid_request`, `invalid_question` or
`unsupported_content`), as do the usual authorization and validation errors.

The response is `text/event-stream`, one JSON AG-UI event per `data` line:

| Event | Payload | Meaning |
| --- | --- | --- |
| `RUN_STARTED` | `threadId`, `runId` | The first event |
| `STEP_STARTED` / `STEP_FINISHED` | `stepName`, `metadata` = `{message,state:"started"\|"completed"\|"failed",tool?}` | A progress step such as "Reading your latest Inbox messages" or "Found 3 calendar events". Steps are always paired; a step started or finished without its counterpart gets a bookkeeping event without `metadata`, which clients should not display |
| `TEXT_MESSAGE_START` / `_CONTENT` / `_END` | `messageId`, `delta` | Answer text to append. Citation markers are already rendered as `[n]`, and a marker split across tokens is held back until it is complete |
| `RUN_FINISHED` | `result` = the `/api/chat` response | The validated final answer, warnings, citations and actions. It is authoritative and replaces the streamed text |
| `RUN_ERROR` | `message`, `code`, `metadata` = `{status,code,message,challenge?,retryAfter?}` | A failure after the stream started. A 401 with `challenge` asks the client to sign in again with those claims and retry |

Statuses name the step, quoting at most a shortened search query, agent ID or proposal
title; they never include Graph results or tool output. Because headers cannot change once the stream
has started, sign-in challenges and `Retry-After` values move into the `RUN_ERROR` metadata.
Streamed text is not final until `RUN_FINISHED` arrives, so the Knowledge Chat discards it on `RUN_ERROR`.
Mock mode streams the same events without Azure.
