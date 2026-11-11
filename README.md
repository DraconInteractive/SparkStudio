# SPARK Studio

A Windows desktop app (WPF, .NET 9) for working with SPARK projects: kanban boards, and rich-text notes
stored as Markdown files on the SPARK server, with Obsidian-style `[[links]]` between them.

```
dotnet run --project src/SparkStudio
```

### Distributable build

```
dotnet publish src/SparkStudio -p:PublishProfile=win-x64
```

Produces `artifacts/publish/SparkStudio.exe`: a single self-contained file (~65 MB, Windows x64) that runs
on any Windows 10/11 PC without installing .NET. Copy it anywhere and run it; settings stay per user in
`%APPDATA%\SparkStudio`.

### Deploying to a SPARK instance

`deploy.ps1` zips `artifacts/publish/SparkStudio.exe` into `artifacts/SparkStudio.zip` and uploads it to a SPARK server. Copy `.env.example` to `.env`
(git ignores it) and set `SPARK_URL` and `SPARK_TOKEN`: a device token with `files.read` and `files.write`.
Optionally set `SPARK_DEVICE_ID` to check the token and its permissions first.

```
./deploy.ps1 -Build      # publish, then upload
./deploy.ps1             # upload the existing build
./deploy.ps1 -DryRun     # check token, build and destination only
```

The zip goes to `/releases/SparkStudio/SparkStudio.zip` by default (`SPARK_DEPLOY_DIR`, `SPARK_DEPLOY_DRIVE`,
`SPARK_DEPLOY_NAME` override this). SPARK keeps duplicate names as separate files, so the script uploads first,
checks the size, then deletes previous uploads of the same name (`-KeepOld` keeps them). A failed upload leaves
the previous build in place. The URL must be `https://` so the token isn't sent in clear text.

The app connects to the server URL in the top bar (saved to `%APPDATA%\SparkStudio\settings.json`).
Live updates come from the server's TCP notification stream, whose port/transport the app reads from `/api`.

## Authenticated servers

Each server URL has its own **device ID** and **device token** (key button in the top bar). Ask an
administrator to issue a device credential (`/admin/credentials`) for the device ID you'll use; the token is
stored encrypted with Windows DPAPI for your Windows account. **Test** runs `/auth/verify` and lists the
token's permissions, flagging any the app needs but doesn't have:

| Permission | Used for |
| --- | --- |
| `projects.read` / `projects.write` | list projects / create projects, edit descriptions |
| `files.read` / `files.write` | read notes / save, create, rename, delete |
| `boards.read` / `boards.write` | view / edit boards |
| `notifications.read` | live updates (token sent in the TCP hello; `tcp+tls` honoured) |
| `discovery.read` | `/api` contract: version, native rename detection, notification transport |

REST calls send `Authorization: Bearer <token>`. If the token is rejected or revoked, live updates stop
(rather than retrying) and the status turns amber; hover it for details, click it to update credentials.
Use the server's `https://` address: over plain `http` the token travels unencrypted.

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
- Saves are automatic, run one at a time in order (each sends the newest text) and are guarded by the
  server's ETag. Echoes of the app's own saves are recognised and ignored. Only a save the server rejects (412)
  whose text really differs counts as a conflict; ETag-only drift is retried silently.
- On a conflict, autosave pauses and **Compare…** shows your text and the server's latest side by side
  (line diff, optional "only differences" view) so you can keep yours or take the server's. **Compare** in
  the note header does the same at any time. Notes that are only opened are never rewritten.
- Sync decisions (foreign revisions, conflicts, ETag drift) are logged to `%LOCALAPPDATA%\SparkStudio\sync.log`.
- **Rename** rewrites `[[links]]` that pointed to the note. On servers with API 1.45+ it uses
  `PATCH /files/{id}` with a new `name` (same ID, ETag and download URL; an open note stays open).
  Older servers fall back to uploading a copy under the new name and deleting the original (new ID).

## Offline

- The cloud button in the Notes file panel (**Keep offline**) keeps a project's text files on this PC and keeps
  them in sync while connected. Notes you open are also saved as you go, even in projects that aren't kept offline.
- If the server can't be reached (at startup or mid-session), the app switches to offline mode: the project list,
  file tree, notes and backlinks come from the saved copies, and **everything is read-only**. The editor, toolbar
  and every create/rename/delete/compare action are locked; files without a saved copy are dimmed. Boards
  aren't kept offline and show a placeholder.
- Text typed just before the connection dropped is kept in the open note and saved, ETag-guarded, as soon as the
  connection returns. The app retries every 20 seconds (or click **Connect**).
- Copies live in `%LOCALAPPDATA%\SparkStudio\offline\<server>\` (plain files, protected by your Windows account).

## Boards

Create/rename/delete boards and columns, add tasks inline, double-click a card to edit it, and drag cards
between columns.

**Sort** (board toolbar) orders the cards within every column by date created, name or severity, ascending or
descending; ties fall back to oldest first. The choice is remembered across sessions and applies to all
boards. Sorting is client-side (the API has no task ordering), so it doesn't change anything on the server.

**Severity** (servers with API 1.48+): each task has a severity — Very Low, Low, Normal (default), High,
Very High or Urgent. Cards show a coloured badge for anything other than Normal, and High and above get a
coloured edge. Set it from the card's right-click **Severity** menu or in the edit dialog. On older servers
the severity controls are hidden and requests never include the field.

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
