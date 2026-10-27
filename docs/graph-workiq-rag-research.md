# Microsoft 365 Grounding (Graph) for App-Owned RAG — Research Findings

**Date compiled:** 2026-10-26
**Scope:** Primary-source research only (learn.microsoft.com, devblogs.microsoft.com/microsoft365dev). No repository code was read; no Azure resources or live tenant data were queried. This file is a **research artifact**, not an implementation plan — the parent integration plan and HTML mockups are owned elsewhere and are not modified by this document.
**Project scope note:** The target design calls **Microsoft Graph itself** (delegated, OBO-mediated) from an ASP.NET backend. **Work IQ API, the Microsoft 365 Copilot Retrieval API, the Copilot Chat API, and any other capability that requires a Microsoft 365 Copilot per-user license or PAYG metering are explicitly out of scope** for this design and are only discussed in Section 2 to explain *why* they are excluded and what that exclusion implies for relevance/ranking design. Everything else in this document (Graph Search API, Graph permissions, OBO, File Picker, Azure AI Search, Copilot Studio agent endpoint) is in scope because it is reachable with standard Graph/Entra permissions and does not require a Copilot license.
**Method:** All claims below are traced to a specific URL fetched or searched on 2026-10-26 via the Microsoft Learn MCP tools (`microsoft_docs_search` / `microsoft_docs_fetch`). Claims that could not be pinned to an exact primary-source sentence are explicitly labeled **[unresolved]** or **[inference]**. Treat dollar figures, preview/GA flags, and numeric ceilings as **volatile** — re-verify against the live page before relying on them in production or customer-facing material. Inline references use plain section pointers (e.g., "see Section 5") or direct URLs — no dangling bracket labels.

---

## 1. Executive summary

- **This design is Graph-only by policy**: all tenant data access goes through standard delegated Microsoft Graph calls made by the ASP.NET backend on behalf of the signed-in user via OBO. Work IQ, the Copilot Retrieval API, and the Copilot Chat API are **not used** — see Section 2 for why, and what it means for relevance/ranking (no semantic index; ranking comes from Graph's own relevance signals plus the app's own Azure AI Search hybrid/semantic ranking over the backend-owned index).
- **Exact permission/consent differences matter and are easy to get wrong**: `Chat.Read` (1:1/group chat), `ChannelMessage.Read.All` (team channel messages, admin-consent required, exists in both delegated and application form), and `ChatMessage.Read.Chat` (a **resource-specific-consent** permission scoped to one chat, not a tenant-wide delegated scope) are three materially different things, not interchangeable synonyms. `Mail.Read` and `Mail.ReadBasic` also differ in payload (full body+attachments vs. metadata-only). See the full matrix in Section 5.
- **File Picker v8 uses a SharePoint-audience token, not a Graph token**, for its core navigation operations — officially documented as non-interchangeable with Graph tokens. A custom Graph-backed picker is viable but requires broader scopes (`Sites.Read.All`/`Files.Read.All`) or per-site `Sites.Selected` grants, and reimplements navigation/search UI Microsoft otherwise hosts for free. See Section 6.
- **OBO is architecturally mandatory, not optional**, because Microsoft Entra ID access tokens are audience-bound (`aud` claim). The token the SPA sends to the backend must be audienced to the **backend's own exposed API**, not Graph; the backend then performs a *separate* on-behalf-of exchange using that inbound token as the assertion to mint a **new, independently issued** Graph-audienced token server-side. A Graph-audienced token obtained directly in the browser cannot simply be forwarded as the backend's own bearer token, and the backend cannot "redeem" its own inbound token a second time against Graph without going through this exchange. Claims-challenge/CAE responses from Graph during the OBO call must be propagated back to the original SPA, not resolved silently server-side. See Section 7 for the corrected mechanics.
- **Azure AI Search supports the proposed architecture (backend-owned chunking/embedding, push-model indexing, hybrid query, per-document delete, filter-based security trimming)** as a GA, documented pattern. Native document-level ACL enforcement (token-based, not string-filter-based) exists only for ADLS Gen2/Blob, Purview-labeled content, and SharePoint-indexer paths — **not** for a generic custom-pushed index, which must use the GA "security filters" (`$filter` on indexed security IDs) pattern instead. See Section 8. **Not all grounding has to flow through this index**: live/direct Graph reads (e.g., a just-in-time `GET /me/events` for "what's on my calendar today") can bypass the index entirely when freshness matters more than semantic recall; the index is for durable, searchable content, not the only permitted read path.
- **Copilot Studio agent endpoints require a dedicated Entra app registration and either the Direct Line API or the Microsoft 365 Agents SDK (`CopilotStudio.Copilots.Invoke` permission)** — these are two *different* authentication models, not variants of the same thing (Direct Line issues a conversation-scoped Bot Framework token that is not an Entra user sign-in token; the Agents SDK path is a genuine delegated Entra OBO flow). Both are distinct from a Foundry Agent Service/raw model endpoint, which has **no implicit tenant data access** but, contrary to an earlier draft of this document, is not categorically "session-less" — modern model/agent APIs can expose conversation and tool constructs. See Section 9.
- **Durable-job design implication (explicit project policy, not itself a primary-source claim)**: a background job must enqueue **already-downloaded, private, temporary content** (bytes or extracted text fetched synchronously while the user's delegated token is live) — never a bare `driveId`/`itemId`/message-ID reference (which still requires a future delegated call to resolve) and never a raw SharePoint/Graph download URL (which may be a pre-authorized bearer URL not bound to the caller's own token or session). This project also **deliberately chooses not to fall back to application-only permissions** when a user token expires mid-job — not because application permissions don't exist for these workloads (several do; see Section 5), but because silently escalating to tenant-wide app identity would be an unreviewed privilege change for a per-user grounding feature. See Section 10.

---

## 2. Out of scope: licensed Copilot / Work IQ APIs

This project does not call Work IQ, the Microsoft 365 Copilot Retrieval API, the Copilot Chat API, or the preview Microsoft 365 Copilot Search API. They are recorded here only to document *why* they were excluded and what that exclusion means for the design — not as capabilities to revisit or evaluate.

