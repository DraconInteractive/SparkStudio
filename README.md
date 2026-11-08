# SPARK Studio

A Windows desktop app (WPF, .NET 9) for working with SPARK projects: kanban boards, and rich-text notes
stored as Markdown files on the SPARK server, with Obsidian-style `[[links]]` between them.

```
dotnet run --project src/SparkStudio
```

The app connects to the server URL in the top bar (saved to `%APPDATA%\SparkStudio\settings.json`).
Live updates come from the server's TCP notification port, which the app reads from `/api`.

## Notes

- `.md` files open in a WYSIWYG editor; other text files (`.txt`, `.log`, …) open as plain text and can be
  converted with **Convert to rich note**.
- Markdown shortcuts while typing: `# ` headings, `- ` / `1. ` lists, `[] ` checklists, `> ` quotes,
  ```` ``` ```` + Enter for code, `---` + Enter for a divider, `**bold**`, `*italic*`, `` `code` ``, `~~strike~~`.
- Links: type `[[` to pick a note, or type `[[Title]]` directly. `[[Title#Heading]]`, `[[Title|alias]]` and
  `[[folder/Title]]` work like Obsidian. **Ctrl+Click** follows a link; a link to a missing note offers to create it.
- Link resolution covers the whole root project (including subprojects): title match, case-insensitive,
  preferring the linking note's folder, then the shortest path.
- The side panel shows backlinks ("Linked from") and the links in the current note.
- Saves are automatic and guarded by the server's ETag; if another device changed the note, you choose
  whose version to keep. Notes that are only opened are never rewritten.
- **Rename** rewrites `[[links]]` that pointed to the note. On servers with API 1.45+ it uses
  `PATCH /files/{id}` with a new `name` (same ID, ETag and download URL; an open note stays open).
  Older servers fall back to uploading a copy under the new name and deleting the original (new ID).

## Boards

Create/rename/delete boards and columns, add tasks inline, double-click a card to edit it, and drag cards
between columns. (The API has no task ordering, so tasks stay in creation order within a column.)

## Layout

| Path | What |
| --- | --- |
| `src/Spark.Api` | Typed REST client and TCP notification client |
| `src/Spark.Notes` | Wikilink parsing/resolution, Markdown ⇄ FlowDocument conversion, rename/backlink logic |
| `src/SparkStudio` | WPF app (MVVM with CommunityToolkit.Mvvm; the editor is `Views/NoteEditor`) |
| `tests/Spark.Notes.Tests` | Markdown round-trip, escaping, link parsing/resolution |
| `tests/Spark.Integration.Tests` | Live tests; run only when `SPARK_TEST_URL` points at a **disposable** SPARK instance |

```
dotnet test                                                  # unit tests (live tests skip)
SPARK_TEST_URL=http://192.168.4.20:47693 dotnet test         # include live tests against the test instance
```
