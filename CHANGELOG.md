# Changelog

All notable changes to Upshift. Each release's section becomes its GitHub release notes, so the heading must be `## [x.y.z]` with the same version as `Directory.Build.props` (a date after it is fine).

## [1.0.0]

The first public release.

### Games and detection

- Finds games from Steam, Epic, GOG, the Xbox app / Game Pass, EA app, Ubisoft Connect, Battle.net, Heroic and itch.io, plus installed programs that look like games, folders you add yourself and an optional drive scan.
- Shows the upscalers each game ships with (DLSS, FSR, XeSS, Streamline) and their versions, with DLSS names such as "310.9.1 (DLSS 4.5)", merged with PCGamingWiki in "Upscalers in this game".
- Detects the main .exe, 32/64-bit, DirectX 11/12 or Vulkan, the engine, anti-cheat and existing mods. Games with anti-cheat are never modified.
- "Suggestions for this game" from the OptiScaler wiki, and recommendations for your graphics card.
- Cover art from Steam, GOG, Epic, the Steam store and, optionally, SteamGridDB.

### OptiScaler

- Installs the official OptiScaler release into a game, with a backup of every file it replaces and a manifest of every change in the game's `.upshift` folder. Uninstall is exact.
- Per-game options: upscaler, frame generation, DLSS model and FSR 4 (INT8), with "Which one should I pick?" guides.
- Game folders that need admin rights are changed through a one-off UAC prompt; the main window never runs as admin.

### Upscaler files

- Updates the DLSS, FSR and XeSS files games ship with to newer ones from NVIDIA's, AMD's and Intel's own GitHub pages: DLSS Super Resolution and Ray Reconstruction to 310.9.1 (DLSS 4.5), FSR 3.1 to 3.1.4 (and FidelityFX SDK 2.x files to 2.3.0), and XeSS within its major version.
- Only files the game already has are replaced, 64-bit for 64-bit, and every new file must carry a valid NVIDIA, AMD or Intel signature. Each original is backed up in the game's `.upshift` folder, and "Restore original files" brings the exact originals back.
- A "DLSS update" bar shows what a newer DLSS unlocks, the DLSS model list offers "Update DLSS to unlock", and the Updates page can update DLSS in all your games at once. Games with anti-cheat are never changed.
- If a game update puts its old file back, Upshift says so and can re-apply the update. A file changed by hand is detected too: the file from before Upshift's first change stays the original, and the changed copy is kept aside rather than thrown away.
- Versions are read from the files on disk (every copy, when a game has several), so updates are only offered when a copy really is older; nothing to do shows "Already up to date", never an error.
- Files belonging to OptiScaler, including an OptiScaler installed by hand, are never updated by Upshift.
- The per-file list is under "Upscaler files (advanced)", collapsed with a one-line summary; the DLSS bar and the Updates page are the main way to update.
- Files that OptiScaler installed are labelled as OptiScaler's and left to OptiScaler.

### DLSS 5 (Neural Rendering, experimental)

- A DLSS 5 section per game: whether your card can use it and why, then "Switch to the DLSS 5 build of OptiScaler" (the OptiScaler DLSSNR fork on NVIDIA, AMD-NR on AMD). Your OptiScaler settings are kept, and "Switch back" returns the game folder to exactly what it was.
- Once switched: Neural Rendering on/off, Strength and Model resolution, the in-game hotkey, and AMD-NR's runtime, preselected for your card so the game doesn't ask on first launch.
- Checks with plain messages: NVIDIA driver too old, DirectX 11 / Vulkan games needing a "w/Dx12" upscaler, anti-cheat games blocked, and a performance note on RTX 40 and older.
- Your nvngx_dlssnr.dll (Settings > Optional files you supply) is checked: "Official NVIDIA file (signed, version …)" or "Modified file: not signed by NVIDIA, can't be verified as safe", with the rule for your card. Upshift never downloads or bundles it. Removing it offers to switch the games that use it back to regular OptiScaler.
- The DLSS 5 builds are updated from the Updates page like OptiScaler.

### Library

- "Hide from library" on a game's right-click menu, and "Show hidden games" in Settings.
- Launchers such as REDlauncher no longer show up as games.

### Updates

- Checks each component's GitHub releases, with Stable or Beta, a version picker and "Keep updated".
- Updates games while keeping your OptiScaler settings, "Undo last update", and repair of changed or missing files.
- A dated "Changes Upshift made to this game" list.

### Play

- Starts games through their own store, with optional launch options. Games are never started as admin.

### Install and self-updates

- Installer for your Windows account (no admin needed) with a Start menu shortcut, and a portable zip.
- Upshift checks for new versions of itself at start-up and in Settings > About, and updates with "Restart to update".
- Uninstalling asks whether to delete Upshift's data too; it's kept unless you say yes.
