using AtlWsl.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AtlWsl.Manager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AppInfo> apps = [];
    private readonly LifecycleService lifecycle;
    private WslBridge bridge;
    private ReleaseManifest? availableRelease;
    private bool isBusy;

    public MainWindow()
    {
        InitializeComponent();
        var distributionName = ReadDistributionArgument() ?? ManagerSettings.Load().DistributionName;
        bridge = new WslBridge(distributionName);
        using var keyRing = typeof(MainWindow).Assembly.GetManifestResourceStream("ATLWSL.ReleasePublicKeys.json")
            ?? throw new InvalidDataException("Embedded release public-key ring is missing.");
        lifecycle = new LifecycleService(ManifestSignatureVerifier.Load(keyRing));
        DistroNameBox.Text = distributionName;
        AboutVersionText.Text = $"ATL-WSL {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        AppsList.ItemsSource = apps;
        DistroSummaryText.Text = bridge.DistributionName;
        ShowPanel(OverviewPanel, showOptions: false);
    }

    private AppInfo? SelectedApp => AppsList.SelectedItem as AppInfo;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await lifecycle.RecoverInterruptedOperationAsync();
        await RefreshLifecycleAsync();
        var state = await lifecycle.LoadStateAsync();
        if (state.State is ManagerLifecycleState.Installed or ManagerLifecycleState.UpdateAvailable or ManagerLifecycleState.Degraded)
        {
            await RefreshAllAsync();
        }
    }

    private async Task RefreshLifecycleAsync()
    {
        var state = await lifecycle.LoadStateAsync();
        LifecycleStateText.Text = state.State;
        LifecycleVersionText.Text = string.IsNullOrWhiteSpace(state.Version) ? "Not installed" : state.Version;
        LifecycleDetailText.Text = string.IsNullOrWhiteSpace(state.LastError)
            ? $"Distribution: {state.DistributionName}"
            : state.LastError;
        try
        {
            availableRelease = await lifecycle.LoadReleaseAsync(
                new Uri("https://github.com/LamPPKK/ATL-WSL/releases/latest/download/release-manifest.json"));
            AvailableVersionText.Text = availableRelease.Version;
            await lifecycle.CheckForUpdateAsync(availableRelease);
        }
        catch (Exception exception)
        {
            AvailableVersionText.Text = "Unavailable";
            SetStatus($"Update check did not block the UI: {exception.Message}");
        }
    }

    private async void InstallRuntime_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var release = availableRelease ?? throw new InvalidOperationException("A verified release manifest is not available.");
        var location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATL-WSL", "Distros", DistroNameBox.Text.Trim());
        await lifecycle.InstallAsync(release, DistroNameBox.Text.Trim(), location, new Progress<string>(SetStatus));
        bridge = new WslBridge(DistroNameBox.Text.Trim());
        await RefreshLifecycleAsync();
        await RefreshAllAsync();
    }, "ATL-WSL installed.");

    private async void UpdateRuntime_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var release = availableRelease ?? throw new InvalidOperationException("A verified release manifest is not available.");
        await lifecycle.UpdateAsync(release, new Progress<string>(SetStatus));
        await RefreshLifecycleAsync();
        await RefreshAllAsync();
    }, "Update committed after doctor passed.");

    private async void RepairRuntime_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var release = availableRelease ?? throw new InvalidOperationException("A verified release manifest is not available.");
        await lifecycle.RepairAsync(release);
        await RefreshLifecycleAsync();
        await RefreshAllAsync();
    }, "Runtime repair completed.");

    private async void UninstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        var destructive = RemoveDistroDataBox.IsChecked == true;
        if (destructive && MessageBox.Show(this,
            "This permanently unregisters the distro and deletes all APK and per-app data after the optional export. Continue?",
            "Permanent data removal", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!destructive && MessageBox.Show(this,
            "Remove the ATL-WSL Manager shortcut while retaining the distro, APKs, and app data?",
            "Detach manager", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunUiAsync(async () =>
        {
            string? export = destructive && ExportDistroBox.IsChecked == true
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"ATL-WSL-export-{DateTime.Now:yyyyMMdd-HHmmss}.vhdx")
                : null;
            await lifecycle.UninstallAsync(destructive, DistroConfirmationBox.Text, export);
            DeleteManagerShortcut();
            await RefreshLifecycleAsync();
        }, destructive ? "Distribution and data removed." : "Manager detached; distro data retained.");
    }

    private static void DeleteManagerShortcut()
    {
        var shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "ATL-WSL Manager.lnk");
        if (File.Exists(shortcut)) File.Delete(shortcut);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(() => RefreshLibraryAsync(), "Library refreshed.");

    private async void Doctor_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync(RefreshDoctorAsync, "System checks completed.");

    private async void AddApk_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose an Android package",
            Filter = "Android packages (*.apk)|*.apk",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var inspection = await bridge.InspectAsync(picker.FileName);
            if (!inspection.Compatible)
            {
                throw new AtlWslException("incompatible_abi", inspection.CompatibilityReason);
            }

            var result = await bridge.AddAsync(picker.FileName);
            await RefreshLibraryAsync(result.App.Id);
            SetStatus(
                result.AlreadyInstalled
                    ? $"{result.App.DisplayName} is already in the library."
                    : result.Restored
                        ? $"{result.App.DisplayName} was restored with its retained data."
                        : $"Added {result.App.DisplayName}.");
        });
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApp;
        if (selected is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var request = ReadConfigureRequest();
            var configured = await bridge.ConfigureAsync(selected.Id, request);
            var result = await bridge.LaunchAsync(selected.Id);
            await RefreshLibraryAsync(configured.Id);
            SetStatus($"Launched {configured.DisplayName} with {result.Renderer}.");
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApp;
        if (selected is null)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var configured = await bridge.ConfigureAsync(selected.Id, ReadConfigureRequest());
            await RefreshLibraryAsync(configured.Id);
        }, "Launch options saved.");
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApp;
        if (selected is null)
        {
            return;
        }

        var dialog = new RemoveDialog(selected.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice is RemoveChoice.Cancel)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var deleteData = dialog.Choice is RemoveChoice.DeleteData;
            await bridge.RemoveAsync(selected.Id, deleteData);
            await RefreshLibraryAsync();
            SetStatus(deleteData ? $"Removed {selected.DisplayName} and its data." : $"Removed {selected.DisplayName}; data was retained.");
        });
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "Export ATL-WSL diagnostics",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"ATL-WSL-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true,
            DefaultExt = ".zip",
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        await RunUiAsync(async () =>
        {
            var result = await bridge.ExportDiagnosticsAsync(picker.FileName);
            SetStatus($"Diagnostics exported to {result.Path}.");
        });
    }

    private async void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        var name = DistroNameBox.Text.Trim();
        if (!IsValidDistributionName(name))
        {
            ShowError("Use 1–64 letters, numbers, spaces, dots, underscores or hyphens.", "invalid_distribution_name");
            return;
        }

        bridge = new WslBridge(name);
        new ManagerSettings(name).Save();
        DistroSummaryText.Text = name;
        await RefreshAllAsync();
    }

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedApp;
        NoSelectionText.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        OptionsEditor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        OptionActions.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is null)
        {
            return;
        }

        DisplayNameBox.Text = selected.DisplayName;
        WidthBox.Text = selected.LaunchOptions.Width?.ToString() ?? string.Empty;
        HeightBox.Text = selected.LaunchOptions.Height?.ToString() ?? string.Empty;
        ActivityBox.Text = selected.LaunchOptions.Activity ?? string.Empty;
        FullscreenBox.IsChecked = selected.LaunchOptions.Fullscreen;
        WebViewBox.IsChecked = selected.LaunchOptions.WebView;
        ValidateCertificatesBox.IsChecked = selected.LaunchOptions.ValidateCertificates;
        DirectEglBox.IsChecked = selected.LaunchOptions.DirectEgl;
        LocationBox.IsChecked = selected.LaunchOptions.Location;
    }

    private void ShowOverview_Click(object sender, RoutedEventArgs e) => ShowPanel(OverviewPanel, showOptions: false);

    private void ShowLibrary_Click(object sender, RoutedEventArgs e) => ShowPanel(LibraryPanel, showOptions: true);

    private void ShowDiagnostics_Click(object sender, RoutedEventArgs e) => ShowPanel(DiagnosticsPanel, showOptions: false);

    private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel, showOptions: false);

    private void ShowPanel(UIElement panel, bool showOptions)
    {
        OverviewPanel.Visibility = panel == OverviewPanel ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = panel == LibraryPanel ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = panel == DiagnosticsPanel ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = panel == SettingsPanel ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
        OptionsColumn.Width = showOptions ? new GridLength(354) : new GridLength(0);
        OverviewNavButton.ClearValue(Button.BackgroundProperty);
        LibraryNavButton.ClearValue(Button.BackgroundProperty);
        DiagnosticsNavButton.ClearValue(Button.BackgroundProperty);
        SettingsNavButton.ClearValue(Button.BackgroundProperty);
        var activeButton = panel == OverviewPanel
            ? OverviewNavButton
            : panel == LibraryPanel
            ? LibraryNavButton
            : panel == DiagnosticsPanel
                ? DiagnosticsNavButton
                : SettingsNavButton;
        activeButton.SetResourceReference(Button.BackgroundProperty, "AccentSoftBrush");
    }

    private async Task RefreshAllAsync()
    {
        await RunUiAsync(async () =>
        {
            await RefreshDoctorAsync();
            await RefreshLibraryAsync();
        }, "ATL-WSL is ready.");
    }

    private async Task RefreshLibraryAsync(string? selectedId = null)
    {
        selectedId ??= SelectedApp?.Id;
        var result = await bridge.ListAppsAsync();
        apps.Clear();
        foreach (var app in result.Apps)
        {
            apps.Add(app);
        }

        EmptyLibraryPanel.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsList.Visibility = apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AppsList.SelectedItem = selectedId is null ? apps.FirstOrDefault() : apps.FirstOrDefault(app => app.Id == selectedId);
    }

    private async Task RefreshDoctorAsync()
    {
        var result = await bridge.DoctorAsync();
        ConnectionStatusText.Text = result.Healthy ? "Ready" : "Needs attention";
        ConnectionDot.Fill = ResourceBrush(result.Healthy ? "SuccessBrush" : "WarningBrush");
        DistroSummaryText.Text = $"{bridge.DistributionName} · {result.Architecture}";
        DoctorDistroText.Text = bridge.DistributionName;
        DoctorArchitectureText.Text = $"{result.Architecture} · requires {result.RequiredAbi}";
        DoctorRendererText.Text = result.Renderer;
        DoctorVersionText.Text = ReadReleaseValue(result.Release, "version") ?? "unknown";
        HealthChecksItems.ItemsSource = result.Checks.Select(pair => new HealthCheckView(
            FriendlyCheckName(pair.Key),
            pair.Value ? "Passed" : "Failed",
            pair.Value ? "✓" : "!",
            ResourceBrush(pair.Value ? "SuccessBrush" : "DangerBrush"))).ToList();
    }

    private ConfigureRequest ReadConfigureRequest()
    {
        var displayName = DisplayNameBox.Text.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            throw new AtlWslException("invalid_display_name", "Display name cannot be empty.");
        }

        int? width = ParseDimension(WidthBox.Text, "width");
        int? height = ParseDimension(HeightBox.Text, "height");
        if (width.HasValue != height.HasValue)
        {
            throw new AtlWslException("invalid_resolution", "Set both width and height, or leave both empty.");
        }

        return new ConfigureRequest(
            displayName,
            width,
            height,
            ActivityBox.Text.Trim(),
            FullscreenBox.IsChecked == true,
            WebViewBox.IsChecked == true,
            ValidateCertificatesBox.IsChecked == true,
            DirectEglBox.IsChecked == true,
            LocationBox.IsChecked == true);
    }

    private static int? ParseDimension(string input, string label)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (!int.TryParse(input, out var value) || value is < 64 or > 8192)
        {
            throw new AtlWslException("invalid_resolution", $"The {label} must be between 64 and 8192.");
        }

        return value;
    }

    private async Task RunUiAsync(Func<Task> action, string? successMessage = null)
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        MainContent.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible;
        SetStatus("Working…");
        try
        {
            await action();
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                SetStatus(successMessage);
            }
        }
        catch (AtlWslException exception)
        {
            ConnectionStatusText.Text = "Needs attention";
            ConnectionDot.Fill = ResourceBrush("WarningBrush");
            ShowError(exception.Message, exception.Code);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message, "unexpected_error");
        }
        finally
        {
            BusyProgress.Visibility = Visibility.Collapsed;
            MainContent.IsEnabled = true;
            isBusy = false;
        }
    }

    private void ShowError(string message, string code)
    {
        SetStatus($"{code}: {message}");
        MessageBox.Show(this, message, "ATL-WSL", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private static string FriendlyCheckName(string value) => value switch
    {
        "wslgMount" => "WSLg mount",
        "wayland" => "Wayland display",
        "pulseAudio" => "PulseAudio bridge",
        "atlBinary" => "ATL executable",
        "sandboxBinary" => "App filesystem sandbox",
        "softwareRenderer" => "Mesa software renderer",
        _ => value,
    };

    private static string? ReadReleaseValue(IReadOnlyDictionary<string, JsonElement> release, string name) =>
        release.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadDistributionArgument()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], "--distro", StringComparison.OrdinalIgnoreCase)
                && IsValidDistributionName(arguments[index + 1]))
            {
                return arguments[index + 1].Trim();
            }
        }

        return null;
    }

    private static bool IsValidDistributionName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 64
        && name.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ');

    private sealed record HealthCheckView(string Name, string Status, string Glyph, Brush Brush);
}
