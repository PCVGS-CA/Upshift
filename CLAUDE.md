# Upshift: notes for Claude

## What it is

Upshift is a Windows desktop app. It finds the games on the PC, shows which upscalers each one ships with (DLSS, FSR,
XeSS, TSR…), suggests what suits the graphics card, and installs the official OptiScaler release into a game, with
a backup of every file it touches, a manifest of every change, an exact uninstall, updates with undo, and repair.
It's free and unofficial (not affiliated with the OptiScaler team, NVIDIA, AMD or Intel); everything it installs is
downloaded from each project's own GitHub releases.

## Stack

- WinUI 3, unpackaged (`WindowsPackageType None`, self-contained), .NET 8 (`net8.0-windows10.0.19041.0`), x64 only.
- **Windows App SDK 1.8 (1.8.260921001). Stay on 1.8, not 2.x.** (1.6 crashed with heap corruption, 0xc0000374.)
- Packages: CommunityToolkit.Mvvm 8.3.2, SharpCompress 1.0.0 (OptiScaler ships as .7z), System.Management 8.0.0.

## Build (x64)

Run from the folder containing `Upshift.sln`:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" Upshift.sln /p:Configuration=Debug /p:Platform=x64 /m /nologo /v:m
```

Output: `src\Upshift.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Upshift.exe`. The build must finish with no
errors and no warnings.

- **Release packages:** `powershell -ExecutionPolicy Bypass -File build\package.ps1 -OutDir <dir> -PublishDir <dir>`
  (defaults `releases\` and `publish\`, both git-ignored; use the scratchpad when testing). It runs `dotnet publish`
  (Release x64, self-contained, not single-file), then `vpk pack` (Velopack, pinned in `dotnet-tools.json`), and
  leaves `Upshift-Setup-x64.exe`, `Upshift-Portable-x64.zip`, `Upshift.App-<v>-full.nupkg`, `releases.win.json` and
  `release-notes.md`.
  - `EnableMsixTooling` must stay `true` in `Upshift.App.csproj`. Without it, `dotnet publish` leaves out
    `Upshift.pri` and the compiled XAML, and the published app crashes at start in `Microsoft.UI.Xaml.dll`
    (0xc000027b).
- If the user has Visual Studio debugging Upshift, the Debug output is locked. Build to another folder with
  `"/p:OutDir=<project>\src\Upshift.App\bin\x64\Verify\"`, and delete that folder afterwards, after asking.
