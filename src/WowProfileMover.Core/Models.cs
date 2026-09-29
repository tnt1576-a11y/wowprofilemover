namespace WowProfileMover.Core;

public enum WowFlavor { Retail, ClassicEra, ClassicProgression, ClassicAnniversary }
public enum TransferScope { Account, Character }
public enum SettingCategory { AddonData, AddonEnablement, ChatLayout, LocalLayout, Keybindings, Macros, GameOptions, EditMode, CompanionSnapshot }
public enum TransferChange { New, Replace, Identical }
public sealed record WowClient(string RootPath, WowFlavor Flavor, string DisplayName, IReadOnlyList<WowAccount> Accounts);
public sealed record WowAccount(string Id, string Path, IReadOnlyList<WowCharacter> Characters);
public sealed record WowCharacter(string Name, string Realm, string Path, string AccountId);
public sealed record WowEndpoint(WowClient Client, WowAccount Account, WowCharacter? Character = null);

public sealed class TransferRequest
{
    public required WowEndpoint Source { get; init; }
    public required WowEndpoint Destination { get; init; }
    public TransferScope Scope { get; init; }
    public IReadOnlyCollection<SettingCategory> Categories { get; init; } = Array.Empty<SettingCategory>();
    // null means every eligible addon; an empty collection means no addons.
    public IReadOnlyCollection<string>? SelectedAddonNames { get; init; }
    public bool IncludeAccountAddonData { get; init; }
    public bool AcknowledgeAccountWideData { get; init; }
}

public sealed record TransferEntry(string SourcePath, string DestinationPath, string RelativePath,
    SettingCategory Category, string? AddonName, TransferChange Change, long Length,
    string SourceSha256, string? DestinationSha256);

public sealed class TransferPlan
{
    internal bool AccountDataAcknowledged { get; init; }
    internal bool IncludesAccountAddonData { get; init; }
    public required string Id { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required WowEndpoint Source { get; init; }
    public required WowEndpoint Destination { get; init; }
    public required TransferScope Scope { get; init; }
    public required IReadOnlyList<TransferEntry> Entries { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record TransferReceipt(string OperationId, string BackupDirectory, int FilesChanged, DateTimeOffset CompletedUtc);
public sealed record RestoreEntry(string DestinationPath, string RelativePath, string? BackupPath,
    string? OriginalSha256, string? CurrentSha256, string? AppliedSha256, bool HasNewerChanges);
public sealed record RestorePlan(string OperationDirectory, string DestinationClientRoot,
    IReadOnlyList<RestoreEntry> Entries, IReadOnlyList<string> Warnings);
public sealed record RecoveryOperation(string Id, string Directory, string State, DateTimeOffset CreatedUtc,
    string DestinationClientRoot = "", string DestinationAccountPath = "");

public interface IGameProcessGuard { void EnsureGameIsClosed(); }
// Optional fault hook for deterministic crash/failure testing. Called at documented journal boundaries.
public interface ITransferObserver { void OnCheckpoint(string checkpoint, int fileIndex); }

public sealed class TransferSafetyException(string message) : IOException(message);