- **Why excluded — license/PAYG gate**: each of these is explicitly licensed, not a standard Graph permission:
  - Work IQ: auth is delegated-only via the single permission `WorkIQAgent.Ask`, and usage is billed through **Copilot Credits** — https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/permissions ; https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq
  - Copilot Retrieval API: part of the **Microsoft 365 Copilot APIs** family, which requires a Microsoft 365 Copilot license per user (or metered PAYG for a narrower source set) — https://learn.microsoft.com/microsoft-365/copilot/extensibility/copilot-apis-overview ; https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/ai-services/retrieval/overview
  - Copilot Chat API: same Microsoft 365 Copilot APIs family/licensing model as above — https://learn.microsoft.com/microsoft-365/copilot/extensibility/copilot-apis-overview
  - Microsoft 365 Copilot Search API (preview, OneDrive-only): *"The Search API is available at no extra cost to users with a Microsoft 365 Copilot license add-on. Support for users without a Microsoft 365 Copilot add-on license isn't currently available."* — https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/ai-services/search/overview#licensing
- **Why excluded — semantic index gate**: all four surfaces above are the only documented ways to reach the Microsoft 365 Copilot **semantic index** (a tenant/user-level AI-generated index built from Graph content for licensed Copilot subscribers). The semantic index itself is *"not independently exportable or directly queryable as a raw vector store"* outside those mediated APIs — https://learn.microsoft.com/en-us/microsoftsearch/semantic-index-for-copilot. A plain Graph caller without a Copilot license has no path to it at all.
- **What this means for the design**: there is **no semantic index available to this app**. Relevance/ranking for Graph-sourced content comes from two layers instead: (1) Microsoft Graph's own relevance/collaboration signals inside `/search/query` and `/me/people` (lexical + usage-pattern ranking, not embeddings-based semantic ranking — see Section 3), and (2) the app's **own** Azure AI Search hybrid/semantic-ranker query over the backend-owned push index (Section 8), which is the only semantic-similarity layer this design actually controls or can audit.
- **`/search/query` itself does not require a Microsoft 365 Copilot license** — confirmed by the absence of any licensing prerequisite on its permissions table: the official API reference for `POST /search/query` lists only standard Microsoft Graph delegated/application permissions (least-privileged `Mail.Read`; higher-privileged `Calendars.Read`, `Chat.Read`, `Files.Read.All`, `Sites.Read.All`, `ExternalItem.Read.All`, plus Microsoft-Search-specific scopes) with **no Microsoft 365 Copilot license or add-on mentioned anywhere on the page** — https://learn.microsoft.com/en-us/graph/api/search-query?view=graph-rest-1.0. This is a materially different licensing posture from the Copilot Search API in the bullet above, which explicitly gates on a Copilot add-on license on its own "Licensing" doc section. Do not confuse the two "search" surfaces by name alone.

---

## 3. Microsoft Graph Search API (`/search/query`) — in-scope relevance surface

- `POST /search/query` scopes a search by `entityTypes` and returns Graph-indexed content directly, ranked by Microsoft Search's own lexical/usage-signal relevance model — **this is not the Copilot semantic index** (Section 2) and does not require a Copilot license.
- **Official supported entity types and their required permission scopes** (verbatim table), per the canonical Search API overview — https://learn.microsoft.com/graph/api/resources/search-api-overview?view=graph-rest-1.0:

| EntityType | Permission scope required | Source | Comment |
|---|---|---|---|
| `acronym` | `Acronym.Read.All` | Microsoft Search | Org acronyms |
| `bookmark` | `Bookmark.Read.All` | Microsoft Search | Org bookmarks |
| `chatMessage` | `Chat.Read`, `Chat.ReadWrite`, `ChannelMessage.Read.All` | Teams | Teams messages |
| `message` | `Mail.Read`, `Mail.ReadWrite` | Exchange Online | Email messages |
| `event` | `Calendars.Read`, `Calendars.ReadWrite` | Exchange Online | Calendar events |
| `drive` | `Files.Read.All`, `Files.ReadWrite.All`, `Sites.Read.All`, `Sites.ReadWrite.All` | SharePoint | Document libraries |
| `driveItem` | `Files.Read.All`, `Files.ReadWrite.All`, `Sites.Read.All`, `Sites.ReadWrite.All` | SharePoint/OneDrive | Files, folders, pages, news |
| `list` / `listItem` | `Sites.Read.All`, `Sites.ReadWrite.All` | SharePoint/OneDrive | Lists and list items |
| `qna` | `QnA.Read.All` | Microsoft Search | Q&A |
| `site` | `Sites.Read.All`, `Sites.ReadWrite.All` | SharePoint | Sites |
| `externalItem` | (connector-specific) | Connectors | Referenced for `fields`/field-selection support on the same overview page; `ExternalItem.Read.All` is the connector-content permission used elsewhere in this project's own permission matrix (Section 5) |

- **"Calendar" is not itself a queryable entity type — `event` is.** `calendarView` (`GET /me/calendarView` and the `/calendar/calendarView` family) is a **separate, non-Search Graph endpoint** for expanding recurring events over a date range; it has no relationship to `/search/query` and is not subject to the Search API's entity-type/permission table above. An earlier draft of this document loosely referred to "calendar" as part of what Graph Search indexes; the precise statement is: Graph Search can query the `event` entity type (permission `Calendars.Read`/`Calendars.ReadWrite`), and `calendarView` is a different API entirely.
- **`person` is a real, sortable Search result type but is conspicuously absent from the official entity-type permission table above.** The same overview page states sort defaults — *"All SharePoint, OneDrive, person and connector types are sorted by relevance"* — confirming `person` is a valid `entityTypes` value, but **no permission scope is listed for it anywhere on that page**, and the dedicated `/search/query` API reference page's own permissions table (Section 2) also does not mention `People.Read`/`People.Read.All` at all. **This project's earlier assumption that `entityTypes:["person"]` requires the same `People.Read`/`People.Read.All` scopes as `/me/people` is unverified** — treat it as an open gate (Section 12, gate re-added) rather than a confirmed fact; re-check the live `search-concept-person` page and/or file a support/forum question before shipping code that depends on the exact scope.
- **`/search/query` with `entityTypes:["chatMessage"]`**: supported, but *"can't be used with other entity types at this time"* and only surfaces **the signed-in user's own messages/threads** — full chat-thread context still requires `GET /chats/{id}/messages` directly. — https://learn.microsoft.com/graph/search-concept-chat-messages
- **Paging**: default page size 25, max 1000; `event`/`message` entities have additional `from`/`size` constraints per the same overview page.

---

## 4. Foundry model endpoint — no implicit tenant access

