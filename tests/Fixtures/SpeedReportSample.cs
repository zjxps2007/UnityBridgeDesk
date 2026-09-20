using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Tests;

internal static class SpeedReportSample
{
    public static SpeedRun Methodology(int repeats = 10, int calls = 20)
    {
        var original = Create();
        var plan = Enumerable.Range(0, repeats * 2).Select(i => new SpeedTrial(Guid.NewGuid(), i + 1, i / 2 + 1,
            i % 2 == 0 ? "vA" : "vB", "F01", "prepared")).ToArray();
        var results = plan.Select((t, i) =>
        {
            double mean = i % 2 == 0 ? 100 + i / 2 * 2 : 78 + i / 2 * 2.3;
            double[] noise = Enumerable.Range(0, calls).Select(j => j - (calls - 1) / 2d).ToArray();
            new Random(243 + i).Shuffle(noise);
            var guest = original.Results[0].Guest! with { TrialId = t.Id, WorkMs = mean,
                Samples = noise.Select((v, j) => new SpeedSample(j, mean + v, 10, "success", OffsetMs: j * mean)).ToArray(),
                WarmupMilliseconds = [200, 130, 105] };
            return new SpeedTrialResult(t, "success", guest, null, true, 60000 + mean * calls);
        }).ToArray();
        return original with { Plan = plan, Results = results, Status = "completed", Evidence = "synthetic-methodology-test",
            Options = new(Repeats: repeats, Warmups: 3, Calls: calls) };
    }
    public static SpeedRun Create()
    {
        var id = Guid.NewGuid();
        SpeedRelease Release(string tag) => new(tag, tag, "commit", "cli", "connector", "cli-hash", "connector-hash", "fixture", null);
        var plan = Enumerable.Range(0, 6).Select(i => new SpeedTrial(Guid.NewGuid(), i + 1, i / 2 + 1,
            i % 2 == 0 ? "vA" : "vB", "F01", "prepared")).ToArray();
        SpeedTrialResult Result(int i, double ms, int calls, string status = "success", bool cleaned = true)
        {
            var guest = new GuestResult(id, plan[i].Id, "nonce", LocalWorkspace.Schema, status, null, 1000,
                status == "success" ? ms : null, 1, Enumerable.Range(0, calls).Select(n => new SpeedSample(n, ms, 10)).ToArray(),
                123, "synthetic", "6000.0.1f1", "cli-hash", "connector-hash", "fixture-hash", 10000000, []);
            return new(plan[i], status, guest, status == "success" ? null : "=HYPERLINK(\"bad\")\u0001", cleaned, 1100);
        }
        return new(id, DateTimeOffset.Parse("2026-09-16T09:00:00+09:00"), LocalWorkspace.Schema, "synthetic-test",
            new(), null, [Release("vA"), Release("vB")], plan,
            [Result(0, 100, 1), Result(1, 80, 3), Result(2, 300, 3), Result(3, 999, 1, "failed"), Result(4, 500, 1, "cleanup-failed", false)],
            "cleanup-failed", "synthetic");
    }
}
