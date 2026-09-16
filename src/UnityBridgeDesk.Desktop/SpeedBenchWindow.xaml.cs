using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
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
    private readonly ReleaseRepository repository;
    private LocalSpeedSettings settings = new();
    private readonly LocalDiscovery editorDiscovery;
    private readonly List<SpeedRelease> prepared = [];
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SpeedRun? shownRun;
    private SpeedReport? shownReport;
    private int historyRequest;
    private bool updatingFilters;
    private string[]? undoInputs;
    private bool[]? undoExperiments;
    private bool loaded;
    private bool closeAfterCleanup;
    private bool closed;
    private record HistoryItem(string Label, string Path);
    public SpeedBenchWindow(string root, LocalDiscovery? discovery = null)
    {
        dataRoot = root; repository = new(root); editorDiscovery = discovery ?? new(); InitializeComponent();
        Title += " · v" + typeof(SpeedBenchWindow).Assembly.GetName().Version!.ToString(3);
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/UnityBridgeDesk;component/Assets/desk.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        MascotImage.Source = icon.Frames.MaxBy(f => f.PixelWidth);
        clockTimer.Tick += (_, _) => UpdateClock(); UpdateClock(); clockTimer.Start();
        Closed += (_, _) => { closed = true; clockTimer.Stop(); };
    }
    private string SettingsPath => Path.Combine(dataRoot, "speed", "local-settings.json");
    private TextBox[] OptionFields => [Repeats, Warmups, Calls, Timeout, PrepareTimeout, Seed, StressRequests, StressConcurrency, StressScenarios];
    private CheckBox[] ExperimentFields => [F01, F02, F03, F04, S01];
    private void UpdateClock() { Clock.Text = DateTime.Now.ToString("HH:mm"); Date.Text = DateTime.Now.ToString("yyyy.MM.dd  ddd"); }
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
            ApplyOptions(settings.Options ?? new()); Palette.Apply((DeskPalette)Math.Clamp(settings.Palette, 0, 2));
            await FindEditors(ct);
            var releases = await repository.CachedList(ct);
            if (releases.Length == 0)
                try { releases = await repository.List(ct); }
                catch (System.Net.Http.HttpRequestException) { Log("릴리스 목록을 가져오지 못했습니다. 연결 후 새로 확인을 눌러 주세요."); }
            DrawReleases(releases);
            await RefreshHistory(ct);
        });
    }
    private void Log(string text)
    {
        State.Text = text; Journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        if (Journal.Text.Length > 80000) Journal.Text = Journal.Text[^60000..]; Journal.ScrollToEnd();
    }
    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        operation = new(); PreparationForm.IsEnabled = false; History.IsEnabled = false; ResultExportActions.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible; Activity.IsIndeterminate = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Log("중단했습니다. 시험 정리 상태는 결과에서 확인할 수 있습니다."); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException)
        { Log(e.Message); NextStep.Text = "아래 상태를 확인하고 준비 탭에서 해당 항목을 완료해 주세요."; }
        finally
        {
            operation.Dispose(); operation = null; PreparationForm.IsEnabled = true; History.IsEnabled = true; ResultExportActions.IsEnabled = shownReport is not null;
            CancelButton.Visibility = Visibility.Collapsed; Activity.IsIndeterminate = false;
            // Save can complete synchronously inside Closing; defer the second Close until that event returns.
            if (closeAfterCleanup) _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => { if (!closed && operation is null) Close(); }));
        }
    }
    private SpeedOptions ReadOptions()
    {
        int Read(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : throw new ArgumentException("횟수와 제한 시간에는 정수를 입력하세요.");
        var value = new SpeedOptions(Read(Repeats), Read(Warmups), Read(Calls), Read(Timeout), Read(PrepareTimeout), Read(Seed),
            ExperimentFields.Where(c => c.IsChecked == true).Select(c => c.Name).ToArray(), Read(StressRequests), Read(StressConcurrency),
            string.IsNullOrWhiteSpace(StressScenarios.Text) ? null :
                System.Text.Json.JsonSerializer.Deserialize<StressCommand[]>(StressScenarios.Text, SpeedProtocol.Json)
                    ?? throw new ArgumentException("시나리오는 JSON 배열이어야 합니다.")); value.Validate(); return value;
    }
    private void ApplyOptions(SpeedOptions options)
    {
        int[] values = [options.Repeats, options.Warmups, options.Calls, options.TimeoutSeconds, options.PrepareSeconds, options.Seed, options.StressRequests, options.StressConcurrency];
        for (int i = 0; i < values.Length; i++) OptionFields[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
        foreach (var c in ExperimentFields) c.IsChecked = options.Selected.Contains(c.Name);
        StressScenarios.Text = options.StressCommands is null ? "" : System.Text.Json.JsonSerializer.Serialize(options.StressCommands, SpeedProtocol.Json);
    }
    private ReleaseChoice[] Selected() => ReleaseList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (ReleaseChoice)c.Tag).ToArray();
    private void DrawReleases(ReleaseChoice[] releases)
    {
        string[] selected = Selected().Select(r => r.Tag).ToArray();
        if (selected.Length == 0) selected = settings.SelectedTags ?? releases.Where(r => r.CliUrl is not null && !r.Prerelease).Take(2).Select(r => r.Tag).ToArray();
        ReleaseList.Children.Clear();
        foreach (var release in releases)
        {
            var box = new CheckBox { Content = release.Display, Tag = release, IsEnabled = release.CliUrl is not null,
                IsChecked = selected.Contains(release.Tag), Margin = new(0, 6, 0, 8) };
            ReleaseList.Children.Add(box);
        }
        ReleaseStatus.Text = releases.Length == 0 ? "릴리스 새로 확인을 눌러 공식 목록을 가져오세요." : $"{releases.Length}개 릴리스 · CLI + Connector 조합 비교";
    }
    private async Task Save(CancellationToken ct, bool tolerateInvalidOptions = false)
    {
        SpeedOptions options;
        try { options = ReadOptions(); }
        catch (Exception e) when (tolerateInvalidOptions && e is ArgumentException or System.Text.Json.JsonException) { options = settings.Options ?? new(); }
        settings = settings with { EditorPath = (EditorList.SelectedItem as LocalCandidate)?.Path ?? settings.EditorPath,
            Options = options, SelectedTags = Selected().Select(r => r.Tag).ToArray() };
        await SpeedFiles.Write(SettingsPath, settings, ct);
    }
    private async Task FindEditors(CancellationToken ct)
    {
        string selected = (EditorList.SelectedItem as LocalCandidate)?.Path ?? settings.EditorPath;
        var discovered = await editorDiscovery.ScanAsync(CatalogDocument.Empty, rememberedPaths: [selected], cancellationToken: ct);
        var editors = discovered.Candidates.Where(c => c.Kind == DiscoveryKind.Editor && c.Version is not null)
            .OrderByDescending(c => Version.Parse(System.Text.RegularExpressions.Regex.Match(c.Version!, @"^\d+\.\d+\.\d+").Value)).ToArray();
        EditorList.ItemsSource = editors;
        EditorList.SelectedItem = editors.FirstOrDefault(c => string.Equals(c.Path, selected, StringComparison.OrdinalIgnoreCase)) ?? editors.FirstOrDefault();
        EditorStatus.Text = editors.Length == 0 ? "설치된 Unity를 찾지 못했습니다. Unity Hub에서 설치·활성화하거나 실행 파일을 선택하세요." : $"Unity {editors.Length}개 발견 · 선택한 Editor로 새 실험 프로젝트를 만듭니다.";
        NextStep.Text = editors.Length == 0 ? "설치된 Unity를 먼저 확인해 주세요." : "비교할 버전을 선택하고 벤치 시작을 누르세요. 파일 준비와 정리는 자동입니다.";
    }
    private async void ReleasesClicked(object sender, RoutedEventArgs e) => await Work(async ct => { Log("공식 릴리스 조회 중"); DrawReleases(await repository.List(ct)); Log("릴리스 목록을 확인했습니다."); });
    private async Task PrepareReleases(CancellationToken ct)
    {
        var choices = Selected(); if (choices.Length is < 2 or > 8) throw new ArgumentException("서로 다른 릴리스 2~8개를 선택하세요.");
        var progress = new Progress<string>(Log); prepared.Clear();
        foreach (var choice in choices) prepared.Add(await Task.Run(() => repository.Prepare(choice, progress, ct), ct));
        ReleaseStatus.Text = "파일 준비 완료 · " + string.Join(" / ", prepared.Select(r => r.Tag)) + "\n" +
            string.Join("\n", prepared.Select(CliDistribution.CompatibilityNote).Where(n => n is not null)); await Save(ct);
    }
    private async void PrepareReleasesClicked(object sender, RoutedEventArgs e) => await Work(PrepareReleases);
    private async void FindEditorsClicked(object sender, RoutedEventArgs e) => await Work(FindEditors);
    private async void RunClicked(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (EditorList.SelectedItem is not LocalCandidate editor) throw new IOException("설치된 Unity를 선택해 주세요.");
        var options = ReadOptions(); await LocalSpeedCoordinator.InspectEditor(editor.Path, ct);
        await PrepareReleases(ct); await Save(ct);
        Tabs.SelectedIndex = 1; var progress = new Progress<string>(Log);
        string worker = Path.Combine(AppContext.BaseDirectory, "worker");
        if (!File.Exists(Path.Combine(worker, "UnityBridgeDesk.Worker.exe")))
            worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../UnityBridgeDesk.Worker/bin/Release/net10.0-windows"));
        shownRun = await Task.Run(() => new LocalSpeedCoordinator(dataRoot, new WorkerRunner(Path.Combine(worker, "UnityBridgeDesk.Worker.exe")))
            .Run(editor.Path, prepared.ToArray(), options, worker, progress, ct), ct);
        await RefreshHistory(CancellationToken.None); ShowRun(shownRun); Tabs.SelectedIndex = 2;
        await BuildReport(shownReport!, CancellationToken.None);
    });
    private async void ResetClicked(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (undoInputs is null)
        {
            undoInputs = OptionFields.Select(b => b.Text).ToArray(); undoExperiments = ExperimentFields.Select(c => c.IsChecked == true).ToArray();
            ApplyOptions(new()); ResetButton.Content = "초기화 되돌리기"; await Save(ct); Log("실험 조건을 초기화했습니다. Unity·릴리스·결과는 유지했습니다.");
        }
        else
        {
            for (int i = 0; i < OptionFields.Length; i++) OptionFields[i].Text = undoInputs[i];
            for (int i = 0; i < ExperimentFields.Length; i++) ExperimentFields[i].IsChecked = undoExperiments![i];
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
            try { var run = await SpeedFiles.Read<SpeedRun>(file, ct); items.Add(new(run.StartedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + string.Join(" / ", run.Releases.Select(r => r.Tag)) + " · " + new SpeedReport(run).State, file)); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        History.ItemsSource = items.OrderByDescending(i => File.GetLastWriteTimeUtc(i.Path)).ToArray();
        History.SelectedItem = items.FirstOrDefault(i => shownRun is not null && Path.GetFileName(Path.GetDirectoryName(i.Path)) == shownRun.Id.ToString("N")) ?? items.FirstOrDefault();
    }
    private async void HistoryClicked(object sender, RoutedEventArgs e) => await Work(RefreshHistory);
    private async void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (History.SelectedItem is not HistoryItem item) return;
        int request = ++historyRequest;
        try { var run = await SpeedFiles.Read<SpeedRun>(item.Path); if (request == historyRequest) ShowRun(run); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { if (request == historyRequest) ReportStatus.Text = "기록을 읽지 못했습니다: " + error.Message; }
    }
    private void ShowRun(SpeedRun run)
    {
        historyRequest++;
        var report = new SpeedReport(run); shownRun = run; shownReport = report;
        updatingFilters = true;
        Summary.ItemsSource = report.Summaries; PairSummary.ItemsSource = report.Comparisons;
        StabilityTable.ItemsSource = report.Stability;
        ChartCondition.ItemsSource = SpeedCharts.Build(report); ChartCondition.SelectedIndex = 0;
        ExperimentFilter.ItemsSource = new[] { "전체 실험" }.Concat(report.Trials.Select(t => t.Experiment).Distinct()).ToArray();
        ReleaseFilter.ItemsSource = new[] { "전체 버전" }.Concat(run.Releases.Select(r => r.Tag)).ToArray();
        ExperimentFilter.SelectedIndex = ReleaseFilter.SelectedIndex = StatusFilter.SelectedIndex = 0;
        updatingFilters = false; ApplyTrialFilters();
        ResultStatus.Text = $"{report.State} · {run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        ResultMemo.Text = report.Overview;
        Comparison.Text = $"기준: {report.Baseline} · 같은 조건의 공동 유효 블록만 비교합니다. 조건별 표와 평균이 다를 수 있습니다.";
        ResultExportActions.IsEnabled = operation is null;
        ReportStatus.Text = "";
        NextStep.Text = "요약 메모 → TXT 열기에서 평균 시간 차이를 읽고, 분석 Excel이나 시행 상세에서 확인하세요.";
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
        { ReportStatus.Text = "보고서를 저장하지 못했습니다. 요약 메모 → TXT 열기, 분석 Excel 열기 또는 자료 폴더 → 보고서 폴더에서 다시 시도하세요. " + error.Message; return null; }
    }
    private void ChartSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultChart is null || ChartMetric is null || ChartHint is null) return;
        bool stability = ChartMetric.SelectedIndex == 1;
        ResultChart.Show(ChartCondition.SelectedItem as ReportChart, stability);
        ChartHint.Text = stability ? "유효 완료 / 종료 시행입니다. 사용자 중단·미수행은 제외합니다. 작은 표본의 100%가 안정성을 보장하지 않습니다." :
            "막대: 유효 시행 평균 · 선: 최소–최대(신뢰구간 아님). 실패는 0ms로 넣지 않습니다. 조건별로 축 범위가 다릅니다.";
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
        TrialDetails.Text = trial?.Details ?? "시행을 선택하면 결과와 제외 이유를 확인할 수 있습니다.";
        TrialFolderButton.IsEnabled = shownRun is not null && trial?.Result is not null &&
            Directory.Exists(Path.Combine(dataRoot, "speed", "local-runs", shownRun.Id.ToString("N"), trial.Trial.Id.ToString("N")));
    }
    private void SummarySelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || Summary.SelectedItem is not ReportSummary summary) return;
        updatingFilters = true; ExperimentFilter.SelectedItem = summary.Experiment; ReleaseFilter.SelectedItem = summary.Release;
        StatusFilter.SelectedIndex = 0; updatingFilters = false; ApplyTrialFilters();
    }
    private void TrialFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters || shownReport is null) return;
        updatingFilters = true; Summary.SelectedItem = null; updatingFilters = false; ApplyTrialFilters();
    }
    private void ClearFiltersClicked(object sender, RoutedEventArgs e)
    {
        updatingFilters = true; Summary.SelectedItem = null;
        ExperimentFilter.SelectedIndex = ReleaseFilter.SelectedIndex = StatusFilter.SelectedIndex = 0;
        updatingFilters = false; ApplyTrialFilters();
    }
    private void ApplyTrialFilters()
    {
        if (shownReport is null) return;
        string status = ((StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string) ?? "";
        string? condition = (Summary.SelectedItem as ReportSummary)?.Condition;
        var rows = shownReport.Trials.Where(t => (ExperimentFilter.SelectedIndex <= 0 || t.Experiment == ExperimentFilter.SelectedItem as string) &&
            (ReleaseFilter.SelectedIndex <= 0 || t.Release == ReleaseFilter.SelectedItem as string) && (condition is null || t.Condition == condition) &&
            (status == "" || status == "excluded" && !t.Included || status == t.StatusCode)).ToArray();
        Trials.ItemsSource = rows;
        Trials.SelectedItem = rows.FirstOrDefault(t => !t.Included) ?? rows.FirstOrDefault();
        TrialScope.Text = $"{rows.Length}/{shownReport.Trials.Length}개 시행 표시" + (condition is null ? "" : " · " + condition);
        ClearTrialFiltersButton.IsEnabled = ExperimentFilter.SelectedIndex > 0 || ReleaseFilter.SelectedIndex > 0 ||
            StatusFilter.SelectedIndex > 0 || condition is not null;
    }
    private void LegacyFolderClicked(object sender, RoutedEventArgs e)
    { Open(dataRoot); }
    private void Open(string path)
    { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) when (e is IOException or Win32Exception) { Log("열지 못했습니다: " + e.Message); } }
    private async void BrowseEditorClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Unity Editor|Unity.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Work(async ct => { await LocalSpeedCoordinator.InspectEditor(dialog.FileName, ct); settings = settings with { EditorPath = dialog.FileName }; EditorList.SelectedItem = null; await FindEditors(ct); await Save(ct, true); });
    }
    private void CancelClicked(object sender, RoutedEventArgs e) { operation?.Cancel(); State.Text = "중단 요청 · 시험 프로세스와 임시 파일을 정리합니다."; }
    private async void ThemeClicked(object sender, RoutedEventArgs e)
    {
        settings = settings with { Palette = (settings.Palette + 1) % 3 }; Palette.Apply((DeskPalette)settings.Palette);
        if (operation is null) await Work(ct => Save(ct, true));
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
