using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed record RunnerPreferences(string Editor, string Codex, string Model, string Reasoning, string AuthenticationHome,
    int Repeats, int Warmups, int InnerCalls, int Timeout, int PrepareTimeout, int MaxCalls, int Repairs,
    bool Balanced, bool KeepFailed, bool KeepSuccessful, ImmutableArray<string> Fixed, ImmutableArray<string> Ai)
{
    public void Validate()
    {
        if(Editor is null||Codex is null||Model is null||Reasoning is null||AuthenticationHome is null||Fixed.IsDefault||Ai.IsDefault||
            Repeats is <1 or >100||Warmups is <0 or >100||InnerCalls is <1 or >1000||Timeout is <1 or >86400||PrepareTimeout is <1 or >3600||MaxCalls<1||Repairs is <0 or >10)
            throw new ArgumentException("저장된 실행 조건이 불완전합니다.");
    }
}

public sealed partial class OperationPanel : UserControl,IDisposable
{
    private readonly DeskRuntime runtime;
    private readonly CatalogService catalog;
    private readonly ToolKind tool;
    private readonly ShellSession session;
    private readonly LocalDiscovery localDiscovery;
    private readonly Dictionary<string,TextBox> fields=[];
    private readonly Dictionary<string,CheckBox> experiments=[];
    private readonly ListBox releases=new(){ SelectionMode=SelectionMode.Multiple,MinHeight=74,MaxHeight=150 };
    private readonly TextBox preview=new(){ IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    private readonly TextBox progress=new(){ IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    private readonly TextBox details=new(){ IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    private readonly ListBox history=new(){ MinHeight=110,MaxHeight=200 };
    private readonly TextBox historyFilter=new(){MinWidth=160,Margin=new(0,0,8,8),ToolTip="프로젝트 · 상태 · 실행 ID로 기록 검색"};
    private IReadOnlyList<HistoryItem> historyItems=[];
    private readonly Button start=new(){ Content="구성 확인",IsEnabled=false };
    private readonly CheckBox fixedMode=new(){ Content="고정 명령",Margin=new(0,6,18,6) };
    private readonly CheckBox aiMode=new(){ Content="AI 제작",Margin=new(0,6,18,6) };
    private readonly CheckBox permission=new(){ Content="이 작업 폴더의 파일 변경과 네트워크 사용 허용",Margin=new(0,8,0,8) };
    private readonly CheckBox balanced=new(){ Content="버전 순서 교대 (AB / BA)",IsChecked=true };
    private readonly CheckBox keepFailed=new(){ Content="실패한 복제본 보관",IsChecked=true };
    private readonly CheckBox keepSuccess=new(){ Content="성공한 복제본도 보관" };
    private RunPlan? frozen;
    private RunOptions? options;
    private readonly AtomicJsonStore<RunnerPreferences> settings;
    private readonly TabControl tabs=new();
    private bool loading=true;
    private bool initialized;
    private sealed record ReleaseRow(ReleaseId Id,string Label){public override string ToString()=>Label;}

    public OperationPanel(DeskRuntime runtime,CatalogService catalog,ToolKind tool,ShellSession session,LocalDiscovery? localDiscovery=null)
    {
        this.runtime=runtime;this.catalog=catalog;this.tool=tool;this.session=session;
        this.localDiscovery=localDiscovery??new LocalDiscovery();
        SetResourceReference(BackgroundProperty,"Paper"); FontFamily=new("Malgun Gothic");FontSize=12;
        settings=new(Path.Combine(runtime.DataRoot,"settings","runner-"+tool+".json"),value=>value.Validate());
        InitializeInteraction();
        tabs.SetResourceReference(StyleProperty,"WorkflowTabs");
        if(tool==ToolKind.Benchmark){BuildBenchmark();return;}
        var surface=new DockPanel();
        var stop=new Button{Content="실행 중단",IsEnabled=false,Visibility=Visibility.Collapsed};stop.Click+=(_,_)=>StopOperation();operationStops.Add(stop);
        AutomationProperties.SetAutomationId(start,"OperationPrimary");start.Click+=PreviewClick;
        var actionBar=ActionBar(footerHint,start,stop);DockPanel.SetDock(actionBar,Dock.Bottom);surface.Children.Add(actionBar);surface.Children.Add(tabs);Content=surface;
        var setup=new StackPanel{ Margin=new(18),IsEnabled=false };operationForm=setup;
        tabs.Items.Add(new TabItem{Header="1  실행 구성",Content=Scroll(setup)});
        setup.Children.Add(Text(tool==ToolKind.Installation?"선택한 프로젝트에 Bridge를 적용합니다.":tool==ToolKind.AiWork?"이 도구에서 바로 AI에게 작업을 전달합니다.":"버전마다 새 복제본에서 같은 실험을 실행합니다.",20));
        setup.Children.Add(Text("공통 보관함의 선택은 새 구성에만 반영됩니다. 검토 후에는 프로젝트와 실행 조건이 고정됩니다."));
        Field(setup,"editor","Unity Editor 실행 파일",true);
        setup.Children.Add(Text(tool==ToolKind.Benchmark?"비교할 릴리스 · 여러 개 선택":"사용할 릴리스"));
        releases.SelectionMode=tool==ToolKind.Benchmark?SelectionMode.Multiple:SelectionMode.Single;
        foreach(var release in catalog.Document.Releases) releases.Items.Add(new ReleaseRow(release.Id,release.Label));
        foreach(ReleaseRow release in releases.Items) if(release.Id==catalog.Document.SelectedRelease) releases.SelectedItems.Add(release);
        releases.SelectionChanged+=(_,_)=>Invalidate();setup.Children.Add(releases);
        var refreshReleases=new Button{Content="보관함의 현재 선택 불러오기",HorizontalAlignment=HorizontalAlignment.Left};
        refreshReleases.Click+=(_,_)=>{Invalidate();releases.Items.Clear();foreach(var release in catalog.Document.Releases){var row=new ReleaseRow(release.Id,release.Label);releases.Items.Add(row);if(release.Id==catalog.Document.SelectedRelease)releases.SelectedItems.Add(row);}};
        setup.Children.Add(refreshReleases);
        if(tool==ToolKind.Benchmark)
        {
            var modes=new WrapPanel();modes.Children.Add(fixedMode);modes.Children.Add(aiMode);setup.Children.Add(modes);
            fixedMode.IsChecked=session.Drafts.FixedCommands;aiMode.IsChecked=session.Drafts.AiCreation;
            fixedMode.Click+=(_,_)=>Invalidate();aiMode.Click+=(_,_)=>Invalidate();
            var selection=new WrapPanel();
            foreach(var (id,name) in new[]{("F01","응답"),("F02","호출 분할"),("F03","응답 크기"),("F04","코드 복귀"),("F05","Play/Stop"),("A01","낙하"),("A02","프리팹"),("A03","물리 도구")})
            { var box=new CheckBox{Content=id+" "+name,Margin=new(0,6,14,6),IsChecked=id is "F01" or "A01"};experiments[id]=box;box.Click+=(_,_)=>Invalidate();selection.Children.Add(box); }
            setup.Children.Add(selection);
            setup.Children.Add(Text("현재 과제 수치·관측 규칙은 draft-v1 예비 명세입니다. 결과는 Pilot로 기록하며 버전 우열을 자동 결정하지 않습니다."));
        }
        var limits=new StackPanel();
        Field(limits,"repeats","독립 시행 반복 수",value:"2");Field(limits,"warmups","준비 호출 수",value:"1");Field(limits,"inner","같은 세션의 응답 반복 수",value:"3");
        Field(limits,"timeout","작업 전체 제한 시간 (초)",value:tool==ToolKind.AiWork?"900":"180");Field(limits,"prepare","환경 준비 제한 시간 (초)",value:"600");
        Field(limits,"repairs","AI 과제 단계별 수정 허용 횟수",value:"0");
        limits.Children.Add(balanced);limits.Children.Add(keepFailed);limits.Children.Add(keepSuccess);
        foreach(var box in new[]{balanced,keepFailed,keepSuccess})box.Click+=(_,_)=>Invalidate();
        setup.Children.Add(new Expander{Header="반복 · 제한 시간 · 복제본 보관",Content=limits,Margin=new(0,14,0,10)});
        var aiSetup=new StackPanel();
        Field(aiSetup,"codex","Codex 실행 파일",true);Field(aiSetup,"auth","Codex 로그인 정보",folder:true);
        Field(aiSetup,"model","모델 ID · 직접 지정");Field(aiSetup,"reasoning","추론 수준",value:"medium");Field(aiSetup,"calls","AI 도구 호출 한도",value:"100");
        aiSetup.Children.Add(permission);permission.Click+=(_,_)=>Invalidate();
        aiSetup.Children.Add(Text("인증 파일의 경로만 설정에 저장합니다. 실행마다 인증만 담은 새 홈과 새 대화를 사용합니다. 토큰은 제공자가 보고한 값만 기록하며, 보고되지 않으면 미제공입니다. 호출 한도는 이벤트를 관측한 시점에 중단하므로 이미 시작한 호출은 진행됐을 수 있습니다."));
        setup.Children.Add(new Expander{Header="AI 연결 · 이 도구의 모델 설정",IsExpanded=tool==ToolKind.AiWork,Content=aiSetup,Margin=new(0,8,0,10),Visibility=tool==ToolKind.Installation?Visibility.Collapsed:Visibility.Visible});
        preview.MinHeight=100;preview.MaxHeight=260;
        operationPlan=new Expander{Header="검토한 실행 계획 · 확인 안내",Content=preview,Margin=new(0,10,0,0)};setup.Children.Add(operationPlan);
        tabs.Items.Add(new TabItem{Header="2  진행",Content=new Border{Padding=new(18),Child=progress}});
        var recordPanel=new Grid{Margin=new(20)};recordPanel.RowDefinitions.Add(new(){Height=GridLength.Auto});recordPanel.RowDefinitions.Add(new(){Height=GridLength.Auto});recordPanel.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        var actions=new WrapPanel();var refresh=new Button{Content="기록 새로고침",Margin=new(0,0,8,8)};refresh.Click+=async(_,_)=>await RefreshHistory();actions.Children.Add(refresh);
        actions.Children.Add(Text("검색"));actions.Children.Add(historyFilter);historyFilter.TextChanged+=(_,_)=>FilterHistory();
        var export=new Button{Content="선택 기록 내보내기",Margin=new(0,0,8,8)};export.Click+=ExportClick;actions.Children.Add(export);recordPanel.Children.Add(actions);
        var recover=new Button{Content="미완료 기록 확인",Margin=new(0,0,8,8)};actions.Children.Add(recover);
        recover.Click+=async(_,_)=>
        {
            recover.IsEnabled=false;
            try
            {
                if(runtime.IsRunning)throw new IOException("진행 중인 작업을 마친 뒤 확인하세요.");
                int count=await new HistoryStore(runtime.DataRoot).RecoverInterruptedAsync();
                await RefreshHistory();
                if(!disposed)InputStatusChanged?.Invoke($"{count}개 미완료 기록을 중단 기록으로 표시했습니다.");
            }
            catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){if(!disposed){details.Text=error.Message;InputStatusChanged?.Invoke(error.Message);}}
            finally{recover.IsEnabled=true;}
        };
        Grid.SetRow(history,1);recordPanel.Children.Add(history);Grid.SetRow(details,2);recordPanel.Children.Add(details);history.SelectionChanged+=HistorySelected;
        history.MinHeight=64;history.Height=100;
        tabs.Items.Add(new TabItem{Header="3  기록",Content=recordPanel});
        tabs.SelectedIndex=0;
        tabs.SelectionChanged+=(_,e)=>{if(e.Source==tabs)UpdateOperationControls();};
        runtime.Changed+=RuntimeChanged;
        Loaded+=async(_,_)=>{if(initialized||disposed)return;initialized=true;try{await LoadSettings();await ResolveLocalSetupAsync();await RefreshHistory(requestedHistoryRun);}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){preview.Text="저장된 설정을 확인해 주세요: "+error.Message;}finally{loading=false;if(inputDirty)QueueInputSave();UpdateOperationControls();}};
    }
    public void Dispose(){runtime.Changed-=RuntimeChanged;DisposeBenchmark();DisposeInteraction();}
    private static TextBlock Text(string value,double size=12)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new(0,5,0,8)};
    private void Field(Panel parent,string key,string label,bool file=false,bool folder=false,string value="")
    {
        bool connection=key is "editor" or "codex" or "auth";
        if(!connection)parent.Children.Add(Text(label));var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var input=new TextBox{Text=value,MinWidth=120,MinHeight=36,Padding=new(10,6,10,6),VerticalContentAlignment=VerticalAlignment.Center,Margin=new(0,0,8,5),MaxLength=32760};
        if(key is "repeats" or "warmups" or "inner" or "timeout" or "prepare" or "repairs" or "calls"){input.Width=150;input.HorizontalAlignment=HorizontalAlignment.Left;}
        fields[key]=input;AutomationProperties.SetName(input,label);AutomationProperties.SetAutomationId(input,"Runner-"+key);input.TextChanged+=(_,_)=>{ClearInputProblem(key);PathEdited(key);Invalidate();};grid.Children.Add(input);
        if(file||folder)
        {
            var browse=new Button{Content=key=="auth"?"폴더 선택":key=="editor"?"Unity.exe 찾기":key=="codex"?"실행 파일 찾기":"찾기",Margin=new(0,0,0,5)};Grid.SetColumn(browse,1);grid.Children.Add(browse);
            browse.Click+=(_,_)=>{ if(folder){var picker=new OpenFolderDialog();if(picker.ShowDialog(Window.GetWindow(this))==true)input.Text=picker.FolderName;}else{var picker=new OpenFileDialog{Filter="Windows 프로그램|*.exe"};if(picker.ShowDialog(Window.GetWindow(this))==true)input.Text=picker.FileName;} };
        }
        if(connection)AddPathDiscovery(parent,key,grid);else parent.Children.Add(grid);
    }
    private void Invalidate(){if(loading||disposed)return;formRevision++;frozen=null;options=null;preview.Clear();QueueInputSave();if(tool==ToolKind.Benchmark)UpdateBenchmarkSetup();else UpdateOperationControls();}
    private int formRevision;
    private int Number(string key)=>RunnerInput.Integer(key,fields[key].Text);
    private string Value(string key)=>fields[key].Text.Trim().Trim('"');
    private async Task LoadSettings()
    {
        var read=await settings.LoadAsync();if(read.Value is not { } value){if(read.Status!=ReadStatus.Missing)preview.Text="저장된 실행 설정을 읽지 못했습니다: "+read.Status+". 실행 파일 경로와 조건을 다시 지정하세요.";await LoadInputDraft();return;}
        foreach(var item in new[]{("editor",value.Editor),("codex",value.Codex),("model",value.Model),("reasoning",value.Reasoning),("auth",value.AuthenticationHome),
            ("repeats",value.Repeats.ToString()),("warmups",value.Warmups.ToString()),("inner",value.InnerCalls.ToString()),("timeout",value.Timeout.ToString()),("prepare",value.PrepareTimeout.ToString()),("calls",value.MaxCalls.ToString()),("repairs",value.Repairs.ToString())})fields[item.Item1].Text=item.Item2;
        balanced.IsChecked=value.Balanced;keepFailed.IsChecked=value.KeepFailed;keepSuccess.IsChecked=value.KeepSuccessful;
        foreach(var e in experiments)e.Value.IsChecked=value.Fixed.Contains(e.Key)||value.Ai.Contains(e.Key);
        await LoadInputDraft();
    }
    private async void PreviewClick(object sender,RoutedEventArgs e)
    {
        if(loading||operationBusy||operationReviewBusy)return;
        if(tabs.SelectedIndex!=0){tabs.SelectedIndex=0;return;}
        if(frozen is not null&&options is not null){StartClick(sender,e);return;}
        operationReviewBusy=true;UpdateOperationControls();
        try{bool valid=await PrepareAsync();if(valid&&operationPlan is not null)operationPlan.IsExpanded=true;}finally{operationReviewBusy=false;UpdateOperationControls();}
    }
    private async Task<bool> PrepareAsync()
    {
        int revision=formRevision;
        try
        {
            if(inputProblem is { } problem)ClearInputProblem(problem);
            if(!catalog.CanWrite)throw new InvalidOperationException("이 보관함이 읽기 전용입니다. 먼저 사용 중인 Desk를 확인하세요.");
            if(!File.Exists(Value("editor")))throw new RunnerInputException("editor","Unity Editor 실행 파일을 찾을 수 없습니다. ‘위치 확인 · 직접 지정’에서 Unity.exe를 선택하세요.");
            BenchmarkModes modes=tool==ToolKind.Benchmark?(fixedMode.IsChecked==true?BenchmarkModes.FixedCommands:0)|(aiMode.IsChecked==true?BenchmarkModes.AiCreation:0):BenchmarkModes.None;
            var selected=releases.SelectedItems.Cast<ReleaseRow>().ToArray();if(selected.Length==0)throw new InvalidOperationException("릴리스를 선택하세요.");
            var document=catalog.Document with {SelectedRelease=selected[0].Id};var draft=document.CreateDraft(tool,modes);draft.Releases.Clear();
            foreach(var release in selected)draft.Releases.Add((document with{SelectedRelease=release.Id}).CreateDraft(tool,modes).Releases[0]);
            var fixedIds=experiments.Where(x=>x.Value.IsChecked==true&&x.Key.StartsWith('F')).Select(x=>x.Key).ToImmutableArray();
            var aiIds=experiments.Where(x=>x.Value.IsChecked==true&&x.Key.StartsWith('A')).Select(x=>x.Key).ToImmutableArray();
            var bench=new BenchOptions(fixedIds,aiIds,Number("repeats"),Number("warmups"),Number("inner"),TimeoutSeconds:Number("timeout"),PrepareTimeoutSeconds:Number("prepare"),
                BalancedOrder:balanced.IsChecked==true,KeepFailedClones:keepFailed.IsChecked==true,KeepSuccessfulClones:keepSuccess.IsChecked==true,RepairAttempts:Number("repairs"));
            AiOptions? profile=null;
            if(tool==ToolKind.AiWork||modes.HasFlag(BenchmarkModes.AiCreation))
            {
                if(!File.Exists(Value("codex")))throw new RunnerInputException("codex","Codex 실행 파일을 찾을 수 없습니다. 실행 파일 경로를 확인하세요.");
                if(string.IsNullOrWhiteSpace(Value("model")))throw new RunnerInputException("model","AI 작업에 사용할 모델 ID를 입력하세요.");
                if(!File.Exists(Path.Combine(Value("auth"),"auth.json")))throw new RunnerInputException("auth","로그인 파일을 찾을 수 없습니다. ‘설정 방법’에서 안내를 확인하거나 auth.json이 있는 폴더를 선택하세요.");
                profile=new(Value("codex"),Value("model"),Value("reasoning"),Value("auth"),Number("timeout"),Number("calls"),null,permission.IsChecked==true);profile.Validate();
                draft.AiProfile=new(AiProfileId.New(),"Codex CLI",profile.Model,profile.Reasoning,null);
            }
            draft.Budget=new(TimeSpan.FromSeconds(Number("timeout")),Math.Max(1,Number("repairs")+1),null,profile?.MaxToolCalls);
            draft.InputSha256=ProjectFiles.HashText(tool==ToolKind.AiWork?session.Drafts.AiPrompt:session.Drafts.BenchmarkNote);
            draft.ExperimentSpecSha256=ProjectFiles.HashText(JsonSerializer.Serialize(bench,DeskJson.Options));
            frozen=draft.Freeze();options=new(Value("editor"),bench,profile,session.Drafts.AiPrompt,tool==ToolKind.Installation);
            try{UnityEnvironment.VerifyEditor(frozen.Project,options.UnityExecutable);}
            catch(Exception error)when(error is IOException or InvalidDataException or ArgumentException){throw new RunnerInputException("editor",error.Message);}
            int count=tool==ToolKind.Benchmark?BenchSchedule.Build(frozen,bench).Length:1;
            if(tool==ToolKind.Installation&&frozen.Releases[0].ConnectorSource is null)throw new InvalidOperationException("설치할 Connector 폴더가 연결된 릴리스를 선택하세요.");
            if(tool==ToolKind.AiWork&&string.IsNullOrWhiteSpace(options.Instruction))throw new InvalidOperationException("AI 작업 창에 지시를 입력하세요.");
            var project=catalog.Document.Projects.Single(x=>x.Project.Id==frozen.Project.Id);
            preview.Text=$"대상: {frozen.Project.DisplayName}\n{frozen.Project.RootPath}\nEditor: {frozen.Project.UnityBuild}\n릴리스: {string.Join(", ",selected.Select(x=>x.Label))}\n실행 ID: {frozen.RunId.Value}\n총 {count}개 시행 · 제한 {bench.TimeoutSeconds}초 / 준비 {bench.PrepareTimeoutSeconds}초\n"+
                (tool==ToolKind.Installation?$"현재 Connector: {project.Observation.PackageSource??"미선언"}\n변경: Packages/manifest.json의 Bridge 의존성 → {frozen.Releases[0].ConnectorSource}\nCLI는 등록 파일을 사용합니다. manifest·lock 백업을 남기며 실패 시 Bridge 의존성만 복원합니다. 첫 설치는 해당 프로젝트의 Editor를 닫은 상태에서 시작하세요.":
                tool==ToolKind.AiWork?$"AI 모델: {profile!.Model} / {profile.Reasoning}\n자연어 요구: {options.Instruction}\n선택한 프로젝트의 파일을 변경합니다. 자동 기능 판정 범위는 Bridge 연결과 컴파일 상태입니다.":
                $"새 프로젝트 복제본·Editor·작업별 AI 홈을 사용합니다. 순서 균형: {bench.BalancedOrder}\n고정 명령: {(modes.HasFlag(BenchmarkModes.FixedCommands)?string.Join(", ",fixedIds):"꺼짐")} / AI 제작: {(modes.HasFlag(BenchmarkModes.AiCreation)?string.Join(", ",aiIds):"꺼짐")}\n명세: draft-v1 · 예비 시험(Pilot). OS 캐시·발열·외부 서비스 기억은 분리되지 않습니다.");
            var preference=new RunnerPreferences(Value("editor"),Value("codex"),Value("model"),Value("reasoning"),Value("auth"),Number("repeats"),Number("warmups"),Number("inner"),Number("timeout"),Number("prepare"),Number("calls"),Number("repairs"),balanced.IsChecked==true,keepFailed.IsChecked==true,keepSuccess.IsChecked==true,fixedIds,aiIds);
            var saved=await settings.SaveAsync(preference);if(saved.Status!=SaveStatus.Saved)preview.Text+="\n설정 저장 실패: "+saved.Status;
            if(revision!=formRevision)return false;
            start.IsEnabled=!runtime.ToolRunning(tool);
            return true;
        }
        catch(Exception error)when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException or UnauthorizedAccessException)
        {frozen=null;options=null;start.IsEnabled=false;preview.Text=error.Message;if(error is RunnerInputException input)ShowInputProblem(input.Field,input.Message);return false;}
    }
    private async void StartClick(object sender,RoutedEventArgs e)
    {
        if(frozen is null||options is null||operationBusy||operationReviewBusy||loading)return;
        var plan=frozen;var runOptions=options;operationBusy=true;operationStopRequested=false;UpdateOperationControls();tabs.SelectedIndex=1;progress.Clear();
        try{await runtime.StartAsync(plan,runOptions);await RefreshHistory(plan.RunId);tabs.SelectedIndex=2;}
        catch(Exception error){progress.AppendText("\n"+EventJournal.Redact(error.Message));}
        finally{frozen=null;options=null;operationBusy=false;UpdateOperationControls();}
    }
    private void RuntimeChanged(RuntimeNotice notice)
    {
        if(notice.Tool!=tool)return;
        Dispatcher.BeginInvoke(()=>{if(disposed)return;bool follow=progress.VerticalOffset>=progress.ExtentHeight-progress.ViewportHeight-8;if(progress.Text.Length>64000)progress.Text=progress.Text[^32000..];progress.AppendText($"{DateTime.Now:HH:mm:ss} · {notice.Message}\n");if(follow)progress.ScrollToEnd();});
    }
    private async Task RefreshHistory(RunId? preferred=null)
    {
        int version=++historyRefreshVersion;
        try
        {
            var items=await Task.Run(async()=>await new HistoryStore(runtime.DataRoot).ReadAsync());
            if(disposed||version!=historyRefreshVersion)return;
            historyItems=items.Where(x=>x.Status is null||x.Status.Tool==tool).ToArray();FilterHistory(preferred);
        }
        catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException)
        {if(!disposed&&version==historyRefreshVersion){historyReadVersion++;details.Text="기록을 불러오지 못했습니다. 다시 새로고침해 주세요.\n"+error.Message;}}
    }
    private void FilterHistory(RunId? preferred=null)
    {
        if(preferred is not null&&historyFilter.Text.Length>0)historyFilter.Clear();
        string? prior=(history.SelectedItem as HistoryItem)?.Directory;string query=historyFilter.Text.Trim();
        var items=historyItems.Where(x=>query.Length==0||$"{x.Status?.Project} {x.Status?.Status} {x.Status?.RunId.Value} {x.Issue}".Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        history.ItemsSource=items;
        history.SelectedItem=preferred is not null?items.FirstOrDefault(x=>x.Status?.RunId==preferred):items.FirstOrDefault(x=>x.Directory==prior)??items.FirstOrDefault();
        if(history.SelectedItem is null){historyReadVersion++;details.Text=preferred is not null?"이번 실행의 기록을 찾지 못했습니다. 진행 화면에서 오류를 확인하세요.":query.Length>0?"검색 결과가 없습니다. 검색어를 지워 전체 기록을 확인하세요.":"아직 실행 기록이 없습니다. 실행이 끝나면 여기에 표시됩니다.";}
    }
    private async void HistorySelected(object sender,SelectionChangedEventArgs e)
    {
        int version=++historyReadVersion;
        if(history.SelectedItem is not HistoryItem item){details.Clear();return;}
        details.Text="선택한 기록을 읽는 중…";
        try
        {
            string content=await Task.Run(()=>
            {
            var trials=HistoryStore.ReadTrials(item.Directory);var groups=HistoryStore.Summarize(trials);
            return $"{item.Issue}\n실행 ID: {item.Status?.RunId.Value}\n프로젝트: {item.Status?.Project}\n상태: {item.Status?.Status}\n{item.Status?.Failure}\n\n"+
                (groups.Count==0?"완료된 시행 기록이 없습니다. 준비 중 실패·중단 여부를 확인하세요.":string.Join("\n\n",groups.Select(g=>$"{g.Release} · {g.Experiment}/{g.Variant} · {g.Specification} · {g.EvidenceKind}\n독립 시행 {g.Trials}회 / 성공 {g.Succeeded} / 실패·중단 {g.Failed}\n평균 {g.MeanMs?.ToString("F1")??"—"} ms · 중앙값 {g.MedianMs?.ToString("F1")??"—"} ms · 범위 {g.MinimumMs?.ToString("F1")??"—"}~{g.MaximumMs?.ToString("F1")??"—"} ms\n{g.Uncertainty}")))+
                "\n\n지시 · 변경 · 독립 검증\n"+HistoryStore.Evidence(item.Directory)+"\n\n시행별 원시 요약\n"+JsonSerializer.Serialize(trials,DeskJson.Options);
            });
            if(!disposed&&version==historyReadVersion)details.Text=content;
        }
        catch(Exception error)when(error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException){if(!disposed&&version==historyReadVersion)details.Text="기록을 읽지 못했습니다: "+error.Message;}
    }
    private async void ExportClick(object sender,RoutedEventArgs e)
    {
        if(exportBusy)return;
        if(history.SelectedItem is not HistoryItem item){if(tool==ToolKind.Benchmark)resultHint.Text="내보낼 실행 기록을 먼저 선택하세요.";else details.Text="내보낼 실행 기록을 먼저 선택하세요.";return;}
        var picker=new OpenFolderDialog{Title="내보낼 상위 폴더 선택"};if(picker.ShowDialog(Window.GetWindow(this))!=true)return;
        exportBusy=true;var button=sender as Button;var caption=button?.Content;
        if(button is not null){button.IsEnabled=false;button.Content="내보내는 중…";}
        string message;
        try{string destination=Path.Combine(picker.FolderName,"Desk-"+item.Status?.RunId.Value.ToString("N")+"-"+DateTime.Now.ToString("HHmmssfff"));await Task.Run(()=>HistoryStore.ExportAsync(item.Directory,destination));message="JSON·CSV 내보내기 완료\n"+destination;}
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException){message="내보내지 못했습니다. 저장 폴더와 권한을 확인하세요.\n"+error.Message;}
        finally{exportBusy=false;if(button is not null){button.IsEnabled=true;button.Content=caption;}}
        if(disposed)return;
        InputStatusChanged?.Invoke(message.Replace('\n',' '));
        if((history.SelectedItem as HistoryItem)?.Directory==item.Directory)
        {details.Text=message;if(tool==ToolKind.Benchmark)resultHint.Text=message;}
    }
}
