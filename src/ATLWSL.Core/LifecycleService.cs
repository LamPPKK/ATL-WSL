using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtlWsl.Core;

public static class ManagerLifecycleState
{
    public const string NotInstalled = "notInstalled";
    public const string Installing = "installing";
    public const string Installed = "installed";
    public const string UpdateAvailable = "updateAvailable";
    public const string Updating = "updating";
    public const string Degraded = "degraded";
    public const string Repairing = "repairing";
    public const string Uninstalling = "uninstalling";

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (NotInstalled, Installing) => true,
        (Installing, Installed or NotInstalled or Degraded) => true,
        (Installed, UpdateAvailable or Updating or Repairing or Uninstalling or Degraded) => true,
        (UpdateAvailable, Installed or Updating or Repairing or Uninstalling) => true,
        (Updating, Installed or Degraded) => true,
        (Degraded, Repairing or Uninstalling) => true,
        (Repairing, Installed or Degraded) => true,
        (Uninstalling, Installed or NotInstalled or Degraded) => true,
        _ => from == to,
    };
}

public sealed class LifecycleRecord
{
    public int SchemaVersion { get; init; } = 2;
    public string State { get; set; } = ManagerLifecycleState.NotInstalled;
    public string DistributionName { get; set; } = "ATL-WSL";
    public string InstallLocation { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public LifecycleTransaction? Transaction { get; set; }
    public string LastError { get; set; } = string.Empty;
}

public sealed class LifecycleTransaction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Operation { get; init; } = string.Empty;
    public string Stage { get; set; } = "started";
    public string FromVersion { get; init; } = string.Empty;
    public string ToVersion { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class LifecycleService
{
    private readonly HttpClient httpClient;
    private readonly IProcessRunner runner;
    private readonly ManifestSignatureVerifier verifier;
    private readonly string stateFile;
    private readonly string cacheDirectory;

    public LifecycleService(
        ManifestSignatureVerifier verifier,
        HttpClient? httpClient = null,
        IProcessRunner? runner = null,
        string? stateRoot = null)
    {
        this.verifier = verifier;
        this.httpClient = httpClient ?? new HttpClient();
        this.runner = runner ?? new ProcessRunner();
        stateRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATL-WSL");
        stateFile = Path.Combine(stateRoot, "manager-state.json");
        cacheDirectory = Path.Combine(stateRoot, "cache");
    }

    public async Task<ReleaseManifest> LoadReleaseAsync(Uri manifestUri, CancellationToken cancellationToken = default)
    {
        if (manifestUri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Manifest URL must use HTTPS.");
        var bytes = await httpClient.GetByteArrayAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        var signature = await httpClient.GetStringAsync(new Uri(manifestUri.AbsoluteUri + ".sig"), cancellationToken).ConfigureAwait(false);
        await using var stream = new MemoryStream(bytes, writable: false);
        var manifest = await ReleaseManifest.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        verifier.RequireValid(bytes, signature, manifest);
        var errors = manifest.Validate(release: true);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (manifest.SchemaVersion == 2 && Version.TryParse(manifest.MinimumManagerVersion, out var requiredManager) &&
            (typeof(LifecycleService).Assembly.GetName().Version ?? new Version()) < requiredManager)
            throw new InvalidOperationException($"Manager {manifest.MinimumManagerVersion} or newer is required.");
        return manifest;
    }

    public async Task<LifecycleRecord> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(stateFile)) return new LifecycleRecord();
        await using var stream = File.OpenRead(stateFile);
        return await JsonSerializer.DeserializeAsync<LifecycleRecord>(stream, ReleaseManifest.JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new LifecycleRecord();
    }

    public async Task RecoverInterruptedOperationAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var transaction = state.Transaction;
        if (transaction is null) return;
        switch (transaction.Operation)
        {
            case "install":
                if (await DistributionExistsAsync(state.DistributionName, cancellationToken).ConfigureAwait(false))
                {
                    var rollback = await runner.RunAsync("wsl.exe", ["--unregister", state.DistributionName], cancellationToken)
                        .ConfigureAwait(false);
                    state.State = rollback.ExitCode == 0 ? ManagerLifecycleState.NotInstalled : ManagerLifecycleState.Degraded;
                }
                else
                {
                    state.State = ManagerLifecycleState.NotInstalled;
                }
                break;
            case "update":
                var updateRollback = await runner.RunAsync("wsl.exe", ["-d", state.DistributionName, "-u", "root", "--exec",
                    "/usr/libexec/atl-wsl-system", "update-rollback"], cancellationToken).ConfigureAwait(false);
                state.State = updateRollback.ExitCode == 0 ? ManagerLifecycleState.Installed : ManagerLifecycleState.Degraded;
                break;
            case "uninstall":
                state.State = await DistributionExistsAsync(state.DistributionName, cancellationToken).ConfigureAwait(false)
                    ? ManagerLifecycleState.Installed
                    : ManagerLifecycleState.NotInstalled;
                break;
            default:
                state.State = ManagerLifecycleState.Degraded;
                break;
        }
        transaction.Stage = state.State == ManagerLifecycleState.Degraded ? "recovery-failed" : "rolled-back-after-restart";
        state.LastError = state.State == ManagerLifecycleState.Degraded
            ? "An interrupted lifecycle operation requires repair."
            : string.Empty;
        state.Transaction = state.State == ManagerLifecycleState.Degraded ? transaction : null;
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckForUpdateAsync(ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if ((state.State is ManagerLifecycleState.Installed or ManagerLifecycleState.UpdateAvailable) &&
            Version.TryParse(manifest.Version, out var available) && Version.TryParse(state.Version, out var current))
        {
            state.State = available > current ? ManagerLifecycleState.UpdateAvailable : ManagerLifecycleState.Installed;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InstallAsync(
        ReleaseManifest manifest,
        string distributionName,
        string installLocation,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDistributionName(distributionName);
        EnsureWindows11();
        await EnsureWslVersionAsync(manifest.MinimumWslVersion, cancellationToken).ConfigureAwait(false);
        if (await DistributionExistsAsync(distributionName, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"A WSL distribution named {distributionName} already exists.");
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        Transition(state, ManagerLifecycleState.Installing);
        state.DistributionName = distributionName;
        state.InstallLocation = Path.GetFullPath(installLocation);
        var transaction = new LifecycleTransaction { Operation = "install", ToVersion = manifest.Version };
        state.Transaction = transaction;
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        var imported = false;
        try
        {
            var rootfs = await DownloadAsync(manifest.Artifact("rootfs"), progress, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(state.InstallLocation);
            progress?.Report("Importing the verified Alpine 3.24 artifact...");
            await RequireSuccessAsync(["--install", "--from-file", rootfs, "--name", distributionName,
                "--location", state.InstallLocation, "--no-launch"], cancellationToken).ConfigureAwait(false);
            imported = true;
            transaction.Stage = "distro-imported";
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(["-d", distributionName, "-u", "root", "--exec", "/usr/libexec/atl-wsl-oobe"], cancellationToken)
                .ConfigureAwait(false);
            await InstallManifestAsync(distributionName, manifest, cancellationToken).ConfigureAwait(false);
            await RequireHealthyAsync(distributionName, cancellationToken).ConfigureAwait(false);
            state.Version = manifest.Version;
            state.State = ManagerLifecycleState.Installed;
            state.Transaction = null;
            state.LastError = string.Empty;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var rollbackFailed = false;
            if (imported)
            {
                var rollback = await runner.RunAsync("wsl.exe", ["--unregister", distributionName], cancellationToken).ConfigureAwait(false);
                rollbackFailed = rollback.ExitCode != 0;
            }
            state.State = rollbackFailed ? ManagerLifecycleState.Degraded : ManagerLifecycleState.NotInstalled;
            state.LastError = exception.Message;
            if (state.Transaction is not null) state.Transaction.Stage = rollbackFailed ? "rollback-failed" : "rolled-back";
            if (!rollbackFailed) state.Transaction = null;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateAsync(ReleaseManifest manifest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        Transition(state, ManagerLifecycleState.Updating);
        var bundle = await DownloadAsync(manifest.Artifact("runtimeBundle"), progress, cancellationToken).ConfigureAwait(false);
        var transaction = new LifecycleTransaction { Operation = "update", FromVersion = state.Version, ToVersion = manifest.Version };
        state.Transaction = transaction;
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            var linuxBundle = await ConvertPathAsync(state.DistributionName, bundle, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(["-d", state.DistributionName, "-u", "root", "--exec", "/usr/libexec/atl-wsl-system", "update", linuxBundle], cancellationToken).ConfigureAwait(false);
            transaction.Stage = "awaiting-doctor";
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            await RequireHealthyAsync(state.DistributionName, cancellationToken).ConfigureAwait(false);
            await InstallManifestAsync(state.DistributionName, manifest, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(["-d", state.DistributionName, "-u", "root", "--exec", "/usr/libexec/atl-wsl-system", "update-commit"], cancellationToken).ConfigureAwait(false);
            state.Version = manifest.Version;
            state.State = ManagerLifecycleState.Installed;
            state.Transaction = null;
            state.LastError = string.Empty;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var rollback = await runner.RunAsync("wsl.exe", ["-d", state.DistributionName, "-u", "root", "--exec",
                "/usr/libexec/atl-wsl-system", "update-rollback"], cancellationToken).ConfigureAwait(false);
            state.State = rollback.ExitCode == 0 ? ManagerLifecycleState.Installed : ManagerLifecycleState.Degraded;
            state.LastError = exception.Message;
            if (state.Transaction is not null) state.Transaction.Stage = rollback.ExitCode == 0 ? "rolled-back" : "rollback-failed";
            if (rollback.ExitCode == 0) state.Transaction = null;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RepairAsync(ReleaseManifest manifest, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        Transition(state, ManagerLifecycleState.Repairing);
        state.Transaction = new LifecycleTransaction { Operation = "repair", FromVersion = state.Version, ToVersion = state.Version };
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireSuccessAsync(["-d", state.DistributionName, "-u", "root", "--exec", "/usr/libexec/atl-wsl-system", "repair"], cancellationToken).ConfigureAwait(false);
            await RequireHealthyAsync(state.DistributionName, cancellationToken).ConfigureAwait(false);
            await RestoreInstalledManifestAsync(state, manifest, cancellationToken).ConfigureAwait(false);
            state.State = ManagerLifecycleState.Installed;
            state.Transaction = null;
            state.LastError = string.Empty;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            state.State = ManagerLifecycleState.Degraded;
            state.LastError = exception.Message;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UninstallAsync(bool removeData, string confirmation, string? exportPath, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (removeData && confirmation != state.DistributionName)
            throw new InvalidOperationException($"Type {state.DistributionName} exactly to confirm removal.");
        Transition(state, ManagerLifecycleState.Uninstalling);
        var transaction = new LifecycleTransaction
        {
            Operation = "uninstall",
            FromVersion = state.Version,
            Stage = "started",
        };
        state.Transaction = transaction;
        await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            if (removeData)
            {
                if (!string.IsNullOrWhiteSpace(exportPath))
                {
                    transaction.Stage = "exporting";
                    await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                    await RequireSuccessAsync(["--export", state.DistributionName, exportPath, "--vhd"], cancellationToken).ConfigureAwait(false);
                }
                transaction.Stage = "unregistering";
                await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                await RequireSuccessAsync(["--unregister", state.DistributionName], cancellationToken).ConfigureAwait(false);
            }
            state.State = removeData ? ManagerLifecycleState.NotInstalled : ManagerLifecycleState.Installed;
            state.Transaction = null;
            state.LastError = string.Empty;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            state.State = ManagerLifecycleState.Degraded;
            state.LastError = exception.Message;
            transaction.Stage = "failed";
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> DownloadAsync(ReleaseArtifact artifact, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Hashes.IsSha256(artifact.Sha256) || artifact.SizeBytes <= 0 ||
            !Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("Artifact integrity metadata is invalid.");
        Directory.CreateDirectory(cacheDirectory);
        var destination = Path.Combine(cacheDirectory, artifact.FileName);
        if (File.Exists(destination) && await VerifyFileAsync(destination, artifact, cancellationToken).ConfigureAwait(false)) return destination;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!);
        if (drive.AvailableFreeSpace < artifact.SizeBytes + 256L * 1024 * 1024)
            throw new IOException($"Insufficient free space to stage {artifact.FileName} safely.");
        progress?.Report($"Downloading {artifact.Role} for {artifact.Architecture}...");
        var temporary = destination + ".part";
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(temporary)) await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (!await VerifyFileAsync(temporary, artifact, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporary);
            throw new InvalidDataException($"Integrity verification failed for {artifact.FileName}.");
        }
        File.Move(temporary, destination, true);
        return destination;
    }

    private static async Task<bool> VerifyFileAsync(string path, ReleaseArtifact artifact, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != artifact.SizeBytes) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InstallManifestAsync(string distributionName, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, $"release-{manifest.Version}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ReleaseManifest.JsonOptions), cancellationToken).ConfigureAwait(false);
        var linuxPath = await ConvertPathAsync(distributionName, path, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(["-d", distributionName, "-u", "root", "--exec", "install", "-m", "0644", linuxPath, "/etc/atl-wsl-release"], cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreInstalledManifestAsync(LifecycleRecord state, ReleaseManifest available, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheDirectory, $"release-{state.Version}.json");
        if (!File.Exists(path))
        {
            if (!string.Equals(state.Version, available.Version, StringComparison.Ordinal))
                throw new InvalidDataException($"Cached release metadata for {state.Version} is unavailable; update instead of relabeling the runtime.");
            await InstallManifestAsync(state.DistributionName, available, cancellationToken).ConfigureAwait(false);
            return;
        }
        var linuxPath = await ConvertPathAsync(state.DistributionName, path, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(["-d", state.DistributionName, "-u", "root", "--exec", "install", "-m", "0644", linuxPath,
            "/etc/atl-wsl-release"], cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ConvertPathAsync(string distro, string path, CancellationToken cancellationToken)
    {
        var result = await RequireSuccessAsync(["-d", distro, "--exec", "wslpath", "-u", path], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Replace("\0", string.Empty).Trim();
    }

    private async Task RequireHealthyAsync(string distro, CancellationToken cancellationToken)
    {
        var result = await RequireSuccessAsync(["-d", distro, "--exec", "atl-wsl", "--json", "doctor"], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (!document.RootElement.GetProperty("ok").GetBoolean() || !document.RootElement.GetProperty("data").GetProperty("healthy").GetBoolean())
            throw new InvalidOperationException("ATL-WSL doctor did not pass.");
    }

    private async Task<ProcessResult> RequireSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("wsl.exe", arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError.Trim());
        return result;
    }

    private async Task<bool> DistributionExistsAsync(string name, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("wsl.exe", ["--list", "--quiet"], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Replace("\0", string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureWslVersionAsync(string minimumVersion, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("wsl.exe", ["--version"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new PlatformNotSupportedException("Store WSL is required.");
        var match = Regex.Match(result.StandardOutput + result.StandardError, @"\b\d+\.\d+\.\d+(?:\.\d+)?\b");
        if (!match.Success || !Version.TryParse(match.Value, out var installed) ||
            !Version.TryParse(minimumVersion, out var minimum) || installed < minimum)
            throw new PlatformNotSupportedException($"WSL {minimumVersion} or newer is required.");
    }

    private async Task SaveStateAsync(LifecycleRecord state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var temporary = stateFile + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, ReleaseManifest.JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, stateFile, true);
    }

    private static void EnsureWindows11()
    {
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 22000)
            throw new PlatformNotSupportedException("ATL-WSL stable requires Windows 11.");
    }

    private static void ValidateDistributionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(character =>
            !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-' or ' ')))
            throw new ArgumentException("Distribution name is invalid.");
    }

    private static void Transition(LifecycleRecord state, string next)
    {
        if (!ManagerLifecycleState.CanTransition(state.State, next))
            throw new InvalidOperationException($"Lifecycle transition {state.State} -> {next} is not allowed.");
        state.State = next;
    }
}
