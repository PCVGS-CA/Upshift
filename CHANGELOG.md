# Changelog

All notable changes to Upshift. Each release's section becomes its GitHub release notes, so the heading must be `## [x.y.z]` with the same version as `Directory.Build.props` (a date after it is fine).

## [1.2.0]

Trust and troubleshooting.

### New

- **"Is it working?" for each game:** after you've played, the game's panel says what OptiScaler's own log shows, for example "According to OptiScaler's log: Working. The game's DLSS is being replaced with FSR 4." It also tells you when OptiScaler loaded but the game isn't using an upscaler it can take over (and what to pick in the game's menu), when OptiScaler didn't load (with the likely causes and what to try), or to run the game once first, with the date and time of the run it's based on. New installs turn OptiScaler's log on at its lightest level, which doesn't slow games down; for games installed earlier, press "Turn on the check". Shown for standard OptiScaler and its DLSS 5 build; the AMD-NR build's log hasn't been checked yet.
- **Report a problem:** "Report a problem" on the About page and "Report a problem with this game" in each game's panel save one zip file to your Desktop for a GitHub issue: Upshift's and Windows' versions, your graphics cards, today's log and crash log, and for a game its OptiScaler settings and version, Upshift's record, the change history, OptiScaler's log and a list of the game's files (names, sizes and versions only). Your Windows user name is taken out of every path, your SteamGridDB key and other games are left out, and you see what's inside before it's saved. Nothing is sent anywhere. The GitHub issue forms now ask for this file.

### Uninstall that explains itself

- Before removing anything, Upshift checks for problems and says how to fix each: the game or its launcher is still running ("Close the game and try again"), the folder needs Windows' permission ("Try again with permission" if the prompt was declined), or files changed since the install (listed, with "Leave them" or "Remove anyway and restore the originals").
- An uninstall is never left half done without saying so: if a file can't be removed (because the game has it open, say), everything else is still finished, Upshift's record keeps exactly what's left, and the full paths are listed, so trying again picks up where it stopped. A missing backup is reported instead of being skipped.
- Afterwards Upshift checks the folder file by file: "This game's folder is back to how it was before Upshift", or a list of what remains with an "Open folder" button.
- If Upshift's record for a game is missing or damaged, "Remove what Upshift added" lists the OptiScaler files it finds (the ones that may be the game's own are unticked) and its backed-up originals, and removes only what you tick.
- Uninstalls and their results are kept in the game's change history and in Upshift's log.

## [1.1.0]

Everything new since 1.0.0 (versions 1.0.1 and 1.0.2 weren't released on their own).

### New

- **Measure performance:** each game's panel has a new section that measures how a game runs. Press "Measure this game", start the game, press F10 (you can pick another key in Settings) and play for 60 seconds; a beep marks the start and two beeps the end. You get the average frame rate and the "low points" (1% low), the two latest runs side by side with the difference, up to 10 older runs, and "What to try": a frame cap you can apply with one click, plus tips on quality and frame generation. Runs with frame generation on are marked. Windows asks for permission only for the short measuring step; Upshift itself stays a normal app. Nothing measured leaves your PC.
- **Frame cap:** a per-game frame cap in the OptiScaler options, using OptiScaler's own limiter. Type a number (empty means no cap); the hint under it uses your monitor's refresh rate, and you can pick the monitor when you have more than one. A "Show FPS counter" switch turns on OptiScaler's frame-rate counter so you can see what a game runs at first.
- **FSR 4.1.1b for RX 6000:** besides the 4.0.2c file, you can add the newer modified FSR 4.1.1b file in Settings. A game's OptiScaler options now have one "FSR 4" switch with a choice of source: OptiScaler's built-in FSR 4, your 4.1.1b file or your 4.0.2c file. Each game shows which one it uses, and there's a "Show FSR 4 watermark" switch.
- **Game upscaler files:** the NVIDIA DLSS, AMD FidelityFX and Intel XeSS rows on the Updates page now download the files games actually use. Each file in a game shows "In this game" and "Latest", with Update, Update all and Restore original per file.
- **About** has its own place in the sidebar, with an optional Buy Me a Coffee button. Upshift is free forever and never asks for donations anywhere else.
- **Upshift's own update** is the first row on the Updates page, "Check now" checks it too, and a dot on Updates in the sidebar shows when a newer Upshift is out.
- **Tidier game panel:** the list of changes Upshift made is collapsible and shows how many there are and the latest date; long suggestion notes show three lines with "Show all"; every collapsible section stays open or closed as you left it.
- **Middle-click auto-scroll** in the Library, the game panel and Settings.
- **Restore my previous OptiScaler settings:** your OptiScaler.ini is saved when you uninstall, and installing again offers your settings back.

### Fixes

- **App icon:** the Start menu and desktop shortcuts show Upshift's logo again. Installing over an older version replaces shortcuts that showed a blank page, and Upshift fixes its shortcuts at start-up if they point somewhere else.
- **Needs repair** now explains each file (what it is, the version Upshift installed, the version there now, what Repair will do), says when a game update probably put the game's own files back, and disappears as soon as the files are fine again. Repair keeps a game's own newer file as the original, so uninstalling puts it back.
- **Uninstall cleans up completely,** including OptiScaler's logs and the folders it made, and a leftover OptiScaler.ini no longer blocks an install.
- **The Library keeps your place:** changing a game refreshes only its card, and the selected game stays in view.
- **Updates page:** downloads show their progress in place, without the page jumping or closing what you had open.
- **OptiScaler.ini** keeps its spacing when Upshift changes a value, so turning a setting off restores the line exactly.
- **Crash log:** if Upshift ever closes unexpectedly, the error is written to logs\crash.log in Upshift's data folder.

### Files Upshift never ships

Upshift never downloads or bundles the DLSS 5 file (nvngx_dlssnr.dll) or the modified FSR 4 files (amdxcffx64.dll 4.0.2c, amd_fidelityfx_upscaler_dx12.dll 4.1.1b); you add those yourself. PresentMon, Intel's frame-timing tool used by Measure performance, is downloaded from its official GitHub page only if you use Measure.

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
