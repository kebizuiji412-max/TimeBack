# TimeBack — a local file-state time machine for Windows

[中文](README.md) | **English**

> **Product name**: TimeBack (Chinese name: 回溯)
> **Tagline**: It turns "Ctrl+Z only works inside one application" into "travel back through the real state and history of files on this machine."
> **Project principle**: Record facts; never fabricate causality. If something is recoverable, say so; if a limit exists, state it plainly.

---

## 1. Current status

| Item | Status |
|---|---|
| Stage | **Phases 1–5 complete**, plus first-run onboarding rework, repositioning as a small Windows tool, a mini file browser with custom restore/delete, and configurable history cleanup and storage location |
| Build | Offline build passes (zero NuGet dependencies) |
| Tests | **189 / 189 passing**, including real-filesystem end-to-end runs, time-display/time-zone consistency, cross-timestamp custom restores, custom deletion, protection-range removal, whole-directory restores, and pre-release permission/scope/privacy cases |
| Code health | One complexity/duplication/dead-code audit completed, followed by surgical cleanup: `MainViewModel` went from 3402 lines down to **3102 lines** (about −9%), with the tests untouched |
| Architecture | A separate **`LastRegret.Runtime`** library (composition root) with **zero WPF dependency**; an **experimental Agent CLI** now sits on top of it (`code/src/LastRegret.Agent`, protocol `timeback-agent 1.0`) — the GUI does not depend on it, and it is not part of the release package |
| Pre-release review | Completed: 2 blocking findings and 3 high-risk findings were all fixed; a re-scan found no blockers |
| Release | `build.ps1 -Target publish` produces a distributable zip plus SHA256; the Release build contains no `.pdb` symbols |
| Application | WPF app verified to launch; **default window 780 × 520** (minimum 600×380, resizable and maximizable) |
| UI shape | Compact top tab navigation (no left sidebar), tight toolbar, table-style lists, small buttons, single-line status bar |
| First-run acceptance | Four rounds of hands-on walkthroughs (14-step first use, 8-step no-manual use, three-step flow correction, overall compaction) |
| Known limitations | Windows only; processes are attributed on a best-effort basis; see section 7 |

---

## 1.1 What it looks like: a small tool, not a background service

- **Window**: 780 × 520 by default — the size of an ordinary small desktop utility; enlarge it if you need more room.
- **Navigation**: one row of small tabs (Home / History / Restore / Settings), with a "protecting ..." line on the right instead of taking up width on the left.
- **Home**: two lines of protection status, a "recent changes" table, and two buttons (view history, recover a previous state).
- **History**: a table (time / type / file) with a details pane for the selected row.
- **Restore**: a single toolbar — `timestamp [▼] → restore to [▼] [current state] [⇄] [confirm]` — and below it a **mini file browser** that shows the real file structure at the selected point in time. Double-click to enter a directory, tick files to add them to the **restore set**. The footer offers select-all / clear / invert (applying only to the visible list) plus a selected-count readout.
- **Custom restore**: the restore set may **mix files picked at different timestamps**. Pick one file from point A, switch to point B, pick another, then restore once — each file is restored to the version from the moment it was picked.
- **Custom delete**: tick files, click "mark for deletion", and they are removed on execution. Restore and delete can run in the **same** execution (one file restored while another is deleted). Before deleting, content copies are kept and a safety point is created, so a mistaken deletion can be undone wholesale.
- **Settings**: protected folders, history retention, disk space, **storage location**, and **history cleanup**, with everything else folded into a collapsed "advanced" section.
- **Removing protection**: each protected directory has a remove action that asks what to do with its history (delete it, keep it, or cancel). Files on disk are never touched.
- **Choosing where data lives**: storage location → "change folder" writes both history data and logs to the selected place (effective after restart), and offers "open current location" and "reset to default".
- **Deleting records**: single history rows can be deleted from the history tab, the current scope's change records can be cleared from settings, and all history data (recovery points plus the content store) can be wiped to actually reclaim disk space. None of these touch the original files on disk.
- **Scrollbars**: no visible scrollbar rails; the mouse wheel does the scrolling.

> Principle: **features may be many, but the UI must stay small.** No oversized cards, heavy rounding, large empty margins, or hero sections.

