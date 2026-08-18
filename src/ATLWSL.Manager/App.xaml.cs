using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AtlWsl.Core;

namespace AtlWsl.Manager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--verify-manifest", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var manifestPath = Path.GetFullPath(e.Args[1]);
                var signaturePath = e.Args.Length >= 3 ? Path.GetFullPath(e.Args[2]) : manifestPath + ".sig";
                var bytes = File.ReadAllBytes(manifestPath);
                using var manifestStream = new MemoryStream(bytes, writable: false);
                var manifest = ReleaseManifest.LoadAsync(manifestStream).GetAwaiter().GetResult();
                using var keyRing = typeof(App).Assembly.GetManifestResourceStream("ATLWSL.ReleasePublicKeys.json")
                    ?? throw new InvalidDataException("Embedded release key ring is missing.");
                ManifestSignatureVerifier.Load(keyRing).RequireValid(bytes, File.ReadAllText(signaturePath), manifest);
                if (manifest.Validate(release: true).Count > 0) throw new InvalidDataException("Release manifest contract validation failed.");
                RequireSupportedManager(manifest);
                Shutdown(0);
            }
            catch
            {
                Shutdown(2);
            }
            return;
        }
        if (e.Args.Length >= 4 && string.Equals(e.Args[0], "--headless", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var operation = e.Args[1].ToLowerInvariant();
                var manifestPath = Path.GetFullPath(e.Args[2]);
                var signaturePath = Path.GetFullPath(e.Args[3]);
                var bytes = File.ReadAllBytes(manifestPath);
                using var manifestStream = new MemoryStream(bytes, writable: false);
                var manifest = ReleaseManifest.LoadAsync(manifestStream).GetAwaiter().GetResult();
                using var keyRing = typeof(App).Assembly.GetManifestResourceStream("ATLWSL.ReleasePublicKeys.json")
                    ?? throw new InvalidDataException("Embedded release key ring is missing.");
                var verifier = ManifestSignatureVerifier.Load(keyRing);
                verifier.RequireValid(bytes, File.ReadAllText(signaturePath), manifest);
                if (manifest.Validate(release: true).Count > 0) throw new InvalidDataException("Release manifest contract validation failed.");
                RequireSupportedManager(manifest);
                var lifecycle = new LifecycleService(verifier);
                lifecycle.RecoverInterruptedOperationAsync().GetAwaiter().GetResult();
                switch (operation)
                {
                    case "install" when e.Args.Length >= 6:
                        lifecycle.InstallAsync(manifest, e.Args[4], e.Args[5]).GetAwaiter().GetResult();
                        break;
                    case "update":
                        lifecycle.UpdateAsync(manifest).GetAwaiter().GetResult();
                        break;
                    case "repair":
                        lifecycle.RepairAsync(manifest).GetAwaiter().GetResult();
                        break;
                    case "uninstall" when e.Args.Length >= 6:
                        lifecycle.UninstallAsync(bool.Parse(e.Args[4]), e.Args[5], e.Args.Length > 6 ? e.Args[6] : null)
                            .GetAwaiter().GetResult();
                        break;
                    default:
                        throw new ArgumentException("Unsupported headless operation.");
                }
                Shutdown(0);
            }
            catch
            {
                Shutdown(2);
            }
            return;
        }
        ApplySystemTheme();
        base.OnStartup(e);
    }

    private void ApplySystemTheme()
    {
        Resources["AccentBrush"] = SystemParameters.WindowGlassBrush;
        Resources["AccentHoverBrush"] = SystemParameters.WindowGlassBrush;
        if (SystemParameters.HighContrast)
        {
            Resources["WindowBrush"] = SystemColors.WindowBrush;
            Resources["SurfaceBrush"] = SystemColors.ControlBrush;
            Resources["SurfaceAltBrush"] = SystemColors.ControlBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedTextBrush"] = SystemColors.GrayTextBrush;
            Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            Resources["AccentBrush"] = SystemColors.HighlightBrush;
            Resources["AccentHoverBrush"] = SystemColors.HighlightBrush;
            Resources["AccentSoftBrush"] = SystemColors.ControlBrush;
            return;
        }
        var usesLightTheme = true;
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            usesLightTheme = (int?)personalize?.GetValue("AppsUseLightTheme") != 0;
        }
        catch (System.Security.SecurityException)
        {
            // Keep the accessible light palette when the preference is unavailable.
        }

        if (usesLightTheme)
        {
            return;
        }

        Resources["WindowBrush"] = Brush("#10131A");
        Resources["SurfaceBrush"] = Brush("#191D26");
        Resources["SurfaceAltBrush"] = Brush("#222834");
        Resources["TextBrush"] = Brush("#F4F7FB");
        Resources["MutedTextBrush"] = Brush("#AAB4C3");
        Resources["BorderBrush"] = Brush("#343C4A");
        Resources["AccentSoftBrush"] = Brush("#17345B");
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private static void RequireSupportedManager(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion == 2 && Version.TryParse(manifest.MinimumManagerVersion, out var required) &&
            (typeof(App).Assembly.GetName().Version ?? new Version()) < required)
            throw new InvalidOperationException($"Manager {manifest.MinimumManagerVersion} or newer is required.");
    }
}
