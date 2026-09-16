using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Catalog;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class SpeedGuest
{
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    public static bool IsVirtualBoxGuest()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\VBoxGuest");
        return service is not null && (key?.GetValue("SystemProductName") as string)?.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) == true;
    }
    public static async Task<int> RunFile(string requestPath, string resultPath)
    {
        if (!IsVirtualBoxGuest() || !InstanceDiscovery.SamePath(Path.GetDirectoryName(requestPath)!, SpeedProtocol.GuestRoot) ||
            !InstanceDiscovery.SamePath(Path.GetDirectoryName(resultPath)!, SpeedProtocol.GuestRoot))
            throw new InvalidOperationException("VM 전용 측정기는 준비된 VirtualBox 게스트에서만 실행됩니다.");
        var request = await SpeedFiles.Read<GuestRequest>(requestPath);
        var result = await Run(request, SpeedProtocol.GuestRoot, CancellationToken.None);
        await SpeedFiles.Write(resultPath, result); return result.Status == "success" ? 0 : 1;
    }
    public static async Task<int> RunLocalFile(string requestPath, string resultPath)
    {
        var local = await SpeedFiles.Read<LocalTrialRequest>(requestPath);
        await LocalWorkspace.Check(local.WorkRoot, local.Measurement.Trial.Id, local.Measurement.GuestNonce);
        if (!InstanceDiscovery.SamePath(requestPath, Path.Combine(local.WorkRoot, "request.json")) ||
            !InstanceDiscovery.SamePath(resultPath, Path.Combine(local.WorkRoot, "result.json")))
            throw new IOException("실험 요청·결과 경로가 다릅니다.");
        foreach (var (key, value) in LocalWorkspace.EnvironmentFor(local.WorkRoot))
            if (Environment.GetEnvironmentVariable(key) != value) throw new IOException("시험 전용 임시 경로가 적용되지 않았습니다.");
        var result = await Run(local.Measurement, local.WorkRoot, CancellationToken.None, local);
        await SpeedFiles.Write(resultPath, result); return result.Status == "success" ? 0 : 1;
    }
    private static async Task<GuestResult> Run(GuestRequest request, string root, CancellationToken ct, LocalTrialRequest? local = null)
    {
        request.Options.Validate();
        var prep = Stopwatch.StartNew(); double preparation = 0, validation = 0; double? work = null, readyGap = null;
        var samples = new List<SpeedSample>(); var commands = new List<string>(); string unityVersion = "", status = "failed"; string? error = null;
        string stage = "preparation"; string? failureKind = null;
        bool usesExec = SpeedExec.UsesExec(request.Trial);
        double? measurementMs = null;
        UnityEnvironment? unity = null; string? reportedConnector = null, cliTreeHash = null;
        try
        {
            if (local is null)
            {
            string tempMarker = Path.Combine(Path.GetTempPath(), "UnityBridgeBench-contamination.txt");
            string profileMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityBridgeBench-contamination.txt");
            using var registry = Registry.CurrentUser.CreateSubKey(@"Software\UnityBridgeBench");
            if (File.Exists(tempMarker) || File.Exists(profileMarker) || registry.GetValue("PreviousTrial") is not null)
                throw new IOException("이전 시행 표식이 남았습니다. 기준 VM을 다시 준비하세요.");
            File.WriteAllText(tempMarker, request.GuestNonce); File.WriteAllText(profileMarker, request.GuestNonce);
            registry.SetValue("PreviousTrial", request.GuestNonce);
            }
            string cliDirectory = Path.Combine(root, "release", "cli"), cli = Path.Combine(cliDirectory, "unity-bridge.exe"),
                connector = Path.Combine(root, "release", "connector");
            string fixture = Path.Combine(root, "worker", "Assets", "DeskProbe.cs.txt");
            if (await SpeedFiles.Hash(cli, ct) != request.CliSha256 || await SpeedFiles.Hash(fixture, ct) != request.FixtureSha256)
                throw new InvalidDataException("게스트에 전달된 CLI 또는 실험 도구 해시가 다릅니다.");
            CliDistribution.ValidateLayout(cliDirectory, request.CliIsBundle);
            if (request.CliIsBundle && request.CliTreeSha256 is null) throw new InvalidDataException("RC 런타임의 전체 해시가 없습니다.");
            if (request.CliTreeSha256 is not null)
            {
                cliTreeHash = await CliDistribution.TreeHash(cliDirectory, ct);
                if (cliTreeHash != request.CliTreeSha256) throw new InvalidDataException("게스트 CLI 런타임 전체 해시가 다릅니다.");
            }
            var inspected = await new LocalInspector().InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder);
            if (inspected.Sha256 != request.ConnectorSha256 || inspected.DeclaredVersion != request.ConnectorVersion)
                throw new InvalidDataException("게스트 Connector 내용/버전이 다릅니다.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.UnityVersion, @"^\d+\.\d+\.\d+[abfp]\d+$"))
                throw new ArgumentException("정확한 게스트 Unity 버전이 필요합니다.");
            string editor = local?.EditorPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor", request.UnityVersion, "Editor", "Unity.exe");
            if (local is not null && await SpeedFiles.Hash(editor, ct) != local.EditorSha256) throw new IOException("Unity 실행 파일이 변경되었습니다.");
            if (!File.Exists(editor)) throw new FileNotFoundException("게스트 Unity Hub에 지정한 Editor를 설치·활성화하세요.");
            unityVersion = FileVersionInfo.GetVersionInfo(editor).ProductVersion ?? "";
            if (!unityVersion.StartsWith(request.UnityVersion + "_", StringComparison.Ordinal)) throw new IOException("게스트 Editor 버전 불일치.");
            string project = Path.Combine(root, "project");
            if (Directory.Exists(project)) throw new IOException("프로젝트 폴더 재사용은 허용되지 않습니다.");
            Directory.CreateDirectory(Path.Combine(project, "Assets")); Directory.CreateDirectory(Path.Combine(project, "Packages"));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            await File.WriteAllTextAsync(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: " + request.UnityVersion + "\n", ct);
            await SpeedFiles.Write(Path.Combine(project, "Packages", "manifest.json"), new { dependencies = new Dictionary<string, string> {
                [LocalInspector.ConnectorPackageName] = "file:" + connector.Replace('\\', '/') } }, ct);
            await UnityEnvironment.InstallFixtureAsync(project, ct, fixture);
            if (usesExec) await SpeedExec.WriteSource(root, ct);
            var runner = new TimedProcessRunner(); var discovery = new InstanceDiscovery(InstanceDiscovery.DefaultDirectory);
            var target = new BridgeTarget(project, editor, cli, request.CliSha256,
                RequiredConnectorVersion: request.ExpectedReportedConnectorVersion ?? request.ConnectorVersion);
            unity = await UnityEnvironment.OpenAsync(runner, discovery, target, Path.Combine(root, "editor.log"),
                TimeSpan.FromSeconds(request.Options.PrepareSeconds), (kind, text) =>
                {
                    if (local is null) return;
                    if (kind == "editor.owned")
                    {
                        using var info = JsonDocument.Parse(text);
                        File.WriteAllText(Path.Combine(root, "editor-process.json"), JsonSerializer.Serialize(new LocalProcessIdentity(
                            info.RootElement.GetProperty("pid").GetInt32(), info.RootElement.GetProperty("startedAt").GetDateTimeOffset()), SpeedProtocol.Json));
                    }
                    if (kind == "editor.owned") Console.WriteLine("Unity 실행 · 컴파일과 Bridge 준비 대기");
                }, ct, true, local is null ? null : ["-crash-report-folder", Path.Combine(root, "crashes")]);
            target = unity.Target; var before = discovery.Find(target); reportedConnector = before.ConnectorVersion; long ready = Stopwatch.GetTimestamp();
            await SpeedFiles.Write(Path.Combine(root, "ready.json"), new GuestReady(request.RunId, request.Trial.Id, request.GuestNonce, unityVersion), ct);
            var grantWait = Stopwatch.StartNew(); string grant = Path.Combine(root, "measure.ok");
            while (local is null && (!File.Exists(grant) || await File.ReadAllTextAsync(grant, ct) != request.GuestNonce))
            {
                if (grantWait.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("호스트의 네트워크 차단·측정 허가를 받지 못했습니다.");
                await Task.Delay(200, ct);
            }
            await Task.Delay(500, ct); // Fixed settling interval after the final host-to-guest transfer.
            if (local is not null) Console.WriteLine("Bridge 준비 완료 · 고정 명령 측정");
            if (request.Trial.Experiment == "S01")
            {
                var scenario = (request.Options.StressCommands ?? SpeedStress.DefaultCommands).Single(c => c.Id == request.Trial.Variant);
                preparation = prep.Elapsed.TotalMilliseconds; stage = "measurement";
                var batch = await SpeedStress.Run(request.Options.StressRequests, request.Options.StressConcurrency, async (index, offset, token) =>
                {
                    string nonce = Guid.NewGuid().ToString("N");
                    var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(scenario.ParametersJson)!;
                    if (scenario.Command == "desk_probe") parameters["nonce"] = JsonSerializer.SerializeToElement(nonce);
                    string[] args = ["--json", "--no-update-check", "--project", project, "--port", before.Port.ToString(),
                        "--instances-dir", InstanceDiscovery.DefaultDirectory, "--timeout-ms", ((long)request.Options.TimeoutSeconds * 1000).ToString(),
                        "call", scenario.Command, "--params", JsonSerializer.Serialize(parameters)];
                    lock (commands) commands.Add(JsonSerializer.Serialize(args));
                    var reply = await new TimedProcessRunner().RunAsync(new(Guid.NewGuid(), cli, [..args], project,
                        Environment: new() { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" }, OutputCodePage: checked((int)GetACP())),
                        TimeSpan.FromSeconds(request.Options.TimeoutSeconds), cancellationToken: token);
                    var sample = new SpeedSample(index, reply.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(reply.Output), OffsetMs: offset);
                    try
                    {
                        var data = SpeedFailure.ReadData(reply);
                        if (scenario.Command == "desk_probe")
                        {
                            var value = SpeedFailure.ReadValue(reply, nonce, before.Pid, project);
                            if (parameters.GetValueOrDefault("action").GetString() == "payload")
                                ValidateValue("F03", parameters["size"].GetInt32().ToString(), value);
                        }
                        if (scenario.ExpectedDataJson is not null)
                        {
                            using var expected = JsonDocument.Parse(scenario.ExpectedDataJson);
                            if (!SpeedStress.Matches(data, expected.RootElement))
                                throw new SpeedMeasurementException("response-mismatch", "명령의 실제 결과가 시나리오의 기대 결과와 다릅니다.");
                        }
                        return sample with { Outcome = "success" };
                    }
                    catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                    { return sample with { Outcome = "failed", FailureKind = SpeedFailure.Classify(e, "validation"), Error = e.Message[..Math.Min(e.Message.Length, 1600)] }; }
                }, ct);
                samples.AddRange(batch.Samples); measurementMs = batch.ElapsedMs;
                ct.ThrowIfCancellationRequested();
                if (batch.FailureKind is not null) throw new SpeedMeasurementException(batch.FailureKind, batch.Error ?? "부하 요청 실패");
                if (samples.Count != request.Options.StressRequests) throw new SpeedMeasurementException("execution-error", "부하 요청이 모두 완료되지 않았습니다.");
                stage = "validation";
                var afterStress = discovery.Find(target, before.Port);
                if (afterStress.CompileErrors || afterStress.State != scenario.ExpectedEditorState)
                    throw new SpeedMeasurementException("response-mismatch", $"부하 실행 후 Editor가 기대 상태({scenario.ExpectedEditorState})와 다릅니다.");
                work = samples.Average(s => s.Milliseconds); status = "success";
            }
            else
            {
            var clock = new TimedProcessRunner(); var timeout = TimeSpan.FromSeconds(request.Options.TimeoutSeconds);
            (ProcessCommand Command, string Nonce) Prepare(string action, Dictionary<string, object>? values = null)
            {
                values ??= []; string nonce = Guid.NewGuid().ToString("N"); values["action"] = action; values["nonce"] = nonce;
                string[] args = ["--json", "--no-update-check", "--project", project, "--port", before.Port.ToString(),
                    "--instances-dir", InstanceDiscovery.DefaultDirectory, "--timeout-ms", ((long)timeout.TotalMilliseconds).ToString(),
                    ..(usesExec ? SpeedExec.Arguments(root) : ["call", "desk_probe", "--params", JsonSerializer.Serialize(values)])];
                commands.Add(JsonSerializer.Serialize(args));
                return (new(Guid.NewGuid(), cli, [..args], project, Environment: new() { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" },
                    OutputCodePage: checked((int)GetACP())), nonce);
            }
            JsonElement Validate(ProcessResult result, string nonce)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    return SpeedFailure.ReadValue(result, nonce, before.Pid, project);
                }
                finally { validation += timer.Elapsed.TotalMilliseconds; }
            }
            void CheckValue(string experimentId, string condition, JsonElement value)
            { var timer = Stopwatch.StartNew(); try { ValidateValue(experimentId, condition, value); } finally { validation += timer.Elapsed.TotalMilliseconds; } }
            async Task<JsonElement> Untimed(string action, Dictionary<string, object>? values = null)
            {
                var call = Prepare(action, values);
                if (usesExec) await SpeedExec.WriteNonce(root, call.Nonce, ct);
                return Validate(await clock.RunAsync(call.Command, timeout, cancellationToken: ct), call.Nonce);
            }
            string experiment = request.Trial.Experiment, variant = request.Trial.Variant;
            if (!SpeedProtocol.Cases(request.Options).Any(c => c.Experiment == experiment && c.Variant == variant)) throw new IOException("계획에 없는 실험입니다.");
            if (experiment == "F02") await Untimed("create", new() { ["count"] = 1000 });
            stage = "warmup";
            if (!(experiment is "F01" or "F04" && variant == "first"))
                for (int i = 0; i < request.Options.Warmups; i++)
                {
                    var value = await Untimed(experiment == "F03" ? "payload" : "echo", experiment == "F03" ? new() { ["size"] = int.Parse(variant) } : null);
                    CheckValue(experiment == "F03" ? "F03" : "F01", variant, value);
                }
            int count = experiment == "F02" ? int.Parse(variant) : experiment is "F01" or "F04" && variant == "first" ? 1 : request.Options.Calls;
            var calls = Enumerable.Range(0, count).Select(i => Prepare(experiment switch { "F02" => "move", "F03" => "payload", _ => "echo" },
                experiment == "F02" ? new() { ["start"] = i * (1000 / count), ["count"] = 1000 / count } :
                experiment == "F03" ? new() { ["size"] = int.Parse(variant) } : null)).ToArray();
            before = discovery.Find(target, before.Port);
            if (before.State != "ready" || before.CompileErrors) throw new IOException("측정 직전 Unity 준비 상태가 바뀌었습니다.");
            preparation = prep.Elapsed.TotalMilliseconds;
            stage = "measurement";
            var replies = new List<ProcessResult>(); long batchStart = 0, batchEnd = 0;
            for (int i = 0; i < calls.Length; i++)
            {
                if (usesExec) await SpeedExec.WriteNonce(root, calls[i].Nonce, ct);
                var reply = await clock.RunAsync(calls[i].Command, timeout, cancellationToken: ct);
                if (i == 0) { batchStart = clock.LastStartedTimestamp; readyGap = Stopwatch.GetElapsedTime(ready, batchStart).TotalMilliseconds; }
                batchEnd = clock.LastCompletedTimestamp;
                replies.Add(reply); samples.Add(new(i, reply.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(reply.Output),
                    "unverified", OffsetMs: Stopwatch.GetElapsedTime(batchStart, clock.LastStartedTimestamp).TotalMilliseconds));
                if (reply.Outcome != ProcessOutcome.Exited || reply.ExitCode != 0) break;
            }
            stage = "validation";
            Exception? validationError = null;
            for (int i = 0; i < replies.Count; i++)
            {
                try { CheckValue(experiment, variant, Validate(replies[i], calls[i].Nonce)); samples[i] = samples[i] with { Outcome = "success" }; }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { samples[i] = samples[i] with { Outcome = "failed", FailureKind = SpeedFailure.Classify(e, stage), Error = e.Message }; validationError ??= e; }
            }
            if (validationError is not null) throw validationError;
            if (replies.Count != calls.Length) throw new SpeedMeasurementException("cli-error", "명령열을 완료하지 못했습니다.");
            if (experiment == "F02" && !(await Untimed("inspect", new() { ["count"] = 1000 })).GetBoolean()) throw new SpeedMeasurementException("response-mismatch", "오브젝트 개수·ID·위치 검증 실패.");
            var after = discovery.Find(target, before.Port);
            if (after.State != "ready" || after.CompileErrors) throw new SpeedMeasurementException("response-mismatch", "응답 후 대상 상태 검증 실패.");
            work = experiment == "F02" ? Stopwatch.GetElapsedTime(batchStart, batchEnd).TotalMilliseconds : samples.Average(s => s.Milliseconds);
            status = "success";
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { error = e.Message; failureKind = SpeedFailure.Classify(e, stage); status = e is OperationCanceledException ? "cancelled" : "failed"; if (preparation == 0) preparation = prep.Elapsed.TotalMilliseconds; }
        finally
        {
            if (unity is not null)
                try { await unity.DisposeAsync(); }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { error = (error is null ? "" : error + " / ") + "Unity 종료 실패: " + e.Message; failureKind = "cleanup-error"; stage = "cleanup"; status = "failed"; }
        }
        return new(request.RunId, request.Trial.Id, request.GuestNonce, local is null ? SpeedProtocol.Schema : LocalWorkspace.Schema, status, error, preparation, work, validation,
            samples.ToArray(), Environment.ProcessId, Environment.MachineName, unityVersion, request.CliSha256, request.ConnectorSha256,
            request.FixtureSha256, Stopwatch.Frequency, commands.ToArray(), readyGap, cliTreeHash, reportedConnector,
            failureKind, failureKind is null ? null : stage, usesExec ? SpeedExec.Sha256 : null, measurementMs);
    }
    private static void ValidateValue(string experiment, string variant, JsonElement value)
    {
        if (experiment is "F01" or "F04" && value.GetInt32() != 42) throw new SpeedMeasurementException("response-mismatch", "고정 응답 값 검증 실패.");
        if (experiment == "F02" && value.GetInt32() != 1000 / int.Parse(variant)) throw new SpeedMeasurementException("response-mismatch", "이동 작업 수 검증 실패.");
        if (experiment == "F03")
        {
            string text = value.GetString() ?? ""; int expected = int.Parse(variant);
            if (text.Length != expected || text.Where((c, i) => c != 'A' + i % 26).Any()) throw new SpeedMeasurementException("response-mismatch", "응답 길이·본문 검증 실패.");
        }
    }
}