### Three independent restore concepts

| Concept | Decided by |
|---|---|
| **Source state** | the `timestamp [▼]` picker — which historical state you are browsing |
| **Target state** | the `restore to [▼]` picker — defaults to "current state", another timestamp is also allowed |
| **Restore selection** | the mini file browser — pick files from the source state; switching timestamps or directories loses nothing |

The timestamp only chooses which historical state you browse. **What actually gets restored is the set you picked yourself.**

---

## 1.2 The only three things a normal user needs to know

| # | Concept | How the UI puts it |
|---|---|---|
| 1 | **What to protect** | "Choose a folder to protect" — protect a folder and you can return to an earlier state after mistakes or accidental deletions. |
| 2 | **What to do when a file breaks** | "Recover a previous state" — pick a time, see what will change, confirm. It can be undone at any point. |
| 3 | **What to do after a bad restore** | The completion screen puts "Undo this restore" in a red-bordered box, so there is nowhere else to look. |

> Complex capabilities (retention modes, exclusion rules, cleanup schedules, run logs, related-process attribution) are all still there — they simply live under a collapsed "advanced" section in Settings.

---

## 2. Technology stack (driven by measured machine constraints)

| Layer | Choice | Why |
|---|---|---|
| Language / runtime | **C# / .NET 8** (`net8.0` + `net8.0-windows`) | WPF reference packs ship with the SDK, so builds work fully offline. The SDK location is supplied with `-DotnetPath` or `LR_DOTNET`; no machine-specific path is hardcoded in the repository. |
| UI | **WPF** (dark, native-feeling, high information density) | Bundled with the same SDK, no third-party packages needed. |
| Database | **SQLite via the system-provided `winsqlite3.dll`** plus a hand-written P/Invoke binding | The package feed is completely unreachable, so `Microsoft.Data.Sqlite` cannot be restored. |
| File watching | **`ReadDirectoryChangesW` with overlapped I/O** (hand-written) | `FileSystemWatcher` silently swallows buffer overflows, which would be fatal for this project. |
| Content storage | **Content-addressed storage** (SHA-256, optional ZLib compression) | Identical content is stored once; A→B→A consumes a single copy of the space. |
| Process attribution | Candidate process list plus kernel handle probing (best effort) | Cross-process `PROCESS_DUP_HANDLE` is largely refused by the OS in practice, so the feature is honestly downgraded. |

### Directory layout

```
project-019-zuihouhui-de-ctrlz/
├── README.md                 # Chinese version
├── README.en.md              # this file
├── docs/                     # design and protocol documents
├── code/
│   ├── LastRegret.sln
│   ├── Directory.Build.props
│   ├── NuGet.config          # cleared package sources: any PackageReference fails immediately (offline constraint)
│   ├── build/build.ps1       # one command for build / test / run
│   ├── src/
│   │   ├── LastRegret.Core       # pure domain logic (no IO, no platform dependency, heavily unit-testable)
│   │   ├── LastRegret.Windows    # all P/Invoke: SQLite, ReadDirectoryChangesW, handles, CAS
│   │   ├── LastRegret.Data       # repository layer: 13 tables, transactions, crash self-check
│   │   ├── LastRegret.Engine     # engine: watch scheduling, merger, snapshots, restore, space management
│   │   └── LastRegret.App        # WPF UI plus composition root
│   └── tests/LastRegret.Tests    # hand-written test framework (zero dependencies)
└── assets/                   # icons and other assets
```

---

## 3. How to run it

```powershell
# Enter the project (replace <clone-dir> with your own path)
cd "<clone-dir>\code"

# Optionally point at a .NET SDK (the repository hardcodes no machine-specific path)
$env:LR_DOTNET = "D:\somewhere\dotnet\dotnet.exe"

# Build
pwsh -File build\build.ps1

# Build and run the full test suite
pwsh -File build\build.ps1 -Target test

# Build and launch the app
pwsh -File build\build.ps1 -Target run
```