- **Smart App Control is on.** It has blocked a freshly built `Upshift.dll` in that other folder ("An Application
  Control policy has blocked this file", Code Integrity event 3077). On 2026-09-28 it allowed the first
  `Upshift-Setup-x64.exe`, then blocked the rebuilt one. Never change that setting or work around a block; tell the
  user.

## Project layout and key files

- `src/Upshift.Core`: all logic, no UI.
  - `Discovery/`: game sources (Steam, Epic, GOG, Xbox, EA, Ubisoft, Battle.net, Heroic, itch.io, installed
    programs, drive scan).
  - `Detection/`: `GameAnalyzer` (exe choice, API, engine, anti-cheat, upscaler DLLs); `UpscalerList` ("Upscalers
    in this game"); `Fingerprints`.
  - `Catalog/`:
    - `catalog.json` (embedded resource) holds components with pinned versions and asset patterns, `installable`,
      `bundledIn`, `groupWith`, DLSS presets, `dlssVersionNames`, `guides` ("Which one should I pick?" text), FSR 4
      support, wiki sources and artwork sources.
    - `RemoteCatalog` loads a newer catalog from the `catalogUrl` setting.
    - `DlssNames` turns a DLSS version into its name, e.g. "310.9.1 (DLSS 4.5)".
  - `Components/`:
    - `GitHubClient` reads the releases API with an ETag cache and handles the rate limit.
    - `ComponentStore` downloads and verifies releases (size, SHA-256 digest, or git blob hash) into
      `components\{id}-{tag}` and keeps the 2 most recent.
    - `UpdateChecker` works out Stable (pinned) and Beta (newest), ordered by publish date.
  - `Install/`:
    - `OptiScalerInstaller.cs`: install, uninstall, configure (ini), and the manifest classes.
    - `OptiScalerUpdates.cs`: update with ini merge, undo, repair, and `Verify` (used by scans).
    - `OptiScalerChanges.cs`: the "Changes Upshift made to this game" list.
    - `OptiScalerOptions.cs`: upscaler, frame generation and DLSS model choices.
    - `IniFile.cs`.
  - `Launch/GameLauncher.cs`: Play through each store; never elevated.
  - `Wiki/`: PCGamingWiki (MediaWiki API, 30-day cache) and the OptiScaler wiki (compatibility list and game pages).
  - `Services/`: library cache, settings, data migration, and `AppLocations`. `AppLocations` holds the Velopack
    package id `Upshift.App`, `ExeDir` (from `Environment.ProcessPath`, never `AppContext.BaseDirectory`) and the
    check that the program and data folders never overlap.
- **Version:** set only in `Directory.Build.props`. `CHANGELOG.md` needs a matching `## [x.y.z]` section, which
  becomes the release notes.
- **Release workflow:** `.github/workflows/release.yml` runs on a `v*.*.*` tag push. It checks the tag against the
  version, runs `build\package.ps1`, and runs `gh release create` with the Setup, the portable zip, this version's
  nupkg(s) and `releases.win.json`.
- **Program folder (installed):** `%LocalAppData%\Upshift.App` (`current\`, `packages\`, `Update.exe`). There's a
  Start menu shortcut, no desktop shortcut, and the uninstall entry is at `HKCU\...\Uninstall\Upshift.App`. The
  portable zip has `Upshift.exe` (a Velopack stub), `Update.exe`, `.portable` and `current\`.
- `src/Upshift.App`: WinUI app.
  - `Program.cs`: the entry point (`DISABLE_XAML_GENERATED_MAIN`). It runs Velopack first (skipped for `--apply`),
    including the uninstall hook that asks whether to delete the data folder (No by default, closes itself after
    25 s). It also refuses to start if the program folder and data folder overlap.
  - `AppServices.cs`: shared services. `AppInfo` holds the version (informational version), `RepoUrl` and
    `AssetsDir`.
  - `Services/AppUpdates.cs`: Upshift's own updates (Velopack `GithubSource` on `AppInfo.RepoUrl`). It checks at
    start-up when "Check for updates automatically" is on, and from Settings > About. It downloads only on
    "Restart to update", and not while an install or game update is running.
  - `Services/InstallRunner.cs`: runs installs in-process, or through a UAC helper (`Upshift.exe --apply plan.json`)
    for folders like Program Files.
  - `Services/GameUpdates.cs`: update, undo and repair for games, plus the start-up check.
  - `Views/`: Library (details pane, Play, options, suggestions, changes), Updates (compact rows, "Used now" /
    "Used in a later version"), Settings.
- **In each game folder** Upshift writes only the OptiScaler files and `.upshift\`:
  - `manifest.json`: what was added or replaced, hashes, sizes, ini changes, last update and repair.
  - `backup<stamp>\`: the game's original files.
  - `undo<stamp>\`: the copy for "Undo last update".
  - `repair<stamp>\`: safety copies of files repair replaced.
  - Older installs use `.pcvgs\`, which is moved to `.upshift\` on the next change.
- **App data:** `%LocalAppData%\Upshift`.
  - Files: `settings.json`, `library.json`, `launch-options.json`, `wiki-cache.json`.
  - Folders: `components\` (plus `components\github-cache\`), `artwork\`, `optiscaler-wiki\`, `user-files\`,
    `pending\` (UAC plans) and `logs\install-yyyy-MM-dd.log` (every install, update, undo and repair).
  - Data from the old name (`%LocalAppData%\PCVGS\UpscalerManager`) is migrated once.

## Standing rules

- **Don't stop for small decisions.** Pick sensible defaults and list what you chose at the end.
- **Ask first** before touching files outside the project folder, deleting anything, or changing how the app
  installs into games.
- **Game folders:** only modify The Witcher 3 and Silent Hill 2, and only for testing. Before any test, check the
  folder is in the expected state (the user installs things between sessions) and stop if it isn't. Record hashes of
  every file before and after, and compare them.
  - Witcher 3: `C:\Program Files\GOG Galaxy\Games\The Witcher 3 Wild Hunt GOTY\bin\x64_dx12`
  - Silent Hill 2: `C:\Program Files\GOG Galaxy\Games\Silent Hill 2\SHProto\Binaries\Win64`
- **Never download or bundle `nvngx_dlssnr.dll` or the FSR 4.0.2c INT8 file (`amdxcffx64.dll`).** The user supplies
  them in Settings.
- **Games with anti-cheat stay blocked:** no install, update or repair.
- Games are never launched with admin rights; the main window never runs as admin.
- Report results honestly, including anything untested or failed.

## Environment quirks

- Source files use LF line endings. Write with the Write/Edit tools, or Python in binary mode.
- Bash heredocs mangle backslashes and apostrophes, so put scripts in files first.
- PowerShell `Get-Content`/`WriteAllText` can corrupt UTF-8.
- This machine's Python can't see some folders in `%LocalAppData%`; check with PowerShell.
- UI testing works with UI Automation scripts: select, invoke, set combos and boxes, screenshot with PrintWindow.
  Controls built in code need `AutomationProperties.Name` to be found.

## Done so far

- **Phase 1:** game discovery, detection and exe choice, cover art (Steam, GOG, Epic, Steam store, SteamGridDB),
  PCGamingWiki merged into "Upscalers in this game", OptiScaler wiki "Suggestions for this game", recommendations
  per GPU, the rename from PCVGS to Upshift (icons, data move, `.upshift`), and "Find it for me" for user files.
- **Phase 2 step 1:**
  - OptiScaler install and uninstall with backups and a manifest, plus the UAC helper.
  - Options: upscaler, frame generation, DLSS model and FSR 4 INT8.
- **Phase 2 step 2:**
  - Updates page with GitHub checks (ETag cache, rate-limit message, auto check every 6 hours, "Check now").
    Stable/Beta, "Keep updated", version picker, compact rows, and bundled items (fakenvapi, dlssg-to-fsr3)
    read from OptiScaler's release notes.
  - Game updates with ini merge, "Undo last update" (tested exact on Witcher 3), repair, the "Update" badge and the
    "Updates available" filter.
- **Extras:**
  - Play button through each store, with launch options and a hover play icon.
  - "Which one should I pick?" guides.
  - DLSS marketing names everywhere.
  - "Updated by Upshift" rows with a chip arrow.
  - A dated "Changes Upshift made to this game" list.
  - README.md and .gitignore.
- **Installer and self-updates (2026-09-28):**
  - Velopack 1.2.158: installer, portable zip, uninstall question, and self-updates from GitHub Releases.
  - "Check for app updates" and "Restart to update" in Settings > About, plus an "Update for Upshift" bar in the
    main window.
  - Version 1.0.0 set in `Directory.Build.props`. `CHANGELOG.md` and the release workflow added.
  - `Environment.ProcessPath` for the app's own folder.
  - The workflow has never run, and no tag or release exists yet. The user will publish the first one.
- **Git:**
  - Branch `main`, committed as "Upshift: phase 1 and 2 so far", then "Add project notes" (CLAUDE.md).
  - The commit identity is set for this repo only: `PCVGS <335109779+PCVGS-CA@users.noreply.github.com>`. No global
    identity is set on this PC.
  - `.gitattributes` keeps LF endings (`* text=auto eol=lf`), whatever `core.autocrlf` says.
  - Remote `origin` is the private GitHub repo `PCVGS-CA/Upshift` (https://github.com/PCVGS-CA/Upshift), created by
    the user from Visual Studio.
  - Push to `origin main` only at the end of each finished and tested step, or when the user asks. Never force-push.
  - No LICENSE yet (MIT or GPL-3.0 still to be chosen).
- **Current game state:**
  - Witcher 3 has OptiScaler v0.9.4 (updated from v0.9.3), with an undo copy in `.upshift\undo20260927-225122`.
  - Silent Hill 2 has OptiScaler v0.9.4, installed by the user.

## Next

1. **Phase 2 step 3:** updating DLSS / FSR / XeSS files in games. The NVIDIA DLSS, FidelityFX and XeSS components
   and their asset patterns are already in the catalog. Several are marked `verifyBeforeRelease`: check which files
   inside each download are the right DLLs.
2. **DLSS 5 support:** the OptiScaler DLSSNR fork, the AMD-NR fork and its runtime, and `NeuralSelector`.
3. **Code signing** for the installer and app (Azure Trusted Signing or a certificate): `vpk pack` takes
   `--signParams` / `--azureTrustedSignFile`. Unsigned files trigger SmartScreen and can be blocked by Smart App
   Control.

## Known open items

- Filter out REDlauncher / REDprelauncher and similar launchers. Play runs the store's reported exe, and for
  The Witcher 3 GOG reports the launcher.
- Plain-English help text for the DLSS models (the dropdown descriptions and preset notes).
- Installer test (2026-09-28), results:
  - Tested:
    - Setup installs per user without admin, with a Start menu shortcut and an uninstall entry.
    - Uninstall shows the question, removes the program folder, shortcut and registry entry, and leaves the data
      folder and the game folders hash-identical (the 25 s timeout path; no button was pressed).
    - The published build (the same files as `current\`) finds the existing data (22 games, covers, data path),
      and About shows 1.0.0.
  - Not tested, because Smart App Control blocked the rebuilt Setup:
    - The installed app actually starting.
    - The uninstall question's "No" and "Yes" buttons.
    - The portable zip.
    - A real self-update. This needs two releases, and the updater can't read releases while the repo is private.
      Velopack's `GithubSource` has no token, so the repo or its releases must be public.
- Size: about 94 MB Setup, 212 MB installed. Windows App SDK 1.8's ML runtime (onnxruntime and DirectML, 39 MB)
  comes in with the `Microsoft.WindowsAppSDK` metapackage. Referencing only the needed 1.8 sub-packages could drop it.
- Untested:
  - Play for EA, Ubisoft, Battle.net, Heroic and Xbox.
  - Play being disabled while a game is busy, and the launch-error box.
  - "Keep updated".
  - A real `catalogUrl`.
  - The Upscaler and Frame generation guide flyouts.
- OptiScaler.ini changes made before 2026-09-28 show "Date not recorded".
- The card's FSR chip can come from files OptiScaler added (their rows say so, but the chip doesn't).
- The user's Silent Hill 2 install has no line in the install log, although other installs are logged. Not
  explained yet.

## Files created outside this folder during the project

- **Claude's scratch folder** (about 210 MB, 114 items), all disposable:
  `%LocalAppData%\Temp\claude\C--Users-%USERNAME%-Downloads-PcvgsUpscaler-phase1\579f543a-cee3-485a-9f23-b377483c6a6d\scratchpad\`
  - `v1.6.250602001\` (163.5 MB) and `v1.8.260921001\`: Windows App SDK package files from the crash investigation.
  - `arttest\` (27 MB): console test harness against Upshift.Core, with a throwaway `fakegame\` install folder.
  - `corebuild\`: a Core-only build. `optiwiki\`: OptiScaler wiki pages fetched for tests.
  - Scripts:
    - UI automation: `ui.ps1`, `setbox.ps1`, `scrollto.ps1`, `elev.ps1`.
    - Folder hash snapshots: `tree.ps1`.
    - Python edit scripts: `*.py`.
  - About 54 screenshots (`*.png`).
  - Test snapshots: `w3-before-update.json`, `w3-after-undo.json`.
  - Copies of the Witcher 3 settings before testing: `witcher-ini-before.ini`, `witcher-manifest-before.json`,
    `w3-ini-093.ini`.
  - Backups: `catalog.before-updates.json`, `LibraryPage.xaml.orig`.
  - The DLSS programming guide (`dlss_guide.pdf` / `.txt`).
- **Claude's scratch folder for the installer session** (2026-09-28), all disposable:
  `%LocalAppData%\Temp\claude\C--Projects-Upshift\dac6047e-ca5e-4672-af97-e014e9bd2035\scratchpad\`.
  - `publish\` (212 MB) and `releases\` (about 270 MB).
  - Scripts: `snap.ps1` (hashes of the data folder and both game folders), `uninstall.ps1` (runs the registry
    uninstall string and answers the question), `ui.ps1` (copied from the old scratch folder).
  - Snapshots (`*.json`) and screenshots (`t1-library.png`, `t2-about.png`).
- **Installer leftovers:**
  - `%TEMP%\velopack\` (Velopack's own temp folder).
  - The vpk 1.2.158 tool, in the NuGet cache (`%UserProfile%\.nuget\packages\vpk`).
  - `%LocalAppData%\Upshift.App` was removed by the uninstall test and no longer exists.
- **App data created by tests** in `%LocalAppData%\Upshift`: `components\optiscaler-v0.9.3\` (downloaded for the
  update test) and `launch-options.json` (now empty). The rest is the app's normal data.
- **Claude's memory for this project:**
  `%USERPROFILE%\.claude\projects\C--Users-%USERNAME%-Downloads-PcvgsUpscaler-phase1\memory\`. Two notes: decide small
  choices; check the game folder before tests. It's keyed to the old folder path, so a session in the new folder
  won't see it. Both rules are repeated above.
