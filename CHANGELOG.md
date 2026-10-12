# Changelog

All notable changes to Upshift. Each release's section becomes its GitHub release notes, so the heading must be `## [x.y.z]` with the same version as `Directory.Build.props` (a date after it is fine).

## [1.3.0]

HelixSR support (experimental, still in testing), a warning for Capcom RE Engine games, and a more flexible Measure performance.

### New

- **A page for each game.** Picking a game opens its own page in place of the Library, with its art across the top (Steam games use their wide art; others a blurred cover), its name, store, graphics API and main actions, and five tabs: Overview, Upscaling, Files, Performance and History. Cards sit side by side and stack in a narrow window. Back (or Esc, or the mouse's back button) returns to the Library at the same place with the same game selected.
  - **Overview:** warnings first (anti-cheat, needs repair, a damaged record, Battle.net, REFramework), then what's installed and in use, whether it's working, what suits your graphics card, which upscaler to pick in the game's own menu, and the last measurement.
  - **Capcom RE Engine games** get the REFramework notice at the top of the Overview: where to get it, which file to copy where, and Check again. If OptiScaler is already there without REFramework, the notice offers to remove it. Once REFramework is found, a short line says so.
- **Shader pre-build (Experimental), off by default (Settings).** On a game's Performance tab: learn the shaders a Direct3D 12 game builds while you play (through OptiScaler installed by Upshift; never for games with anti-cheat), and "Pre-build now" builds them all again after the driver's shader cache was wiped. It only restores shaders already learned; ray tracing isn't covered. Test tools: clear and restore the driver's shader cache (shared by all games; nothing is deleted) and a before-and-after report to copy. What it learns never leaves the PC.
- **Battle.net games** get a warning on their Overview and before OptiScaler is installed: most are played online, where changed game files can get players banned, so check that the game allows mods first.
- **Sharing results.** Each measurement can carry a note (resolution and in-game settings, which Upshift can't read), and can be copied as text or saved as an image with the game, the frame rates, the verdict, and the PC's processor, graphics card, memory and Windows version. "Export all results" saves every game's results as a CSV file to combine with other PCs. Shared results never include your Windows user name, the PC's name or a folder path.
- **HelixSR (experimental, still in testing), for AMD RDNA 1 and RDNA 2 cards (RX 5000 and RX 6000 series, and Radeon 680M-class graphics):** HelixSR is an unofficial project, not affiliated with NVIDIA or AMD, that runs NVIDIA's DLSS 3.7 network on Radeon cards without FSR 4. It's off by default. On those cards, the Updates page has a HelixSR row with "Set up". After you accept a notice (what HelixSR is, everything its setup downloads and from where, that its source code isn't published and nothing it ships is signed, and how long it takes), Upshift runs the HelixSR author's own setup once. It downloads only the HelixSR release Upshift has checked, verifies it and its scripts against pinned SHA-256 values, and stops with the real error if anything goes wrong, leaving nothing half-installed.
- **Setup progress you can leave:** the setup keeps running if you switch to another page, and the HelixSR row picks it up again when you come back. The time keeps counting through the last step (compiling shaders), which takes several minutes and prints nothing while it works. If the setup ends for any reason, including being closed from outside, Upshift notices and shows the result.
- **"DLSS 3.7 via HelixSR (experimental, still in testing)"** is then an upscaler choice in Direct3D 12 games with OptiScaler installed by Upshift. Choosing it points OptiScaler at HelixSR's files in Upshift's data folder; nothing is added to the game folder, and choosing another upscaler or uninstalling puts OptiScaler.ini back exactly. HelixSR and FSR 4 both use OptiScaler's FSR slot, so turning one on turns the other off, and Upshift says so.
- **Safety net:** if Upshift didn't close properly last time and a game is set to HelixSR, Upshift switches that game back to its previous upscaler at start-up and offers "Turn HelixSR back on".
- **Which option to pick in the game's own menu:** once OptiScaler is installed, its options say which upscaler to choose in the game's graphics menu: the OptiScaler wiki's advice for that game when it has some, otherwise DLSS when the game offers it, otherwise FSR 3.1, then XeSS. The same line shows when HelixSR is selected.
- **Capcom RE Engine games:** OptiScaler's wiki says these games need REFramework before OptiScaler works. Upshift recognises them by the engine's own files, not by name, and asks before installing OptiScaler into one without REFramework in its folder:
  - Games the wiki confirms (Resident Evil 2, 3, 4, 7, Village and Requiem, Devil May Cry 5, Monster Hunter Wilds, Monster Hunter Stories 3, Dragon's Dogma 2, Onimusha: Way of the Sword, PRAGMATA): "This game crashes with OptiScaler unless REFramework is installed first."
  - Any other RE Engine game: "Capcom RE Engine games usually need REFramework before OptiScaler will work. Check that REFramework supports this game."
  - Games the wiki says don't need it, such as Monster Hunter Rise, get no warning.
  - Both lists are in Upshift's online catalog, so they can change without an app update. Upshift never downloads or installs REFramework, and games with anti-cheat stay blocked as before.
- **Measure performance:**
  - Choose how long to measure, from 10 seconds to 30 minutes (60 seconds by default); Upshift remembers your choice.
  - Press the hotkey again during a measurement to stop early and keep the result.
  - Each result shows how long it really ran.
  - New: **Max FPS**, the best one-second average (not the single fastest frame), in results, history and the comparison. Older results show a dash.
  - New: **what held the frame rate back.** Each result says "Limited by your graphics card", "Limited by your processor", "Balanced" or "Limited by your frame cap" (when Upshift's frame cap or VSync sets the pace), with one line of advice: a lower upscaler quality when it's the card, frame generation when it's the processor. With frame generation on, the verdict is marked less certain.
  - The details behind it: how long the graphics card and the processor were busy and waiting per frame (from PresentMon's recording), GPU usage, processor usage and its busiest core, and peak video memory against what the card has (from Windows' own counters, no extra download). They're saved with each result and shown in the comparison and history; older results, or a counter a PC doesn't have, show a dash.
  - While waiting, Upshift says "Waiting for you to press F10 in the game" instead of just spinning.
- **"Which upscaler should I pick?"** on RX 6000 cards compares HelixSR with FSR 4.1.1b and suggests trying both with Measure performance.
- **"Is it working?"** also reads HelixSR's own log and says whether its DLSS network actually ran. "Report a problem" includes that log, never HelixSR's network files.
- **Safe if something's missing:** if a game is set to HelixSR and HelixSR's files are gone, Upshift switches the game back to the upscaler it had before and tells you. "Remove HelixSR" switches every game using it back first, then deletes its files, and offers to delete the setup's tools too. If HelixSR's files are incomplete or can't be read (a setup that didn't finish, a damaged record), it shows as "Not set up" with the reason and a "Remove what's there" link; HelixSR's state can never stop Upshift from starting.

**Where HelixSR stands:**

- HelixSR's setup has completed once, on a Radeon RX 6600.
- HelixSR has never been run in a game.
- A crash of Upshift after that setup was hardened against, but it isn't confirmed fixed.

If you have an RX 5000 or RX 6000 card and try it, please tell us how it went, good or bad, with **Report a problem** (About page, or in the game's panel). On other cards the HelixSR row explains why it isn't needed.

### Fixes

- The PresentMon row on the Updates page showed a greyed-out "No download" when PresentMon wasn't on the PC; it's a working Download button again.
- Restoring your previous OptiScaler settings after a reinstall no longer brings back settings that pointed at HelixSR's files.

## [1.2.0]

Trust and troubleshooting: Upshift can now tell you whether OptiScaler is working, explains every uninstall, and makes reporting a problem easy.

### New

- **"Is it working?" for each game:** after you've played, the game's panel says what OptiScaler's own log shows, for example "According to OptiScaler's log: Working. The game's DLSS is being replaced with FSR 4." It also tells you when OptiScaler loaded but the game isn't using an upscaler it can take over (and what to pick in the game's menu), when OptiScaler didn't load (with the likely causes and what to try), or to run the game once first, with the date and time of the run it's based on. New installs turn OptiScaler's log on at its lightest level, which doesn't slow games down; for games installed earlier, press "Turn on the check". Shown for standard OptiScaler and its DLSS 5 build; the AMD-NR build's log hasn't been checked yet.
- **Uninstall that explains itself:** before removing anything, Upshift checks for problems and says how to fix each: the game or its launcher is still running ("Close the game and try again"), the folder needs Windows' permission ("Try again with permission" if the prompt was declined), or files changed since the install (listed, with "Leave them" or "Remove anyway and restore the originals"). Afterwards it checks the folder file by file and says "This game's folder is back to how it was before Upshift", or lists what remains with an "Open folder" button. Uninstalls and their results are kept in the game's change history.
- **Remove what Upshift added:** if Upshift's record for a game is missing or damaged, it lists the OptiScaler files it finds (the ones that may be the game's own are unticked) and its backed-up originals, and removes only what you tick.
- **Report a problem:** "Report a problem" on the About page and "Report a problem with this game" in each game's panel save one zip file to your Desktop for a GitHub issue: Upshift's and Windows' versions, your graphics cards, today's log and crash log, and for a game its OptiScaler settings and version, Upshift's record, the change history, OptiScaler's log and a list of the game's files (names, sizes and versions only). Your Windows user name is taken out of every path, your SteamGridDB key and other games are left out, and you see what's inside before it's saved. Nothing is sent anywhere. The GitHub issue forms now ask for this file.

### Fixes

- **An uninstall is never left half done without saying so:** if a file can't be removed (because the game has it open, say), everything else is still finished, the exact paths of what's left are listed, and trying again picks up where it stopped. Before, it stopped at the first such file, and trying again could mistake files it had already put back for files that had changed.
- **A missing backup is reported:** if Upshift's copy of a game's original file has gone missing, the uninstall says so (with the path) instead of skipping it.

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