> The build script locates a portable SDK automatically and redirects the NuGet cache and the CLI home into the working area (otherwise it would read a `NuGet.Config` under the user profile that may be access-denied), and it **forces serial builds** (`-m:1`).
> `build.ps1` uses a UTF-8 BOM for compatibility with how Windows PowerShell 5.1 parses script files.
> All three targets (`build` / `test` / `run`) have been verified end-to-end with the exact invocations above.

### Where data lives

| Content | Location |
|---|---|
| Database and content objects | `%LOCALAPPDATA%\LastRegret\data\` (`events.db`, `objects.db`, `store\objects\`) |
| When the data directory is not writable | falls back to a `LastRegretData\` folder next to the executable and says so in the startup self-check |
| Crash log | `logs\crash.log`, beside the data directory |

---

## 4. Implemented capabilities (all of these really run — none are UI mockups)

### 4.1 Protected scope

- You **choose** one or more folders; nothing is monitored by default.
- Each directory can be paused, resumed, removed from protection, snapshotted immediately, **rescanned to fill gaps**, or have its scan cancelled.
- **Adding a directory never blocks the UI**: registration and watching are instant, while the expensive first baseline scan runs in the background. A **size assessment** runs first (enumeration only, no content reads; about 14 seconds for a directory with hundreds of thousands of files) and honestly reports how many files, how much data, how long it will take, how much space it will cost, and which files can be restored — then you decide whether to start.
- The UI stays fully usable during a scan and the scan can be cancelled at any time. Whatever was already scanned stays valid, and a rescan skips files that were already covered.
- Recovery points created before the baseline scan finishes are marked "content incomplete" with the reason, so they cannot be misused.
- The application refuses to add its own data directory to the protected scope, since restoring it would corrupt the history database itself.

### 4.1b Retention modes (they decide whether content can be restored)

Three modes, each with its cost and capability stated in the UI:

| Mode | Behaviour | Measured cost (directory with hundreds of thousands of files, tens of GB) |
|---|---|---|
| **Full content** | keeps the historical content of every file | ≈110 files/sec; history footprint up to roughly the size of the source directory |
| **Smart retention** (default) | keeps content only for files below a threshold (4 MB by default) and records facts only for larger files | a middle ground; configuration, code, and document directories are essentially fully covered |
| **Facts only** | records which file changed and when, keeping no content | ≈3500 records/sec, zero space; **content cannot be restored** (clearly labelled in the UI) |

> These figures come from measurements on real directories, not estimates.

### 4.2 Watching and events (real `ReadDirectoryChangesW`)

- Recognises **create / modify / delete / rename / move** (plus transient changes, collapsed in the timeline by default).
- **Deduplication and merging**: repeated notifications for the same path and action within 120 ms are absorbed; consecutive modifications of one path merge into a single entry that records how many were merged.
- **Delayed confirmation**: content is captured only once a file has settled, so half-written files are never read.
- **Rename pairing**: `OLD_NAME` + `NEW_NAME` are paired; if the target already existed, the result is classified as a modification rather than a creation.
- **Editor atomic replaces**: when a temporary file is renamed over the real one, only the real file's change is recorded, with no spurious create/delete pair.
- **Exclusion rules**: `node_modules`, `.git`, `*.tmp` and similar are downgraded to collapsed events — the fact is still recorded, but it does not drown out the signal.
- **Buffer overflow**: when the kernel reports an overflow, the app says so immediately and triggers a full rescan. It never pretends nothing happened.

### 4.3 Storage (incremental; the whole folder is never copied)

- **Content-addressed storage**: addressed by SHA-256, so identical content is stored once (A→B→A occupies one copy of the space).
- **A snapshot is a complete manifest**: every snapshot can be used for restore independently of the event log; the incremental part is only the rows added relative to its parent snapshot.
- **Crash safety**: WAL plus `synchronous=FULL`, atomic writes (temp file → verify hash → atomic rename), and an `integrity_check` at startup.
- **Rebuildable content index**: the object files on disk are authoritative; a damaged index can be rebuilt from them.

### 4.4 Home / History / Restore (from a normal user's point of view)

**Home** answers three questions: what is being protected now, what happened recently, and what to do when something breaks. With no folder protected, it is a single screen inviting you to protect a folder, without exposing retention modes, retention days, or cleanup schedules.

**History** answers "what happened before":
- On the left, one change per row (time / create·modify·delete / path / size).
- In the middle, changes within five minutes of each other are grouped into one block ("today 13:58 · 5 created · 3 modified · 4 deleted") so that a normal user can match it against what they just did.
- On the right, details for the selected row: plain language first (when, what happened, whether it can be undone), with technical detail (possibly related programs, content fingerprint, recording method) at the end.

**Restore** is a fixed three-step flow with the progress always visible in the header: choose a time, see what will change, confirm. The first step lists human-readable timestamps; the second states exactly what will happen; the third lists every affected file (action, path, size, whether it is possible), asks for confirmation, and states that the current state is saved automatically and that a restore can be undone with one click. Completion shows a green "restored" banner plus an "Undo this restore" button in a red box.

### 4.4b Terminology is always user-facing

| Internal wording | Wording shown to users |
|---|---|
| baseline / establish baseline | preparing protection / protection started |
| snapshot | recovery point (still called a snapshot internally) |
| event | file change |
| restore operation | restore record |
| manual / baseline / safety snapshot | one you left / taken when protection started / taken automatically before a restore |
| related process | possibly related program (explicitly labelled "correlation only, not causation") |
| timeline | history |

Raw notification counts, merge/dedup ratios, and buffer-overflow counters moved from the status bar to a run log under advanced settings.

### 4.4c Timeline, comparison, and diff (internal capabilities, unchanged location)

- The timeline groups by day and shows time, action badge, path, **related process (worded by confidence)**, size, and merge count.
- **Timestamps are chosen from a list, never a slider.** An earlier version used a slider at the top of the timeline and it was unusable in practice: all points collapsed into one, clicking the same pixel twice selected different timestamps, and nothing labelled which segment was which moment — so the desired point could not be selected at all. It is now a list, one timestamp per row.
- **State comparison**: pick a timestamp, compare against the current state, and get counts for added / deleted / modified / renamed.
- **Text diff**: line level plus inline character ranges, marking exactly which characters changed. **Binary content is never force-diffed as text** — only size, hash, time, and origin are reported.
- **Times are always displayed in local time**: every time column stores UTC ticks and is converted back to local time on read. An earlier version treated `ts_local` as if it were already local, so displayed times were off by one time zone (eight hours under UTC+8) and the whole "look up a snapshot by local time" chain failed silently.

### 4.5 Restore: six hard rules, each backed by code

1. **Preview before execution**: preview and execution are separate, and execution verifies the preview fingerprint.
2. **A safety point always precedes execution**: a full snapshot of the current state is taken first; if any file's content cannot be read, the restore is aborted.
3. **Differences only**: a whole directory is never overwritten; every step maps to a line in the preview.
4. **Skip on conflict**: if the on-disk content differs from what the preview saw, that step is skipped and reported honestly — never silently overwritten.
5. **Per-step accounting**: the pre-execution content of every step is written to CAS and to the database, so after a crash it is possible to tell how far execution actually got.
6. **A restore is itself undoable**: undo means running another restore whose target is the pre-restore safety point, so it can equally be previewed, traced, and undone again.

### 4.5b Custom management of recovery points

- **Deleting a single recovery point**: any middle point can be deleted, but three hard protections refuse with a reason — a point referenced by a restore (deleting it would make that restore impossible to undo), the newest point of a scope (deleting it would remove the timeline's anchor to the current state), and the only baseline.
- **Adding or editing notes**: name a point, for example "before the upgrade" or "before sending to the client".
- **Batch cleanup of old automatic points** by retention days; manual points, baselines, and pre-restore safety points are always kept.
- **Every restore record is individually undoable**: each completed restore carries an undo button, and when undo is impossible the button states why (already undone, no safety point, not completed successfully) instead of leaving a grey button to guess at.

### 4.6 Space management

- Shows history-data usage, protected-scope size, number of historical versions, space saved by deduplication, configured limits, and free disk space.
- Policies cover retention days, maximum usage, per-file retention limit, and automatic reclamation when space runs low.
- **Iron rule**: content referenced by any snapshot is never deleted. Automatic cleanup only reclaims objects that no recovery point references, so "restore to a historical point" can never break, no matter how much cleanup runs.
- Recovery points (baseline, manual, pre-restore safety) are not cleaned up automatically by default.

---

## 5. Tests

```powershell
pwsh -File build\build.ps1 -Target test
```

The suite covers path utilities (separator unification, `..` escape rejection, case-insensitivity, non-ASCII/space/special/emoji names, over-long paths), content classification (UTF-8 / BOM / UTF-16 / GBK, binary signatures, NUL bytes, empty files), diffing (identical content, single-line edits, inline-diff invariants, empty-file transitions, CRLF, 8000-line text), the SQLite binding (version, parameter round-trips including 64-bit integers, non-ASCII text, BLOBs and times; NULL semantics; multi-statement scripts; transaction rollback; integrity and foreign-key checks), content-addressed storage (deduplication, index rebuild, atomic restore, compressed write/read round-trip, corruption detection, refusal to restore corrupted objects), watching and events against a real filesystem (create/modify/delete/rename/cross-directory move, merge of consecutive saves, editor atomic replace, non-ASCII names, empty files, over-limit large files, batches of 120 created files, bulk deletes, empty directories, exclusion rules, baseline restore, snapshot integrity, realignment after downtime, synthetic notification merging, statistics consistency), the restore loop (full restore, undo, restoring deleted files, confirmation for added files, fingerprint verification, conflict skipping without overwriting, repeated restores, traceable history chains, abort when a safety point is missing, crash-residue detection and repair, per-step accounting), protected-directory and recovery-point management, retention modes, time display (a time read back from any snapshot, event, restore record, or historical version must equal the real local time of the event), whole-directory restores, and pre-release permission/scope/privacy cases.

Also included are the scenarios named in the product requirements: program crash (a forged running operation → detected and repaired), repeated restore, and modify-then-restore again.

---

## 6. Explicitly out of scope (phase-one boundaries)

- Restoring arbitrary registry changes, Windows settings, or application-internal state.
- Restoring cloud data, network requests, sent messages, or remote databases.
- Restoring payments or physical device state.
- Intercepting and undoing PowerShell / CMD / Terminal commands themselves (command history and its blast radius are a later phase).
- Paths that were created and then deleted while the machine was off (no trace remains on disk; physically unobtainable).
- Deciding with certainty that a specific process changed a given file (not achievable on this platform, so the app only reports "related process / possible source").

---

## 7. Known limitations (recorded honestly, not glossed over)

| Limitation | Detail |
|---|---|
| Process attribution accuracy | Cross-process handle enumeration is largely refused by the OS (in one measurement, only 2 of 281 processes could be opened), so most events can only name processes active around that moment. |
| Changes while the machine is off | File mtimes are used as evidence, so several modifications collapse into one, and paths that were created and then deleted cannot be discovered. |
| Very large files | Beyond the retention limit (64 MB by default) only the fact of the change is stored; content is not kept, and both the event and the UI state clearly that it cannot be restored. |
| ANSI text such as GBK | The .NET runtime does not ship code-page encoders by default, so such text is handled as local ANSI and may display as mojibake — but it is never passed off as correct text. |
| Binary diff | Text comparison is deliberately not attempted; only metadata is offered for checking. |
| Timestamp list height | The timestamp list on the timeline has a fixed height (about five rows) and scrolls when there are many points; making it adjustable or grouped is future work. |

### 7.1 Problems found during first-run acceptance (all fixed)

| # | Problem | Nature |
|---|---|---|
| 1 | The timestamp was chosen with a slider, and the wanted point could not actually be selected | interaction design — replaced with a list |
| 2 | "Compare with current" and "preview restore" rendered on another tab, so clicking appeared to do nothing | interaction design — the app now switches tabs automatically |
| 3 | "Confirm restore" never appeared after a preview (writing back the selection cleared the preview state) | code defect (two ListBoxes bound to the same selection fought each other) |
| 4 | The app exited immediately after a restore finished (`XamlParseException` from binding `Run.Text` to a read-only property) | code defect (fatal) |
| 5 | The UI displayed UTC as if it were local time (eight hours off under UTC+8), and the whole "look up a snapshot by local time" chain failed silently | code defect |
| 6 | The timeline's empty-state hint was squeezed into a small card in the lower left while the main area showed configuration items | interaction design |

---

## 8. Deliverables

| Artifact | Path |
|---|---|
| Executable | `code\src\LastRegret.App\bin\Debug\net8.0-windows\win-x64\LastRegret.exe` |
| One-command build script | `code\build\build.ps1` |
| Test suite | `code\tests\LastRegret.Tests` (`pwsh -File build\build.ps1 -Target test`) |
| Database schema | `code\src\LastRegret.Windows\Sqlite\Schema.cs` (13 tables plus indexes) |
| Design notes | `docs\` |

### Phase plan

| Phase | Content | Status |
|---|---|---|
| Phase 1 | Project setup, database, directory management, file watching, event recording | complete |
| Phase 2 | File version history, content hashing, incremental storage, object deduplication | complete |
| Phase 3 | Timeline UI, event browsing, file history, diff | complete |
| Phase 4 | Restore preview, restore engine, pre-restore safety point, restore undo | complete |
| Phase 5 | Exception handling, disk space management, history cleanup, background service | complete (tray residency still pending) |
| Phase 6 | Association with shells, editors, version control, registry, and shadow copies | not started (explicitly outside the phase-one commitment) |

---

## Data and privacy

> This section answers one question: **could this software expose something it should not?**

### It does not use the network

| Check | Method | Result |
|---|---|---|
| Source calls | searching the whole source tree for `HttpClient` / `System.Net` / `WebClient` / `Socket` | **0 hits** (the only `http://` strings are XAML namespace declarations) |
| Compiled artifact | reading the assembly reference table of `LastRegret.dll` | `System.Net.Http` / `Sockets` / `Primitives` / `Requests` / `WebClient` are **not referenced at all** |

