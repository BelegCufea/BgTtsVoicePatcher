# BgTtsVoicePatcher

Pre-bakes Windows SAPI text-to-speech audio for every **unvoiced** line in an
Infinity Engine `dialog.tlk` (Baldur's Gate / BG2 / IWD : Enhanced Edition) and
patches each line's `SoundResRef` in place, so the engine plays the generated
WAV exactly like it would a real recorded voice line.

This intentionally does *not* try to hook the engine at runtime (no EEex, no
memory patching of the executable). Since every possible line of dialogue
already exists as text in `dialog.tlk` before the game even launches, there's
no need for a live "read whatever's on screen" hook the way you'd need in a
game with dynamic text - everything can be generated and wired up ahead of
time, which is simpler and can't break across game patches.

## Requirements

- Windows (System.Speech wraps SAPI via COM - this won't run on Linux/macOS).
- .NET 10 SDK.
- At least one installed SAPI voice. Windows ships some by default; for
  noticeably better quality, install
  [NaturalVoiceSAPIAdapter](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)
  (the same tool Osmodium's Pathfinder SpeechMod points people at) and any
  natural voice you like - it'll show up via the `voices` command like any
  other SAPI voice.
- ffmpeg on PATH, only if you're using `--ogg`.

### Running this on a different machine than the game

The project folder is just code - copy it anywhere and `dotnet build`/`dotnet run`
(NuGet needs network access the first time, to restore `System.Speech`; cached
after that). You only need to bring along:
- `dialog.tlk`, copied from the game (for `scan`/`generate`)
- the `.dlg`/`.cre` export folders, if using `--dlg-dir`/`--cre-dir` for
  automatic gendering
- ffmpeg, if using `--ogg`

Afterwards, copy the generated `.wav`/`.ogg` files and the patched `dialog.tlk`
back to wherever the actual game lives, if that's not the same machine.

## Build

```
dotnet restore
dotnet build -c Release
```

## Usage

```
dotnet run -- voices

dotnet run -- scan --tlk "D:\Games\BG2EE\lang\en_US\dialog.tlk"
```

For automatic male/female voice assignment, mass-export both `.dlg` and `.cre`
resources with Near Infinity (covers BIFF-packed content, not just `override`)
into two folders. From there you have two options:

**Quick, fully automatic, no review step** - point `generate` straight at both
folders and it resolves gender live, per line, every run:

```
dotnet run -- generate ^
  --tlk "D:\Games\BG2EE\lang\en_US\dialog.tlk" ^
  --override "D:\Games\BG2EE\override" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --dlg-dir "D:\Games\BG2EE\AllDlg" ^
  --cre-dir "D:\Games\BG2EE\AllCre" ^
  --limit 50 --dry-run
```

**Reviewable** - run `speakers` first to get a file you can fix mistakes in
before generating:

```
dotnet run -- speakers --tlk "D:\Games\BG2EE\lang\en_US\dialog.tlk" --dlg-dir "D:\Games\BG2EE\AllDlg" --cre-dir "D:\Games\BG2EE\AllCre"
```

This writes `speaker-strrefs.json` (StrRef -> speaker, don't edit) and
`speaker-names.json`, pre-filled and grouped by in-game display name:

```json
{
  "Jaheira": { "JAHEIRA": "F", "BJAHEIR": "F", "TTJAHEIR": "F" },
  "Minsc":   { "MINSC": "M", "BMINSC": "M" }
}
```

Open `speaker-names.json`, fill in any `null` (a speaker the CRE lookup
couldn't find) or fix a wrong guess, then point `generate` at both files -
they take priority over the live `--dlg-dir`/`--cre-dir` lookup, so your
review sticks:

```
dotnet run -- generate ^
  --tlk "D:\Games\BG2EE\lang\en_US\dialog.tlk" ^
  --override "D:\Games\BG2EE\override" ^
  --male-voice "Microsoft David Desktop" ^
  --female-voice "Microsoft Hazel Desktop" ^
  --speaker-map "D:\Games\BG2EE\AllDlg\speaker-strrefs.json" ^
  --name-gender-map "D:\Games\BG2EE\AllDlg\speaker-names.json" ^
  --limit 50 --dry-run
```

Either way, the name-matching itself is a best-effort heuristic - it strips a
leading `B`, a trailing `J`/`P`, trailing digits, then a trailing `A`/`E`,
before falling back to a wildcard glob - not a guaranteed-correct one, so
expect the occasional wrong call on an unusual name. Anything that can't be
resolved at all falls back to `--voice`/`--default-voice`.

### Browsing everything: `report`

For a full read-only picture - useful while a long `generate` run is still
going, or just to filter/sort in Excel - `report` dumps every `dialog.tlk`
entry that has text to CSV (or JSON):

```
dotnet run -- report --tlk "D:\Games\BG2EE\lang\en_US\dialog.tlk" --speaker-map "D:\Games\BG2EE\AllDlg\speaker-strrefs.json" --name-gender-map "D:\Games\BG2EE\AllDlg\speaker-names.json" --override "D:\Games\BG2EE\override"
```

Columns: `StrRef, SystemName, RealName, Gender, HasSound, SoundResRef,
SoundFileExists, Text`. `HasSound`/`SoundResRef` come straight from
`dialog.tlk` itself, so `--override` is only needed for the extra
`SoundFileExists` check (did the actual file survive on disk) - everything
else works without it. Like `generate`, it accepts either
`--speaker-map`/`--name-gender-map`, a live `--dlg-dir`/`--cre-dir` scan,
both (file takes priority), or neither (StrRef/sound/text still show). No
synthesis happens - it's pure reads, so it's fast and safe to run alongside
a generation pass.

Drop `--dry-run` (and raise/remove `--limit`) once you're happy with the
sample output and the chosen voice.

### Options

| Option | Default | Notes |
|---|---|---|
| `--tlk` | required | Path to `dialog.tlk` (typically `<GameRoot>\lang\<lang_code>\dialog.tlk`) |
| `--override` | required (generate only) | Where WAVs are written - typically `<GameRoot>\override` |
| `--manifest` | `<tlk dir>\tts-manifest.json` | Resumability/audit log |
| `--voice` / `--default-voice` | system default | SAPI voice name, see `voices`. Can also be the literal word `male` or `female` to reuse whichever of `--male-voice`/`--female-voice` you already set, instead of typing it twice |
| `--male-voice` / `--female-voice` | none | SAPI voice used for lines resolved to M/F via any of `--gender-map`, `--speaker-map`+`--name-gender-map`, or `--cre-dir`; falls back to `--voice`/`--default-voice` otherwise |
| `--gender-map` | none | Path to a small hand-maintained JSON file `{ "12345": "F", "67890": "M" }` keyed by StrRef, for one-off manual overrides. Checked first |
| `--speaker-map` | none | The `speaker-strrefs.json` produced by `speakers`. Checked second |
| `--name-gender-map` | none | The reviewed `speaker-names.json` produced by `speakers`. Checked second, alongside `--speaker-map` |
| `--dlg-dir` | `--override` | Directory of `.dlg` files to scan live for speaker names (a Near Infinity mass-export folder, or your real `override` if that already has the relevant ones). Used by both `speakers` and as `generate`'s last-resort live lookup |
| `--cre-dir` | none | Directory of `.CRE` files (Near Infinity mass-export) - enables automatic male/female resolution via each speaker's Sex byte, checked last in `generate`. Omit to skip auto-gendering entirely |
| `--skip-unmatched` | off | Exclude lines with no known speaker at all (the same set `speakers` writes to `speaker-unmatched.txt`) - usually non-dialogue text rather than missed NPC lines. Triggers a `--dlg-dir` scan even without `--cre-dir`. Not applied when `--strrefs` is given |
| `--out` | `dialog-report.csv` next to the TLK | `report` only: output path |
| `--format` | inferred from `--out`'s extension, else `csv` | `report` only: `csv` or `json` |
| `--rate` | `0` | -10 (slow) to 10 (fast) |
| `--ogg` | off | Transcode each line to Ogg Vorbis via ffmpeg right after synthesis, replacing the file under the same `.wav` filename (matches Voices Voices Extravaganza - its `.wav` files are actually OggS streams internally; EE sniffs the real format rather than trusting the extension) |
| `--ffmpeg` | `ffmpeg` | Path to ffmpeg.exe if it's not on PATH |
| `--ogg-quality` | `2` | libvorbis quality scale, 0-10 (higher = bigger file/better quality) |
| `--volume` | `100` | 0-100 |
| `--prefix` | `TS` | 2-char prefix for generated resrefs, e.g. `TS0009IX` |
| `--encoding` | `windows-1252` | Use `windows-1250` for Czech fan-translated TLKs, or any other code page name |
| `--min-length` | `2` | Skip candidates shorter than this after cleaning |
| `--charname` | `friend` | Stand-in word for the `<CHARNAME>` token (can't know the player's actual name ahead of time) |
| `--limit` | unlimited | Cap how many candidates are processed - use this for testing |
| `--dry-run` | off | Show what would happen, write nothing |
| `--strrefs` | none | Path to a flat JSON array of StrRef numbers, e.g. `[12345, 67890]`, to (re)generate exactly those lines instead of scanning the whole TLK for unvoiced ones. Listed lines are processed even if already voiced, and always resynthesized fresh (skips the manifest reuse check) - for deliberately redoing specific lines. Ignores `--limit` |

## How it works

- `dialog.tlk` (TLK V1) has an 18-byte header followed by one fixed 26-byte
  record per StrRef: a flags word, an 8-byte `SoundResRef`, volume/pitch
  variance (unused), and an offset+length into the strings section. See the
  [IESDP TLK V1 page](https://gibberlings3.github.io/iesdp/file_formats/ie_formats/tlk_v1.htm).
- "Unvoiced" = the entry has text but the sound-exists flag bit (`0x0002`) is
  unset / the `SoundResRef` is blank.
- For each candidate, the tool cleans dialogue tokens (`<CHARNAME>` etc.),
  synthesizes a 22.05kHz/16-bit/mono WAV via SAPI into your `override`
  folder, then seeks directly to that entry's 26-byte record and overwrites
  *only* the flags + `SoundResRef` bytes. Nothing else in the file moves, so
  there's no resizing and no risk to any other entry's offsets.
- A `.bak` copy of the original TLK is made automatically before the first
  write (it won't overwrite an existing `.bak`, so it's safe to re-run).
- The manifest records, per StrRef, which resref/text-hash/status was used -
  re-running skips entries that are already voiced or unchanged, and only
  retries ones that previously failed.

## Known limitations

- Windows-only (SAPI). On macOS you'd swap `VoiceSynthesizer` for the `say`
  command the way Osmodium's Pathfinder mod does; on Linux there's no
  first-party equivalent.
- Resrefs are derived deterministically from the StrRef number and a 2-char
  prefix, with no cross-check against the game's existing BIFF/override
  resources. Collisions are astronomically unlikely with a distinctive
  prefix, but if you want certainty, grep your extracted game resources for
  the prefix before a big run.
- Output is PCM WAV by default. Use `--ogg` to transcode each line to Ogg
  Vorbis via ffmpeg right after synthesis - it replaces the file in place
  under the same `.wav` filename, the same way Voices Voices Extravaganza's
  own ".wav" files are actually OggS streams internally (EE sniffs the real
  audio format rather than trusting the extension).
- `<CHARNAME>` and the other gender/relationship tokens are replaced with a
  fixed word since a pre-baked line can't know the player's actual name -
  this is the same compromise real Bioware voice actors worked around.
- The `--dlg-dir`/`--cre-dir` name matching is a heuristic tuned against one
  specific BGEET install's naming conventions, not a verified-correct lookup.
  It'll get plenty of names right and the occasional one wrong (especially
  generic/shared dialogues that were never a clean 1:1 with a single
  creature). Using `generate` directly with `--dlg-dir`/`--cre-dir` has no
  review step - a wrong call just means a wrong-gendered voice on that line,
  not a build failure. Run `speakers` first if you'd rather see and correct
  the guesses before committing to a full generation pass.
- The in-game display name in `speaker-names.json` comes from the CRE's own
  Long name StrRef (falling back to its Short name/tooltip StrRef). Generic
  creatures without either just get grouped under their own system name
  instead of a real display name.
