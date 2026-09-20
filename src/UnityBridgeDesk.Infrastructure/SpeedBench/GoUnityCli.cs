using System.Globalization;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class GoUnityCli
{
    public static string InstancesDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-cli", "instances");
    public static string Profile(string root) => Path.Combine(root, "go-cli-profile");
    public static Dictionary<string, string> EnvironmentFor(string root) => new()
    {
        ["USERPROFILE"] = Profile(root), ["HOME"] = Profile(root),
        ["TEMP"] = Path.Combine(root, "temp"), ["TMP"] = Path.Combine(root, "temp")
    };
    public static async Task Inspect(string cli, string root, GoUnityToolchain tool, bool usesExec, CancellationToken ct)
    {
        foreach (string directory in EnvironmentFor(root).Values.Distinct()) { SpeedFiles.Regular(directory); Directory.CreateDirectory(directory); }
        await SpeedFiles.Write(Path.Combine(root, "go-cli-environment.json"), new
        {
            environment = EnvironmentFor(root), tool,
            discovery = "owned ready instance snapshot; CLI still performs live health and version checks; original heartbeat validated before/after measurement",
            updatePolicy = "fresh version-check cache per batch; upstream one-hour cache policy applies",
            execInput = "identical C# source via stdin (Bridge/Pipeline use a file); native compiler discovery"
        }, ct);
        var runner = new TimedProcessRunner();
        foreach (var (name, args) in new[] { ("go-cli-version.txt", new[] { "version" }), ("go-cli-help.txt", new[] { "help" }), ("go-cli-exec-help.txt", new[] { "exec", "--help" }) })
        {
            if (!usesExec && name == "go-cli-exec-help.txt") continue;
            var response = await runner.RunAsync(new(Guid.NewGuid(), cli, [..args], root, Environment: EnvironmentFor(root), OutputCodePage: 65001), TimeSpan.FromSeconds(30), cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(root, name), response.Output + response.Error, ct);
            bool valid = response.Outcome == ProcessOutcome.Exited && response.ExitCode == 0 && (name switch
            {
                "go-cli-version.txt" => response.Output.Trim() == "unity-cli v" + tool.CliVersion || response.Output.Trim() == "unity-cli " + tool.CliVersion,
                "go-cli-help.txt" => new[] { "--project", "--timeout", "--params" }.All(response.Output.Contains),
                _ => response.Output.Contains("stdin", StringComparison.OrdinalIgnoreCase)
            });
            if (!valid) throw new SpeedMeasurementException("bench-incompatible", "Go CLI의 버전·프로젝트 선택·명령 입력 방식을 확인하지 못했습니다: " + name);
        }
    }
    // The Go CLI has no --instances-dir/--no-update-check. Give only this verified target
    // to its private home. It cannot scan/delete the user's other instance or cache files.
    // Live health, matching versions and the complete CLI process remain in the timed path.
    public static async Task PrepareConnection(string root, BridgeInstance instance, GoUnityToolchain tool, CancellationToken ct)
    {
        if (instance.ConnectorVersion != tool.ConnectorVersion || instance.State != "ready" || instance.CompileErrors)
            throw new SpeedMeasurementException("response-mismatch", "Go Connector의 버전·준비 상태가 바뀌었습니다.");
        string directory = Path.Combine(Profile(root), ".unity-cli");
        await SpeedFiles.Write(Path.Combine(directory, "instances", "desk.json"), new
        {
            projectPath = instance.ProjectPath, pid = instance.Pid, port = instance.Port, state = instance.State,
            unityVersion = instance.UnityVersion, connectorVersion = instance.ConnectorVersion,
            timestamp = instance.Timestamp, compileErrors = instance.CompileErrors
        }, ct);
        await SpeedFiles.Write(Path.Combine(directory, "version-check.json"), new { checked_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), latest = tool.ReleaseTag, outdated = false }, ct);
    }
    public static string[] Arguments(string project, int timeoutSeconds, string command, string parameters, bool execStdin = false)
    {
        string[] prefix = ["--project", project, "--timeout", checked(timeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture)];
        if (execStdin) return [..prefix, "exec"];
        if (command == "desk_probe") return [..prefix, "desk_probe", "--params", parameters];
        if (command == "exec")
        {
            using var json = JsonDocument.Parse(parameters);
            if (json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return [..prefix, "exec", "--params", parameters];
        }
        throw new SpeedMeasurementException("bench-incompatible", "Go CLI와 동일 작업 대응이 정의되지 않은 명령입니다: " + command);
    }
    public static void ValidateScenario(GuestRequest request)
    {
        if (request.Trial.Experiment != "S01") return;
        var scenario = (request.Options.StressCommands ?? SpeedStress.CommonCommands).Single(c => c.Id == request.Trial.Variant);
        _ = Arguments("validation-only", request.Options.TimeoutSeconds, scenario.Command, scenario.ParametersJson);
        if (scenario.ExpectedEditorState != "ready") throw new SpeedMeasurementException("bench-incompatible", "Go 비교의 부하 시험은 편집 준비 상태의 공통 작업만 지원합니다.");
    }
    public static JsonElement ReadData(ProcessResult response)
    {
        if (response.Outcome != ProcessOutcome.Exited) throw new SpeedMeasurementException(SpeedFailure.ProcessKind(response.Outcome), "Go CLI " + response.Outcome);
        if (response.ExitCode != 0)
        {
            string error = response.Error[..Math.Min(response.Error.Length, 1600)];
            string kind = error.Contains("Compile error:", StringComparison.OrdinalIgnoreCase) ? "compile-error" :
                error.Contains("Runtime error:", StringComparison.OrdinalIgnoreCase) ? "runtime-error" :
                error.Contains("version mismatch", StringComparison.OrdinalIgnoreCase) ? "bench-incompatible" :
                error.Contains("Unknown command", StringComparison.OrdinalIgnoreCase) ? "unsupported-command" :
                error.Contains("no Unity instance", StringComparison.OrdinalIgnoreCase) ? "instance-unavailable" : "cli-error";
            throw new SpeedMeasurementException(kind, $"Go CLI 종료 코드 {response.ExitCode} / {error}");
        }
        try { using var json = JsonDocument.Parse(response.Output); return json.RootElement.Clone(); }
        catch (JsonException ex) { throw new SpeedMeasurementException("invalid-response", "Go CLI 응답 JSON을 확인하지 못했습니다: " + ex.Message); }
    }
    public static async Task CleanInstance(string root, string? directory = null)
    {
        if (!File.Exists(Path.Combine(root, "go-cli-environment.json")) || !File.Exists(Path.Combine(root, "editor-process.json"))) return;
        var identity = await SpeedFiles.Read<LocalProcessIdentity>(Path.Combine(root, "editor-process.json"));
        if (LocalWorkspace.Alive(identity)) throw new IOException("시험 Go Connector의 Unity가 아직 실행 중입니다.");
        directory ??= InstancesDirectory; SpeedFiles.Regular(directory);
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.EnumerateFiles(directory).Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".json.tmp", StringComparison.Ordinal)))
        {
            SpeedFiles.Regular(file); if (new FileInfo(file).Length > 65536) continue;
            bool owned;
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file)); var value = json.RootElement;
                owned = value.TryGetProperty("projectPath", out var project) && project.ValueKind == JsonValueKind.String &&
                    InstanceDiscovery.SamePath(project.GetString()!, Path.Combine(root, "project")) &&
                    value.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out int number) && number == identity.Pid;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { continue; }
            if (owned) File.Delete(file);
        }
    }
}
