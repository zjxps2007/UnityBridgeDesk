using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record StressCommand(string Id, string Command, string ParametersJson, string? ExpectedDataJson = null,
    string? Description = null, string ExpectedEditorState = "ready");
public sealed record StressBatch(SpeedSample[] Samples, double ElapsedMs, string? Error, string? FailureKind);

public static class SpeedStress
{
    // The state workload is also shared C# when comparing different tool providers.
    public static StressCommand[] CommonCommands => DefaultCommands.Select(c => c.Id == "state"
        ? new StressCommand("state", "desk_probe", """{"action":"state"}""", """{"value":{"playing":false,"paused":false,"compiling":false}}""", "공통 Editor 상태 요청 밀집") : c).ToArray();
    public static StressCommand[] DefaultCommands =>
    [
        new("echo", "desk_probe", """{"action":"echo"}""", """{"value":42}""", "작은 요청 밀집"),
        new("payload", "desk_probe", """{"action":"payload","size":1048576}""", null, "1 MiB 응답 밀집"),
        new("objects", "desk_probe", """{"action":"stress_objects","count":1000}""", """{"value":1000}""", "요청당 오브젝트 1,000개 생성·제거"),
        new("exec", "exec", """{"code":"return 42;"}""", "42", "C# 컴파일·실행 밀집"),
        new("state", "get_editor_state", "{}", null, "Editor 상태 요청 밀집")
    ];
    public static void ValidateCommands(StressCommand[] commands)
    {
        if (commands is null || commands.Length is < 1 or > 32 || commands.Any(c => c is null) ||
            commands.Select(c => c.Id).Distinct().Count() != commands.Length)
            throw new ArgumentException("명령 시나리오는 고유한 ID를 가진 1~32개여야 합니다.");
        foreach (var command in commands)
        {
            if (!Regex.IsMatch(command.Id ?? "", @"^[a-zA-Z0-9_-]{1,48}$") ||
                !Regex.IsMatch(command.Command ?? "", @"^[a-zA-Z0-9_-]{1,64}$") || command.ParametersJson is null ||
                command.ParametersJson.Length > 65536 || command.ExpectedDataJson?.Length > 65536 ||
                command.ExpectedEditorState is not ("ready" or "playing" or "paused"))
                throw new ArgumentException("명령 ID·이름·입력 크기를 확인하세요.");
            using var parameters = JsonDocument.Parse(command.ParametersJson);
            if (parameters.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("명령 입력은 JSON 객체여야 합니다.");
            if (command.ExpectedDataJson is not null) using (JsonDocument.Parse(command.ExpectedDataJson)) { }
        }
    }
    public static bool Matches(JsonElement actual, JsonElement expected) =>
        expected.ValueKind == JsonValueKind.Object
            ? actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(p =>
                actual.TryGetProperty(p.Name, out var value) && Matches(value, p.Value))
            : JsonElement.DeepEquals(actual, expected);

    // Bounded workers submit through the CLI. Unity may serialize these requests internally.
    public static async Task<StressBatch> Run(int requests, int concurrency,
        Func<int, double, CancellationToken, Task<SpeedSample>> execute, CancellationToken ct)
    {
        if (requests is < 1 or > 1000 || concurrency is < 1 or > 16 || concurrency > requests) throw new ArgumentException("부하 요청 수를 확인하세요.");
        var timer = Stopwatch.StartNew(); var samples = new SpeedSample?[requests];
        int next = -1, stopped = 0; string? error = null, failure = null;
        var gate = new object();
        async Task Worker()
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref stopped) == 0)
            {
                int index = Interlocked.Increment(ref next); if (index >= requests) break;
                double offset = timer.Elapsed.TotalMilliseconds;
                try { samples[index] = await execute(index, offset, ct); }
                catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    samples[index] = new(index, timer.Elapsed.TotalMilliseconds - offset, 0, "failed",
                        SpeedFailure.Classify(e, "measurement"), offset, e.Message);
                }
                if (samples[index]!.Outcome != "success")
                {
                    lock (gate) { failure ??= samples[index]!.FailureKind; error ??= samples[index]!.Error ?? SpeedFailure.Label(failure); }
                    // Already submitted requests finish; do not add work behind a hung or untrusted target.
                    if (samples[index]!.FailureKind is "timeout" or "response-mismatch" or "unsupported-command" or "bench-incompatible" or "cancelled")
                        Interlocked.Exchange(ref stopped, 1);
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Worker()));
        return new(samples.Where(s => s is not null).Select(s => s!).OrderBy(s => s.Index).ToArray(),
            timer.Elapsed.TotalMilliseconds, error, failure);
    }
}
