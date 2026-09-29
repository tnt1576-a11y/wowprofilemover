namespace WowProfileMover.Core;

public sealed class TransferPlanner
{
    public TransferPlan CreatePlan(TransferRequest request)
    {
        DiscoveryService.ValidateEndpoint(request.Source, request.Scope == TransferScope.Character);
        DiscoveryService.ValidateEndpoint(request.Destination, request.Scope == TransferScope.Character);
        if (request.Source.Client.Flavor != request.Destination.Client.Flavor)
            throw new TransferSafetyException("Transfers between different WoW versions are not supported.");
        var sourceRoot = request.Scope == TransferScope.Account ? request.Source.Account.Path : request.Source.Character!.Path;
        var destinationRoot = request.Scope == TransferScope.Account ? request.Destination.Account.Path : request.Destination.Character!.Path;
        if (SafeFiles.Same(sourceRoot, destinationRoot) || SafeFiles.Within(sourceRoot, destinationRoot) || SafeFiles.Within(destinationRoot, sourceRoot))
            throw new TransferSafetyException("Select separate source and destination folders.");
        var entries = new List<TransferEntry>();
        var warnings = new List<string>();
        var categories = request.Categories.ToHashSet();
        var addons = request.SelectedAddonNames is null ? null : new HashSet<string>(request.SelectedAddonNames, StringComparer.OrdinalIgnoreCase);
        if (addons is not null) foreach (var name in addons) SafeFiles.CheckName(name);

        void Add(string source, string destination, SettingCategory category, string? addon = null)
        {
            if (!File.Exists(source)) return;
            SafeFiles.CheckPath(source, true); SafeFiles.CheckPath(destination, true);
            if (Directory.Exists(destination)) throw new TransferSafetyException($"A directory occupies a destination file: {destination}");
            var sourceHash = SafeFiles.Hash(source);
            var destinationHash = SafeFiles.HashOrNull(destination);
            entries.Add(new TransferEntry(source, destination, Path.GetRelativePath(request.Destination.Account.Path, destination), category, addon,
                destinationHash is null ? TransferChange.New : sourceHash == destinationHash ? TransferChange.Identical : TransferChange.Replace,
                new FileInfo(source).Length, sourceHash, destinationHash));
        }
        void AddAddons(string from, string to, bool accountWide)
        {
            var directory = Path.Combine(from, "SavedVariables");
            SafeFiles.CheckPath(directory);
            if (!Directory.Exists(directory)) return;
            var files = Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p).Equals(".lua", StringComparison.OrdinalIgnoreCase))
                .Where(p => !Path.GetFileName(p).Equals("WowProfileMoverBridge.lua", StringComparison.OrdinalIgnoreCase))
                .Where(p => addons is null || addons.Contains(Path.GetFileNameWithoutExtension(p))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length > 0 && accountWide && !request.AcknowledgeAccountWideData)
                throw new TransferSafetyException("Account addon data affects all destination characters. Acknowledge this before continuing.");
            foreach (var file in files) Add(file, Path.Combine(to, "SavedVariables", Path.GetFileName(file)), SettingCategory.AddonData, Path.GetFileNameWithoutExtension(file));
            if (files.Length > 0 && accountWide) warnings.Add("Account addon files replace the entire selected addon database, including profiles and data used by other destination characters. Profile selection may still be required in the addon.");
        }

        if (categories.Contains(SettingCategory.AddonData))
        {
            if (request.Scope == TransferScope.Character)
                warnings.Add("Some addon profiles are stored at account level. Character-only files may not include them; profile selection may still be required in-game.");
            AddAddons(sourceRoot, destinationRoot, request.Scope == TransferScope.Account);
            if (request.Scope == TransferScope.Character && request.IncludeAccountAddonData)
            {
                if (!SafeFiles.Same(request.Source.Account.Path, request.Destination.Account.Path)) AddAddons(request.Source.Account.Path, request.Destination.Account.Path, true);
                else warnings.Add("Both characters already share this account's addon files; account files are unchanged.");
            }
        }
        var filenames = new Dictionary<SettingCategory, string>
        {
            [SettingCategory.AddonEnablement] = "AddOns.txt", [SettingCategory.ChatLayout] = "chat-cache.txt",
            [SettingCategory.LocalLayout] = "layout-local.txt", [SettingCategory.Keybindings] = "bindings-cache.wtf",
            [SettingCategory.Macros] = "macros-cache.txt", [SettingCategory.GameOptions] = "config-cache.wtf",
            [SettingCategory.EditMode] = request.Scope == TransferScope.Account ? "edit-mode-cache-account.txt" : "edit-mode-cache-character.txt"
        };
        foreach (var pair in filenames.Where(p => categories.Contains(p.Key)))
        {
            var source = Path.Combine(sourceRoot, pair.Value);
            if (!File.Exists(source)) warnings.Add($"{pair.Key}: {pair.Value} is not present in the selected source folder; it will be skipped.");
            Add(source, Path.Combine(destinationRoot, pair.Value), pair.Key);
        }
        if (categories.Overlaps([SettingCategory.ChatLayout, SettingCategory.Keybindings, SettingCategory.Macros, SettingCategory.GameOptions, SettingCategory.EditMode]))
            warnings.Add("These local cache files may be replaced by server-synchronized settings at login. Use the companion's explicit in-game import for supported server-backed settings; local file restore cannot undo server changes.");
        if (categories.Contains(SettingCategory.CompanionSnapshot))
        {
            if (request.Scope != TransferScope.Character) throw new TransferSafetyException("Companion snapshots require character-to-character mode.");
            var snapshot = Path.Combine(sourceRoot, "SavedVariables", "WowProfileMoverBridge.lua");
            if (!File.Exists(snapshot) || new FileInfo(snapshot).Length == 0) throw new TransferSafetyException("Capture a companion snapshot on the source character, then log out and close WoW first.");
            SafeFiles.CheckPath(snapshot, true);
            if (new FileInfo(snapshot).Length > 16 * 1024 * 1024) throw new TransferSafetyException("The companion snapshot is unexpectedly large.");
            var snapshotText = File.ReadAllText(snapshot);
            if (!snapshotText.Contains("WowProfileMoverBridgeDB", StringComparison.Ordinal)
                || !System.Text.RegularExpressions.Regex.IsMatch(snapshotText, @"\[""schema""\]\s*=\s*1\s*[,}]", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                || !System.Text.RegularExpressions.Regex.IsMatch(snapshotText, @"\[""export""\]\s*=\s*\{", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                || !System.Text.RegularExpressions.Regex.IsMatch(snapshotText, @"\[""metadata""\]\s*=\s*\{", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new TransferSafetyException("This companion file does not contain a supported captured export. Capture again in-game and then close WoW.");
            Add(snapshot, Path.Combine(destinationRoot, "SavedVariables", "WowProfileMoverBridge.lua"), SettingCategory.CompanionSnapshot);
            warnings.Add("The companion snapshot requires explicit in-game review and import on the destination. Copying it alone does not change server settings.");
        }
        if (entries.Count == 0) warnings.Add("No source files match the selected categories and addons. Log in on the source character first if its settings are missing.");
        return new TransferPlan { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTimeOffset.UtcNow,
            AccountDataAcknowledged = request.AcknowledgeAccountWideData, IncludesAccountAddonData = request.IncludeAccountAddonData,
            Source = request.Source, Destination = request.Destination, Scope = request.Scope,
            Entries = entries.OrderBy(e => e.DestinationPath, StringComparer.OrdinalIgnoreCase).ToArray(), Warnings = warnings.Distinct().ToArray() };
    }
}
