---
name: m365-todo
description: >-
  Read the signed-in user's Microsoft To Do lists and tasks, and stage new tasks or status changes for
  confirmation. Use for personal task lists, flagged emails as tasks and to-do items.
metadata:
  connector: graph
  source: todo
  label: Microsoft To Do
  hint: "Personal task lists & items"
  group: Microsoft 365
  order: "30"
---

# Microsoft To Do

- Call `graph_list_todo_lists` before `graph_list_todo_tasks` unless the user supplied a list ID.
  The default list has `wellknownListName` `defaultList`; flagged email has `flaggedEmails`.
- For due dates use the `dueDate` field (already in the user's time zone), not the raw UTC `dueDateTime`.
- To add a task use `graph_propose_todo_task`; to complete or reopen one use
  `graph_propose_todo_task_status`. Both only stage the change for the user's confirmation.
- These are not Planner tasks; use the Planner skill for plan or assigned team tasks.
