using System.Collections.Immutable;
using System.Globalization;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed class RunnerInputException(string field,string message):FormatException(message)
{ public string Field { get; }=field; }

public static class RunnerInput
{
    public static int Integer(string key,string text)
    {
        var (label,min,max)=key switch
        {
            "repeats"=>("독립 반복 수",1,100), "warmups"=>("준비 호출 수",0,100),
            "inner"=>("측정 호출 수",1,1000), "timeout"=>("작업 제한 시간",1,86400),
            "prepare"=>("환경 준비 제한 시간",1,3600), "repairs"=>("AI 수정 허용 횟수",0,10),
            "calls"=>("AI 도구 호출 한도",1,int.MaxValue), _=>throw new ArgumentException("Unknown numeric field.")
        };
        if(!int.TryParse(text.Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out int value)||value<min||value>max)
            throw new RunnerInputException(key,$"{label}에 {min:N0}~{max:N0} 사이의 정수를 입력하세요.");
        return value;
    }
}

// Unfinished text is a draft, never an executable plan. Permission grants are intentionally absent.
public sealed record RunnerDraft(ImmutableDictionary<string,string> Fields,ImmutableArray<ReleaseId> Releases,
    ImmutableArray<string> Experiments,bool Balanced,bool KeepFailed,bool KeepSuccessful)
{
    public void Validate()
    {
        string[] names=["editor","codex","model","reasoning","auth","repeats","warmups","inner","timeout","prepare","calls","repairs"];
        if(Fields is null||Fields.Count>names.Length||Fields.Any(x=>!names.Contains(x.Key)||x.Value is null||x.Value.Length>32760)||
            Releases.IsDefault||Releases.Length>1000||Releases.Any(x=>x.Value==Guid.Empty)||Releases.Distinct().Count()!=Releases.Length||
            Experiments.IsDefault||Experiments.Length>8||Experiments.Any(x=>!new[]{"F01","F02","F03","F04","F05","A01","A02","A03"}.Contains(x)))
            throw new ArgumentException("저장된 입력 초안이 올바르지 않습니다.");
    }
}
