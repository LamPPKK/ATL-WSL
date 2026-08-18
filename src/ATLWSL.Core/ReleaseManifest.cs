using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AtlWsl.Core;

public sealed class ReleaseManifest
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "atl-wsl";
    public string Version { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public DateTimeOffset PublishedAtUtc { get; init; }
    public string MinimumManagerVersion { get; init; } = string.Empty;
    public string MinimumWslVersion { get; init; } = string.Empty;
    public string SigningKeyId { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Components { get; init; } = new Dictionary<string, string>();
    public ReleaseCompatibility Compatibility { get; init; } = new();
    public IReadOnlyList<ReleaseArtifact> Artifacts { get; init; } = [];

    [JsonIgnore]
    public bool IsDevelopment => Channel == "development";

    public ReleaseArtifact Artifact(string role, string? architecture = null) => Artifacts.Single(value =>
        value.Role == role && value.Architecture == (architecture ?? CurrentArchitecture()));

    public static async Task<ReleaseManifest> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var schema = document.RootElement.GetProperty("schemaVersion").GetInt32();
        if (schema == 2)
        {
            return document.RootElement.Deserialize<ReleaseManifest>(JsonOptions)
                ?? throw new InvalidDataException("Release manifest is empty.");
        }
        if (schema != 1)
        {
            throw new InvalidDataException($"Unsupported release manifest schema: {schema}.");
        }
        var legacy = document.RootElement.Deserialize<LegacyManifest>(JsonOptions)
            ?? throw new InvalidDataException("Release manifest is empty.");
        var artifacts = new List<ReleaseArtifact>();
        foreach (var (architecture, artifact) in legacy.Artifacts)
        {
            artifacts.Add(artifact.ToV2("rootfs", architecture));
        }
        foreach (var (architecture, artifact) in legacy.Manager.Artifacts)
        {
            artifacts.Add(artifact.ToV2("manager", architecture));
        }
        return new ReleaseManifest
        {
            SchemaVersion = 1,
            Version = legacy.Version,
            Channel = "stable",
            MinimumManagerVersion = legacy.Manager.Version,
            MinimumWslVersion = legacy.MinimumWslVersion,
            Components = legacy.Sources,
            Compatibility = new ReleaseCompatibility { SupportedArchitectures = legacy.Artifacts.Keys.ToArray() },
            Artifacts = artifacts,
        };
    }

    public IReadOnlyList<string> Validate(bool release)
    {
        List<string> errors = [];
        if (SchemaVersion is not (1 or 2)) errors.Add("Unsupported schemaVersion.");
        if (Product != "atl-wsl") errors.Add("Manifest product must be atl-wsl.");
        if (!System.Version.TryParse(Version.Split('-')[0], out _)) errors.Add("Manifest version is invalid.");
        if (SchemaVersion == 2 && (!System.Version.TryParse(MinimumManagerVersion, out _) ||
            !System.Version.TryParse(MinimumWslVersion, out _))) errors.Add("Minimum versions are invalid.");
        if (SchemaVersion == 2 && !IsDevelopment && string.IsNullOrWhiteSpace(SigningKeyId)) errors.Add("signingKeyId is required.");
        var architectures = Compatibility.SupportedArchitectures.Distinct().ToArray();
        if (architectures.Length == 0 || architectures.Any(value => value is not ("x64" or "arm64"))) errors.Add("Architecture set is invalid.");
        if (SchemaVersion == 2 && (!Components.TryGetValue("alpine", out var alpine) || alpine != "3.24.1")) errors.Add("Alpine baseline must be 3.24.1.");
        foreach (var architecture in architectures)
        {
            foreach (var role in new[] { "rootfs", "manager", "runtimeBundle" })
            {
                if (SchemaVersion == 1 && role == "runtimeBundle") continue;
                if (!Artifacts.Any(value => value.Architecture == architecture && value.Role == role))
                    errors.Add($"Missing {role} artifact for {architecture}.");
            }
        }
        foreach (var artifact in Artifacts)
        {
            if (artifact.Architecture is not ("x64" or "arm64") ||
                string.IsNullOrWhiteSpace(artifact.FileName) || Path.GetFileName(artifact.FileName) != artifact.FileName)
                errors.Add($"Unsafe artifact metadata for {artifact.Role}.");
            if (release && (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !Hashes.IsSha256(artifact.Sha256) || artifact.SizeBytes <= 0))
                errors.Add($"Invalid release integrity metadata for {artifact.Role}/{artifact.Architecture}.");
        }
        if (release && Channel != "stable") errors.Add("Release channel must be stable.");
        return errors;
    }

    public static string CurrentArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var value => throw new PlatformNotSupportedException($"Unsupported architecture: {value}.")
    };

    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

public sealed class ReleaseCompatibility
{
    public IReadOnlyList<string> SupportedArchitectures { get; init; } = [];
    public string Renderer { get; init; } = "llvmpipe";
    public string Tier { get; init; } = "experimental-runtime";
}

public sealed class ReleaseArtifact
{
    public string Role { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string MediaType { get; init; } = "application/octet-stream";
}

public static partial class Hashes
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Regex();
    public static bool IsSha256(string value) => Sha256Regex().IsMatch(value ?? string.Empty);
}

internal sealed class LegacyManifest
{
    public string Version { get; init; } = string.Empty;
    public string MinimumWslVersion { get; init; } = string.Empty;
    public LegacyManager Manager { get; init; } = new();
    public Dictionary<string, LegacyArtifact> Artifacts { get; init; } = [];
    public Dictionary<string, string> Sources { get; init; } = [];
}

internal sealed class LegacyManager
{
    public string Version { get; init; } = string.Empty;
    [JsonExtensionData]
    public Dictionary<string, JsonElement> RawArtifacts { get; init; } = [];
    [JsonIgnore]
    public Dictionary<string, LegacyArtifact> Artifacts => RawArtifacts
        .Where(value => value.Key is "x64" or "arm64")
        .ToDictionary(value => value.Key, value => value.Value.Deserialize<LegacyArtifact>(ReleaseManifest.JsonOptions)!);
}

internal sealed class LegacyArtifact
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public ReleaseArtifact ToV2(string role, string architecture) => new()
    {
        Role = role,
        Architecture = architecture,
        FileName = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : $"{role}-{architecture}",
        Url = Url,
        Sha256 = Sha256,
        SizeBytes = Size,
    };
}
