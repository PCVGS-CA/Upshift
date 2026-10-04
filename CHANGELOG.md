# Changelog

All notable changes to Upshift. Each release's section becomes its GitHub release notes, so the heading must be `## [x.y.z]` with the same version as `Directory.Build.props` (a date after it is fine).

## [Unreleased]

- **About** has its own place in the sidebar, under Settings, with the logo, version, licence and disclaimer. It has an optional Buy Me a Coffee button; Upshift never asks for donations anywhere else.
- **Upshift's own update** is the first row on the Updates page ("On this PC", "Latest", Update or Up to date), and "Check now" checks it too. A dot on Updates in the sidebar shows when a newer Upshift is available.
- **Game upscaler files:** the NVIDIA DLSS, AMD FidelityFX and Intel XeSS section on the Updates page has its final name.
- **Frame cap:** a per-game number box in OptiScaler options (empty means no cap; whole numbers; saved on Enter or when you click away), using OptiScaler's own Reflex-based limiter ([Framerate] FramerateLimit). The hint under it uses the refresh rate set in Windows for your main monitor, or the monitor you pick when there are several. It's listed in the game's change history.
- **Show FPS counter:** a per-game switch for OptiScaler's frame-rate counter ([Menu] ShowFps), to see what a game runs at before choosing a cap.
- **Game panel:** "Changes Upshift made to this game" is collapsible (closed by default) and its header gives the number of changes and the latest date. Suggestion notes show three lines, then "Show all". Every collapsible section stays open or closed as you left it, across restarts.
- **Middle-click auto-scroll** in the Library, the game panel and Settings.
- **Needs repair** explains each file: what it is, the version Upshift installed, the version there now and what Repair will do, and says when it looks like a game update put the game's own files back. It's worked out from the files as they are now, so it goes away after a repair. While another task runs, the Repair button says when it will be available.
- **Measure performance** (each game's panel, closed by default): press Measure this game, start the game, press F10 (changeable in Settings) and play for 60 seconds; a beep marks the start and two the end. Uses Intel's PresentMon (MIT licence), downloaded from its GitHub page the first time and listed on the Updates page. Windows asks for permission for a small measuring helper only; Upshift stays a normal app. Shows the average and low points (1% low), the two latest runs side by side, up to 10 older runs, and "What to try" with a frame cap you can apply. Runs with frame generation are marked. Nothing measured leaves the PC.
- **OptiScaler.ini** keeps the spacing around "=" when Upshift changes a value, so a setting put back restores the line exactly.

## [1.0.2]

- **App icon in the Start menu and on the desktop:** the installer creates both shortcuts with Upshift's logo, and installing over 1.0.0 or 1.0.1 replaces old shortcuts that showed a blank page. Upshift also checks its shortcuts when it starts and fixes them if they point somewhere else. Release builds now fail if the icon is missing anywhere.
- **FSR 4.1.1b for RX 6000:** a second optional file in Settings, "FSR 4.1.1b INT8 for RX 6000 (amd_fidelityfx_upscaler_dx12.dll)", next to 4.0.2c. It's a modified file that isn't signed by AMD; Upshift shows its version and SHA-256 and never downloads it. "Find it for me" only looks in Downloads, Desktop and Documents, and skips AMD's ordinary copies and OptiScaler packages. In a game it takes the place of the amd_fidelityfx_upscaler_dx12.dll OptiScaler puts there; that copy is kept and comes back when you turn it off or uninstall.
- **One FSR 4 setting:** a game's OptiScaler options have one "FSR 4" switch with a source: OptiScaler's built-in FSR 4, your 4.1.1b file or your 4.0.2c file. A game uses one at a time, and switching takes the previous file out. Each game shows which FSR 4 it uses and the file's version. New "Show FSR 4 watermark" switch.
- **Upscaler files for games:** the NVIDIA DLSS, AMD FidelityFX and Intel XeSS rows on the Updates page (previously "Used in a later version") now download the files games actually use and say how many of your games can use them. Each file in a game shows "In this game" and "Latest", with Update, Update all and Restore original per file. A modified FSR 4 file is never replaced; games with anti-cheat say which one and stay untouched; a game with FSR built in (no separate file) says it can't be updated this way.

## [1.0.1]

Bug fixes.

- **Uninstall cleans up completely:** OptiScaler.ini, the logs OptiScaler writes while a game runs, and folders it made are removed too, even after the in-game menu changed them. Your OptiScaler.ini is saved first (in Upshift's data folder, under saved-settings).
- **Restore my previous OptiScaler settings:** installing again offers your saved settings back, on by default.
- **A leftover OptiScaler.ini no longer blocks an install:** "Remove it and continue" backs it up and removes it, and the install can restore its settings.
- **Repair keeps the game's own files:** when a file OptiScaler added was replaced by the game (a game update or file check), Repair now keeps that copy as the original, so uninstalling puts it back instead of losing it.
- **Updates page:** downloads and updates show their progress in place; expanded sections and dropdowns stay open, and the buttons are off until it's done.
- **Library:** installing, uninstalling, updating or changing a game's options refreshes only that game's card, keeping your place in the list and your selection. After a full rescan, the selected game is scrolled back into view.
- **Crash log:** if Upshift ever closes unexpectedly, the error and where it happened are written to `logs\crash.log` in Upshift's data folder, starting from the moment Upshift starts.
- **FSR 4:** one "Use FSR 4 on this card" switch replaces the two. RX 6000 cards use your 4.0.2c file when you've added it in Settings, otherwise OptiScaler's built-in FSR 4; other cards use the built-in one. A "Using:" choice appears only when both are possible. Games that already had either setting on keep it.

## [1.0.0]

The first public release. Upshift is free, unofficial and open source (MIT License).

- **Finds your games** from Steam, Epic, GOG, Xbox / Game Pass, EA, Ubisoft, Battle.net, Heroic and itch.io, plus folders you add and an optional drive scan.
- **Shows what's inside each game:** its DLSS, FSR and XeSS files and versions (with names such as "DLSS 4.5"), DirectX or Vulkan, the engine and anti-cheat, together with PCGamingWiki info and suggestions from the OptiScaler wiki.
- **Installs OptiScaler in one click,** with a backup of every file it touches and an exact uninstall. Pick the upscaler, frame generation and DLSS model, with a "Which one should I pick?" guide for each.
- **Updates the DLSS, FSR and XeSS files** a game ships with to newer official ones, in one game or all of them at once. Every new file must be signed by NVIDIA, AMD or Intel, and "Restore original files" brings the originals back.
- **Keeps things up to date:** new OptiScaler versions (Stable or Beta) keep your settings, with "Undo last update" and repair for changed or missing files.
- **DLSS 5 (experimental):** switch a game to the DLSS 5 build of OptiScaler and back, with checks for your card and driver.
- **Play** games through their own store, with launch options, and see a dated list of every change Upshift made to each game.
- **Safe by design:** games with anti-cheat are never changed, games never start as admin, and admin rights are asked for only when a game folder needs them.
- **Installer and portable zip,** no admin needed. Upshift updates itself when you choose "Restart to update", and uninstalling keeps your data unless you ask.
