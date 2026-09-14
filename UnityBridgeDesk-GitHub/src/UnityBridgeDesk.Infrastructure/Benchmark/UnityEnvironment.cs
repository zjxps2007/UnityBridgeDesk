using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Installation;

namespace UnityBridgeDesk.Infrastructure.Benchmark;

public sealed class UnityEnvironment : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task<ProcessResult>? process;
    public required BridgeTarget Target { get; set; }
    public string? OwnedFolder { get; init; }
    public string? OwnerToken { get; init; }
    public bool OwnsEditor => process is not null;
    public static async Task<UnityEnvironment> OpenAsync(IProcessRunner runner, IInstanceDiscovery discovery,
        BridgeTarget target, string logPath, TimeSpan timeout, Action<string, string> observe, CancellationToken ct, bool requireNew)
    {
        var environment = new UnityEnvironment { Target = target };
        try
        {
            BridgeInstance? external = null;
            try { external = discovery.Find(target with { RequiredConnectorVersion = null }); } catch (BridgeException e) when (e.Failure is BridgeFailure.MissingInstance or BridgeFailure.StaleInstance) { }
            if (external is not null)
            {
                if (requireNew) throw new IOException("새 시행 경로에 이미 실행 중인 Editor가 있습니다.");
                environment.Target = target with { RequiredPid = external.Pid, RequiredStartedAt = external.ProcessStartedAt };
                observe("editor.external", "이미 실행 중인 Editor에 연결합니다. 종료 소유권은 가져오지 않습니다.");
            }
            else
            {
                var started = new TaskCompletionSource<(int, DateTimeOffset)>(TaskCreationOptions.RunContinuationsAsynchronously);
                environment.process = runner.RunAsync(new(Guid.NewGuid(), target.UnityExecutable,
                    ["-batchmode", "-nographics", "-projectPath", target.ProjectPath, "-logFile", logPath], target.ProjectPath),
                    TimeSpan.FromDays(2), frame =>
                    {
                        if (frame.Kind == "started")
                        {
                            using var doc = JsonDocument.Parse(frame.Text);
                            started.TrySetResult((doc.RootElement.GetProperty("pid").GetInt32(), doc.RootElement.GetProperty("startedAt").GetDateTimeOffset()));
                        }
                        if (frame.Kind is "stderr" or "stdout") observe("editor." + frame.Kind, frame.Text);
                    }, environment.lifetime.Token);
                await Task.WhenAny(started.Task, environment.process).WaitAsync(timeout, ct);
                if (!started.Task.IsCompleted) throw new IOException("Unity 실행을 시작하지 못했습니다: " + (await environment.process).Error);
                var identity = await started.Task;
                environment.Target = target with { RequiredPid = identity.Item1, RequiredStartedAt = identity.Item2 };
                observe("editor.owned", JsonSerializer.Serialize(new { pid = identity.Item1, startedAt = identity.Item2, project = target.ProjectPath }));
            }
            var clock = Stopwatch.StartNew(); BridgeException? last = null;
            while (clock.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                if (environment.process?.IsCompleted == true) throw new IOException("준비 중 Unity가 종료되었습니다. editor.log를 확인하세요: " + (await environment.process).ExitCode);
                try
                {
                    var found = discovery.Find(environment.Target);
                    if (found.CompileErrors) throw new InvalidDataException("Unity 컴파일 오류입니다. editor.log를 확인하세요.");
                    if (found.State == "ready") return environment;
                }
                catch (BridgeException e) { last = e; }
                await Task.Delay(200, ct);
            }
            throw new TimeoutException("Unity 준비 시간 초과: " + last?.Failure);
        }
        catch { await environment.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (process is not null) await process;
        lifetime.Dispose();
    }
    public static void VerifyEditor(ProjectRef project, string editor)
    {
        if (!File.Exists(editor)) throw new FileNotFoundException("Unity Editor 실행 파일을 지정하세요.", editor);
        string? version = FileVersionInfo.GetVersionInfo(editor).ProductVersion;
        if (project.UnityBuild is null || version is null || !version.StartsWith(project.UnityBuild + "_", StringComparison.Ordinal))
            throw new InvalidDataException("프로젝트 Editor 버전과 선택한 실행 파일 버전이 다릅니다.");
    }
    public static async Task InstallFixtureAsync(string project, CancellationToken ct)
    {
        string dir = Path.Combine(project, "Assets", "DeskBenchmark", "Editor");
        if (Directory.Exists(Path.Combine(project, "Assets", "DeskBenchmark"))) throw new IOException("기준 프로젝트에 DeskBenchmark가 있습니다. 깨끗한 기준을 사용하세요.");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "DeskProbe.cs"), await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "DeskProbe.cs.txt"), ct), ct);
        await WriteRevisionAsync(project, 1, ct);
    }
    public static Task WriteRevisionAsync(string project, int revision, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(project, "Assets", "DeskBenchmark", "Editor", "DeskRevision.cs"),
            "public static class DeskRevision { public const int Value = " + revision + "; }", ct);
}