In other words, **there is no network library for the assembly to call**, so silent uploading is not merely absent by policy but impossible in the build. There is no telemetry, no analytics, no crash reporting, and no third-party SDK (zero NuGet dependencies).

### What it stores and where

| Item | Location | Content |
|---|---|---|
| History database | `%LOCALAPPDATA%\LastRegret\data\events.db` | full paths of protected directories, change records, recovery points, file index |
| Content store | `%LOCALAPPDATA%\LastRegret\data\objects.db` + `store\` | content copies of protected files, deduplicated by content hash |
| Settings | `%LOCALAPPDATA%\LastRegret\data\*` + `storage.json` | only thresholds and retention mode — **no credentials of any kind** |
| Crash log | `%LOCALAPPDATA%\LastRegret\data\logs\crash.log` | exception stacks, with local paths automatically redacted |

- Nothing is encrypted (these are ordinary SQLite files), so other processes on the same machine can read them. That is normal for a local tool, but it should not be mistaken for encrypted storage.
- **No passwords, no tokens, no registry writes, no auto-start entries** (verified at the source level).
- The Settings page shows the data directory and provides buttons to open the data directory and the log directory, so you can inspect the contents at any time.
- To start completely fresh, Settings offers a "clear all data and remove the protected scope" action that returns the app to a never-used state.
- **Original files on disk are never deleted or modified by these operations.**

### Paths in logs are redacted

The program operates on the filesystem all day, so exception messages naturally contain real paths. Before anything is written to disk, they are collapsed:

```
example:
C:\Users\me\Documents\Reports\report.docx   ->   C:\Users\me\…\report.docx
C:\notes.txt                                 ->   C:\notes.txt     (already shallow, kept as is)
```

The drive letter and the final file name are kept — otherwise the log would be useless for locating a problem — while the middle levels are omitted. The crash dialog states this as well, so logs can be shared with a developer without hesitation.

### Uninstalling

The program is portable (unzip and run), so **there is no installer and therefore no uninstaller**:

1. Delete the extracted folder to remove the program itself.
2. To remove the history as well, delete `%LOCALAPPDATA%\LastRegret`, or click "clear all data" in Settings first — the two are equivalent.
3. No registry keys and no auto-start entries were ever written, so there is nothing else to clean up.

> Note the reverse: **deleting `%LOCALAPPDATA%\LastRegret` discards every protection registration and all history.** Back that directory up before moving to another machine or reinstalling Windows.
