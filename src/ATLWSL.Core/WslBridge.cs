using System.Text.Json;

namespace AtlWsl.Core;

public sealed class AtlWslException : Exception
{
    public AtlWslException(string code, string message, Exception? innerException = null, JsonElement? details = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonElement? Details { get; }
}

public sealed class WslBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IProcessRunner processRunner;

    public WslBridge(string distributionName = "ATL-WSL", IProcessRunner? processRunner = null)
    {
        DistributionName = distributionName;
        this.processRunner = processRunner ?? new ProcessRunner();
    }

    public string DistributionName { get; }

    public Task<DoctorResult> DoctorAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<DoctorResult>(["doctor"], cancellationToken);

    public Task<SystemStatus> SystemStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<SystemStatus>(["system", "status"], cancellationToken);

    public Task<AppList> ListAppsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<AppList>(["library", "list"], cancellationToken);

    public Task<ApkInspection> InspectAsync(string apkPath, CancellationToken cancellationToken = default) =>
        InvokeAsync<ApkInspection>(["inspect", apkPath], cancellationToken);

    public Task<AddAppResult> AddAsync(string apkPath, CancellationToken cancellationToken = default) =>
        InvokeAsync<AddAppResult>(["library", "add", apkPath], cancellationToken);

    public async Task<AppInfo> ConfigureAsync(
        string appId,
        ConfigureRequest request,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "library", "configure", appId,
            "--display-name", request.DisplayName,
        };

        if (request.Width.HasValue && request.Height.HasValue)
        {
            arguments.AddRange(["--width", request.Width.Value.ToString(), "--height", request.Height.Value.ToString()]);
        }
        else
        {
            arguments.Add("--clear-resolution");
        }

        if (string.IsNullOrWhiteSpace(request.Activity))
        {
            arguments.Add("--clear-activity");
        }
        else
        {
            arguments.AddRange(["--activity", request.Activity.Trim()]);
        }

        AddBoolean(arguments, "fullscreen", request.Fullscreen);
        AddBoolean(arguments, "web-view", request.WebView);
        AddBoolean(arguments, "validate-certificates", request.ValidateCertificates);
        AddBoolean(arguments, "direct-egl", request.DirectEgl);
        AddBoolean(arguments, "location", request.Location);
        var result = await InvokeAsync<AppContainer>(arguments, cancellationToken).ConfigureAwait(false);
        return result.App;
    }

    public Task<LaunchResult> LaunchAsync(string appId, CancellationToken cancellationToken = default) =>
        InvokeAsync<LaunchResult>(["launch", appId], cancellationToken);

    public Task<RemoveResult> RemoveAsync(
        string appId,
        bool deleteData,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "library", "remove", appId };
        if (deleteData)
        {
            arguments.Add("--delete-data");
        }

        return InvokeAsync<RemoveResult>(arguments, cancellationToken);
    }

    public Task<ExportResult> ExportDiagnosticsAsync(
        string outputPath,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ExportResult>(["logs", "export", outputPath], cancellationToken);

    private static void AddBoolean(ICollection<string> arguments, string name, bool value) =>
        arguments.Add(value ? $"--{name}" : $"--no-{name}");

    private async Task<T> InvokeAsync<T>(
        IReadOnlyList<string> atlArguments,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-d", DistributionName, "--exec", "atl-wsl", "--json" };
        arguments.AddRange(atlArguments);
        var result = await processRunner.RunAsync("wsl.exe", arguments, cancellationToken).ConfigureAwait(false);

        ApiEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(result.StandardOutput.Trim(), JsonOptions);
        }
        catch (JsonException exception)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? "ATL-WSL returned an unreadable response."
                : result.StandardError.Trim();
            throw new AtlWslException("invalid_response", message, exception);
        }

        if (envelope is null)
        {
            throw new AtlWslException("empty_response", "ATL-WSL did not return a response.");
        }

        if (envelope.SchemaVersion != 1)
        {
            throw new AtlWslException(
                "unsupported_schema",
                $"ATL-WSL returned schema {envelope.SchemaVersion}; this manager requires schema 1.");
        }

        if (envelope.Product is not null && !string.Equals(envelope.Product, "atl-wsl", StringComparison.Ordinal))
        {
            throw new AtlWslException("unexpected_product", "The runtime response belongs to another product.");
        }

        if (!string.Equals(envelope.Command, atlArguments[0], StringComparison.Ordinal))
        {
            throw new AtlWslException("unexpected_response", "ATL-WSL returned a response for a different command.");
        }

        if (!envelope.Ok || envelope.Error is not null)
        {
            throw new AtlWslException(
                envelope.Error?.Code ?? "atl_wsl_failed",
                envelope.Error?.Message ?? $"ATL-WSL exited with code {result.ExitCode}.",
                details: envelope.Error?.Details);
        }

        if (result.ExitCode != 0 || envelope.Data is null)
        {
            throw new AtlWslException("atl_wsl_failed", $"ATL-WSL exited with code {result.ExitCode}.");
        }

        return envelope.Data;
    }
}
