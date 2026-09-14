using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.Storage;

public sealed record HistoryItem(string Directory, RunStatus? Status, string Issue)
{ public override string ToString() => Status is null ? "읽을 수 없는 기록 · " + Path.GetFileName(Directory) : $"{Status.UpdatedAt.LocalDateTime:MM-dd HH:mm} · {Status.Tool} · {Status.Status} · {Path.GetFileName(Status.Project)}"; }
public sealed record ResultGroup(string Release, string Experiment, string Variant, string Specification, string EvidenceKind,
    int Trials, int Succeeded, int Failed, double? MeanMs, double? MedianMs, double? MinimumMs, double? MaximumMs, string Uncertainty);
public sealed class HistoryStore(string dataRoot)
{
    private static string PlanRoot(string directory)
    {
        if(File.Exists(Path.Combine(directory,"plan.json")))return directory;
        string? parent=Path.GetDirectoryName(directory),root=parent is null?null:Path.GetDirectoryName(parent);
        if(parent is not null&&root is not null&&Path.GetFileName(parent)=="runs"&&Guid.TryParseExact(Path.GetFileName(directory),"N",out _))
        {string legacy=Path.Combine(root,Path.GetFileName(directory));if(File.Exists(Path.Combine(legacy,"plan.json")))return legacy;}
        return directory;
    }
    public async Task<IReadOnlyList<HistoryItem>> ReadAsync()
    {
        string root = Path.Combine(dataRoot,"runs"); if (!Directory.Exists(root)) return [];
        var result = new List<HistoryItem>();
        foreach (string directory in Directory.EnumerateDirectories(root).OrderDescending().Take(5000))
        {
            var record = await new AtomicJsonStore<RunStatus>(Path.Combine(directory,"status.json"),_=>{}).LoadAsync();
            string issue=record.Status is ReadStatus.Current or ReadStatus.RecoveredBackup ? "" : record.Status.ToString();
            var status = record.Value;
            if (status?.Status is "대기 중" or "준비 중" or "실행 중") issue="미완료 기록 · 실행 프로세스 상태 확인 필요";
            result.Add(new(directory,status,issue));
        }
        return result.OrderByDescending(x=>x.Status?.UpdatedAt).ToArray();
    }
    public static IReadOnlyList<TrialResult> ReadTrials(string runDirectory)
    {
        string root=Path.Combine(runDirectory,"executions");
        var result=new List<TrialResult>();
        foreach (string path in Directory.Exists(root)?Directory.EnumerateFiles(root,"result.json",SearchOption.AllDirectories).Take(1000):[])
        {
            if (new FileInfo(path).Length>4*1024*1024) throw new InvalidDataException("과대한 시행 기록입니다.");
            var doc=JsonSerializer.Deserialize<VersionedDocument<TrialResult>>(File.ReadAllText(path),DeskJson.Options);
            if (doc?.FormatVersion!=1) throw new InvalidDataException("지원하지 않는 시행 기록입니다.");
            var item=doc.Data;
            item.Route.Validate();
            if (item.WorkMs is <0 || item.Samples.IsDefault || item.Samples.Any(x=>x.Milliseconds<0) ||
                Path.GetFileName(runDirectory)!=item.Route.RunId.Value.ToString("N")) throw new InvalidDataException("시행 기록의 값·실행 ID가 잘못되었습니다.");
            result.Add(item);
        }
        if (result.Select(x=>x.Route.ExecutionId).Distinct().Count()!=result.Count) throw new InvalidDataException("중복 시행 기록입니다.");
        string planRoot=PlanRoot(runDirectory);var planPath=Path.Combine(planRoot,"plan.json");
        if(File.Exists(planPath))
        {
            if(new FileInfo(planPath).Length>4*1024*1024)throw new InvalidDataException("과대한 실행 계획입니다.");
            var planDocument=JsonSerializer.Deserialize<VersionedDocument<RunPlan>>(File.ReadAllText(planPath),DeskJson.Options);
            if(planDocument?.FormatVersion!=1)throw new InvalidDataException("지원하지 않는 실행 계획 형식입니다.");
            var plan=planDocument.Data;
            var statePath=Path.Combine(runDirectory,"status.json");
            var runState=File.Exists(statePath)?JsonSerializer.Deserialize<VersionedDocument<RunStatus>>(File.ReadAllText(statePath),DeskJson.Options)?.Data:null;
            string recordRoot=Path.Combine(planRoot,"executions");
            foreach(string recordPath in Directory.Exists(recordRoot)?Directory.EnumerateFiles(recordRoot,"record.json",SearchOption.AllDirectories):[])
            {
                if(new FileInfo(recordPath).Length>4*1024*1024)throw new InvalidDataException("과대한 생명주기 기록입니다.");
                var recordDocument=JsonSerializer.Deserialize<VersionedDocument<ExecutionRecord>>(File.ReadAllText(recordPath),DeskJson.Options);
                if(recordDocument?.FormatVersion!=1)throw new InvalidDataException("지원하지 않는 생명주기 기록 형식입니다.");
                var record=recordDocument.Data;
                if(record is null||plan is null||result.Any(x=>x.Route.ExecutionId==record.Spec.Route.ExecutionId))continue;
                record.Validate(plan);
                if(Path.GetFileName(runDirectory)!=plan.RunId.Value.ToString("N"))throw new InvalidDataException("다른 실행의 계획입니다.");
                bool running=runState?.Status is "대기 중" or "준비 중" or "실행 중";
                var release=plan.Releases.FirstOrDefault(x=>x.Id==record.Spec.Release);
                result.Add(new(record.Spec.Route,release?.Label??"미확인","미완료 시행","",1,"미확인","Incomplete",
                    running?"Running":"Interrupted","완료 결과 없음 · 원시 생명주기 기록 참조",null,null,null,null,null,null,record.Events[^1].Snapshot.Attempt,null,null,null,[],record.Spec.WorkingDirectory,Directory.Exists(record.Spec.WorkingDirectory))
                    {ReleaseId=release?.Id??default});
            }
        }
        return result;
    }
    public async Task<int> RecoverInterruptedAsync()
    {
        Directory.CreateDirectory(dataRoot);
        using var lease=new FileStream(Path.Combine(dataRoot,"runtime.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        int count=0;
        foreach(var item in await ReadAsync())
        {
            if(item.Status is not {Status:"대기 중" or "준비 중" or "실행 중"} status)continue;
            var saved=await new AtomicJsonStore<RunStatus>(Path.Combine(item.Directory,"status.json"),_=>{}).SaveAsync(status with
            {Status="중단 기록",UpdatedAt=DateTimeOffset.UtcNow,Failure="실행 감독자 종료 후 복구한 기록입니다. 기존 시행을 재개하지 않습니다. 보관된 복제본과 패키지 변경을 확인하세요."});
            if(saved.Status!=SaveStatus.Saved)throw new IOException("중단 기록 복구 실패");
            count++;
        }
        return count;
    }
    public static string Evidence(string runDirectory)
    {
        var text=new StringBuilder();string root=Path.Combine(runDirectory,"executions");if(!Directory.Exists(root))return "";
        foreach(string file in Directory.EnumerateFiles(root,"events.jsonl",SearchOption.AllDirectories).Take(100))
        {
            foreach(string line in File.ReadLines(file))
            {
                if(line.Length>8*1024*1024)continue;
                var entry=JsonSerializer.Deserialize<JournalEvent>(line);
                if(entry is null||entry.Kind is not ("instruction" or "ai.report" or "files.changed" or "package.change" or "validation" or "error" or "cleanup.error" or "verification.scope"))continue;
                text.AppendLine($"{entry.At:O} · {entry.Route.ExecutionId.Value} · {entry.Kind}\n{entry.Text}");
                if(text.Length>200000)return EventJournal.Redact(text.ToString()[..200000])+"\n추가 원시 기록은 JSON 내보내기로 확인하세요.";
            }
        }
        return EventJournal.Redact(text.ToString());
    }
    public static IReadOnlyList<ResultGroup> Summarize(IEnumerable<TrialResult> results) => results.Where(x=>x.Outcome!="Running")
        .GroupBy(x=>new { x.Route.RunId,x.ReleaseId,x.Release,x.Experiment,x.Variant,x.Specification,x.EvidenceKind })
        .Select(group=>
        {
            var times=group.Where(x=>x.Outcome=="Succeeded").Select(x=>x.EndToEndMs??x.WorkMs).Where(x=>x.HasValue).Select(x=>x!.Value).Order().ToArray();
            int success=group.Count(x=>x.Outcome=="Succeeded");
            return new ResultGroup(group.Key.Release,group.Key.Experiment,group.Key.Variant,group.Key.Specification,group.Key.EvidenceKind,
                group.Count(),success,group.Count()-success,times.Length==0?null:times.Average(),times.Length==0?null:times.Length%2==1?times[times.Length/2]:(times[times.Length/2-1]+times[times.Length/2])/2,
                times.Length==0?null:times[0],times.Length==0?null:times[^1],"독립 시행 단위의 관측 범위입니다. 신뢰구간·승패를 추정하지 않습니다.");
        }).ToArray();
    public static string Csv(IEnumerable<TrialResult> records)
    {
        var text=new StringBuilder("schema,runId,executionId,trialId,mode,release,releaseId,experiment,variant,repeat,specification,evidenceKind,outcome,failure,preparationMs,workMs,validationMs,recoveryMs,endToEndMs,firstPass,attempts,inputTokens,outputTokens,toolCalls\r\n");
        foreach (var r in records)
        {
            object?[] values=[1,r.Route.RunId.Value,r.Route.ExecutionId.Value,r.Route.TrialId?.Value,r.Route.Mode,r.Release,r.ReleaseId.Value,r.Experiment,r.Variant,r.Repeat,r.Specification,r.EvidenceKind,r.Outcome,r.Failure,
                r.PreparationMs,r.WorkMs,r.ValidationMs,r.RecoveryMs,r.EndToEndMs,r.FirstPass,r.Attempts,r.InputTokens,r.OutputTokens,r.ToolCalls];
            text.AppendLine(string.Join(",",values.Select(v=>Escape(Convert.ToString(v,CultureInfo.InvariantCulture)??""))));
        }
        return text.ToString();
    }
    private static string Escape(string value)
    {
        value=EventJournal.Redact(value);
        if (value.Length>0 && "=+-@\t\r".Contains(value[0])) value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
    public static async Task ExportAsync(string runDirectory,string destination,CancellationToken ct=default)
    {
        if (Directory.Exists(destination)) throw new IOException("비어 있는 새 내보내기 폴더를 선택하세요.");
        var trials=ReadTrials(runDirectory); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination,"trials.csv"),Csv(trials),new UTF8Encoding(true),ct);
        // Explicit record allowlist. Credentials, private homes, Unity projects and arbitrary files are excluded.
        foreach (string file in Directory.EnumerateFiles(runDirectory,"*",SearchOption.AllDirectories))
        {
            if (!new[]{"plan.json","options.json","environment.json","status.json","result.json","record.json","events.jsonl","change.json","manifest.before.json","packages-lock.before.json"}.Contains(Path.GetFileName(file))) continue;
            if (new FileInfo(file).Length>128*1024*1024) throw new IOException("내보내기 파일 한도 초과");
            string target=Path.Combine(destination,Path.GetRelativePath(runDirectory,file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target,EventJournal.Redact(await File.ReadAllTextAsync(file,ct)),ct);
        }
        string planRoot=PlanRoot(runDirectory);
        if(planRoot!=runDirectory)
        {
            foreach(string file in Directory.EnumerateFiles(planRoot,"*",SearchOption.AllDirectories).Where(x=>Path.GetFileName(x) is "plan.json" or "record.json"))
            {
                if(new FileInfo(file).Length>4*1024*1024)throw new InvalidDataException("이전 형식 계획·생명주기 기록이 너무 큽니다.");
                string target=Path.Combine(destination,Path.GetRelativePath(planRoot,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target,EventJournal.Redact(await File.ReadAllTextAsync(file,ct)),ct);
            }
        }
    }
}
