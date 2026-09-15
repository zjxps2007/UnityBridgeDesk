using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    private readonly Button benchmarkReset=new(){Content="벤치마크 설정 초기화",Margin=new(0,0,8,6),
        ToolTip="실험·반복·시간 제한·AI 옵션·메모를 기본값으로 되돌립니다. 프로젝트·버전 선택·실행 경로·결과는 유지합니다."};
    private readonly Button benchmarkResetUndo=new(){Content="초기화 되돌리기",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,4,0,0)};
    private readonly StackPanel benchmarkResetNotice=new(){Visibility=Visibility.Collapsed,Margin=new(0,0,0,10)};
    private readonly TextBlock benchmarkResetStatus=Text("");
    private TextBox benchmarkMemo=null!;
    private RunnerDraft benchmarkDefaults=null!;
    private BenchmarkResetSnapshot? benchmarkBeforeReset;
    private bool benchmarkResetBusy,applyingBenchmarkReset;
    private sealed record BenchmarkResetSnapshot(RunnerDraft Inputs,bool FixedCommands,bool AiCreation,string Note,bool Permission);

    private void AddBenchmarkReset(Panel actions)
    {
        AutomationProperties.SetAutomationId(benchmarkReset,"BenchmarkReset");
        AutomationProperties.SetAutomationId(benchmarkResetUndo,"BenchmarkResetUndo");
        AutomationProperties.SetAutomationId(benchmarkResetStatus,"BenchmarkResetStatus");
        benchmarkReset.Click+=async(_,_)=>await ResetBenchmarkAsync(false);
        benchmarkResetUndo.Click+=async(_,_)=>await ResetBenchmarkAsync(true);
        actions.Children.Add(benchmarkReset);
        benchmarkResetNotice.Children.Add(benchmarkResetStatus);benchmarkResetNotice.Children.Add(benchmarkResetUndo);
    }
    private void UpdateBenchmarkResetControls()
    {
        benchmarkReset.IsEnabled=!loading&&!disposed&&!benchmarkBusy&&!benchmarkReviewBusy&&!bridgeSetupBusy&&!benchmarkResetBusy&&!resolvingPaths;
        benchmarkResetUndo.IsEnabled=benchmarkReset.IsEnabled&&benchmarkBeforeReset is not null;
        benchmarkResetUndo.Visibility=benchmarkBeforeReset is null?Visibility.Collapsed:Visibility.Visible;
    }
    private void InvalidateBenchmarkResetUndo()
    {
        if(applyingBenchmarkReset)return;
        benchmarkBeforeReset=null;benchmarkResetNotice.Visibility=Visibility.Collapsed;
    }
    private async Task ResetBenchmarkAsync(bool undo)
    {
        if(tool!=ToolKind.Benchmark||loading||disposed||benchmarkBusy||benchmarkReviewBusy||bridgeSetupBusy||benchmarkResetBusy||resolvingPaths||runtime.ToolRunning(tool))return;
        if(undo&&benchmarkBeforeReset is null)return;
        var before=new BenchmarkResetSnapshot(CaptureRunnerDraft(),fixedMode.IsChecked==true,aiMode.IsChecked==true,benchmarkMemo.Text,permission.IsChecked==true);
        var defaults=DeskDrafts.Empty;
        var target=undo?benchmarkBeforeReset!:new(benchmarkDefaults,defaults.FixedCommands,defaults.AiCreation,defaults.BenchmarkNote,false);
        benchmarkResetBusy=true;applyingBenchmarkReset=true;
        try
        {
            loading=true;
            try
            {
                // Connection paths and the current project/version selection belong to the prepared environment.
                foreach(var (key,value) in target.Inputs.Fields)
                    if(key is not ("editor" or "codex" or "auth")&&fields.TryGetValue(key,out var input))input.Text=value;
                foreach(var (id,box) in experiments)box.IsChecked=target.Inputs.Experiments.Contains(id);
                fixedMode.IsChecked=target.FixedCommands;aiMode.IsChecked=target.AiCreation;
                balanced.IsChecked=target.Inputs.Balanced;keepFailed.IsChecked=target.Inputs.KeepFailed;keepSuccess.IsChecked=target.Inputs.KeepSuccessful;
                permission.IsChecked=target.Permission;benchmarkMemo.Text=target.Note;
                session.SetDrafts(session.Drafts with{FixedCommands=target.FixedCommands,AiCreation=target.AiCreation,BenchmarkNote=target.Note});
                if(inputProblem is { } problem&&problem is not ("editor" or "codex" or "auth"))ClearInputProblem(problem);
            }
            finally{loading=false;}
            Invalidate();
            benchmarkBeforeReset=undo?null:before;
            benchmarkResetStatus.Text=undo?"이전 설정을 저장하고 있어요…":"기본 설정을 저장하고 있어요…";
            benchmarkResetNotice.Visibility=Visibility.Visible;
            UpdateBenchmarkFooter();
            bool saved=await FlushInputAsync();
            if(disposed)return;
            benchmarkResetStatus.Text=(undo?"초기화 전 설정으로 되돌렸어요.":"벤치마크 설정을 기본값으로 되돌렸어요. 고정 명령 F01 · 독립 2회 · 준비 호출 1회 · 응답 반복 3회")+
                (saved?"\n프로젝트·버전 선택·연결 경로·과거 결과는 유지됩니다. 실행 전 ‘구성 확인’을 눌러 주세요.":"\n설정 저장을 완료하지 못했어요. 현재 화면에는 반영했으며 저장 위치와 권한을 확인해 주세요.");
            InputStatusChanged?.Invoke(benchmarkResetStatus.Text);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {if(!disposed){benchmarkResetStatus.Text="설정 저장을 완료하지 못했어요. "+error.Message;benchmarkResetNotice.Visibility=Visibility.Visible;}}
        finally
        {
            applyingBenchmarkReset=false;benchmarkResetBusy=false;
            if(!disposed)UpdateBenchmarkFooter();
        }
    }
}
