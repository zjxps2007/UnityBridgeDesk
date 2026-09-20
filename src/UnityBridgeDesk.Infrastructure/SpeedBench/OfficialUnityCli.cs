using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class OfficialUnityCli
{
    public static Dictionary<string, string> EnvironmentFor(string root)
    {
        string profile = Path.Combine(Path.GetFullPath(root), "cli-profile");
        return new()
        {
            ["UNITY_NO_UPDATE_CHECK"] = "1", ["UNITY_NON_INTERACTIVE"] = "1", ["UNITY_NO_BANNER"] = "1",
            ["UNITY_NO_PAGER"] = "1", ["UNITY_NO_CONSENT_PROMPT"] = "1", ["UNITY_NO_CRASH_REPORT"] = "1",
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"), ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"),
            ["USERPROFILE"] = profile, ["HOME"] = profile,
            ["HOMEDRIVE"] = Path.GetPathRoot(profile)!.TrimEnd(Path.DirectorySeparatorChar),
            ["HOMEPATH"] = profile[Path.GetPathRoot(profile)!.TrimEnd(Path.DirectorySeparatorChar).Length..],
            ["XDG_CONFIG_HOME"] = Path.Combine(profile, "config"), ["XDG_CACHE_HOME"] = Path.Combine(profile, "cache"),
            ["XDG_DATA_HOME"] = Path.Combine(profile, "data"), ["XDG_STATE_HOME"] = Path.Combine(profile, "state"),
            ["TEMP"] = Path.Combine(root, "temp"), ["TMP"] = Path.Combine(root, "temp")
        };
    }
    public static async Task Inspect(string cli, string root, OfficialUnityToolchain tool, CancellationToken ct)
    {
        var environment = EnvironmentFor(root);
        foreach (string key in new[] { "APPDATA", "LOCALAPPDATA", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "TEMP" })
        { SpeedFiles.Regular(environment[key]); Directory.CreateDirectory(environment[key]); }
        await SpeedFiles.Write(Path.Combine(root, "official-cli-environment.json"), environment, ct);
        var runner = new TimedProcessRunner();
        foreach (var (name, args) in new[] { ("official-cli-version.json", new[] { "version", "--format", "json" }),
            ("official-cli-help.txt", new[] { "command", "--help" }) })
        {
            var response = await runner.RunAsync(new(Guid.NewGuid(), cli, [..args], root, Environment: environment), TimeSpan.FromSeconds(60), cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(root, name), response.Output + response.Error, ct);
            if (response.Outcome != ProcessOutcome.Exited || response.ExitCode != 0) throw new SpeedMeasurementException("bench-incompatible", "공식 CLI 도움말·버전을 확인하지 못했습니다: " + response.Error);
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(response.Output);
                if (!json.RootElement.GetProperty("success").GetBoolean() || json.RootElement.GetProperty("data").GetProperty("version").GetString() != tool.CliVersion)
                    throw new SpeedMeasurementException("bench-incompatible", "실제 공식 CLI 버전이 선택한 버전과 다릅니다.");
            }
            else if (!new[] { "--project-path", "--timeout", "--format" }.All(response.Output.Contains))
                throw new SpeedMeasurementException("bench-incompatible", "공식 CLI의 명시적 프로젝트·시간 제한·JSON 옵션이 필요합니다.");
        }
    }
    public static string[] Arguments(string project, int timeoutSeconds, string command, string parameters, string? execFile = null)
    {
        string[] prefix = ["--format", "json", "command", "--project-path", project, "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture)];
        if (execFile is not null) return [..prefix, "eval_file", execFile];
        if (command == "desk_probe") return [..prefix, "desk_probe", "--parameters", parameters];
        if (command == "exec")
        {
            using var json = JsonDocument.Parse(parameters);
            if (json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return [..prefix, "eval", code.GetString()!];
        }
        throw new SpeedMeasurementException("bench-incompatible", "공식 Pipeline과 동일 작업 대응이 정의되지 않은 명령입니다: " + command);
    }
    public static void ValidateScenario(GuestRequest request)
    {
        if (request.Trial.Experiment != "S01") return;
        var scenario = (request.Options.StressCommands ?? SpeedStress.DefaultCommands).Single(c => c.Id == request.Trial.Variant);
        _ = Arguments("validation-only", request.Options.TimeoutSeconds, scenario.Command, scenario.ParametersJson);
        if (scenario.ExpectedEditorState != "ready") throw new SpeedMeasurementException("bench-incompatible", "공식 도구 비교의 부하 시험은 편집 준비 상태의 공통 작업만 지원합니다.");
    }
    public static JsonElement ReadData(ProcessResult response, bool evaluatedCode = false)
    {
        if (response.Outcome != ProcessOutcome.Exited) throw new SpeedMeasurementException(SpeedFailure.ProcessKind(response.Outcome), "공식 CLI " + response.Outcome);
        try
        {
            using var json = JsonDocument.Parse(response.Output);
            var value = json.RootElement;
            if (response.ExitCode != 0 || value.GetProperty("success").ValueKind != JsonValueKind.True)
                throw new SpeedMeasurementException("command-error", "공식 CLI 명령 실패: " + response.Output[..Math.Min(response.Output.Length, 1200)]);
            var data = value.GetProperty("data");
            if (data.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
                throw new SpeedMeasurementException("command-error", "Pipeline 명령 실패: " + data.ToString()[..Math.Min(data.ToString().Length, 1200)]);
            var result = data.GetProperty("result");
            // Pipeline's eval commands return an EvalResponse inside the command result.
            // Only unwrap that known envelope; a normal workload may itself return "result".
            if (evaluatedCode)
            {
                // Lean Pipeline responses omit the command metadata.
                if (result.TryGetProperty("command", out var command) && command.GetString() != "eval")
                    throw new SpeedMeasurementException("invalid-response", "Pipeline C# 실행 응답의 명령이 다릅니다.");
                if (result.GetProperty("success").ValueKind != JsonValueKind.True)
                {
                    string error = result.TryGetProperty("error", out var message) ? message.ToString() : "C# 실행 실패";
                    string kind = error == "Compilation Failed" ? "compile-error" : "runtime-error";
                    throw new SpeedMeasurementException(kind, "Pipeline " + result.ToString()[..Math.Min(result.ToString().Length, 1200)]);
                }
                result = result.GetProperty("result");
            }
            return result.Clone();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new SpeedMeasurementException(response.ExitCode == 0 ? "invalid-response" : "cli-error", "공식 CLI 응답 형식 확인 실패: " + e.Message + " / " + response.Error[..Math.Min(response.Error.Length, 1000)]); }
    }
}

// Observes files in the owned project; no warm-up CLI command and no token copied into evidence.
public sealed class PipelineDiscovery : IInstanceDiscovery
{
    public BridgeInstance Find(BridgeTarget target, int? expectedPort = null)
    {
        try
        {
            string descriptor = Path.Combine(target.ProjectPath, "Library", "Pipeline", ".unity-pipeline-port");
            string readiness = Path.Combine(target.ProjectPath, "Library", "desk-pipeline-ready.json");
            SpeedFiles.Regular(descriptor); SpeedFiles.Regular(readiness);
            if (!File.Exists(descriptor) || !File.Exists(readiness)) throw new BridgeException(BridgeFailure.MissingInstance, "Pipeline·실험 코드 준비 대기");
            using var portFile = JsonDocument.Parse(File.ReadAllText(descriptor));
            using var readyFile = JsonDocument.Parse(File.ReadAllText(readiness));
            var port = portFile.RootElement; var ready = readyFile.RootElement;
            int pid = ready.GetProperty("pid").GetInt32(), number = port.GetProperty("port").GetInt32();
            long stamp = ready.GetProperty("timestamp").GetInt64();
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp is > 10000 or < -2000)
                throw new BridgeException(BridgeFailure.StaleInstance, "Pipeline 준비 정보가 오래되었습니다.");
            if (pid != port.GetProperty("pid").GetInt32() || !InstanceDiscovery.SamePath(port.GetProperty("projectPath").GetString()!, target.ProjectPath) ||
                !InstanceDiscovery.SamePath(ready.GetProperty("projectPath").GetString()!, target.ProjectPath) || target.RequiredPid is { } expected && pid != expected)
                throw new BridgeException(BridgeFailure.WrongProcess, "Pipeline 대상 프로젝트·프로세스가 다릅니다.");
            using var process = Process.GetProcessById(pid);
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (process.HasExited || !InstanceDiscovery.SamePath(process.MainModule!.FileName, target.UnityExecutable) || stamp < started.ToUnixTimeMilliseconds() ||
                target.RequiredStartedAt is { } start && Math.Abs((start - started).TotalMilliseconds) > 2)
                throw new BridgeException(BridgeFailure.WrongProcess, "Pipeline Editor 실행 정보를 확인하지 못했습니다.");
            if (number is < 1 or > 65535 || expectedPort is { } previous && previous != number)
                throw new BridgeException(BridgeFailure.PortChanged, "Pipeline 포트가 변경되었습니다.");
            string version = ready.GetProperty("connectorVersion").GetString()!;
            if (version != target.RequiredConnectorVersion) throw new BridgeException(BridgeFailure.WrongResponse, "실제 Pipeline 패키지 버전이 계획과 다릅니다.");
            return new(target.ProjectPath, pid, number, ready.GetProperty("state").GetString()!, ready.GetProperty("unityVersion").GetString()!,
                version, stamp, ready.GetProperty("compileErrors").GetBoolean(), started);
        }
        catch (BridgeException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new BridgeException(BridgeFailure.MissingInstance, "Pipeline 준비 정보 대기: " + e.Message); }
    }
}
