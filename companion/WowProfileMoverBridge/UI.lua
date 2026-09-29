local addonName, NS = ...
local app, panel
local function say(text)
    DEFAULT_CHAT_FRAME:AddMessage("|cff64c5ffWoW Profile Mover:|r " .. tostring(text))
end
local function output(lines)
    for _, text in ipairs(lines or {}) do say(text) end
    if panel then
        panel.report:SetText(table.concat(lines or {}, "\n\n"))
        panel.report:SetHeight(math.max(170, panel.report:GetStringHeight() + 20))
        panel.content:SetHeight(math.max(180, panel.report:GetStringHeight() + 20))
        panel.scroll:SetVerticalScroll(0)
    end
end
local function run(method, selection)
    local ok, success, lines = pcall(method, app, selection)
    if not ok then output({"An API rejected the operation. Any existing restore point was kept. Review settings before retrying."}); return end
    output(lines)
    return success
end
local function createPanel()
    if panel then return panel end
    local frame = CreateFrame("Frame", "WowProfileMoverBridgePanel", UIParent, "BackdropTemplate")
    panel = frame
    frame:SetSize(650, 570)
    frame:SetPoint("CENTER")
    frame:SetFrameStrata("DIALOG")
    frame:SetBackdrop({bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background", edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Border", tile = true, tileSize = 32, edgeSize = 32, insets = {left = 8, right = 8, top = 8, bottom = 8}})
    frame:SetMovable(true)
    frame:EnableMouse(true)
    frame:RegisterForDrag("LeftButton")
    frame:SetScript("OnDragStart", function(self) self:StartMoving() end)
    frame:SetScript("OnDragStop", function(self) self:StopMovingOrSizing() end)
    local close = CreateFrame("Button", nil, frame, "UIPanelCloseButton")
    close:SetPoint("TOPRIGHT", -6, -6)
    local title = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
    title:SetPoint("TOPLEFT", 24, -24)
    title:SetText("WoW Profile Mover — review import")
    local help = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    help:SetPoint("TOPLEFT", 24, -54)
    help:SetWidth(595)
    help:SetJustifyH("LEFT")
    help:SetText("Choose what to apply. Nothing changes until Apply is clicked. Leave combat first.\nA capture does not contain action-bar spells. Macro bodies are never executed here.")
    frame.checks = {}
    local function checkbox(id, label, y)
        local box = CreateFrame("CheckButton", nil, frame, "UICheckButtonTemplate")
        box:SetPoint("TOPLEFT", 24, y)
        box:SetSize(26, 26)
        local text = box:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
        text:SetPoint("LEFT", box, "RIGHT", 4, 0)
        text:SetText(label)
        frame.checks[id] = box
        return box
    end
    checkbox("bindings", "Apply captured keys; keep other destination keys", -94)
    checkbox("macros", "Add missing macros; keep duplicates and conflicting names", -126)
    checkbox("cvars", "Apply reviewed game options (some affect all account characters)", -158)
    checkbox("editmode", "Add a new inactive character Edit Mode layout, when available", -190)
    local character = checkbox("character", "Character bindings", -224)
    local account = CreateFrame("CheckButton", nil, frame, "UICheckButtonTemplate")
    account:SetPoint("TOPLEFT", 255, -224)
    account:SetSize(26, 26)
    local accountText = account:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    accountText:SetPoint("LEFT", account, "RIGHT", 4, 0)
    accountText:SetText("Account bindings (all characters)")
    frame.checks.account = account
    character:SetScript("OnClick", function() character:SetChecked(true); account:SetChecked(false) end)
    account:SetScript("OnClick", function() account:SetChecked(true); character:SetChecked(false) end)
    local note = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    note:SetPoint("TOPLEFT", 28, -258)
    note:SetWidth(580)
    note:SetJustifyH("LEFT")
    note:SetText("Your chosen binding scope must already be active in WoW's keybinding settings.\nRestore reverses eligible keys/options. Added macros remain for manual review.")
    local scroll = CreateFrame("ScrollFrame", nil, frame, "UIPanelScrollFrameTemplate")
    scroll:SetPoint("TOPLEFT", 28, -300)
    scroll:SetPoint("BOTTOMRIGHT", -46, 66)
    local content = CreateFrame("Frame", nil, scroll)
    content:SetSize(560, 180)
    local report = content:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    report:SetPoint("TOPLEFT")
    report:SetWidth(552)
    report:SetJustifyH("LEFT")
    report:SetJustifyV("TOP")
    frame.report, frame.scroll = report, scroll
    frame.content = content
    scroll:SetScrollChild(content)
    local function button(text, x, handler)
        local b = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
        b:SetSize(180, 28)
        b:SetPoint("BOTTOMLEFT", x, 24)
        b:SetText(text)
        b:SetScript("OnClick", handler)
        return b
    end
    frame.apply = button("Apply selected settings", 28, function()
        local selected = {}
        for _, id in ipairs({"bindings", "macros", "cvars", "editmode"}) do selected[id] = frame.checks[id]:GetChecked() end
        if not (selected.bindings or selected.macros or selected.cvars or selected.editmode) then output({"Select at least one category first."}); return end
        selected.bindingScope = frame.checks.account:GetChecked() and 1 or 2
        run(app.Apply, selected)
    end)
    button("Restore previous settings", 220, function() run(app.Restore) end)
    button("Capture this character", 412, function() run(app.Export) end)
    UISpecialFrames[#UISpecialFrames + 1] = "WowProfileMoverBridgePanel"
    frame:Hide()
    return frame
end

local function show()
    local frame = createPanel()
    local ok, lines = app:Preview()
    local data = app.db.export or {}
    for _, id in ipairs({"bindings", "macros", "cvars", "editmode"}) do
        frame.checks[id]:SetChecked(false)
        frame.checks[id]:SetEnabled(ok and data[id] ~= nil)
    end
    local scope = GetCurrentBindingSet and GetCurrentBindingSet() or 2
    frame.checks.character:SetChecked(scope == 2)
    frame.checks.account:SetChecked(scope == 1)
    frame.apply:SetEnabled(ok)
    frame:Show()
    output(lines)
end

local events = CreateFrame("Frame")
events:RegisterEvent("ADDON_LOADED")
events:SetScript("OnEvent", function(self, _, name)
    if name ~= addonName then return end
    if type(WowProfileMoverBridgeDB) ~= "table" then WowProfileMoverBridgeDB = {} end
    app = NS.Core.New(NS.API, WowProfileMoverBridgeDB)
    SLASH_WOWPROFILEMOVERBRIDGE1 = "/wpm"
    SlashCmdList.WOWPROFILEMOVERBRIDGE = function(command)
        command = (command or ""):lower():match("^%s*(.-)%s*$")
        if command == "export" then run(app.Export)
        elseif command == "import" or command == "" then show()
        elseif command == "restore" then run(app.Restore)
        elseif command == "forget confirm" then app:Forget(); say("Restore point discarded; current settings kept.")
        elseif command == "forget" then say("To keep current changes and discard the restore point, type /wpm forget confirm.")
        else say("/wpm export — capture; /wpm import — review; /wpm restore — restore; /wpm forget — discard restore point.") end
    end
    self:UnregisterEvent("ADDON_LOADED")
end)
