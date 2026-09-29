local NS = {}
assert(loadfile("companion/WowProfileMoverBridge/Core.lua"))("WowProfileMoverBridge", NS)
local Core = NS.Core
local count = 0
local function eq(a, b, msg) assert(Core.Same(a, b), msg or "values differ") end
local function test(name, fn) local ok, err = pcall(fn); assert(ok, name..": "..tostring(err)); count = count + 1; print("PASS "..name) end

WOW_PROJECT_ID = 1
function GetBuildInfo() return "12.0.1", "1", "today", 120100 end
function UnitClass() return "Mage", "MAGE" end
function UnitName() return "Tester" end
function UnitGUID() return "Player-1-Test" end
function GetRealmName() return "TestRealm" end
function time() return 100 end
function InCombatLockdown() return false end
local currentScope, bindingWrites, currentKeys = 2, {}, {}
function GetCurrentBindingSet() return currentScope end
function GetNumBindings() return 2 end
function GetBinding(index) return index == 1 and "JUMP" or "HOUSING", "CATEGORY", "SPACE" end
function GetBindingAction(key, checkOverride, context)
    assert(not checkOverride, "override binding lookup used for stored key")
    return currentKeys[tostring(context)..":"..key] or ""
end
function SetBinding(key, action, context)
    bindingWrites[#bindingWrites + 1] = {key, action, context}
    currentKeys[tostring(context)..":"..key] = action
    return true
end
function SaveBindings(scope) eq(scope, currentScope); return true end
function GetBindingKey(action, _, context)
    eq(context, action == "JUMP" and 0 or 1, "wrong context sent to GetBindingKey")
    return "SPACE"
end
local function enableContexts()
    Enum = {BindingContext = {None = 0}}
    C_KeyBindings = {
        GetBindingContextForAction = function(action) return action == "JUMP" and 0 or 1 end,
        GetBindingByKey = function(key, context) return currentKeys[tostring(context)..":"..key] or "" end,
    }
end
MAX_ACCOUNT_MACROS, MAX_CHARACTER_MACROS = 120, 18
local macros, extraMacros, macroCreated = {}, 0, nil
function GetNumMacros() return #macros + extraMacros, 0 end
function GetMacroInfo(index) local m = macros[index]; if m then return m.name, m.icon, m.body end end
function CreateMacro(name, icon, body, character)
    macroCreated = {name = name, icon = icon, body = body, character = character}
    macros[#macros + 1] = macroCreated
    return #macros
end
local cvarValue, cvarFlags = "0", {false, false, false}
C_CVar = {
    GetCVarInfo = function() return cvarValue, "0", true, false, cvarFlags[1], cvarFlags[2], cvarFlags[3] end,
    SetCVar = function(_, value) cvarValue = value end,
}
assert(loadfile("companion/WowProfileMoverBridge/Runtime.lua"))("WowProfileMoverBridge", NS)
local API = NS.API

test("runtime exports full flavor and identity metadata", function()
    local metadata = API.metadata()
    eq(metadata.family, 12); eq(metadata.guid, "Player-1-Test"); eq(metadata.season, 0); eq(metadata.regional, false)
end)
test("modern binding capture and write preserve identical keys in different contexts", function()
    enableContexts()
    local result = API.bindings()
    eq(result.mode, "contexts"); eq(#result.keys, 2)
    eq(result.keys[1].context, 0); eq(result.keys[2].context, 1)
    assert(API.setKey("SPACE", "JUMP", 0)); assert(API.setKey("SPACE", "HOUSING", 1))
    eq(API.getKey("SPACE", 0), "JUMP"); eq(API.getKey("SPACE", 1), "HOUSING")
end)
test("partial modern context API fails instead of flattening", function()
    enableContexts(); local exactLookup = GetBindingAction; GetBindingAction = nil
    local captured = API.bindings()
    eq(captured, nil)
    GetBindingAction = exactLookup
    C_KeyBindings.GetBindingContextForAction = nil
    eq(pcall(API.bindings), false)
    C_KeyBindings = nil
    eq(pcall(API.bindings), false)
    Enum = nil
end)
test("modifier fallback cannot create an unintended binding during restore", function()
    enableContexts()
    currentKeys["0:G"] = "BASEG"
    currentKeys["0:CTRL-G"] = nil
    C_KeyBindings.GetBindingByKey = function() return "BASEG" end
    eq(API.getKey("CTRL-G", 0), "", "effective fallback read instead of exact stored key")
    local db = {schema = 1, export = {metadata = API.metadata(), macros = {}, cvars = {},
        bindings = {scope = 2, mode = "contexts", keys = {{key = "CTRL-G", action = "JUMP", context = 0}}}}}
    local app = Core.New(API, db)
    assert(app:Apply({bindings = true, bindingScope = 2}))
    eq(db.backup.bindings[1].before, "")
    assert(app:Restore())
    eq(currentKeys["0:CTRL-G"], nil, "restore introduced a modifier binding")
    eq(currentKeys["0:G"], "BASEG")
    C_KeyBindings, Enum = nil, nil
end)
test("legacy runtime refuses context key writes", function()
    local ok = API.setKey("SPACE", "JUMP", 1)
    eq(ok, false)
    assert(API.setKey("SPACE", "JUMP", nil)); eq(API.getKey("SPACE"), "JUMP")
end)
test("macro capture and append keep bodies as data", function()
    macros = {{name = "Existing", icon = 123, body = "/run error('do not execute')"}}
    local captured = API.macros()
    eq(captured[1].body, macros[1].body)
    assert(API.createMacro({name = "New", icon = 456, body = "/run error('never executed')", character = false}))
    eq(macroCreated.body, "/run error('never executed')")
end)
test("macro capacity and length failures prevent creation", function()
    local prior = #macros
    extraMacros = 120
    eq(API.createMacro({name = "Full", icon = 1, body = "test", character = false}), false)
    extraMacros = 0
    eq(API.createMacro({name = "TooLong", icon = 1, body = string.rep("x", 256), character = false}), false)
    eq(#macros, prior)
end)
test("current Constants macro limits work without legacy globals", function()
    local oldAccount, oldCharacter = MAX_ACCOUNT_MACROS, MAX_CHARACTER_MACROS
    local oldCount, oldInfo = GetNumMacros, GetMacroInfo
    MAX_ACCOUNT_MACROS, MAX_CHARACTER_MACROS = nil, nil
    Constants = {MacroConsts = {MAX_ACCOUNT_MACROS = 120, MAX_CHARACTER_MACROS = 30}}
    GetNumMacros = function() return 1, 1 end
    GetMacroInfo = function(index)
        if index == 1 then return "Account", 1, "/say account" end
        if index == 121 then return "Character", 2, "/say character" end
    end
    local captured = API.macros()
    eq(#captured, 2); eq(captured[2].index, 121); eq(captured[2].character, true)
    -- Namespaced limits take precedence over stale globals.
    MAX_ACCOUNT_MACROS, MAX_CHARACTER_MACROS = 999, 999
    eq(API.macros()[2].index, 121)
    GetNumMacros = function() return 0, 30 end
    eq(API.createMacro({name = "FullChar", icon = 1, body = "test", character = true}), false)
    MAX_ACCOUNT_MACROS, MAX_CHARACTER_MACROS = oldAccount, oldCharacter
    GetNumMacros, GetMacroInfo = oldCount, oldInfo
    Constants = nil
end)
test("CVar writes require all protection flags and an approved name", function()
    eq(API.cvarInfo("synchronizeBindings"), nil)
    assert(API.cvarInfo("autoLootDefault").writable)
    eq(API.cvarInfo("autoLootDefault").account, true)
    eq(API.cvarInfo("autoLootDefault").character, false)
    assert(API.setCvar("autoLootDefault", "1")); eq(cvarValue, "1")
    cvarFlags = {false, true, false}; eq(API.setCvar("autoLootDefault", "0"), false)
    cvarFlags = {}; eq(API.cvarInfo("autoLootDefault").writable, false)
end)

local lastAdded, lastDeleted
local function setupLayouts()
    Enum = {EditModeLayoutType = {Preset = 0, Account = 1, Character = 2}}
    Constants = {EditModeConsts = {EditModeMaxLayoutsPerType = 5}}
    EditModeManagerFrame = {
        layoutInfo = {activeLayout = 1, layouts = {
            {layoutName = "Modern", layoutType = 0, systems = {1}},
            {layoutName = "Classic", layoutType = 0, systems = {2}},
            {layoutName = "MyAccount", layoutType = 1, systems = {3}},
        }},
        IsShown = function(self) return self.shown or false end,
        HasActiveChanges = function(self) return self.changed or false end,
        GetActiveLayoutInfo = function(self) return self.layoutInfo.layouts[self.layoutInfo.activeLayout] end,
    }
    C_EditMode = {
        ConvertLayoutInfoToString = function(layout) return layout.layoutName..":"..tostring(layout.layoutType)..":"..tostring(layout.systems[1]) end,
        ConvertStringToLayoutInfo = function(value) if value ~= "source" then return nil end; return {layoutName = "source", layoutType = 1, systems = {9}} end,
        IsValidLayoutName = function(name) return #name > 0 end,
        SaveLayouts = function(info) EditModeManagerFrame.layoutInfo = info end,
        OnLayoutAdded = function(index, activate, imported) lastAdded = {index, activate, imported} end,
        OnLayoutDeleted = function(index) lastDeleted = index end,
    }
end
test("Edit Mode uses full preset-inclusive list and adds inactive character layout", function()
    setupLayouts()
    eq(API.getLayout(), "Modern:0:1")
    local saved = API.addLayout("source")
    assert(saved); eq(lastAdded, {4, false, true}); eq(#EditModeManagerFrame.layoutInfo.layouts, 4)
    eq(EditModeManagerFrame.layoutInfo.layouts[4].layoutType, 2)
    eq(EditModeManagerFrame.layoutInfo.activeLayout, 1)
    assert(API.removeLayout(saved)); eq(lastDeleted, 4); eq(#EditModeManagerFrame.layoutInfo.layouts, 3)
end)
test("Edit Mode preserves active or edited layouts and pending UI edits", function()
    setupLayouts()
    local saved = API.addLayout("source")
    EditModeManagerFrame.layoutInfo.activeLayout = 4
    eq(API.removeLayout(saved), false)
    EditModeManagerFrame.layoutInfo.activeLayout = 1
    EditModeManagerFrame.layoutInfo.layouts[4].systems[1] = 99
    eq(API.removeLayout(saved), false)
    EditModeManagerFrame.changed = true
    eq(API.addLayout("source"), false)
end)
test("Edit Mode capacity and malformed import fail before saving", function()
    setupLayouts(); Constants.EditModeConsts.EditModeMaxLayoutsPerType = 0
    eq(API.addLayout("source"), false); eq(#EditModeManagerFrame.layoutInfo.layouts, 3)
    Constants.EditModeConsts.EditModeMaxLayoutsPerType = 5
    eq(API.addLayout("invalid"), false); eq(#EditModeManagerFrame.layoutInfo.layouts, 3)
end)
print("Runtime tests passed: " .. count)
