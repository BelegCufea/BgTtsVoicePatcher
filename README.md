# BgTtsVoicePatcher

Pre-bakes Windows SAPI text-to-speech audio for every **unvoiced** line in an
Infinity Engine `dialog.tlk` (Baldur's Gate / BG2 / IWD : Enhanced Edition) and
patches each line's `SoundResRef` in place, so the engine plays the generated
WAV exactly like it would a real recorded voice line.

This intentionally does *not* try to hook the engine at runtime (no EEex, no
memory patching of the executable). Since every possible line of dialogue
already exists as text in `dialog.tlk` before the game even launches, there is
no need for a live hook - everything can be generated and wired up ahead of
time.

## Requirements

- **Windows** — System.Speech wraps SAPI via COM and will not run on Linux/macOS.
- **.NET 10 SDK.**
- **At least one SAPI voice.** Windows ships defaults; for noticeably better
  quality install
  [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)
  and any natural voice — it shows up via the `voices` command like any other
  SAPI voice.
- **ffmpeg** on PATH, only when using `--ogg`.
- **patcher-config.json** next to the executable (shipped with the project).
  Edit it to customise PC name/race/gender tokens, CRE name heuristics, gender
  overrides, and phonetic cleanup rules. Pass `--config <path>` to use a
  different file.

### Near Infinity — mass-exporting DLG and CRE files

The gender-resolution and speaker-identification features work by reading
`.dlg` and `.cre` files. In a fresh EET install most of these are packed inside
`.bif` archives rather than sitting loose in the `override` folder.

**You need to export them first with Near Infinity:**

1. Open Near Infinity and point it at your game directory.
2. Press **Ctrl+M** (or **Tools → Mass Export**).
3. Select resource types **DLG** and **CRE** (do them one at a time or
   together).
4. **Uncheck "Decompile scripts and dialogs"** — you want the raw binary files,
   not decompiled text.
5. Export each type to its own folder, e.g. `C:\Relax\BGEET\AllDlg` and
   `C:\Relax\BGEET\AllCre`.
6. Pass these folders as `--dlg-dir` and `--cre-dir` respectively.

This is a one-time setup step. The export covers everything in the BIF archives
plus your `override` folder, so the speaker and gender resolution is complete.

### Running on a different machine

The project folder is just code — copy it anywhere and `dotnet build`/`dotnet run`
(NuGet only needs network on the first `restore`, to pull `System.Speech`).
Bring along:

- `dialog.tlk` from the game (for `scan`/`generate`)
- Your exported `AllDlg`/`AllCre` folders, or the `speaker-*.json` files if
  you have already run `speakers`
- `patcher-config.json`
- ffmpeg, if using `--ogg`

Copy the generated `.wav`/`.ogg` files and the patched `dialog.tlk` back to
the game machine afterwards.

## Build

```
dotnet restore
dotnet build -c Release
```

## Quick start — one command

If you just want to run the full pipeline with minimal fuss:

```
dotnet run -- run --game-dir "C:\Relax\BGEET" --ogg
```

The `run` command auto-discovers `override\`, `lang\en_US\dialog.tlk`, runs
`speakers` if needed, then `generate`, then `report`. It prompts you
interactively to pick voices for any slot you did not supply on the command
line. See the `run` section below for all options.

## Step-by-step usage

### 1. List installed voices

```
dotnet run -- voices
```

### 2. Scan to see how many lines are candidates

```
dotnet run -- scan --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk"
```

### 3. Build speaker files (recommended once, then reuse)

Point `speakers` at your Near Infinity exports. With `--cre-dir` it
pre-fills gender from each speaker's CRE Sex byte and groups entries by
in-game display name, so the resulting `speaker-names.json` is already
mostly filled in — you only need to fix the occasional wrong guess or
fill in a `null` for names the heuristic could not resolve.

```
dotnet run -- speakers ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --cre-dir "C:\Relax\BGEET\AllCre"
```

This writes four files into `--dlg-dir`:

| File | Purpose |
|---|---|
| `speaker-strrefs.json` | StrRef → system name mapping. **Do not edit.** |
| `speaker-names.json` | Grouped by display name, gender pre-filled. **Edit this** to fix nulls or wrong guesses. |
| `speaker-stats.json` | Line counts per display name and per system name, plus unmatched summary. |
| `speaker-unmatched.txt` | Every TTS candidate with no speaker found — mostly item/spell descriptions and journal text, not real dialogue gaps. |

Re-running `speakers` is safe: it only adds brand-new names to
`speaker-names.json`, never overwriting entries you have already reviewed.

### 4. Generate

```
dotnet run -- generate ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --override "C:\Relax\BGEET\override" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --speaker-map "C:\Relax\BGEET\AllDlg\speaker-strrefs.json" ^
  --name-gender-map "C:\Relax\BGEET\AllDlg\speaker-names.json" ^
  --ogg --limit 50 --dry-run
```

Drop `--dry-run` and `--limit` once you are happy with the voice selection.

**Important:** lines with no known speaker are always excluded — non-dialogue
TLK entries (item descriptions, journal text, etc.) would break the game if
voiced. The `--speaker-map` file is used as the filter; if it is absent, a
live DLG scan is run instead. When `--speaker-map` is present the DLG scan is
skipped entirely, saving significant time on large installs.

### 5. Browse results: `report`

```
dotnet run -- report ^
  --tlk "C:\Relax\BGEET\lang\en_US\dialog.tlk" ^
  --override "C:\Relax\BGEET\override" ^
  --speaker-map "C:\Relax\BGEET\AllDlg\speaker-strrefs.json" ^
  --name-gender-map "C:\Relax\BGEET\AllDlg\speaker-names.json"
```

Writes a CSV (or JSON with `--format json`) of every TLK entry that has text:
`StrRef, SystemName, RealName, Gender, HasSound, SoundResRef, SoundFileExists, Text`.
Safe to run alongside a generation pass — pure reads, no synthesis.

## `run` — full pipeline in one command

```
dotnet run -- run ^
  --game-dir "C:\Relax\BGEET" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --cre-dir "C:\Relax\BGEET\AllCre" ^
  --dlg-dir "C:\Relax\BGEET\AllDlg" ^
  --ogg
```

`run` derives all paths from `--game-dir`:

| Path | Derived as |
|---|---|
| TLK | `<game-dir>\lang\<lang>\dialog.tlk` |
| Override | `<game-dir>\override` |
| Speaker files | `<game-dir>\lang\<lang>\speaker-*.json` |
| Report | `<game-dir>\lang\<lang>\dialog-report.csv` |

`--lang` defaults to `en_US`. If the speaker files are absent it runs
`speakers` first. It then runs `generate` and `report`. Any voice not supplied
on the command line is prompted for interactively.

## `patcher-config.json`

All token substitution, CRE name heuristics, gender overrides, and phonetic
cleanup rules live in `patcher-config.json` next to the executable. Pass
`--config <path>` to use a different file; the file is required (no silent
built-in fallback).

Key settings at the top of the file:

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
| `--config` | `patcher-config.json` next to exe | Path to config file. Required — no built-in fallback. |
| `--tlk` | required | Path to `dialog.tlk`. |
| `--encoding` | `windows-1252` | Code page for TLK text. Use `windows-1250` for Czech fan-translated TLKs. |
| `--min-length` | `2` | Skip TTS candidates shorter than this after token cleaning. |
| `--dlg-dir` | `--override` | Directory of `.dlg` files (Near Infinity mass-export recommended). Used by `speakers` for scanning; used by `generate`/`report` as live fallback when `--speaker-map` is absent. |
| `--cre-dir` | none | Directory of `.CRE` files (Near Infinity mass-export). Enables gender resolution from each speaker's Sex byte. |
| `--speaker-map` | none | `speaker-strrefs.json` from `speakers`. When provided in `generate`, the DLG scan is skipped. |
| `--name-gender-map` | none | Reviewed `speaker-names.json` from `speakers`. |

### `run`

| Option | Default | Notes |
|---|---|---|
| `--game-dir` | required | Root of the game installation. |
| `--lang` | `en_US` | Language subfolder under `lang\`. |

### `speakers`

| Option | Default | Notes |
|---|---|---|
| `--out-map` | `<dlg-dir>\speaker-strrefs.json` | |
| `--out-names` | `<dlg-dir>\speaker-names.json` | |
| `--out-stats` | `<dlg-dir>\speaker-stats.json` | Per-character line counts and unmatched summary. |
| `--out-unmatched` | `<dlg-dir>\speaker-unmatched.txt` | |

### `generate`

| Option | Default | Notes |
|---|---|---|
| `--override` | required | Where WAVs are written — `<GameRoot>\override`. |
| `--manifest` | `<tlk-dir>\tts-manifest.json` | Resumability log. |
| `--voice` / `--default-voice` | system default | SAPI voice for lines whose gender cannot be resolved. Can be the literal word `male` or `female` to reuse the corresponding `--male-voice`/`--female-voice`. |
| `--male-voice` / `--female-voice` | none | SAPI voice for M/F resolved lines. |
| `--gender-map` | none | Hand-maintained JSON `{ "12345": "F" }` — per-StrRef override, checked first. |
| `--ogg` | off | Transcode each WAV to Ogg Vorbis via ffmpeg in-place (same `.wav` filename). Matches how Voices Voices Extravaganza works — EE sniffs the real audio format rather than trusting the extension. |
| `--ffmpeg` | `ffmpeg` | Path to ffmpeg.exe if not on PATH. |
| `--ogg-quality` | `2` | libvorbis quality scale 0–10. |
| `--rate` | `0` | SAPI speech rate, -10 to 10. |
| `--volume` | `100` | SAPI volume, 0–100. |
| `--prefix` | `TS` | 2-char prefix for generated resrefs, e.g. `TS0009IX`. |
| `--limit` | unlimited | Cap candidates — use for testing. |
| `--dry-run` | off | Synthesizes WAVs but does **not** patch `dialog.tlk` and does **not** write the manifest. |
| `--strrefs` | none | Flat JSON array of StrRef numbers, e.g. `[12345, 67890]`. Processes exactly those lines regardless of voiced state, always resynthesizes fresh, ignores `--limit`. |

### `report`

| Option | Default | Notes |
|---|---|---|
| `--override` | none | If provided, adds `SoundFileExists` column checking whether the `.wav` is actually on disk. |
| `--out` | `dialog-report.csv` next to the TLK | |
| `--format` | inferred from extension, else `csv` | `csv` or `json`. |

## How it works

- `dialog.tlk` (TLK V1) has an 18-byte header followed by one fixed 26-byte
  record per StrRef: a flags word, an 8-byte `SoundResRef`, volume/pitch
  variance (unused), and an offset+length into the strings section. See the
  [IESDP TLK V1 page](https://gibberlings3.github.io/iesdp/file_formats/ie_formats/tlk_v1.htm).
- "Unvoiced" = the entry has text but the sound-exists flag bit (`0x0002`) is
  unset / the `SoundResRef` is blank.
- For each candidate, the tool cleans dialogue tokens, synthesizes a
  22.05kHz/16-bit/mono WAV via SAPI, then seeks directly to that entry's
  26-byte record and overwrites *only* the flags + `SoundResRef` bytes.
  Nothing else in the file moves — no resizing, no risk to any other entry's
  offsets.
- A `.bak` copy of the original TLK is made automatically before the first
  write (never overwrites an existing `.bak`, so re-running is safe).
- The manifest records, per StrRef, the resref/text-hash/voice used. Re-runs
  skip unchanged entries and only retry failures.

## Known limitations

- Windows-only (SAPI).
- Resrefs are derived deterministically from the StrRef number and `--prefix`,
  with no cross-check against existing BIFF/override resources. Collisions are
  astronomically unlikely with a distinctive prefix.
- The CRE name-matching heuristic (strip leading `B`, trailing `J`/`P`,
  trailing digits, trailing `A`/`E`, wildcard fallback) is tuned against
  BGEET naming conventions. It gets most names right and the occasional one
  wrong. Run `speakers` and review `speaker-names.json` before committing to
  a full generation pass if accuracy matters.
- Token substitution uses the values set in `patcher-config.json`. A pre-baked
  line cannot know the player's actual name or chosen gender — the same
  compromise real Bioware voice actors worked around.