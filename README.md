# WoW Profile Mover

A local Windows app for copying selected World of Warcraft settings between accounts or characters, with a preview, verified backups, and restore.

**Version 0.1.0 is a preview.** Desktop filesystem behavior is regression-tested against isolated fixtures. Live Retail and Classic validation is still required for addon profiles and the optional in-game companion. A successful file copy does not prove WoW accepted server-synchronized settings.

## Run

Extract the entire Windows release ZIP and run **WoWProfileMover.exe**. The release includes its .NET runtime; no Python, Node, Battle.net login, or API key is needed to use it. Keep the `Companion` folder beside the executable if you want the optional addon installer.

1. Enter the world once on the source and destination characters, then exit every WoW client normally.
2. Browse to the World of Warcraft installation or its individual client folder, then scan.
3. Select Retail, Classic Progression, Classic Era, or Classic Anniversary. Detection uses `_retail_`, `_classic_`, `_classic_era_`, and `_anniversary_`; unknown, PTR, and beta folders are deliberately unsupported. Settings transfers stay within the selected client. Classic modes sharing a client directory are not necessarily compatible with each other.
4. Choose **Account → Account** or **Character → Character**, then the source and destination.
5. Select categories and, optionally, individual addons. Review the account-wide effect before including account addon data.
6. Review the exact destination and file list, then choose **Back up and copy**.
7. Check the destination in game. Select the copied profile inside an addon when necessary, exit normally, and check again after logging in.

Account folder identifiers are displayed exactly as found on disk. They are not guaranteed to be the friendly WoW1/WoW2 labels. Character names help identify accounts. Old character folders may remain after a rename or transfer; inspect your selection.

## What is included

| Feature | Behavior |
| --- | --- |
| Account addon data | Replaces selected `SavedVariables/*.lua` files; destination-only files remain. |
| Character addon data | Copies the selected character's files. Account files require a separate option and acknowledgement. |
| Addon enablement | Copies `AddOns.txt` when present. The app does not install or update your other addons. |
| Local window layout | Copies `layout-local.txt` when present. |
| Advanced game caches | Opt-in copy of keybinding, macro, gameplay-option, Edit Mode, and chat cache files, with a synchronization warning. Missing files are reported. |
| Captured in-game settings | Copies the optional companion's capture in character mode. Applying it requires an explicit in-game action. |
| Restore | Previews affected files and saves current destination contents before restoring an earlier backup. |

**Addon files are replaced as whole databases, not merged.** Character-specific profiles often live in account-wide addon files. This version does not automatically remap character names or merge AceDB profiles. Replacing an account-wide addon file can affect all characters on that destination account. On the same account, both characters already share those files; use the addon's profile menu to select/copy the intended profile. The app never performs a global name replacement or executes SavedVariables as Lua.

Installation-wide `Config.wtf`, addon program files, `.bak`/`.old` files, checksum/synchronization metadata, and unrecognized files are not included in settings transfers. Graphics/device settings and game progress are outside this release's scope. Raw cache copying may be overridden by WoW's server data.

## Optional companion

The **In-game settings** tab can install `WowProfileMoverBridge` into the selected client while WoW is closed. It preserves an existing different version instead of overwriting it.

- On the source, activate the account or character binding set you want, then run `/wpm export` and fully exit WoW.
- Use **Character → Character → Captured in-game settings** to copy the capture.
- On the destination, run `/wpm import`, choose supported categories and the active destination binding scope, then apply.
- `/wpm restore` conditionally restores bindings/options and eligible imported layouts. Added macros are intentionally retained for manual removal because they may be referenced by action bars.

The companion supports the active binding set, additive macro import, a small tested gameplay-option list, and conditional Edit Mode import. It does not copy action-bar spell placement, talent builds, click-casting, or addon profile mappings. It never imports automatically on login. See [companion behavior and live test steps](companion/README.md).

## Backups and recovery

Defaults: `%LOCALAPPDATA%\WoWProfileMover\Backups`. The location can be changed in the app. Backups are local and are never automatically pruned or uploaded.

Each operation stores a JSON journal, exact previous bytes, hashes, and source staging data. All target backups are verified before the first destination write. The app detects stale previews, rejects linked/reparse and hard-linked target paths, and blocks writes while a recognized WoW process is running. If the game starts during a transfer, further writes stop and the operation remains available for recovery after WoW is closed.

Windows replacements are atomic per file, not across an entire transfer. Interrupted operations are shown in backup history. Restore changes only that operation's files and backs up newer destination contents first. Source data and unrelated destination files remain untouched. Empty directories created for copied files may remain after restore. File restore does not undo server-saved settings; use the companion's in-game restore for its changes.

Keep backups outside the WoW installation. Backup files can contain account/character names and addon data; treat them like the original WTF files. Do not edit a backup journal. Standard user privileges are used; the app does not clear read-only attributes or change folder permissions.

## Build and validate

In a source checkout, Windows x64, PowerShell 5.1+, and a .NET 10 SDK are required for development. `scripts/bootstrap.ps1` downloads a project-local SDK from Microsoft and verifies its SHA-512 digest. SDK and package caches stay under the ignored `.tools` folder.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bootstrap.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-package.ps1
```

Node/npm are needed only to install/run the pinned Lua test interpreter; they are not required by the release app. The publish script runs tests and produces the portable Windows ZIP plus SHA-256 checksum under `dist`.

Core tests cover exact-byte round trips, source integrity, scope boundaries, newer-data backups, corrupt staging/backups, stale previews, read-only/locked files, path aliases, native links, cancellation, process races, and a real subprocess crash followed by recovery. Companion tests exercise data transformations, API adapters, and UI wiring with mocks. App rendering/smoke uses synthetic folders only.

## Research basis

- [AceDB profile and identity mapping](https://github.com/WoWUIDev/Ace3/blob/master/AceDB-3.0/AceDB-3.0.lua)
- [Blizzard binding UI source mirror](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_Settings_Shared/Blizzard_Keybindings.lua)
- [Blizzard CVar API source mirror](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_APIDocumentationGenerated/CVarDocumentation.lua)
- [Blizzard Edit Mode source mirror](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_EditMode/Shared/EditModeManager.lua)
- [WowUp backup workflow](https://wowup.io/guide/my-addons/addon-settings)
- [WowUp client folder mapping, including Anniversary](https://github.com/WowUp/WowUp/blob/4fdae600d94ad7dd187a6bc93b517e657100b01a/wowup-electron/src/common/constants.ts#L219)
- [Windows file replacement guarantees and failure cases](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)

Independent project; not affiliated with Blizzard Entertainment. No external addon code or artwork is bundled apart from this project's original companion.
