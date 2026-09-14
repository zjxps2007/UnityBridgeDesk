using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using UnityBridgeDesk.Core.Execution;

namespace UnityBridgeDesk.Infrastructure.Bridge;

public enum BridgeFailure { None, MissingExecutable, MissingInstance, StaleInstance, AmbiguousTarget,
    WrongProcess, PortChanged, Compiling, Transitioning, CompileErrors, BadJson, NonzeroExit,
    CommandRejected, Cancelled, TimedOut, OutputLimit, ProtocolError, WrongResponse }
public sealed class BridgeException(BridgeFailure failure, string message) : IOException(message)
{ public BridgeFailure Failure { get; } = failure; }
public sealed record BridgeInstance(string ProjectPath, int Pid, int Port, string State, string UnityVersion,
    string ConnectorVersion, long Timestamp, bool CompileErrors, DateTimeOffset ProcessStartedAt)
{ public int DiscoveryRetries { get; init; } }
public sealed record BridgeTarget(string ProjectPath, string UnityExecutable, string CliExecutable, string CliSha256,
    int? RequiredPid = null, DateTimeOffset? RequiredStartedAt = null, string? RequiredConnectorVersion = null);
public sealed record BridgeReply(JsonElement Json, ProcessResult Process, BridgeInstance Before, BridgeInstance After);
public interface IInstanceDiscovery { BridgeInstance Find(BridgeTarget target, int? expectedPort = null); }

public sealed class InstanceDiscovery(string instancesDirectory,Action<int>? retried=null) : IInstanceDiscovery
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-bridge", "instances");
    public static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    public BridgeInstance Find(BridgeTarget target, int? expectedPort = null)
    {
        for(int attempt=0;;attempt++)
        {
            try{return FindOnce(target,expectedPort) with{DiscoveryRetries=attempt};}
            catch(BridgeException error)when(error.Failure==BridgeFailure.MissingInstance && attempt<4)
            {retried?.Invoke(attempt+1);Thread.Sleep(10);}
        }
    }
    private BridgeInstance FindOnce(BridgeTarget target,int? expectedPort)
    {
        var matches = new List<BridgeInstance>();
        var failure = BridgeFailure.MissingInstance;
        if (!Directory.Exists(instancesDirectory)) throw new BridgeException(failure, "연결 정보가 없습니다.");
        foreach (string file in Directory.EnumerateFiles(instancesDirectory, "*.json").Take(1000))
        {
            try
            {
                if (new FileInfo(file).Length > 65536) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!SamePath(root.GetProperty("projectPath").GetString()!, target.ProjectPath)) continue;
                long stamp = root.GetProperty("timestamp").GetInt64();
                double age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp;
                if (age > 10000 || age < -2000) { failure = BridgeFailure.StaleInstance; continue; }
                int pid = root.GetProperty("pid").GetInt32();
                if (target.RequiredPid is { } required && required != pid) { failure = BridgeFailure.WrongProcess; continue; }
                using var process = Process.GetProcessById(pid);
                var start = new DateTimeOffset(process.StartTime.ToUniversalTime());
                if(stamp<start.ToUnixTimeMilliseconds()){failure=BridgeFailure.StaleInstance;continue;}
                if (process.HasExited || process.MainModule?.FileName is not { } exe || !SamePath(exe, target.UnityExecutable) ||
                    (target.RequiredStartedAt is { } wanted && Math.Abs((start - wanted).TotalMilliseconds) > 2))
                { failure = BridgeFailure.WrongProcess; continue; }
                int port = root.GetProperty("port").GetInt32();
                if (port is < 1 or > 65535) { failure = BridgeFailure.BadJson; continue; }
                if (expectedPort is { } old && old != port) { failure = BridgeFailure.PortChanged; continue; }
                matches.Add(new(target.ProjectPath, pid, port, root.GetProperty("state").GetString()!,
                    root.GetProperty("unityVersion").GetString()!, root.GetProperty("connectorVersion").GetString()!,
                    stamp, root.TryGetProperty("compileErrors", out var errors) && errors.GetBoolean(), start));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* A foreign or concurrently replaced file is never repaired or removed here. */ }
        }
        if (matches.Count == 0) throw new BridgeException(failure, "정확한 프로젝트·Editor 연결을 확인하지 못했습니다: " + failure);
        if (matches.Count != 1) throw new BridgeException(BridgeFailure.AmbiguousTarget, "같은 프로젝트의 연결이 여러 개입니다.");
        var match = matches[0];
        if (target.RequiredConnectorVersion is { } version && match.ConnectorVersion != version)
            throw new BridgeException(BridgeFailure.WrongResponse, "실제 Connector 버전이 계획과 다릅니다.");
        return match;
    }
}

