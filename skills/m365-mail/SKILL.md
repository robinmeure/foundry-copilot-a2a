---
name: m365-mail
description: >-
  Read the signed-in user's Outlook mail (recent Inbox messages and mailbox search) and stage email
  drafts, replies or read-state changes for confirmation. Use for questions about emails, senders,
  unread mail or requests to draft or reply to a message.
metadata:
  connector: graph
  source: mail
  label: Mail
  hint: Mailbox message previews
  group: Microsoft 365
  order: "10"
---

# Microsoft 365 mail

- For the latest, newest, recent or unread email use `graph_list_recent_mail` (set `unreadOnly` for unread).
- Use `graph_search_mail` only for keywords, senders or topics; it searches the whole mailbox.
- Results are message previews, not full threads or attachments.
- To write mail, use `graph_propose_mail_draft` or `graph_propose_mail_reply_draft`. These only save a
  draft after the user confirms; mail is never sent. Use `graph_propose_mail_read_state` to mark a
  message read or unread. Use message IDs only from tool results.
