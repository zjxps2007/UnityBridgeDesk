using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed class LocalSpeedCoordinator(string dataRoot, IProcessRunner runner)
{
    public static async Task<LocalEnvironment> InspectEditor(string editor, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(editor) || !Path.GetFileName(editor).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(editor))
            throw new IOException("설치된 Unity를 찾지 못했습니다. Unity Hub에서 Editor를 설치·활성화해 주세요.");
        string version = LocalDiscovery.EditorVersion(editor) ?? throw new IOException("Unity 버전을 확인할 수 없습니다.");
        var memory = LocalWorkspace.Memory();
        return new(Path.GetFullPath(editor), version, await SpeedFiles.Hash(editor, ct), System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.ProcessorCount, memory.Total, "fresh project + process tree + TEMP/TMP + UPM cache; shared Windows account",
            "fresh project Library and per-trial UPM cache; OS cache and user preferences shared; no cold-OS claim",
            "host network unchanged; package preparation may use network; measured commands target local Editor");
    }
    public async Task<SpeedRun> Run(string editor, SpeedRelease[] releases, SpeedOptions options, string workerDirectory,
        IProgress<string>? progress, CancellationToken ct, IProgress<SpeedLiveProgress>? live = null, ToolWorkspaceCleanup? officialCleanup = null, ToolWorkspaceCleanup? goCleanup = null)
    {
        options.Validate();
        SpeedResearch.ValidateTargets(options, releases);
        if (options.Selected.Contains("S01") && options.StressCommands is null) options = options with { StressCommands = releases.Any(r => r.OfficialUnity is not null || r.GoUnity is not null) ? SpeedStress.CommonCommands : SpeedStress.DefaultCommands };
        var plan = SpeedProtocol.Schedule(options, releases.Select(r => r.Tag).ToArray());
        var environment = await InspectEditor(editor, ct);
        if (releases.Any(r => r.OfficialUnity is not null)) environment = environment with
        { CachePolicy = environment.CachePolicy + "; official CLI uses per-trial profile environment; official originals deleted after run" };
        if (releases.Any(r => r.GoUnity is not null)) environment = environment with
        { CachePolicy = environment.CachePolicy + "; Go CLI private home + owned discovery snapshot + fresh one-hour update cache per batch; original heartbeat checked and removed after Editor exit; Go originals deleted after run" };
        if (releases.Any(r => r.OfficialUnity is not null || r.GoUnity is not null) && !environment.EditorVersion.StartsWith("6000.", StringComparison.Ordinal))
            throw new ArgumentException("공식 Pipeline 또는 Go unity-cli 비교에는 Unity 6 Editor가 필요합니다.");
        if (releases.Any(r => r.OfficialUnity?.EditorVersion is { } version && version != environment.EditorVersion))
            throw new ArgumentException("선택한 Editor에 맞춰 공식 Pipeline 의존 패키지를 다시 준비하세요.");
        if (releases.Any(r => r.GoUnity?.EditorVersion is { } version && version != environment.EditorVersion))
            throw new ArgumentException("선택한 Editor에 맞춰 Go 의존 패키지를 다시 준비하세요.");
        foreach (var release in releases) if (!await ReleaseRepository.Valid(release, ct)) throw new IOException("릴리스 보관 파일이 변경되었습니다: " + release.Tag);
        string worker = Path.Combine(workerDirectory, "UnityBridgeDesk.Worker.exe"), fixture = Path.Combine(workerDirectory, "Assets", "DeskProbe.cs.txt");
        if (!File.Exists(worker) || !File.Exists(fixture)) throw new IOException("측정기가 포함된 배포 폴더 전체가 필요합니다.");
        SpeedFiles.Regular(worker); string fixtureHash = await SpeedFiles.Hash(fixture, ct);
        string lockPath = Path.Combine(Path.GetTempPath(), "UnityBridgeDesk-local-speed.lock"); SpeedFiles.Regular(lockPath);
        using var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await LocalWorkspace.Recover(dataRoot, progress, ct);
        Guid id = Guid.NewGuid(); string output = Path.Combine(Path.GetFullPath(dataRoot), "speed", "local-runs", id.ToString("N"));
        var run = new SpeedRun(id, DateTimeOffset.UtcNow, LocalWorkspace.Schema, releases.Any(r => r.OfficialUnity is not null || r.GoUnity is not null) ? "local-cross-tool-direct-measurement" : "local-direct-measurement", options, null,
            releases, plan, [], "running", Environment.MachineName, environment, officialCleanup, goCleanup);
        progress?.Report("실행기·런타임·검증 자산의 파일 명세를 고정합니다.");
        var manifest = await ResearchRuntime.Capture(AppContext.BaseDirectory, workerDirectory, ct);
        run = run with { ResearchPlan = SpeedResearch.Freeze(run, fixtureHash, await SpeedFiles.Hash(worker, ct), manifest) };
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "frozen-plan.json"), run.ResearchPlan.Payload, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "frozen-plan.sha256"), run.ResearchPlan.Sha256, ct);
        await SpeedFiles.Write(Path.Combine(output, "run.json"), run, ct);
        var results = new List<SpeedTrialResult>();
        void Notify(SpeedLiveStage stage, SpeedTrial? trial, SpeedTrialResult? result = null) => live?.Report(new(stage, results.Count, plan.Length, trial, result));
        live?.Report(new(SpeedLiveStage.Preparation, 0, plan.Length, null, Plan: plan));
        foreach (var trial in plan)
        {
            if (ct.IsCancellationRequested) { run = run with { Status = "cancelled" }; break; }
            var life = Stopwatch.StartNew(); string token = Guid.NewGuid().ToString("N");
            string root = Path.Combine(LocalWorkspace.Parent(dataRoot), trial.Id.ToString("N"));
            string evidence = Path.Combine(output, trial.Id.ToString("N"));
            GuestResult? result = null; string? error = null, failureKind = null, failureStage = null; bool cleaned = false;
            string stage = "preparation";
            var release = releases.Single(r => r.Tag == trial.Tag);
            var request = new GuestRequest(id, trial, options, environment.EditorVersion, "", "", release.CliSha256,
                release.ConnectorSha256, release.Version, fixtureHash, token, CliDistribution.IsBundle(release.CliUrl),
                release.CliTreeSha256, release.GoUnity?.ConnectorVersion ?? release.OfficialUnity?.PipelineVersion ?? CliDistribution.ReportedConnectorVersion(release), release.OfficialUnity, release.GoUnity);
            try
            {
                Notify(SpeedLiveStage.Preparation, trial);
                progress?.Report($"{trial.Order}/{plan.Length} · {trial.Tag} · {trial.Experiment}/{trial.Variant} · 새 실험 폴더 준비");
                await LocalWorkspace.Create(dataRoot, trial.Id, token, ct);
                await Prepare(root, release, fixture, ct);
                var local = new LocalTrialRequest(request, root, environment.EditorPath, environment.EditorSha256);
                await SpeedFiles.Write(Path.Combine(root, "request.json"), local, ct);
                await SpeedFiles.Write(Path.Combine(evidence, "request.json"), local, ct);
                var memory = LocalWorkspace.Memory();
                await SpeedFiles.Write(Path.Combine(evidence, "host-before.json"), new { at = DateTimeOffset.UtcNow,
                    activePowerScheme = ResearchHost.PowerScheme(), availableMemoryBytes = memory.Available, memoryLoadPercent = memory.Load, logicalProcessors = Environment.ProcessorCount }, ct);
                if (await SpeedFiles.Hash(editor, ct) != environment.EditorSha256) throw new IOException("시행 중 Unity 실행 파일이 변경되었습니다.");
                int calls = trial.Experiment == "S01" ? (int)Math.Ceiling((double)options.StressRequests / options.StressConcurrency) :
                    trial.Experiment == "F02" ? int.Parse(trial.Variant) + 2 : trial.Variant == "first" ? 1 : options.Calls;
                var timeout = TimeSpan.FromSeconds(options.PrepareSeconds + (long)(calls + options.Warmups) * options.TimeoutSeconds + 180);
                stage = "worker";
                var reply = await runner.RunAsync(new(Guid.NewGuid(), worker, ["--local-trial", Path.Combine(root, "request.json"), Path.Combine(root, "result.json")], root,
                    Environment: LocalWorkspace.EnvironmentFor(root)), timeout, frame =>
                    {
                        if (frame.Kind == "started")
                        {
                            using var json = JsonDocument.Parse(frame.Text);
                            File.WriteAllText(Path.Combine(root, "worker-process.json"), JsonSerializer.Serialize(new LocalProcessIdentity(
                                json.RootElement.GetProperty("pid").GetInt32(), json.RootElement.GetProperty("startedAt").GetDateTimeOffset()), SpeedProtocol.Json));
                        }
                        if (frame.Kind == "stdout")
                        {
                            string message = frame.Text.Trim();
                            if (message.Contains("Unity 실행 · 컴파일과 Bridge 준비 대기", StringComparison.Ordinal)) Notify(SpeedLiveStage.Unity, trial);
                            if (message.Contains("Bridge 준비 완료 · 고정 명령 측정", StringComparison.Ordinal)) Notify(SpeedLiveStage.Measurement, trial);
                            progress?.Report(message);
                        }
                    }, ct);
                await SpeedFiles.Write(Path.Combine(evidence, "worker.json"), reply);
                if (File.Exists(Path.Combine(root, "result.json")))
                {
                    stage = "validation";
                    result = await SpeedFiles.Read<GuestResult>(Path.Combine(root, "result.json"));
                    SpeedCoordinator.ValidateResult(request, result, LocalWorkspace.Schema);
                    if (reply.ProcessId != result.GuestPid) throw new IOException("측정기 프로세스가 결과와 다릅니다.");
                    await SpeedFiles.Write(Path.Combine(evidence, "result.json"), result);
                }
                if (reply.Outcome != ProcessOutcome.Exited || reply.ExitCode != 0 || result?.Status != "success")
                {
                    error = result?.Error ?? $"측정기 {reply.Outcome} / 종료 코드 {reply.ExitCode} · {reply.Error}";
                    failureKind = result?.FailureKind ?? (reply.Outcome == ProcessOutcome.Exited ? "worker-error" : SpeedFailure.ProcessKind(reply.Outcome));
                    failureStage = result?.FailureStage ?? "worker";
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { error = ex.Message; failureKind = SpeedFailure.Classify(ex, stage); failureStage = stage; }
            finally
            {
                Notify(SpeedLiveStage.Cleanup, trial);
                progress?.Report($"{trial.Order}/{plan.Length} · 결과 보관 · 시험 프로세스와 임시 파일 정리");
                try
                {
                    await KeepDiagnostics(root, evidence);
                    await LocalWorkspace.CleanInstance(root);
                    await GoUnityCli.CleanInstance(root);
                    await LocalWorkspace.Delete(dataRoot, root, trial.Id, token);
                    cleaned = !Directory.Exists(root);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
                { error = (error is null ? "" : error + " / ") + "정리 실패: " + ex.Message; failureKind = "cleanup-error"; failureStage = "cleanup"; }
            }
            string status = !cleaned ? "cleanup-failed" : ct.IsCancellationRequested ? "cancelled" : error is null ? "success" : "failed";
            results.Add(new(trial, status, result, error, cleaned, life.Elapsed.TotalMilliseconds,
                ct.IsCancellationRequested && cleaned ? "cancelled" : failureKind, failureStage));
            run = run with { Results = results.ToArray() };
            await SpeedFiles.Write(Path.Combine(output, "run.json"), run);
            await File.WriteAllTextAsync(Path.Combine(output, "samples.csv"), SpeedAnalysis.Csv(run));
            Notify(SpeedLiveStage.Cleanup, trial, results[^1]);
            if (!cleaned) { run = run with { Status = "cleanup-failed" }; break; }
            if (ct.IsCancellationRequested) { run = run with { Status = "cancelled" }; break; }
        }
        if (run.Status == "running") run = run with { Status = "completed" };
        await SpeedFiles.Write(Path.Combine(output, "run.json"), run);
        await File.WriteAllTextAsync(Path.Combine(output, "samples.csv"), SpeedAnalysis.Csv(run));
        Notify(SpeedLiveStage.Finished, null);
        progress?.Report("시험 종료 · 결과 탭에서 성공·실패와 정리 상태를 확인하세요."); return run;
    }
    public static async Task Prepare(string root, SpeedRelease release, string fixture, CancellationToken ct)
    {
        if (release.OfficialUnity is { } official)
        {
            foreach (var package in official.Packages)
                await ProjectFiles.CopyFolderAsync(package.Folder, Path.Combine(root, "release", "packages", package.Name), ct);
            string cliFolder = Path.Combine(root, "release", "cli"); Directory.CreateDirectory(cliFolder);
            File.Copy(release.CliPath, Path.Combine(cliFolder, "unity.exe"));
            string probe = Path.Combine(root, "worker", "Assets", "DeskProbe.cs.txt"); Directory.CreateDirectory(Path.GetDirectoryName(probe)!); File.Copy(fixture, probe);
            return;
        }
        await ProjectFiles.CopyFolderAsync(release.ConnectorPath, Path.Combine(root, "release", "connector"), ct);
        if (release.GoUnity is { } go)
            foreach (var package in go.Packages ?? throw new InvalidDataException("Go 의존 패키지를 다시 준비하세요."))
                await ProjectFiles.CopyFolderAsync(package.Folder, Path.Combine(root, "release", "packages", package.Name), ct);
        string cli = Path.Combine(root, "release", "cli");
        if (release.CliDirectory is { } folder) await ProjectFiles.CopyFolderAsync(folder, cli, ct);
        else { Directory.CreateDirectory(cli); File.Copy(release.CliPath, Path.Combine(cli, "unity-bridge.exe")); }
        string target = Path.Combine(root, "worker", "Assets", "DeskProbe.cs.txt"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(fixture, target);
    }
    private static async Task KeepDiagnostics(string root, string evidence)
    {
        if (!Directory.Exists(root)) return;
        Directory.CreateDirectory(evidence);
        // Logs are copied after timing. Dump filenames/sizes are recorded; large dumps are not carried into the next trial.
        foreach (string name in new[] { "scene-observed.json", "editor.log", "ready.json", "editor-process.json", "result.json", "cli-help.txt", "cli-call-help.txt", "cli-exec-help.txt", "official-cli-version.json", "official-cli-help.txt", "official-cli-environment.json", "go-cli-version.txt", "go-cli-help.txt", "go-cli-exec-help.txt", "go-cli-environment.json", "packages-lock.json", SpeedExec.SourceName, SpeedExec.NonceName })
        {
            string file = Path.Combine(root, name); if (!File.Exists(file)) continue; SpeedFiles.Regular(file);
            await using var input = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (input.Length > 4 * 1024 * 1024) input.Seek(-4 * 1024 * 1024, SeekOrigin.End);
            await using var dest = File.Create(Path.Combine(evidence, name)); await input.CopyToAsync(dest);
        }
        var dumps = ProjectFiles.Files(root).Where(f => f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            .Select(f => new { path = Path.GetRelativePath(root, f), bytes = new FileInfo(f).Length }).ToArray();
        await SpeedFiles.Write(Path.Combine(evidence, "dumps.json"), dumps);
    }
}
