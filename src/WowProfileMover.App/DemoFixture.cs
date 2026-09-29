using System.IO;

namespace WowProfileMover.App;

internal static class DemoFixture
{
    public static string Create(string dataRoot)
    {
        var root = Path.Combine(dataRoot, "Demo World of Warcraft");
        foreach (var flavor in new[] { "_retail_", "_classic_", "_classic_era_", "_anniversary_" })
        {
            foreach (var account in new[] { "EXAMPLE#1", "EXAMPLE#2" })
            {
                var path = Path.Combine(root, flavor, "WTF", "Account", account);
                foreach (var addon in new[] { "Bartender4", "Details", "Plater", "WeakAuras", "BigWigs", "OmniCD" })
                    Put(Path.Combine(path, "SavedVariables", addon + ".lua"), $"-- Synthetic UI demonstration data, not a real addon database.\n{addon}Demo = {{ account = \"{account}\" }}\n");
                foreach (var (realm, character) in account.EndsWith('1') ? new[] { ("Silvermoon", "Aeloria"), ("Draenor", "Thorne") } : new[] { ("Silvermoon", "Lunara"), ("Draenor", "Kaelen") })
                {
                    var charPath = Path.Combine(path, realm, character);
                    Put(Path.Combine(charPath, "SavedVariables", "Details.lua"), "DetailsDemo = { theme = \"violet\" }\n");
                    Put(Path.Combine(charPath, "AddOns.txt"), "Details: enabled\n");
                    Put(Path.Combine(charPath, "layout-local.txt"), "-- Synthetic demo only\n");
                }
            }
        }
        return root;
    }
    private static void Put(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, contents);
    }
}
