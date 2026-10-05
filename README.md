# Upshift

Upshift finds the games on your PC, shows which upscalers each one ships with (DLSS, FSR, XeSS, TSR…), suggests
what suits your graphics card, and installs the official [OptiScaler](https://github.com/optiscaler/OptiScaler)
release into a game: with a backup of every file it touches, a record of every change, and an exact uninstall.

> **Upshift is a free, unofficial tool. It isn't made by or affiliated with the OptiScaler team, NVIDIA, AMD or
> Intel.** Everything it installs is downloaded from each project's own GitHub releases; nothing is re-hosted.

## Download

Get Upshift from the [Releases page](https://github.com/PCVGS-CA/Upshift/releases). The
[latest release](https://github.com/PCVGS-CA/Upshift/releases/latest) has two downloads:

- **`Upshift-Setup-x64.exe`**: installs Upshift for your Windows account (no admin rights needed) into
  `%LocalAppData%\Upshift.App`, with a Start menu shortcut. Uninstall it from Windows Settings > Apps; it asks
  whether to delete your Upshift data as well, and keeps it unless you say yes.
- **`Upshift-Portable-x64.zip`**: no installer. Unzip it to a folder of its own and run `Upshift.exe`.

Both update themselves: Upshift checks for a new version at start-up (when "Check for updates automatically" is on)
and with "Check now" on the Updates page, and installs it when you press **Update** on Upshift's row there (or
**Restart to update**).

## First launch

Upshift isn't code-signed yet, so the first time you run it Windows SmartScreen may say **"Windows protected your
PC"**. Click **More info**, then **Run anyway**. On PCs with Smart App Control turned on, Windows may block it
outright.

## Screenshots

**Library:** every game on the PC, with the upscalers each one ships with.

![Library](docs/screenshots/library.png)

**A game's details:** what's inside the game, suggestions, OptiScaler and its options.

![Game details](docs/screenshots/details.png)

**Updates:** new versions of OptiScaler and the upscaler files, for all your games at once.

![Updates](docs/screenshots/updates.png)

## Video

[![Watch the video](https://img.youtube.com/vi/F7Zr62BZHzc/hqdefault.jpg)](https://www.youtube.com/watch?v=F7Zr62BZHzc)

## What it does

- **Finds your games** from Steam, Epic, GOG, the Xbox app / Game Pass, EA app, Ubisoft Connect, Battle.net,
  Heroic and itch.io, installed programs that look like games, folders you add yourself, and (optionally) a scan of
  every drive.
- **Looks inside each game:** DLSS, FSR, XeSS and Streamline files and their versions (DLSS versions with their
  names, e.g. "310.9.1 (DLSS 4.5)"), the main .exe, 32/64-bit, DirectX 11/12 or Vulkan, the engine, anti-cheat, and
  mods that are already there. Games with anti-cheat are never modified.
- **"Upscalers in this game":** what the files show, merged with PCGamingWiki, plus "Suggestions for this game"
  from the OptiScaler wiki.
- **Installs OptiScaler** the way its own setup does, under the loading name you choose (dxgi.dll by default).
  Every game file it overwrites is backed up first, and everything is recorded in `.upshift\manifest.json` in the
  game folder.
- **"Is it working?":** after you've played, the game's panel reads OptiScaler's own log (Upshift turns it on at its
  lightest level) and says, for example, "According to OptiScaler's log: Working. The game's DLSS is being replaced
  with FSR 4.", or that OptiScaler loaded but the game isn't using an upscaler it can take over, or that it didn't
  load, with what to try.
- **Uninstall that explains itself:** before removing anything, Upshift checks whether the game or its launcher is
  still running, whether Windows will ask for permission, and which files changed since the install ("Leave them"
  or "Remove anyway and restore the originals"). Afterwards it checks the folder file by file and either says it's
  back to how it was before Upshift, or lists exactly what's left with "Open folder". If Upshift's record of a game
  is missing or damaged, "Remove what Upshift added" lists OptiScaler's files and removes only the ones you tick.
- **Upscaler files:** updates the DLSS, FSR and XeSS DLLs a game ships with to newer ones from NVIDIA's, AMD's and
  Intel's own GitHub pages (for example DLSS 3.x to 310.9.1, DLSS 4.5, which unlocks the DLSS 4 and 4.5 models).
  Only swaps known to work are offered, only files the game already has are replaced, every new file must carry its
  vendor's digital signature, and "Restore original files" puts the exact originals back.
- **OptiScaler options** per game: upscaler, frame generation, DLSS model, FSR 4 (OptiScaler's built-in, or a
  4.1.1b or 4.0.2c file you add yourself), a frame cap and an FPS counter, each with a "Which one should I pick?" guide.
- **Measure performance:** press a key in game (F10 by default) to record 60 seconds with Intel's PresentMon, then
  see the average and low points, compare runs, and get "What to try" (such as a frame cap you can apply). PresentMon
  is downloaded from its GitHub page the first time you measure, and Windows asks for permission only for the
  measuring step. Nothing measured leaves your PC.
- **Updates:** checks each component's GitHub releases (cached, with ETags), lets you pick Stable or Beta, updates
  games while keeping your OptiScaler settings, and can undo the last update exactly. Changed or missing files are
  flagged at scan time and can be repaired.
- **Play:** starts a game through its store (Steam, Epic, GOG, …), with optional launch options. Games are never
  started with admin rights.
- **"Changes Upshift made to this game":** a dated list of everything it changed in each game folder, including
  uninstalls and their results.
- **About** (in the sidebar): the version, licence and an optional Buy Me a Coffee button, plus **Report a
  problem**.
- **Cover art** from Steam's local cache, GOG, Epic and the Steam store, and optionally SteamGridDB (with your own
  API key).

When a game folder needs admin rights (games under Program Files), Upshift starts a second copy of itself with a UAC
prompt for just that one change. The main window never runs as administrator.

## Requirements

**To run:** Windows 10 version 1809 or later, or Windows 11; 64-bit (x64). The build is self-contained, so no
separate .NET or Windows App SDK install is needed. An internet connection is used for downloads, cover art and wiki
lookups; everything else works offline.

**To build:**

1. Install **Visual Studio 2022 or newer** (Community is free) with the **"WinUI application development"**
   workload, which includes the .NET 8 SDK.
2. Open **Upshift.sln**, set the platform to **x64** and the startup project to **Upshift.App**.
3. Press **F5**. The first build downloads the NuGet packages.

Or from a Developer Command Prompt: `msbuild Upshift.sln /p:Configuration=Debug /p:Platform=x64`

**To make the installer and portable zip locally:** `powershell -ExecutionPolicy Bypass -File build\package.ps1`
(output in `releases\`). It uses Velopack's `vpk`, pinned in `dotnet-tools.json`.

## Releasing

1. Set the new version in `Directory.Build.props` (the only place it's set).
2. Add a `## [x.y.z]` section to `CHANGELOG.md`; it becomes the release notes.
3. Commit, then push a tag with the same version: `git tag v1.2.3` and `git push origin v1.2.3`.

The [Release workflow](.github/workflows/release.yml) builds Release x64, makes the installer, the portable zip and
the self-update packages, and publishes them as a GitHub release, with the SHA-256 of the installer and the portable
zip added to the notes. It stops if the tag and the version differ.

## Where it keeps things

Everything Upshift stores lives in `%LocalAppData%\Upshift`: the library cache, settings (including an optional
SteamGridDB key), cover art, downloaded components (PresentMon too, once you've measured), files you supply, wiki
caches, your saved OptiScaler settings (`saved-settings\`), measurements (`measurements\`), each game's uninstall
history (`history\`) and `logs\`. None of it is part of this repository, and none of it is sent anywhere. The
installed program is in a separate folder, `%LocalAppData%\Upshift.App`, so uninstalling never touches your data
unless you ask it to. In game folders, Upshift only writes the files it installs and its own `.upshift` folder.
A problem report is only written when you press "Report a problem", as a zip on your Desktop.

## Project layout

- `src/Upshift.Core`: all the logic, no UI. Discovery (launchers), Detection (what's inside a game), Hardware (GPU),
  Catalog (`catalog.json`: download sources, DLSS presets and names, guide text), Components (GitHub releases and
  downloads), Install (install, update, undo, repair, options), Launch, Wiki, Artwork and Services.
- `src/Upshift.App`: the WinUI 3 app (Library, Updates and Settings pages).

`catalog.json` holds everything that changes more often than the app: pinned versions, asset patterns, DLSS names
and the "Which one should I pick?" text. At start-up Upshift downloads the copy on this repository's `main` branch
and uses it from the next start when it's valid and newer than the one built into the app, so fixes reach everyone
without an app update. When it can't be reached, the built-in copy is used. Settings > "Catalog address" can point
to another catalog instead.

## Credits

Upshift downloads, installs or reads information from these projects. All credit for them goes to their authors.

- **[OptiScaler](https://github.com/optiscaler/OptiScaler)**: the upscaler and frame-generation mod Upshift installs.
- **[OptiScaler DLSSNR](https://github.com/Dagherbou/OptiScaler_DLSSNR)**: an OptiScaler fork that adds DLSS 5
  Neural Rendering.
- **[AMD-NR](https://github.com/3zwr1/AMD-NR---OptiScaler)**: an OptiScaler fork for neural rendering on AMD cards,
  with the AMD-NR runtime by danielblnc.
- **[dlssg-to-fsr3](https://github.com/Nukem9/dlssg-to-fsr3)** by Nukem9: runs FSR frame generation through a
  game's DLSS Frame Generation option (bundled with OptiScaler).
- **[fakenvapi](https://github.com/optiscaler/fakenvapi)**: Reflex support through Anti-Lag 2, LatencyFlex or XeLL
  on non-NVIDIA cards (bundled with OptiScaler).
- **[PresentMon](https://github.com/GameTechDev/PresentMon)** by Intel (MIT License): the frame-timing tool behind
  "Measure performance", downloaded from its GitHub releases only when you measure.
- **[PCGamingWiki](https://www.pcgamingwiki.com/)**: engine and upscaler information for each game. PCGamingWiki
  content is available under
  [CC BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/); Upshift links back to each game's page.
- **[The OptiScaler wiki](https://github.com/optiscaler/OptiScaler/wiki)**: the compatibility list and per-game
  notes behind "Suggestions for this game".

Built with the [Windows App SDK](https://github.com/microsoft/WindowsAppSDK),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
[SharpCompress](https://github.com/adamhathcock/sharpcompress) and [Velopack](https://github.com/velopack/velopack)
(installer and updates). NVIDIA DLSS, AMD FidelityFX and Intel XeSS are
trademarks of their owners.

## Reporting a problem

Use [Issues](https://github.com/PCVGS-CA/Upshift/issues/new/choose): "Bug report" for something that went wrong, or
"Game not detected / wrong info" for a game Upshift misses or describes wrongly.

Please attach Upshift's problem report: press **Report a problem with this game** in the game's panel (or **About >
Report a problem**). It saves one zip file to your Desktop with Upshift's and Windows' versions, your graphics cards,
today's log and, for a game, its OptiScaler details, change history, OptiScaler's log and a list of its files (names,
sizes and versions only). Your Windows user name is taken out of every path, your SteamGridDB key and other games are
left out, and you see what's inside before it's saved. Nothing is sent anywhere: you attach the file yourself.

## Support

This program is free forever. The coffee button is purely optional and unlocks nothing. It just keeps a hobo
developer caffeinated and the AI bills paid.

[Buy Me a Coffee](https://buymeacoffee.com/pcvgs)

<sub>Donations go to PCVGS, not to the OptiScaler team, NVIDIA, AMD or Intel. OptiScaler does the heavy lifting: if
you want to support them too, visit the [OptiScaler GitHub page](https://github.com/optiscaler/OptiScaler).</sub>

## License

Upshift is free and open source under the [MIT License](LICENSE). Copyright (c) 2026 PCVGS.

---

<sub>Upshift is provided as-is, without warranty. Changing game files is at your own risk. Never use it with online
or anti-cheat games. Made by PCVGS, [pcvgs.com](https://pcvgs.com).</sub>
