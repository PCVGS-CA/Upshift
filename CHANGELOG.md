# Changelog

All notable changes to Upshift. Each release's section becomes its GitHub release notes, so the heading must be `## [x.y.z]` with the same version as `Directory.Build.props` (a date after it is fine).

## [1.0.1]

Bug fixes.

- **Uninstall cleans up completely:** OptiScaler.ini, the logs OptiScaler writes while a game runs, and folders it made are removed too, even after the in-game menu changed them. Your OptiScaler.ini is saved first (in Upshift's data folder, under saved-settings).
- **Restore my previous OptiScaler settings:** installing again offers your saved settings back, on by default.
- **A leftover OptiScaler.ini no longer blocks an install:** "Remove it and continue" backs it up and removes it, and the install can restore its settings.
- **Repair keeps the game's own files:** when a file OptiScaler added was replaced by the game (a game update or file check), Repair now keeps that copy as the original, so uninstalling puts it back instead of losing it.
- **Updates page:** downloads and updates show their progress in place; expanded sections and dropdowns stay open, and the buttons are off until it's done.
- **Library:** installing, uninstalling, updating or changing a game's options refreshes only that game's card, keeping your place in the list and your selection. After a full rescan, the selected game is scrolled back into view.
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
