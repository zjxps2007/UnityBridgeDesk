using UnityBridgeDesk.Infrastructure.SpeedBench;

namespace UnityBridgeDesk.Tests;

internal static class SpeedReportSample
{
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
