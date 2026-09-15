using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    public event Action? CatalogRequested;
    private readonly StackPanel benchmarkForm=new(){Margin=new(18)};
    private readonly StackPanel fixedChoices=new(),aiChoices=new(),resultCards=new();
    private readonly TextBlock setupGuide=Text("준비 항목을 불러오는 중입니다."),setupSummary=Text(""),runStage=Text("아직 시작하지 않았어요.",20),runContext=Text(""),runCounts=Text(""),runElapsed=Text(""),resultHeading=Text("결과를 불러오는 중입니다.",18),resultHint=Text("");
    private readonly ProgressBar runBar=new(){Height=7,Minimum=0,Maximum=1,Margin=new(0,12,0,8)};
    private readonly Button benchmarkPrimary=new(){Content="구성 확인",IsEnabled=false,MinWidth=130},benchmarkStop=new(){Content="실행 중단",Visibility=Visibility.Collapsed,Margin=new(8,0,0,0)};
    private readonly TextBlock footerHint=Text("");
    private readonly Stopwatch runClock=new();
    private readonly DispatcherTimer elapsedTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private Expander? aiSettings,rawEvidence;
    private bool benchmarkBusy,benchmarkReviewBusy,stopRequested;
    private RunId? displayedRun;
    private int resultReadVersion;
    private string lastStage="시작 요청 접수됨";
    private DateTime lastEventAt;

    private void BuildBenchmark()
    {
        AutomationProperties.SetAutomationId(this,"GuidedBenchmark");
        AutomationProperties.SetAutomationId(benchmarkPrimary,"BenchmarkPrimary");
        AutomationProperties.SetAutomationId(setupGuide,"BenchmarkReadiness");
        AutomationProperties.SetAutomationId(runStage,"BenchmarkStage");
        AutomationProperties.SetAutomationId(resultCards,"BenchmarkResults");
        var surface=new DockPanel();Content=surface;
        var footer=ActionBar(footerHint,benchmarkPrimary,benchmarkStop);DockPanel.SetDock(footer,Dock.Bottom);surface.Children.Add(footer);
        surface.Children.Add(tabs);
        tabs.Items.Add(new TabItem{Header="1  준비",Content=Scroll(benchmarkForm)});
        benchmarkForm.Children.Add(Text("같은 조건으로 버전을 비교하세요.",20));
        benchmarkForm.Children.Add(Text("환경 자동 준비 → 시행 시작. 다른 버전과 실험은 직접 선택할 수 있습니다. 완료하면 결과 화면이 자동으로 열립니다."));
        BuildBridgeSetupCard();
        benchmarkForm.Children.Add(Card(setupGuide,"Tint"));
        var shortcuts=new WrapPanel{Margin=new(0,8,0,10)};
        shortcuts.Children.Add(ActionButton("프로젝트·버전 보관함",()=>CatalogRequested?.Invoke()));
        shortcuts.Children.Add(ActionButton("F01 응답 시험 선택",UseResponsePreset));
        shortcuts.Children.Add(ActionButton("지난 결과 보기",()=>tabs.SelectedIndex=2));benchmarkForm.Children.Add(shortcuts);
        Field(benchmarkForm,"editor","1. Unity Editor 실행 파일",true);
        benchmarkForm.Children.Add(Text("2. 비교할 버전 · 클릭해 선택하거나 해제하세요."));
        benchmarkForm.Children.Add(Text("버전 비교에는 두 개 이상을 선택하세요. 하나만 선택하면 해당 버전의 응답을 측정합니다.",11));
        releases.SelectionMode=SelectionMode.Multiple;releases.MinHeight=64;benchmarkForm.Children.Add(releases);
        releases.SelectionChanged+=(_,_)=>Invalidate();SyncBenchmarkCatalog();
        benchmarkForm.Children.Add(Text("3. 측정 방식과 실험"));
        var modes=new WrapPanel();modes.Children.Add(fixedMode);modes.Children.Add(aiMode);benchmarkForm.Children.Add(modes);
        fixedMode.IsChecked=session.Drafts.FixedCommands;aiMode.IsChecked=session.Drafts.AiCreation;
        fixedMode.Click+=BenchmarkModesChanged;aiMode.Click+=BenchmarkModesChanged;
        foreach(var (id,title,description) in new[]{
            ("F01","응답 시간","Unity 준비 후 첫 명령 / 준비 호출 후 명령"),
            ("F02","명령 분할","같은 1,000개 이동 작업을 1·10·100회로 나눔"),
            ("F03","응답 크기","1 KiB·64 KiB·1 MiB 응답과 정확성"),
            ("F04","코드 복귀","준비 상태 / 코드 변경·컴파일 후 응답"),
            ("F05","Play / Stop","진입·복귀 시간과 실제 상태"),
            ("A01","낙하 장면","AI 제작·저장·물리 검증"),
            ("A02","프리팹","AI 제작·개별 예외·변경 전파 검증"),
            ("A03","물리 도구","AI 제작·시작·초기화·일시정지 검증")})
        {
            var box=new CheckBox{Content=id+"  "+title+" — "+description,IsChecked=id is "F01" or "A01",Margin=new(0,5,0,5)};
            // A wrapping label keeps the choices usable at narrow widths.
            box.Content=Text(id+"  "+title+" — "+description,12);experiments[id]=box;box.Click+=(_,_)=>Invalidate();
            (id.StartsWith('F')?fixedChoices:aiChoices).Children.Add(box);
        }
        benchmarkForm.Children.Add(fixedChoices);benchmarkForm.Children.Add(aiChoices);
        Field(benchmarkForm,"repeats","4. 독립 반복 수 · 각 조건·버전마다",value:"2");
        var limits=new StackPanel();Field(limits,"warmups","F01 준비 호출 수",value:"1");Field(limits,"inner","F01 준비 후 조건의 측정 호출 수",value:"3");
        Field(limits,"timeout","시행당 작업 제한 (초)",value:"180");Field(limits,"prepare","시행당 환경 준비 제한 (초)",value:"600");Field(limits,"repairs","AI 단계별 수정 허용 횟수",value:"0");
        limits.Children.Add(balanced);limits.Children.Add(keepFailed);limits.Children.Add(keepSuccess);
        foreach(var box in new[]{balanced,keepFailed,keepSuccess})box.Click+=(_,_)=>Invalidate();
        benchmarkForm.Children.Add(new Expander{Header="세부 조건 · 시간 제한 · 복제본 보관",Content=limits,Margin=new(0,8,0,12)});
        var aiForm=new StackPanel();Field(aiForm,"codex","Codex 실행 파일",true);Field(aiForm,"auth","Codex 로그인 정보",folder:true);Field(aiForm,"model","모델 ID");Field(aiForm,"reasoning","추론 수준",value:"medium");Field(aiForm,"calls","AI 도구 호출 한도",value:"100");aiForm.Children.Add(permission);permission.Click+=(_,_)=>Invalidate();
        aiSettings=new Expander{Header="AI 제작 연결 설정",IsExpanded=true,Content=aiForm,Margin=new(0,8,0,12)};benchmarkForm.Children.Add(aiSettings);
        var memo=new TextBox{Text=session.Drafts.BenchmarkNote,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=64,MaxLength=32000};
        memo.TextChanged+=(_,_)=>{session.SetDrafts(session.Drafts with{BenchmarkNote=memo.Text});Invalidate();};
        benchmarkForm.Children.Add(new Expander{Header="실험 메모",Content=memo,Margin=new(0,8,0,12)});
        benchmarkForm.Children.Add(Card(setupSummary,"Mint"));
        preview.MinHeight=0;preview.MaxHeight=240;
        benchmarkForm.Children.Add(new Expander{Header="검토한 실행 계획 상세",Content=preview,Margin=new(0,10,0,0)});
        var measuring=new StackPanel{Margin=new(18)};measuring.Children.Add(runStage);measuring.Children.Add(runContext);measuring.Children.Add(runBar);measuring.Children.Add(runCounts);measuring.Children.Add(runElapsed);
        measuring.Children.Add(Card(Text("환경 준비 → 작업 측정·검증 → Editor 종료·정리\n완료 수는 시행 개수입니다. 남은 시간의 비율이 아닙니다. Unity 준비가 끝날 때까지 측정값은 나오지 않을 수 있어요.",12),"Tint"));
        progress.MinHeight=120;progress.MaxHeight=220;measuring.Children.Add(new Expander{Header="상세 진행 로그",Content=progress,Margin=new(0,14,0,0)});
        tabs.Items.Add(new TabItem{Header="2  진행",Content=Scroll(measuring)});
        var results=new StackPanel{Margin=new(18)};results.Children.Add(resultHeading);results.Children.Add(resultHint);
        var historyActions=new WrapPanel();historyActions.Children.Add(ActionButton("기록 새로고침",async()=>await RefreshBenchmarkHistory()));var export=new Button{Content="선택 결과 내보내기",Margin=new(0,0,8,6)};export.Click+=ExportClick;historyActions.Children.Add(export);historyActions.Children.Add(ActionButton("미완료 기록 확인",async()=>await RecoverBenchmarkHistory()));results.Children.Add(historyActions);
        results.Children.Add(Text("확인할 실행 · 가장 최근 기록을 기본으로 선택합니다.",11));history.MinHeight=58;history.MaxHeight=115;results.Children.Add(history);history.SelectionChanged+=BenchmarkHistorySelected;
        results.Children.Add(resultCards);details.MinHeight=140;details.MaxHeight=260;
        rawEvidence=new Expander{Header="원시 기록·오류 상세",Content=details,Margin=new(0,12,0,0)};rawEvidence.Expanded+=async(_,_)=>await LoadBenchmarkEvidence();results.Children.Add(rawEvidence);
        tabs.Items.Add(new TabItem{Header="3  결과",Content=Scroll(results)});
        tabs.SelectedIndex=0;
        tabs.SelectionChanged+=(_,e)=>{if(e.Source==tabs)UpdateBenchmarkFooter();};
        benchmarkPrimary.Click+=BenchmarkPrimaryClick;benchmarkStop.Click+=(_,_)=>RequestBenchmarkStop();
        runtime.Changed+=BenchmarkNotice;runtime.ProgressChanged+=BenchmarkProgress;catalog.Changed+=SyncBenchmarkCatalog;
        elapsedTimer.Tick+=(_,_)=>UpdateElapsed();
        Loaded+=async(_,_)=>{if(initialized||disposed)return;initialized=true;try{await LoadSettings();await ResolveLocalSetupAsync();await RefreshBenchmarkHistory(requestedHistoryRun);}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){preview.Text=error.Message;}finally{loading=false;if(inputDirty)QueueInputSave();UpdateBenchmarkSetup();}};
        UpdateBenchmarkSetup();
    }
    private static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private static Border Card(UIElement child,string resource){var card=new Border{Child=child,Padding=new(13),CornerRadius=new(12),Margin=new(0,8,0,8)};card.SetResourceReference(BackgroundProperty,resource);return card;}
    private static Button ActionButton(string label,Action action){var button=new Button{Content=label,Margin=new(0,0,8,6)};button.Click+=(_,_)=>action();return button;}
    private void BenchmarkModesChanged(object sender,RoutedEventArgs e){session.SetDrafts(session.Drafts with{FixedCommands=fixedMode.IsChecked==true,AiCreation=aiMode.IsChecked==true});Invalidate();}
    private void SyncBenchmarkCatalog()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(SyncBenchmarkCatalog);return;}
        var selected=releases.SelectedItems.Cast<ReleaseRow>().Select(x=>x.Id).ToHashSet();
        bool wasLoading=loading;loading=true;releases.Items.Clear();
        foreach(var release in catalog.Document.Releases){var row=new ReleaseRow(release.Id,release.Label);releases.Items.Add(row);if(selected.Contains(row.Id)||(selected.Count==0&&row.Id==catalog.Document.SelectedRelease))releases.SelectedItems.Add(row);}
        loading=wasLoading;if(!benchmarkBusy)Invalidate();
    }
    private BenchOptions CurrentBenchOptions()=>new(experiments.Where(x=>x.Key.StartsWith('F')&&x.Value.IsChecked==true).Select(x=>x.Key).ToImmutableArray(),experiments.Where(x=>x.Key.StartsWith('A')&&x.Value.IsChecked==true).Select(x=>x.Key).ToImmutableArray(),Number("repeats"),Number("warmups"),Number("inner"),TimeoutSeconds:Number("timeout"),PrepareTimeoutSeconds:Number("prepare"),BalancedOrder:balanced.IsChecked==true,KeepFailedClones:keepFailed.IsChecked==true,KeepSuccessfulClones:keepSuccess.IsChecked==true,RepairAttempts:Number("repairs"));
    private BenchmarkModes SelectedModes=>(fixedMode.IsChecked==true?BenchmarkModes.FixedCommands:0)|(aiMode.IsChecked==true?BenchmarkModes.AiCreation:0);
    private void UseResponsePreset()
    {
        if(benchmarkBusy||bridgeSetupBusy)return;
        loading=true;fixedMode.IsChecked=true;aiMode.IsChecked=false;foreach(var pair in experiments)pair.Value.IsChecked=pair.Key=="F01";
        fields["repeats"].Text="2";fields["warmups"].Text="1";fields["inner"].Text="3";loading=false;
        BenchmarkModesChanged(this,new RoutedEventArgs());
    }
    private void UpdateBenchmarkSetup()
    {
        if(aiSettings is null)return;
        fixedChoices.Visibility=fixedMode.IsChecked==true?Visibility.Visible:Visibility.Collapsed;aiChoices.Visibility=aiSettings.Visibility=aiMode.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        if(loading){UpdateBenchmarkFooter();return;}
        var missing=new List<string>();
        var project=catalog.Document.Projects.FirstOrDefault(x=>x.Project.Id==catalog.Document.SelectedProject)?.Project;
        if(project is null)missing.Add("보관함에서 기준 프로젝트를 선택하세요.");
        if(!File.Exists(Value("editor")))missing.Add("Unity Editor 카드에서 자동 확인 결과와 필요한 버전을 확인하세요.");
        else if(project is not null&&LocalDiscovery.EditorVersion(Value("editor"))!=LocalDiscovery.ProjectVersion(project.RootPath))missing.Add("프로젝트와 Unity Editor 버전이 다릅니다. Editor 카드에서 맞는 버전을 선택하세요.");
        if(releases.SelectedItems.Count==0)missing.Add("비교할 버전을 하나 이상 선택하세요.");
        if(SelectedModes==BenchmarkModes.None)missing.Add("고정 명령 또는 AI 제작을 켜세요.");
        try{var spec=CurrentBenchOptions();int cases=spec.Cases(SelectedModes).Length;int count=cases*releases.SelectedItems.Count*spec.Repeats;if(count>1000)missing.Add("한 번에 1,000개 시행까지 가능합니다.");setupSummary.Text=$"{project?.DisplayName??"프로젝트 미선택"}\n{cases}개 조건 × {releases.SelectedItems.Count}개 버전 × 독립 {spec.Repeats}회 = {count}개 시행\n원본을 복제해 사용 · 환경 준비와 명령 시간은 별도 기록\nF01 첫 명령은 Unity 기동·컴파일이 끝난 뒤 측정합니다.";}
        catch(Exception error)when(error is FormatException or OverflowException or InvalidOperationException){missing.Add(error is RunnerInputException?error.Message:"켜 둔 방식의 실험과 반복·시간 제한 값을 확인하세요.");setupSummary.Text="실험과 유효한 반복 수를 선택하면 전체 시행 수가 표시됩니다.";}
        if(aiMode.IsChecked==true)
        {
            if(!File.Exists(Value("codex")))missing.Add("AI 프로그램 카드에서 Codex 실행 파일을 연결하세요.");
            if(!File.Exists(Path.Combine(Value("auth"),"auth.json")))missing.Add("Codex 로그인 정보 카드의 ‘설정 방법’을 확인하세요.");
            if(string.IsNullOrWhiteSpace(Value("model")))missing.Add("AI 제작 연결 설정에서 사용할 모델 ID를 입력하세요.");
            if(permission.IsChecked!=true)missing.Add("AI 작업에 필요한 파일 변경과 네트워크 사용 허용을 선택하세요.");
        }
        setupGuide.Text=missing.Count>0?"다음 항목을 준비하세요.\n• "+string.Join("\n• ",missing):"준비 항목을 입력했어요. 아래 ‘구성 확인’으로 대상과 실행 조건을 검사하세요.";
        if(!string.IsNullOrWhiteSpace(preview.Text)&&frozen is null)setupGuide.Text+="\n최근 확인: "+preview.Text.Split('\n')[0];
        UpdateBenchmarkFooter();
    }
    private void UpdateBenchmarkFooter()
    {
        SummaryChanged?.Invoke();
        if(aiSettings is null)return;
        benchmarkStop.Visibility=benchmarkBusy?Visibility.Visible:Visibility.Collapsed;benchmarkStop.IsEnabled=!stopRequested;
        benchmarkForm.IsEnabled=!loading&&!benchmarkBusy&&!benchmarkReviewBusy;
        foreach(UIElement child in benchmarkForm.Children)child.IsEnabled=!bridgeSetupBusy||ReferenceEquals(child,bridgeSetupCard);
        UpdateBridgeSetupControls();
        benchmarkPrimary.IsEnabled=!loading&&!benchmarkBusy&&!benchmarkReviewBusy&&!bridgeSetupBusy;
        if(bridgeSetupBusy){benchmarkPrimary.Content="환경 준비 중…";footerHint.Text="CLI·Connector를 확인하고 있어요. 위 카드에서 준비를 취소할 수 있습니다.";return;}
        if(benchmarkBusy){benchmarkPrimary.Content=stopRequested?"정리 중…":"진행 중";footerHint.Text="다른 탭을 보아도 작업은 계속됩니다.";return;}
        if(tabs.SelectedIndex==2){benchmarkPrimary.Content="새 벤치 준비";footerHint.Text="성공 여부 → 조건별 시간 → 표본 수 순서로 확인하세요.";return;}
        if(tabs.SelectedIndex==1){benchmarkPrimary.Content="준비로 돌아가기";footerHint.Text="완료 후 결과는 ‘3 결과’에서 다시 볼 수 있어요.";return;}
        if(frozen is not null&&options is not null){int count=BenchSchedule.Build(frozen,options.Benchmark).Length;benchmarkPrimary.Content=$"{count}개 시행 시작";footerHint.Text=$"{frozen.Project.DisplayName}\n{string.Join(" ↔ ",frozen.Releases.Select(x=>x.Label))} · 검토 완료";}
        else{benchmarkPrimary.Content=benchmarkReviewBusy?"확인 중…":"구성 확인";footerHint.Text="프로젝트·버전·실험을 고르고 구성 확인을 누르세요.";}
    }
    private async void BenchmarkPrimaryClick(object sender,RoutedEventArgs e)
    {
        if(benchmarkBusy||benchmarkReviewBusy||bridgeSetupBusy||loading)return;
        if(tabs.SelectedIndex!=0){tabs.SelectedIndex=0;return;}
        if(frozen is null||options is null)
        {
            benchmarkReviewBusy=true;UpdateBenchmarkFooter();
            try{bool valid=await PrepareAsync();if(valid)setupGuide.Text="검토 완료. 아래 버튼을 누르면 새 복제본에서 벤치를 시작합니다.\n"+setupSummary.Text;else UpdateBenchmarkSetup();}
            finally{benchmarkReviewBusy=false;UpdateBenchmarkFooter();}return;
        }
        var plan=frozen;var runOptions=options;displayedRun=plan.RunId;benchmarkBusy=true;stopRequested=false;runClock.Restart();elapsedTimer.Start();lastEventAt=DateTime.Now;
        lastStage="시작 요청 접수됨";runStage.Text=lastStage;runContext.Text=plan.Project.DisplayName+" · "+string.Join(" ↔ ",plan.Releases.Select(x=>x.Label));runBar.Maximum=BenchSchedule.Build(plan,runOptions.Benchmark).Length;runBar.Value=0;runCounts.Text=$"완료 0 / {runBar.Maximum} 시행";progress.Clear();tabs.SelectedIndex=1;UpdateBenchmarkFooter();
        try{await runtime.StartAsync(plan,runOptions);}
        catch(Exception error){lastStage="실행 확인 필요";progress.AppendText(EventJournal.Redact(error.Message));}
        finally{benchmarkBusy=false;runClock.Stop();elapsedTimer.Stop();frozen=null;options=null;runStage.Text=lastStage;UpdateElapsed();await RefreshBenchmarkHistory(plan.RunId);tabs.SelectedIndex=2;UpdateBenchmarkFooter();}
    }
    private void RequestBenchmarkStop(){if(!benchmarkBusy||stopRequested)return;stopRequested=true;runtime.Cancel(tool);runStage.Text="중단 요청됨 · 작업 정리 대기";UpdateBenchmarkFooter();}
    private void BenchmarkNotice(RuntimeNotice notice)
    {
        if(notice.Tool!=ToolKind.Benchmark)return;
        Dispatcher.BeginInvoke(()=>
        {
            if(disposed||displayedRun!=notice.RunId)return;
            bool follow=progress.VerticalOffset>=progress.ExtentHeight-progress.ViewportHeight-8;
            if(progress.Text.Length>64000)progress.Text=progress.Text[^32000..];progress.AppendText($"{DateTime.Now:HH:mm:ss} · {notice.Message}\n");if(follow)progress.ScrollToEnd();
            if(!notice.Running||notice.Message is "대기 중" or "준비 중" or "실행 중")
            {lastStage=notice.Message;if(!stopRequested)runStage.Text=notice.Message=="대기 중"?"실행 슬롯 대기 중":notice.Message=="준비 중"?"기준 환경·실행 파일 확인 중":notice.Message;}
            lastEventAt=DateTime.Now;UpdateElapsed();
        });
    }
    private void BenchmarkProgress(RuntimeProgress state)=>Dispatcher.BeginInvoke(()=>
    {
        if(disposed||displayedRun!=state.RunId)return;lastStage=state.Stage;lastEventAt=DateTime.Now;
        runStage.Text=stopRequested?"중단 요청됨 · "+state.Stage:state.Stage;
        runContext.Text=$"{state.Release} · {state.Experiment} {BenchmarkPresentation.Condition(state.Experiment,state.Variant)} · 반복 {state.Repeat}";
        runBar.Maximum=state.Total;runBar.Value=state.Completed;runCounts.Text=$"완료 {state.Completed} / {state.Total} 시행";UpdateElapsed();
    });
    private void UpdateElapsed()=>runElapsed.Text=$"전체 경과 {runClock.Elapsed:hh\\:mm\\:ss} · 마지막 상태 수신 {lastEventAt:HH:mm:ss}\n남은 시간은 아직 추정하지 않습니다.";
    private async Task RefreshBenchmarkHistory(RunId? preferred=null)
    {
        int refresh=++historyRefreshVersion;
        try
        {
            var items=await Task.Run(async()=>await new HistoryStore(runtime.DataRoot).ReadAsync());
            if(disposed||refresh!=historyRefreshVersion)return;
            var prior=preferred??(history.SelectedItem as HistoryItem)?.Status?.RunId;
            historyItems=items.Where(x=>x.Status?.Tool==tool||x.Status is null).ToArray();
            history.ItemsSource=historyItems;
            // A failed run must never silently open an older successful run as its result.
            var selected=historyItems.FirstOrDefault(x=>x.Status?.RunId==prior);
            history.SelectedItem=selected??(preferred is null?historyItems.FirstOrDefault():null);
            if(history.SelectedItem is null)
            {
                resultReadVersion++;resultCards.Children.Clear();details.Clear();
                resultHeading.Text=preferred is null?"아직 벤치 결과가 없습니다.":"이번 실행의 결과를 찾지 못했습니다.";
                resultHint.Text=preferred is null?"‘1 준비’에서 구성 확인 후 시작하세요. 완료된 실행이 여기에 나타납니다.":$"실행 ID: {preferred.Value.Value}\n‘2 진행’에서 오류를 확인하세요. 이전 결과는 위 목록에서 직접 선택할 수 있습니다.";
            }
        }
        catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){if(disposed||refresh!=historyRefreshVersion)return;resultReadVersion++;resultCards.Children.Clear();details.Clear();resultHeading.Text="기록을 읽지 못했습니다.";resultHint.Text=error.Message;}
    }
    private async Task RecoverBenchmarkHistory()
    {
        try
        {
            if(runtime.IsRunning)throw new IOException("진행 중인 작업을 마친 뒤 확인하세요.");
            int count=await new HistoryStore(runtime.DataRoot).RecoverInterruptedAsync();
            await RefreshBenchmarkHistory();
            resultHint.Text=$"{count}개 기록을 중단 기록으로 표시했습니다. 파일·프로세스는 삭제하거나 종료하지 않았습니다.";
            InputStatusChanged?.Invoke(resultHint.Text);
        }
        catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){resultHint.Text=error.Message;}
    }
    private async void BenchmarkHistorySelected(object sender,SelectionChangedEventArgs e)
    {
        int version=++resultReadVersion;if(history.SelectedItem is not HistoryItem item)return;
        resultHeading.Text="선택한 결과를 읽는 중…";resultCards.Children.Clear();details.Clear();
        try
        {
            var trials=await Task.Run(()=>HistoryStore.ReadTrials(item.Directory));if(version!=resultReadVersion)return;
            var groups=BenchmarkPresentation.Summarize(trials);int succeeded=trials.Count(x=>x.Outcome=="Succeeded"),failed=trials.Count(x=>x.Outcome is "Failed" or "TimedOut"),cancelled=trials.Count(x=>x.Outcome is "Cancelled" or "Interrupted");
            resultHeading.Text=$"{item.Status?.Status??"상태 미확인"} · 성공 {succeeded} / 실패·시간 초과 {failed} / 중단 {cancelled}";
            resultHint.Text=$"{item.Status?.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {Path.GetFileName(item.Status?.Project??"")}\n실행 ID: {item.Status?.RunId.Value}\n{item.Issue} {item.Status?.Failure}".Trim();
            resultCards.Children.Add(Card(Text("읽는 순서: 성공 여부 → 같은 조건의 시간 → 독립 시행 수\n작은 표본으로 종합 우열을 정하지 않습니다. 실패 시간은 성공 평균에서 제외합니다. ‘—’는 시간을 확인할 수 없다는 뜻입니다.\nF01 첫 명령은 Unity 기동·컴파일이 끝난 뒤부터 측정합니다. 준비 후 조건은 시행 안의 호출 평균을 구한 뒤 독립 시행끼리 비교합니다.\n내보낸 원시 CSV의 workMs는 작업 합계이며, F01 화면의 호출당 평균과 구분합니다.",12),"Tint"));
            if(groups.Count==0)resultCards.Children.Add(Text("완료된 측정값이 없습니다. 준비 중 실패하거나 시작 전에 중단되었을 수 있습니다. 아래 오류 상세를 확인하세요."));
            foreach(var condition in groups.GroupBy(g=>new{g.Experiment,g.Variant,g.Specification,g.EvidenceKind}))
            {
                var card=new StackPanel();card.Children.Add(Text(condition.Key.Experiment+" · "+BenchmarkPresentation.Condition(condition.Key.Experiment,condition.Key.Variant),16));
                card.Children.Add(Text($"단위: {BenchmarkPresentation.Unit(condition.Key.Experiment)} · 명세 {condition.Key.Specification} · {condition.Key.EvidenceKind}",11));
                foreach(var g in condition.OrderBy(x=>x.Release)){card.Children.Add(Text($"{g.Release}    평균 {BenchmarkPresentation.Number(g.MeanMs)} {BenchmarkPresentation.Unit(g.Experiment)}",16));card.Children.Add(Text($"독립 시행 {g.Trials}회 · 성공 {g.Succeeded} · 실패/중단 {g.Failed}\n중앙값 {BenchmarkPresentation.Number(g.MedianMs)} · 관측 범위 {BenchmarkPresentation.Number(g.MinimumMs)}~{BenchmarkPresentation.Number(g.MaximumMs)}",12));}
                resultCards.Children.Add(Card(card,"Tint"));
            }
            string Total(Func<TrialResult,double?> selector){var values=trials.Select(selector).ToArray();return values.Length==0||values.Any(x=>!x.HasValue)?"미확인":(values.Sum(x=>x!.Value)/1000).ToString("N2")+"초";}
            resultCards.Children.Add(Text($"기록된 구간 합계 · 환경 준비 {Total(t=>t.PreparationMs)} / 측정 작업 {Total(t=>t.WorkMs)} / 최종 검증 {Total(t=>t.ValidationMs)} / 정리 {Total(t=>t.RecoveryMs)}\n이 합계는 앱 전체 대기 시간을 모두 포함하지 않습니다.",12));
            details.Text=trials.Any(x=>x.Failure is not null)?string.Join("\n",trials.Where(x=>x.Failure is not null).Select(x=>$"{x.Release} · {x.Experiment} · {x.Outcome} · {x.Failure}")):"오류 상세는 패널을 펼치면 불러옵니다.";
            if(rawEvidence?.IsExpanded==true)await LoadBenchmarkEvidence();
        }
        catch(Exception error)when(error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException){if(version==resultReadVersion){resultHeading.Text="이 기록을 읽지 못했습니다.";resultHint.Text=error.Message;}}
    }
    private async Task LoadBenchmarkEvidence()
    {
        if(history.SelectedItem is not HistoryItem item)return;int version=resultReadVersion;details.Text="상세 기록을 읽는 중…";
        try{string evidence=await Task.Run(()=>HistoryStore.Evidence(item.Directory)+"\n\n시행별 원시 요약\n"+JsonSerializer.Serialize(HistoryStore.ReadTrials(item.Directory),DeskJson.Options));if(version==resultReadVersion)details.Text=EventJournal.Redact(evidence);}
        catch(Exception error)when(error is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException){if(version==resultReadVersion)details.Text=error.Message;}
    }
    private void DisposeBenchmark(){elapsedTimer.Stop();runtime.Changed-=BenchmarkNotice;runtime.ProgressChanged-=BenchmarkProgress;catalog.Changed-=SyncBenchmarkCatalog;}
}
