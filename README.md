# AI Video Editor

A portable Windows video editor whose primary job is holding **prompts per clip**
for an AI to perform the edits later. Basic regular editing tools included:
trim, split, reorder, preview.

No Python. No web. No installer. No LocalAppData. Unzip anywhere, run
`AiVideoEditor.exe`. It updates itself in place, in the same folder.

## Status

Scaffold (v1). The editor holds the project; the AI renderer is later work.
The project file is the contract between the two.

## What it does now

- Add videos, reorder (Move up/down), remove, split at playhead, trim In/Out
  per clip, preview with play/pause + seek.
- Per-clip **Prompt** (AI edit instruction) plus a project-wide **Global prompt**.
- Per-clip **Note** (context, e.g. what happened in the clip).
- Save/load projects as JSON (`*.aive.json`, default in `projects/` beside the exe).
- **Import from triage**: reads medal-sorter's `clip_data.sqlite` read-only,
  imports kept, non-discarded clips (optionally one collection), edit
  preferred over raw source, chronological like triage. `edit_idea` becomes
  the clip Prompt, `note` becomes the Note, game/groups become tags.
- **Export ffmpeg**: writes `<project>.concat.txt` + `<project>.ffmpeg.bat`
  (trim + concat, `-c copy`). Cuts on keyframes; frame-accurate cuts need a
  re-encode pass (later).
- **Self-update**: checks `version.txt` on the rolling `latest` release at
  startup. On mismatch it downloads `AiVideoEditor-portable.zip` and shows
  "Restart to update". Applying extracts to temp, then a batch script copies
  over after exit, excluding `config.json` and `projects/`, restarts the exe.

## Project file

```json
{
  "Name": "Friday friends",
  "GlobalPrompt": "Fast-paced montage, keep game audio up on funny moments.",
  "Clips": [
    {
      "SourcePath": "W:\\medal\\Clips\\Marvel Rivals\\clip.mp4",
      "InSeconds": 2.5,
      "OutSeconds": 14.0,
      "Prompt": "Slow-mo the clutch, zoom the health bar.",
      "Note": "Low-health survival",
      "Game": "Marvel Rivals",
      "Groups": ["friday_friends"]
    }
  ]
}
```

`OutSeconds <= InSeconds` means "full clip" (no trim).

## Build

GitHub Actions (`.github/workflows/build.yml`, `windows-latest`) publishes
on every push to `main` plus manual dispatch:

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
- `version.txt` = commit SHA
- `AiVideoEditor-portable.zip` (exe + `version.txt`) + `version.txt` uploaded
  to the rolling `latest` release (created if missing).

Local build needs .NET 9 SDK on Windows; WPF does not build on Linux/macOS.
There is no `.sln` on purpose — the workflow publishes the `.csproj` directly.

## Layout

```
src/AiVideoEditor/
  AiVideoEditor.csproj   net9.0-windows, WPF, one NuGet dep (Microsoft.Data.Sqlite)
  App.xaml(.cs)          entry point
  MainWindow.xaml(.cs)   timeline + preview + per-clip prompt UI
  Models.cs              Project / TimelineClip (the editor↔AI contract)
  ProjectStore.cs        JSON save/load + ffmpeg concat export
  TriageImporter.cs      clip_data.sqlite read-only import
  Updater.cs             custom in-place updater (version.txt + zip + batch)
```

## Decisions (locked)

- WPF + C#, not Python, not web, not Qt, not WinUI 3. See discussion:
  WPF builds in Actions in 1–2 min, single-file exe, and the custom updater
  is ~100 lines reusing the Kikitan `Updater.cs` pattern.
- Portable folder, not Velopack: updates happen in the same directory the
  program runs from. `config.json` and `projects/` survive updates.
- Triage is an import adapter, not a dependency: the editor runs standalone
  on any videos.
- No new dependencies without a reason. One NuGet package total
  (`Microsoft.Data.Sqlite`, for the triage import). Video preview is
  `MediaElement`; deterministic rendering is `ffmpeg` CLI via the export.
