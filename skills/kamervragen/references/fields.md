# Kamervragen index fields

| Field | Meaning |
| --- | --- |
| `onderwerp` | Subject line of the question. |
| `documentNummer` | Official document number, for example `2026Z01234`. Use it as an exact filter only when known. |
| `datum` | Publication date (UTC). Filter with `fromDate` and `toDate`. |
| `thema` | Policy theme assigned to the question. |
| `minister` / `ministerie` | The answering minister and ministry, for example `BZK`. |
| `partijen` | Parties of the members who asked the question. Filter one party with `partij`. |
| `indieners` | Members who submitted the question. |
| `vergaderjaar` | Parliamentary session year, for example `2025-2026`. |
| `soort` | Document type, for example a question or an answer. |
| `aanhangselnummer` | Appendix number of the answer in the Handelingen, when published. |
| `keyPoints` | Generated key points; treat them as a summary, not as the official text. |
| `level` | `document` for whole-document summaries, `chunk` for passages. |
