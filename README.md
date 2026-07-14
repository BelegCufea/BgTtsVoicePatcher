# BgTtsVoicePatcher

Pre-bakes text-to-speech voiceover for unvoiced dialogue in Infinity Engine
games (Baldur's Gate / BG2 / IWD : Enhanced Edition) and patches it directly
into `dialog.tlk`, so the game plays it exactly like a real recorded line.

No engine hooking, no memory patching, no runtime dependency once you're
done - it edits the game's own dialogue file once, using Windows' built-in
text-to-speech voices (or better ones you install).

**Point this at your game, review who's speaking, pick two voices, and go.**

## Requirements

- **Windows 10/11.** Voice synthesis uses Windows' built-in speech engine
  (SAPI), which doesn't exist on macOS/Linux.
- **At least one voice.** Windows ships some by default. For noticeably
  better quality, install [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)
  and any natural voice - it shows up automatically alongside the built-in ones.
- **ffmpeg** (optional, only if you want smaller `.ogg` files instead of
  `.wav`). Easiest install:
  ```
  winget install Gyan.FFmpeg
  ```

## Get started

1. Download `BgTtsVoicePatcher.Gui.exe` from the [Releases](https://github.com/BelegCufea/BgTtsVoicePatcher/releases) page.
2. Run it. No installer, no admin rights needed.
3. Work through the five tabs, top to bottom:

   | Tab | What you do there |
   |---|---|
   | **1. Directories** | Point it at your game folder (the one where *chitin.key* file lives). It finds `dialog.tlk` automatically. Setting DLG and CRE directories are **HIGHLY** recomended (see below). |
   | **2. Config** | Set your character's name/race/pronouns for how unvoiced lines refer to you. Defaults are fine to start. |
   | **3. Speaker Review** | Click **Find Speakers** - it figures out who says each line and their gender. Skim the table, fix anything wrong, filter/search like a spreadsheet. |
   | **4. Voices and Sound Options** | Pick a male voice and a female voice from your installed voices. |
   | **5. Run** | Hit **Run**. Grab a coffee - a full game can take a while. |

That's it. The tool patches `dialog.tlk` in place (after making a backup)
and drops the generated audio into your `override` folder.

### Recommended extra step

Speaker/gender detection works much better if you first export your game's
dialogue and creature data with **[Near Infinity](https://github.com/Argent77/NearInfinity)**:

1. Open Near Infinity, point it at your game.
2. Press **Ctrl+M** (Tools -> Mass Export).
3. Export resource type **DLG** to one folder, and **CRE** to another.
   Uncheck "Decompile scripts and dialogs" for both.
4. Point the tool at those two folders in the **Directories** tab.

This is a one-time setup per game install. Skipping it still works, just
with less accurate name/gender detection.

## Undoing things

If a game update or another mod overwrites `dialog.tlk`, or you just want to
back out entirely, the app remembers what it patched:

- **Reinstall** - re-applies your generated audio's references without
  re-synthesizing anything (fast).
- **Uninstall** - removes them, restoring `dialog.tlk` to how it was before.
  Your generated `.wav`/`.ogg` files aren't deleted, just unreferenced.

Both options appear automatically once the tool finds a previous run's
manifest next to your `dialog.tlk`.

## Power users: command line

Everything the GUI does is also available as `BgTtsVoicePatcher.exe`, a
scriptable console tool with per-command flags for automation, batch
regeneration of specific lines, custom configs, and more.

```
BgTtsVoicePatcher.exe run --game-dir "C:\Games\BG2EE" --ogg
```

Full command and flag reference: **[see the wiki](https://github.com/BelegCufea/BgTtsVoicePatcher/wiki/CLI)**.

## Known limitations

- Windows-only.
- Speaker/gender detection is a best-effort heuristic, not guaranteed-correct
  - review it in the Speaker Review tab before a full run if accuracy matters.
- A pre-baked line can't know your character's actual name or gender at
  playback time; both are fixed at generation time via the Config tab.

More detail, troubleshooting, and the full configuration reference live on
the **[wiki](https://github.com/BelegCufea/BgTtsVoicePatcher/wiki)**.

## License

MIT - see [LICENSE.txt](LICENSE.txt).