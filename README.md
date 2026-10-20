# Lumy

Lumy is a fast Windows photo viewer for culling a photoshoot down to the few
shots worth posting. It rates photos in tiers (like, super like, reject),
compares near-identical frames, runs a tournament to pick the final N photos,
and never copies or moves your files.

Status: early development. The product spec lives in the project's
[spec document](https://claude.ai/code/artifact/e125b4d7-f217-43d7-9711-6521a2092fb6).

## Requirements

- Windows 10 1809+ or Windows 11
- .NET 10 SDK and the Windows App SDK workload (Visual Studio 2022/2026 with
  "Windows application development") to build the app

## Running

Every push builds a ready-to-run copy on GitHub Actions: open the latest
**Build** run, download the `Lumy-win-x64` artifact, unzip it and start
`Lumy.exe`. Nothing is installed; ratings live in
`%LOCALAPPDATA%\Lumy\library.db`.

To build locally: `dotnet run --project src/Lumy.App -p:Platform=x64`.

## Keys (v0.1)

| Key | Action |
| --- | --- |
| ← → or A D | Previous / next photo |
| Shift + → | Next unrated photo |
| 1 or F | Like |
| 2 or R | Super like |
| X | Reject |
| 0 | Clear rating |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+0…4 | Filter: all, likes, super likes, unrated, rejected |
| Enter / Esc | Open full screen / back to the gallery |
| Z or double-click | Zoom to 100% and back |
| F11 | Full screen |
| ` | Toggle moving to the next photo after rating |
| ? or F1 | Key reference |

Ratings are also written to the `.xmp` sidecar of RAW files (Like = 4 stars,
Super like = 5, Reject = −1), so Lightroom and Bridge see them.

## Layout

- `src/Lumy.Core` — folder scanning, library database, ratings, XMP. No UI, cross-platform, unit tested.
- `src/Lumy.App` — the WinUI 3 app.
- `tests/Lumy.Core.Tests` — xUnit tests for the core (`dotnet test tests/Lumy.Core.Tests`).
