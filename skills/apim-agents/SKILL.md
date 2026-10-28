---
name: apim-agents
description: >-
  Ask specialist knowledge agents published through the Azure API Management (APIM) A2A gateway.
  Use when a question needs domain expertise that the selected APIM agents provide, or when the
  user asks which specialist agents are available.
metadata:
  connector: apim-a2a
  label: APIM agents
  hint: Specialist agents through the APIM gateway
  group: Agents
  order: "70"
---

# APIM specialist agents

1. Call `list_a2a_agents` to see the agents the user selected for this chat, with the description an
   administrator wrote for each one.
2. Pick the most relevant agent and call `ask_a2a_agent` with one focused, self-contained, read-only
   knowledge question. Include the context the agent needs; it cannot see this conversation.
3. Ask a second agent only when the first answer is insufficient or the question spans domains.

Rules:

- Ask knowledge questions only. Never ask a remote agent to perform actions or changes.
- Cite the server-assigned `[[cite:n]]` markers of the returned evidence, never citation numbers that
  appear inside the agent's text.
- If no selected agent fits, say so and suggest enabling another agent instead of guessing.
