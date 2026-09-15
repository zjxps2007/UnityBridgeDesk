using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private string[]? undoInputs;
    private bool[]? undoExperiments;
    private bool loaded;
    private bool closeAfterCleanup;
    private record HistoryItem(string Label, string Path);
    public SpeedBenchWindow(string root, LocalDiscovery? discovery = null)
    {
        dataRoot = root; repository = new(root); editorDiscovery = discovery ?? new(); InitializeComponent();
        Title += " · v" + typeof(SpeedBenchWindow).Assembly.GetName().Version!.ToString(3);
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/UnityBridgeDesk;component/Assets/desk.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        MascotImage.Source = icon.Frames.MaxBy(f => f.PixelWidth);
        clockTimer.Tick += (_, _) => UpdateClock(); UpdateClock(); clockTimer.Start();
        Closed += (_, _) => clockTimer.Stop();
    }
    private string SettingsPath => Path.Combine(dataRoot, "speed", "local-settings.json");
    private TextBox[] OptionFields => [Repeats, Warmups, Calls, Timeout, PrepareTimeout, Seed];
    private CheckBox[] ExperimentFields => [F01, F02, F03];
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
        operation = new(); PreparationForm.IsEnabled = false; CancelButton.Visibility = Visibility.Visible; Activity.IsIndeterminate = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Log("중단했습니다. 시험 정리 상태는 결과에서 확인할 수 있습니다."); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.Net.Http.HttpRequestException)
        { Log(e.Message); NextStep.Text = "아래 상태를 확인하고 준비 탭에서 해당 항목을 완료해 주세요."; }
        finally
        {
            operation.Dispose(); operation = null; PreparationForm.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; Activity.IsIndeterminate = false;
            if (closeAfterCleanup) Close();
        }
    }
    private SpeedOptions ReadOptions()
    {
        int Read(TextBox box) => int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : throw new ArgumentException("횟수와 제한 시간에는 정수를 입력하세요.");
        var value = new SpeedOptions(Read(Repeats), Read(Warmups), Read(Calls), Read(Timeout), Read(PrepareTimeout), Read(Seed),
            ExperimentFields.Where(c => c.IsChecked == true).Select(c => c.Name).ToArray()); value.Validate(); return value;
    }
    private void ApplyOptions(SpeedOptions options)
    {
        int[] values = [options.Repeats, options.Warmups, options.Calls, options.TimeoutSeconds, options.PrepareSeconds, options.Seed];
        for (int i = 0; i < values.Length; i++) OptionFields[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
        foreach (var c in ExperimentFields) c.IsChecked = options.Selected.Contains(c.Name);
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
        catch (ArgumentException) when (tolerateInvalidOptions) { options = settings.Options ?? new(); }
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
            try { var run = await SpeedFiles.Read<SpeedRun>(file, ct); items.Add(new(run.StartedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + string.Join(" / ", run.Releases.Select(r => r.Tag)) + " · " + run.Status, file)); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        History.ItemsSource = items.OrderByDescending(i => File.GetLastWriteTimeUtc(i.Path)).ToArray();
    }
    private async void HistoryClicked(object sender, RoutedEventArgs e) => await Work(RefreshHistory);
    private async void HistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (History.SelectedItem is not HistoryItem item) return;
        try { ShowRun(await SpeedFiles.Read<SpeedRun>(item.Path)); } catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { Log(error.Message); }
    }
    private void ShowRun(SpeedRun run)
    {
        shownRun = run; Summary.ItemsSource = SpeedAnalysis.Summaries(run);
        int success = run.Results.Count(r => r.Status == "success"), failed = run.Results.Count(r => r.Status != "success");
        ResultStatus.Text = $"계획 {run.Plan.Length} · 수행 {run.Results.Length} · 성공 {success} · 실패/중단 {failed} · 미수행 {run.Plan.Length - run.Results.Length}\n{run.Schema} · {run.Evidence} · {run.Status}";
        Comparison.Text = string.Join("\n", SpeedAnalysis.Pairs(run).Select(p => $"{p.Experiment}/{p.Condition} · {p.Baseline} → {p.Candidate}: " +
            (p.CompleteBlocks == 0 ? "비교 가능한 성공 쌍 없음" : $"시간 감소율 {p.ReductionPercent:F2}% · 두 버전 성공 {p.CompleteBlocks}/{p.PlannedBlocks}블록"))) + "\n예비 기술 통계입니다. 신뢰구간·전체 승자는 아직 판정하지 않습니다.";
        TrialDetails.Text = string.Join("\n", run.Releases.Select(CliDistribution.CompatibilityNote).Where(n => n is not null)) + "\n" +
            string.Join("\n\n", run.Results.Select(r => $"#{r.Trial.Order} · {r.Trial.Tag} · {r.Trial.Experiment}/{r.Trial.Variant} · {r.Status}\n" +
                $"Connector 보고 값: {r.Guest?.ReportedConnectorVersion ?? "미확인"} · 임시 폴더 정리: {r.ResetVerified} · {r.Error ?? "응답 검증 통과"}"));
        NextStep.Text = "성공·실패 수를 먼저 보고 같은 조건의 시간을 비교하세요. JSON·CSV에서 원시 기록을 확인할 수 있습니다.";
    }
    private void ResultsFolderClicked(object sender, RoutedEventArgs e)
    { if (shownRun is not null) Open(Path.Combine(dataRoot, "speed", "local-runs", shownRun.Id.ToString("N"))); }
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
