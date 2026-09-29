-- Original migration logic. No captured text is evaluated as Lua.
local _, NS = ...
local Core = {}
NS.Core = Core

Core.CVARS = {
    autoLootDefault = true, autoSelfCast = true, deselectOnClick = true,
    lootUnderMouse = true, showTargetOfTarget = true, showTutorials = true,
    countdownForCooldowns = true, showTimestamps = true,
}

local function copy(value)
    if type(value) ~= "table" then return value end
    local result = {}
    for k, v in pairs(value) do result[k] = copy(v) end
    return result
end
local function same(a, b)
    if type(a) ~= type(b) then return false end
    if type(a) ~= "table" then return a == b end
    for k, v in pairs(a) do if not same(v, b[k]) then return false end end
    for k in pairs(b) do if a[k] == nil then return false end end
    return true
end
local function validString(v, limit) return type(v) == "string" and #v <= limit end
local function owner(m) return tostring(m.project)..":"..tostring(m.family)..":"..tostring(m.guid)..":"..tostring(m.name)..":"..tostring(m.realm) end
local function report(log, text) log[#log + 1] = text end
local function attempt(log, label, fn)
    local ok, result, detail = pcall(fn)
    if not ok then report(log, label .. ": API rejected the operation."); return false end
    if result == false or result == nil then report(log, label .. ": " .. (detail or "not available")); return false end
    return result
end

function Core.New(api, db)
    return setmetatable({api = api, db = db}, {__index = Core})
end

function Core:Compatible(data)
    if type(data) ~= "table" or type(data.metadata) ~= "table" then return false, "No captured export. Use /wpm export on the source first." end
    local src, dst = data.metadata, self.api.metadata()
    if src.project ~= dst.project or src.family ~= dst.family or src.season ~= dst.season or src.regional ~= dst.regional then
        return false, "Different WoW client flavor or season. This capture cannot be imported here."
    end
    return true
end

function Core:Export()
    if self.api.inCombat() then return false, {"Leave combat before capturing settings."} end
    local log, api = {}, self.api
    local data = {metadata = api.metadata(), cvars = {}, macros = {}}
    data.metadata.exportedAt = api.now()
    local bindings = attempt(log, "Bindings", api.bindings)
    if bindings then data.bindings = bindings end
    local macros = attempt(log, "Macros", api.macros)
    if macros then data.macros = macros end
    for name in pairs(self.CVARS) do
        local ok, info = pcall(api.cvarInfo, name)
        if ok and info and info.writable and validString(info.value, 128) then data.cvars[name] = info.value end
    end
    local layout = attempt(log, "Edit Mode", api.getLayout)
    if layout then data.editmode = layout end
    self.db.schema = 1
    self.db.export = data
    report(log, "Captured current settings. Exit WoW fully before copying this character's capture.")
    report(log, "Bindings capture only the currently selected account or character binding set.")
    return true, log
end

function Core:Validate(data)
    if self.db.schema ~= 1 then return false, "Unsupported capture version." end
    local ok, reason = self:Compatible(data)
    if not ok then return false, reason end
    if type(data.macros) ~= "table" or #data.macros > 500 or type(data.cvars) ~= "table" then return false, "Invalid capture data." end
    for _, macro in ipairs(data.macros) do
        if type(macro) ~= "table" or not validString(macro.name, 64) or #macro.name == 0 or not validString(macro.body, 1024)
            or (type(macro.icon) ~= "string" and type(macro.icon) ~= "number") or type(macro.character) ~= "boolean" then
            return false, "Invalid macro data."
        end
    end
    if data.bindings then
        local b = data.bindings
        if type(b) ~= "table" or (b.mode ~= "legacy" and b.mode ~= "contexts") or type(b.keys) ~= "table"
            or #b.keys > 5000 or (b.scope ~= 1 and b.scope ~= 2) then return false, "Invalid binding data." end
        local seen = {}
        for _, key in ipairs(b.keys) do
            if type(key) ~= "table" or not validString(key.key, 100) or #key.key == 0 or not validString(key.action, 200)
                or #key.action == 0 or (b.mode == "contexts" and (type(key.context) ~= "number" or key.context < 0 or key.context > 100 or key.context % 1 ~= 0))
                or (b.mode == "legacy" and key.context ~= nil) then return false, "Invalid binding data." end
            local identity = tostring(key.context) .. ":" .. key.key
            if seen[identity] then return false, "Duplicate key in binding capture." end
            seen[identity] = true
        end
    end
    for name, value in pairs(data.cvars) do
        if not self.CVARS[name] or not validString(value, 128) then return false, "Capture contains an unapproved game option." end
    end
    if data.editmode and not validString(data.editmode, 1000000) then return false, "Invalid Edit Mode capture." end
    return true
end

function Core:Preview()
    local data, lines = self.db.export, {}
    local ok, reason = self:Validate(data)
    if not ok then return false, {reason} end
    report(lines, "Source: " .. tostring(data.metadata.name) .. " - " .. tostring(data.metadata.realm))
    report(lines, "Captured bindings: " .. (data.bindings and tostring(#data.bindings.keys) .. " keys (" .. (data.bindings.scope == 2 and "character" or "account") .. ")" or "unavailable"))
    local account, character = 0, 0
    for _, m in ipairs(data.macros) do if m.character then character = character + 1 else account = account + 1 end end
    report(lines, "Macros: " .. account .. " account + " .. character .. " character; existing macros are preserved.")
    local count = 0
    for _ in pairs(data.cvars) do count = count + 1 end
    report(lines, "Game options: " .. count .. " reviewed options. Edit Mode: " .. (data.editmode and "captured" or "unavailable"))
    if count > 0 then
        report(lines, "Some game options apply to every character on the destination WoW account.")
        local names = {}
        for name in pairs(data.cvars) do names[#names + 1] = name end
        table.sort(names)
        for _, name in ipairs(names) do
            local readable, info = pcall(self.api.cvarInfo, name)
            local scope = readable and info and (info.account and "account-wide" or info.character and "character" or "client/UI") or "unavailable; skipped"
            report(lines, name .. " = " .. data.cvars[name] .. " (" .. scope .. ")")
        end
    end
    report(lines, "Keys overwrite matching destination keys; other keys remain. Macro name conflicts are skipped.")
    report(lines, "Restore preserves later changes. Added macros stay for manual review; unchanged inactive layouts can be removed.")
    return true, lines
end

local function macroMatches(a, b)
    return a.name == b.name and a.body == b.body and a.character == b.character
end

function Core:Apply(selection)
    local log, api, data = {}, self.api, self.db.export
    if api.inCombat() then return false, {"Leave combat before importing settings."} end
    local ok, reason = self:Validate(data)
    if not ok then return false, {reason} end
    -- A copied source backup must never be mistaken for this character's backup.
    local backupOwner = owner(api.metadata())
    if self.db.backup and self.db.backup.owner == backupOwner and not self.db.backup.restored then
        return false, {"A restore point already exists. Restore it or use /wpm forget to keep those changes before another import."}
    end
    local backup = {owner = backupOwner, createdAt = api.now(), bindings = {}, cvars = {}, macros = {}, layouts = {}}
    self.db.backup = backup
    -- Append first, so direct macro keybindings can be remapped by verified name/body.
    if selection.macros then
        for _, src in ipairs(data.macros) do
            local current = attempt(log, "Read macros", api.macros)
            if not current then break end
            local conflict, duplicate = false, false
            for _, dst in ipairs(current) do
                if dst.name == src.name then
                    conflict = true
                    if macroMatches(src, dst) then duplicate = true end
                end
            end
            if duplicate then report(log, "Macro already exists: " .. src.name)
            elseif conflict then report(log, "Macro name conflict, kept existing: " .. src.name)
            else
                local created = attempt(log, "Add macro " .. src.name, function() return api.createMacro(src) end)
                if created then backup.macros[#backup.macros + 1] = copy(src); report(log, "Added macro: " .. src.name) end
            end
        end
    end
    if selection.bindings and data.bindings then
        local capture = attempt(log, "Read destination bindings", api.bindings)
        if capture and capture.mode ~= data.bindings.mode then
            report(log, "Bindings skipped: source and destination use different binding context APIs.")
        elseif capture and capture.scope ~= selection.bindingScope then
            report(log, "Bindings skipped: choose the matching account/character binding set in WoW's keybinding settings first.")
        elseif capture then
            backup.bindingScope, backup.bindingMode = capture.scope, capture.mode
            local currentMacros = api.macros() or {}
            for _, src in ipairs(data.bindings.keys) do
                local action = src.action
                local macroRef = action:match("^MACRO (.+)$")
                if macroRef then
                    local sourceMacro
                    for _, macro in ipairs(data.macros) do
                        if (tonumber(macroRef) and macro.index == tonumber(macroRef)) or (not tonumber(macroRef) and macro.name == macroRef) then sourceMacro = macro; break end
                    end
                    local matches = 0
                    for _, macro in ipairs(currentMacros) do
                        if sourceMacro and macroMatches(sourceMacro, macro) then matches = matches + 1 end
                    end
                    local nameCount = 0
                    for _, macro in ipairs(currentMacros) do if sourceMacro and macro.name == sourceMacro.name then nameCount = nameCount + 1 end end
                    if sourceMacro and matches == 1 and nameCount == 1 then action = "MACRO " .. sourceMacro.name
                    else action = nil; report(log, "Skipped macro key " .. src.key .. ": missing, conflicting, or ambiguous macro.") end
                end
                if action then
                    local before = api.getKey(src.key, src.context)
                    if before ~= action then
                        local entry = {key = src.key, context = src.context, before = before or "", after = action}
                        -- Journal before calling a potentially failing API.
                        backup.bindings[#backup.bindings + 1] = entry
                        local applied = attempt(log, "Key " .. src.key, function() return api.setKey(src.key, action, src.context) end)
                        entry.applied = applied and api.getKey(src.key, src.context) == action or false
                    end
                end
            end
            attempt(log, "Save bindings", function() return api.saveBindings(capture.scope) end)
            report(log, "Processed captured keys; unrelated destination keys were kept.")
        end
    end
    if selection.cvars then
        for name, value in pairs(data.cvars) do
            local info = api.cvarInfo(name)
            if info and info.writable then
                backup.cvars[name] = {before = info.value, after = value}
                attempt(log, "Option " .. name, function() return api.setCvar(name, value) end)
            else report(log, "Skipped unavailable or protected option: " .. name) end
        end
    end
    if selection.editmode and data.editmode then
        local layout = attempt(log, "Import Edit Mode", function() return api.addLayout(data.editmode) end)
        if layout then backup.layouts[#backup.layouts + 1] = layout; report(log, "Added inactive Edit Mode layout: " .. layout.name .. ". Select it in Edit Mode when ready.") end
    end
    report(log, "Import finished. Review this report, then exit WoW to persist the capture and restore point.")
    return true, log
end

function Core:Restore()
    local api, backup, log = self.api, self.db.backup, {}
    if api.inCombat() then return false, {"Leave combat before restoring settings."} end
    if not backup or backup.owner ~= owner(api.metadata()) or backup.restored then return false, {"No restore point for this character."} end
    local capture = attempt(log, "Read bindings", api.bindings)
    local pending = false
    if #backup.bindings > 0 and (not capture or capture.scope ~= backup.bindingScope or capture.mode ~= backup.bindingMode) then
        pending = true
        report(log, "Bindings were kept. Select the original binding scope in WoW and restore again.")
    elseif capture then
        for _, entry in ipairs(backup.bindings) do
            if not entry.restored then
                local current = api.getKey(entry.key, entry.context) or ""
                if current == entry.after then
                    local restored = attempt(log, "Restore key " .. entry.key, function() return api.setKey(entry.key, entry.before ~= "" and entry.before or nil, entry.context) end)
                    entry.restored = restored and (api.getKey(entry.key, entry.context) or "") == entry.before or false
                    if not entry.restored then pending = true end
                else entry.restored = true; report(log, "Kept later key change: " .. entry.key) end
            end
        end
        if #backup.bindings > 0 then
            if not attempt(log, "Save restored bindings", function() return api.saveBindings(backup.bindingScope) end) then pending = true end
        end
    end
    for name, entry in pairs(backup.cvars) do
        if not entry.restored then
            local info = api.cvarInfo(name)
            if info and info.writable and info.value == entry.after then
                entry.restored = attempt(log, "Restore option " .. name, function() return api.setCvar(name, entry.before) end) and true or false
                if not entry.restored then pending = true end
            else entry.restored = true; report(log, "Kept later or protected option: " .. name) end
        end
    end
    for _, layout in ipairs(backup.layouts) do
        if not layout.restored then
            layout.restored = attempt(log, "Remove imported layout", function() return api.removeLayout(layout) end) and true or false
            if not layout.restored then report(log, "Layout kept for manual review in Edit Mode.") end
        end
    end
    if #backup.macros > 0 then report(log, "Added macros were kept: they may now be used by action bars. Remove unwanted ones manually.") end
    backup.restored = not pending
    report(log, pending and "Restore incomplete; review the report and retry." or "Restore finished; later changes were preserved.")
    return not pending, log
end

function Core:Forget()
    self.db.backup = nil
end

Core.Copy = copy
Core.Same = same
