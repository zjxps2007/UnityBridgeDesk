using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Infrastructure.Bridge;

namespace UnityBridgeDesk.Infrastructure.Benchmark;

public sealed record ExperimentMeasurement(double PreparationMs, double WorkMs, double ValidationMs,
    IReadOnlyList<TimingSample> Samples, bool FirstPass = true, int Attempts = 1,
    long? InputTokens = null, long? OutputTokens = null, int? ToolCalls = null, double? EndToEndMs = null);

public sealed class FixedExperiments(BridgeAdapter bridge)
{
    public async Task<ExperimentMeasurement> RunAsync(BridgeTarget target, ExperimentCase experiment, BenchOptions options,
        Action<string, string> observe, CancellationToken ct)
    {
        var samples = new List<TimingSample>(); double work = 0, validation = 0, preparation = 0;
        TimeSpan timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        async Task<BridgeReply> Raw(string name, IEnumerable<string> args, bool measured = true, bool busy = false, bool checking = false)
        {
            observe("command", JsonSerializer.Serialize(args)); var clock = Stopwatch.StartNew();
            var reply = await bridge.CallAsync(target, args, timeout, frame => observe("cli." + frame.Kind, frame.Text), ct, busy);
            double elapsed = clock.Elapsed.TotalMilliseconds;
            observe("target.confirmed",JsonSerializer.Serialize(new{reply.Before,reply.After}));
            if (checking) validation += elapsed;
            else if (measured) { work += elapsed; samples.Add(new(name, elapsed, Encoding.UTF8.GetByteCount(reply.Process.Output))); }
            else preparation += elapsed;
            observe("timing", JsonSerializer.Serialize(new { name, elapsed, phase = checking ? "validation" : measured ? "work" : "preparation" }));
            return reply;
        }
        async Task<JsonElement> Probe(string action, Dictionary<string, object>? values = null, bool measured = true, bool checking = false)
        {
            string nonce = Guid.NewGuid().ToString("N"); values ??= []; values["nonce"] = nonce; values["action"] = action;
            var reply = await Raw(action, ["call", "desk_probe", "--params", JsonSerializer.Serialize(values)], measured, checking: checking);
            var clock = Stopwatch.StartNew();
            try
            {
                var data = reply.Json.GetProperty("data");
                if (data.GetProperty("nonce").GetString() != nonce || data.GetProperty("pid").GetInt32() != reply.Before.Pid ||
                    !InstanceDiscovery.SamePath(data.GetProperty("projectPath").GetString()!, target.ProjectPath))
                    throw new InvalidDataException("이번 요청의 응답이 아닙니다.");
                return data.GetProperty("value").Clone();
            }
            finally { validation += clock.Elapsed.TotalMilliseconds; }
        }
        void Check(bool condition, string message)
        {
            var clock = Stopwatch.StartNew(); observe("validation", message + (condition ? " · 통과" : " · 실패"));
            validation += clock.Elapsed.TotalMilliseconds;
            if (!condition) throw new InvalidDataException(message);
        }
        switch (experiment.Id)
        {
            case "F01":
                if (experiment.Variant == "warm")
                    for (int i = 0; i < options.Warmups; i++) Check((await Probe("echo", measured: false)).GetInt32() == 42, "준비 호출 값");
                int calls = experiment.Variant == "cold" ? 1 : options.InnerCalls;
                for (int i = 0; i < calls; i++) Check((await Probe("echo")).GetInt32() == 42, "요청 표식·고정 값");
                break;
            case "F02":
                await Probe("create", new() { ["count"] = options.ObjectCount }, false);
                int split = int.Parse(experiment.Variant, System.Globalization.CultureInfo.InvariantCulture), batch = options.ObjectCount / split;
                for (int i = 0; i < split; i++) await Probe("move", new() { ["start"] = i * batch, ["count"] = batch });
                Check((await Probe("inspect", new() { ["count"] = options.ObjectCount }, checking: true)).GetBoolean(), "전체 ID·개수·위치");
                break;
            case "F03":
                int size = int.Parse(experiment.Variant, System.Globalization.CultureInfo.InvariantCulture);
                string actual = (await Probe("payload", new() { ["size"] = size })).GetString()!;
                var verify = Stopwatch.StartNew();
                bool matches = actual.Length == size && actual.Select((c, i) => c == 'A' + i % 26).All(x => x);
                validation += verify.Elapsed.TotalMilliseconds;
                Check(matches, "본문 길이·전체 ASCII 내용");
                break;
            case "F04":
                if (experiment.Variant == "revision")
                {
                    var change = Stopwatch.StartNew(); await UnityEnvironment.WriteRevisionAsync(target.ProjectPath, 2, ct); work += change.Elapsed.TotalMilliseconds;
                    await Raw("refresh-compile", ["refresh", "--compile", "request", "--wait", "--timeout-sec", options.TimeoutSeconds.ToString()], busy: true);
                }
                else await Raw("wait-ready", ["wait-ready", "--timeout-sec", options.TimeoutSeconds.ToString()], busy: true);
                var revision = await Probe("revision", checking: true);
                Check(revision.GetInt32() == (experiment.Variant == "revision" ? 2 : 1), "실제 실행 코드 리비전");
                break;
            case "F05":
                await Raw("enter-play", ["editor", "play", "--wait", "--timeout-sec", options.TimeoutSeconds.ToString()], busy: true);
                Check((await Probe("state", checking: true)).GetProperty("playing").GetBoolean(), "실제 Play 상태");
                var observation = Stopwatch.StartNew(); await Task.Delay(TimeSpan.FromSeconds(options.ObservationSeconds), ct);
                samples.Add(new("observation-excluded", observation.Elapsed.TotalMilliseconds, 0));
                await Raw("exit-play", ["editor", "stop", "--wait", "--timeout-sec", options.TimeoutSeconds.ToString()], busy: true);
                var state = await Probe("state", checking: true); Check(!state.GetProperty("playing").GetBoolean() && !state.GetProperty("compiling").GetBoolean(), "실제 Edit 복귀·후속 응답");
                break;
            default: throw new InvalidOperationException("지원하지 않는 실험입니다.");
        }
        return new(preparation, work, validation, samples);
    }
}
