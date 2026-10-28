---
name: apim-agent-mock-knowledge
description: >-
  Mock APIM knowledge agent for local development. Answers general questions about the sample
  knowledge base without calling Azure.
metadata:
  connector: apim-a2a
  agent-id: mock-apim-knowledge
  label: Mock APIM knowledge agent
  group: Agents
  order: "71"
---

# Mock APIM knowledge agent

Ask `mock-apim-knowledge` with `ask_a2a_agent` for questions about the sample knowledge base.
It only exists when the API runs with `Knowledge:Backend=Mock`; its answers are canned test data.
