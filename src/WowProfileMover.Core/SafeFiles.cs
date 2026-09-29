using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WowProfileMover.Core;

internal static class SafeFiles
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Full(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public static bool Same(string left, string right) => string.Equals(PhysicalFull(left), PhysicalFull(right), Comparison);
    public static bool Within(string path, string root) => PhysicalFull(path).StartsWith(PhysicalFull(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);
    // Resolve an existing ancestor through a handle so short names and SUBST drives cannot evade overlap checks.
    private static string PhysicalFull(string path)
    {
        var full = Full(path);
        if (!OperatingSystem.IsWindows()) return full;
        var missing = new Stack<string>();
        var ancestor = full;
        while (!File.Exists(ancestor) && !Directory.Exists(ancestor))
        {
            missing.Push(Path.GetFileName(ancestor));
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new TransferSafetyException("The path has no accessible parent.");
        }
        using var handle = CreateFileW(ancestor, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Could not resolve the physical path: {ancestor}");
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException($"Could not resolve the physical path: {ancestor}");
        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        while (missing.Count > 0) resolved = Path.Combine(resolved, missing.Pop());
        return Path.TrimEndingDirectorySeparator(resolved);
    }
    public static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name != System.IO.Path.GetFileName(name))
            throw new TransferSafetyException("An account, realm, character, or addon name is invalid.");
    }

    public static void CheckPath(string path, bool rejectHardLinks = false)
    {
        var full = Full(path);
        for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new TransferSafetyException($"Linked or cloud-placeholder paths are not supported: {current}");
        }
        if (rejectHardLinks && File.Exists(full) && OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Could not inspect file identity.");
            if (info.NumberOfLinks > 1) throw new TransferSafetyException($"Hard-linked destination files are not supported: {full}");
        }
    }

    public static string Hash(string path)
    {
        CheckPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public static string? HashOrNull(string path) => File.Exists(path) ? Hash(path) : null;
    public static void ExpectHash(string path, string? expected)
    {
        CheckPath(path, true);
        if (Directory.Exists(path)) throw new TransferSafetyException($"A directory occupies the planned file path: {path}");
        if (!string.Equals(HashOrNull(path), expected, StringComparison.Ordinal))
            throw new TransferSafetyException($"The file changed since the preview. Refresh the preview: {path}");
    }
    public static void CopyVerified(string source, string destination, string expected)
    {
        CheckPath(source);
        CheckPath(destination, true);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        if (Hash(destination) != expected) throw new TransferSafetyException($"File verification failed: {source}");
    }
    public static void WriteDurable(string path, string content)
    {
        CheckPath(path, true);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Move(temp, path, true); else File.Move(temp, path);
    }
    public static void Install(string staged, string destination, string? expectedCurrent)
    {
        CheckPath(destination, true);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, ".wpm-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyVerified(staged, temporary, Hash(staged));
            ExpectHash(destination, expectedCurrent);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
