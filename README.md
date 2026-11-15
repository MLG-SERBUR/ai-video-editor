# AI Video Editor

Trim, split, and reorder clips — and hold a per-clip prompt for the AI
to perform the edits (the AI renderer itself is later work). One project,
two targets, one codebase: [Uno Platform](https://platform.uno) 7.0,
.NET 10, C# + WinUI XAML.

The WPF version lasted one day: the UI was confusing, and WPF is
Windows-only. Uno gives the same editor on Windows and the web — and
the web build is the iOS path (open it in Safari; no App Store, no Mac
build, no signing).

## Get it

Rolling `latest` release: https://github.com/MLG-SERBUR/ai-video-editor/releases/tag/latest

- **Windows** — `AiVideoEditor-portable.zip`. Unzip anywhere and run
  `AiVideoEditor.exe`. No installer, no LocalAppData. It checks the
  `latest` release on startup and offers "Restart to update" when a
  new build lands; the update replaces the folder in place and never
  touches `projects/` or `config.json`.
- **Web / iPhone** — open the GitHub Pages site from the repo
  (Settings → Pages). `AiVideoEditor-web.zip` on the release is the
  same site as a backup if you want to host it yourself.

## Use it

Top to bottom, one job at a time:

1. **Add videos** — pick clips. They land in the timeline in order.
2. **Preview** — tap a clip, play/pause, drag the seek bar.
   **Split here** cuts the selected clip at the playhead.
3. **Timeline** — tap a clip to select it. Move up / Move down /
   Remove live in the details panel below.
4. **Details** — Prompt (what the AI should do with this clip),
   Trim in / Trim out in seconds (type them, or "Set in/out here"
   from the playhead; Out = 0 means full clip).
5. **Save / Open** — projects are JSON (`*.aive.json`). On the web
   build, Save downloads the file and Open uploads it.

## Export (Windows only)

Export writes `<project>.concat.txt` + `<project>.ffmpeg.bat` next to
the project file: an ffmpeg concat of the trims in order
(`-c copy`, so cuts land on keyframes; frame-accurate re-encode is
later work). Run the `.bat` to render. It needs real file paths and a
local ffmpeg, so the button only exists on Windows.

## Web limits

The web build edits prompts, trims, and order like the others, but
the browser gives no real file paths (clips are stored by name) and
there is no local ffmpeg — so no Export, and a web-saved project
needs its clips re-added on desktop before exporting. Projects are
still the same JSON, so a project started on your phone opens on
Windows.

## Project format

`*.aive.json`: `{ Name, Clips: [{ SourcePath, InSeconds,
OutSeconds, Prompt, Game, Groups }] }`. This JSON is the contract
between the editor now and the AI renderer later. `OutSeconds <=
InSeconds` means "full clip". Older project files with a global
prompt or per-clip notes still load; those fields are just ignored.

## Build it

Actions builds both targets on every push to `main`
(`.github/workflows/build.yml`). The Uno version is pinned in
`global.json` (`Uno.Sdk 7.0.22`).

History: scaffolded 2026-11-13 as WPF, triage/sqlite import added then
removed the same night (it crashed on first use; a middle layer between
triage and the editor is still to come), rewritten as Uno 2026-11-14
for the simpler UI and the phone/web path. Android target dropped
2026-11-15; the UI was simplified the same day (global prompt, note
field, trim sliders, and the details ScrollViewer all removed).
