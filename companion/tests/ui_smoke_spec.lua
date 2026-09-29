local NS, frames, messages, writes = {}, {}, {}, 0
assert(loadfile("companion/WowProfileMoverBridge/Core.lua"))("WowProfileMoverBridge", NS)
UIParent = {}
UISpecialFrames = {}
SlashCmdList = {}
DEFAULT_CHAT_FRAME = {AddMessage = function(_, message) messages[#messages + 1] = message end}
local methods = {}
for _, method in ipairs({"SetSize", "SetPoint", "SetFrameStrata", "SetBackdrop", "SetMovable", "EnableMouse", "RegisterForDrag", "StartMoving", "StopMovingOrSizing", "SetWidth", "SetHeight", "SetJustifyH", "SetJustifyV", "SetVerticalScroll", "SetScrollChild", "RegisterEvent", "UnregisterEvent"}) do methods[method] = function() end end
function methods:SetScript(event, fn) self.scripts[event] = fn end
function methods:GetScript(event) return self.scripts[event] end
function methods:SetText(text) self.text = text end
function methods:GetStringHeight() return #(self.text or "") / 5 end
function methods:SetChecked(checked) self.checked = checked end
function methods:GetChecked() return self.checked end
function methods:SetEnabled(enabled) self.enabled = enabled end
function methods:Hide() self.shown = false end
function methods:Show() self.shown = true end
function methods:CreateFontString() return setmetatable({scripts = {}}, {__index = methods}) end
function CreateFrame(_, name)
    local frame = setmetatable({scripts = {}}, {__index = methods})
    frames[#frames + 1] = frame
    if name then _G[name] = frame end
    return frame
end
function GetCurrentBindingSet() return 2 end
local metadata = {project = 1, family = 12, season = 0, regional = false, name = "Target", realm = "Realm", guid = "target-guid"}
NS.API = {
    metadata = function() return NS.Core.Copy(metadata) end,
    now = function() return 100 end,
    inCombat = function() return false end,
    cvarInfo = function() return {value = "0", writable = true} end,
    setCvar = function() writes = writes + 1; return true end,
    bindings = function() return {mode = "legacy", scope = 2, keys = {}} end,
    macros = function() return {} end,
}
WowProfileMoverBridgeDB = {schema = 1, export = {metadata = NS.Core.Copy(metadata), macros = {}, cvars = {autoLootDefault = "1"}}}
local before = NS.Core.Copy(WowProfileMoverBridgeDB)
assert(loadfile("companion/WowProfileMoverBridge/UI.lua"))("WowProfileMoverBridge", NS)
frames[1]:GetScript("OnEvent")(frames[1], "ADDON_LOADED", "WowProfileMoverBridge")
assert(NS.Core.Same(before, WowProfileMoverBridgeDB), "login changed capture")
assert(writes == 0, "login applied settings")
SlashCmdList.WOWPROFILEMOVERBRIDGE("import")
local panel = WowProfileMoverBridgePanel
assert(panel.shown and panel.apply.enabled, "preview not open")
assert(not panel.checks.cvars:GetChecked(), "category selected without user action")
assert(writes == 0, "preview applied settings")
panel.apply:GetScript("OnClick")()
assert(writes == 0, "empty selection applied settings")
panel.checks.cvars:SetChecked(true)
panel.apply:GetScript("OnClick")()
assert(writes == 1, "explicit Apply did not call option setter")
assert(WowProfileMoverBridgeDB.backup, "missing restore point")
SlashCmdList.WOWPROFILEMOVERBRIDGE("forget")
assert(WowProfileMoverBridgeDB.backup, "forget discarded restore point without explicit confirmation command")
SlashCmdList.WOWPROFILEMOVERBRIDGE("forget confirm")
assert(not WowProfileMoverBridgeDB.backup)
print("UI smoke passed: login preservation, preview, empty selection, explicit Apply, restore point, discard confirmation")
