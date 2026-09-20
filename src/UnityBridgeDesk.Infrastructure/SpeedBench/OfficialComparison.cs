using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public sealed record OfficialComparisonRequest(string DataRoot, string EditorPath, string[] BridgeTags, SpeedOptions Options,
    string? CliVersion = null, string? PipelineVersion = null, bool OfficialBaseline = false);

// Optional headless entry point; shares the same workflow as the desktop buttons.
public static class OfficialComparison
{
    public static async Task<SpeedRun> Run(OfficialComparisonRequest request, string workerDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        request.Options.Validate();
        if (!Path.IsPathFullyQualified(request.DataRoot) || request.BridgeTags is not { Length: >= 1 and <= 7 } ||
            request.BridgeTags.Distinct().Count() != request.BridgeTags.Length)
            throw new ArgumentException("결과 저장 경로와 UnityBridge 비교 버전 1~7개를 지정하세요.");
        if (string.IsNullOrWhiteSpace(request.EditorPath))
        {
            string settingsPath = Path.Combine(request.DataRoot, "speed", "local-settings.json");
            string previous = File.Exists(settingsPath) ? (await SpeedFiles.Read<LocalSpeedSettings>(settingsPath, ct)).EditorPath : "";
            var found = await new LocalDiscovery().ScanAsync(CatalogDocument.Empty, rememberedPaths: [previous], cancellationToken: ct);
            var editors = found.Candidates.Where(c => c.Kind == DiscoveryKind.Editor && c.Version?.StartsWith("6000.", StringComparison.Ordinal) == true).ToArray();
            string editor = editors.FirstOrDefault(c => c.Path.Equals(previous, StringComparison.OrdinalIgnoreCase))?.Path
                ?? editors.OrderByDescending(c => Version.Parse(System.Text.RegularExpressions.Regex.Match(c.Version!, @"^\d+\.\d+\.\d+").Value)).FirstOrDefault()?.Path
                ?? throw new IOException("설치된 Unity 6를 찾지 못했습니다. EditorPath에 Unity.exe 경로를 지정하세요.");
            request = request with { EditorPath = editor };
        }
        var repository = new ReleaseRepository(request.DataRoot);
        var choices = await repository.CachedList(ct);
        if (request.BridgeTags.Any(t => !choices.Any(c => c.Tag == t))) choices = await repository.List(ct);
        var selected = new List<ReleaseChoice>();
        foreach (string tag in request.BridgeTags)
        {
            var choice = choices.SingleOrDefault(c => c.Tag == tag && c.CliUrl is not null)
                ?? throw new ArgumentException("Windows CLI가 있는 UnityBridge 릴리스를 찾지 못했습니다: " + tag);
            selected.Add(choice);
        }
        var workflow = new SpeedBenchWorkflow(request.DataRoot, workerDirectory);
        var releases = await workflow.Prepare(request.EditorPath, selected.ToArray(), new(request.CliVersion, request.PipelineVersion),
            request.OfficialBaseline ? SpeedBenchWorkflow.OfficialLabel : selected[0].Tag, progress, ct);
        var run = await workflow.Run(request.EditorPath, releases, request.Options, progress, null, ct);
        string report = await SpeedReportFiles.Export(request.DataRoot, new SpeedReport(run), CancellationToken.None);
        progress?.Report("비교 보고서: " + report);
        return run;
    }
}
