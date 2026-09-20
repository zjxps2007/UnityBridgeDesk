using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class SpeedReportFiles
{
    public const string SummaryName = "00_결과요약.txt", WorkbookName = "01_결과분석.xlsx", IssuesName = "02_실패내역.txt";
    public const string ChartName = "03_결과그래프.svg";
    public const string MethodologyName = "04_측정근거.txt";
    private static readonly string[] ReportNames = [SummaryName, WorkbookName, IssuesName, ChartName, MethodologyName];
    private const int FormatVersion = 10;
    private static readonly SemaphoreSlim Gate = new(1);
    private sealed record Manifest(int FormatVersion, Guid RunId, string SnapshotSha256, DateTimeOffset GeneratedAt, Dictionary<string, string> Files);

    public static async Task<string> Export(string dataRoot, SpeedReport report, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            string parent = Path.Combine(Path.GetFullPath(dataRoot), "speed", "reports"); SpeedFiles.Regular(parent);
            Directory.CreateDirectory(parent);
            string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report.Run, SpeedProtocol.Json)));
            string stem = $"{report.Run.StartedAt.ToLocalTime():yyyy-MM-dd_HHmmss}__{report.Run.Releases.Length}개버전__{report.Run.Id.ToString("N")[..8]}";
            string destination = Path.Combine(parent, stem);
            for (int copy = 1; Directory.Exists(destination); copy++, destination = Path.Combine(parent, stem + $"__사본-{copy}"))
            {
                ct.ThrowIfCancellationRequested(); SpeedFiles.Regular(destination);
                string manifestPath = Path.Combine(destination, ".report.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = await SpeedFiles.Read<Manifest>(manifestPath, ct);
                    if (manifest.FormatVersion != FormatVersion || manifest.RunId != report.Run.Id || manifest.SnapshotSha256 != hash || manifest.Files is null) continue;
                    bool same = true;
                    foreach (string name in ReportNames)
                        if (!manifest.Files.TryGetValue(name, out var expected) || !File.Exists(Path.Combine(destination, name)) ||
                            await SpeedFiles.Hash(Path.Combine(destination, name), ct) != expected) { same = false; break; }
                    if (same) return destination;
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
            string temporary = Path.Combine(parent, ".writing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary); File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(temporary, SummaryName), report.Text(), new UTF8Encoding(true), ct);
                await File.WriteAllTextAsync(Path.Combine(temporary, IssuesName), report.Issues(), new UTF8Encoding(true), ct);
                await File.WriteAllTextAsync(Path.Combine(temporary, MethodologyName), SpeedStatistics.Explanation(report), new UTF8Encoding(true), ct);
                SpeedWorkbook.Write(Path.Combine(temporary, WorkbookName), report, ct);
                SpeedCharts.WriteSvg(Path.Combine(temporary, ChartName), report);
                var hashes = new Dictionary<string, string>();
                foreach (string name in ReportNames) hashes[name] = await SpeedFiles.Hash(Path.Combine(temporary, name), ct);
                string metadata = Path.Combine(temporary, ".report.json");
                await SpeedFiles.Write(metadata, new Manifest(FormatVersion, report.Run.Id, hash, DateTimeOffset.UtcNow, hashes), ct);
                File.SetAttributes(metadata, File.GetAttributes(metadata) | FileAttributes.Hidden);
                ct.ThrowIfCancellationRequested();
                File.SetAttributes(temporary, File.GetAttributes(temporary) & ~FileAttributes.Hidden);
                Directory.Move(temporary, destination);
                return destination;
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    foreach (string name in ReportNames.Append(".report.json"))
                        try { File.Delete(Path.Combine(temporary, name)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    try { Directory.Delete(temporary, false); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally { Gate.Release(); }
    }
}
