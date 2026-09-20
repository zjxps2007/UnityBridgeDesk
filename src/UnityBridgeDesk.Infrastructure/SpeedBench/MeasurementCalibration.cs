using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record CalibrationSample(int Block, int Position, int RequestedDelayMs, double? OuterMs, double? InnerMs, string Status, string? Error);
public static class MeasurementCalibration
{
    public const string SummaryName = "00_진단요약.txt";
    public static async Task<string> Run(string worker, string parent, IProgress<string>? progress, CancellationToken ct)
    {
        worker = Path.GetFullPath(worker); parent = Path.GetFullPath(parent);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(worker)) throw new IOException("Worker가 포함된 실행 폴더가 필요합니다.");
        string lockPath = Path.Combine(Path.GetTempPath(), "UnityBridgeDesk-local-speed.lock"); SpeedFiles.Regular(lockPath);
        using var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string directory = Path.Combine(parent, "calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string hash = await SpeedFiles.Hash(worker, ct);
        var random = new Random(93483);
        var plan = Enumerable.Range(0, 20).SelectMany(block =>
        {
            int[] delays = [0, 25, 100]; random.Shuffle(delays);
            return delays.Select((delay, position) => new { block, position, delay });
        }).ToArray();
        await SpeedFiles.Write(Path.Combine(directory, "plan.json"), new { version = 1, workerHash = hash, plan,
            scope = "synthetic process timing diagnostic; not a Unity validation", power = ResearchHost.PowerScheme() }, ct);
        var samples = new List<CalibrationSample>();
        var runner = new TimedProcessRunner();
        string status = "completed";
        try
        {
            foreach (var trial in plan)
            {
                ct.ThrowIfCancellationRequested();
                string nonce = Guid.NewGuid().ToString("N");
                var reply = await runner.RunAsync(new(Guid.NewGuid(), Path.GetFullPath(worker),
                    ["--calibration-child", trial.delay.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce], directory, ExpectedSha256: hash),
                    TimeSpan.FromSeconds(30), null, ct);
                double? inner = null; string? error = null;
                try
                {
                    using var json = JsonDocument.Parse(reply.Output);
                    inner = json.RootElement.GetProperty("elapsedMs").GetDouble();
                    if (reply.Outcome != ProcessOutcome.Exited || reply.ExitCode != 0 ||
                        json.RootElement.GetProperty("nonce").GetString() != nonce ||
                        !double.IsFinite(inner.Value) || inner < 0 || inner > reply.ElapsedMilliseconds)
                        error = "child identity, interval or outcome mismatch";
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { error = ex.Message; }
                samples.Add(new(trial.block, trial.position, trial.delay, reply.ElapsedMilliseconds, inner,
                    error is null ? "success" : "failed", error));
                await SpeedFiles.Write(Path.Combine(directory, "samples.json"), samples, CancellationToken.None);
                progress?.Report($"계측 확인 {samples.Count}/{plan.Length}");
            }
        }
        catch (OperationCanceledException) { status = "cancelled"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { status = "failed: " + ex.Message; }
        if (status == "completed" && samples.Any(s => s.Status != "success")) status = "failed";
        var diagnostics = new[] {25,100}.Select(delay =>
        {
            var errors = samples.GroupBy(s => s.Block).Where(g => g.Any(s => s.RequestedDelayMs == 0 && s.Status == "success") &&
                g.Any(s => s.RequestedDelayMs == delay && s.Status == "success")).Select(g =>
                (g.Single(s => s.RequestedDelayMs == delay).OuterMs!.Value - g.Single(s => s.RequestedDelayMs == 0).OuterMs!.Value) -
                (g.Single(s => s.RequestedDelayMs == delay).InnerMs!.Value - g.Single(s => s.RequestedDelayMs == 0).InnerMs!.Value)).ToArray();
            double? mean = errors.Length == 0 ? null : errors.Average();
            double? half = errors.Length < 5 ? null : SpeedStatistics.Student95(errors.Length-1) * Math.Sqrt(SpeedStatistics.Variance(errors) / errors.Length);
            return new { requestedDelayMs = delay, pairs = errors.Length, residualMeanMs = mean, lower = mean-half, upper = mean+half };
        }).ToArray();
        await SpeedFiles.Write(Path.Combine(directory, "diagnostic.json"), new { status, samples = samples.Count, planned = plan.Length, diagnostics,
            certification = false, note = "Residual=(outer difference)-(actual inner difference). Process start/exit and scheduling remain. Zero in CI does not prove equivalence. Fixed 20 blocks; no optional stopping. No Unity or real CLI was measured." }, CancellationToken.None);
        var lines = new List<string>
        {
            $"실행기 진단 · {(status == "completed" ? "완료" : status == "cancelled" ? "중단" : "오류 포함")} · {samples.Count}/{plan.Length}회 기록",
            "이 결과는 Unity를 사용하지 않은 자식 프로세스 진단입니다."
        };
        lines.AddRange(diagnostics.Select(d => $"{d.requestedDelayMs}ms 대기 · 유효 {d.pairs}/20쌍 · 외부−내부 증가 잔차 {SpeedReport.Time(d.residualMeanMs)}ms (95% 구간 {SpeedReport.Time(d.lower)}–{SpeedReport.Time(d.upper)})"));
        lines.Add("잔차에는 프로세스 시작·종료와 스케줄링 차이가 남습니다. 이 값을 벤치 시간에서 빼거나 정확성 합격으로 해석하지 않습니다.");
        lines.Add(status == "completed" ? "다음 확인: 앱의 실험 목적에서 같은 릴리스 A/A 또는 알려진 지연 감지를 선택해 실제 Unity 경로를 검증하세요." :
            "다음 확인: samples.json의 오류와 실행 중인 벤치를 확인하세요. 미완료 결과로 계측 성능을 판정하지 않습니다.");
        await File.WriteAllTextAsync(Path.Combine(directory, SummaryName), string.Join("\n", lines), new System.Text.UTF8Encoding(true), CancellationToken.None);
        return directory;
    }
    public static async Task<int> Child(int delay, string nonce)
    {
        if (delay is < 0 or > 1000 || !Guid.TryParseExact(nonce, "N", out _)) return 2;
        long begin = Stopwatch.GetTimestamp();
        if (delay > 0) await Task.Delay(delay);
        double elapsed = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        Console.WriteLine(JsonSerializer.Serialize(new { nonce, elapsedMs = elapsed }));
        return 0;
    }
}
