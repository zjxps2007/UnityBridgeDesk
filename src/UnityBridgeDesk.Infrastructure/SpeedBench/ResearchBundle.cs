using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class ResearchBundle
{
    public static async Task Write(string path, SpeedReport report, CancellationToken ct = default, string? runEvidenceDirectory = null)
    {
        ct.ThrowIfCancellationRequested();
        // Select fixed entries only: never sweep a user's result directory or copy authentication files.
        var run = report.Run;
        var entries = new Dictionary<string, string>
        {
            ["run.json"] = JsonSerializer.Serialize(run, SpeedProtocol.Json),
            ["research-summary.txt"] = report.ResearchSummary + "\n\n" + report.Counts + "\n\n" + report.Issues(),
            ["README.txt"] = "UnityBridge Desk 연구 자료\n\n압축을 새 폴더에 풀고 Python 3.10 이상에서 python verify_analysis.py를 실행한다. 추가 패키지·인터넷·Unity가 필요하지 않다. 검산 결과는 analysis-check.json에 저장된다.\n\nrun.json은 내보내기 시점 원시 실행 기록의 사본이다. 모든 계획 시행과 기록된 성공·실패·미수행을 포함한다. frozen-plan.json은 실행 전에 저장한 계획 원문이며 과거 기록에는 없다. SHA256SUMS.json은 파일 손상 확인용으로 외부 인증·공개 사전등록을 뜻하지 않는다. 로컬 경로·명령·사용자 입력이 포함되므로 공개 전 내용을 검토한다. 진단 로그·자격 증명·실행 바이너리는 자동 수집하지 않는다.\n\n재실행: 같은 Desk 소스/실행기 해시, Unity·CLI·Connector·의존 패키지 해시, 설정·연구 질문을 고정한다. 앱의 기록 조건 재사용으로 별도 실행한다. 순서는 시드를 사용하며 개별 시행 ID는 새로 생성된다. 원본 자료로 논문의 모든 표·그래프를 생성하는 분석은 연구자가 별도로 확정한다. Unity 설치·활성화는 필요하다.\n\n이 자료는 계산 일치와 파일 무결성 확인을 지원한다. 실제 계측 정확성, 정상 상태, 독립성, 대표성, 제삼자 재현을 인증하지 않는다. 시간 초과는 실제 완료 시간이 아니며 성공 조건부 평균과 별도로 읽는다.\n"
        };
        if (run.ResearchPlan is { } plan) entries["frozen-plan.json"] = plan.Payload;
        var evidenceNotes = new List<string>();
        long evidenceBytes = 0;
        foreach (var trial in report.Trials.Where(t => t.Experiment == "F02"))
        {
            string? hash = trial.Result?.Guest?.SceneEvidenceSha256;
            string? source = runEvidenceDirectory is null ? null : Path.Combine(runEvidenceDirectory, trial.Trial.Id.ToString("N"), "scene-observed.json");
            if (hash is null || source is null || !File.Exists(source)) { evidenceNotes.Add($"#{trial.Order}: 최종 씬 관측 자료 없음 (과거·실패·파일 누락 가능)"); continue; }
            SpeedFiles.Regular(source);
            long bytes = new FileInfo(source).Length;
            if (bytes > 2 * 1024 * 1024 || (evidenceBytes += bytes) > 128 * 1024 * 1024) throw new IOException("씬 증거 자료가 내보내기 상한을 넘었습니다. 원본 실행 폴더에서 자료를 보관하세요.");
            string body = await File.ReadAllTextAsync(source, ct);
            if (SpeedResearch.Digest(body) != hash) throw new IOException("최종 씬 증거 파일이 측정 기록의 해시와 다릅니다: #" + trial.Order);
            entries[$"scene-{trial.Trial.Id:N}.json"] = body;
            evidenceNotes.Add($"#{trial.Order}: 최종 씬 관측 자료 포함 · {hash}");
        }
        entries["scene-evidence.txt"] = evidenceNotes.Count == 0 ? "F02 시행 없음" : string.Join("\n", evidenceNotes);
        entries["completion.txt"] = ResearchOutcomes.CompletionText(report);
        if (run.ResearchPlan is { } frozen)
        {
            using var document = JsonDocument.Parse(frozen.Payload);
            if (document.RootElement.TryGetProperty("runtimeManifest", out var manifest) && manifest.ValueKind == JsonValueKind.Object)
                entries["runtime-manifest.json"] = manifest.GetRawText();
        }
        var means = run.Plan.GroupBy(t => (t.Experiment, t.Variant, t.Tag)).Select(g =>
        {
            var s = report.Summaries.Single(s => s.Experiment == g.Key.Experiment && s.Condition == SpeedReport.Condition(g.Key.Experiment, g.Key.Variant) && s.Release == g.Key.Tag);
            return new { g.Key.Experiment, g.Key.Variant, g.Key.Tag, N = s.Valid, s.Mean, s.Evidence?.Lower, s.Evidence?.Upper };
        });
        var comparisons = run.Plan.GroupBy(t => (t.Experiment, t.Variant)).SelectMany(g => run.Releases.Skip(1).Select(r =>
        {
            var c = report.Comparisons.Single(c => c.Experiment == g.Key.Experiment && c.Condition == SpeedReport.Condition(g.Key.Experiment, g.Key.Variant) && c.Candidate == r.Tag);
            return new { g.Key.Experiment, g.Key.Variant, r.Tag, N = c.Valid, c.Evidence?.Ratio, c.Evidence?.Lower, c.Evidence?.Upper };
        }));
        entries["expected-analysis.json"] = JsonSerializer.Serialize(new { means, comparisons }, SpeedProtocol.Json);
        using var resource = typeof(ResearchBundle).Assembly.GetManifestResourceStream("UnityBridgeDesk.Infrastructure.Assets.ResearchVerifier.py.txt")
            ?? throw new IOException("연구 자료 검산기가 배포에 없습니다.");
        using var reader = new StreamReader(resource);
        entries["verify_analysis.py"] = await reader.ReadToEndAsync(ct);
        entries["SHA256SUMS.json"] = JsonSerializer.Serialize(entries.ToDictionary(e => e.Key, e => SpeedResearch.Digest(e.Value)), SpeedProtocol.Json);
        SpeedFiles.Regular(path);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(path)) throw new IOException("같은 이름의 파일이 있습니다. 새 파일 이름을 선택하세요.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    using var stream = archive.CreateEntry(entry.Key).Open();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(entry.Value), ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
