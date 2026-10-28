# Knowledge skills

Each folder here is one [Agent Skill](https://agentskills.io/specification) that the Knowledge API
offers to its model. Adding an APIM agent, an Azure AI Search index or an M365 source is a matter
of adding a folder; the C# connectors that execute the calls stay unchanged.

```text
skills/
  apim-agents/                generic A2A tools (list_a2a_agents, ask_a2a_agent)
  apim-agent-<name>/          one per APIM agent; the administrator writes the description
  kamervragen/                Azure AI Search index: SKILL.md + connector.json + references/
  m365-<source>/              Microsoft Graph sources (mail, calendar, todo, planner, teams, people)
  office-documents/           Word and PowerPoint generation (source: documents)
```

## How the model uses skills (progressive disclosure)

1. **Catalog** – every request lists only `name: description` for the skills of the sources the user
   selected, plus the `load_skill` tool (and `read_skill_resource` when a skill has references).
2. **Load on demand** – the model calls `load_skill` with the skill names it needs. The API returns the
   SKILL.md body and from then on also advertises that skill's tools.
3. **Resources** – files under `references/` are read only when the model calls `read_skill_resource`.

The description is therefore the most important text: it decides whether the skill is loaded at all.
State *what* the agent or index knows and *when* to use it, in at most 1,024 characters.

## Add a skill with the CLI

```powershell
# APIM agent (the agent id must also be approved in A2A:AllowedAgentIds)
dotnet run --project src/FoundryCopilotA2A.Cli -- new-skill --connector apim-a2a `
  --name apim-agent-hr --agent-id hr-policies --label "HR policies" `
  --description "HR policy specialist. Use for leave, benefits and expense policy questions."

# Azure AI Search index
dotnet run --project src/FoundryCopilotA2A.Cli -- new-skill --connector azure-ai-search `
  --name wetten --label "Wetten" --description "Dutch legislation. Use for questions about laws and articles."

# Validate everything without starting the API
dotnet run --project src/FoundryCopilotA2A.Cli -- validate-skills
```

`new-skill` never overwrites a folder and rolls back when the result conflicts with existing skills.
Restart the Knowledge API after changing skills; folders are validated once at startup and the API
refuses to start when any skill is invalid.

## SKILL.md

```markdown
---
name: apim-agent-hr            # must equal the folder name; lowercase, digits, single hyphens
description: >-
  HR policy specialist. Use for leave, benefits and expense policy questions.
metadata:
  connector: apim-a2a          # apim-a2a | azure-ai-search | graph | office-documents
  agent-id: hr-policies        # apim-a2a only
  source: wetten               # selection id; fixed to a2a for APIM, a Graph source for graph
  label: HR policies           # UI label, at most 60 characters
  hint: "Read-only policy agent"   # optional UI hint, at most 120 characters
  group: Agents                # optional UI group
  order: "72"                  # optional sort order (quoted integer)
---

# Instructions the model reads after load_skill (at most 20,000 characters)
```

| Connector | Source | Tools | Configuration |
|-----------|--------|-------|---------------|
| `apim-a2a` | always `a2a` | `list_a2a_agents`, `ask_a2a_agent` | `A2A:*`; the agent id must be in `A2A:AllowedAgentIds` |
| `azure-ai-search` | defaults to the skill name | defined in `connector.json` | `<configurationSection>:Enabled`, `:Endpoint`, `:Index` |
| `graph` | `mail`, `calendar`, `todo`, `planner`, `teams` or `people` | code-defined `graph_*` tools | Graph delegated scopes |
| `office-documents` | always `documents` | `create_word_document`, `create_presentation` | `Documents:OneDriveFolder`; saving needs `Files.ReadWrite` |

APIM agents: the `apim-agents` skill (without `agent-id`) carries the shared A2A guidance. Every
`apim-agent-*` skill describes exactly one agent; its description is also shown in the source picker
and returned by `list_a2a_agents`. The per-chat agent selection still decides which agents can be called.

Graph: each source can be claimed by one skill only. A new M365 workload needs code in `GraphSkill`
first; a skill folder can then describe it.

Office documents: one skill only, without `connector.json`. The model writes a JSON outline
(sections, bullets, tables or slides) as tool arguments; the API validates it and renders the
`.docx` or `.pptx` with the OpenXML SDK, so no macros, templates or model-written code run.
The file is offered as a download in the chat plus a **Save to OneDrive** proposal that writes
only after the user confirms. The repository ships `office-documents/`; if it was removed,
`new-skill --connector office-documents` scaffolds it again.

## connector.json (azure-ai-search)

`new-skill` writes a minimal `connector.json` for an index with `id`, `title` and `content` fields.
Adapt it to the index schema; [kamervragen/connector.json](kamervragen/connector.json) is a full
example with semantic ranking, vector queries and filters.

| Key | Purpose |
|-----|---------|
| `configurationSection` | Where `Enabled`, `Endpoint` and `Index` are read (default `Skills:<name>`) |
| `tool.name`, `tool.description` | The search tool the model calls (default `search_<name>`) |
| `search` | `queryType`, `semanticConfiguration`, `scoringProfile`, `searchFields`, `select`, `vector` |
| `filters` | Optional tool arguments mapped to fields; `kind` is `equals`, `any`, `dateFrom` or `dateTo` |
| `result` | How a document becomes a cited passage: id, title, content, metadata and locator fields |
| `coverage` | Text returned with results; must say results are not exhaustive |
| `mock` | The passage returned when `Knowledge:Backend=Mock` |
| `status` | Optional progress texts shown in the chat |

Every field used in `result` must be listed in `search.select`. The API identity needs the
**Search Index Data Reader** role on the index.

## Rules and limits

- Unknown connectors, duplicate names, sources, agent ids or tool names fail validation.
- Skills cannot define reserved tools (`load_skill`, `read_skill_resource`, `search_documents`,
  `retrieve_documents`, `list_a2a_agents`, `ask_a2a_agent`, `graph_*`).
- `references/` holds at most 20 flat `.md`, `.txt`, `.json` or `.csv` files of up to 64 KB each.
- Skills contain instructions and declarative configuration only. They never contain code, endpoints,
  keys, tenant ids or other environment-specific values: put those in configuration or Key Vault.
- Skill text is trusted operator input. Review changes to this folder like code.
- `Skills:Paths` overrides the folders the API loads (default: `skills` next to the API binaries).