- **A raw Azure AI Foundry / Azure OpenAI model endpoint has no implicit tenant data access.** Grounding is always an explicit, bolt-on step the calling application configures (Azure OpenAI "On Your Data," or the Foundry Agents service with an attached Azure AI Search index) — https://learn.microsoft.com/azure/ai-foundry/openai/concepts/use-your-data ; https://learn.microsoft.com/azure/foundry-classic/openai/faq#how-do-i-ground-a-model-on-my-own-data. **[inference]**: no single page states the literal phrase "model endpoints have no tenant awareness" — this is a strongly supported synthesis, not a verbatim quote.
- **This claim is narrower than "no session/conversation concept" (see Section 9's correction)**: the absence of implicit tenant access is well supported; a blanket claim that model/agent endpoints have no session, conversation, or tool concept at all is not, and has been removed from this document.
- **Recommendation implication**: using a direct Foundry **Model** endpoint with a backend-owned Azure AI Search index (the stated architecture) is the officially supported and only way to get tenant-grounded answers out of a bare model call for this design — there is no "ambient" tenant context to rely on, by design.

---

## 5. Exact Microsoft Graph delegated permission / admin-consent matrix

All scope names, identifiers, `AdminConsentRequired` flags, and descriptions below are verified against **Microsoft Graph permissions reference**, https://learn.microsoft.com/en-us/graph/permissions-reference, plus the API-specific permission tables cited per row.

### 5.1 Documents (SharePoint/OneDrive)

| Scope | Type | Admin consent | Description (verbatim/paraphrased) | Notes |
|---|---|---|---|---|
| `Files.Read` | Delegated | No | Read the signed-in user's files | Least-privileged default |
| `Files.Read.All` | Delegated | No | Read all files the signed-in user can access | **Application** variant of the same name *does* require admin consent — don't conflate delegated vs. application rows of the same display name |
| `Sites.Read.All` | Delegated | No | Read documents/list items in all site collections, on behalf of the signed-in user | Application variant requires admin consent |
| `Sites.Selected` | Delegated/Application | Yes (app registration consent) + per-site `POST /sites/{siteId}/permissions` grant | Least-privilege alternative to tenant-wide `Sites.Read.All`; access is per-site, explicitly granted | https://learn.microsoft.com/graph/permissions-selected-overview |

### 5.2 Mail — `Mail.Read` vs `Mail.ReadBasic` (materially different payloads)

| Scope | Type | Admin consent | What it returns |
|---|---|---|---|
| `Mail.Read` | Delegated | No | Full mailbox read: body, previewBody, attachments, all properties |
| `Mail.ReadBasic` | Delegated | No | *"Read email in the signed-in user's mailbox **except** body, previewBody, attachments, and extended properties."* Metadata/headers only — **no body or attachments**. Consentable for personal Microsoft accounts too. | id `a4b8392a-d8d1-4954-a029-8e668a39a170` |
| `Mail.ReadBasic.All` | Application only (no delegated form) | **Yes** | Same "basic" shape, but app-only, all mailboxes |
| `Mail.ReadBasic.Shared` | Delegated | No | Basic-only read of mail the user can access, including shared mailboxes |

Source: https://learn.microsoft.com/en-us/graph/permissions-reference (sections "Mail.Read", "Mail.ReadBasic", "Mail.ReadBasic.All", "Mail.ReadBasic.Shared"). **Implication for chunking/embedding**: if the RAG pipeline needs message **bodies** (the actual content to embed), `Mail.ReadBasic` is insufficient — it must request `Mail.Read` (or `Mail.Read.Shared`/`Mail.ReadWrite` if write/shared access is also needed). `Mail.ReadBasic` is only useful for a lightweight metadata/triage pass, not content extraction.

### 5.3 Calendar

| Scope | Type | Admin consent | Notes |
|---|---|---|---|
| `Calendars.Read` | Delegated | No | Read events in user calendars; also the permission scope for the `event` Search entity type (Section 3) |

Source: permissions-reference, "Calendars.Read" section. Note again: this scope governs the `event` entity and the `calendarView` endpoint, not a "calendar" Search entity (none exists).

### 5.4 Teams — three distinct, non-interchangeable permissions

This is the area most prone to confusion; the three names function very differently:

| Scope | Type | Admin consent | Scope of access | Consent model |
|---|---|---|---|---|
| **`Chat.Read`** | Delegated | **No** | Read **1:1 or group chat** threads the signed-in user is a member of | Standard tenant-wide delegated consent |
| **`ChannelMessage.Read.All`** | Delegated | **Yes** | Read **all channel messages** in Microsoft Teams (team/channel, not 1:1/group chat) — classified sensitive | Standard tenant-wide delegated consent; **application variant also exists and also requires admin consent** — see policy note below |
| **`ChatMessage.Read.Chat`** | **Resource-specific consent (RSC)** — not a standard tenant-wide delegated/application scope | N/A (consented per-resource, not via tenant admin consent) | *"Read this chat's messages"* — scoped to **one specific chat resource only**, consented by the chat owner/members adding the app to that chat, not by a tenant admin | Uses Teams RSC model (https://aka.ms/teams-rsc), fundamentally different consent flow from `Chat.Read`/`ChannelMessage.Read.All` |
| `ChannelMessage.Read.Group` | Application, **resource-specific consent** | N/A (RSC) | Channel-message read scoped to one specific team/group via RSC, app-only | Distinct from `ChannelMessage.Read.All`; "uses resource-specific consent" per official note |
| `Chat.Read.All` | **Application only** (no delegated form) | Yes | Read all 1:1/group chats tenant-wide, app-only | No delegated equivalent exists for reading *all* chats as an app |

Verbatim official note: *"The `ChannelMessage.Read.Group` and `ChatMessage.Read.Chat` permissions use [resource-specific consent](https://aka.ms/teams-rsc)."* — https://learn.microsoft.com/graph/api/chatmessage-get?view=graph-rest-1.0 and https://learn.microsoft.com/graph/api/channel-list-messages (permissions table).

**Practical implication for this project**: for a conventional tenant-wide-consent backend reading a user's own chats and their team channels, the correct pair is **`Chat.Read`** (no admin consent) **+ `ChannelMessage.Read.All`** (admin consent required, delegated form). `ChatMessage.Read.Chat`/`ChannelMessage.Read.Group` (RSC) are a *different* model intended for apps installed into a specific Teams chat/team by its members — not something a tenant admin grants once for the whole org, and not an appropriate substitute for `Chat.Read` in a general-purpose grounding backend.

**Policy note (not a platform limitation) — application permissions do exist**: `ChannelMessage.Read.All` and `People.Read.All` (Section 5.5) both have documented **application** forms in the Graph permissions reference. Microsoft Entra ID does not prohibit granting them. **This project chooses not to use them as a silent fallback** when a user's delegated token expires mid-job (Section 10) — that is a deliberate design decision to avoid an unreviewed, tenant-wide privilege escalation inside a per-user grounding feature, not a statement that app-only access is technically unavailable for these workloads. Any future decision to use the application form of either permission should be a conscious, admin-reviewed change, not an automatic retry path.

**Teams read throttling** (verified, https://learn.microsoft.com/graph/throttling-limits#microsoft-teams-service-limits):
- `GET /teams/{id}/channels/{id}/messages`: 200 rps/app, 20 rps/app/tenant, **1 rps per channel**.
- `GET /chats/{id}/messages`: 200 rps/app, 20 rps/app/tenant, **1 rps per chat**.
- `GET /chats` / `/me/chats`: 200/20 rps as above, 1 rps per user.
- Sustained traffic should target ~83% of listed limits; use exponential backoff.

**`/search/query` with `entityTypes:["chatMessage"]`**: see Section 3 — supported, own-messages-only, can't combine with other entity types.

**Export/compliance APIs are a separate, more heavily gated surface** — not needed for normal RAG reads: `getAllRetainedMessages` requires **application-only** permissions (`Chat.Read.All`, `ChannelMessage.Read.All`, etc.), a Teams license on exported users, and (for soft-deleted/retained content beyond 21 days) an active Teams retention policy; it explicitly does **not** require Purview DLP licensing. — https://learn.microsoft.com/microsoftteams/export-teams-content. **[unresolved]**: no primary-source confirmation was found that a Communication Compliance or Teams Premium add-on license gates the ordinary delegated `Chat.Read`/`ChannelMessage.Read.All` read path — do not assume this requirement without further verification.

### 5.5 People

| Scope | Type | Admin consent | Notes |
|---|---|---|---|
| `People.Read` | Delegated | No | *"Read a ranked list of relevant people of the signed-in user."* Confirmed scope for `/me/people` (and `/users/{id}/people`). Personal MS accounts supported too |
| `People.Read.All` | Delegated + Application | **Yes** | Broader — relevant-people lists for *other* users in the org. Confirmed scope for `/me/people`/`/users/{id}/people`; **application form exists** (see Section 5.4 policy note on deliberately not using app-only as a silent fallback) |

**Critical limit**: the **People API is officially in maintenance mode**. *"For new development, we recommend using the Microsoft Search API (`POST /search/query` with `entityTypes: ["person"]`) instead of the People API... The People API is in maintenance mode."* — https://learn.microsoft.com/graph/api/user-list-people?view=graph-rest-1.0.

**Corrected — do not assume identical permissions across the two paths.** `People.Read`/`People.Read.All` are confirmed, API-specific permissions for `/me/people` (the classic People API), sourced directly from that API's own reference page. For `/search/query` with `entityTypes:["person"]`, the **official Search API entity-type/permission table does not list `person` at all**, and the dedicated `person` Search Search-concept page (`search-concept-person`) does not publish a permissions table either — https://learn.microsoft.com/graph/api/resources/search-api-overview?view=graph-rest-1.0 ; https://learn.microsoft.com/en-us/graph/search-concept-person. An earlier draft of this document asserted "both paths require the same `People.Read`/`People.Read.All` scopes" — **that assertion is unverified and should not be relied on**. Treat the exact permission requirement for `entityTypes:["person"]` as an open item (Section 12) until confirmed by a live tenant test or a Microsoft support/forum answer; do not hard-code a scope assumption into the consent-request flow based on this document alone.

**Relevance-model mechanics**: a person is only surfaced if there's a **public relationship** — org-chart link (manager/report/peer) or shared membership in a group/distribution list with **fewer than 30 members** — ranked by public interaction signals within that group. People in larger groups, with no public relationship, or external/cross-tenant contacts **never appear**, regardless of actual collaboration intensity. — https://learn.microsoft.com/graph/people-insights-overview

**Directory vs. relevance** — `/users`, `/me/directReports`, etc. are flat, unranked directory enumerations governed by directory-read permissions (e.g., `User.Read.All`), structurally different from People/person-entity relevance ranking. **[inference/partially synthesized]**: no single Microsoft page explicitly contrasts `/users` and `/me/people` side-by-side in this exact framing; this comparison is assembled from the People API docs plus general `/users` list semantics, not one verbatim source.

### 5.6 Reference table — Graph permissions

| Scope | Workload | Admin consent | Delegated/App |
|---|---|---|---|
| `Files.Read` / `Files.Read.All` | Documents | No (delegated); App variant Yes | Both |
| `Sites.Read.All` | Documents (SharePoint) | No (delegated); App variant Yes | Both |
| `Sites.Selected` | Documents (least-privilege, per-site) | Yes (app reg) + per-site grant | Both |
| `Mail.Read` | Mail (full content) | No | Delegated |
| `Mail.ReadBasic` | Mail (metadata only) | No | Delegated |
| `Mail.ReadBasic.All` | Mail (metadata only, all mailboxes) | Yes | Application only |
| `Calendars.Read` | Calendar / `event` Search entity | No | Delegated |
| `Chat.Read` | Teams 1:1/group chat | No | Delegated |
| `Chat.Read.All` | Teams all chats (app-wide) | Yes | Application only |
| `ChannelMessage.Read.All` | Teams channel messages | **Yes** | Delegated + Application (app-only not used as fallback — Section 5.4) |
| `ChannelMessage.Read.Group` | Teams channel (RSC, per-team) | N/A (RSC) | Application, resource-specific |
| `ChatMessage.Read.Chat` | Teams single chat (RSC, per-chat) | N/A (RSC) | Resource-specific |
| `People.Read` | People (own relevance list, `/me/people`) | No | Delegated |
| `People.Read.All` | People (others' relevance lists, `/me/people` path) | Yes | Delegated + Application (app-only not used as fallback — Section 5.4) |

---

## 6. File picker and frontend selection — official picker vs. custom Graph picker

- **File Picker v8 ("File and folder picker")** is a Microsoft-hosted iframe/popup control (`postMessage` protocol), not an npm SDK component — https://learn.microsoft.com/onedrive/developer/controls/file-pickers/?view=odsp-graph-online
- **Token audience — critical finding**: *"Currently the control relies on SharePoint tokens and not Graph, so you will need to ensure your resource is correct and you cannot reuse tokens for Graph calls."* Tokens are requested against the **SharePoint resource** (e.g., `https://{tenant}.sharepoint.com` or `https://{tenant}-my.sharepoint.com`) via `{resource}/.default`, not `https://graph.microsoft.com`. The v8 schema does expose independent `authentication.tokens.graph`/`.sharePoint`/`.substrate` flags (both Graph and SharePoint default `true`), so the host app must be able to answer token requests for **both** audiences, but initiating navigation requires the SharePoint-audience token specifically. — same URL; schema at https://learn.microsoft.com/onedrive/developer/controls/file-pickers/v8-schema?view=odsp-graph-online
- **Required scopes** (verbatim "Required Setup"): Graph delegated `Files.Read.All`, `Sites.Read.All`, `User.Read`; SharePoint delegated `AllSites.Read`, `MyFiles.Read` (add `*.ReadWrite*` variants for upload). **The picker always operates using delegated permissions** — *"can only ever access file and folders to which the current user already has access"* — no application-permission path exists for the picker.
- **Stated minimum/least-privilege**: *"At a minimum you must request the SharePoint `MyFiles.Read` permission to read files from a user's OneDrive and SharePoint sites."* Tenant-wide browsing works through the picker's own SharePoint-native navigation using this single scope; `Sites.Read.All`/`AllSites.Read` is presented as the broader "everything the user can see" tier, not a documented narrower alternative.
- **Output / follow-up audience mismatch confirmed**: the picker returns `{id, parentReference.driveId, @sharePoint.endpoint}`; the docs explicitly warn *"you will need to include a valid token with appropriate rights to read the file"* for the follow-up call — i.e., the picker's SharePoint token and the subsequent Graph-audience token used to actually fetch `/drives/{driveId}/items/{id}` are **separate token-acquisition events**, consistent with Section 7's OBO/audience findings.
- **Custom Graph-backed picker** (no single official build-vs-buy comparison page found — this section is explicitly labeled synthesis): would need broader scopes (`Sites.Read.All`/`Files.Read.All`) or per-site `Sites.Selected` grants (https://learn.microsoft.com/graph/permissions-selected-overview) to enumerate arbitrary sites, since Graph has no single "show everything this user can reach" call; search-as-you-type would be built against `/search/query`, which the official picker provides natively via its `search.enabled` config flag. **[inference]**: trade-off is more implementation/maintenance effort for a custom picker, in exchange for tighter per-site scoping (Selected scopes) than the official picker documents using.

---

## 7. OBO flow, audience, and claims challenges — confirmed architectural requirements (corrected)

- **OBO flow**: a middle-tier (backend) Web API uses the inbound user token (`aud` = **the backend's own App ID URI** — this is the assertion's audience, and it must match the middle tier, not Graph) as an `assertion` in a `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` request with `requested_token_use=on_behalf_of`, trading it for a **new, separately-issued** token whose `aud` is the *target* resource (e.g., `aud=https://graph.microsoft.com`). — https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow
- **Audience restriction, verbatim**: *"Applications can't redeem a token for a different app (for example, if a client sends an API a token meant for Microsoft Graph, the API can't redeem it using OBO. It should instead reject the token)."* — same URL, "Middle-tier access token request." And: *"DO NOT send access tokens that were issued to the middle tier to anywhere except the intended audience for the token."* — same section.
- **Corrected mechanics (earlier draft had the audience direction backwards)**: the assertion presented *to* the OBO token endpoint must already be audienced to the **middle tier's own API** (not Graph) — that's precisely why the SPA must acquire its token for the backend's own exposed API and never for Graph directly. The OBO exchange then *produces* a brand-new token, separately audienced to Graph, which the backend uses for the downstream Graph call. The two tokens (inbound assertion vs. outbound Graph token) are different tokens with different audiences; OBO is the mechanism that bridges them, not a reinterpretation of the same token's `aud` claim.
- **This directly rules out forwarding a browser-obtained Graph token as the backend API's own bearer token**: the backend's auth middleware validates the inbound token's `aud` against its own Application ID URI; a Graph-audience token presented to the backend would fail that validation outright (wrong audience), and a backend-audience token presented directly to Graph would equally fail Graph's own audience check. The only correct shape is: SPA acquires a token for the **backend's own API** (not Graph) -> backend validates it -> backend performs OBO using that token as the assertion to mint a **separate** Graph-audience token server-side -> backend calls Graph with that new token.
- **Claims challenges / CAE**: Graph may return `401` + `WWW-Authenticate: ******"insufficient_claims", claims="<base64>"` when the OBO exchange succeeds structurally but Graph itself requires step-up (e.g., MFA/Conditional Access) that the original user session didn't satisfy. The documented .NET pattern (Microsoft.Identity.Web / MSAL.NET) is: catch `MsalUiRequiredException.Claims` from `AcquireTokenOnBehalfOf`, and **propagate a 401 + claims challenge back to the SPA** — the backend cannot silently resolve this itself; only the original authenticating client can prompt the user for step-up. — https://learn.microsoft.com/entra/identity-platform/claims-challenge ; https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/web-apps-apis/on-behalf-of-flow#handling-multi-factor-auth-mfa-conditional-access-and-incremental-consent ; CAE mechanics at https://learn.microsoft.com/entra/identity-platform/app-resilience-continuous-access-evaluation (requires declaring the `"cp1"` client capability to receive CAE-aware claims challenges at all).
- **Per-resource, per-scope tokens (incremental consent)**: *"The token received will contain all the previously consented scopes (for that specific resource)"* — i.e., each Graph resource/scope set is tokenized independently; a token scoped for `Mail.Read` is not automatically valid for `Sites.Read.All` calls, and each new scope may require its own (silent or interactive) acquisition. — https://learn.microsoft.com/entra/msal/javascript/browser/resources-and-scopes#dynamic-scopes-and-incremental-consent ; https://learn.microsoft.com/entra/identity-platform/consent-types-developer#incremental-and-dynamic-user-consent
- **OBO works only for user principals**: a client requesting app-only access must use client-credentials flow instead; SPAs (public clients) cannot safely perform OBO themselves and must delegate the exchange to a confidential-client middle tier. — https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#client-limitations

---

## 8. Azure AI Search — supports the proposed backend-owned RAG architecture

- **Push model, GA**: `POST /indexes/{index}/docs/index` supports `upload`/`merge`/`mergeOrUpload`/`delete` actions, batches up to 1,000 docs/16MB. Azure AI Search does **not** generate embeddings for pushed documents outside the separate, optional integrated-vectorization/skillset pipeline — for the push path, **the caller must supply pre-computed vectors**, directly matching a "backend owns chunking + embedding" design. — https://learn.microsoft.com/azure/search/search-what-is-data-import#pushing-data-to-an-index
- **Deletion/incremental update, GA**: `delete` action is **idempotent** (deleting an absent key still returns HTTP 200) — directly supports "user revokes consent -> purge all their indexed chunks" as a straightforward batched delete-by-key operation. `merge` fails (404) on a non-existent key and **replaces entire collection-typed fields** rather than patching elements — relevant if security-trimming group-ID fields are stored as collections and updated incrementally.
- **Hybrid search**: vector + BM25 keyword + optional semantic ranker (L2 rerank) in one query (`queryType=semantic`). This semantic ranker is **this project's own**, separately licensed/tiered Azure AI Search feature — distinct from, and unrelated to, the Microsoft 365 Copilot semantic index discussed in Section 2.
- **Security trimming for a custom-pushed index**: GA pattern is to index a filterable security-identifier field (e.g., `userId`, `conversationId`, optionally `group_ids`) and apply `$filter` at query time — https://learn.microsoft.com/azure/search/search-security-trimming-for-azure-search. This is a string/ID filter enforced by the query, not a token-based native ACL check.
- **Native document-level ACL enforcement** (token-based, checked against Entra group membership at query time) exists only for specific first-party connectors — ADLS Gen2/Blob with indexer-based ACL ingestion, Purview-sensitivity-labeled content, and the SharePoint indexer path — https://learn.microsoft.com/azure/search/search-document-level-access-overview. **A generic custom-pushed index (this project's design) is not covered by that native ACL feature** and must rely on the `$filter`-based security-trimming pattern above.
- **Vector index basics**: fields marked `"retrievable": false` can still be used for filtering/vector search without being returned in results; dimension count and distance metric are configured per vector field at index-creation time — https://learn.microsoft.com/azure/search/vector-search-overview ; https://learn.microsoft.com/azure/search/vector-search-how-to-create-index. **[unresolved]**: the exact current maximum `dimensions` ceiling and the complete current list of supported distance metrics were not pinned to a specific number/enum this session — re-fetch the live page before hard-coding either value into index schema code.

---

## 9. Copilot Studio agent endpoint vs. Foundry model endpoint

- **Copilot Studio programmatic invocation has two genuinely different authentication models — do not conflate them**:
  - **Direct Line API**: Channels -> Mobile app -> copy per-agent **Token Endpoint** -> `GET` it for a **conversation-scoped Bot Framework token** (explicitly *not* an end-user sign-in token — it authorizes a conversation session with the agent, not an Entra-authenticated identity) -> use with the standard Bot Framework Direct Line API (start conversation / send activity / receive activity). This token carries **no Entra user claims** and cannot be used for OBO or Graph calls. — https://learn.microsoft.com/microsoft-copilot-studio/publication-connect-bot-to-custom-application
  - **Microsoft 365 Agents SDK ("Direct-to-Engine")**: this *is* a genuine Entra delegated flow — it requires an Entra app registration with the Power Platform API delegated permission **`CopilotStudio.Copilots.Invoke`**, plus `EnvironmentId`/`SchemaName`/`TenantId`/`AppClientId`; the caller acquires a real delegated Entra access token for the signed-in user and invokes `start_conversation()`/`startConversationStreaming` with it. — https://learn.microsoft.com/microsoft-copilot-studio/publication-integrate-web-or-native-app-m365-agents-sdk (**[flag: snippet-level only this session, recommend re-fetch before implementation]**).
  - **Practical implication**: if the design needs the agent call to carry genuine per-user Entra identity/claims (consistent with the rest of this project's OBO-based architecture), the **Agents SDK path is the only one of the two that provides that** — Direct Line's conversation token is a different, non-Entra authorization artifact and should not be described or implemented as equivalent to the project's OBO user-identity flow.
- **Custom grounding in Copilot Studio**: native **knowledge sources** (SharePoint/Dataverse/OneDrive/public web) are Microsoft-managed retrieval with per-user security trimming inherited from the source system (SharePoint knowledge source runs "Authenticate with Microsoft" as the signed-in user, capped at **top 3 results**, site-plus-subpaths scope only); **custom grounding via Tools/Actions** (custom connector, Power Automate/Agent flow) lets the caller's own backend supply retrieved RAG content directly into the conversation, **capped at 500 KB per tool response** (HTTP 400 if exceeded). This is the documented mechanism matching the stated architecture ("custom grounding via tools/input vs native SharePoint knowledge"). **[flag: exact canonical URLs/dates for the SharePoint-knowledge-source limits and the 500 KB tool cap were snippet-level only this session — re-fetch `knowledge-add-sharepoint` and the custom-connector/tools article to pin exact citations before using these numbers in customer-facing material]**.
- **Licensing**: Copilot Studio agents have their own billing model, separate from the Microsoft 365 Copilot user license. Copilot Studio itself is **not** one of the Section 2 out-of-scope surfaces, because the existing adapter path already depends on it. Confirm current billing terms in [Copilot Studio billing and licensing](https://learn.microsoft.com/microsoft-copilot-studio/billing-licensing) before estimating costs; this report deliberately records no prices.
- **Foundry Model endpoint, by contrast**: has **no implicit tenant data access of any kind** (Section 4) — grounding is 100% the calling application's responsibility via explicit RAG (e.g., the stated Azure AI Search push-index architecture). **Correction to an earlier draft**: this document previously claimed Foundry model endpoints have "no conversation/session concept, no native knowledge source" — that overstates the case. Modern model/agent APIs (e.g., Responses-style/conversation APIs) can expose conversation state and tool-calling constructs; what remains true and load-bearing for this design is narrower: **no implicit tenant authorization is ever granted by the platform** — any conversation/tool construct the model API offers still requires the calling app to supply its own access-controlled data, exactly as Section 4 describes. This is the structural reason the "Foundry model + app-owned Search index" path and the "Copilot Studio agent + native/custom grounding" path are architecturally distinct choices, not two configurations of the same thing — not because one "has sessions" and the other doesn't.

---

## 10. Durable-job design implication (explicit project policy, not a standalone primary-source claim)

Given:
- File Picker v8 tokens and Graph tokens are separate, short-lived, audience-specific, delegated-only artifacts (Sections 6-7);
- OBO-derived Graph tokens are intended "only for use by the middle tier to communicate with the intended audience endpoint" and must not be persisted/forwarded outside that immediate call context (Section 7);
- Several of the permissions in this project's scope have documented application forms (`ChannelMessage.Read.All`, `People.Read.All` — Section 5), but this project deliberately does not use them as a silent fallback (Section 5.4 policy note) — so a durable job cannot assume it can "step down" to app identity if the user's delegated token expires mid-job;

a durable background pipeline (chunk -> embed -> index) **must enqueue already-downloaded, private, temporary content**, captured **synchronously while the user's OBO-derived token is live**:

- **Correct unit of work**: the actual file bytes (or already-extracted text) fetched from Graph/SharePoint during the original authorized request, written to short-lived private storage (or an in-memory/queue payload) that the async worker can read without needing any further delegated authorization.
- **Not sufficient on their own**: a bare `driveId`+`itemId` (or message/event/chat ID) reference — these still require a **future** delegated Graph call to resolve content, and by the time an async worker picks up the job, the original user token may have expired, been revoked, or be unavailable, with no app-only fallback permitted (see above) to resolve them instead.
- **Not safe to queue**: a raw SharePoint/Graph **download URL** — these may be **pre-authorized bearer URLs that are not necessarily bound to the caller's own token or session**; treating a download URL as equivalent to "an authorized reference the worker can always redeem" is an unverified assumption about its binding/expiry semantics and should not be relied on for a durable queue.
- **Not safe to queue**: a raw user bearer assertion/token itself (expires, may be revoked, must not be persisted/forwarded per Section 7's OBO guidance).
- **On token expiry mid-job**: the job must **fail-and-retry with a freshly re-acquired delegated/OBO token** (e.g., via a refresh-token-backed silent reacquisition at enqueue time, or by re-prompting the user), not silently escalate to an application-only identity.

This is an architectural inference drawn directly from the audience/lifetime/delegated-only constraints documented in Sections 5-7, not an independent Microsoft "how to build durable Graph jobs" citation — no such prescriptive doc was located in this research pass, and none was searched for beyond what was needed to support this inference.

---

## 11. Recommendations (tied to the stated target architecture)

1. **Keep the backend as the sole owner of chunking, embedding, and Azure AI Search push-indexing** — fully supported, GA (Section 8). Index `userId`/`conversationId` (and, if sharing is ever introduced, `group_ids`) as filterable security fields and apply `$filter` at query time (Section 8) to get "selected docs private per user+conversation" plus a clear path to broaden later. Recall that not all grounding must route through this index — live/direct Graph reads remain valid for freshness-sensitive queries (Section 1).
2. **Use OBO from the ASP.NET backend for all Graph reads**, with the frontend acquiring a token only for the backend's own exposed API (never Graph) — architecturally required, not optional (Section 7). Implement claims-challenge propagation (401 + `WWW-Authenticate`) back to the SPA rather than attempting silent server-side MFA resolution.
3. **Request exact, differentiated Graph scopes per workload**, not broad umbrella guesses: `Files.Read.All`+`Sites.Read.All` (docs), `Mail.Read` (if message **bodies** are needed — not `Mail.ReadBasic`), `Calendars.Read`, `Chat.Read` + `ChannelMessage.Read.All` (Teams — admin consent required for the channel scope specifically), `People.Read`/`People.Read.All` for `/me/people` **or** verify the actual required scope for `/search/query entityTypes:["person"]` before depending on it (Section 5.5 — this is an open gate, not confirmed identical).
4. **Use a custom Graph-backed picker** (per the stated recommendation) with `Sites.Read.All`/`Files.Read.All` or, for tighter scoping, `Sites.Selected` per-site grants — understanding this trades implementation effort (site/library enumeration, search-as-you-type via `/search/query`) for finer-grained, auditable access compared to the official SharePoint-audience-token picker (Section 6).
5. **Treat the Foundry Model endpoint as having zero implicit tenant context** — all tenant grounding must flow through either the app-owned Azure AI Search index or a direct, explicitly-authorized Graph call; do not expect any ambient Graph/semantic-index awareness from the model call itself (Section 4).
6. **If/when the Copilot Studio agent endpoint path is exercised** (per the "Foundry MODEL vs Copilot Studio AGENT" choice), use custom Tools/Actions to feed the same backend-retrieved RAG content into the agent rather than relying on native SharePoint knowledge sources, to keep grounding logic, security trimming, and permission-shaping centralized in the one backend-owned index; if genuine per-user Entra identity needs to reach the agent call, use the Microsoft 365 Agents SDK path, not Direct Line (Section 9).
7. **Design durable jobs to resolve and fetch authorized content synchronously at enqueue time, persisting only already-downloaded private bytes/text** — never persist raw bearer tokens, bare drive/item/message ID references alone, or download URLs as the unit of queued work; do not build a silent application-only fallback path for any workload in scope (Section 10).
8. **Do not evaluate or build against Work IQ, the Copilot Retrieval API, or the Copilot Chat API** — they are out of scope by license/PAYG requirement (Section 2). If relevance quality from Graph Search + the app's own Azure AI Search ranking proves insufficient in practice, treat adopting a licensed Copilot surface as a **separate, licensing-gated decision** requiring explicit stakeholder sign-off — not a drop-in technical upgrade to this design.

---

## 12. Explicit unresolved / verification gates (do not treat as settled without a follow-up fetch)

| # | Gate | What to re-verify before relying on it |
|---|---|---|
| 1 | Exact permission scope required for `/search/query` with `entityTypes:["person"]` | Neither the Search API overview's entity-type table nor the `search-concept-person` page lists a permission for `person`; do not assume `People.Read`/`People.Read.All` applies without independent confirmation (live tenant test or Microsoft support answer) — Section 3/5.5 |
| 2 | Communication Compliance / Teams Premium license requirement for ordinary `Chat.Read`/`ChannelMessage.Read.All` reads | Not found in primary docs this session — only confirmed for the separate Export API's Teams-license requirement; do not assume a stricter license gate on ordinary reads without further search |
| 3 | M365 Agents SDK (`CopilotStudio.Copilots.Invoke`) exact parameter names/flow | Snippet-level only — re-fetch `publication-integrate-web-or-native-app-m365-agents-sdk` in full |
| 4 | SharePoint knowledge-source limits (top-3 results, 10-list cap, 35k-row threshold) and 500 KB custom-tool response cap | Snippet-level only — re-fetch the canonical Copilot Studio knowledge-source and custom-connector/tools articles for exact URL/title/date |
| 5 | Copilot Studio Credit/message pricing figures | Volatile, recently renamed (Sept 2025) — use the official Licensing Guide PDF, not these cached numbers |
| 6 | Exact `vectorSearchDimensions` maximum ceiling and complete vector distance-metric list | Not pinned to a specific number/complete list this session — re-fetch `vector-search-overview` / `vector-search-how-to-create-index` |
| 7 | Document-level access control (native ACL features) current GA/preview status | Preview flags for Azure AI Search features change frequently — re-fetch `search-document-level-access-overview` immediately before any compliance-sensitive use |
| 8 | File Picker v8 `search.enabled` tenant-wide search scope/behavior detail, and any max-selection-size limit | Page fetch was truncated before full detail; no official max-selection-size was found at all |
| 9 | `externalItem` entity-type permission detail (Section 3) | The Search API overview page references `externalItem` for field-selection but the exact permission row for it was not located in the entity-type table excerpt reviewed this session — confirm against `ExternalItem.Read.All` in the full permissions reference before relying on connector search |

---

## Reference table (all sources cited above)

| Topic | URL |
|---|---|
| Work IQ overview (out-of-scope context, Section 2) | https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq |
| Work IQ API permissions reference (out-of-scope context, Section 2) | https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/work-iq/permissions |
| Microsoft 365 Copilot semantic index (out-of-scope context, Section 2) | https://learn.microsoft.com/en-us/microsoftsearch/semantic-index-for-copilot |
| Microsoft 365 Copilot APIs overview (Retrieval/Chat API licensing, Section 2) | https://learn.microsoft.com/microsoft-365/copilot/extensibility/copilot-apis-overview |
| Copilot Retrieval API overview/licensing (out-of-scope context, Section 2) | https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/ai-services/retrieval/overview |
| Microsoft 365 Copilot Search API (preview) — licensing section | https://learn.microsoft.com/microsoft-365/copilot/extensibility/api/ai-services/search/overview#licensing |
| Graph `/search/query` API reference (permissions table, Section 2/3) | https://learn.microsoft.com/en-us/graph/api/search-query?view=graph-rest-1.0 |
| Search API overview (entity types + permission table, Section 3) | https://learn.microsoft.com/graph/api/resources/search-api-overview?view=graph-rest-1.0 |
| Search chat messages concept | https://learn.microsoft.com/graph/search-concept-chat-messages |
| Search person concept (no permission table found, Section 3/5.5) | https://learn.microsoft.com/en-us/graph/search-concept-person |
| Azure OpenAI On Your Data | https://learn.microsoft.com/azure/ai-foundry/openai/concepts/use-your-data |
| Azure OpenAI FAQ — grounding | https://learn.microsoft.com/azure/foundry-classic/openai/faq#how-do-i-ground-a-model-on-my-own-data |
| Graph permissions reference | https://learn.microsoft.com/en-us/graph/permissions-reference |
| Graph Selected permissions overview | https://learn.microsoft.com/graph/permissions-selected-overview |
| Channel list messages API | https://learn.microsoft.com/en-us/graph/api/channel-list-messages |
| Chat list messages API | https://learn.microsoft.com/en-us/graph/api/chat-list-messages |
| chatMessage-get (RSC note) | https://learn.microsoft.com/graph/api/chatmessage-get?view=graph-rest-1.0 |
| Teams RSC permissions | https://aka.ms/teams-rsc |
| Teams Export APIs | https://learn.microsoft.com/en-us/microsoftteams/export-teams-content |
| Graph throttling limits | https://learn.microsoft.com/graph/throttling-limits |
| People insights overview | https://learn.microsoft.com/graph/people-insights-overview |
| List people API (maintenance-mode note) | https://learn.microsoft.com/graph/api/user-list-people?view=graph-rest-1.0 |
| File Picker v8 overview | https://learn.microsoft.com/onedrive/developer/controls/file-pickers/?view=odsp-graph-online |
| File Picker v8 config schema | https://learn.microsoft.com/onedrive/developer/controls/file-pickers/v8-schema?view=odsp-graph-online |
| OBO flow protocol | https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow |
| OBO with MSAL.NET / Identity.Web | https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/web-apps-apis/on-behalf-of-flow |
| Access tokens / audience (`aud`) | https://learn.microsoft.com/entra/identity-platform/access-tokens |
| Claims challenge format/handling | https://learn.microsoft.com/entra/identity-platform/claims-challenge |
| CAE handling in applications | https://learn.microsoft.com/entra/identity-platform/app-resilience-continuous-access-evaluation |
| Incremental/dynamic consent | https://learn.microsoft.com/entra/identity-platform/consent-types-developer#incremental-and-dynamic-user-consent |
| MSAL.js resources/scopes (incremental consent) | https://learn.microsoft.com/entra/msal/javascript/browser/resources-and-scopes#dynamic-scopes-and-incremental-consent |
| Copilot Studio Direct Line integration | https://learn.microsoft.com/microsoft-copilot-studio/publication-connect-bot-to-custom-application |
| Copilot Studio M365 Agents SDK integration | https://learn.microsoft.com/microsoft-copilot-studio/publication-integrate-web-or-native-app-m365-agents-sdk |
| Copilot Studio billing and licensing | https://learn.microsoft.com/microsoft-copilot-studio/billing-licensing |
| Copilot Studio Licensing Guide (authoritative) | https://go.microsoft.com/fwlink/?linkid=2320995 |
| Azure AI Search — data import strategies (push model) | https://learn.microsoft.com/azure/search/search-what-is-data-import#pushing-data-to-an-index |
| Azure AI Search — semantic ranking overview | https://learn.microsoft.com/azure/search/semantic-search-overview |
| Azure AI Search — vector search overview | https://learn.microsoft.com/azure/search/vector-search-overview |
| Azure AI Search — create vector index how-to | https://learn.microsoft.com/azure/search/vector-search-how-to-create-index |
| Azure AI Search — security filters for trimming | https://learn.microsoft.com/azure/search/search-security-trimming-for-azure-search |
| Azure AI Search — document-level access control overview | https://learn.microsoft.com/azure/search/search-document-level-access-overview |

*End of research findings. This file does not modify, supersede, or duplicate the parent integration plan or HTML mockups.*
