local _, NS = ...
local API = {}
NS.API = API
local Core = NS.Core

function API.metadata()
    local _, _, _, interface = GetBuildInfo()
    local _, class = UnitClass("player")
    return {project = WOW_PROJECT_ID, interface = interface, family = math.floor(interface / 10000),
        season = C_Seasons and C_Seasons.GetActiveSeason and C_Seasons.GetActiveSeason() or 0,
        regional = RegionalUniqueNamesEnabled and RegionalUniqueNamesEnabled() or false,
        name = UnitName("player"), realm = GetRealmName(), class = class, guid = UnitGUID("player")}
end
function API.now() return time() end
function API.inCombat() return InCombatLockdown() end

local function contextAPI()
    local hasContexts = (C_KeyBindings and (C_KeyBindings.GetBindingContextForAction or C_KeyBindings.GetBindingByKey)) or (Enum and Enum.BindingContext)
    if not hasContexts then return false end
    if not C_KeyBindings or not C_KeyBindings.GetBindingContextForAction or not GetBindingAction or not GetBindingKey or not Enum or not Enum.BindingContext then
        error("Incomplete binding context API")
    end
    return true
end

function API.bindings()
    if not GetNumBindings or not GetBinding or not GetBindingAction or not GetCurrentBindingSet or not SetBinding or not SaveBindings then return nil, "binding API unavailable" end
    local contexts = contextAPI()
    local result = {scope = GetCurrentBindingSet(), mode = contexts and "contexts" or "legacy", keys = {}}
    if result.scope ~= 1 and result.scope ~= 2 then return nil, "unknown binding scope" end
    local seen = {}
    for i = 1, GetNumBindings() do
        local action, _, first, second = GetBinding(i)
        if type(action) == "string" and action ~= "" then
            local context, keys
            if contexts then
                context = C_KeyBindings.GetBindingContextForAction(action)
                if context == nil then context = Enum.BindingContext.None end
                if type(context) ~= "number" then return nil, "unrecognized binding context" end
                keys = {GetBindingKey(action, nil, context)}
            else keys = {first, second} end
            for _, key in pairs(keys) do
                if type(key) == "string" and key ~= "" then
                    local id = tostring(context) .. ":" .. key
                    if seen[id] and seen[id] ~= action then return nil, "ambiguous bindings; capture was skipped" end
                    if not seen[id] then
                        seen[id] = action
                        result.keys[#result.keys + 1] = {key = key, action = action, context = context}
                    end
                end
            end
        end
    end
    table.sort(result.keys, function(a, b) return tostring(a.context)..":"..a.key < tostring(b.context)..":"..b.key end)
    return result
end

function API.getKey(key, context)
    if contextAPI() then
        if type(context) ~= "number" then error("Missing binding context") end
        -- Read the exact stored key, excluding overrides/modifier fallback. This
        -- matches Blizzard's keybinding editor and is essential for rollback.
        return GetBindingAction(key, nil, context)
    end
    if context ~= nil then error("Cannot flatten context binding") end
    return GetBindingAction(key, nil)
end
function API.setKey(key, action, context)
    if InCombatLockdown() then return false, "in combat" end
    if contextAPI() then
        if type(context) ~= "number" then return false, "missing binding context" end
        return SetBinding(key, action, context)
    end
    if context ~= nil then return false, "cannot flatten binding context" end
    return SetBinding(key, action)
end
function API.saveBindings(scope)
    if GetCurrentBindingSet() ~= scope then return false, "binding scope changed" end
    local result = SaveBindings(scope)
    return result ~= false
end

local function macroLimits()
    local limits = Constants and Constants.MacroConsts
    local account = limits and limits.MAX_ACCOUNT_MACROS or MAX_ACCOUNT_MACROS
    local character = limits and limits.MAX_CHARACTER_MACROS or MAX_CHARACTER_MACROS
    if type(account) ~= "number" or account <= 0 or account % 1 ~= 0
        or type(character) ~= "number" or character <= 0 or character % 1 ~= 0 then return nil end
    return account, character
end
function API.macros()
    local accountLimit, characterLimit = macroLimits()
    if not GetNumMacros or not GetMacroInfo or not accountLimit or not characterLimit then return nil, "macro API unavailable" end
    local result = {}
    local countAccount, countCharacter = GetNumMacros()
    local function read(index, character)
        local name, icon, body = GetMacroInfo(index)
        if name and body then
            if C_Macro and C_Macro.GetSelectedMacroIcon then icon = C_Macro.GetSelectedMacroIcon(index) or icon end
            result[#result + 1] = {index = index, name = name, icon = icon or 134400, body = body, character = character}
        end
    end
    for i = 1, countAccount do read(i, false) end
    for i = 1, countCharacter do read(accountLimit + i, true) end
    return result
end
function API.createMacro(macro)
    if InCombatLockdown() or not CreateMacro then return false, "macro creation unavailable" end
    local accountLimit, characterLimit = macroLimits()
    if not accountLimit or not characterLimit or not GetNumMacros or not GetMacroInfo then return false, "macro limits or API unavailable" end
    if #macro.body > 255 or (strlenutf8 and strlenutf8(macro.name) or #macro.name) > 16 then return false, "macro exceeds supported name/body length" end
    local account, character = GetNumMacros()
    if (macro.character and character >= characterLimit) or (not macro.character and account >= accountLimit) then return false, "macro storage is full" end
    local index = CreateMacro(macro.name, macro.icon, macro.body, macro.character)
    if type(index) ~= "number" then return false, "macro creation was refused" end
    local name, _, body = GetMacroInfo(index)
    if name ~= macro.name or body ~= macro.body then return false, "macro verification failed; review it in the macro window" end
    return index
end

function API.cvarInfo(name)
    if not Core.CVARS[name] then return nil end
    local fn = C_CVar and C_CVar.GetCVarInfo or GetCVarInfo
    if not fn then return nil end
    local value, _, account, character, locked, secure, readOnly = fn(name)
    if type(value) ~= "string" then return nil end
    -- Missing flags on an older API are not proof that a setting is writable.
    return {value = value, writable = locked == false and secure == false and readOnly == false,
        account = account == true, character = character == true}
end
function API.setCvar(name, value)
    local info = API.cvarInfo(name)
    if not info or not info.writable then return false, "unavailable or protected" end
    local fn = C_CVar and C_CVar.SetCVar or SetCVar
    if not fn then return false, "CVar setter unavailable" end
    fn(name, value)
    local after = API.cvarInfo(name)
    return after and after.value == value or false
end

local function editManager()
    local manager = EditModeManagerFrame
    if not C_EditMode or not C_EditMode.ConvertLayoutInfoToString or not C_EditMode.ConvertStringToLayoutInfo
        or not C_EditMode.SaveLayouts or not C_EditMode.OnLayoutAdded or not C_EditMode.OnLayoutDeleted
        or not Enum or not Enum.EditModeLayoutType or not manager or not manager.layoutInfo
        or not manager.GetActiveLayoutInfo or not manager.HasActiveChanges then return nil, "Edit Mode is unavailable on this client" end
    if manager:IsShown() or manager:HasActiveChanges() or manager.overrideLayoutInfo then return nil, "close Edit Mode and save/revert pending changes first" end
    return manager
end
function API.getLayout()
    local manager, why = editManager()
    if not manager then return nil, why end
    local active = manager:GetActiveLayoutInfo()
    if not active then return nil, "no active layout" end
    return C_EditMode.ConvertLayoutInfoToString(active)
end
function API.addLayout(serialized)
    if InCombatLockdown() then return false, "in combat" end
    local manager, why = editManager()
    if not manager then return false, why end
    local layout = C_EditMode.ConvertStringToLayoutInfo(serialized)
    if not layout or type(layout.systems) ~= "table" then return false, "layout format was rejected" end
    local cap = Constants and Constants.EditModeConsts and Constants.EditModeConsts.EditModeMaxLayoutsPerType
    if not cap then return false, "layout capacity cannot be verified" end
    local count, names = 0, {}
    for _, current in ipairs(manager.layoutInfo.layouts) do
        names[current.layoutName] = true
        if current.layoutType == Enum.EditModeLayoutType.Character then count = count + 1 end
    end
    if count >= cap then return false, "character layout storage is full" end
    local name, suffix = "WPM Imported", 1
    while names[name] do suffix = suffix + 1; name = "WPM Imported " .. suffix end
    if not C_EditMode.IsValidLayoutName(name) then return false, "layout name rejected" end
    layout.layoutName, layout.layoutType = name, Enum.EditModeLayoutType.Character
    local info = Core.Copy(manager.layoutInfo)
    local index = #info.layouts + 1
    table.insert(info.layouts, layout)
    -- Blizzard's manager passes its full list, including presets, to SaveLayouts.
    C_EditMode.SaveLayouts(info)
    C_EditMode.OnLayoutAdded(index, false, true)
    return {name = name, serialized = C_EditMode.ConvertLayoutInfoToString(layout)}
end
function API.removeLayout(saved)
    if InCombatLockdown() then return false, "in combat" end
    local manager, why = editManager()
    if not manager then return false, why end
    local match
    for index, layout in ipairs(manager.layoutInfo.layouts) do
        if layout.layoutName == saved.name then
            if match then return false, "ambiguous layout name" end
            match = index
        end
    end
    if not match then return true end
    local current = manager.layoutInfo.layouts[match]
    if current.layoutType ~= Enum.EditModeLayoutType.Character or manager.layoutInfo.activeLayout == match
        or C_EditMode.ConvertLayoutInfoToString(current) ~= saved.serialized then return false, "layout is active or has changed" end
    local info = Core.Copy(manager.layoutInfo)
    table.remove(info.layouts, match)
    C_EditMode.SaveLayouts(info)
    C_EditMode.OnLayoutDeleted(match)
    return true
end
