local NS = {}
assert(loadfile("companion/WowProfileMoverBridge/Core.lua"))("WowProfileMoverBridge", NS)
local Core, count = NS.Core, 0
local function eq(actual, expected, label)
    assert(Core.Same(actual, expected), (label or "mismatch") .. ": " .. tostring(actual) .. " ~= " .. tostring(expected))
end
local function test(name, fn)
    local ok, err = pcall(fn)
    if not ok then error(name .. ": " .. tostring(err)) end
    count = count + 1
    print("PASS " .. name)
end
local function fixture(mode)
    local state = {scope = 2, mode = mode or "legacy", keys = {}, macros = {}, cvars = {autoLootDefault = "1", showTutorials = "0"},
        metadata = {project = 1, interface = 120100, family = 12, season = 0, regional = false, name = "Source", realm = "Realm", class = "MAGE", guid = "source-guid"}, calls = {}}
    local api = {}
    function api.metadata() return Core.Copy(state.metadata) end
    function api.now() return 100 end
    function api.inCombat() return state.combat end
    function api.bindings()
        local result = {scope = state.scope, mode = state.mode, keys = {}}
        for _, key in pairs(state.keys) do result.keys[#result.keys + 1] = Core.Copy(key) end
        return result
    end
    function api.getKey(key, context) local entry = state.keys[tostring(context)..":"..key]; return entry and entry.action or "" end
    function api.setKey(key, action, context)
        state.calls[#state.calls + 1] = "binding"
        state.keys[tostring(context)..":"..key] = action and {key = key, action = action, context = context} or nil
        return true
    end
    function api.saveBindings(scope) state.savedScope = scope; return not state.saveFail end
    function api.macros() return Core.Copy(state.macros) end
    function api.createMacro(macro)
        if state.full then return false, "macro storage is full" end
        local value = Core.Copy(macro)
        value.index = 100 + #state.macros
        state.macros[#state.macros + 1] = value
        state.calls[#state.calls + 1] = "macro"
        return value.index
    end
    function api.cvarInfo(name)
        local value = state.cvars[name]
        return value and {value = value, writable = not state.locked} or nil
    end
    function api.setCvar(name, value) state.cvars[name] = value; state.calls[#state.calls + 1] = "cvar"; return true end
    function api.getLayout() return state.layout end
    function api.addLayout(serialized) state.added = {name = "WPM Imported", serialized = serialized}; return state.added end
    function api.removeLayout() if state.layoutChanged then return false, "layout has changed" end; state.added = nil; return true end
    local db, app = {}, nil
    app = Core.New(api, db)
    local function destination()
        state.metadata.name, state.metadata.guid = "Target", "target-guid"
        state.calls = {}
    end
    return app, state, db, destination, api
end
local function macro(name, body, index, character) return {name = name, body = body, icon = 134400, index = index or 1, character = character or false} end

test("capture persists without login-time mutation", function()
    local app, state, db, _, api = fixture()
    state.macros = {macro("Test", "/cast Fireball")}
    assert(app:Export())
    local snapshot = Core.Copy(db)
    Core.New(api, db)
    eq(db, snapshot)
    eq(db.schema, 1)
    eq(db.export.metadata.exportedAt, 100)
end)
test("flavor and season mismatches reject before any writes", function()
    local app, state, db, dest = fixture()
    app:Export(); dest(); state.metadata.project = 2
    eq(app:Apply({cvars = true}), false); eq(#state.calls, 0); eq(db.backup, nil)
    state.metadata.project = 1; state.metadata.season = 2
    eq(app:Apply({cvars = true}), false)
end)
test("combat blocks capture import and restore", function()
    local app, state, _, dest = fixture()
    app:Export(); dest(); state.combat = true
    eq(app:Export(), false); eq(app:Apply({cvars = true}), false); eq(app:Restore(), false); eq(#state.calls, 0)
end)
test("unapproved option and malformed bindings fail closed", function()
    local app, state, db = fixture()
    app:Export(); db.export.cvars.synchronizeBindings = "0"
    eq(app:Apply({cvars = true}), false); eq(#state.calls, 0)
    db.export.cvars.synchronizeBindings = nil
    db.export.bindings.keys = {{key = "A", action = "JUMP"}, {key = "A", action = "OTHER"}}
    eq(app:Apply({bindings = true, bindingScope = 2}), false)
end)
test("append macros while preserving duplicates and conflicting names", function()
    local app, state, _, dest = fixture()
    state.macros = {macro("Same", "/say original"), macro("Conflict", "/say source", 2), macro("New", "/cast Heal", 3, true)}
    app:Export(); dest()
    state.macros = {macro("Same", "/say original"), macro("Conflict", "/say target", 2)}
    assert(app:Apply({macros = true}))
    eq(#state.macros, 3); eq(state.macros[2].body, "/say target"); eq(state.macros[3].character, true)
    assert(app:Restore()); eq(#state.macros, 3, "restore must not delete macros now used elsewhere")
end)
test("full macro storage never replaces existing macros", function()
    local app, state, _, dest = fixture()
    state.macros = {macro("New", "/say safe")}; app:Export(); dest()
    state.macros = {macro("Kept", "/say existing")}; state.full = true
    assert(app:Apply({macros = true})); eq(#state.macros, 1); eq(state.macros[1].name, "Kept")
end)
test("legacy key overlay and conflict-aware restore preserve later edits", function()
    local app, state, _, dest, api = fixture()
    api.setKey("1", "ACTIONBUTTON1"); api.setKey("2", "ACTIONBUTTON2"); app:Export(); dest()
    state.keys = {}; api.setKey("1", "JUMP"); api.setKey("X", "TOGGLEMAP")
    assert(app:Apply({bindings = true, bindingScope = 2})); eq(api.getKey("1"), "ACTIONBUTTON1"); eq(api.getKey("X"), "TOGGLEMAP")
    api.setKey("2", "CUSTOM")
    assert(app:Restore()); eq(api.getKey("1"), "JUMP"); eq(api.getKey("2"), "CUSTOM"); eq(api.getKey("X"), "TOGGLEMAP")
end)
test("binding contexts remain independent", function()
    local app, state, _, dest, api = fixture("contexts")
    api.setKey("A", "MOVELEFT", 0); api.setKey("A", "HOUSINGROTATE", 1); app:Export(); dest(); state.keys = {}
    assert(app:Apply({bindings = true, bindingScope = 2}))
    eq(api.getKey("A", 0), "MOVELEFT"); eq(api.getKey("A", 1), "HOUSINGROTATE")
    assert(app:Restore()); eq(api.getKey("A", 0), ""); eq(api.getKey("A", 1), "")
end)
test("context incompatibility and wrong scope skip bindings", function()
    local app, state, _, dest, api = fixture("contexts")
    api.setKey("A", "MOVELEFT", 0); app:Export(); dest(); state.keys = {}; state.mode = "legacy"
    assert(app:Apply({bindings = true, bindingScope = 2})); eq(next(state.keys), nil)
    app:Forget(); state.mode = "contexts"; state.scope = 1
    assert(app:Apply({bindings = true, bindingScope = 2})); eq(next(state.keys), nil)
end)
test("macro index bindings remap only to exact unique destination macro", function()
    local app, state, _, dest, api = fixture()
    state.macros = {macro("Fire", "/cast Fireball", 1)}; api.setKey("F", "MACRO 1"); app:Export(); dest()
    state.keys = {}; state.macros = {macro("Other", "/say kept", 1), macro("Fire", "/cast Fireball", 8)}
    assert(app:Apply({bindings = true, bindingScope = 2})); eq(api.getKey("F"), "MACRO Fire")
    app:Forget(); state.keys = {}; state.macros[2].body = "/cast Frostbolt"
    assert(app:Apply({bindings = true, bindingScope = 2})); eq(api.getKey("F"), "")
end)
test("CVar restore preserves later changes and protected options", function()
    local app, state, _, dest = fixture()
    app:Export(); dest(); state.cvars.autoLootDefault = "0"; state.cvars.showTutorials = "1"
    assert(app:Apply({cvars = true})); eq(state.cvars.autoLootDefault, "1")
    state.cvars.showTutorials = "later"
    assert(app:Restore()); eq(state.cvars.autoLootDefault, "0"); eq(state.cvars.showTutorials, "later")
end)
test("preview names option values and warns about account scope", function()
    local app, _, _, _, api = fixture()
    app:Export()
    api.cvarInfo = function() return {value = "0", writable = true, account = true, character = false} end
    local ok, lines = app:Preview()
    assert(ok)
    local text = table.concat(lines, "\n")
    assert(text:find("every character on the destination WoW account", 1, true))
    assert(text:find("autoLootDefault = 1 (account-wide)", 1, true))
end)
test("restore point not overwritten and source backup cannot restore on target", function()
    local app, state, db, dest = fixture()
    app:Export(); assert(app:Apply({cvars = true})); eq(app:Apply({cvars = true}), false)
    dest(); eq(app:Restore(), false)
    assert(app:Apply({cvars = true})); assert(db.backup.owner:find("target%-guid"))
end)
test("restore wrong scope can be retried", function()
    local app, state, db, dest, api = fixture()
    api.setKey("A", "JUMP"); app:Export(); dest(); state.keys = {}
    app:Apply({bindings = true, bindingScope = 2}); state.scope = 1
    eq(app:Restore(), false); eq(db.backup.restored, false)
    state.scope = 2; assert(app:Restore()); eq(api.getKey("A"), "")
end)
test("changed or active imported layout is kept for manual review", function()
    local app, state, _, dest = fixture()
    state.layout = "layout source"; app:Export(); dest(); app:Apply({editmode = true})
    state.layoutChanged = true; assert(app:Restore()); assert(state.added)
end)
print("Core tests passed: " .. count)
