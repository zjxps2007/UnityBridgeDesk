using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    public event Action<string>? InputStatusChanged;
    private readonly DispatcherTimer inputSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(650)};
    private AtomicJsonStore<RunnerDraft> inputStore=null!;
    private bool disposed,inputDirty,operationBusy,operationReviewBusy,operationStopRequested,exportBusy;
    private readonly SemaphoreSlim inputSaveGate=new(1,1);
    private int historyReadVersion,historyRefreshVersion;
    private readonly List<Button> operationStops=[];
    private StackPanel? operationForm;
    private Expander? operationPlan;
    private string lastInstruction="";
    private string? inputProblem;

    private void InitializeInteraction()
    {
        inputStore=new(Path.Combine(runtime.DataRoot,"settings","draft-runner-"+tool+".json"),x=>x.Validate());
        inputSaveTimer.Tick+=async(_,_)=>await FlushInputAsync();
        lastInstruction=session.Drafts.AiPrompt;session.Changed+=ObserveInstruction;
        catalog.Changed+=SetupCatalogChanged;
        setupRelease=catalog.Document.SelectedRelease;
    }
    private void ObserveInstruction(ShellChange change)
    {
        if(disposed||change!=ShellChange.Draft||tool!=ToolKind.AiWork||lastInstruction==session.Drafts.AiPrompt)return;
        lastInstruction=session.Drafts.AiPrompt;
        if(!operationBusy)
        {
            Invalidate();preview.Text="지시가 바뀌었습니다. 구성 확인으로 새 지시를 검토하세요.";
        }
    }
    private void QueueInputSave()
    {
        if(loading||disposed)return;
        inputDirty=true;inputSaveTimer.Stop();inputSaveTimer.Start();
    }
    public async Task<bool> FlushInputAsync()
    {
        inputSaveTimer.Stop();
        await inputSaveGate.WaitAsync();
        try
        {
        if(loading||!inputDirty)return true;
        inputDirty=false;
        var snapshot=new RunnerDraft(fields.ToImmutableDictionary(x=>x.Key,x=>x.Value.Text),
            [..releases.SelectedItems.Cast<ReleaseRow>().Select(x=>x.Id)],
            [..experiments.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key)],
            balanced.IsChecked==true,keepFailed.IsChecked==true,keepSuccess.IsChecked==true);
        var saved=await inputStore.SaveAsync(snapshot);
        bool success=saved.Status==SaveStatus.Saved;
        if(success&&!disposed)await RememberInputPathsAsync();
        if(!success)inputDirty=true;
        if(!disposed)InputStatusChanged?.Invoke(ToolFrame.NameFor(tool)+(success?" 입력 저장됨":" 입력을 저장하지 못했어요 · 경로와 권한을 확인하세요"));
        return success;
        }
        finally{inputSaveGate.Release();}
    }
    private async Task LoadInputDraft()
    {
        var read=await inputStore.LoadAsync();
        if(disposed)return;
        if(read.Value is not { } draft)
        {
            if(read.Status!=ReadStatus.Missing)preview.Text="입력 초안을 읽지 못했습니다. 마지막 검토 설정을 불러왔으니 입력을 다시 확인해 주세요.";
            return;
        }
        foreach(var (key,value) in draft.Fields)if(fields.TryGetValue(key,out var input))input.Text=value;
        SetReleaseSelection(draft.Releases);
        foreach(var (id,box) in experiments)box.IsChecked=draft.Experiments.Contains(id);
        balanced.IsChecked=draft.Balanced;keepFailed.IsChecked=draft.KeepFailed;keepSuccess.IsChecked=draft.KeepSuccessful;
    }
    private void SetReleaseSelection(IEnumerable<ReleaseId> ids)
    {
        var selected=ids.ToHashSet();
        if(releases.SelectionMode==SelectionMode.Single)
            releases.SelectedItem=releases.Items.Cast<ReleaseRow>().FirstOrDefault(row=>selected.Contains(row.Id));
        else
        {
            releases.SelectedItems.Clear();
            foreach(ReleaseRow row in releases.Items)if(selected.Contains(row.Id))releases.SelectedItems.Add(row);
        }
    }
    private void ShowInputProblem(string key,string message)
    {
        inputProblem=key;
        if(!fields.TryGetValue(key,out var input))return;
        input.SetResourceReference(BorderBrushProperty,"AccentInk");input.BorderThickness=new(2);input.ToolTip=message;
        for(DependencyObject? node=input;node is not null;node=(node is Visual?VisualTreeHelper.GetParent(node):null)??LogicalTreeHelper.GetParent(node))
            if(node is Expander expander)expander.IsExpanded=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input,()=>
        {
            if(disposed)return;
            input.BringIntoView();input.Focus();input.SelectAll();
        });
    }
    private void ClearInputProblem(string key)
    {
        if(inputProblem!=key)return;
        fields[key].ClearValue(BorderBrushProperty);fields[key].ClearValue(BorderThicknessProperty);fields[key].ClearValue(ToolTipProperty);inputProblem=null;
    }
    private void UpdateOperationControls()
    {
        SummaryChanged?.Invoke();
        if(tool==ToolKind.Benchmark)return;
        if(operationForm is not null)operationForm.IsEnabled=!loading&&!operationBusy&&!operationReviewBusy;
        start.IsEnabled=!loading&&!operationBusy&&!operationReviewBusy;
        foreach(var stop in operationStops)
        {stop.Visibility=operationBusy?Visibility.Visible:Visibility.Collapsed;stop.IsEnabled=operationBusy&&!operationStopRequested;stop.Content=operationStopRequested?"정리 대기 중":"실행 중단";}
        if(loading){start.Content="불러오는 중…";footerHint.Text="저장된 입력을 불러오고 있어요.";return;}
        if(operationBusy){start.Content=operationStopRequested?"정리 중…":"진행 중";footerHint.Text="진행 탭에서 상태를 확인할 수 있어요.";return;}
        if(tabs.SelectedIndex!=0){start.Content=tabs.SelectedIndex==2?"새 작업 준비":"준비로 돌아가기";footerHint.Text="기록과 입력은 도구를 전환해도 유지됩니다.";return;}
        if(operationReviewBusy){start.Content="확인 중…";footerHint.Text="대상과 실행 조건을 확인하고 있어요.";return;}
        if(frozen is not null&&options is not null){start.Content=tool==ToolKind.Installation?"설치 시작":"AI 작업 시작";footerHint.Text=frozen.Project.DisplayName+" · 검토 완료\n펼쳐진 실행 계획을 확인한 뒤 시작하세요.";return;}
        start.Content="구성 확인";
        footerHint.Text=string.IsNullOrWhiteSpace(preview.Text)?"조건을 입력하고 구성 확인을 누르세요.":preview.Text.Split('\n')[0];
    }
    private static Border ActionBar(TextBlock hint,Button primary,Button stop)
    {
        primary.SetResourceReference(StyleProperty,"PrimaryButton");stop.Margin=new(8,0,0,0);stop.MinHeight=36;
        hint.FontSize=11;hint.Margin=new(0,0,12,0);hint.VerticalAlignment=VerticalAlignment.Center;hint.MaxHeight=54;hint.TextTrimming=TextTrimming.CharacterEllipsis;
        hint.SetBinding(ToolTipProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)){Source=hint});
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.Children.Add(hint);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};actions.Children.Add(primary);actions.Children.Add(stop);Grid.SetColumn(actions,1);grid.Children.Add(actions);
        var bar=new Border{Child=grid,Padding=new(16,10,16,12),BorderThickness=new(0,1,0,0)};bar.SetResourceReference(BackgroundProperty,"Tint");bar.SetResourceReference(BorderBrushProperty,"Line");AutomationProperties.SetAutomationId(bar,"WorkflowActions");return bar;
    }
    private void StopOperation()
    {
        if(!operationBusy||operationStopRequested)return;
        operationStopRequested=true;runtime.Cancel(tool);UpdateOperationControls();
        progress.AppendText("\n중단을 요청했습니다. 작업 정리가 끝날 때까지 기다려 주세요.\n");
    }
    private void DisposeInteraction()
    {
        disposed=true;historyReadVersion++;historyRefreshVersion++;resultReadVersion++;
        inputSaveTimer.Stop();session.Changed-=ObserveInstruction;
        catalog.Changed-=SetupCatalogChanged;setupGeneration++;
    }
}
