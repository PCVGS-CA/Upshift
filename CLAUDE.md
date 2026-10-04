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
  - Referenced as its components at the metapackage's pinned versions: WinUI, Foundation, InteractiveExperiences,
    DWrite, Base. Not the metapackage, AI, ML, Widgets or Runtime: the Runtime package makes a self-contained build
    unpack the whole framework MSIX (onnxruntime, DirectML…). This cut the published app from 211.6 to 164.1 MB.
- Packages: CommunityToolkit.Mvvm 8.3.2, SharpCompress 1.0.0 (OptiScaler ships as .7z), System.Management 8.0.0,
  Velopack 1.2.158.

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
  - 2026-10-03: the "1.0.1 crashes at startup" report (exit code 0xe0434352, faulting module KERNELBASE.dll, every
    time, from Visual Studio) was this block, not a code bug. Each of the user's crashes had a Code Integrity 3077
    event for the Debug `Upshift.dll` at the same second. With a debugger attached there's no ".NET Runtime" 1026
    event, so the user saw only "Application Error".
  - The next build (with the crash log) was allowed and ran from the exe, from Visual Studio and from a test
    installer.
  - **To tell the two apart:** look for 3077 events in Microsoft-Windows-CodeIntegrity/Operational at the time of
    the crash. If there are none, read `logs\crash.log`.
- **Crash log:** `Services/CrashLog.cs`, installed first thing in `Program.Main`. It records unhandled exceptions
  (AppDomain, XAML `UnhandledException`, unobserved tasks, and start-up failures in `Main` / `OnLaunched`) in
  `%LocalAppData%\Upshift\logs\crash.log`, falling back to `%TEMP%\Upshift-crash.log`.
  - It can't catch a code-integrity block: that stops Upshift.dll from loading before any of Upshift's code runs.
  - `Upshift.exe --crash-test` fails on purpose in `OnLaunched` to check the log works.
