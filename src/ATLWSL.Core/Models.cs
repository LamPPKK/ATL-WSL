using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlWsl.Core;

public sealed record ApiEnvelope<T>(
    int SchemaVersion,
    string? Product,
    bool Ok,
    string Command,
    T? Data,
    IReadOnlyList<string>? Warnings,
    ApiError? Error);

public sealed record ApiError(string Code, string Message, JsonElement? Details);

public sealed record AppList(IReadOnlyList<AppInfo> Apps);

public sealed record AppContainer(AppInfo App);

public sealed record AddAppResult(AppInfo App, bool Restored, bool AlreadyInstalled);

public sealed record AppInfo(
    string Id,
    string DisplayName,
    string SourceFileName,
    string Sha256,
    long Size,
    IReadOnlyList<string> NativeAbis,
    string HostArchitecture,
    string CompatibilityReason,
    DateTimeOffset InstalledAt,
    DateTimeOffset? RemovedAt,
    LaunchOptions LaunchOptions,
    bool ApkPresent,
    string DataPath)
{
    [JsonIgnore]
    public string ArchitectureSummary => NativeAbis.Count == 0 ? "Java-only" : string.Join(" · ", NativeAbis);

    [JsonIgnore]
    public string SizeSummary => Size switch
    {
        >= 1_073_741_824 => $"{Size / 1_073_741_824d:0.0} GB",
        >= 1_048_576 => $"{Size / 1_048_576d:0.0} MB",
        >= 1024 => $"{Size / 1024d:0.0} KB",
        _ => $"{Size} B",
    };
}

public sealed record LaunchOptions(
    int? Width,
    int? Height,
    string? Activity,
    bool Fullscreen,
    bool WebView,
    bool ValidateCertificates,
    bool DirectEgl,
    bool Location);

public sealed record ApkInspection(
    string Path,
    string FileName,
    string DisplayName,
    string Sha256,
    long Size,
    IReadOnlyList<string> NativeAbis,
    string HostArchitecture,
    string RequiredAbi,
    bool Compatible,
    string CompatibilityReason);

public sealed record DoctorResult(
    bool Healthy,
    IReadOnlyDictionary<string, bool> Checks,
    IReadOnlyDictionary<string, JsonElement> Release,
    string Architecture,
    string RequiredAbi,
    string Renderer,
    string User,
    string Home,
    string StateRoot,
    IReadOnlyList<string> Warnings);

public sealed record LaunchResult(string Id, int? Pid, string LogPath, string Renderer);

public sealed record RemoveResult(string Id, bool DataDeleted, bool Retained, string? DataPath);

public sealed record ExportResult(string Path, long Size);

public sealed record SystemStatus(
    string RuntimeVersion,
    string AlpineVersion,
    string Architecture,
    string ExpectedPackageInventorySha256,
    string ActualPackageInventorySha256,
    bool Drift,
    string AppsRoot,
    bool TransactionPending);

public sealed record ConfigureRequest(
    string DisplayName,
    int? Width,
    int? Height,
    string? Activity,
    bool Fullscreen,
    bool WebView,
    bool ValidateCertificates,
    bool DirectEgl,
    bool Location);
