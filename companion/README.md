# Optional in-game capture

The `WowProfileMoverBridge` addon complements the desktop file copier. It uses WoW's own APIs for settings that can be server synchronized. It never applies settings on login and never evaluates captured macro text.

1. Install the `WowProfileMoverBridge` directory under the selected client's `Interface/AddOns` directory.
2. On the source character, select the desired account or character binding set in WoW's keybinding settings, then run `/wpm export`.
3. Exit **all WoW clients fully**. In the desktop app, copy the source character's **Captured in-game settings** to the target character.
4. Log into the target and run `/wpm import`. Read the preview, select categories and the destination binding scope, then click **Apply selected settings**. The destination binding scope must already be active in WoW's keybinding settings.
5. Review the chat/report output. Select the newly added layout in Edit Mode if wanted. Exit WoW to persist the result and restore point.

The per-character file is `WTF/Account/<account>/<realm>/<character>/SavedVariables/WowProfileMoverBridge.lua`. Its top-level variable is `WowProfileMoverBridgeDB`, schema `1`. The desktop app copies this file as data and does not execute Lua. The addon validates its capture again before applying it. Export capture and local restore journal are separate fields; restore points are tied to the destination character GUID, name, realm and client.

## Behavior

- **Bindings:** captures the currently active binding set, including distinct binding contexts when the client exposes the verified context API. It applies captured keys over matching destination keys, retaining all unrelated keys. It refuses partial modern context APIs and mismatched context formats. It does not capture both account and character sets simultaneously or change WoW's synchronization settings.
- **Macros:** adds missing macros in their original account/character scope. Exact duplicates and conflicting names are skipped. Capacity and length checks precede creation. Direct macro keybindings are remapped only when the source macro matches one unambiguous destination macro by name, body and scope. Macro index numbers are never used as destination identity. Macro bodies are stored, never run by the importer.
- **Options:** only `autoLootDefault`, `autoSelfCast`, `deselectOnClick`, `lootUnderMouse`, `showTargetOfTarget`, `showTutorials`, `countdownForCooldowns`, and `showTimestamps`. Some apply to all characters on the destination account. Preview lists captured names/values and the target API's account/character scope. Unknown options and options without explicit unlocked, non-secure, non-read-only API flags are skipped.
- **Edit Mode:** conditional on runtime capability, creates a uniquely named, inactive character layout, preserving existing layouts and active selection. It refuses pending Edit Mode edits and full layout storage.
- **Client compatibility:** project, interface family, season and regional-name mode must match. No Retail-to-Classic or cross-expansion/season imports.
- **Excluded:** action-bar spell/item placement, talents, equipment, click-casting bindings, addon profile remapping, and settings outside the reviewed option list.

## Restore and limitations

`/wpm restore` (also the panel's Restore button) reverses imported keys and options only when they still match the applied values. Later changes are kept. Select the original destination binding scope before restoring keys. Unchanged imported layouts can be removed when inactive. Active, edited or ambiguous layouts are kept for manual review.

**Added macros are retained during restore**, because they may have acquired action-bar references after import. Remove unwanted macros manually. A new import cannot overwrite an outstanding local restore point; `/wpm forget confirm` explicitly keeps changes and discards that point.

The journal is held in SavedVariables and is persisted by the client on logout/reload; it is not a durable on-disk transaction while WoW is running. API refusals or partial category failures are reported, and the restore point is retained. Desktop file backups and in-game restore serve different purposes: copying old files alone does not guarantee reversal of server-synchronized settings.

## Validation

The addon has Lua syntax checks, migration/restore regression tests, API-adapter mocks and a UI smoke test. **Live Retail and Classic validation remains required** before claiming game-client compatibility. API protection rules and supported options may differ between builds. The TOC lists the researched supported client interfaces; a future client may show the addon as out of date.

From the project root, with the local ignored Fengari runtime installed:

```powershell
.\.tools\lua-runtime\node_modules\.bin\fengari.cmd companion/tests/core_spec.lua
.\.tools\lua-runtime\node_modules\.bin\fengari.cmd companion/tests/runtime_spec.lua
.\.tools\lua-runtime\node_modules\.bin\fengari.cmd companion/tests/ui_smoke_spec.lua
```

Live smoke test: use disposable settings on one source/target pair per client flavor, export and fully quit, copy, preview, verify no changes before Apply, import a conflicting key and macro name, test a full macro store, select an imported layout, change one imported key/option, restore and confirm the later change remains. Confirm a relog keeps intended results and that combat blocks apply/restore.

## API references reviewed

- [Blizzard keybinding implementation](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_Settings_Shared/Blizzard_Keybindings.lua)
- [Generated keybinding API](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_APIDocumentationGenerated/KeyBindingsDocumentation.lua)
- [Generated CVar API](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_APIDocumentationGenerated/CVarDocumentation.lua)
- [Generated macro limits](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_APIDocumentationGenerated/MacroConstantsDocumentation.lua)
- [Generated Edit Mode API](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_APIDocumentationGenerated/EditModeManagerDocumentation.lua)
- [Blizzard Edit Mode manager](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_EditMode/Shared/EditModeManager.lua)

These are references for API behavior; the companion's implementation is original.
