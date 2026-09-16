using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed class SpeedCoordinator(string dataRoot, VirtualBoxHost host)
{
    public async Task<SpeedRun> Run(VmProfile machine, string guestUser, string password, string unityVersion,
        SpeedRelease[] releases, SpeedOptions options, string workerDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        options.Validate();
        if (options.Selected.Contains("S01") && options.StressCommands is null) options = options with { StressCommands = SpeedStress.DefaultCommands };
        if (string.IsNullOrWhiteSpace(guestUser) || string.IsNullOrEmpty(password)) throw new ArgumentException("게스트 Windows 사용자와 암호를 입력하세요. 암호는 저장하지 않습니다.");
        string worker = Path.Combine(workerDirectory, "UnityBridgeDesk.Worker.exe"), fixture = Path.Combine(workerDirectory, "Assets", "DeskProbe.cs.txt");
        if (!File.Exists(worker) || !File.Exists(Path.Combine(workerDirectory, "coreclr.dll")) || !File.Exists(fixture))
            throw new IOException("런타임이 포함된 시험 배포본에서 실행하세요. 게스트로 전달할 측정기 파일이 부족합니다.");
        var plan = SpeedProtocol.Schedule(options, releases.Select(r => r.Tag).ToArray());
        string fixtureHash = await SpeedFiles.Hash(fixture, ct);
        foreach (var release in releases) if (!await ReleaseRepository.Valid(release, ct)) throw new IOException("보관 릴리스 내용이 변경되었습니다: " + release.Tag);
        await host.VerifyOwnership(machine, ct);
        using var lease = new FileStream(Path.Combine(machine.VmDirectory, "desk-experiment.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress?.Report("기준 VM·디스크 검증 중"); await host.VerifyBaseline(machine, ct);
        Guid id = Guid.NewGuid(); string directory = Path.Combine(dataRoot, "speed", "runs", id.ToString("N")); Directory.CreateDirectory(directory);
        string secret = Path.Combine(dataRoot, "speed", ".guest-" + Guid.NewGuid().ToString("N") + ".secret");
        var results = new List<SpeedTrialResult>();
        var run = new SpeedRun(id, DateTimeOffset.UtcNow, SpeedProtocol.Schema, "real-vm-pilot", options, machine, releases, plan, [], "running",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription + " / " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture + " / logical CPUs " + Environment.ProcessorCount);
        try
        {
            await File.WriteAllTextAsync(secret, password, new UTF8Encoding(false), ct);
            await SpeedFiles.Write(Path.Combine(directory, "run.json"), run, ct);
            foreach (var trial in plan)
            {
                if (ct.IsCancellationRequested) { run = run with { Status = "cancelled" }; break; }
                var life = Stopwatch.StartNew(); GuestResult? guest = null; string? error = null; bool reset = false;
                string trialDir = Path.Combine(directory, trial.Id.ToString("N")); Directory.CreateDirectory(trialDir);
                var release = releases.Single(r => r.Tag == trial.Tag);
                var request = new GuestRequest(id, trial, options, unityVersion, machine.VmId, machine.SnapshotId, release.CliSha256,
                    release.ConnectorSha256, release.Version, fixtureHash, Guid.NewGuid().ToString("N"),
                    CliDistribution.IsBundle(release.CliUrl), release.CliTreeSha256, CliDistribution.ReportedConnectorVersion(release));
                await SpeedFiles.Write(Path.Combine(trialDir, "request.json"), request, ct);
                progress?.Report($"{trial.Order}/{plan.Length} · {trial.Tag} · {trial.Experiment} {trial.Variant} · VM 복원");
                await SpeedFiles.Write(Path.Combine(trialDir, "state.json"), new { trial.Id, phase = "restoring" }, ct);
                try
                {
                    await host.Restore(machine, ct);
                    string zip = Path.Combine(trialDir, "input.zip");
                    await Package(zip, workerDirectory, release, Path.Combine(trialDir, "request.json"), ct);
                    string zipHash = await SpeedFiles.Hash(zip, ct);
                    progress?.Report($"{trial.Order}/{plan.Length} · {trial.Tag} · 게스트 기동·입력 전달");
                    await host.Start(machine, ct); await host.WaitGuest(machine, guestUser, secret, ct);
                    await host.PowerShell(machine, guestUser, secret,
                        "$ErrorActionPreference='Stop'; if ((Test-Path -LiteralPath 'C:\\UnityBridgeBench\\Trial') -or (Test-Path -LiteralPath 'C:\\UnityBridgeBench\\input.zip')) { throw 'Previous input remains in baseline' }; New-Item -ItemType Directory -Path 'C:\\UnityBridgeBench' -Force | Out-Null", ct);
                    await host.CopyTo(machine, guestUser, secret, zip, @"C:\UnityBridgeBench", ct);
                    await host.PowerShell(machine, guestUser, secret,
                        "$ErrorActionPreference='Stop'; if ((Get-FileHash -LiteralPath 'C:\\UnityBridgeBench\\input.zip' -Algorithm SHA256).Hash.ToLowerInvariant() -ne '" + zipHash +
                        "') { throw 'Input hash mismatch' }; Expand-Archive -LiteralPath 'C:\\UnityBridgeBench\\input.zip' -DestinationPath 'C:\\UnityBridgeBench\\Trial'", ct);
                    progress?.Report($"{trial.Order}/{plan.Length} · {trial.Tag} · Unity 준비·게스트 속도 측정");
                    await SpeedFiles.Write(Path.Combine(trialDir, "state.json"), new { trial.Id, phase = "guest-measuring" }, ct);
                    int callCount = trial.Experiment == "S01" ? (int)Math.Ceiling((double)options.StressRequests / options.StressConcurrency) :
                        trial.Experiment == "F02" ? int.Parse(trial.Variant) + options.Warmups + 2 : options.Calls + options.Warmups;
                    string? commandError = null;
                    try
                    {
                        var guestTask = host.Guest(machine, guestUser, secret, SpeedProtocol.GuestRoot + @"\worker\UnityBridgeDesk.Worker.exe",
                            ["--vm-trial", SpeedProtocol.GuestRoot + @"\request.json", SpeedProtocol.GuestRoot + @"\result.json"], ct,
                            TimeSpan.FromSeconds(options.PrepareSeconds + (long)options.TimeoutSeconds * (callCount + 1) + 120));
                        try { await GrantMeasurement(machine, guestUser, secret, request, trialDir, guestTask, progress, ct); }
                        catch
                        {
                            // Observe the remote command before leaving; the outer finally powers off this owned VM.
                            _ = guestTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            throw;
                        }
                        await guestTask;
                    }
                    catch (IOException e) { commandError = e.Message; }
                    progress?.Report($"{trial.Order}/{plan.Length} · {trial.Tag} · 결과 회수·검증");
                    await host.CopyFrom(machine, guestUser, secret, SpeedProtocol.GuestRoot + @"\result.json", Path.Combine(trialDir, "result.json"), ct);
                    guest = await SpeedFiles.Read<GuestResult>(Path.Combine(trialDir, "result.json"), ct); ValidateResult(request, guest);
                    if (commandError is not null && guest.Status == "success") throw new IOException(commandError);
                    error = guest.Error;
                    // The bounded result is sufficient for analysis; no guest home or dump directories are imported.
                    File.Delete(zip);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
                { error = e.Message; }
                finally
                {
                    progress?.Report($"{trial.Order}/{plan.Length} · 시험 VM 종료·기준 상태 복원");
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                    try { await host.Stop(machine, cleanup.Token); await host.Restore(machine, cleanup.Token); reset = true; }
                    catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { error = (error is null ? "" : error + " / ") + "격리 복구 실패: " + e.Message; }
                    try { string input = Path.Combine(trialDir, "input.zip"); if (File.Exists(input)) File.Delete(input); }
                    catch (IOException e) { reset = false; error = (error is null ? "" : error + " / ") + "호스트 입력 파일 정리 실패: " + e.Message; }
                }
                string status = ct.IsCancellationRequested ? "cancelled" : !reset ? "isolation-failed" : error is null && guest?.Status == "success" ? "success" : "failed";
                results.Add(new(trial, status, guest, error, reset, life.Elapsed.TotalMilliseconds,
                    !reset ? "cleanup-error" : ct.IsCancellationRequested ? "cancelled" : guest?.FailureKind,
                    !reset ? "cleanup" : guest?.FailureStage));
                run = run with { Results = results.ToArray() };
                await SpeedFiles.Write(Path.Combine(directory, "run.json"), run, CancellationToken.None);
                if (!reset) { run = run with { Status = "isolation-failed" }; break; }
                if (ct.IsCancellationRequested) { run = run with { Status = "cancelled" }; break; }
            }
            if (run.Status == "running") run = run with { Status = results.Count == plan.Length ? "completed" : "incomplete" };
            await SpeedFiles.Write(Path.Combine(directory, "run.json"), run, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "samples.csv"), SpeedAnalysis.Csv(run), CancellationToken.None);
            progress?.Report("시험 종료 · 성공/실패와 독립 시행 수를 결과 탭에서 확인하세요."); return run;
        }
        finally { if (File.Exists(secret)) File.Delete(secret); }
    }
    private async Task GrantMeasurement(VmProfile machine, string user, string secret, GuestRequest request, string trialDirectory,
        Task guestTask, IProgress<string>? progress, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew(); string readyPath = Path.Combine(trialDirectory, "ready.json");
        while (!guestTask.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(request.Options.PrepareSeconds + 30))
        {
            ct.ThrowIfCancellationRequested(); GuestReady? ready = null;
            try
            {
                await host.CopyFrom(machine, user, secret, SpeedProtocol.GuestRoot + @"\ready.json", readyPath, ct);
                ready = await SpeedFiles.Read<GuestReady>(readyPath, ct);
            }
            catch (IOException) { }
            if (ready is not null)
            {
                if (ready.RunId != request.RunId || ready.TrialId != request.Trial.Id || ready.GuestNonce != request.GuestNonce)
                    throw new IOException("다른 시행의 준비 응답입니다.");
                progress?.Report("Unity ready · 외부 네트워크 차단 후 측정 시작");
                await host.DisconnectForMeasurement(machine, ct);
                string grant = Path.Combine(trialDirectory, "measure.ok"); await File.WriteAllTextAsync(grant, request.GuestNonce, new UTF8Encoding(false), ct);
                await host.CopyTo(machine, user, secret, grant, SpeedProtocol.GuestRoot, ct); return;
            }
            await Task.Delay(1000, ct);
        }
        if (!guestTask.IsCompleted) throw new IOException("Unity 준비 허가 단계가 시간 제한을 넘었습니다.");
    }
    public static void ValidateResult(GuestRequest request, GuestResult result, string schema = SpeedProtocol.Schema)
    {
        if (result.Samples is null || result.Commands is null || result.UnityVersion is null || result.GuestMachine is null ||
            result.RunId != request.RunId || result.TrialId != request.Trial.Id || result.GuestNonce != request.GuestNonce ||
            result.Schema != schema || result.CliSha256 != request.CliSha256 || result.ConnectorSha256 != request.ConnectorSha256 ||
            result.FixtureSha256 != request.FixtureSha256 || result.CliTreeSha256 != request.CliTreeSha256 || result.GuestPid <= 0 || result.ClockFrequency <= 0 ||
            result.Status is not ("success" or "failed" or "cancelled")) throw new IOException("다른 시행이거나 불완전한 게스트 결과입니다.");
        if (result.Samples.Any(s => s is null || !double.IsFinite(s.Milliseconds) || s.Milliseconds < 0 || s.Bytes < 0 || s.Index < 0 ||
                s.OffsetMs is { } offset && (!double.IsFinite(offset) || offset < 0) ||
                s.Outcome is not (null or "unverified" or "success" or "failed")) ||
            result.Samples.Select(s => s.Index).Distinct().Count() != result.Samples.Length ||
            result.MeasurementMs is { } measured && (!double.IsFinite(measured) || measured < 0) ||
            !double.IsFinite(result.PreparationMs) || result.PreparationMs < 0 || !double.IsFinite(result.ValidationMs) || result.ValidationMs < 0)
            throw new IOException("올바르지 않은 시간 표본입니다.");
        if (result.Status != "success") return;
        int expected = request.Trial.Experiment == "S01" ? request.Options.StressRequests :
            request.Trial.Experiment == "F02" ? int.Parse(request.Trial.Variant) : request.Trial.Variant == "first" ? 1 : request.Options.Calls;
        if (SpeedExec.UsesExec(request.Trial) && result.ExecSourceSha256 != SpeedExec.Sha256 ||
            request.Trial.Experiment == "S01" && (result.MeasurementMs is not > 0 || !double.IsFinite(result.MeasurementMs.Value) ||
                result.Samples.Any(s => s.Outcome != "success")))
            throw new IOException("exec 소스 또는 부하 시험 검증 정보가 다릅니다.");
        if (result.Samples.Length != expected || result.WorkMs is not { } work || !double.IsFinite(work) || work <= 0 ||
            result.FailureKind is not null || result.Samples.Any(s => s.Outcome is "failed" or "unverified" || s.FailureKind is not null) ||
            !result.UnityVersion.StartsWith(request.UnityVersion + "_", StringComparison.Ordinal) || result.Error is not null ||
            result.ReportedConnectorVersion != (request.ExpectedReportedConnectorVersion ?? request.ConnectorVersion) ||
            !result.Samples.Select(s => s.Index).SequenceEqual(Enumerable.Range(0, expected))) throw new IOException("성공 결과의 표본·버전·시간이 명세와 다릅니다.");
        if (request.Trial.Experiment != "F02" && Math.Abs(work - result.Samples.Average(s => s.Milliseconds)) > .0001 ||
            request.Trial.Experiment == "F02" && work + .0001 < result.Samples.Sum(s => s.Milliseconds)) throw new IOException("게스트 시간 집계가 원시 표본과 다릅니다.");
    }
    public static async Task Package(string path, string workerDirectory, SpeedRelease release, string request, CancellationToken ct)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create); long total = 0; int files = 0;
        async Task Add(string input, string name)
        {
            SpeedFiles.Regular(input); if (++files > 15000 || (total += new FileInfo(input).Length) > 1024L * 1024 * 1024) throw new IOException("전달 파일 크기 제한을 넘었습니다.");
            await using var source = File.OpenRead(input); await using var target = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            await SpeedFiles.CopyBounded(source, target, new FileInfo(input).Length, ct);
        }
        foreach (string file in Execution.ProjectFiles.Files(workerDirectory))
            await Add(file, "worker/" + Path.GetRelativePath(workerDirectory, file).Replace('\\', '/'));
        foreach (string file in Execution.ProjectFiles.Files(release.ConnectorPath))
            await Add(file, "release/connector/" + Path.GetRelativePath(release.ConnectorPath, file).Replace('\\', '/'));
        if (release.CliDirectory is { } cliDirectory)
        {
            CliDistribution.ValidateLayout(cliDirectory, CliDistribution.IsBundle(release.CliUrl));
            foreach (string file in Execution.ProjectFiles.Files(cliDirectory))
                await Add(file, "release/cli/" + Path.GetRelativePath(cliDirectory, file).Replace('\\', '/'));
        }
        else
        {
            if (CliDistribution.IsBundle(release.CliUrl)) throw new IOException("RC 런타임 전체를 먼저 준비하세요.");
            await Add(release.CliPath, "release/cli/unity-bridge.exe");
        }
        await Add(request, "request.json");
    }
}
