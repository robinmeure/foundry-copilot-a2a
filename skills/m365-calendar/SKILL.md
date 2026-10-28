---
name: m365-calendar
description: >-
  Read the signed-in user's Outlook calendar for an explicit date range and stage new calendar events
  for confirmation. Use for questions about meetings, appointments, availability or schedules.
metadata:
  connector: graph
  source: calendar
  label: Calendar
  hint: "Schedule & event occurrences"
  group: Microsoft 365
  order: "20"
---

# Microsoft 365 calendar

- Call `graph_list_calendar` with an explicit range of at most 31 days. Both `start` and `end` need a
  UTC `Z` or an offset; resolve relative dates (today, next week) in the user's time zone.
- Returned times are already in the user's time zone. Mention all-day and cancelled events as such.
- To schedule something, use `graph_propose_calendar_event`; it is created only after the user confirms.
  Never invent attendees.
