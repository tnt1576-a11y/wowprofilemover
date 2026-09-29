using System.Diagnostics;
using System.Text.Json;

namespace WowProfileMover.Core;

public sealed class WindowsGameProcessGuard : IGameProcessGuard
{
    public void EnsureGameIsClosed()
    {
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            string name;
            try { name = process.ProcessName; }
            catch (InvalidOperationException) { continue; }
            if (name.Equals("Wow", StringComparison.OrdinalIgnoreCase) || name.Equals("WowB", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WowT", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WowClassic", StringComparison.OrdinalIgnoreCase))
                throw new TransferSafetyException("Close every World of Warcraft client before copying or restoring settings.");
        }
    }
}

public sealed class TransferEngine
{
    private readonly IGameProcessGuard guard;
    private readonly ITransferObserver? observer;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public TransferEngine(IGameProcessGuard? guard = null, ITransferObserver? observer = null)
    { this.guard = guard ?? new WindowsGameProcessGuard(); this.observer = observer; }

    public TransferReceipt Execute(TransferPlan plan, string backupRoot, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            using var globalLock = AcquireGlobalLock();
            guard.EnsureGameIsClosed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlan(plan);
            var root = ValidateBackupRoot(backupRoot, plan.Source.Client.RootPath, plan.Destination.Client.RootPath);
            Directory.CreateDirectory(root);
            using var operationLock = AcquireLock(root);
            foreach (var entry in plan.Entries)
            {
                SafeFiles.ExpectHash(entry.SourcePath, entry.SourceSha256);
                SafeFiles.ExpectHash(entry.DestinationPath, entry.DestinationSha256);
            }
            var entries = plan.Entries.Where(e => e.Change != TransferChange.Identical).ToArray();
            if (entries.Length == 0) throw new TransferSafetyException("There are no changed files to copy.");
            CheckSpace(root, entries.Sum(e => e.Length + (File.Exists(e.DestinationPath) ? new FileInfo(e.DestinationPath).Length : 0)));
            CheckSpace(plan.Destination.Account.Path, entries.Max(e => e.Length));
            var manifest = new OperationManifest
            {
                Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTimeOffset.UtcNow,
                DestinationClientRoot = SafeFiles.Full(plan.Destination.Client.RootPath), DestinationAccountPath = SafeFiles.Full(plan.Destination.Account.Path),
                Files = entries.Select((entry, index) => new OperationFile
                {
                    DestinationPath = SafeFiles.Full(entry.DestinationPath), RelativePath = entry.RelativePath,
                    OriginalSha256 = entry.DestinationSha256, AppliedSha256 = entry.SourceSha256,
                    BackupRelativePath = entry.DestinationSha256 is null ? null : $"before/{index:D5}.bin", StageRelativePath = $"stage/{index:D5}.bin"
                }).ToList()
            };
            var directory = Path.Combine(root, manifest.Id);
            Directory.CreateDirectory(directory);
            Save(directory, manifest);
            try
            {
                for (var i = 0; i < entries.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); guard.EnsureGameIsClosed();
                    var file = manifest.Files[i];
                    SafeFiles.CopyVerified(entries[i].SourcePath, VaultPath(directory, file.StageRelativePath!), file.AppliedSha256!);
                    SafeFiles.ExpectHash(entries[i].SourcePath, file.AppliedSha256);
                    SafeFiles.ExpectHash(file.DestinationPath, file.OriginalSha256);
                    if (file.BackupRelativePath is not null)
                        SafeFiles.CopyVerified(file.DestinationPath, VaultPath(directory, file.BackupRelativePath), file.OriginalSha256!);
                }
                manifest.State = "Ready"; Save(directory, manifest);
                observer?.OnCheckpoint("backup-complete", -1);
                // All backups are complete before the first mutation; stale previews fail together.
                foreach (var entry in plan.Entries)
                {
                    SafeFiles.ExpectHash(entry.SourcePath, entry.SourceSha256);
                    SafeFiles.ExpectHash(entry.DestinationPath, entry.DestinationSha256);
                }
                Apply(directory, manifest, cancellationToken);
                return new TransferReceipt(manifest.Id, directory, entries.Length, DateTimeOffset.UtcNow);
            }
            catch (Exception error)
            {
                TryRollback(directory, manifest);
                if (manifest.State == "RecoveryRequired") throw new TransferSafetyException($"{error.Message} Some files need recovery. Close WoW and use backup history to restore operation {manifest.Id}. Backup: {directory}");
                throw;
            }
        }
    }

    public RestorePlan CreateRestorePlan(string operationDirectory)
    {
        var directory = SafeFiles.Full(operationDirectory);
        var manifest = Load(directory);
        if (manifest.State is "Preparing" or "Aborted" or "RolledBack")
            throw new TransferSafetyException("This operation did not leave an applied transfer to restore.");
        var entries = new List<RestoreEntry>();
        foreach (var file in manifest.Files.Where(f => f.State is "Intent" or "Applied"))
        {
            SafeFiles.CheckPath(file.DestinationPath, true);
            var backup = file.BackupRelativePath is null ? null : VaultPath(directory, file.BackupRelativePath);
            if (backup is not null && SafeFiles.HashOrNull(backup) != file.OriginalSha256)
                throw new TransferSafetyException($"The backup is missing or damaged: {file.RelativePath}");
            var current = SafeFiles.HashOrNull(file.DestinationPath);
            entries.Add(new RestoreEntry(file.DestinationPath, file.RelativePath, backup, file.OriginalSha256,
                current, file.AppliedSha256, current != file.AppliedSha256 && current != file.OriginalSha256));
        }
        var warnings = new List<string> { "Restore affects local files only. Settings already imported or synchronized in-game are not reversed." };
        if (entries.Any(e => e.HasNewerChanges)) warnings.Add("Some files changed after this transfer. Restoring will replace those changes; a fresh backup will be created first.");
        return new RestorePlan(directory, manifest.DestinationClientRoot, entries, warnings);
    }

    public TransferReceipt Restore(RestorePlan plan, string backupRoot, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            using var globalLock = AcquireGlobalLock();
            guard.EnsureGameIsClosed(); cancellationToken.ThrowIfCancellationRequested();
            var original = Load(plan.OperationDirectory);
            var fresh = CreateRestorePlan(plan.OperationDirectory);
            if (!SafeFiles.Same(plan.DestinationClientRoot, fresh.DestinationClientRoot) || plan.Entries.Count != fresh.Entries.Count)
                throw new TransferSafetyException("The restore preview is no longer valid.");
            for (var i = 0; i < plan.Entries.Count; i++)
                if (plan.Entries[i] != fresh.Entries[i]) throw new TransferSafetyException("Files changed after the restore preview. Refresh the preview.");
            var root = ValidateBackupRoot(backupRoot, original.DestinationClientRoot);
            Directory.CreateDirectory(root);
            using var operationLock = AcquireLock(root);
            var changes = fresh.Entries.Where(e => e.CurrentSha256 != e.OriginalSha256).ToArray();
            if (changes.Length == 0) throw new TransferSafetyException("The destination already matches this backup.");
            CheckSpace(root, changes.Sum(e => (File.Exists(e.DestinationPath) ? new FileInfo(e.DestinationPath).Length : 0) + (e.BackupPath is null ? 0 : new FileInfo(e.BackupPath).Length)));
            var manifest = new OperationManifest
            {
                Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTimeOffset.UtcNow, DestinationClientRoot = original.DestinationClientRoot,
                DestinationAccountPath = original.DestinationAccountPath, RestoresOperation = original.Id,
                Files = changes.Select((entry, i) => new OperationFile
                {
                    DestinationPath = entry.DestinationPath, RelativePath = entry.RelativePath,
                    OriginalSha256 = entry.CurrentSha256, AppliedSha256 = entry.OriginalSha256,
                    BackupRelativePath = entry.CurrentSha256 is null ? null : $"before/{i:D5}.bin",
                    StageRelativePath = entry.OriginalSha256 is null ? null : $"stage/{i:D5}.bin"
                }).ToList()
            };
            var directory = Path.Combine(root, manifest.Id);
            Directory.CreateDirectory(directory); Save(directory, manifest);
            try
            {
                for (var i = 0; i < changes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); guard.EnsureGameIsClosed();
                    var file = manifest.Files[i]; var change = changes[i];
                    SafeFiles.ExpectHash(file.DestinationPath, file.OriginalSha256);
                    if (file.BackupRelativePath is not null) SafeFiles.CopyVerified(file.DestinationPath, VaultPath(directory, file.BackupRelativePath), file.OriginalSha256!);
                    if (file.StageRelativePath is not null) SafeFiles.CopyVerified(change.BackupPath!, VaultPath(directory, file.StageRelativePath), file.AppliedSha256!);
                }
                manifest.State = "Ready"; Save(directory, manifest);
                observer?.OnCheckpoint("backup-complete", -1);
                foreach (var entry in fresh.Entries) SafeFiles.ExpectHash(entry.DestinationPath, entry.CurrentSha256);
                Apply(directory, manifest, cancellationToken);
                original.State = "Restored";
                Save(plan.OperationDirectory, original);
                return new TransferReceipt(manifest.Id, directory, changes.Length, DateTimeOffset.UtcNow);
            }
            catch (Exception error)
            {
                TryRollback(directory, manifest);
                if (manifest.State == "RecoveryRequired") throw new TransferSafetyException($"{error.Message} Some files need recovery. Close WoW and use backup history to restore operation {manifest.Id}. Backup: {directory}");
                throw;
            }
        }
    }

    public IReadOnlyList<RecoveryOperation> ListOperations(string backupRoot)
    {
        var root = SafeFiles.Full(backupRoot); SafeFiles.CheckPath(root);
        if (!Directory.Exists(root)) return Array.Empty<RecoveryOperation>();
        var operations = new List<RecoveryOperation>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(directory, "manifest.json"))) continue;
            try { var manifest = Load(directory); operations.Add(new RecoveryOperation(manifest.Id, directory, manifest.State, manifest.CreatedUtc, manifest.DestinationClientRoot, manifest.DestinationAccountPath)); }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            { operations.Add(new RecoveryOperation(Path.GetFileName(directory), directory, "Unreadable", DateTimeOffset.MinValue)); }
        }
        return operations.OrderByDescending(o => o.CreatedUtc).ToArray();
    }
    public IReadOnlyList<RecoveryOperation> ListRecoverableOperations(string backupRoot) =>
        ListOperations(backupRoot).Where(o => o.State is not ("Completed" or "RolledBack" or "Aborted" or "Restored")).ToArray();

    private void Apply(string directory, OperationManifest manifest, CancellationToken cancellationToken)
    {
        manifest.State = "Applying"; Save(directory, manifest);
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); guard.EnsureGameIsClosed();
            var file = manifest.Files[i];
            observer?.OnCheckpoint("before-apply", i);
            SafeFiles.ExpectHash(file.DestinationPath, file.OriginalSha256);
            if (file.StageRelativePath is not null && SafeFiles.Hash(VaultPath(directory, file.StageRelativePath)) != file.AppliedSha256)
                throw new TransferSafetyException("The staged file changed before application.");
            file.State = "Intent"; Save(directory, manifest);
            if (file.AppliedSha256 is null) File.Delete(file.DestinationPath);
            else SafeFiles.Install(VaultPath(directory, file.StageRelativePath!), file.DestinationPath, file.OriginalSha256);
            SafeFiles.ExpectHash(file.DestinationPath, file.AppliedSha256);
            file.State = "Applied"; Save(directory, manifest);
            observer?.OnCheckpoint("after-apply", i);
        }
        guard.EnsureGameIsClosed();
        foreach (var file in manifest.Files) SafeFiles.ExpectHash(file.DestinationPath, file.AppliedSha256);
        manifest.State = "Completed"; Save(directory, manifest);
        observer?.OnCheckpoint("completed", -1);
    }

    private void TryRollback(string directory, OperationManifest manifest)
    {
        var affected = manifest.Files.Where(f => f.State is "Intent" or "Applied").Reverse().ToArray();
        if (affected.Length == 0) { manifest.State = "Aborted"; TrySave(directory, manifest); return; }
        manifest.State = "RollingBack"; TrySave(directory, manifest);
        var conflict = false;
        foreach (var file in affected)
        {
            try
            {
                guard.EnsureGameIsClosed(); SafeFiles.CheckPath(file.DestinationPath, true);
                var current = SafeFiles.HashOrNull(file.DestinationPath);
                if (current == file.OriginalSha256) { file.State = "RolledBack"; TrySave(directory, manifest); continue; }
                if (current != file.AppliedSha256) { conflict = true; continue; }
                if (file.OriginalSha256 is null) { SafeFiles.ExpectHash(file.DestinationPath, file.AppliedSha256); File.Delete(file.DestinationPath); }
                else
                {
                    var backup = VaultPath(directory, file.BackupRelativePath!);
                    if (SafeFiles.Hash(backup) != file.OriginalSha256) throw new TransferSafetyException("Rollback backup is damaged.");
                    SafeFiles.Install(backup, file.DestinationPath, file.AppliedSha256);
                }
                SafeFiles.ExpectHash(file.DestinationPath, file.OriginalSha256);
                file.State = "RolledBack"; TrySave(directory, manifest);
            }
            catch { conflict = true; }
        }
        manifest.State = conflict ? "RecoveryRequired" : "RolledBack"; TrySave(directory, manifest);
    }

    private static void ValidatePlan(TransferPlan plan)
    {
        DiscoveryService.ValidateEndpoint(plan.Source, plan.Scope == TransferScope.Character);
        DiscoveryService.ValidateEndpoint(plan.Destination, plan.Scope == TransferScope.Character);
        if (plan.Source.Client.Flavor != plan.Destination.Client.Flavor) throw new TransferSafetyException("Cross-version transfers are unsupported.");
        var destinations = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in plan.Entries)
        {
            if (!SafeFiles.Within(entry.SourcePath, plan.Source.Account.Path) || !SafeFiles.Within(entry.DestinationPath, plan.Destination.Account.Path)
                || SafeFiles.Same(entry.SourcePath, entry.DestinationPath) || !destinations.Add(SafeFiles.Full(entry.DestinationPath)))
                throw new TransferSafetyException("The transfer plan contains an invalid or repeated path.");
            var sourceDirectory = plan.Scope == TransferScope.Account ? plan.Source.Account.Path : plan.Source.Character!.Path;
            var destinationDirectory = plan.Scope == TransferScope.Account ? plan.Destination.Account.Path : plan.Destination.Character!.Path;
            var addon = entry.Category is SettingCategory.AddonData or SettingCategory.CompanionSnapshot;
            var accountAddon = entry.Category == SettingCategory.AddonData && SafeFiles.Same(Path.GetDirectoryName(entry.SourcePath)!, Path.Combine(plan.Source.Account.Path, "SavedVariables"));
            if (accountAddon)
            {
                if (!plan.AccountDataAcknowledged || (plan.Scope == TransferScope.Character && !plan.IncludesAccountAddonData))
                    throw new TransferSafetyException("Account-wide addon replacement was not acknowledged in this plan.");
                sourceDirectory = plan.Source.Account.Path; destinationDirectory = plan.Destination.Account.Path;
            }
            if (addon) { sourceDirectory = Path.Combine(sourceDirectory, "SavedVariables"); destinationDirectory = Path.Combine(destinationDirectory, "SavedVariables"); }
            if (!SafeFiles.Same(Path.GetDirectoryName(entry.SourcePath)!, sourceDirectory) || !SafeFiles.Same(Path.GetDirectoryName(entry.DestinationPath)!, destinationDirectory)
                || !string.Equals(Path.GetFileName(entry.SourcePath), Path.GetFileName(entry.DestinationPath), SafeFiles.Comparison))
                throw new TransferSafetyException("A planned file does not belong to the selected source and destination scope.");
            if (File.Exists(entry.DestinationPath) && (File.GetAttributes(entry.DestinationPath) & FileAttributes.ReadOnly) != 0)
                throw new TransferSafetyException($"A destination file is read-only: {entry.DestinationPath}");
            ValidateAllowedFile(entry.SourcePath); ValidateAllowedFile(entry.DestinationPath);
            SafeFiles.CheckPath(entry.SourcePath); SafeFiles.CheckPath(entry.DestinationPath, true);
        }
    }
    private static void ValidateAllowedFile(string path)
    {
        var name = Path.GetFileName(path);
        var allowed = new[] { "AddOns.txt", "chat-cache.txt", "layout-local.txt", "bindings-cache.wtf", "macros-cache.txt", "config-cache.wtf", "edit-mode-cache-account.txt", "edit-mode-cache-character.txt" };
        if (allowed.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        if (Path.GetExtension(name).Equals(".lua", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "SavedVariables", StringComparison.OrdinalIgnoreCase)) return;
        throw new TransferSafetyException("The operation contains a file outside the supported settings allowlist.");
    }
    private static string ValidateBackupRoot(string backupRoot, params string[] gameRoots)
    {
        var root = SafeFiles.Full(backupRoot); SafeFiles.CheckPath(root);
        foreach (var gameRoot in gameRoots)
        {
            var installation = Path.GetDirectoryName(SafeFiles.Full(gameRoot))!;
            if (SafeFiles.Same(root, installation) || SafeFiles.Within(root, installation) || SafeFiles.Within(installation, root))
                throw new TransferSafetyException("Choose a dedicated backup folder outside the game installation.");
        }
        return root;
    }
    private static IDisposable AcquireGlobalLock()
    {
        var mutex = new Mutex(false, OperatingSystem.IsWindows() ? @"Local\WowProfileMover-Transfers" : "WowProfileMover-Transfers");
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TransferSafetyException("Another WoW Profile Mover transfer is running. Wait for it to finish.");
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    private static FileStream AcquireLock(string root)
    {
        var path = Path.Combine(root, ".transfer.lock"); SafeFiles.CheckPath(path, true);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new TransferSafetyException("Another transfer is using this backup folder. Wait for it to finish."); }
    }
    private static void CheckSpace(string path, long required)
    {
        var drive = new DriveInfo(Path.GetPathRoot(SafeFiles.Full(path))!);
        if (drive.IsReady && drive.AvailableFreeSpace < required + 4 * 1024 * 1024)
            throw new TransferSafetyException("There is not enough free disk space for verified staging and backups.");
    }
    private static string VaultPath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new TransferSafetyException("The backup manifest has an invalid path.");
        var path = SafeFiles.Full(Path.Combine(directory, relative));
        if (!SafeFiles.Within(path, directory)) throw new TransferSafetyException("The backup manifest path escapes its operation folder.");
        SafeFiles.CheckPath(path, true); return path;
    }
    private static void Save(string directory, OperationManifest manifest) => SafeFiles.WriteDurable(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
    private static void TrySave(string directory, OperationManifest manifest) { try { Save(directory, manifest); } catch { /* Existing intent journal remains available for recovery. */ } }
    private static OperationManifest Load(string directory)
    {
        directory = SafeFiles.Full(directory); SafeFiles.CheckPath(directory);
        var manifestPath = Path.Combine(directory, "manifest.json"); SafeFiles.CheckPath(manifestPath, true);
        var manifest = JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(manifestPath)) ?? throw new TransferSafetyException("The backup manifest is empty.");
        if (manifest.Version != 1 || !Guid.TryParseExact(manifest.Id, "N", out _) || manifest.Files.Count == 0)
            throw new TransferSafetyException("The backup manifest is invalid or unsupported.");
        var accountRoot = Path.Combine(manifest.DestinationClientRoot, "WTF", "Account");
        if (!DiscoveryService.Flavors.ContainsKey(Path.GetFileName(SafeFiles.Full(manifest.DestinationClientRoot)))
            || !SafeFiles.Same(Path.GetDirectoryName(manifest.DestinationAccountPath)!, accountRoot))
            throw new TransferSafetyException("The backup manifest has an invalid destination account.");
        SafeFiles.CheckPath(manifest.DestinationAccountPath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!SafeFiles.Within(file.DestinationPath, manifest.DestinationAccountPath) || !paths.Add(SafeFiles.Full(file.DestinationPath)))
                throw new TransferSafetyException("The backup manifest has an invalid destination path.");
            ValidateAllowedFile(file.DestinationPath); SafeFiles.CheckPath(file.DestinationPath, true);
            if (file.BackupRelativePath is not null) VaultPath(directory, file.BackupRelativePath);
            if (file.StageRelativePath is not null) VaultPath(directory, file.StageRelativePath);
            if ((file.OriginalSha256 is null) != (file.BackupRelativePath is null) || (file.AppliedSha256 is null) != (file.StageRelativePath is null))
                throw new TransferSafetyException("The backup manifest has inconsistent file metadata.");
        }
        return manifest;
    }

    private sealed class OperationManifest
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public string State { get; set; } = "Preparing";
        public string DestinationClientRoot { get; set; } = "";
        public string DestinationAccountPath { get; set; } = "";
        public string? RestoresOperation { get; set; }
        public List<OperationFile> Files { get; set; } = [];
    }
    private sealed class OperationFile
    {
        public string DestinationPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string? OriginalSha256 { get; set; }
        public string? AppliedSha256 { get; set; }
        public string? BackupRelativePath { get; set; }
        public string? StageRelativePath { get; set; }
        public string State { get; set; } = "Pending";
    }
}
