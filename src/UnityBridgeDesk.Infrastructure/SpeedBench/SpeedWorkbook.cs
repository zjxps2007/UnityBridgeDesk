using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

/// <summary>Portable SpreadsheetML output: typed numbers, explicit blanks, filters and frozen headers. No Office installation.</summary>
public static class SpeedWorkbook
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private sealed record Sheet(string Name, double[] Widths, List<object?[]> Rows, int Header, int FilterEnd, int[] ExtraHeaders);
    private sealed record Percent(double? Value);

    public static void Write(string path, SpeedReport report, CancellationToken ct = default)
    {
        if (report.Trials.Sum(t => (long)(t.Result?.Guest?.Samples.Length ?? 0)) > 1_048_569)
            throw new IOException("호출 표본이 Excel 한 시트의 최대 행 수를 넘었습니다. 앱 요약과 원본 자료에서 확인해 주세요.");
        var sheets = Build(report);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var content = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");
        Save(zip, "[Content_Types].xml", new XElement(content + "Types",
            new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(content + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(content + "Override", new XAttribute("PartName", "/xl/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
            sheets.Select((_, i) => new XElement(content + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{i + 1}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));
        Save(zip, "_rels/.rels", new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml")));
        Save(zip, "xl/workbook.xml", new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R),
            new XElement(S + "bookViews", new XElement(S + "workbookView", new XAttribute("activeTab", 0))),
            new XElement(S + "sheets", sheets.Select((s, i) => new XElement(S + "sheet", new XAttribute("name", s.Name), new XAttribute("sheetId", i + 1), new XAttribute(R + "id", $"rId{i + 1}"))))));
        Save(zip, "xl/_rels/workbook.xml.rels", new XElement(P + "Relationships",
            sheets.Select((_, i) => Relationship($"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.xml")), Relationship($"rId{sheets.Length + 1}", "styles", "styles.xml")));
        Save(zip, "xl/styles.xml", Styles());
        for (int i = 0; i < sheets.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var sheet = sheets[i];
            using var output = zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Optimal).Open();
            using var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });
            xml.WriteStartDocument(); xml.WriteStartElement("worksheet", S.NamespaceName);
            new XElement(S + "dimension", new XAttribute("ref", $"A1:{Column(sheet.Widths.Length)}{sheet.Rows.Count}")).WriteTo(xml);
            new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0), new XAttribute("showGridLines", 0),
                new XElement(S + "pane", new XAttribute("xSplit", 2), new XAttribute("ySplit", sheet.Header), new XAttribute("topLeftCell", $"C{sheet.Header + 1}"),
                    new XAttribute("activePane", "bottomRight"), new XAttribute("state", "frozen")),
                new XElement(S + "selection", new XAttribute("pane", "bottomRight"), new XAttribute("activeCell", $"C{sheet.Header + 1}"), new XAttribute("sqref", $"C{sheet.Header + 1}")))).WriteTo(xml);
            new XElement(S + "sheetFormatPr", new XAttribute("defaultRowHeight", 22)).WriteTo(xml);
            new XElement(S + "cols", sheet.Widths.Select((w, j) => new XElement(S + "col", new XAttribute("min", j + 1), new XAttribute("max", j + 1),
                new XAttribute("width", w), new XAttribute("customWidth", 1)))).WriteTo(xml);
            xml.WriteStartElement("sheetData", S.NamespaceName);
            for (int row = 1; row <= sheet.Rows.Count; row++)
            {
                if (row % 256 == 0) ct.ThrowIfCancellationRequested();
                bool header = row == sheet.Header || sheet.ExtraHeaders.Contains(row);
                var cells = sheet.Rows[row - 1];
                var node = new XElement(S + "row", new XAttribute("r", row), new XAttribute("ht", row == 1 || header ? 32 : 23), new XAttribute("customHeight", 1));
                for (int col = 1; col <= cells.Length; col++) node.Add(Cell($"{Column(col)}{row}", cells[col - 1], row == 1 ? 1 : header ? 2 : 0));
                node.WriteTo(xml);
            }
            xml.WriteEndElement();
            if (sheet.FilterEnd > sheet.Header)
                new XElement(S + "autoFilter", new XAttribute("ref", $"A{sheet.Header}:{Column(sheet.Widths.Length)}{sheet.FilterEnd}")).WriteTo(xml);
            new XElement(S + "mergeCells", new XAttribute("count", sheet.Header - 1), Enumerable.Range(1, sheet.Header - 1)
                .Select(row => new XElement(S + "mergeCell", new XAttribute("ref", $"A{row}:{Column(sheet.Widths.Length)}{row}")))).WriteTo(xml);
            xml.WriteEndElement(); xml.WriteEndDocument();
        }
    }

    private static Sheet[] Build(SpeedReport report)
    {
        List<object?[]> Rows(string title) => [[title], [$"실행 ID: {report.Run.Id} · {report.Run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}"],
            [report.State + " · " + report.Context], [report.Counts + (report.ToolCleanupNote.Length == 0 ? "" : " · " + report.ToolCleanupNote)], ["저장 기록의 분석 사본입니다. 셀 편집은 원본과 요약을 자동 갱신하지 않습니다."], []];
        var summary = Rows("UnityBridge 벤치 결과");
        summary.Add(["실험", "조건", "버전", "계획", "유효", "제외", "평균(ms)", "중앙값(ms)", "단위"]);
        summary.AddRange(report.Summaries.Select(s => new object?[] { s.Experiment, s.Condition, s.Release, s.Planned, s.Valid, s.Excluded, s.Mean, s.Median, s.Unit }));
        int summaryEnd = summary.Count;
        summary.Add([]); summary.Add(["기준 버전 대비 비교 · 공동 유효 블록만 사용 · 감소율 양수: 시간 감소 / 음수: 시간 증가"]);
        summary.Add(["실험", "조건", "후보 버전", "계획 블록", "유효 블록", "제외 블록", "기준 평균(ms)", "후보 평균(ms)", "시간 감소율"]);
        int compareHeader = summary.Count;
        summary.AddRange(report.Comparisons.Select(c => new object?[] { c.Experiment, c.Condition, c.Candidate, c.Planned, c.Valid, c.Planned - c.Valid, c.BaselineMs, c.CandidateMs, new Percent(c.ReductionPercent / 100) }));
        summary.Add([]); summary.Add(["F01·F03·F04·S01: ms/호출. F02: ms/전체 작업. 서로 다른 실험의 시간을 합산하지 않습니다."]);
        summary.Add(["개별 평균·시간비의 95% 구간은 ‘측정신뢰도’ 시트에서 확인하세요. 표본 부족·제외를 확인하고 전체 조건의 우열로 일반화하지 않습니다."]);
        if (report.GoMeasurementNote.Length > 0) summary.Add([report.GoMeasurementNote]);
        var blocks = Rows("같은 블록의 버전 비교");
        blocks.Add(["실험", "조건", "블록", "기준 버전", "후보 버전", "기준시간(ms)", "후보시간(ms)", "비교 포함", "차이(ms)", "단위", "기준 시행 ID", "후보 시행 ID"]);
        blocks.AddRange(report.Blocks.Select(b => new object?[] { b.Experiment, b.Condition, b.Block, b.Baseline, b.Candidate, b.BaselineMs, b.CandidateMs,
            b.Included ? "포함" : "제외", b.Included ? b.CandidateMs - b.BaselineMs : null, SpeedReport.Unit(b.Experiment), b.BaselineId.ToString(), b.CandidateId.ToString() }));
        var trials = Rows("시행 기록 · 한 행 = 새 프로젝트 시행 1회");
        trials.Add(["시행", "블록", "실험", "조건", "버전", "상태", "정리 확인", "대표시간(ms)", "측정 호출수", "통계 포함", "제외 이유", "시행 ID", "준비시간(ms)", "기록된 원래 시간(ms)"]);
        trials.AddRange(report.Trials.Select(t => new object?[] { t.Order, t.Block, t.Experiment, t.Condition, t.Release, t.Status, t.Cleanup, t.WorkMs,
            t.Result?.Guest?.Samples.Length, t.Included ? "포함" : "제외", t.Reason, t.Trial.Id.ToString(), t.Result?.Guest?.PreparationMs, t.Result?.Guest?.WorkMs }));
        var samples = Rows("개별 호출 표본 · 한 행 = 측정된 CLI 호출 1회");
        samples[4] = ["실패 시행의 부분 표본도 보존합니다. F02의 전체 시간은 시행기록에서 확인하세요. 본문 크기와 stdout 크기는 다릅니다."];
        samples.Add(["시행", "블록", "실험", "조건", "버전", "호출", "호출시간(ms)", "stdout 크기(B)", "시행 상태", "시행 통계", "시행 ID", "호출 판정", "호출 오류", "시작 오프셋(ms)", "오류 내용"]);
        samples.AddRange(report.Trials.SelectMany(t => (t.Result?.Guest?.Samples ?? []).Select(s => new object?[] { t.Order, t.Block, t.Experiment, t.Condition,
            t.Release, s.Index + 1, s.Milliseconds, s.Bytes, t.Status, t.Included ? "포함" : "제외", t.Trial.Id.ToString(),
            s.Outcome ?? "기록 없음", SpeedFailure.Label(s.Outcome == "success" ? "none" : s.FailureKind), s.OffsetMs, s.Error })));
        var stability = Rows("안정성 · 시행과 호출을 구분");
        stability[4] = ["유효 완료 / 평가 대상 시행(환경 준비·호환성 오류, 사용자 중단·미수행 제외; 정리 실패 포함). 과거 기록의 오류·호출 판정은 추정하지 않습니다. 호출 P95는 성공 호출의 관찰 분포이며 독립 표본 신뢰구간이 아닙니다."];
        stability.Add(["실험", "조건", "버전", "계획", "종료", "유효", "유효 완료율", "중단", "미수행", "시간 초과", "응답 불일치", "정리 실패", "기타", "미분류", "관측 호출", "성공 호출", "실패 호출", "판정 없음", "시행 최소(ms)", "시행 최대(ms)", "시행 표준편차(ms)", "성공 호출 P95(ms)", "부하 처리량(요청/초)", "평가 대상", "환경 준비 실패", "벤치 호환성 오류"]);
        stability.AddRange(report.Stability.Select(s => new object?[] { s.Experiment, s.Condition, s.Release, s.Planned, s.Finished, s.Valid,
            new Percent(s.SuccessPercent / 100), s.Cancelled, s.NotRun, s.Timeouts, s.Mismatches, s.CleanupFailures, s.OtherFailures, s.UnknownFailures,
            s.CallsObserved, s.CallsVerified, s.CallsFailed, s.CallsUnknown, s.MinimumMs, s.MaximumMs, s.StandardDeviationMs, s.CallP95Ms, s.RequestsPerSecond, s.Evaluated, s.EnvironmentFailures, s.CompatibilityFailures }));
        var confidence = Rows("측정 신뢰도 · 새 프로젝트 시행 단위 · " + SpeedStatistics.MethodologyId);
        confidence[4] = ["95%는 각 비교의 구간이며 동시 신뢰도가 아닙니다. 시간 감소율: 양수 감소 / 음수 증가. 5회 미만은 구간 미계산. 제외가 있으면 성공쌍에 한정합니다."];
        confidence.Add(["실험", "조건", "후보", "유효 쌍", "감소율 하한", "감소율 상한", "구간 상태", "해석", "다음 실험 블록 추정", "권장 적용 조건"]);
        confidence.AddRange(report.Comparisons.Select(c => new object?[] { c.Experiment, c.Condition, c.Candidate, c.Valid,
            new Percent(c.Evidence?.ReductionLower / 100), new Percent(c.Evidence?.ReductionUpper / 100), c.Evidence?.Status, c.Inference,
            c.CanPlan ? c.Evidence?.SuggestedBlocks : null, c.Planning }));
        int confidenceEnd = confidence.Count;
        confidence.Add([]); confidence.Add(["실험", "조건", "버전", "유효 시행", "평균(ms)", "평균 하한(ms)", "평균 상한(ms)", "95% 구간"]);
        int meanHeader = confidence.Count;
        confidence.AddRange(report.Summaries.Select(s => new object?[] { s.Experiment, s.Condition, s.Release, s.Valid, s.Mean, s.Evidence?.Lower, s.Evidence?.Upper, s.ConfidenceRange }));
        var replication = Rows("반복 설계 · 논문 §9 두 단계 분산·비용 추정");
        replication[4] = ["호출 수 제안은 순서 독립성 확인 후 새 예비 실험에만 사용합니다. 현재 호출 수를 바꾸면 블록 수 추정을 재검토해야 합니다. 목표: 시간비의 상대 반폭 5%."];
        replication.Add(["실험", "조건", "버전", "유효 시행", "시행당 호출", "호출 내 분산(ms²)", "시행 간 분산(ms²)", "호출 비용(ms)", "준비·종료 비용(ms)", "호출 수 제안", "순서 진단", "주의 사항"]);
        replication.AddRange(report.Replication.Select(r => new object?[] { r.Experiment, r.Condition, r.Release, r.Trials, r.CallsPerTrial,
            r.WithinVariance, r.BetweenVariance, r.CallCostMs, r.SetupCostMs, r.SuggestedCalls, r.SequenceCheck, r.Note }));
        var warmups = Rows("사전 실행 순서 · 모든 값은 속도 집계에서 제외");
        warmups[4] = ["과거 기록에 없는 사전 실행 시간은 복원하지 않습니다. 실패한 사전 호출도 기록될 수 있으므로 시행 상태와 함께 읽으세요."];
        warmups.Add(["시행", "블록", "실험", "조건", "버전", "사전 호출", "시간(ms)", "시행 상태"]);
        warmups.AddRange(report.Trials.SelectMany(t => (t.Result?.Guest?.WarmupMilliseconds ?? []).Select((v, i) => new object?[] {
            t.Order, t.Block, t.Experiment, t.Condition, t.Release, i + 1, v, t.Status })));
        return [new("결과요약", [12, 23, 19, 13, 13, 13, 20, 20, 23], summary, 7, summaryEnd, [compareHeader]),
            new("블록비교", [12, 23, 10, 19, 19, 20, 20, 13, 20, 23, 40, 40], blocks, 7, blocks.Count, []),
            new("시행기록", [10, 10, 12, 23, 19, 16, 14, 20, 15, 14, 65, 40, 20, 26], trials, 7, trials.Count, []),
            new("호출표본", [10, 10, 12, 23, 19, 10, 20, 20, 16, 14, 40, 16, 24, 22, 65], samples, 7, samples.Count, []),
            new("안정성", [12, 23, 19, 12, 12, 12, 18, 12, 12, 14, 16, 14, 12, 12, 15, 15, 15, 15, 20, 20, 23, 24, 24, 14, 18, 20], stability, 7, stability.Count, []),
            new("측정신뢰도", [12, 28, 20, 14, 21, 21, 21, 33, 27, 90], confidence, 7, confidenceEnd, [meanHeader]),
            new("반복설계", [12, 28, 20, 14, 16, 24, 24, 22, 25, 18, 52, 110], replication, 7, replication.Count, []),
            new("사전실행", [12, 12, 12, 28, 20, 14, 20, 22], warmups, 7, warmups.Count, [])];
    }
    private static XElement Cell(string reference, object? value, int style)
    {
        if (value is Percent percent) { value = percent.Value; style = 4; }
        var cell = new XElement(S + "c", new XAttribute("r", reference));
        if (value is null) return cell;
        if (value is double number && !double.IsFinite(number)) return cell;
        if (value is double or float or decimal or int or long)
        {
            cell.Add(new XAttribute("s", style != 0 ? style : value is double or float or decimal ? 3 : 5), new XElement(S + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
        else
        {
            string text = value.ToString() ?? "";
            text = string.Concat(text.EnumerateRunes().Where(r => r.Value is 9 or 10 or 13 || r.Value >= 32 && r.Value is not (0xFFFE or 0xFFFF)));
            if (text.Length > 32_767) text = text[..(char.IsHighSurrogate(text[32_749]) ? 32_749 : 32_750)] + "…(원본 참조)";
            cell.Add(new XAttribute("s", style), new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        }
        return cell;
    }
    private static string Column(int number) { string value = ""; while (number > 0) { number--; value = (char)('A' + number % 26) + value; number /= 26; } return value; }
    private static XElement Relationship(string id, string type, string target) => new(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", target));
    private static void Save(ZipArchive zip, string path, XElement value)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "utf-8", "yes"), value).Save(writer);
    }
    private static XElement Styles() => XElement.Parse("""
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2"><numFmt numFmtId="164" formatCode="#,##0.00"/><numFmt numFmtId="165" formatCode="0.0%;-0.0%;0.0%"/></numFmts>
          <fonts count="3"><font><sz val="11"/><color rgb="FF40394F"/><name val="Malgun Gothic"/></font><font><b/><sz val="17"/><color rgb="FF584278"/><name val="Malgun Gothic"/></font><font><b/><sz val="11"/><color rgb="FF40394F"/><name val="Malgun Gothic"/></font></fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEAE3F7"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="6">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" horizontal="left" indent="1"/></xf>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="center" horizontal="right" indent="1"/></xf>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyAlignment="1"><alignment vertical="center" horizontal="right" indent="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" horizontal="center"/></xf>
          </cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """);
}
