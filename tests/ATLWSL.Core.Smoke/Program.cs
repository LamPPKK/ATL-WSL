using AtlWsl.Core;

var emptyListRunner = new FakeRunner("""
    {"schemaVersion":1,"ok":true,"command":"library","data":{"apps":[]},"warnings":[],"error":null}
    """);
var bridge = new WslBridge("ATL Test", emptyListRunner);
var list = await bridge.ListAppsAsync();
Check(list.Apps.Count == 0, "Empty app list was not parsed.");
Check(
    emptyListRunner.LastArguments.SequenceEqual(["-d", "ATL Test", "--exec", "atl-wsl", "--json", "library", "list"]),
    "WSL arguments were not passed as isolated argument-list entries.");

var configureRunner = new FakeRunner("""
    {
      "schemaVersion": 1,
      "ok": true,
      "command": "library",
      "data": {
        "app": {
          "id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "displayName": "Fixture App",
          "sourceFileName": "fixture.apk",
          "sha256": "00",
          "size": 42,
          "nativeAbis": [],
          "hostArchitecture": "x86_64",
          "compatibilityReason": "Java-only APK",
          "installedAt": "2026-01-01T00:00:00Z",
          "removedAt": null,
          "launchOptions": {
            "width": 1280,
            "height": 720,
            "activity": null,
            "fullscreen": true,
            "webView": false,
            "validateCertificates": true,
            "directEgl": false,
            "location": false
          },
          "apkPresent": true,
          "dataPath": "/tmp/data"
        }
      },
      "warnings": [],
      "error": null
    }
    """);
bridge = new WslBridge("ATL-WSL", configureRunner);
var configured = await bridge.ConfigureAsync(
    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    new ConfigureRequest("Fixture App", 1280, 720, null, true, false, true, false, false));
Check(configured.LaunchOptions.Fullscreen, "Configured app response was not parsed.");
Check(configureRunner.LastArguments.Contains("--fullscreen"), "Positive Boolean option was omitted.");
Check(configureRunner.LastArguments.Contains("--no-web-view"), "Negative Boolean option was omitted.");
Check(configureRunner.LastArguments.Contains("--clear-activity"), "Empty activity was not cleared.");

var errorRunner = new FakeRunner("""
    {"schemaVersion":1,"ok":false,"command":"inspect","data":null,"warnings":[],"error":{"code":"incompatible_abi","message":"Wrong ABI","details":null}}
    """, exitCode: 2);
bridge = new WslBridge("ATL-WSL", errorRunner);
try
{
    await bridge.InspectAsync("C:\\fixture.apk");
    throw new InvalidOperationException("Error envelope did not throw.");
}
catch (AtlWslException exception)
{
    Check(exception.Code == "incompatible_abi", "Error code was not preserved.");
}

var signatureVerifier = new ManifestSignatureVerifier(new Dictionary<string, byte[]>
{
    ["test"] = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"),
});
var signature = Convert.FromHexString(
    "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555" +
    "fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
Check(signatureVerifier.Verify([], Convert.ToBase64String(signature), "test"), "Ed25519 signature was not accepted.");
Check(!signatureVerifier.Verify("changed"u8, Convert.ToBase64String(signature), "test"), "Modified manifest bytes were accepted.");
Check(ManagerLifecycleState.CanTransition(ManagerLifecycleState.Installed, ManagerLifecycleState.Updating), "Installed-to-updating transition was rejected.");
Check(!ManagerLifecycleState.CanTransition(ManagerLifecycleState.NotInstalled, ManagerLifecycleState.Updating), "Unsafe lifecycle transition was accepted.");

const string legacyManifestJson = """
    {
      "schemaVersion": 1,
      "version": "0.1.0",
      "minimumWslVersion": "2.4.4",
      "manager": {
        "version": "0.1.0",
        "x64": { "url": "https://example.invalid/manager.zip", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 }
      },
      "artifacts": {
        "x64": { "url": "https://example.invalid/runtime.wsl", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "size": 1 }
      },
      "sources": {}
    }
    """;
await using (var legacyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(legacyManifestJson)))
{
    var legacy = await ReleaseManifest.LoadAsync(legacyStream);
    Check(legacy.SchemaVersion == 1, "Manifest v1 was not normalized.");
    Check(legacy.Artifact("rootfs", "x64").FileName == "runtime.wsl", "Manifest v1 rootfs mapping failed.");
    Check(legacy.Validate(release: false).Count == 0, "Manifest v1 compatibility validation failed.");
}

var recoveryRoot = Path.Combine(Path.GetTempPath(), "atl-wsl-recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(recoveryRoot);
try
{
    var interrupted = new LifecycleRecord
    {
        State = ManagerLifecycleState.Updating,
        DistributionName = "ATL-Recovery",
        Version = "0.1.0",
        Transaction = new LifecycleTransaction { Operation = "update", FromVersion = "0.1.0", ToVersion = "0.2.0" },
    };
    await File.WriteAllTextAsync(Path.Combine(recoveryRoot, "manager-state.json"), System.Text.Json.JsonSerializer.Serialize(interrupted));
    var recoveryRunner = new FakeRunner(string.Empty);
    var recoveryLifecycle = new LifecycleService(signatureVerifier, runner: recoveryRunner, stateRoot: recoveryRoot);
    await recoveryLifecycle.RecoverInterruptedOperationAsync();
    var recovered = await recoveryLifecycle.LoadStateAsync();
    Check(recovered.State == ManagerLifecycleState.Installed && recovered.Transaction is null, "Interrupted update was not rolled back.");
    Check(recoveryRunner.LastArguments.Contains("update-rollback"), "Runtime rollback command was not issued.");
    await recoveryLifecycle.UninstallAsync(removeData: false, confirmation: string.Empty, exportPath: null);
    var retained = await recoveryLifecycle.LoadStateAsync();
    Check(retained.State == ManagerLifecycleState.Installed, "Default uninstall changed retained-distro state.");
    Check(retained.Transaction is null, "Successful default uninstall left a pending transaction.");
}
finally
{
    Directory.Delete(recoveryRoot, recursive: true);
}

var incompatibleSchemaRunner = new FakeRunner("""
    {"schemaVersion":2,"ok":true,"command":"library","data":{"apps":[]},"warnings":[],"error":null}
    """);
bridge = new WslBridge("ATL-WSL", incompatibleSchemaRunner);
try
{
    await bridge.ListAppsAsync();
    throw new InvalidOperationException("Unsupported schema did not throw.");
}
catch (AtlWslException exception)
{
    Check(exception.Code == "unsupported_schema", "Schema mismatch was not rejected.");
}

Console.WriteLine("ATLWSL.Core smoke tests passed.");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class FakeRunner(string response, int exitCode = 0) : IProcessRunner
{
    public IReadOnlyList<string> LastArguments { get; private set; } = [];

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        CheckExecutable(executable);
        cancellationToken.ThrowIfCancellationRequested();
        LastArguments = arguments.ToArray();
        return Task.FromResult(new ProcessResult(exitCode, response, string.Empty));
    }

    private static void CheckExecutable(string executable)
    {
        if (executable != "wsl.exe")
        {
            throw new InvalidOperationException($"Unexpected executable: {executable}");
        }
    }
}
