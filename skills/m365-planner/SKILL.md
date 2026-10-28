---
name: m365-planner
description: >-
  Read Microsoft Planner tasks assigned to the signed-in user or in a known plan, and stage task
  progress changes for confirmation. Use for team plans, assigned work and Planner buckets.
metadata:
  connector: graph
  source: planner
  label: Planner
  hint: "Assigned & plan tasks"
  group: Microsoft 365
  order: "40"
---

# Microsoft Planner

- Start with `graph_list_my_planner_tasks`. Use `graph_list_plan_tasks` only with a plan ID from those
  results or supplied by the user; never guess plan IDs.
- For due dates use the `dueDate` field (already in the user's time zone).
- To change progress use `graph_propose_planner_task_progress` (0, 50 or 100); it only stages the change.
