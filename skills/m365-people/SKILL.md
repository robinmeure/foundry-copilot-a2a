---
name: m365-people
description: >-
  Find colleagues and contacts in Microsoft 365 by name, topic or role. Use for questions about who
  someone is, who works on something, or how to reach a colleague.
metadata:
  connector: graph
  source: people
  label: People
  hint: "Colleagues, roles & contacts"
  group: Microsoft 365
  order: "60"
---

# Microsoft 365 people

- Call `graph_search_people` with a name, role or topic (at most 200 characters).
- Report only the profile fields that were returned; do not guess email addresses or phone numbers.
