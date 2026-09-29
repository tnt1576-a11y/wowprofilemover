using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WowProfileMover.Core;

namespace WowProfileMover.App;

public partial class MainWindow : Window
{
    private readonly string _dataRoot;
    private readonly AppSettings _settings;
    private readonly string[] _arguments;
    private readonly TransferEngine _engine = new();
    private readonly List<CheckBox> _addonBoxes = [];
    private readonly Dictionary<string, Dictionary<string, bool>> _addonSelections = new(StringComparer.OrdinalIgnoreCase);
    private string _addonContext = "";
    private bool _ready;
    private bool _busy;
    private bool _scanning;
    private bool IsCharacter => CharacterMode.IsChecked == true;
    private WowClient? CurrentClient => ClientPicker.SelectedItem as WowClient;
    private sealed record CharacterChoice(WowCharacter Character) { public string Label => $"{Character.Name} — {Character.Realm}"; }
    private sealed record BackupChoice(RecoveryOperation Operation)
    {
        public string Id => Operation.Id;
        public string State => Operation.State;
        public string Destination => string.IsNullOrEmpty(Operation.DestinationAccountPath) ? "Unavailable" : $"{Path.GetFileName(Operation.DestinationAccountPath)} · {Path.GetFileName(Operation.DestinationClientRoot)}";
        public string DateLabel => Operation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm");
    }

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        _dataRoot = Path.GetFullPath(GetArgument("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWProfileMover"));
        _settings = AppSettings.Load(_dataRoot);
        InitializeComponent();
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        _ready = true;
        if (GetArgument("--render") is not null)
        {
            ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000;
            if (_arguments.Contains("--compact")) { Width = MinWidth; Height = MinHeight; }
        }
        InstallPath.Text = _settings.LastInstallation;
        BackupPathText.Text = _settings.BackupRoot;
        SourceInitialized += (_, _) => UseDarkTitleBar(this);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; SetStatus("Please wait for the current operation to finish before closing."); } };
        Loaded += OnLoaded;
        UpdateMode();
    }

    private string? GetArgument(string name)
    {
        var index = Array.IndexOf(_arguments, name);
        return index >= 0 && index + 1 < _arguments.Length ? _arguments[index + 1] : null;
    }
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_arguments.Contains("--demo"))
            {
                InstallPath.Text = DemoFixture.Create(_dataRoot);
                Title += " — demonstration data";
            }
            if (!string.IsNullOrWhiteSpace(InstallPath.Text) && Directory.Exists(InstallPath.Text)) await ScanAsync();
            if (_arguments.Contains("--demo"))
                SetStatus("DEMONSTRATION DATA · These example accounts are isolated fixtures, not your WoW accounts.");
            else
            {
                var interrupted = await Task.Run(() => _engine.ListRecoverableOperations(_settings.BackupRoot));
                if (interrupted.Count > 0) SetStatus($"{interrupted.Count} interrupted operation(s) need attention. Open Backups & restore before making another transfer.", true);
            }
            if (GetArgument("--render") is { } imagePath)
            {
                if (GetArgument("--render-page") == "character") CharacterMode.IsChecked = true;
                if (GetArgument("--render-page") == "backups") { Navigate(BackupPage); await RefreshBackupsAsync(); }
                if (GetArgument("--render-page") == "bridge") Navigate(BridgePage);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (_arguments.Contains("--compact")) MainScroll.ScrollToEnd();
                UpdateLayout();
                var visual = (FrameworkElement)Content;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
                using (var stream = File.Create(imagePath)) encoder.Save(stream);
                if (_arguments.Contains("--smoke")) await RunUiSmokeAsync();
                Close();
            }
        }
        catch (Exception ex) { Report(ex); if (GetArgument("--render") is not null) Application.Current.Shutdown(1); }
    }

    private async Task RunUiSmokeAsync()
    {
        if (!_arguments.Contains("--demo")) throw new InvalidOperationException("UI smoke requires isolated demonstration fixtures.");
        if (CurrentClient is null || CurrentClient.Accounts.Count != 2) throw new InvalidOperationException("Demo accounts were not populated.");
        var excludedName = (string)_addonBoxes[0].Tag;
        _addonBoxes[0].IsChecked = false;
        AddonData.IsChecked = false; AddonData.IsChecked = true;
        if (_addonBoxes.Single(c => (string)c.Tag == excludedName).IsChecked != false)
            throw new InvalidOperationException("An addon exclusion was lost when changing options.");
        AccountAcknowledge.IsChecked = true;
        var request = BuildRequest();
        var plan = await Task.Run(() => new TransferPlanner().CreatePlan(request));
        if (plan.Entries.Count < 3) throw new InvalidOperationException("Addon selections did not produce the expected preview.");
        if (plan.Entries.Any(e => e.AddonName == excludedName)) throw new InvalidOperationException("The excluded addon appeared in the plan.");
        var fixtureRoot = Path.GetFullPath(Path.Combine(_dataRoot, "Demo World of Warcraft")) + Path.DirectorySeparatorChar;
        if (plan.Entries.Any(e => !e.SourcePath.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase) || !e.DestinationPath.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UI smoke escaped its synthetic data root.");
        var fixtureEngine = new TransferEngine(new FixtureGameGuard());
        var receipt = await Task.Run(() => fixtureEngine.Execute(plan, _settings.BackupRoot));
        var restore = fixtureEngine.CreateRestorePlan(receipt.BackupDirectory);
        await Task.Run(() => fixtureEngine.Restore(restore, _settings.BackupRoot));
        CompanionInstaller.Install(BridgeSource, CurrentClient.RootPath, new FixtureGameGuard());
        CompanionInstaller.Install(BridgeSource, CurrentClient.RootPath, new FixtureGameGuard());
        var installedCore = Path.Combine(CurrentClient.RootPath, "Interface", "AddOns", "WowProfileMoverBridge", "Core.lua");
        File.AppendAllText(installedCore, "\n-- synthetic changed-version fixture\n");
        var rejected = false;
        try { CompanionInstaller.Install(BridgeSource, CurrentClient.RootPath, new FixtureGameGuard()); }
        catch (IOException) { rejected = true; }
        if (!rejected || !File.ReadAllText(installedCore).Contains("synthetic changed-version fixture")) throw new InvalidOperationException("Companion installer overwrote an existing different version.");
        CharacterMode.IsChecked = true;
        var charPlan = new TransferPlanner().CreatePlan(BuildRequest());
        if (charPlan.Scope != TransferScope.Character || charPlan.Entries.Any(e => !e.DestinationPath.StartsWith(((CharacterChoice)DestinationCharacter.SelectedItem).Character.Path + Path.DirectorySeparatorChar)))
            throw new InvalidOperationException("Character UI escaped its selected scope.");
        await RefreshBackupsAsync();
        File.WriteAllText(Path.Combine(_dataRoot, "ui-smoke-passed.txt"), "Account and character selections, preserved addon exclusions, preview, fixture copy/restore, companion installation/idempotence/conflict protection, and backup navigation passed. No game files were touched.");
    }
    private sealed class FixtureGameGuard : IGameProcessGuard { public void EnsureGameIsClosed() { } }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select World of Warcraft or a client folder" };
        if (picker.ShowDialog(this) == true) { InstallPath.Text = picker.FolderName; Scan_Click(sender, e); }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        try { await ScanAsync(); } catch (Exception ex) { Report(ex); }
    }
    private async Task ScanAsync()
    {
        if (_busy) return;
        var path = InstallPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) { SetStatus("Choose your World of Warcraft installation folder first.", true); return; }
        await BusyAsync("Reading local accounts and characters…", async () =>
        {
            var clients = await Task.Run(() => new DiscoveryService().Discover(path));
            _scanning = true;
            ClientPicker.ItemsSource = clients;
            ClientPicker.SelectedIndex = clients.Count > 0 ? 0 : -1;
            _scanning = false;
            LoadClient();
            _settings.LastInstallation = path;
            SaveSettings();
            SetStatus(clients.Count == 0
                ? "No supported client folders found. Choose an installation containing _retail_, _classic_, _classic_era_, or _anniversary_. PTR and beta are not supported."
                : "Installation loaded. Choose the source and destination, then review the files before copying.", clients.Count == 0);
        });
    }
    private void ClientPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_scanning) LoadClient(); }
    private void LoadClient()
    {
        var accounts = CurrentClient?.Accounts ?? [];
        SourceAccount.ItemsSource = accounts; DestinationAccount.ItemsSource = accounts;
        SourceAccount.SelectedIndex = accounts.Count > 0 ? 0 : -1;
        DestinationAccount.SelectedIndex = accounts.Count > 1 ? 1 : accounts.Count > 0 ? 0 : -1;
        ScanSummary.Text = $"{accounts.Count} accounts · {accounts.Sum(a => a.Characters.Count)} characters";
        AccountAcknowledge.IsChecked = false;
        RefreshAddons();
    }
    private void SourceAccount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        PopulateCharacters(SourceAccount, SourceCharacter, SourceDetails);
        RefreshAddons();
    }
    private void DestinationAccount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        PopulateCharacters(DestinationAccount, DestinationCharacter, DestinationDetails);
        AccountAcknowledge.IsChecked = false;
    }
    private static void PopulateCharacters(ComboBox accounts, ComboBox characters, TextBlock description)
    {
        var account = accounts.SelectedItem as WowAccount;
        characters.ItemsSource = account?.Characters.Select(c => new CharacterChoice(c)).ToList();
        characters.SelectedIndex = account?.Characters.Count > 0 ? 0 : -1;
        description.Text = account is null ? "No account selected." : account.Characters.Count == 0 ? "No character folders found. Enter the world once, then exit WoW." : string.Join(" · ", account.Characters.Take(3).Select(c => c.Name)) + (account.Characters.Count > 3 ? $" +{account.Characters.Count - 3}" : "");
        description.ToolTip = account?.Path;
    }
    private void SourceCharacter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshAddons(); }
    private void DestinationCharacter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) AccountAcknowledge.IsChecked = false; }
    private void Mode_Checked(object sender, RoutedEventArgs e) { if (_ready) UpdateMode(); }
    private void UpdateMode()
    {
        SourceCharacter.Visibility = DestinationCharacter.Visibility = IsCharacter ? Visibility.Visible : Visibility.Collapsed;
        IncludeAccountData.Visibility = IsCharacter ? Visibility.Visible : Visibility.Collapsed;
        CompanionSnapshot.IsEnabled = IsCharacter;
        if (!IsCharacter) CompanionSnapshot.IsChecked = false;
        AccountAcknowledge.IsChecked = false;
        RefreshAccountNotice(); RefreshAddons();
    }
    private void CategoryChanged(object sender, RoutedEventArgs e) { if (_ready) { RefreshAccountNotice(); RefreshAddons(); } }
    private void RefreshAccountNotice() => AccountNotice.Visibility = AddonData.IsChecked == true && (!IsCharacter || IncludeAccountData.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
    private void RefreshAddons()
    {
        if (!_ready) return;
        if (!string.IsNullOrEmpty(_addonContext))
        {
            if (!_addonSelections.TryGetValue(_addonContext, out var saved)) _addonSelections[_addonContext] = saved = new(StringComparer.OrdinalIgnoreCase);
            foreach (var box in _addonBoxes) saved[(string)box.Tag] = box.IsChecked == true;
        }
        _addonBoxes.Clear(); AddonChoices.Children.Clear();
        var account = SourceAccount.SelectedItem as WowAccount;
        var character = (SourceCharacter.SelectedItem as CharacterChoice)?.Character;
        _addonContext = IsCharacter ? character?.Path ?? "" : account?.Path ?? "";
        _addonSelections.TryGetValue(_addonContext, out var priorSelection);
        var roots = new List<string>();
        if (IsCharacter && character is not null) roots.Add(character.Path);
        if (account is not null && (!IsCharacter || IncludeAccountData.IsChecked == true)) roots.Add(account.Path);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in roots)
            {
                var folder = Path.Combine(root, "SavedVariables");
                if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).Equals(".lua", StringComparison.OrdinalIgnoreCase)))
                    if (!Path.GetFileNameWithoutExtension(path).Equals("WowProfileMoverBridge", StringComparison.OrdinalIgnoreCase)) names.Add(Path.GetFileNameWithoutExtension(path));
            }
        }
        catch (Exception ex) { SetStatus("Could not list addon files: " + ex.Message, true); }
        foreach (var name in names)
        {
            var checkbox = new CheckBox { Content = name, Tag = name, IsChecked = priorSelection is null || !priorSelection.TryGetValue(name, out var selected) || selected, Width = 218, Margin = new Thickness(0, 7, 10, 7), ToolTip = name };
            _addonBoxes.Add(checkbox); AddonChoices.Children.Add(checkbox);
        }
        AddonCount.Text = names.Count == 0 ? "No saved addon files in this scope." : $"{names.Count} addons found";
        AddonExpander.IsEnabled = AddonData.IsChecked == true;
    }
    private void SelectAllAddons_Click(object sender, RoutedEventArgs e) { foreach (var box in _addonBoxes) box.IsChecked = true; }
    private void ClearAddons_Click(object sender, RoutedEventArgs e) { foreach (var box in _addonBoxes) box.IsChecked = false; }

    private TransferRequest BuildRequest()
    {
        var client = CurrentClient ?? throw new TransferSafetyException("Choose a WoW client first.");
        var source = SourceAccount.SelectedItem as WowAccount ?? throw new TransferSafetyException("Choose the source account.");
        var destination = DestinationAccount.SelectedItem as WowAccount ?? throw new TransferSafetyException("Choose the destination account.");
        var sourceChar = IsCharacter ? (SourceCharacter.SelectedItem as CharacterChoice)?.Character : null;
        var destinationChar = IsCharacter ? (DestinationCharacter.SelectedItem as CharacterChoice)?.Character : null;
        if (IsCharacter && (sourceChar is null || destinationChar is null)) throw new TransferSafetyException("Choose two existing character folders. Enter the world once on each character, then close WoW.");
        var categories = new List<SettingCategory>();
        foreach (var (box, category) in new[] {
            (AddonData, SettingCategory.AddonData), (AddonEnablement, SettingCategory.AddonEnablement),
            (LocalLayout, SettingCategory.LocalLayout), (CompanionSnapshot, SettingCategory.CompanionSnapshot),
            (RawBindings, SettingCategory.Keybindings), (RawMacros, SettingCategory.Macros),
            (RawOptions, SettingCategory.GameOptions), (RawEditMode, SettingCategory.EditMode), (ChatLayout, SettingCategory.ChatLayout) })
            if (box.IsChecked == true) categories.Add(category);
        if (categories.Count == 0) throw new TransferSafetyException("Select at least one category to copy.");
        return new TransferRequest {
            Source = new WowEndpoint(client, source, sourceChar), Destination = new WowEndpoint(client, destination, destinationChar),
            Scope = IsCharacter ? TransferScope.Character : TransferScope.Account, Categories = categories,
            SelectedAddonNames = _addonBoxes.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(),
            IncludeAccountAddonData = IsCharacter && IncludeAccountData.IsChecked == true,
            AcknowledgeAccountWideData = AccountAcknowledge.IsChecked == true
        };
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var request = BuildRequest();
            TransferPlan? plan = null;
            await BusyAsync("Comparing source and destination files…", async () => plan = await Task.Run(() => new TransferPlanner().CreatePlan(request)));
            if (plan is null) return;
            var changed = plan.Entries.Count(x => x.Change != TransferChange.Identical);
            var source = EndpointLabel(plan.Source); var destination = EndpointLabel(plan.Destination);
            var review = new ReviewWindow("Review your transfer", $"{source}  →  {destination}\nDestination: {plan.Destination.Character?.Path ?? plan.Destination.Account.Path}\n{changed} file(s) to change · {plan.Entries.Count - changed} identical · Backup: {_settings.BackupRoot}",
                plan.Entries.Select(x => new ReviewRow(x.Change.ToString(), x.RelativePath, CategoryLabel(x.Category), FormatSize(x.Length))),
                plan.Warnings, "Back up and copy", changed > 0) { Owner = this };
            if (review.ShowDialog() != true) { SetStatus("Preview closed. No files were changed."); return; }
            await BusyAsync("Backing up, copying, and verifying selected files…", async () =>
            {
                var receipt = await Task.Run(() => _engine.Execute(plan, _settings.BackupRoot));
                SetStatus($"Copied and verified {receipt.FilesChanged} files. Backup saved. Log in to check profile selection and settings; game cache results need in-game verification.");
                ShowReceipt(receipt);
            });
        }
        catch (Exception ex) { Report(ex); }
    }
    private void ShowReceipt(TransferReceipt receipt) => MessageBox.Show(this,
        $"{receipt.FilesChanged} files copied and verified.\n\nBackup:\n{receipt.BackupDirectory}\n\nCheck the destination in WoW. If you copied a companion capture, type /wpm import there. Some addons require you to select the copied profile in their own menu.",
        "Transfer complete", MessageBoxButton.OK, MessageBoxImage.Information);
    private static string EndpointLabel(WowEndpoint endpoint) => endpoint.Character is null ? endpoint.Account.Id : $"{endpoint.Character.Name} — {endpoint.Character.Realm} ({endpoint.Account.Id})";
    private static string CategoryLabel(SettingCategory category) => category switch { SettingCategory.AddonData => "Addon data", SettingCategory.AddonEnablement => "Addon state", SettingCategory.CompanionSnapshot => "In-game capture", _ => category.ToString() };
    private static string FormatSize(long size) => size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024d:0.#} KB" : $"{size / (1024d * 1024):0.#} MB";

    private void Navigate(StackPanel target)
    {
        foreach (var panel in new[] { TransferPage, BackupPage, BridgePage }) panel.Visibility = panel == target ? Visibility.Visible : Visibility.Collapsed;
        TransferActions.Visibility = target == TransferPage ? Visibility.Visible : Visibility.Collapsed;
        TransferNav.Background = NavBrush(target == TransferPage); BackupNav.Background = NavBrush(target == BackupPage); BridgeNav.Background = NavBrush(target == BridgePage);
        MainScroll.ScrollToTop();
    }
    private static Brush NavBrush(bool selected) => new SolidColorBrush(selected ? Color.FromRgb(41, 38, 63) : Colors.Transparent);
    private void TransferNav_Click(object sender, RoutedEventArgs e) { if (!_busy) Navigate(TransferPage); }
    private async void BackupNav_Click(object sender, RoutedEventArgs e) { if (!_busy) { Navigate(BackupPage); await RefreshBackupsAsync(); } }
    private void BridgeNav_Click(object sender, RoutedEventArgs e) { if (!_busy) Navigate(BridgePage); }
    private async void RefreshBackups_Click(object sender, RoutedEventArgs e) => await RefreshBackupsAsync();
    private async Task RefreshBackupsAsync()
    {
        try
        {
            var operations = await Task.Run(() => _engine.ListOperations(_settings.BackupRoot));
            BackupGrid.ItemsSource = operations.Select(o => new BackupChoice(o)).ToList();
            BackupEmpty.Text = operations.Count == 0 ? "No backups yet. Your first transfer will appear here." : $"{operations.Count} saved operation(s). Select one to review its restore.";
            BackupPathText.Text = _settings.BackupRoot;
        }
        catch (Exception ex) { Report(ex); }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (BackupGrid.SelectedItem is not BackupChoice choice) { SetStatus("Select a backup to restore.", true); return; }
            RestorePlan? plan = null;
            await BusyAsync("Checking backup integrity and current files…", async () => plan = await Task.Run(() => _engine.CreateRestorePlan(choice.Operation.Directory)));
            if (plan is null) return;
            var review = new ReviewWindow("Review restore", $"Destination account: {choice.Operation.DestinationAccountPath}\nClient: {plan.DestinationClientRoot}\nCurrent destination files will be backed up first. Hover a file to see its full path.",
                plan.Entries.Select(x => new ReviewRow(x.OriginalSha256 is null ? "Remove" : "Restore", x.DestinationPath, x.HasNewerChanges ? "Newer changes" : "", "")),
                plan.Warnings.Concat(["This restores local files. It cannot reverse settings already saved on WoW's servers. For companion imports, use its in-game Restore action."]),
                "Back up and restore", plan.Entries.Count > 0) { Owner = this };
            if (review.ShowDialog() != true) { SetStatus("Restore preview closed. No files were changed."); return; }
            await BusyAsync("Saving current files and restoring the selected backup…", async () =>
            {
                var receipt = await Task.Run(() => _engine.Restore(plan, _settings.BackupRoot));
                SetStatus($"Restored {receipt.FilesChanged} files. The previous current files are preserved in a new backup.");
            });
            await RefreshBackupsAsync();
        }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_settings.BackupRoot); OpenFolder(_settings.BackupRoot); } catch (Exception ex) { Report(ex); }
    }
    private async void ChangeBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose backup storage outside your WoW installation" };
        if (picker.ShowDialog(this) != true) return;
        _settings.BackupRoot = picker.FolderName; SaveSettings(); await RefreshBackupsAsync();
        SetStatus("Backup location changed. Existing backups remain in the old location.");
    }
    private string BridgeSource => Path.Combine(AppContext.BaseDirectory, "Companion", "WowProfileMoverBridge");
    private void OpenBridgeFolder_Click(object sender, RoutedEventArgs e) { try { OpenFolder(BridgeSource); } catch (Exception ex) { Report(ex); } }
    private async void InstallBridge_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var client = CurrentClient ?? throw new TransferSafetyException("Select your WoW client on the Transfer settings tab first.");
            var destination = Path.Combine(client.RootPath, "Interface", "AddOns", "WowProfileMoverBridge");
            if (MessageBox.Show(this, $"Install the companion into:\n{destination}\n\nWoW must be closed. Existing different files will not be overwritten.", "Install companion", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await BusyAsync("Installing the companion addon…", async () => await Task.Run(() => CompanionInstaller.Install(BridgeSource, client.RootPath)));
            SetStatus("Companion installed. Log in to your source character and type /wpm export.");
        }
        catch (Exception ex) { Report(ex); }
    }
    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Folder not found: " + path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
    private void SaveSettings()
    {
        try { _settings.Save(_dataRoot); } catch (Exception ex) { SetStatus("Settings could not be saved: " + ex.Message, true); }
    }
    private async Task BusyAsync(string text, Func<Task> action)
    {
        if (_busy) return;
        _busy = true; MainScroll.IsEnabled = false; PreviewButton.IsEnabled = false; TransferNav.IsEnabled = BackupNav.IsEnabled = BridgeNav.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible; SetStatus(text);
        try { await action(); }
        finally { _busy = false; MainScroll.IsEnabled = true; PreviewButton.IsEnabled = true; TransferNav.IsEnabled = BackupNav.IsEnabled = BridgeNav.IsEnabled = true; BusyProgress.Visibility = Visibility.Collapsed; }
    }
    private void SetStatus(string text, bool error = false) { StatusText.Text = text; StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(235, 178, 155)) : (Brush)FindResource("Muted"); }
    private void Report(Exception ex)
    {
        SetStatus(ex.Message, true);
        if (GetArgument("--render") is not null)
        {
            Directory.CreateDirectory(_dataRoot); File.WriteAllText(Path.Combine(_dataRoot, "ui-error.txt"), ex.ToString());
        }
        else MessageBox.Show(this, ex.Message, "Operation could not complete", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    internal static void UseDarkTitleBar(Window window)
    {
        try { var value = 1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref value, sizeof(int)); } catch { }
    }
}
