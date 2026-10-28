---
name: office-documents
description: >-
  Create Word documents and PowerPoint presentations from the conversation and retrieved evidence. Use when
  the user asks to draft, write or build a document, report, memo, brief, slides or a deck.
metadata:
  connector: office-documents
  source: documents
  label: "Word & PowerPoint"
  hint: "Create .docx and .pptx files"
  group: Create
  order: "10"
---

# Word & PowerPoint

- Use `create_word_document` for documents, reports, memos and briefs; use `create_presentation` for
  slides and decks. Create one file per request unless the user asks for more.
- Gather content first: when the request depends on mail, meetings, files or other sources, retrieve the
  evidence with those tools before writing the outline. Never invent facts, figures, names or quotes.
- If the topic or audience is unclear and nothing in the conversation supplies it, ask one short
  question instead of creating a generic file.
- Documents: start with a short summary section, use clear headings, prefer short paragraphs and bullets,
  and use a table only for genuinely tabular data. End with a "Sources" section when evidence was used.
- Presentations: one idea per slide, 3–6 short bullets per slide, and put detail in speaker notes. End
  with a "Sources" slide when evidence was used. Typical decks have 5–10 slides.
- Write in the language the user writes in. Use `**bold**` sparingly for key terms.
- After the file is created, summarize it in two or three sentences. The user downloads it or saves it to
  OneDrive with the buttons below the answer; do not claim it was saved or shared.
