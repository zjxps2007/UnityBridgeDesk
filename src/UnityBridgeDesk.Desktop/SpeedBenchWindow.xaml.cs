using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Desktop.Themes;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Desktop;

public partial class SpeedBenchWindow : Window
{
    private readonly string dataRoot;
    private readonly string WorkerDirectory;
    private readonly ReleaseRepository repository;
    private readonly ISpeedBenchWorkflow workflow;
    private LocalSpeedSettings settings = new();
    private readonly LocalDiscovery editorDiscovery;
    private readonly List<SpeedRelease> prepared = [];
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SpeedRun? shownRun;
    private SpeedReport? shownReport;
    private int historyRequest;
    private bool syncingHistory;
    private bool updatingFilters;
    private string[]? undoInputs;
    private bool[]? undoExperiments;
    private bool loaded;
    private bool closeAfterCleanup;
    private bool closed;
    private record HistoryItem(string Title, string Detail, string Path) { public string Label => Title + " · " + Detail; }
    private HistoryItem[] historyItems = [];
    private ReportChart[] reportCharts = [];
    private string? trialCondition;
    private bool updatingBaseline;
    public SpeedBenchWindow(string root, LocalDiscovery? discovery = null, ISpeedBenchWorkflow? workflow = null)
    {
        dataRoot = root; repository = new(root); editorDiscovery = discovery ?? new();
        string worker = Path.Combine(AppContext.BaseDirectory, "worker");
        if (!File.Exists(Path.Combine(worker, "UnityBridgeDesk.Worker.exe")))
            worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../UnityBridgeDesk.Worker/bin/Release/net10.0-windows"));
        WorkerDirectory = worker;
        this.workflow = workflow ?? new SpeedBenchWorkflow(root, worker); InitializeComponent(); ApplyTheme(); InitializeNavigation(); InitializeMotion();
        Title += " · v" + typeof(SpeedBenchWindow).Assembly.GetName().Version!.ToString(3);
        foreach (var field in OptionFields) field.TextChanged += PreparationChanged;
        foreach (var field in ExperimentFields) { field.Checked += PreparationChanged; field.Unchecked += PreparationChanged; }
        OfficialCliVersion.TextChanged += PreparationChanged; OfficialPipelineVersion.TextChanged += PreparationChanged;
        GoVersion.TextChanged += PreparationChanged;
        ResultChart.ReleaseSelected += ChartReleaseSelected;
        clockTimer.Tick += (_, _) => UpdateClock(); UpdateClock(); clockTimer.Start();
        Closed += (_, _) => { closed = true; clockTimer.Stop(); };
    }
    private string SettingsPath => Path.Combine(dataRoot, "speed", "local-settings.json");
    private TextBox[] OptionFields => [Repeats, Warmups, Calls, Timeout, PrepareTimeout, Seed, StressRequests, StressConcurrency, StressScenarios];
    private CheckBox[] ExperimentFields => [F01, F02, F03, F04, S01];
    private void UpdateClock() { Clock.Text = DateTime.Now.ToString("HH:mm"); Date.Text = DateTime.Now.ToString("yyyy.MM.dd  ddd"); UpdateElapsed(); }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return; loaded = true;
        await Work(async ct =>
        {
            if (File.Exists(SettingsPath)) settings = await SpeedFiles.Read<LocalSpeedSettings>(SettingsPath, ct);
            else if (File.Exists(Path.Combine(dataRoot, "speed", "settings.json")))
            {
                var old = await SpeedFiles.Read<SpeedSettings>(Path.Combine(dataRoot, "speed", "settings.json"), ct);
                settings = new(Options: old.Options, Palette: old.Palette, SelectedTags: old.SelectedTags);
            }
            else settings = settings with { Palette = (int)((await new ShellPersistence(dataRoot).LoadAsync()).Preferences.Value?.Palette ?? DeskPalette.Lilac) };
            ApplyOfficialSelection(); ApplyOptions(settings.Options ?? new());
            settings = settings with { Palette = Math.Clamp(settings.Palette, 0, 2) }; sidebarExpanded = settings.SidebarExpanded; ApplyTheme(); ApplySidebarLayout(ActualWidth > 0 ? ActualWidth : Width);
            await FindEditors(ct);
            var releases = await repository.CachedList(ct);
            if (releases.Length == 0)
                try { releases = await repository.List(ct); }
                catch (System.Net.Http.HttpRequestException) { Log("릴리스 목록을 가져오지 못했습니다. 연결 후 새로 확인을 눌러 주세요."); }
            DrawReleases(releases);
            await RefreshHistory(ct);
        });
        UpdatePreparation();
        ManagementMenuButton.IsEnabled = true;
    }
    private void Log(string text)
    {
        State.Text = text; Journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        if (Journal.Text.Length > 80000) Journal.Text = Journal.Text[^60000..];
        if (JournalExpander.IsExpanded) Journal.ScrollToEnd();
    }
    private async Task Work(Func<CancellationToken, Task> action, FrameworkElement? scope = null)
    {
        if (operation is not null) return;
        operation = new();
        if (scope is null) { PreparationForm.IsEnabled = false; }
        else scope.IsEnabled = false;
        StartButton.IsEnabled = false; ResultExportActions.IsEnabled = false; ReuseButton.IsEnabled = false; FollowupPlanButton.IsEnabled = false;
        if (scope is null) { CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true; }
        try { await action(operation.Token); }
        catch (OperationCanceledException) { await CleanupPreparedTools(); Log("중단했습니다. 시험 정리 상태는 결과에서 확인할 수 있습니다."); EndInterruptedRun("중단됨", State.Text); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException)
        { await CleanupPreparedTools(); if (scope == EditorSection) EditorStatus.Text = "Unity 확인 실패: " + e.Message;
          if (scope == ReleaseSection) ReleaseStatus.Text = "릴리스 확인 실패: " + e.Message;
          Log(e.Message); LatestNotice.Text = e.Message; NextStep.Text = "준비 탭에서 입력을 확인해 주세요."; EndInterruptedRun("실행 확인 필요", e.Message); }
        finally
        {
            if (closeAfterCleanup && !await CleanupPreparedTools()) closeAfterCleanup = false;
            operation.Dispose(); operation = null; if (scope is not null) scope.IsEnabled = true; PreparationForm.IsEnabled = true; ResultExportActions.IsEnabled = shownReport is not null;
            ReuseButton.IsEnabled = shownRun is not null; CancelButton.Visibility = Visibility.Collapsed; Activity.IsIndeterminate = false;
            FollowupPlanButton.IsEnabled = shownReport is { } report && SpeedStatistics.FollowupOptions(report) is not null;
            UpdatePreparation();
            // Save can complete synchronously inside Closing; defer the second Close until that event returns.
            if (closeAfterCleanup) _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => { if (!closed && operation is null) Close(); }));
        }
    }
    private async Task<bool> CleanupPreparedTools()
    {
        try { await workflow.Cleanup(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Log("공식 도구 임시 파일 정리 실패: " + e.Message); LatestNotice.Text = State.Text; return false; }
    }
    private SpeedOptions ReadOptions()
    {
        bool repeated = F01.IsChecked == true || F03.IsChecked == true || F04.IsChecked == true;
        int Read(TextBox box) => ReadNumber(box);
        var previous = settings.Options ?? new();
        var value = new SpeedOptions(Read(Repeats), repeated || F02.IsChecked == true ? Read(Warmups) : previous.Warmups, repeated ? Read(Calls) : previous.Calls, Read(Timeout), Read(PrepareTimeout), Read(Seed),
            ExperimentFields.Where(c => c.IsChecked == true).Select(c => c.Name).ToArray(), S01.IsChecked == true ? Read(StressRequests) : previous.StressRequests, S01.IsChecked == true ? Read(StressConcurrency) : previous.StressConcurrency,
            S01.IsChecked != true ? previous.StressCommands : string.IsNullOrWhiteSpace(StressScenarios.Text) ? null :
                System.Text.Json.JsonSerializer.Deserialize<StressCommand[]>(StressScenarios.Text, SpeedProtocol.Json)
                    ?? throw new ArgumentException("시나리오는 JSON 배열이어야 합니다."), ReadResearch()); value.Validate(); return value;
    }
    private void ApplyOptions(SpeedOptions options)
    {
        ApplyResearch(options.Research);
        int[] values = [options.Repeats, options.Warmups, options.Calls, options.TimeoutSeconds, options.PrepareSeconds, options.Seed, options.StressRequests, options.StressConcurrency];
        for (int i = 0; i < values.Length; i++) OptionFields[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
        foreach (var c in ExperimentFields) c.IsChecked = options.Selected.Contains(c.Name);
        StressScenarios.Text = options.StressCommands is null ? "" : System.Text.Json.JsonSerializer.Serialize(options.StressCommands, SpeedProtocol.Json);
    }
    private ReleaseChoice[] Selected() => ReleaseList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
        .Select(c => (ReleaseChoice)c.Tag).OrderBy(r => r.Tag == BaselineList?.SelectedItem as string ? 0 : 1).ToArray();
    private void DrawReleases(ReleaseChoice[] releases)
    {
        string[] selected = Selected().Select(r => r.Tag).ToArray();
        if (selected.Length == 0) selected = settings.SelectedTags ?? releases.Where(r => r.CliUrl is not null && !r.Prerelease).Take(2).Select(r => r.Tag).ToArray();
        ReleaseList.Children.Clear();
        foreach (var release in releases)
        {
            var box = new CheckBox { Content = release.Display, Tag = release, IsEnabled = release.CliUrl is not null,
                IsChecked = selected.Contains(release.Tag), Style = (Style)FindResource("TargetChoice") };
            System.Windows.Automation.AutomationProperties.SetName(box, "UnityBridge " + release.Tag);
            box.Checked += PreparationChanged; box.Unchecked += PreparationChanged;
            ReleaseList.Children.Add(box);
        }
        ReleaseStatus.Text = releases.Length == 0 ? "릴리스 새로 확인을 눌러 공식 목록을 가져오세요." : $"{releases.Length}개 릴리스 발견 · CLI + Connector 목록";
        UpdatePreparation();
    }
    private async Task Save(CancellationToken ct, bool tolerateInvalidOptions = false, bool appearanceOnly = false)
    {
        await settingsGate.WaitAsync(ct);
        try
        {
            if (!appearanceOnly)
            {
                SpeedOptions options;
                try { options = ReadOptions(); }
                catch (Exception e) when (tolerateInvalidOptions && e is ArgumentException or System.Text.Json.JsonException) { options = settings.Options ?? new(); }
                settings = settings with { EditorPath = (EditorList.SelectedItem as LocalCandidate)?.Path ?? settings.EditorPath,
                    Options = options, SelectedTags = Selected().Select(r => r.Tag).ToArray(), BaselineTag = BaselineList.SelectedItem as string,
                    OfficialUnity = ReadOfficialSelection(), GoUnity = ReadGoSelection() };
            }
            await SpeedFiles.Write(SettingsPath, settings, ct);
        }
        finally { settingsGate.Release(); }
    }
    private async Task FindEditors(CancellationToken ct)
    {
        EditorStatus.Text = "Unity 찾는 중… 다른 실험 조건을 설정할 수 있습니다.";
        string selected = (EditorList.SelectedItem as LocalCandidate)?.Path ?? settings.EditorPath;
        var discovered = await editorDiscovery.ScanAsync(CatalogDocument.Empty, rememberedPaths: [selected], cancellationToken: ct);
        var editors = discovered.Candidates.Where(c => c.Kind == DiscoveryKind.Editor && c.Version is not null)
            .OrderByDescending(c => Version.Parse(System.Text.RegularExpressions.Regex.Match(c.Version!, @"^\d+\.\d+\.\d+").Value)).ToArray();
        EditorList.ItemsSource = editors;
        EditorList.SelectedItem = editors.FirstOrDefault(c => string.Equals(c.Path, selected, StringComparison.OrdinalIgnoreCase) &&
            (IncludeOfficial.IsChecked != true && IncludeGo.IsChecked != true || c.Version!.StartsWith("6000.", StringComparison.Ordinal)))
            ?? (IncludeOfficial.IsChecked == true || IncludeGo.IsChecked == true ? editors.FirstOrDefault(c => c.Version!.StartsWith("6000.", StringComparison.Ordinal)) : null) ?? editors.FirstOrDefault();
        EditorStatus.Text = editors.Length == 0 ? "설치된 Unity를 찾지 못했습니다. Unity Hub에서 설치·활성화하거나 실행 파일을 선택하세요." : $"Unity {editors.Length}개 발견 · 선택한 Editor로 새 실험 프로젝트를 만듭니다.";
        UpdatePreparation();
    }
    private async void ReleasesClicked(object sender, RoutedEventArgs e) => await Work(async ct => { ReleaseStatus.Text = "공식 릴리스 조회 중…"; DrawReleases(await repository.List(ct)); Log("릴리스 목록을 확인했습니다."); }, ReleaseSection);
    private async Task PrepareReleases(CancellationToken ct)
    {
        var choices = Selected(); var official = ReadOfficialSelection(); var go = ReadGoSelection(); bool aa = IsAa;
        SpeedBenchWorkflow.ValidateSelection(choices.Length, official is not null, go is not null, aa); official?.Validate(); go?.Validate();
        if (EditorList.SelectedItem is not LocalCandidate editor) throw new IOException("설치된 Unity를 선택해 주세요.");
        string? baseline = BaselineList.SelectedItem as string;
        var progress = new Progress<string>(message => { ReleaseStatus.Text = message; Log(message); }); prepared.Clear();
        prepared.AddRange(await Task.Run(() => workflow.Prepare(editor.Path, choices, official, baseline, progress, ct, go, aa), ct));
        ReleaseStatus.Text = "파일 준비 완료 · " + string.Join(" / ", prepared.Select(r => r.Tag)) + "\n" +
            string.Join("\n", prepared.Where(r => r.OfficialUnity is null && r.GoUnity is null).Select(CliDistribution.CompatibilityNote).Where(n => n is not null)); await Save(ct, true);
    }
    private async void PrepareReleasesClicked(object sender, RoutedEventArgs e) => await Work(PrepareReleases, ReleaseSection);
    private async void FindEditorsClicked(object sender, RoutedEventArgs e) => await Work(FindEditors, EditorSection);
    private async void RunClicked(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (EditorList.SelectedItem is not LocalCandidate editor) throw new IOException("설치된 Unity를 선택해 주세요.");
        var options = ReadOptions();
        SpeedBenchWorkflow.ValidateSelection(Selected().Length, IncludeOfficial.IsChecked == true, IncludeGo.IsChecked == true, IsAa); ReadOfficialSelection()?.Validate(); ReadGoSelection()?.Validate();
        if (options.Research?.Stage is "confirmatory" or "aa" or "sensitivity" && options.Repeats % SelectedTargetCount != 0) throw new ArgumentException("실험 횟수는 대상 수의 배수로 설정하세요.");
        BeginRun(options);
        await PrepareReleases(ct); await Save(ct);
        var progress = new Progress<string>(message => { if (options.Research?.QuietProgress != true) Log(message); });
        var live = new Progress<SpeedLiveProgress>(ShowProgress);
        shownRun = await Task.Run(() => workflow.Run(editor.Path, prepared.ToArray(), options, progress, live, ct), ct);
        lastFinishedRun = shownRun;
        await RefreshHistory(CancellationToken.None); ShowRun(lastFinishedRun);
        await BuildReport(shownReport!, CancellationToken.None);
        CompleteRun(lastFinishedRun);
    });
    private async void ResetClicked(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (undoInputs is null)
        {
            undoInputs = OptionFields.Select(b => b.Text).ToArray(); undoExperiments = ExperimentFields.Select(c => c.IsChecked == true).ToArray();
            undoResearch = (ResearchStage.SelectedIndex, ResearchQuestion.Text, ResearchTolerance.Text, ResearchQuiet.IsChecked, ResearchStudyGroup.Text, ResearchSessionNote.Text);
            ApplyOptions(new()); ResetButton.Content = "초기화 되돌리기"; await Save(ct); Log("실험 조건을 초기화했습니다. Unity·릴리스·결과는 유지했습니다.");
        }
        else
        {
            for (int i = 0; i < OptionFields.Length; i++) OptionFields[i].Text = undoInputs[i];
            for (int i = 0; i < ExperimentFields.Length; i++) ExperimentFields[i].IsChecked = undoExperiments![i];
            if (undoResearch is { } research)
            {
                applyingResearch = true;
                try { ResearchStage.SelectedIndex = research.Stage; ResearchQuestion.Text = research.Question;
                    ResearchTolerance.Text = research.Tolerance; ResearchQuiet.IsChecked = research.Quiet; ResearchStudyGroup.Text = research.StudyGroup; ResearchSessionNote.Text = research.SessionNote; }
                finally { applyingResearch = false; undoResearch = null; }
            }
            undoInputs = null; undoExperiments = null; ResetButton.Content = "벤치 설정 초기화";
            try { await Save(ct); } catch (ArgumentException) { } Log("이전 실험 입력을 복원했습니다.");
        }
    });
    private async Task RefreshHistory(CancellationToken ct)
    {
        string root = Path.Combine(dataRoot, "speed", "local-runs"); var items = new List<HistoryItem>();
        if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root)
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "run.json"))).Take(200))
        {
            string file = Path.Combine(directory, "run.json"); if (!File.Exists(file)) continue;
            try { var run = await SpeedFiles.Read<SpeedRun>(file, ct); items.Add(new(run.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + new SpeedReport(run).State, string.Join(" / ", run.Releases.Select(r => r.Tag)) + (run.Options.Research?.StudyGroup is { Length: > 0 } group ? " · 연구 " + group : "") + " · 세션 " + run.Id.ToString("N")[..8], file)); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        historyItems = items.OrderByDescending(i => File.GetLastWriteTimeUtc(i.Path)).ToArray();
        ApplyHistorySearch();
    }
    private async void HistoryClicked(object sender, RoutedEventArgs e) => await Work(RefreshHistory);
    private async void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingHistory && workspacePage == 1)
        {
            historyRequest++;
            if (openingHistoryRequest is not null) HistoryCount.Text = "선택한 기록을 열어 결과를 확인하세요.";
            openingHistoryRequest = null;
        }
        OpenHistoryButton.IsEnabled = History.SelectedItem is not null;
        if (workspacePage == 1 || syncingHistory || History.SelectedItem is not HistoryItem item) return;
        int request = ++historyRequest;
        try { var run = await SpeedFiles.Read<SpeedRun>(item.Path); if (request == historyRequest) ShowRun(run); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { if (request == historyRequest) ReportStatus.Text = "기록을 읽지 못했습니다: " + error.Message; }
    }
    private void ShowRun(SpeedRun run)
    {
        RememberResultPosition();
        recordPositions.TryGetValue(run.Id, out var savedPosition);
        displayedConditionKey = null;
        historyRequest++;
        SelectShownHistory(run);
        var report = new SpeedReport(run); shownRun = run; shownReport = report;
        updatingFilters = true; TrialSearch.Text = "";
        reportCharts = SpeedCharts.Build(report);
        ChartExperiment.ItemsSource = reportCharts.Select(c => c.Experiment).Distinct().ToArray();
        ChartExperiment.SelectedItem = ChartExperiment.Items.Cast<string>().FirstOrDefault(x => x == savedPosition?.Experiment) ?? ChartExperiment.Items.Cast<string>().FirstOrDefault();
        ChartCondition.ItemsSource = reportCharts.Where(c => c.Experiment == ChartExperiment.SelectedItem as string).ToArray();
        ChartCondition.SelectedItem = ChartCondition.Items.Cast<ReportChart>().FirstOrDefault(x => x.Condition == savedPosition?.Condition) ?? ChartCondition.Items.Cast<ReportChart>().FirstOrDefault(); trialCondition = null;
        ExperimentFilter.ItemsSource = new[] { "전체 실험" }.Concat(report.Trials.Select(t => t.Experiment).Distinct()).ToArray();
        ReleaseFilter.ItemsSource = new[] { "전체 버전" }.Concat(run.Releases.Select(r => r.Tag)).ToArray();
        ExperimentFilter.SelectedIndex = ReleaseFilter.SelectedIndex = StatusFilter.SelectedIndex = 0;
        updatingFilters = false; RefreshCondition();
        UpdateOverview(report);
        resultView = savedPosition?.View ?? 3;
        SelectResultView(resultView);
        ResultStatus.Visibility = Visibility.Collapsed;
        ResultStatus.Text = $"{report.State} · {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        ResultContext.Text = $"열어 본 기록: {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}  /  {report.State}";
        ResultMemo.Text = report.Overview;
        Comparison.Text = (run.Options.Research?.Stage is "aa" or "sensitivity" ? "동일 릴리스 측정기 검증 · 제품 우열 비교 아님\n" : "") + $"기준: {report.Baseline} · 같은 조건의 공동 유효 블록만 비교합니다. 조건별 표와 평균이 다를 수 있습니다.";
        ResultExportActions.IsEnabled = operation is null; ReuseButton.IsEnabled = operation is null;
        ReportStatus.Text = "";
    }
    private async Task<string?> BuildReport(SpeedReport report, CancellationToken ct)
    {
        ReportStatus.Text = "읽기용 결과 파일을 만들고 있습니다.";
        try
        {
            string folder = await Task.Run(() => SpeedReportFiles.Export(dataRoot, report, ct), ct);
            if (shownReport == report) ReportStatus.Text = "결과 파일 준비 완료 · 요약 TXT / 분석 Excel / 실패 내역 / 그래프 SVG";
            return folder;
        }
        catch (OperationCanceledException) { ReportStatus.Text = "보고서 작성을 중단했습니다. 저장된 벤치 결과는 유지됩니다."; return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { ReportStatus.Text = "보고서를 저장하지 못했습니다. 내보내기 메뉴의 TXT·Excel·보고서 폴더에서 다시 시도하세요. " + error.Message; return null; }
    }
    private void ChartSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingFilters && shownReport is not null) RefreshCondition();
    }
    private void ActionMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { IsEnabled: true, ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.VerticalOffset = 4;
            menu.IsOpen = true;
        }
    }
    private void ActionMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.SetBinding(Controls.DeskMotion.AllowedProperty, new System.Windows.Data.Binding { Source = this, Path = new PropertyPath(Controls.DeskMotion.AllowedProperty) });
            menu.Focus();
            menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }
    private void ActionMenuPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not ContextMenu menu) return;
        menu.IsOpen = false; menu.PlacementTarget?.Focus(); e.Handled = true;
    }
    private void ActionMenuKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;
        ActionMenuClicked(sender, e); e.Handled = true;
    }
    private async void ResultsFolderClicked(object sender, RoutedEventArgs e) => await OpenReport(null);
    private async void ExcelClicked(object sender, RoutedEventArgs e) => await OpenReport(SpeedReportFiles.WorkbookName);
    private async void SummaryTextClicked(object sender, RoutedEventArgs e) => await OpenReport(SpeedReportFiles.SummaryName);
    private async Task OpenReport(string? fileName)
    {
        if (shownReport is not { } report) return;
        await Work(async ct => { if (await BuildReport(report, ct) is { } folder) Open(fileName is null ? folder : Path.Combine(folder, fileName)); });
    }
    private void CopySummaryClicked(object sender, RoutedEventArgs e)
    {
        if (shownReport is null) return;
        try { Clipboard.SetText(shownReport.Memo()); ReportStatus.Text = "조건별 평균 시간 차이를 정리한 메모를 복사했습니다."; }
        catch (System.Runtime.InteropServices.ExternalException) { ReportStatus.Text = "다른 앱이 클립보드를 사용 중입니다. 잠시 후 다시 복사하세요."; }
    }
    private void RawResultsFolderClicked(object sender, RoutedEventArgs e)
    { if (shownRun is not null) Open(Path.Combine(dataRoot, "speed", "local-runs", shownRun.Id.ToString("N"))); }
    private void TrialFolderClicked(object sender, RoutedEventArgs e)
    { if (shownRun is not null && Trials.SelectedItem is ReportTrial trial) Open(Path.Combine(dataRoot, "speed", "local-runs", shownRun.Id.ToString("N"), trial.Trial.Id.ToString("N"))); }
    private void TrialSelected(object sender, SelectionChangedEventArgs e)
    {
        var trial = Trials.SelectedItem as ReportTrial;
        CallSequence.SetTrial(trial);
        SequenceDiagnosis.Text = SpeedStatistics.SequenceDescription(trial);
        SynchronizeTrialIndex(trial);
        TrialDetails.Text = trial?.Details ?? "시행을 선택하면 결과와 제외 이유를 확인할 수 있습니다.";
        TrialHeadline.Text = trial is null ? "시행을 선택하세요" : $"#{trial.Order}  {trial.Release}";
        TrialVerdict.Text = trial is null ? "좌측 목록에서 번호를 고르세요." : $"{trial.Status}  /  정리 {trial.Cleanup}  /  통계 {(trial.Included ? "포함" : "제외")}";
        TrialTime.Text = trial is null ? "—" : SpeedReport.Time(trial.WorkMs) + " " + SpeedReport.Unit(trial.Experiment);
        TrialReason.Text = trial is null ? "" : trial.Included ? "응답과 실행 대상을 확인했습니다. " + trial.CallProgress : trial.Reason;
        TrialPreviousButton.IsEnabled = Trials.SelectedIndex > 0;
        TrialNextButton.IsEnabled = Trials.SelectedIndex >= 0 && Trials.SelectedIndex < Trials.Items.Count - 1;
        TrialFolderButton.IsEnabled = shownRun is not null && trial?.Result is not null &&
            Directory.Exists(Path.Combine(dataRoot, "speed", "local-runs", shownRun.Id.ToString("N"), trial.Trial.Id.ToString("N")));
    }
    private void SummarySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || Summary.SelectedItem is not ReportSummary summary) return;
        updatingFilters = true; ExperimentFilter.SelectedItem = summary.Experiment; ReleaseFilter.SelectedItem = summary.Release;
        TrialSearch.Text = "";
        StatusFilter.SelectedIndex = 0; trialCondition = summary.Condition; updatingFilters = false; ApplyTrialFilters();
        SelectResultView(2);
    }
    private void TrialFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || shownReport is null) return;
        updatingFilters = true; Summary.SelectedItem = null; updatingFilters = false; ApplyTrialFilters();
    }
    private void ClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        updatingFilters = true; Summary.SelectedItem = null;
        ReleaseFilter.SelectedIndex = StatusFilter.SelectedIndex = 0;
        TrialSearch.Text = "";
        updatingFilters = false; ApplyTrialFilters();
    }
    private void ApplyTrialFilters()
    {
        if (shownReport is null) return;
        string status = ((StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string) ?? "";
        string? condition = trialCondition;
        string query = TrialSearch.Text.Trim().TrimStart('#');
        var previous = (Trials.SelectedItem as ReportTrial)?.Trial.Id;
        var rows = shownReport.Trials.Where(t => (ExperimentFilter.SelectedIndex <= 0 || t.Experiment == ExperimentFilter.SelectedItem as string) &&
            (ReleaseFilter.SelectedIndex <= 0 || t.Release == ReleaseFilter.SelectedItem as string) && (condition is null || t.Condition == condition) &&
            (status == "" || status == "excluded" && !t.Included || status == t.StatusCode) &&
            MatchesTrialQuery(query, t.Order, $"{t.Release} {t.Status} {t.Experiment} {t.Condition}")).OrderBy(t => t.Order).ToArray();
        syncingIndex = true;
        Trials.ItemsSource = rows;
        TrialIndex.ItemsSource = rows;
        Trials.SelectedItem = rows.FirstOrDefault(t => t.Trial.Id == previous) ?? rows.FirstOrDefault(t => !t.Included) ?? rows.FirstOrDefault();
        TrialIndex.SelectedItem = Trials.SelectedItem; syncingIndex = false;
        TrialScope.Text = rows.Length == 0 ? "일치하는 시행 없음 · 검색·필터를 확인하세요." : $"{rows.Length} / {shownReport.Trials.Length}개 시행";
        ClearTrialFiltersButton.IsEnabled = ReleaseFilter.SelectedIndex > 0 || StatusFilter.SelectedIndex > 0 || query.Length > 0;
    }
    private void LegacyFolderClicked(object sender, RoutedEventArgs e)
    { Open(dataRoot); }
    private void Open(string path)
    { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) when (e is IOException or Win32Exception) { Log("열지 못했습니다: " + e.Message); } }
    private async void BrowseEditorClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity Editor|Unity.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Work(async ct => { await LocalSpeedCoordinator.InspectEditor(dialog.FileName, ct); settings = settings with { EditorPath = dialog.FileName }; EditorList.SelectedItem = null; await FindEditors(ct); await Save(ct, true); }, EditorSection);
    }
    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        operation?.Cancel(); CancelButton.IsEnabled = false;
        State.Text = "중단 요청 · 시험 프로세스와 임시 파일을 정리합니다.";
        if (benchmarkActive) { ProgressTitle.Text = "중단 요청 · 정리 중"; NextStep.Text = "실험 공간을 정리하고 있습니다."; }
    }
    private void ApplyTheme()
    {
        var palette = (DeskPalette)settings.Palette; Palette.Apply(palette);
        ApplyWorkspaceTheme(palette); ApplyLandscape(); UpdateCompanion();
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (operation is not null) { e.Cancel = true; closeAfterCleanup = true; operation.Cancel(); State.Text = "시험 정리가 끝나면 종료합니다."; }
        else if (loaded && !closeAfterCleanup)
        {
            e.Cancel = true; closeAfterCleanup = true;
            await Work(ct => Save(ct, true));
        }
    }
}