- **Installer tests:** run `build\package.ps1 -TestPackId Upshift.App.Test` so the test Setup installs to
  `%LocalAppData%\Upshift.App.Test`.
  - The regular Setup finds the user's real installed Upshift (through the sandbox) and offers to update it. Don't:
    edits to existing files may land in the real install while new files land in Claude's copy.
  - `-TestPackId` refuses to run with `-Tag`.
  - A test install from 2026-10-03 (1.0.1) is in Claude's sandboxed `%LocalAppData%\Upshift.App.Test`, not visible
    to the user.

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
    - `upscalerFiles` in `catalog.json`: the signers per family, and one source per DLL. Each source gives the file,
      its own version (not the SDK's), minVersion/belowVersion for the game files it may replace, component, tag,
      and `repoPath` or `archivePath`. The notes there explain which swaps are allowed and why.
    - `RemoteCatalog` loads a newer catalog from the online address: `AppSettings.DefaultCatalogUrl` (the raw
      `catalog.json` on `main` of PCVGS-CA/Upshift) unless the `catalogUrl` setting names another. It's used from
      the next start when valid and newer; otherwise the built-in copy is used. **Bump `"updated"` in catalog.json
      whenever it changes**, or installed copies won't pick the change up.
    - `DlssNames` turns a DLSS version into its name, e.g. "310.9.1 (DLSS 4.5)".
  - `Components/`:
    - `GitHubClient` reads the releases API with an ETag cache and handles the rate limit.
    - `ComponentStore` downloads and verifies releases (size, SHA-256 digest, or git blob hash) into
      `components\{id}-{tag}` and keeps the 2 most recent.
      - `ComponentStore.UpscalerFiles.cs` fetches single upscaler DLLs into `components\upscaler-files\{id}-{tag}\`.
        DLSS and FidelityFX DLLs come straight from the repo (git blob hash); XeSS comes out of the release zip,
        which is deleted afterwards.
      - Each DLL must have the catalog's version and a valid vendor signature.
    - `UpdateChecker` works out Stable (pinned) and Beta (newest), ordered by publish date.
  - `Install/`:
    - `OptiScalerInstaller.cs`: install, uninstall, configure (ini), and the manifest classes.
    - `OptiScalerUpdates.cs`: update with ini merge, undo, repair, and `Verify` (used by scans).
    - `OptiScalerChanges.cs`: the "Changes Upshift made to this game" list.
    - `OptiScalerOptions.cs`: upscaler, frame generation and DLSS model choices.
    - `UpscalerFiles.cs`: updating the DLSS / FSR / XeSS DLLs games ship, and restoring the originals.
      - `Items` gives each file's state: game file, update available, updated by Upshift, game restored its old
        file, changed since, or OptiScaler's copy.
      - `Update` checks everything first: same name, the file already there, inside the game, 64-bit, newer,
        vendor signature, hash. It then backs up each original once and rolls back on failure.
      - `Restore` puts the originals back, checked by SHA-256.
    - `SignatureCheck.cs`: WinVerifyTrust (no online revocation) plus the signer's O/CN against the catalog's list.
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
  - `Services/GameUpdates.cs`: update, undo and repair for games, plus the start-up check. `RunExclusiveAsync` is
    the one-change-at-a-time gate that upscaler updates share.
  - `Services/UpscalerUpdates.cs`: upscaler file update, restore and re-apply for the Library and the Updates page,
    the DLSS candidates, and the "DLSS downloaded" event behind the Library notice.
  - `Views/`: Library (details pane, Play, options, suggestions, changes), Updates (compact rows, "Used now" /
    "Used in a later version"), Settings.
- **In each game folder** Upshift writes only the OptiScaler files, the upscaler DLLs it updates, and `.upshift\`:
  - In the exe folder (OptiScaler):
    - `manifest.json`: what was added or replaced, hashes, sizes, ini changes, last update and repair.
    - `backup<stamp>\`: the game's original files.
    - `undo<stamp>\`: the copy for "Undo last update".
    - `repair<stamp>\`: safety copies of files repair replaced.
  - In the game's install folder (upscaler files, which can sit anywhere in the game):
    - `upscaler-files.json`: each file Upshift updated, its original hash, version and size, and Upshift's copy.
    - `originals\<relative path>`: the game's original of each.
    - OptiScaler's uninstall leaves these two alone when both records share a folder.
  - Older installs use `.pcvgs\`, which is moved to `.upshift\` on the next change.
- **App data:** `%LocalAppData%\Upshift`.
  - Files: `settings.json` (also `HiddenGames`), `library.json`, `launch-options.json`, `wiki-cache.json`.
  - Folders: `components\` (plus `components\github-cache\`), `artwork\`, `optiscaler-wiki\`, `user-files\`,
    `pending\` (UAC plans) and `logs\install-yyyy-MM-dd.log` (every install, update, undo and repair).
  - Data from the old name (`%LocalAppData%\PCVGS\UpscalerManager`) is migrated once.

## Standing rules

- **Don't stop for small decisions.** Pick sensible defaults and list what you chose at the end.
- **Ask first** before touching files outside the project folder, deleting anything, or changing how the app
  installs into games.
- **Game folders:** only modify The Witcher 3 and Silent Hill 2 (and other games only with the user's OK for that
  test), and only for testing. Before any test, check the
  folder is in the expected state (the user installs things between sessions) and stop if it isn't. Record hashes of
  every file before and after, and compare them.
  - Witcher 3: `C:\Program Files\GOG Galaxy\Games\The Witcher 3 Wild Hunt GOTY\bin\x64_dx12`, and the game root's
    `.upshift\` for upscaler files.
  - Silent Hill 2: `C:\Program Files\GOG Galaxy\Games\Silent Hill 2\SHProto\Binaries\Win64`, plus
    `SHProto\Plugins\DLSS|XeSS\Binaries\ThirdParty\Win64` and the root's `.upshift\` for upscaler files.
  - GOG's folders are writable without admin here, so these tests don't go through the UAC helper.
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
- **Claude's shell runs inside the Claude desktop app's MSIX package, so `%LocalAppData%` is virtualized** (found
  2026-10-03). What Claude's shell, Python and any Upshift it launches see as `%LocalAppData%\Upshift` is a private
  copy: writes land in `%LocalAppData%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\Upshift`, and reads of
  files Claude never wrote fall through to the real folder.
  - The user's own Upshift writes to the real `%LocalAppData%\Upshift`, which Claude can't see where its copy
    has the same file. Claude's copy froze around 2026-09-29 14:36; the user's logs, library and saved settings since
    are only in the real folder.
  - This, not "two windows overwriting library.json", explains the stale library and the missing Silent Hill 2 log
    line noted below.
  - Running something outside the package (e.g. through explorer.exe) was refused by Claude Code's permission
    check; don't try to get around it. Game folders and `C:\Projects` are not virtualized, so game-folder results are
    real.

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
- **Phase 2 step 3 (2026-09-28): upscaler files.**
  - **Research:**
    - DLSS v310.9.1 has `nvngx_dlss/dlssd/dlssg.dll` in `lib/Windows_x86_64/rel`.
    - FidelityFX's signed DLLs are in the repo: v1.1.4 `PrebuiltSignedDLL/` (FSR 3.1.4, 1.0.1.41314) and v2.3.0
      `Kits/FidelityFX/signedbin/` (loader 2.3.0, upscaler 4.1.1, frame generation 4.0.1).
    - XeSS SDK zips: 1.3.1 has `libxess` 1.3.1.32; 2.1.1 and 3.0.2 have `libxess` and `libxess_dx11` 2.0.2.68;
      3.0.2 has `libxess_fg` 1.3.1.78 and `libxell` 1.3.2.10.
    - None of them contain a 32-bit DLL or `amdxcffx64.dll`.
  - **Swaps allowed:**
    - DLSS Super Resolution 2.0+ and Ray Reconstruction 3.5+ → 310.9.1.
    - FSR 3.1 API DLLs 1.0.x → 3.1.4.
    - SDK 2.x DLLs → 2.3.0.
    - XeSS within its major file version, and XeSS 1.x only from 1.3.
  - **Swaps not offered:**
    - FSR 3.1 → SDK 2.x: it needs the upscaler DLL added.
    - XeSS 1.x → 2.x: Intel says a major version may break.
    - DLSS Frame Generation: it's paired with Streamline.
    - DLSS 1.x and FSR 2.
  - **UI:**
    - The DLSS bar and "Upscaler files" section, per-file Update, Update all, Restore original files, Re-apply.
    - "Update DLSS to unlock" in the DLSS model list, and the Library notice after a DLSS download.
    - The "Upscaler update" badge and the "Upscaler update available" filter.
    - "Update DLSS in all games" on the Updates page.
    - Corrected "who changed this file" wording, and the chip arrow only for Upshift's updates.
  - **Also:**
    - Plain-English DLSS model help.
    - Launchers (REDlauncher…) filtered out of the Library.
    - "Hide from library" and "Show hidden games".
    - The smaller Windows App SDK footprint.
  - **Fixes after the user's testing:**
    - **Live versions:** `Items` and `RefreshVersions` read every copy's version from disk, never the scan cache.
      The user's session had shown stale versions: its `library.json` writes never landed, cause unknown.
    - **Up to date:** already-current copies are skipped as "Already up to date", never an error dialog.
    - **OptiScaler's files:** `OptiScalerOwner` also covers an OptiScaler installed by hand, and `Update` refuses
      its files.
    - **Hand changes:** the first original is never replaced; a copy changed outside Upshift goes to
      `.upshift\set-aside\<stamp>\` before Upshift replaces or restores it.
    - **Section layout:** collapsed, titled "Upscaler files (advanced)", with a summary.
    - **Specific badges:**
      - The card badge says "DLSS update", "XeSS update" or "DLSS + FSR + XeSS updates" (it wraps on the card). Its
        tooltip lists each file as current → new.
      - The filter label names the technologies ("DLSS+FSR+XeSS updates"); its tooltip lists the games.
      - A release that isn't downloaded yet says "XeSS 3.0.2 available (downloads when you update)"; Update
        downloads it.
    - **Fresh cards:** `GameAnalyzer.RefreshQuick` runs when every card is built. It re-reads DLL versions, the mods
      next to the exe (the OptiScaler badge) and changed files, so cards don't depend on `library.json`.
      - Two Upshift windows at once each keep their own library in memory and overwrite `library.json`. The user's
        sessions at 20:27 and 20:51 on 2026-09-28 ran alongside Claude's, and their cache writes didn't last.
  - **Tested on both games** (hash snapshots `G0`–`G5` in the scratchpad):
    - Update, then Restore exact: 0 of 2,897 entries differed from before.
    - Update again. The Witcher 3 also went through a simulated "game put its old file back" → warning → Re-apply.
    - Silent Hill 2 also did DLSS + XeSS 1.3.0.28 → 1.3.1.32 → restore exact, before the final DLSS-only update.
    - Re-test after the fixes (snapshots `H0`, `H1`):
      - Each game has one `nvngx_dlss.dll`: The Witcher 3's `bin\x64_dx12` and Silent Hill 2's
        `SHProto\Plugins\DLSS\…`, both 310.9.1.
      - Both games' 18 OptiScaler files match their record and the v0.9.4 release.
      - The Witcher 3's hand-change case: 3.7.0 copied in by hand → detected on scan → Update (3.1.1 kept as the
        original, 3.7.0 set aside) → Restore exact to 3.1.1 → Update.
- **DLSS 5 (2026-09-28):**
  - **Forks:**
    - DLSSNR (`Dagherbou/OptiScaler_DLSSNR` v0.2.0-dlssnr, NVIDIA): `OptiScaler.dll/.ini`, the forwarder
      `nvngx.dll_dlssnr.dll`, and DLLs under `OptiScaler\`. It ships no `nvngx_dlssnr.dll`, and needs driver 616.56+.
    - AMD-NR (`3zwr1/AMD-NR---OptiScaler` Alpha0.3.2): the same layout plus `LmxxfNrRuntime.dll/.pak`. Its danielblnc
      runtime comes from the separate `amd-nr-runtime` zip. It ships no `amdxcffx64.dll`.
  - **Settings, all `[DlssNr]`** (checked in the fork's `Config.cpp` and `DlssNr_Menu.cpp` at v0.2.0-dlssnr):
    - `Enabled`.
    - `ToggleKey`: a Windows virtual-key code, read as an int and written decimal; −1/auto means unbound for
      DLSSNR, Home 0x24 for AMD-NR. Upshift has a "Set key…" picker and "Default".
    - `TransferStrength`: the fork's menu calls it "Detail strength" (slider 0-2, Reset 1.0). The fork's log shows
      it loaded, e.g. Silent Hill 2's `DlssNr.TransferStrength: 0.75`.
    - `ColourStrength`: "Colour strength" (slider 0-4 in the fork, 0-2 in Upshift, Reset 1.0).
    - `WorkingScale`: model resolution (50-100% in Upshift). Upshift's Reset buttons write `auto`.
    - `WhitePointSource` (0 paper white, 1 the game's exposure (default), 2 a scanned buffer): for games without
      exposure, the fork's menu says "paper white is in use. Try the scan instead". Upshift shows that as a tip, and
      mentions when `OptiScaler.log` shows `ExposureScan::Adopt` candidates.
    - AMD-NR also has `NrBackend = daniel | lmxxf`. It ships commented out, so it's allow-listed in
      `IsAddableIniKey`.
  - **Switching:**
    - `InstallOperation.SwitchBuild` runs `Update` in switch mode. It keeps the previous undo copy, saves a return
      point in `.upshift\switch<stamp>\` (files, `OptiScaler.ini`, `manifest.json`, `manifest.original.json`,
      `ini-after-switch.ini`), adds the user's DLSS 5 file and sets `NrBackend`.
    - `SwitchBack` restores the return point byte for byte (the manifest from `manifest.original.json`), then
      carries over settings changed while switched that the regular build also has.
    - "Undo last update" is off while switched. `manifest.Switch` is left out of the JSON when null.
  - **Core logic:** `Core/Install/Dlss5.cs` handles the file check (signature + `knownNvidiaNrFiles`), the rule
    sentence per card, the NVIDIA driver number (the last 5 digits of the Windows version's last two parts), the
    blockers and warnings, and the hotkey text.
  - **App:**
    - The UI is `Views/Dlss5View.cs`.
    - The pretend card is `AppServices.PretendGpu`. It's session-only and set from Settings > Developer options,
      which show only when Settings is opened with Shift held. Installs refuse while it's set.
    - Removing the DLSS 5 file asks to switch back the games using it (`GameUpdates.GamesUsingDlss5File`).
  - **Tested on The Witcher 3** with a dummy DLL Claude compiled (4 KB, unsigned, version 0.0.0.1; never
    launched):
    - The switch kept all 12 settings, and the toggle and sliders wrote `Enabled`, `TransferStrength` and
      `WorkingScale`.
    - After switching back, a second full cycle and Remove → Yes, the folder is exact against its pre-cycle snapshot
      (D2 = D3 = D4, 0 differences).
    - Against D0 the only difference is `manifest.json`'s timestamp (hash identical), from the first switch made
      before the byte-exact manifest fix.
    - Previews were taken for RTX 5080, RTX 3070, RX 9070 XT, RX 7900 XTX, RX 6800 and Arc B580 (`p1`–`p6` PNGs in
      the scratchpad).
  - **Not tested:**
    - AMD-NR installs (no AMD card here; its 516 MB + 106 MB downloads never ran).
    - Removing the DLSS 5 file with "No".
    - Switching through the UAC helper.
    - Repair of an AMD-NR install (repair only knows the main zip's files, not the runtime's).
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
  - Push to `origin main` only at the end of each finished and tested step, or when the user asks. **Never
    force-push.**
    - The one exception, already used: on 2026-09-29 the user allowed a single history rewrite and force-push to
      remove their Windows user name from CLAUDE.md in every commit. It was done with `git filter-branch
      --index-filter` (only the CLAUDE.md blob changed; every other file and the final tree are identical), and
      the old commits were pruned locally. That permission is used up.
    - The pre-rewrite history is backed up as a git bundle: `%USERPROFILE%\Documents\Upshift-history-backup-20260929.bundle`.
    - Never write the user name into the repo: use `%USERPROFILE%`, `%LocalAppData%` or `%USERNAME%` in paths.
  - License: MIT, "Copyright (c) 2026 PCVGS" (`LICENSE`).
- **1.0 release prep (2026-09-29):**
  - MIT `LICENSE`; Settings > About ends with the license line and the as-is disclaimer ("Made by PCVGS,
    pcvgs.com"); the README ends with the same.
  - The online catalog is on by default (see `RemoteCatalog` above). While the repo is private the raw URL returns
    404, so every install uses the built-in catalog until the repo is public.
  - README: Download (Releases page), First launch (SmartScreen: More info > Run anyway), 3 screenshots in
    `docs/screenshots` (library, details, updates), Reporting a problem, License.
  - Issue templates in `.github/ISSUE_TEMPLATE`: Bug report, Game not detected / wrong info, plus a link to
    OptiScaler's own issues.
  - CHANGELOG 1.0.0 shortened to a user-facing list.
  - Final check, all passed: Release x64 build with no warnings, `build\package.ps1`, Setup installs and the app
    starts with the existing 21 games and settings, uninstall with "No" leaves `%LocalAppData%\Upshift` and both game
    folders hash-identical, and the portable zip runs (1.0.0). Smart App Control didn't block anything this time.
  - No tag or release exists yet; the user will publish it.
- **Current game state** (2026-09-28, after the step 3 test):
  - Witcher 3 has OptiScaler v0.9.4 (updated from v0.9.3), with an undo copy in `.upshift\undo20260927-225122`.
    - DLSS was updated by Upshift: 3.1.1 → 310.9.1, with the original in the root's `.upshift\originals\`.
    - Its OptiScaler.ini has `Dx12Upscaler=dlss`, set at 18:12 that day by an Upshift session that wasn't
      Claude's (pid 18620).
    - DLSS model M (set by the user, 20:37).
    - The hand-change test's set-aside copy was deleted with the user's OK; `.upshift` holds only `originals` and
      the record.
  - Silent Hill 2 has OptiScaler v0.9.4, reinstalled by Claude on 2026-10-03 in the 1.0.1 test with the user's
    settings restored (DLSS upscaler, model M, frame generation off). It was on the DLSS 5 build (DLSSNR v0.2.0,
    with DLSS 5 turned off) before the test; switching back is one click in its DLSS 5 section.
    - DLSS (`SHProto\Plugins\DLSS\…`) was updated by Upshift: 3.7.0 → 310.9.1.
    - XeSS in `Plugins\XeSS` was updated by the user through Upshift: 1.3.0.28 → 1.3.1.32.
    - Both originals are in the root's `.upshift\originals\`, and DLSS model M is set.
  - RoboCop: Rogue City (changed for a test with the user's OK on 2026-09-28):
    - The user installed OptiScaler v0.9.4 there and updated DLSS 3.7.20 → 310.9.1 through Upshift.
    - Claude updated the game's own XeSS `libxess.dll` 2.0.0.18 → 2.0.2.68 and left it updated. The original is in
      the root `.upshift\originals\`, and its folder snapshots are R0 and R1.
    - Two XeSS updates remain there: `libxell` and `libxess_fg`.
  - Resident Evil Requiem (not a test game) was changed by the user through Upshift on 2026-09-28, with no OptiScaler
    there:
    - DLSS and Ray Reconstruction 310.3.0 → 310.9.1.
    - Its own FSR SDK 2.1 and XeSS 2 files → 2.3.0 / 2.0.2.
    - Originals are backed up in its root `.upshift\`.

- **1.0.1 (2026-10-03), bug fixes:**
  - **Uninstall** saves the game's OptiScaler.ini to `%LocalAppData%\Upshift\saved-settings\<game>\` (as
    `OptiScaler-<stamp>.ini` plus a `.json` saying where it came from; `Core/Install/SavedSettings.cs`) and then
    removes it even when the in-game menu changed it. It also removes OptiScaler's logs and anything inside the
    folders the install created, including those listed in a DLSS 5 switch's `manifest.original.json`, and
    `D3D12_Optiscaler` / `Licenses` / `OptiScaler` when they end up empty.
  - **Install** offers "Restore my previous OptiScaler settings" (on by default): the newest of the saved copy and a
    leftover ini. It restores values that differ from the fresh default, skipping the old version's plain defaults
    when that version is still downloaded, and records each restored value as an IniChange.
  - **A leftover OptiScaler.ini** is no longer a blocker (`InstallPreview.Leftovers`). "Remove it and continue"
    backs it up to the saved settings and removes it; a failed install puts it back. The elevated helper only
    accepts saved-settings paths under the data folder.
  - **Repair:** a file the install *added* that something else replaced becomes a Replaced entry with that copy as
    the backup, so uninstall puts it back (before, the repair safety copy was deleted with `.upshift`).
  - **Updates page:** rows keep handles to their controls (`RowState`). Downloads write progress into the row's
    status line, buttons are off meanwhile, `RefreshRowInPlace` updates texts at the end, and automatic refreshes
    wait (`_rebuildPending`) until nothing runs. The "Update selected" game and DLSS sections do the same.
  - **Library:** `RefreshCard(gameId)` swaps one card in place after an install, uninstall, settings change,
    update or exe change (`GameUpdates.LibraryChanged` now passes the game id). Filters that change in the
    background use `ReapplyFilterInPlace`. Hiding removes just that card, and a full rescan scrolls the selected
    game back into view.
  - **FSR 4:** one "Use FSR 4 on this card" switch. RDNA 2 uses the user's 4.0.2c file when added, otherwise the
    built-in one with a note; other cards use the built-in one. "Using:" shows only on AMD cards with the file
    available. The options panel follows the pretend card (`EffectiveGpu`) but never writes while one is set. The
    pretend list now has an RX 6600 and an RTX 4070.
  - **Tested:**
    - 18 checks in the scratch harness on a throwaway folder.
    - Silent Hill 2 uninstall (50 files → the game's own 8, all unchanged; the ini saved byte for byte) and
      reinstall with restore: all 12 of the user's values back, plus the two DLSS 5 build's log defaults, which are
      skipped on purpose.
    - Library scroll and selection kept through option changes, and the selected game in view after a rescan.
    - Downloading OptiScaler v0.9.2 on the Updates page: the same row and picker throughout, still expanded,
      buttons off.
    - The FSR 4 switch for a pretend RX 6600 (with and without a dummy file in Claude's data copy, removed after),
      a pretend RTX 4070, and on/off on the real RTX 4070 (ini back byte for byte).
- **1.0.2 (2026-10-04), not released:**
  - **Blank Start menu icon:** caused by a test install run from Claude's sandbox (`-TestPackId`), whose
    Velopack shortcut in the real `%AppData%\…\Start Menu\Programs\Upshift.lnk` pointed into the sandbox. Fixes:
    - `package.ps1` fails a release without the icon in every place, and test packs make no shortcuts.
    - `Core/Services/Shortcuts.cs` (raw-buffer IPropertyStore, see the comment) plus `App/Services/Shortcuts.cs`.
      These create or repair `Upshift.lnk` on install and update, and check it at start-up, only for the real
      `%LocalAppData%\Upshift.App` install.
    - **Never run a real installer from Claude's shell:** shortcuts land in the real Start menu, the files in the
      sandbox.
  - **FSR 4.1.1b:**
    - New user file `UserFileKind.Fsr411bInt8`. AMD-signed copies are refused; "Find it for me" searches only
      Downloads, Desktop and Documents.
    - In a game it's an **override**: `InstallManifest.Overrides` / `FileOverride`. OptiScaler's
      `amd_fidelityfx_upscaler_dx12.dll` waits in `.upshift\overrides\`.
    - Configure checks everything first and undoes every step on failure (`PutBack` leaves an unchanged file alone).
    - Verify, Repair, Update (the new release goes into the kept slot), Undo, Switch (dropped when the DLSS 5 build
      has the DLL elsewhere) and Uninstall handle it.
    - ini: `Fsr4ForceEnableInt8=true` and `Fsr4Update=true` (OptiScaler 0.9.4's ini says both default on only for
      RDNA 4). Not verified on a real RX 6000.
    - Options: one "FSR 4" switch with a source picker and a watermark toggle. Cards show `Fsr4Text`.
  - **Upscaler files for games:** Updates page rows for NVIDIA DLSS, AMD FidelityFX and Intel XeSS.
    - They download the catalog's single DLLs (`EnsureUpscalerFileAsync`), not the SDK release.
    - Rows show "In this game / Latest" and a per-file "Restore original".
    - `UpscalerFiles.IsModifiedFsr4` never offers, and `CheckJob` refuses, an unsigned
      `amd_fidelityfx_upscaler_dx12.dll` or an override.
    - Anti-cheat note names the anti-cheat (swaps stay blocked).
    - `BuiltInFsrNote` when PCGamingWiki lists FSR with no FSR file.
  - **Tested:** scratch harness (`fsr411b_test.cs.txt` and the swap test in `arttest`), both all passed, on
    throwaway folders with v0.9.2 / v0.9.4 / DLSSNR from the component store. Game folders were not changed.

- **After 1.0.2 (2026-10-04), not released:**
  - `Views/AboutPage` (sidebar "about"); About removed from Settings. The Buy Me a Coffee link is only there.
  - `AppUpdates`: `LatestVersion` (Velopack when installed, otherwise GitHub's release list), `NewerExists` for the
    sidebar `InfoBadge`. The Upshift row is built in `UpdatesPage.BuildAppRow`; "Check now" runs `AppUpdates.CheckAsync`.
  - `Helpers/Sections`: open/closed per section key in `settings.json` (`OpenSections`).
  - `Helpers/AutoScroll`: middle-click auto-scroll. A full-window transparent Popup takes clicks, Esc and the wheel;
    the cursor is polled each tick (PointerMoved never reached the overlay).
  - **Frame cap:** `[Framerate] FramerateLimit` (float, 0 = off). From OptiScaler 0.9.4's source (`Reflex_Hooks.cpp`,
    `menu_common.cpp`):
    - It works only with Reflex markers from the game; on AMD and Intel cards fakenvapi.dll (installed with the
      package) stands in. With OptiScaler FG on, it's off unless Reflex is on in-game (or fakenvapi is in use).
    - It's the final frame rate: OptiScaler halves it itself for OptiFG with FSR FG, and for DLSS FG through fakenvapi.
    - VRR calculator: `round(10000 / (1000/Hz + 0.3)) / 10`, i.e. 144 → 138, 165 → 157.2.
  - `OptiScalerInstaller.RepairDetails` mirrors Repair's rules. The "game update" guess is at least three
    non-OptiScaler files in the folder written within 2 minutes. `GameAnalyzer.RefreshQuick` re-runs `Verify`, so
    "Needs repair" is never stale.
  - `IniFile.Set` keeps the whitespace after "=".
  - Testing note: for a while the WinUI windows (old builds too) drew white and ignored input. Nothing in the logs,
    and it came back by itself, so test again before blaming a change.
  - The Witcher 3: frame cap Suggested → typed → Off. OptiScaler.ini is now `FramerateLimit =auto` instead of
    `= auto` (before the spacing fix); the manifest hash matches the file.

## Next

1. **DLSS 5 follow-ups:**
   - Test AMD-NR on a real AMD card.
   - Consider moving the AMD-NR pin from Alpha0.3.2 to 0.3.4.x: lmxxf on RX 7000, a new runtime zip layout, and
     `amd-nr-runtime`'s assetPattern would need `v0.4.3-Runtime.zip`.
2. **Code signing** for the installer and app (Azure Trusted Signing or a certificate): `vpk pack` takes
   `--signParams` / `--azureTrustedSignFile`. Unsigned files trigger SmartScreen and can be blocked by Smart App
   Control.

## Known open items

- Play still runs the store's reported exe, which for The Witcher 3 (GOG) is REDprelauncher. Launchers are now kept
  out of the Library.
- Upscaler files, not tested:
  - FSR updates: no test game has FSR files of its own; both have only OptiScaler's.
  - Ray Reconstruction (`nvngx_dlssd.dll`).
  - Upscaler updates through the UAC helper (GOG's folders are writable).
  - The 32-bit refusal and the anti-cheat block in the UI.
  - "Update selected" on the Updates page: it would change other games.
  - A remote catalog with `upscalerFiles`.
- The card's chips can be a little wider than the card when one has the up-arrow (e.g. The Witcher 3's "TAAU" is
  clipped).
- Installer test (2026-09-28), results:
  - Tested:
    - Setup installs per user without admin, with a Start menu shortcut and an uninstall entry.
    - Uninstall shows the question, removes the program folder, shortcut and registry entry, and leaves the data
      folder and the game folders hash-identical (the 25 s timeout path; no button was pressed).
    - The published build (the same files as `current\`) finds the existing data (22 games, covers, data path),
      and About shows 1.0.0.
  - Tested on 2026-09-29 (Setup wasn't blocked that time): the installed app starting, the uninstall question's
    "No", and the portable zip. After the question, Velopack shows an "Uninstall Complete" box, and the program
    folder is removed only once it's closed with OK.
  - Still not tested:
    - The uninstall question's "Yes".
    - A real self-update. This needs two releases, and the updater can't read releases while the repo is private.
      Velopack's `GithubSource` has no token, so the repo or its releases must be public.
- Size, after the Windows App SDK trim:
  - Setup: 75.1 MB (was 94.6).
  - Portable zip: 67.9 MB (was 87.3).
  - Installed: 164.1 MB (was 211.6).
  - The installed build hasn't been run since the trim; the published folder was.
- Untested:
  - Play for EA, Ubisoft, Battle.net, Heroic and Xbox.
  - Play being disabled while a game is busy, and the launch-error box.
  - "Keep updated".
  - A real online catalog (the default address 404s until the repo is public).
  - The Upscaler and Frame generation guide flyouts.
- OptiScaler.ini changes made before 2026-09-28 show "Date not recorded".
- The card's FSR chip can come from files OptiScaler added (their rows say so, but the chip doesn't).
- The user's Silent Hill 2 install had no line in the install log Claude could see: the user's log is in the real
  `%LocalAppData%\Upshift\logs`, which Claude's virtualized view doesn't show (see Environment quirks).
- The Witcher 3 shows "Needs repair" (seen 2026-10-03):
  - `libxell.dll` and `libxess_fg.dll` are older Intel builds (1.2.1.13 and 1.2.2.118) than OptiScaler's (1.3.0.5,
    1.3.1.78). They arrived on 2026-09-29 at 08:21, when every file in `bin\x64_dx12` was rewritten (all others with
    the same content).
  - Repair copies OptiScaler's back, and in 1.0.1 keeps those two as the originals for uninstall. Left for the user
    to decide.

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
  - Snapshots (`*.json`) and screenshots (`t1-library.png` … `t10-updates.png`).
  - From the step 3 session:
    - `research\` (391 MB): the XeSS 1.3.1, 2.1.1 and 3.0.2 SDK zips, FidelityFX 2.3.0 samples zip, and
      FSR 3.1.4 DLLs.
    - `gsnap.ps1` (game folder snapshots G0–G5), `link.ps1`, `menu.ps1`.
  - From the DLSS 5 session:
    - `nr\` (about 135 MB): the DLSSNR zip, both forks' ini and readme files, and the READMEs.
    - `dummy\`: the dummy `nvngx_dlssnr.dll` project and build.
    - Scripts: `preview.ps1`, `pickfile.ps1`, `dlss5set.ps1`.
    - Snapshots D0–D4 and screenshots `p0`–`p6`, `r1`, `w*`, `u-updates`.
  - App data: `components\optiscaler-dlssnr-v0.2.0-dlssnr\` (downloaded for the switch test).
  - From the 1.0 release session: `rel10\` (about 545 MB: publish, releases and the extracted portable copy),
    `rel-*.png` screenshots and `rel-*.json` snapshots.
- **Outside the scratch folder (1.0 release session):**
  - `%USERPROFILE%\Documents\Upshift-history-backup-20260929.bundle` (1.9 MB): the full history before the rewrite.
    Keep it until you're sure it isn't needed; it still contains the old paths.
  - `%TEMP%\upshift\` (`scrub.py`, `indexfilter.sh`): the rewrite scripts, disposable.
- **Installer leftovers:**
  - `%TEMP%\velopack\` (Velopack's own temp folder).
  - The vpk 1.2.158 tool, in the NuGet cache (`%UserProfile%\.nuget\packages\vpk`).
  - `%LocalAppData%\Upshift.App` was removed by the uninstall test and no longer exists.
- **App data created by tests** in `%LocalAppData%\Upshift`:
  - `components\optiscaler-v0.9.3\` (downloaded for the update test) and `launch-options.json` (now empty).
  - `components\upscaler-files\nvidia-dlss-v310.9.1\` (56 MB), used by both games' DLSS updates.
  - `components\upscaler-files\intel-xess-v1.3.1\` (64 MB), from the XeSS test.
  - The rest is the app's normal data.
- **Claude's memory for this project:**
  `%USERPROFILE%\.claude\projects\C--Users-%USERNAME%-Downloads-PcvgsUpscaler-phase1\memory\`. Two notes: decide small
  choices; check the game folder before tests. It's keyed to the old folder path, so a session in the new folder
  won't see it. Both rules are repeated above.
