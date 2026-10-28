---
name: kamervragen
description: >-
  Search the shared Dutch parliamentary questions (Kamervragen) reference index: written questions from
  members of the Tweede Kamer and the ministers' answers. Use for questions about Kamervragen,
  parliamentary questions or answers, ministries, parties or members asking about a topic.
metadata:
  connector: azure-ai-search
  source: kamervragen
  label: Kamervragen
  hint: "Shared parliamentary questions · read-only search"
  group: Reference
  order: "80"
---

# Kamervragen

Use `search_kamervragen` for Dutch parliamentary questions, parliamentary answers or Kamervragen.

- This is a shared public reference corpus, separate from the user's documents and APIM agents.
- Search in Dutch where appropriate; the index combines keyword, vector and semantic ranking.
- Re-query it for follow-up questions instead of relying on earlier answers.
- Leave filters `null` unless the user, or an earlier result, supplied the exact value.
  Read `references/fields.md` when you need the meaning of a metadata field.
- Results are bounded excerpts, not exhaustive coverage. Say so when the user asks for "all" questions.
- Do not invent parliamentary documents, document numbers or answers.
