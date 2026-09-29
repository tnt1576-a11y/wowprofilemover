using System.IO;
using System.Security.Cryptography;
using WowProfileMover.Core;

namespace WowProfileMover.App;

internal static class CompanionInstaller
{
    public static void Install(string source, string clientRoot, IGameProcessGuard? processGuard = null)
    {
        var guard = processGuard ?? new WindowsGameProcessGuard(); guard.EnsureGameIsClosed();
        var sourceFull = Path.GetFullPath(source);
        if (!Directory.Exists(sourceFull)) throw new IOException("The bundled companion folder is missing. Extract the entire release ZIP first.");
        RejectLinks(sourceFull);
        var files = Directory.GetFiles(sourceFull, "*", SearchOption.TopDirectoryOnly)
            .Where(p => new[] { ".lua", ".toc" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (!files.Any(p => Path.GetExtension(p).Equals(".toc", StringComparison.OrdinalIgnoreCase))) throw new IOException("The companion package is incomplete.");
        foreach (var file in files) RejectLinks(file);
        var addonRoot = Path.Combine(Path.GetFullPath(clientRoot), "Interface", "AddOns");
        var destination = Path.Combine(addonRoot, "WowProfileMoverBridge");
        RejectLinks(destination);
        if (Directory.Exists(destination))
        {
            var existing = Directory.GetFiles(destination, "*", SearchOption.TopDirectoryOnly);
            if (Directory.GetDirectories(destination).Length > 0 || existing.Length != files.Length || files.Any(p => !SameBytes(p, Path.Combine(destination, Path.GetFileName(p)))))
                throw new IOException("A different companion version already exists. Its files were preserved. Move that addon folder aside manually before installing this version.");
            return;
        }
        Directory.CreateDirectory(addonRoot);
        var stage = Path.Combine(addonRoot, ".wpm-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(stage, Path.GetFileName(file));
                File.Copy(file, target, false);
                if (!SameBytes(file, target)) throw new IOException("Companion file verification failed.");
            }
            guard.EnsureGameIsClosed(); RejectLinks(destination);
            Directory.Move(stage, destination);
        }
        finally
        {
            // This directory was created by this operation under the validated AddOns root.
            if (Directory.Exists(stage))
            {
                RejectLinks(stage);
                foreach (var file in Directory.GetFiles(stage)) { RejectLinks(file); File.Delete(file); }
                Directory.Delete(stage, false);
            }
        }
    }
    private static bool SameBytes(string a, string b)
    {
        if (!File.Exists(b)) return false;
        RejectLinks(a); RejectLinks(b);
        using var first = File.OpenRead(a); using var second = File.OpenRead(b);
        return SHA256.HashData(first).SequenceEqual(SHA256.HashData(second));
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked addon paths are not supported: " + current); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
