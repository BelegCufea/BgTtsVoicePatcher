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

## Installation

Download the latest **[Release](https://github.com/BelegCufea/BgTtsVoicePatcher/releases)** and extract it. The package contains:

- `BgTtsVoicePatcher.exe` — self-contained, no .NET installation required.
- `patcher-config.json` — edit this before running.
- `LICENSE` — MIT license (do whatever you want with it, just don't sue me).
- `README.md` — this file.

Place all files in any folder you like, then open a terminal there.

## Initial setup

#### <u>EDIT `patcher-config.json` before running!!!</u>
**`patcher-config.json`** is located next to the executable (shipped with the project).
**Edit it to customise PC name/race/gender** tokens, CRE name heuristics, gender
overrides, and phonetic cleanup rules. Pass `--config <path>` to use a
different file.

- `pcName` — word replacing `<CHARNAME>` and `<GABBER>`.
- e.g. `"pcName": "friend"` → `<CHARNAME>` becomes "friend" in the generated audio.
- `pcRace` — word replacing `<RACE>` / `<PRO_RACE>`.
- e.g. `"pcRace": "human"` → `<RACE>` becomes "human" in the generated audio.
- `pcGender` — setting for `<PRO_HESHE>`, `<BROTHERSISTER>`, etc. tokens with
gender specific forms defined in `genderTokens` section.
- e.g. `"pcGender": "neutral"` → `<PRO_HESHE>` becomes "they" in the generated audio.


## Recommended setup: Near Infinity mass-export

Gender resolution and speaker identification work by reading `.dlg` and `.cre`
files. In a fresh EET/BG2EE install most of these are packed inside `.bif`
archives, not sitting loose in `override`. **Export them once with Near Infinity
before running the tool.**

1. Download and open [Near Infinity](https://github.com/Argent77/NearInfinity).
2. Point it at your game directory.
3. Press **Ctrl+M** (or **Tools → Mass Export**).
4. Select resource type **DLG** — uncheck **"Decompile scripts and dialogs"**
   and export to a dedicated folder, e.g. `C:\Relax\BGEET\AllDlg`.
5. Repeat for resource type **CRE**, e.g. `C:\Relax\BGEET\AllCre`.
6. Pass these as `--dlg-dir` and `--cre-dir` when running the tool.

This is a one-time step. The export covers everything in the BIF archives plus
your `override` folder, so speaker and gender resolution is complete.

## Quick start — recommended way

The `run` command handles the full pipeline in one step. With the Near Infinity
exports in place this is all you need:

```
BgTtsVoicePatcher.exe run ^
  --game-dir "C:\Relax\BGEET" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --cre-dir "C:\Relax\BGEET\AllCre" ^
  --ogg
```

What `run` does automatically:

1. Locates `override\`, `lang\en_US\dialog.tlk` inside `--game-dir`.
2. Runs `speakers` (if `speaker-strrefs.json` / `speaker-names.json` are
   absent from the lang folder) — pre-fills gender from the CRE Sex bytes and
   groups speakers by in-game display name.
3. Runs `generate` — synthesizes a WAV/OGG for every unvoiced dialogue line
   that has a known speaker, then patches `dialog.tlk` in place.
4. Runs `report` — writes `dialog-report.csv` to the lang folder for review.

The tool lists installed voices and lets you pick interactively for
default/fallback, male and female voices.

Use `--limit 20 --dry-run` for a quick test before committing to a full run:

```
BgTtsVoicePatcher.exe run ^
  --game-dir "C:\Relax\BGEET" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --cre-dir "C:\Relax\BGEET\AllCre" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --ogg --limit 20 --dry-run
```

This will also create wav files in `override` for you to check if everything works as intended but will **not** patch `dialog.tlk` or write the manifest.

## Step-by-step usage

### List installed voices

```
BgTtsVoicePatcher.exe voices
```

### Scan — how many lines are candidates

```
BgTtsVoicePatcher.exe scan --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk"
```

### speakers — build and review speaker data

```
BgTtsVoicePatcher.exe speakers ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --cre-dir "C:\Relax\BGEET\AllCre"
```

Writes four files into `--dlg-dir`:

| File | Purpose |
|---|---|
| `speaker-strrefs.json` | StrRef → system name mapping. **Do not edit.** |
| `speaker-names.json` | Grouped by display name, gender pre-filled. **Open this** to fix any `null` or wrong guess before generating. |
| `speaker-stats.json` | Line counts per display name and system name, plus unmatched summary. |
| `speaker-unmatched.txt` | TTS candidates with no speaker found — mostly item/spell descriptions, not real gaps. |

Re-running `speakers` is safe: it only adds brand-new names, never overwrites
entries you have already reviewed.

### generate

```
BgTtsVoicePatcher.exe generate ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --override "C:\Relax\BGEET\override" ^
  --speaker-map "C:\Relax\BGEET\AllDlg\speaker-strrefs.json" ^
  --name-gender-map "C:\Relax\BGEET\AllDlg\speaker-names.json" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --ogg --limit 50 --dry-run
```

Drop `--dry-run` and `--limit` when ready for a full run.

Lines with no known speaker are always excluded — non-dialogue TLK entries
(item descriptions, journal text, etc.) would interfere with the game if
voiced. When `--speaker-map` is provided the DLG scan is skipped entirely,
saving significant time on large installs.

### report

```
BgTtsVoicePatcher.exe report ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --override "C:\Relax\BGEET\override" ^
  --speaker-map "C:\Relax\BGEET\AllDlg\speaker-strrefs.json" ^
  --name-gender-map "C:\Relax\BGEET\AllDlg\speaker-names.json"
```

Writes a CSV (or JSON with `--format json`) of every TLK entry that has text:
`StrRef, SystemName, RealName, Gender, HasSound, SoundResRef, SoundFileExists, Text`.
Pure reads — safe to run alongside a generation pass.

### repatch — re-apply after dialog.tlk is overwritten

If anything overwrites `dialog.tlk` (a game patch, a WeiDU reinstall, etc.)
your `.wav`/`.ogg` files in `override` are unaffected but the sound references
in the TLK are gone. `repatch` restores them in seconds from the manifest,
without re-synthesizing any audio:

```
BgTtsVoicePatcher.exe repatch --game-dir "C:\Relax\BGEET"
```

Or with an explicit TLK path:

```
BgTtsVoicePatcher.exe repatch --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk"
```

Preview first with `--dry-run`:

```
BgTtsVoicePatcher.exe repatch --game-dir "C:\Relax\BGEET" --dry-run
```

### unpatch — remove all patches

Removes every SoundResRef this tool wrote, leaving `dialog.tlk` as if
`generate` had never run. Only entries whose SoundResRef still matches the
manifest are touched — entries changed by another mod since are skipped with a
warning. `.wav`/`.ogg` files in `override` are NOT deleted.

```
BgTtsVoicePatcher.exe unpatch --game-dir "C:\Relax\BGEET"
```

Always do a dry run first:

```
BgTtsVoicePatcher.exe unpatch --game-dir "C:\Relax\BGEET" --dry-run
```

## patcher-config.json

All token substitution, CRE name heuristics, gender overrides, and phonetic
cleanup rules live in `patcher-config.json`. The file is required — pass
`--config <path>` to use a different one.

Key settings at the top:

```json
{
  "pcName":   "friend",
  "pcRace":   "human",
  "pcGender": "neutral"
}
```

- **`pcName`** — word replacing `<CHARNAME>` and `<GABBER>`.
- **`pcRace`** — word replacing `<RACE>` / `<PRO_RACE>`.
- **`pcGender`** — `"male"`, `"female"`, or `"neutral"`. Controls which side
  of gender-sensitive tokens (`<PRO_HESHE>`, `<BROTHERSISTER>`, etc.) is used.
  Neutral uses they/them/their/person/sibling/etc.

Other sections:

| Section | Purpose |
|---|---|
| `creNameReplacements` | Maps DLG system names to CRE basenames when the normal heuristic fails (e.g. `HEXXAT → OHHEX`). |
| `genderOverrides` | Per-system-name gender that bypasses CRE lookup (e.g. `EDWINW → F`). |
| `genderTokens` | Full token table with male/female/neutral values for every IE gender token. |
| `identityTokens` | Token names replaced by `pcName` or `pcRace`. |
| `phoneticRules` | Regex find/replace pairs applied after token substitution (e.g. strip `*sigh*`, fix garbled em-dashes). |

## Options reference

### Common to multiple commands

| Option | Default | Notes |
|---|---|---|
| `--config` | `patcher-config.json` next to exe | Required — no built-in fallback. |
| `--tlk` | required (unless `--game-dir`) | Path to `dialog.tlk`. |
| `--game-dir` | — | Game root. Derives TLK as `<game-dir>\lang\<lang>\dialog.tlk`. |
| `--lang` | `en_US` | Language subfolder, used with `--game-dir`. |
| `--manifest` | `tts-manifest.json` next to TLK | Resumability/audit log. |
| `--encoding` | `utf-8` | TLK text encoding. Use `windows-1250` for Czech fan-translated TLKs. |
| `--dlg-dir` | `--override` | Directory of `.dlg` files (Near Infinity mass-export recommended). |
| `--cre-dir` | none | Directory of `.CRE` files (Near Infinity mass-export). Enables gender from Sex byte. |
| `--speaker-map` | none | `speaker-strrefs.json` from `speakers`. When provided in `generate`/`report`, the DLG scan is skipped. |
| `--name-gender-map` | none | Reviewed `speaker-names.json` from `speakers`. |

### `run`

| Option | Default | Notes |
|---|---|---|
| `--game-dir` | required | Root of the game installation. |
| `--lang` | `en_US` | Language subfolder under `lang\`. |

### `speakers`

| Option | Default | Notes |
|---|---|---|
| `--min-length` | `2` | Skip TTS candidates shorter than this after token cleaning. |
| `--out-map` | `<dlg-dir>\speaker-strrefs.json` | |
| `--out-names` | `<dlg-dir>\speaker-names.json` | |
| `--out-stats` | `<dlg-dir>\speaker-stats.json` | Per-character line counts and unmatched summary. |
| `--out-unmatched` | `<dlg-dir>\speaker-unmatched.txt` | |

### `generate`

| Option | Default | Notes |
|---|---|---|
| `--override` | required | Where WAVs are written — `<GameRoot>\override`. |
| `--voice` / `--default-voice` | system default | SAPI voice for lines whose gender cannot be resolved. Can be the literal word `male` or `female` to reuse the corresponding `--male-voice`/`--female-voice`. |
| `--male-voice` / `--female-voice` | none | SAPI voice for M/F resolved lines. |
| `--gender-map` | none | Hand-maintained JSON `{ "12345": "F" }` — per-StrRef override, checked first. |
| `--ogg` | off | Transcode each WAV to Ogg Vorbis via ffmpeg in-place. Matches how Voices Voices Extravaganza works — EE sniffs the real audio format, not the extension. |
| `--ffmpeg` | `ffmpeg` | Path to ffmpeg.exe if not on PATH. |
| `--ogg-quality` | `2` | libvorbis quality scale 0–10. |
| `--rate` | `0` | SAPI speech rate, -10 to 10. |
| `--volume` | `100` | SAPI volume, 0–100. |
| `--prefix` | `TS` | 2-char prefix for generated resrefs, e.g. `TS0009IX`. |
| `--min-length` | `2` | Skip candidates shorter than this after token cleaning. |
| `--limit` | unlimited | Cap candidates — use for testing. |
| `--dry-run` | off | Synthesizes WAVs but does **not** patch `dialog.tlk` and does **not** write the manifest. |
| `--strrefs` | none | Flat JSON array e.g. `[12345, 67890]` — (re)generate exactly those lines, ignores `--limit`. |

### `repatch` / `unpatch`

| Option | Default | Notes |
|---|---|---|
| `--game-dir` or `--tlk` | required | Locates `dialog.tlk`. |
| `--lang` | `en_US` | Used with `--game-dir`. |
| `--manifest` | `tts-manifest.json` next to TLK | |
| `--dry-run` | off | Shows what would change without writing anything. |

### `report`

| Option | Default | Notes |
|---|---|---|
| `--override` | none | If provided, adds `SoundFileExists` column. |
| `--out` | `dialog-report.csv` next to the TLK | |
| `--format` | inferred from extension, else `csv` | `csv` or `json`. |

## How it works

- `dialog.tlk` (TLK V1) has an 18-byte header followed by one fixed 26-byte
  record per StrRef: a flags word, an 8-byte `SoundResRef`, volume/pitch
  variance (unused), and an offset+length into the strings section. See the
  [IESDP TLK V1 page](https://gibberlings3.github.io/iesdp/file_formats/ie_formats/tlk_v1.htm).
- "Unvoiced" = the entry has text but the sound-exists flag bit (`0x0002`) is
  unset / the `SoundResRef` is blank.
- For each candidate the tool cleans dialogue tokens, synthesizes a
  22.05kHz/16-bit/mono WAV via SAPI, then seeks directly to that entry's
  26-byte record and overwrites *only* the flags + `SoundResRef` bytes.
  Nothing else moves — no resizing, no risk to any other entry's offsets.
- A `.bak` copy of the original TLK is made automatically before the first
  write (never overwrites an existing `.bak`, so re-running is safe).
- The manifest records, per StrRef, the resref/text-hash/voice used. Re-runs
  skip unchanged entries and only retry failures.

## Known limitations

- Windows-only (SAPI).
- Resrefs are derived deterministically from the StrRef number and `--prefix`,
  with no cross-check against existing BIFF/override resources. Collisions are
  astronomically unlikely with a distinctive prefix.
- The CRE name-matching heuristic is tuned against BGEET naming conventions.
  Run `speakers` and review `speaker-names.json` before a full generation pass
  if accuracy matters.
- Token substitution uses the values in `patcher-config.json`. A pre-baked
  line cannot know the player's actual name or chosen gender.

## Building from source

If you prefer to build and run from source rather than using the compiled
executable, you need the **.NET 10 SDK**.

```
dotnet restore
dotnet build -c Release
```

Run any command by prefixing with `dotnet run --`:

```
dotnet run -- voices

dotnet run -- run ^
  --game-dir "C:\Relax\BGEET" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --cre-dir "C:\Relax\BGEET\AllCre" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --ogg

dotnet run -- repatch --game-dir "C:\Relax\BGEET"
```

To publish a self-contained single-file executable yourself (the same as the
release build):

```
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```