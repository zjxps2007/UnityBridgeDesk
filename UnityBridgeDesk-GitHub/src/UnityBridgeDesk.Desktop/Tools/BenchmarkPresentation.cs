using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public static class BenchmarkPresentation
{
    public static string Condition(string experiment,string variant)=> (experiment,variant) switch
    {
        ("F01","cold")=>"Unity 준비 후 첫 명령",
        ("F01","warm")=>"준비 호출 후 명령",
        ("F02",_)=>$"전체 이동 작업 · {variant}회로 분할",
        ("F03","1024")=>"응답 1 KiB", ("F03","65536")=>"응답 64 KiB", ("F03","1048576")=>"응답 1 MiB",
        ("F04","ready")=>"준비 상태 확인", ("F04","revision")=>"코드 변경 후 복귀",
        ("F05",_)=>"Play 진입·Stop 복귀", ("A01",_)=>"낙하 장면 제작", ("A02",_)=>"프리팹 제작", ("A03",_)=>"물리 도구 제작",
        _=>experiment+" / "+variant
    };
    public static string Unit(string experiment)=>experiment=="F01"?"ms/호출":experiment.StartsWith('A')?"ms/제작·검증":"ms/작업";
    public static IReadOnlyList<ResultGroup> Summarize(IEnumerable<TrialResult> trials)=>HistoryStore.Summarize(trials.Select(trial=>
    {
        if(trial.Experiment!="F01"||trial.Route.Mode!=ExecutionMode.FixedCommands)return trial;
        // Warm calls within a trial are correlated: first average each trial, then compare independent trials.
        var calls=trial.Samples.Where(x=>x.Name=="echo"&&double.IsFinite(x.Milliseconds)&&x.Milliseconds>=0).ToArray();
        double? perCall=calls.Length>0?calls.Average(x=>x.Milliseconds):trial.Variant=="cold"?trial.WorkMs:null;
        return trial with { WorkMs=perCall,EndToEndMs=null };
    }));
    public static string Number(double? value)=>value is { } n&&double.IsFinite(n)?n.ToString("N1"):"—";
}