public sealed class BridgeAdapter(IProcessRunner runner, IInstanceDiscovery discovery, string instancesDirectory)
{
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    public IInstanceDiscovery Discovery => discovery;
    public async Task<BridgeReply> CallAsync(BridgeTarget target, IEnumerable<string> arguments, TimeSpan timeout,
        Action<ProcessFrame>? observe = null, CancellationToken cancellationToken = default, bool allowBusy = false)
    {
        if (!File.Exists(target.CliExecutable)) throw new BridgeException(BridgeFailure.MissingExecutable, "선택한 CLI 실행 파일이 없습니다.");
        var before = discovery.Find(target);
        if (!allowBusy) RequireUsable(before);
        var args = new List<string> { "--json", "--no-update-check", "--project", target.ProjectPath, "--port", before.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--instances-dir", instancesDirectory, "--timeout-ms", Math.Clamp((long)timeout.TotalMilliseconds, 1, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        args.AddRange(arguments);
        var process = await runner.RunAsync(new(Guid.NewGuid(), target.CliExecutable, [..args], target.ProjectPath,
            Environment: new() { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" },
            ExpectedSha256: target.CliSha256, OutputCodePage: checked((int)GetACP())), timeout, observe, cancellationToken);
        var failure = process.Outcome switch
        {
            ProcessOutcome.Cancelled => BridgeFailure.Cancelled, ProcessOutcome.TimedOut => BridgeFailure.TimedOut,
            ProcessOutcome.OutputLimit => BridgeFailure.OutputLimit, ProcessOutcome.Exited => BridgeFailure.None,
            _ => BridgeFailure.ProtocolError
        };
        if (failure != BridgeFailure.None) throw new BridgeException(failure, "CLI 실행을 마치지 못했습니다: " + failure);
        if (process.ExitCode != 0) throw new BridgeException(BridgeFailure.NonzeroExit, "CLI 종료 코드: " + process.ExitCode + " / " + process.Error);
        JsonElement json;
        try { using var doc = JsonDocument.Parse(process.Output); json = doc.RootElement.Clone(); }
        catch (JsonException) { throw new BridgeException(BridgeFailure.BadJson, "CLI 응답이 올바른 JSON이 아닙니다."); }
        if (json.ValueKind != JsonValueKind.Object) throw new BridgeException(BridgeFailure.BadJson, "CLI 응답 객체가 없습니다.");
        if (!json.TryGetProperty("success",out _) && !json.TryGetProperty("projectPath",out _))
            throw new BridgeException(BridgeFailure.BadJson,"CLI 응답의 필수 필드가 없습니다.");
        if (json.TryGetProperty("success", out var success) && success.ValueKind != JsonValueKind.True)
            throw new BridgeException(BridgeFailure.CommandRejected, json.TryGetProperty("message", out var message) ? message.ToString() : "명령 거부");
        var after = discovery.Find(target with { RequiredPid = before.Pid, RequiredStartedAt = before.ProcessStartedAt }, before.Port);
        if (json.TryGetProperty("projectPath", out var path) && !InstanceDiscovery.SamePath(path.GetString()!, target.ProjectPath))
            throw new BridgeException(BridgeFailure.WrongResponse, "다른 프로젝트의 응답입니다.");
        if (json.TryGetProperty("pid", out var responsePid) && responsePid.GetInt32()!=before.Pid)
            throw new BridgeException(BridgeFailure.WrongResponse,"다른 Editor 프로세스의 응답입니다.");
        return new(json, process, before, after);
    }
    public async Task<BridgeReply> ExecAsync(BridgeTarget target, string body, TimeSpan timeout,
        Action<ProcessFrame>? observe = null, CancellationToken cancellationToken = default)
    {
        // Strings survive both versions' serializer; anonymous-object properties do not.
        string nonce = Guid.NewGuid().ToString("N");
        string code = "var deskValue = new System.Func<object>(() => { " + body + " })(); return Newtonsoft.Json.JsonConvert.SerializeObject(new { nonce = \"" + nonce +
            "\", projectPath = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath), pid = System.Diagnostics.Process.GetCurrentProcess().Id, value = deskValue });";
        var reply = await CallAsync(target, ["exec", "--code", code], timeout, observe, cancellationToken);
        try
        {
            using var inner = JsonDocument.Parse(reply.Json.GetProperty("data").GetString()!);
            var data = inner.RootElement;
            if (data.GetProperty("nonce").GetString() != nonce || data.GetProperty("pid").GetInt32() != reply.Before.Pid ||
                !InstanceDiscovery.SamePath(data.GetProperty("projectPath").GetString()!, target.ProjectPath))
                throw new BridgeException(BridgeFailure.WrongResponse, "응답의 프로젝트·프로세스·요청 표식이 다릅니다.");
            return reply with { Json = data.GetProperty("value").Clone() };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new BridgeException(BridgeFailure.BadJson, "실행 결과의 확인 정보가 없습니다."); }
    }
    public static void RequireUsable(BridgeInstance instance)
    {
        if (instance.CompileErrors) throw new BridgeException(BridgeFailure.CompileErrors, "Unity 컴파일 오류가 있습니다.");
        if (instance.State is "compiling" or "reloading" or "refreshing") throw new BridgeException(BridgeFailure.Compiling, "Unity가 컴파일·갱신 중입니다.");
        if (instance.State is not ("ready" or "playing" or "paused")) throw new BridgeException(BridgeFailure.Transitioning, "Unity 전환 상태: " + instance.State);
    }
}
