using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WowProfileMover.Core;

namespace WowProfileMover.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    private static int Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--crash-transfer")
        {
            using var childFixture = new Fixture(arguments[1], ownsRoot: false);
            childFixture.Engine(new DelegateObserver((checkpoint, _) =>
            {
                if (checkpoint == "after-apply") Environment.Exit(73);
            })).Execute(childFixture.Plan(), childFixture.BackupRoot);
            return 74;
        }
        Run("Discovery separates supported flavors and ignores unknown folders", DiscoverySeparatesFlavors);
        Run("Discovery accepts a client root and preserves Unicode identities", DiscoveryClientRoot);
        Run("Discovery ignores directories without character settings markers", DiscoveryIgnoresUnrelatedDirectories);
        Run("Account scope copies eligible account files, not character trees", AccountScopeIsBounded);
        Run("Character scope excludes account data by default", CharacterScopeIsBounded);
        Run("Cross-account character data requires explicit account acknowledgment", AccountDataAcknowledgment);
        Run("Same-account character operation leaves shared addon data untouched", SameAccountSharedData);
        Run("Addon selection is explicit and case insensitive", SelectedAddonFiltering);
        Run("Empty addon selection does not mean all addons", EmptyAddonSelection);
        Run("Companion snapshots require a captured export and explicit category", CompanionSnapshotSelection);
        Run("Identical files need no overwrite", IdenticalFiles);
        Run("Exact bytes copy, backup and restore", ExactBytesRoundTrip);
        Run("Restore removes only files introduced by the operation", RestoreNewFiles);
        Run("Source changes invalidate an approved plan", StaleSource);
        Run("Destination changes invalidate an approved plan", StaleDestination);
        Run("Destination appearing after preview invalidates a new-file plan", AppearingDestination);
        Run("Read-only destination fails without replacing original data", ReadOnlyDestination);
        Run("Locked destination fails without replacing original data", LockedDestination);
        Run("Reviewed restore backs up edits made after migration", RestoreNewerChanges);
        Run("Restore preview becomes stale if a destination changes", StaleRestore);
        Run("Process guard blocks migration and restore", ProcessGuard);
        Run("Same source and destination are rejected", SameTarget);
        Run("Case and short-path aliases cannot bypass same-target rejection", SameTargetAliases);
        Run("Cross-flavor transfer is rejected", CrossFlavor);
        Run("Forged endpoint escaping account tree is rejected", EscapingEndpoint);
        Run("Forged destination entry cannot escape target root", EscapingPlanEntry);
        Run("Source reparse point is rejected", SourceReparsePoint);
        Run("Destination reparse point is rejected", DestinationReparsePoint);
        Run("Dangling destination junction cannot redirect writes", DanglingDestinationJunction);
        Run("Source hard link is rejected", SourceHardLink);
        Run("Destination hard link is rejected", DestinationHardLink);
        Run("Backup location cannot overlap game files", UnsafeBackupRoot);
        Run("Partial failure restores original files", PartialFailureRollback);
        Run("Partial failure removes newly introduced files", PartialFailureNewFiles);
        Run("Corrupt backup fails without destroying applied data", CorruptBackup);
        Run("Corrupt staged data fails before destination changes", CorruptStage);
        Run("Canceled migration makes no destination changes", CanceledMigration);
        Run("Cancellation during apply restores the original state", CanceledDuringApply);
        Run("Abrupt process termination leaves a recoverable journal", CrashRecovery);
        Run("Game launch during migration stops writes until recovery", RunningGameDuringMigration);

        Console.WriteLine($"\nRESULT: {_passed} passed, {_failed} failed, {_skipped} skipped.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (TestSkippedException ex) { _skipped++; Console.WriteLine($"SKIP {name}: {ex.Message}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static void DiscoverySeparatesFlavors()
    {
        using var f = new Fixture();
        foreach (var flavor in new[] { "_retail_", "_classic_era_", "_classic_", "_anniversary_", "_classic_anniversary_", "_unrecognized_" })
            f.Write($"WoW/{flavor}/WTF/Account/ONE/Realm/Character/SavedVariables/Addon.lua", "DB = {}");
        var clients = new DiscoveryService().Discover(Path.Combine(f.Root, "WoW"));
        Equal(4, clients.Count, "discovered clients");
        Equal(4, clients.Select(x => x.Flavor).Distinct().Count(), "distinct flavors");
        True(clients.All(x => x.Accounts.Count == 1 && x.Accounts[0].Characters.Count == 1), "separate account/character inventory");
    }

    private static void DiscoveryClientRoot()
    {
        using var f = new Fixture();
        f.Write("WoW/_retail_/WTF/Account/123#1/Ревущий фьорд/Älpha/SavedVariables/Example.lua", "DB = {}");
        var clients = new DiscoveryService().Discover(f.RetailRoot);
        Equal(1, clients.Count, "direct client root");
        var character = clients[0].Accounts.Single().Characters.Single();
        Equal("Älpha", character.Name, "Unicode character name");
        Equal("Ревущий фьорд", character.Realm, "Unicode realm name");
        Equal("123#1", character.AccountId, "account ID");
    }

    private static void AccountScopeIsBounded()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "DB = { account = true }");
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/CharacterOnly.lua", "CharDB = {}");
        f.WriteAccount("SOURCE", "unknown.bin", "do not copy");
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua.bak", "old backup");
        f.WriteAccount("SOURCE", "SavedVariables/Unexpected.exe", "do not copy");
        var plan = f.Plan();
        Equal(1, plan.Entries.Count, "allowlisted account file count");
        Equal("Example", plan.Entries[0].AddonName, "addon identity");
        var receipt = f.Engine().Execute(plan, f.BackupRoot);
        Equal(1, receipt.FilesChanged, "changed files");
        False(File.Exists(f.AccountFile("TARGET", "Realm/Alpha/SavedVariables/CharacterOnly.lua")), "realm tree was not copied");
        False(File.Exists(f.AccountFile("TARGET", "unknown.bin")), "unknown file was not copied");
    }

    private static void DiscoveryIgnoresUnrelatedDirectories()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "Archives/Backup/notes.txt", "This is not a character folder.");
        var source = f.Endpoint("SOURCE");
        Equal(1, source.Account.Characters.Count, "unrelated nested folders excluded");
        Equal("Alpha", source.Account.Characters.Single().Name, "settings-bearing character retained");
    }

    private static void CharacterScopeIsBounded()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Shared.lua", "Shared = {}");
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/CharacterOnly.lua", "CharacterOnly = {}");
        var plan = f.Plan(TransferScope.Character);
        Equal(1, plan.Entries.Count, "character-only entry count");
        True(plan.Entries.All(e => e.DestinationPath.StartsWith(f.AccountFile("TARGET", "Realm/Beta"), StringComparison.OrdinalIgnoreCase)), "all writes target selected character");
    }

    private static void AccountDataAcknowledgment()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Shared.lua", "Shared = {}");
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/CharacterOnly.lua", "CharacterOnly = {}");
        ThrowsSafety(() => f.Plan(TransferScope.Account, acknowledge: false));
        ThrowsSafety(() => f.Plan(TransferScope.Character, includeAccount: true, acknowledge: false));
        var plan = f.Plan(TransferScope.Character, includeAccount: true, acknowledge: true);
        Equal(2, plan.Entries.Count, "explicitly selected account and character data");
    }

    private static void SameAccountSharedData()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "Realm/Beta/SavedVariables/Placeholder.lua", "Target = {}");
        f.WriteAccount("SOURCE", "SavedVariables/Shared.lua", "Shared = {}");
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/Only.lua", "Only = {}");
        var plan = f.Plan(TransferScope.Character, includeAccount: true, targetAccount: "SOURCE");
        Equal(1, plan.Entries.Count, "shared account files are not rewritten");
        True(plan.Entries.All(e => e.AddonName == "Only"), "only selected source character data");
    }

    private static void SelectedAddonFiltering()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "A = {}");
        f.WriteAccount("SOURCE", "SavedVariables/Other.lua", "B = {}");
        var plan = f.Plan(selected: new[] { "eXaMpLe" });
        Equal(1, plan.Entries.Count, "selected addon count");
        Equal("Example", plan.Entries[0].AddonName, "case-insensitive selection");
    }

    private static void EmptyAddonSelection()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "A = {}");
        var plan = f.Plan(selected: Array.Empty<string>());
        Equal(0, plan.Entries.Count, "no selected addons");
    }

    private static void IdenticalFiles()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "Same = {}");
        f.WriteAccount("TARGET", "SavedVariables/Example.lua", "Same = {}");
        var plan = f.Plan();
        True(plan.Entries.Count == 0 || plan.Entries.All(e => e.Change == TransferChange.Identical), "identical source/destination recognized");
    }

    private static void CompanionSnapshotSelection()
    {
        using var f = Fixture.Standard();
        const string snapshot = "WowProfileMoverBridgeDB = { [\"schema\"] = 1, [\"export\"] = { [\"metadata\"] = { [\"character\"] = \"Alpha\" }, }, }";
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/WowProfileMoverBridge.lua", snapshot);
        Equal(0, f.Plan(TransferScope.Character).Entries.Count, "ordinary addon migration excludes bridge snapshot");
        TransferPlan BridgePlan() => new TransferPlanner().CreatePlan(new TransferRequest
        {
            Source = f.Endpoint("SOURCE", "Alpha"), Destination = f.Endpoint("TARGET", "Beta"), Scope = TransferScope.Character,
            Categories = new[] { SettingCategory.CompanionSnapshot }
        });
        var plan = BridgePlan();
        Equal(1, plan.Entries.Count, "explicit companion selection");
        Equal(SettingCategory.CompanionSnapshot, plan.Entries.Single().Category, "companion category");
        f.WriteAccount("SOURCE", "Realm/Alpha/SavedVariables/WowProfileMoverBridge.lua", "WowProfileMoverBridgeDB = { [\"schema\"] = 1 }");
        ThrowsSafety(() => BridgePlan());
    }

    private static void ExactBytesRoundTrip()
    {
        using var f = Fixture.Standard();
        var source = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("Data = {\r\n [\"Älpha - Realm\"] = \"unchanged\",\n}\r\n")).ToArray();
        var original = Encoding.UTF8.GetBytes("Original = {\n  x = 42,\r\n}\n");
        f.WriteAccountBytes("SOURCE", "SavedVariables/Example.lua", source);
        f.WriteAccountBytes("TARGET", "SavedVariables/Example.lua", original);
        var sourcePath = f.AccountFile("SOURCE", "SavedVariables/Example.lua");
        var targetPath = f.AccountFile("TARGET", "SavedVariables/Example.lua");
        var engine = f.Engine();
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        Bytes(source, File.ReadAllBytes(targetPath), "copied bytes");
        Bytes(source, File.ReadAllBytes(sourcePath), "source remains unchanged");
        True(Directory.EnumerateFiles(receipt.BackupDirectory, "*", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "backup contains exact original bytes");
        engine.Restore(engine.CreateRestorePlan(receipt.BackupDirectory), f.BackupRoot);
        Bytes(original, File.ReadAllBytes(targetPath), "restored original bytes");
        Bytes(source, File.ReadAllBytes(sourcePath), "source unchanged after restore");
    }

    private static void RestoreNewFiles()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Introduced.lua", "New = {}");
        var engine = f.Engine();
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        f.WriteAccount("TARGET", "SavedVariables/Unrelated.lua", "User = { important = true }");
        var restoreReceipt = engine.Restore(engine.CreateRestorePlan(receipt.BackupDirectory), f.BackupRoot);
        False(File.Exists(f.AccountFile("TARGET", "SavedVariables/Introduced.lua")), "introduced file removed");
        Equal("User = { important = true }", File.ReadAllText(f.AccountFile("TARGET", "SavedVariables/Unrelated.lua")), "unrelated file preserved");
        engine.Restore(engine.CreateRestorePlan(restoreReceipt.BackupDirectory), f.BackupRoot);
        Equal("New = {}", File.ReadAllText(f.AccountFile("TARGET", "SavedVariables/Introduced.lua")), "undoing restore reinstates previously introduced file");
        Equal("User = { important = true }", File.ReadAllText(f.AccountFile("TARGET", "SavedVariables/Unrelated.lua")), "unrelated file preserved through undo restore");
    }

    private static void StaleSource()
    {
        using var f = Fixture.WithCopy();
        var plan = f.Plan();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "changed source");
        ThrowsSafety(() => f.Engine().Execute(plan, f.BackupRoot));
        f.AssertOriginal();
    }

    private static void StaleDestination()
    {
        using var f = Fixture.WithCopy();
        var plan = f.Plan();
        f.WriteAccount("TARGET", "SavedVariables/Example.lua", "changed destination");
        ThrowsSafety(() => f.Engine().Execute(plan, f.BackupRoot));
        Equal("changed destination", File.ReadAllText(f.TargetFile), "newer destination retained");
    }

    private static void AppearingDestination()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "new source");
        var plan = f.Plan();
        f.WriteAccount("TARGET", "SavedVariables/Example.lua", "appeared later");
        ThrowsSafety(() => f.Engine().Execute(plan, f.BackupRoot));
        Equal("appeared later", File.ReadAllText(f.TargetFile), "newly appeared destination retained");
    }

    private static void RestoreNewerChanges()
    {
        using var f = Fixture.WithCopy();
        var engine = f.Engine();
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        f.WriteAccount("TARGET", "SavedVariables/Example.lua", "edited after migration");
        var restore = engine.CreateRestorePlan(receipt.BackupDirectory);
        True(restore.Entries.Single().HasNewerChanges, "restore identifies later edits");
        var restoreReceipt = engine.Restore(restore, f.BackupRoot);
        f.AssertOriginal();
        True(Directory.EnumerateFiles(restoreReceipt.BackupDirectory, "*", SearchOption.AllDirectories)
            .Any(p => File.ReadAllBytes(p).SequenceEqual(Encoding.UTF8.GetBytes("edited after migration"))), "later edit preserved in fresh verified backup");
        engine.Restore(engine.CreateRestorePlan(restoreReceipt.BackupDirectory), f.BackupRoot);
        Equal("edited after migration", File.ReadAllText(f.TargetFile), "restore can itself be undone");
    }

    private static void ReadOnlyDestination()
    {
        using var f = Fixture.WithCopy();
        var plan = f.Plan();
        var attributes = File.GetAttributes(f.TargetFile);
        File.SetAttributes(f.TargetFile, attributes | FileAttributes.ReadOnly);
        try { Throws<IOException>(() => f.Engine().Execute(plan, f.BackupRoot)); f.AssertOriginal(); }
        finally { File.SetAttributes(f.TargetFile, attributes); }
    }

    private static void LockedDestination()
    {
        using var f = Fixture.WithCopy();
        var plan = f.Plan();
        using (var locked = new FileStream(f.TargetFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => f.Engine().Execute(plan, f.BackupRoot));
        f.AssertOriginal();
    }

    private static void StaleRestore()
    {
        using var f = Fixture.WithCopy();
        var engine = f.Engine();
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        var restore = engine.CreateRestorePlan(receipt.BackupDirectory);
        f.WriteAccount("TARGET", "SavedVariables/Example.lua", "edited after restore preview");
        ThrowsSafety(() => engine.Restore(restore, f.BackupRoot));
        Equal("edited after restore preview", File.ReadAllText(f.TargetFile), "stale restore did not overwrite edit");
    }

    private static void ProcessGuard()
    {
        using var f = Fixture.WithCopy();
        var guard = new FakeGuard { IsRunning = true };
        var engine = new TransferEngine(guard);
        ThrowsSafety(() => engine.Execute(f.Plan(), f.BackupRoot));
        f.AssertOriginal();
        guard.IsRunning = false;
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        var restore = engine.CreateRestorePlan(receipt.BackupDirectory);
        guard.IsRunning = true;
        ThrowsSafety(() => engine.Restore(restore, f.BackupRoot));
        Equal(Fixture.SourceText, File.ReadAllText(f.TargetFile), "running-game restore blocked");
        True(guard.Calls >= 3, "guard applied on operation boundaries");
    }

    private static void SameTarget()
    {
        using var f = Fixture.WithCopy();
        var endpoint = f.Endpoint("SOURCE");
        ThrowsSafety(() => new TransferPlanner().CreatePlan(new TransferRequest
        {
            Source = endpoint, Destination = endpoint, Scope = TransferScope.Account,
            Categories = new[] { SettingCategory.AddonData }, AcknowledgeAccountWideData = true
        }));
    }

    private static void CrossFlavor()
    {
        using var f = Fixture.WithCopy();
        f.Write("WoW/_classic_era_/WTF/Account/TARGET/Realm/Beta/config-cache.wtf", "SET example \"1\"");
        var classic = new DiscoveryService().Discover(f.WowRoot).Single(c => c.Flavor == WowFlavor.ClassicEra);
        var destination = new WowEndpoint(classic, classic.Accounts.Single());
        ThrowsSafety(() => new TransferPlanner().CreatePlan(new TransferRequest
        {
            Source = f.Endpoint("SOURCE"), Destination = destination, Scope = TransferScope.Account,
            Categories = new[] { SettingCategory.AddonData }, AcknowledgeAccountWideData = true
        }));
    }

    private static void SameTargetAliases()
    {
        if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows case and short-path alias validation.");
        using var f = Fixture.WithCopy();
        var original = f.Endpoint("SOURCE");
        void RejectAlias(string root, string accountPath)
        {
            var account = new WowAccount(original.Account.Id, accountPath, Array.Empty<WowCharacter>());
            var client = new WowClient(root, original.Client.Flavor, original.Client.DisplayName, new[] { account });
            ThrowsSafety(() => new TransferPlanner().CreatePlan(new TransferRequest
            {
                Source = new WowEndpoint(client, account), Destination = original, Scope = TransferScope.Account,
                Categories = new[] { SettingCategory.AddonData }, AcknowledgeAccountWideData = true
            }));
        }
        RejectAlias(original.Client.RootPath.ToLowerInvariant(), original.Account.Path.ToLowerInvariant());
        var shortRoot = new StringBuilder(32768);
        var shortAccount = new StringBuilder(32768);
        var rootLength = GetShortPathName(original.Client.RootPath, shortRoot, shortRoot.Capacity);
        var accountLength = GetShortPathName(original.Account.Path, shortAccount, shortAccount.Capacity);
        True(rootLength > 0 && accountLength > 0, "Windows resolved short path spelling");
        if (!string.Equals(shortRoot.ToString(), original.Client.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            RejectAlias(shortRoot.ToString(), shortAccount.ToString());
            Console.WriteLine("NOTE distinct Windows 8.3 alias was exercised.");
        }
        else Console.WriteLine("NOTE 8.3 aliases are unavailable on this fixture volume; lowercase alias was exercised.");
        f.AssertOriginal();
    }

    private static void EscapingEndpoint()
    {
        using var f = Fixture.WithCopy();
        f.Write("outside/SavedVariables/Example.lua", "outside bytes");
        var normal = f.Endpoint("TARGET");
        var badAccount = new WowAccount("TARGET", Path.Combine(f.Root, "outside"), Array.Empty<WowCharacter>());
        ThrowsSafety(() => new TransferPlanner().CreatePlan(new TransferRequest
        {
            Source = f.Endpoint("SOURCE"), Destination = new WowEndpoint(normal.Client, badAccount),
            Scope = TransferScope.Account, Categories = new[] { SettingCategory.AddonData }, AcknowledgeAccountWideData = true
        }));
        Equal("outside bytes", File.ReadAllText(Path.Combine(f.Root, "outside", "SavedVariables", "Example.lua")), "outside bytes retained");
    }

    private static void EscapingPlanEntry()
    {
        using var f = Fixture.WithCopy();
        var plan = f.Plan();
        var outside = f.Write("outside/important.lua", "outside important");
        var forged = new TransferPlan
        {
            Id = plan.Id, CreatedUtc = plan.CreatedUtc, Source = plan.Source, Destination = plan.Destination,
            Scope = plan.Scope, Warnings = plan.Warnings,
            Entries = new[] { plan.Entries.Single() with { DestinationPath = outside, RelativePath = "../../outside/important.lua" } }
        };
        ThrowsSafety(() => f.Engine().Execute(forged, f.BackupRoot));
        Equal("outside important", File.ReadAllText(outside), "outside target preserved");
        f.AssertOriginal();
    }

    private static void SourceReparsePoint()
    {
        using var f = Fixture.Standard();
        var outsideDir = Path.Combine(f.Root, "outside-source");
        f.Write("outside-source/Example.lua", "outside source");
        var link = f.AccountFile("SOURCE", "SavedVariables");
        f.Junction(link, outsideDir);
        ThrowsSafety(() => { var plan = f.Plan(); f.Engine().Execute(plan, f.BackupRoot); });
        False(File.Exists(f.TargetFile), "linked source not copied");
    }

    private static void DestinationReparsePoint()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", Fixture.SourceText);
        f.Write("outside-target/Example.lua", "outside destination");
        f.Junction(f.AccountFile("TARGET", "SavedVariables"), Path.Combine(f.Root, "outside-target"));
        ThrowsSafety(() => { var plan = f.Plan(); f.Engine().Execute(plan, f.BackupRoot); });
        Equal("outside destination", File.ReadAllText(Path.Combine(f.Root, "outside-target", "Example.lua")), "junction target unchanged");
    }

    private static void SourceHardLink()
    {
        using var f = Fixture.Standard();
        var outside = f.Write("outside-source.lua", "outside hardlink source");
        f.HardLink(f.AccountFile("SOURCE", "SavedVariables/Example.lua"), outside);
        ThrowsSafety(() => { var plan = f.Plan(); f.Engine().Execute(plan, f.BackupRoot); });
        False(File.Exists(f.TargetFile), "hardlinked source not copied");
    }

    private static void DanglingDestinationJunction()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", Fixture.SourceText);
        var absentTarget = Path.Combine(f.Root, "not-yet-existing");
        f.Junction(f.AccountFile("TARGET", "SavedVariables"), absentTarget);
        ThrowsSafety(() => { var plan = f.Plan(); f.Engine().Execute(plan, f.BackupRoot); });
        False(Directory.Exists(absentTarget), "dangling junction target must not be created");
    }

    private static void DestinationHardLink()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", Fixture.SourceText);
        var outside = f.Write("outside-target.lua", "outside hardlink destination");
        f.HardLink(f.TargetFile, outside);
        ThrowsSafety(() => { var plan = f.Plan(); f.Engine().Execute(plan, f.BackupRoot); });
        Equal("outside hardlink destination", File.ReadAllText(outside), "hardlink original retained");
    }

    private static void UnsafeBackupRoot()
    {
        using var f = Fixture.WithCopy();
        foreach (var unsafeRoot in new[]
        {
            f.AccountFile("TARGET", "SavedVariables/Backups"),
            f.AccountFile("SOURCE", "SavedVariables/Backups"),
            Path.Combine(f.WowRoot, "Backups"),
            f.WowRoot,
            f.Root
        }) ThrowsSafety(() => f.Engine().Execute(f.Plan(), unsafeRoot));
        f.AssertOriginal();
    }

    private static void PartialFailureRollback()
    {
        using var f = Fixture.WithCopy();
        f.WriteAccount("SOURCE", "SavedVariables/Second.lua", "Second = { source = true }");
        f.WriteAccount("TARGET", "SavedVariables/Second.lua", "Second = { original = true }");
        var observer = new DelegateObserver((checkpoint, _) => { if (checkpoint == "after-apply") throw new InjectedFailureException(); });
        Throws<Exception>(() => f.Engine(observer).Execute(f.Plan(), f.BackupRoot));
        f.AssertOriginal();
        Equal("Second = { original = true }", File.ReadAllText(f.AccountFile("TARGET", "SavedVariables/Second.lua")), "untouched/rolled-back second file");
    }

    private static void PartialFailureNewFiles()
    {
        using var f = Fixture.Standard();
        f.WriteAccount("SOURCE", "SavedVariables/Example.lua", "New = {}");
        f.WriteAccount("SOURCE", "SavedVariables/Second.lua", "NewSecond = {}");
        var observer = new DelegateObserver((checkpoint, _) => { if (checkpoint == "after-apply") throw new InjectedFailureException(); });
        Throws<Exception>(() => f.Engine(observer).Execute(f.Plan(), f.BackupRoot));
        False(File.Exists(f.TargetFile), "introduced first file removed after failure");
        False(File.Exists(f.AccountFile("TARGET", "SavedVariables/Second.lua")), "second file absent after failure");
    }

    private static void CorruptBackup()
    {
        using var f = Fixture.WithCopy();
        var engine = f.Engine();
        var receipt = engine.Execute(f.Plan(), f.BackupRoot);
        var originalBytes = Encoding.UTF8.GetBytes(Fixture.OriginalText);
        var backup = Directory.EnumerateFiles(receipt.BackupDirectory, "*", SearchOption.AllDirectories)
            .Single(path => File.ReadAllBytes(path).SequenceEqual(originalBytes));
        File.WriteAllText(backup, "CORRUPT BACKUP");
        ThrowsSafety(() => engine.Restore(engine.CreateRestorePlan(receipt.BackupDirectory), f.BackupRoot));
        Equal(Fixture.SourceText, File.ReadAllText(f.TargetFile), "valid current data survives bad backup");
    }

    private static void CanceledMigration()
    {
        using var f = Fixture.WithCopy();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => f.Engine().Execute(f.Plan(), f.BackupRoot, cancellation.Token));
        f.AssertOriginal();
    }

    private static void CorruptStage()
    {
        using var f = Fixture.WithCopy();
        var observer = new DelegateObserver((checkpoint, _) =>
        {
            if (checkpoint != "backup-complete") return;
            var staged = Directory.EnumerateFiles(f.BackupRoot, "*.bin", SearchOption.AllDirectories)
                .Single(p => File.ReadAllText(p) == Fixture.SourceText);
            File.WriteAllText(staged, "tampered stage bytes");
        });
        ThrowsSafety(() => f.Engine(observer).Execute(f.Plan(), f.BackupRoot));
        f.AssertOriginal();
    }

    private static void CanceledDuringApply()
    {
        using var f = Fixture.WithCopy();
        f.WriteAccount("SOURCE", "SavedVariables/Second.lua", "Second = {}");
        using var cancellation = new CancellationTokenSource();
        var observer = new DelegateObserver((checkpoint, _) => { if (checkpoint == "after-apply") cancellation.Cancel(); });
        Throws<OperationCanceledException>(() => f.Engine(observer).Execute(f.Plan(), f.BackupRoot, cancellation.Token));
        f.AssertOriginal();
        False(File.Exists(f.AccountFile("TARGET", "SavedVariables/Second.lua")), "cancellation did not leave new file");
    }

    private static void CrashRecovery()
    {
        using var f = Fixture.WithCopy();
        f.WriteAccount("SOURCE", "SavedVariables/Second.lua", "Second = { source = true }");
        f.WriteAccount("TARGET", "SavedVariables/Second.lua", "Second = { original = true }");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-transfer");
        start.ArgumentList.Add(f.Root);
        using var child = Process.Start(start)!;
        var outputRead = child.StandardOutput.ReadToEndAsync();
        var errorRead = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(15000)) { child.Kill(); throw new InvalidOperationException("Crash fixture timed out."); }
        var output = outputRead.GetAwaiter().GetResult();
        var error = errorRead.GetAwaiter().GetResult();
        Equal(73, child.ExitCode, $"child intentionally terminated after first write; output={output}; error={error}");
        Equal(Fixture.SourceText, File.ReadAllText(f.TargetFile), "first file applied before termination");
        Equal("Second = { original = true }", File.ReadAllText(f.AccountFile("TARGET", "SavedVariables/Second.lua")), "second file untouched by terminated process");
        var engine = f.Engine();
        var recovery = engine.ListRecoverableOperations(f.BackupRoot).Single();
        var restore = engine.CreateRestorePlan(recovery.Directory);
        Equal(1, restore.Entries.Count, "only intent/applied file needs crash recovery");
        engine.Restore(restore, f.BackupRoot);
        f.AssertOriginal();
        Equal(0, engine.ListRecoverableOperations(f.BackupRoot).Count, "successful recovery is no longer pending");
    }

    private static void RunningGameDuringMigration()
    {
        using var f = Fixture.WithCopy();
        f.WriteAccount("SOURCE", "SavedVariables/Second.lua", "Second = {}");
        var guard = new FakeGuard();
        var observer = new DelegateObserver((checkpoint, _) => { if (checkpoint == "after-apply") guard.IsRunning = true; });
        var engine = new TransferEngine(guard, observer);
        ThrowsSafety(() => engine.Execute(f.Plan(), f.BackupRoot));
        Equal(Fixture.SourceText, File.ReadAllText(f.TargetFile), "rollback does not write into running game");
        False(File.Exists(f.AccountFile("TARGET", "SavedVariables/Second.lua")), "further writes stopped");
        var recovery = engine.ListRecoverableOperations(f.BackupRoot).Single();
        guard.IsRunning = false;
        new TransferEngine(guard).Restore(engine.CreateRestorePlan(recovery.Directory), f.BackupRoot);
        f.AssertOriginal();
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
    }
    private static void True(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static void False(bool value, string label) => True(!value, label);
    private static void Bytes(byte[] expected, byte[] actual, string label) => True(expected.SequenceEqual(actual), label);
    private static void ThrowsSafety(Action action) => Throws<TransferSafetyException>(action);
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}, but operation succeeded.");
    }

    private sealed class FakeGuard : IGameProcessGuard
    {
        public bool IsRunning { get; set; }
        public int Calls { get; private set; }
        public void EnsureGameIsClosed()
        {
            Calls++;
            if (IsRunning) throw new TransferSafetyException("Fixture game process is running.");
        }
    }

    private sealed class DelegateObserver(Action<string, int> callback) : ITransferObserver
    {
        public void OnCheckpoint(string checkpoint, int fileIndex) => callback(checkpoint, fileIndex);
    }
    private sealed class InjectedFailureException : IOException;
    private sealed class TestSkippedException(string message) : Exception(message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

    private sealed class Fixture : IDisposable
    {
        public const string SourceText = "Example = { source = true }";
        public const string OriginalText = "Example = { original = true }";
        private readonly List<string> _junctions = new();
        private readonly bool _ownsRoot;
        public string Root { get; }
        public string WowRoot => Path.Combine(Root, "WoW");
        public string RetailRoot => Path.Combine(WowRoot, "_retail_");
        public string BackupRoot => Path.Combine(Root, "Backups");
        public string TargetFile => AccountFile("TARGET", "SavedVariables/Example.lua");

        public Fixture(string? root = null, bool ownsRoot = true)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "WowProfileMover.Tests", Guid.NewGuid().ToString("N"));
            _ownsRoot = ownsRoot;
            Directory.CreateDirectory(Root);
        }
        public static Fixture Standard()
        {
            var fixture = new Fixture();
            fixture.WriteAccount("SOURCE", "Realm/Alpha/config-cache.wtf", "SET example \"1\"");
            fixture.WriteAccount("TARGET", "Realm/Beta/config-cache.wtf", "SET example \"2\"");
            return fixture;
        }
        public static Fixture WithCopy()
        {
            var fixture = Standard();
            fixture.WriteAccount("SOURCE", "SavedVariables/Example.lua", SourceText);
            fixture.WriteAccount("TARGET", "SavedVariables/Example.lua", OriginalText);
            return fixture;
        }

        public string Write(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));
        public string WriteBytes(string relative, byte[] content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }
        public string AccountFile(string account, string relative) => Path.Combine(RetailRoot, "WTF", "Account", account, relative.Replace('/', Path.DirectorySeparatorChar));
        public void WriteAccount(string account, string relative, string content) => WriteAccountBytes(account, relative, Encoding.UTF8.GetBytes(content));
        public void WriteAccountBytes(string account, string relative, byte[] content)
        {
            var path = AccountFile(account, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        public WowEndpoint Endpoint(string account, string? character = null)
        {
            var client = new DiscoveryService().Discover(RetailRoot).Single();
            var found = client.Accounts.Single(a => a.Id == account);
            return new WowEndpoint(client, found, character is null ? null : found.Characters.Single(c => c.Name == character));
        }
        public TransferPlan Plan(TransferScope scope = TransferScope.Account, IReadOnlyCollection<string>? selected = null,
            bool includeAccount = false, bool acknowledge = true, string targetAccount = "TARGET") =>
            new TransferPlanner().CreatePlan(new TransferRequest
            {
                Source = Endpoint("SOURCE", scope == TransferScope.Character ? "Alpha" : null),
                Destination = Endpoint(targetAccount, scope == TransferScope.Character ? "Beta" : null),
                Scope = scope, Categories = new[] { SettingCategory.AddonData }, SelectedAddonNames = selected,
                IncludeAccountAddonData = includeAccount, AcknowledgeAccountWideData = acknowledge
            });
        public TransferEngine Engine(ITransferObserver? observer = null) => new(new FakeGuard(), observer);
        public void AssertOriginal() => Equal(OriginalText, File.ReadAllText(TargetFile), "original destination preserved");

        public void Junction(string path, string target)
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(path, target);
                _junctions.Add(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", path, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new TestSkippedException($"Could not create fixture junction: {output} {error}");
            _junctions.Add(path);
        }

        public void HardLink(string path, string target)
        {
            if (!OperatingSystem.IsWindows()) throw new TestSkippedException("Windows hard-link validation fixture.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!CreateHardLink(path, target, IntPtr.Zero)) throw new TestSkippedException($"Could not create fixture hard link (Win32 {Marshal.GetLastWin32Error()}).");
        }

        public void Dispose()
        {
            if (!_ownsRoot) return;
            foreach (var path in _junctions)
            {
                try { Directory.Delete(path); }
                catch (DirectoryNotFoundException) { }
            }
            // The target is an immutable GUID directory allocated by this fixture under the system temp directory.
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WowProfileMover.Tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            try { Directory.Delete(Root, true); }
            catch (IOException) { Console.WriteLine($"NOTE fixture cleanup deferred: {Root}"); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
