namespace WowProfileMover.Core;

public sealed class DiscoveryService
{
    internal static readonly IReadOnlyDictionary<string, (WowFlavor Flavor, string Name)> Flavors =
        new Dictionary<string, (WowFlavor, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["_retail_"] = (WowFlavor.Retail, "Retail"),
            ["_classic_"] = (WowFlavor.ClassicProgression, "Classic Progression"),
            ["_classic_era_"] = (WowFlavor.ClassicEra, "Classic Era"),
            ["_anniversary_"] = (WowFlavor.ClassicAnniversary, "Classic Anniversary")
        };

    public IReadOnlyList<WowClient> Discover(string selectedRoot)
    {
        var root = SafeFiles.Full(selectedRoot);
        SafeFiles.CheckPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var roots = Flavors.ContainsKey(Path.GetFileName(root)) ? new[] { root }
            : Flavors.Keys.Select(flavor => Path.Combine(root, flavor)).Where(Directory.Exists);
        return roots.Select(DiscoverClient).ToArray();
    }

    private static WowClient DiscoverClient(string root)
    {
        SafeFiles.CheckPath(root);
        var flavor = Flavors[Path.GetFileName(root)];
        var accountRoot = Path.Combine(root, "WTF", "Account");
        SafeFiles.CheckPath(accountRoot);
        var accounts = new List<WowAccount>();
        if (Directory.Exists(accountRoot))
        foreach (var accountPath in Directory.EnumerateDirectories(accountRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            SafeFiles.CheckPath(accountPath);
            var id = Path.GetFileName(accountPath);
            var characters = new List<WowCharacter>();
            foreach (var realmPath in Directory.EnumerateDirectories(accountPath))
            {
                if (Path.GetFileName(realmPath).Equals("SavedVariables", StringComparison.OrdinalIgnoreCase)) continue;
                SafeFiles.CheckPath(realmPath);
                foreach (var characterPath in Directory.EnumerateDirectories(realmPath))
                {
                    SafeFiles.CheckPath(characterPath);
                    if (!Directory.Exists(Path.Combine(characterPath, "SavedVariables")) && !new[] { "AddOns.txt", "config-cache.wtf", "bindings-cache.wtf", "chat-cache.txt", "layout-local.txt", "macros-cache.txt" }.Any(name => File.Exists(Path.Combine(characterPath, name)))) continue;
                    characters.Add(new WowCharacter(Path.GetFileName(characterPath), Path.GetFileName(realmPath), characterPath, id));
                }
            }
            accounts.Add(new WowAccount(id, accountPath, characters.OrderBy(c => c.Realm).ThenBy(c => c.Name).ToArray()));
        }
        return new WowClient(root, flavor.Flavor, flavor.Name, accounts);
    }

    internal static void ValidateEndpoint(WowEndpoint endpoint, bool characterRequired)
    {
        var root = SafeFiles.Full(endpoint.Client.RootPath);
        SafeFiles.CheckPath(root);
        if (!Flavors.TryGetValue(Path.GetFileName(root), out var flavor) || flavor.Flavor != endpoint.Client.Flavor)
            throw new TransferSafetyException("The client folder does not match its WoW version.");
        SafeFiles.CheckName(endpoint.Account.Id);
        var expected = Path.Combine(root, "WTF", "Account", endpoint.Account.Id);
        if (!SafeFiles.Same(expected, endpoint.Account.Path) || !Directory.Exists(expected))
            throw new TransferSafetyException("The selected account folder is unavailable or invalid. Log in once and then close WoW.");
        SafeFiles.CheckPath(expected);
        if (characterRequired && endpoint.Character is null) throw new TransferSafetyException("Select both characters.");
        if (endpoint.Character is { } character)
        {
            SafeFiles.CheckName(character.Name); SafeFiles.CheckName(character.Realm);
            var expectedCharacter = Path.Combine(expected, character.Realm, character.Name);
            if (character.AccountId != endpoint.Account.Id || !SafeFiles.Same(character.Path, expectedCharacter) || !Directory.Exists(expectedCharacter))
                throw new TransferSafetyException("The selected character folder is unavailable or invalid. Log in once and then close WoW.");
            SafeFiles.CheckPath(expectedCharacter);
        }
    }
}
