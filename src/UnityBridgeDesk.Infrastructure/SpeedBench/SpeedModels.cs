using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record SpeedOptions(int Repeats = 2, int Warmups = 1, int Calls = 3, int TimeoutSeconds = 180,
    int PrepareSeconds = 600, int Seed = 20260915, string[]? Experiments = null,
    int StressRequests = 32, int StressConcurrency = 4, StressCommand[]? StressCommands = null)
{
    public string[] Selected => Experiments ?? ["F01"];
    public void Validate()
    {
        if (Repeats is < 1 or > 100 || Warmups is < 0 or > 100 || Calls is < 1 or > 1000 ||
            TimeoutSeconds is < 1 or > 3600 || PrepareSeconds is < 30 or > 3600 || Selected.Length is < 1 or > 5 ||
            StressRequests is < 1 or > 1000 || StressConcurrency is < 1 or > 16 || StressConcurrency > StressRequests ||
            Selected.Distinct().Count() != Selected.Length || Selected.Any(x => x is not ("F01" or "F02" or "F03" or "F04" or "S01")))
            throw new ArgumentException("실험·반복·준비 호출·제한 시간을 확인해 주세요.");
        if (Selected.Contains("S01")) SpeedStress.ValidateCommands(StressCommands ?? SpeedStress.DefaultCommands);
    }
}
public sealed record ReleaseChoice(long Id, string Tag, string Version, string Title, bool Prerelease,
    string? CliUrl, string? PublisherDigest, string PageUrl)
{
    public string Display => $"{Tag}{(Prerelease ? " · 사전 릴리스" : "")} · {CliDistribution.Description(CliUrl)}";
}
public sealed record SpeedRelease(string Tag, string Version, string Commit, string CliPath, string ConnectorPath,
    string CliSha256, string ConnectorSha256, string CliUrl, string? PublisherDigest,
    string? CliDirectory = null, string? CliTreeSha256 = null, string? AssetSha256 = null);
public sealed record VmProfile(string ManagerPath, string VmId, string SnapshotId, string OwnershipToken,
    string VmDirectory, Dictionary<string, string> Settings, Dictionary<string, string> BaselineFiles,
    string ManagerVersion);
public sealed record SpeedSettings(string ManagerPath = "", string GuestUser = "", string UnityVersion = "",
    SpeedOptions? Options = null, VmProfile? Machine = null, int Palette = 0, string[]? SelectedTags = null);
public sealed record SpeedCase(string Experiment, string Variant);
public sealed record SpeedTrial(Guid Id, int Order, int Block, string Tag, string Experiment, string Variant);
public sealed record GuestRequest(Guid RunId, SpeedTrial Trial, SpeedOptions Options, string UnityVersion,
    string VmId, string SnapshotId, string CliSha256, string ConnectorSha256, string ConnectorVersion,
    string FixtureSha256, string GuestNonce, bool CliIsBundle = false, string? CliTreeSha256 = null, string? ExpectedReportedConnectorVersion = null);
public sealed record GuestReady(Guid RunId, Guid TrialId, string GuestNonce, string UnityVersion);
public sealed record SpeedSample(int Index, double Milliseconds, int Bytes,
    string? Outcome = null, string? FailureKind = null, double? OffsetMs = null, string? Error = null);
public sealed record GuestResult(Guid RunId, Guid TrialId, string GuestNonce, string Schema, string Status,
    string? Error, double PreparationMs, double? WorkMs, double ValidationMs, SpeedSample[] Samples,
    int GuestPid, string GuestMachine, string UnityVersion, string CliSha256, string ConnectorSha256,
    string FixtureSha256, long ClockFrequency, string[] Commands, double? ReadyToFirstMs = null,
    string? CliTreeSha256 = null, string? ReportedConnectorVersion = null,
    string? FailureKind = null, string? FailureStage = null, string? ExecSourceSha256 = null, double? MeasurementMs = null);
public sealed record SpeedTrialResult(SpeedTrial Trial, string Status, GuestResult? Guest, string? Error,
    bool ResetVerified, double HostLifecycleMs, string? FailureKind = null, string? FailureStage = null);
public sealed record SpeedRun(Guid Id, DateTimeOffset StartedAt, string Schema, string Evidence,
    SpeedOptions Options, VmProfile? Machine, SpeedRelease[] Releases, SpeedTrial[] Plan,
    SpeedTrialResult[] Results, string Status, string HostDescription, LocalEnvironment? Local = null);

public static class SpeedProtocol
{
    public const string Schema = "vm-cli-completion-v1-pilot";
    public const string GuestRoot = @"C:\UnityBridgeBench\Trial";
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static SpeedCase[] Cases(SpeedOptions options)
    {
        options.Validate();
        return options.Selected.SelectMany(id => (id switch {
            "F01" or "F04" => new[] { "first", "prepared" }, "F02" => ["1", "10", "100"],
            "F03" => ["1024", "65536", "1048576"],
            "S01" => (options.StressCommands ?? SpeedStress.DefaultCommands).Select(c => c.Id).ToArray(), _ => []
        }).Select(v => new SpeedCase(id, v))).ToArray();
    }
    public static SpeedTrial[] Schedule(SpeedOptions options, string[] tags)
    {
        if (tags.Length is < 2 or > 8 || tags.Distinct().Count() != tags.Length) throw new ArgumentException("서로 다른 릴리스 2~8개를 선택하세요.");
        var random = new Random(options.Seed); var result = new List<SpeedTrial>(); int block = 0;
        foreach (var condition in Cases(options))
        {
            var rotations = Enumerable.Range(0, options.Repeats).Select(i => i % tags.Length).ToArray();
            random.Shuffle(rotations);
            foreach (int offset in rotations)
            {
                block++;
                for (int p = 0; p < tags.Length; p++) result.Add(new(Guid.NewGuid(), result.Count + 1, block,
                    tags[(p + offset) % tags.Length], condition.Experiment, condition.Variant));
            }
        }
        return result.ToArray();
    }
}
