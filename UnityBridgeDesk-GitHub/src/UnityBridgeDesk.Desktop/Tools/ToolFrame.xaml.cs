using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Desktop.Catalog;

namespace UnityBridgeDesk.Desktop.Tools;

public partial class ToolFrame : UserControl
{
    public ToolKind Tool { get; }
    private readonly ShellSession session;
    private bool ready;
    private bool executionAvailable;
    private UIElement? lastFocusedInput;
    public event Action? CatalogRequested;
    public event Action? ExecutionRequested;
    public void EnableExecution()
    {
        executionAvailable=true;DraftActions.Visibility=DraftSurface.Visibility;ReadyChip.Text="사용 가능";
        ExecuteButton.Content=Tool==ToolKind.Installation?"설치 구성 열기":Tool==ToolKind.AiWork?"AI 실행 구성":"벤치 준비 열기";
        Availability.Text=Tool==ToolKind.Installation?"등록한 CLI와 Connector를 검토하고 프로젝트에 적용할 수 있어요.":Tool==ToolKind.AiWork?"실행 구성에서 AI 연결을 지정한 뒤 이 지시를 바로 전달하세요.":"고정 명령과 AI 제작을 각각 켜고 새 복제본에서 측정하세요. 현재 명세는 예비 시험용이에요.";
    }
    private void ExecuteClick(object sender,RoutedEventArgs e)=>ExecutionRequested?.Invoke();
    public void ShowExecution(UIElement panel){ExecutionHost.Content=panel;DraftSurface.Visibility=DraftActions.Visibility=Visibility.Collapsed;ExecutionSurface.Visibility=Visibility.Visible;BackToDraftButton.Visibility=Tool==ToolKind.Benchmark?Visibility.Collapsed:Visibility.Visible;BackToDraftButton.Content=Tool==ToolKind.AiWork?"← 지시 수정":"← 설치 메모";UpdateActivity();}
    private void BackToDraftClick(object sender,RoutedEventArgs e){ExecutionSurface.Visibility=Visibility.Collapsed;DraftSurface.Visibility=Visibility.Visible;DraftActions.Visibility=executionAvailable?Visibility.Visible:Visibility.Collapsed;Dispatcher.BeginInvoke(()=>DraftInput.Focus());}
    public TargetSummary? Target { get; private set; }
    public static string NameFor(ToolKind tool) => tool switch { ToolKind.Installation => "설치·관리", ToolKind.AiWork => "AI 작업", _ => "벤치마크" };
    public static string SymbolFor(ToolKind tool) => tool switch { ToolKind.Installation => "\uE7B8", ToolKind.AiWork => "\uE8F2", _ => "\uE9D9" };

    public ToolFrame(ToolKind tool, ShellSession session)
    {
        Tool = tool; this.session = session;
        InitializeComponent();
        AutomationProperties.SetAutomationId(this, tool + "Window");
        AutomationProperties.SetName(this, NameFor(tool) + " 작업 영역");
        AutomationProperties.SetAutomationId(DraftInput, tool + "Draft");
        if (tool == ToolKind.Installation)
        {
            Eyebrow.Text = "BRIDGE / PACKAGE MANAGER"; Headline.Text = "프로젝트에 맞는 브릿지.";
            Subtitle.Text = "패키지와 실행 도구를 한곳에서 관리하세요.";
            SecondFieldLabel.Text = "참고 릴리스 · 적용 전"; SecondFieldValue.Text = "아직 선택하지 않았어요";
            InputLabel.Text = "설치 메모"; Placeholder.Text = "적용할 버전이나 준비할 내용을 적어 두세요.";
            DraftInput.Text = session.Drafts.InstallationNote;
            Availability.Text = "프로젝트와 로컬 파일을 보관함에 등록할 수 있어요. 설치·연결 기능은 준비 중이에요.";
        }
        else if (tool == ToolKind.AiWork)
        {
            Eyebrow.Text = "CREATE / AI WORKSPACE"; Headline.Text = "생각을 장면으로.";
            Subtitle.Text = "만들고 싶은 장면을 이곳에서 자연어로 적어 보세요.";
            InputLabel.Text = "AI에게 전달할 작업"; Placeholder.Text = "어떤 장면을 만들까요? 필요한 동작과 조건을 자유롭게 적어 주세요.";
            DraftInput.MinHeight = 145; DraftInput.Text = session.Drafts.AiPrompt;
            Availability.Text = "AI 연결 기능은 준비 중이에요. 입력한 내용은 아직 전송되거나 실행되지 않아요.";
        }
        else
        {
            Eyebrow.Text = "EXPERIMENT / BENCHMARK"; Headline.Text = "같은 조건, 다른 버전.";
            Subtitle.Text = "두 가지 측정을 원하는 조합으로. 결과는 나란히.";
            SecondFieldLabel.Text = "비교 버전"; SecondFieldValue.Text = "아직 선택하지 않았어요";
            ModesPanel.Visibility = Visibility.Visible;
            FixedMode.IsChecked = session.Drafts.FixedCommands; AiMode.IsChecked = session.Drafts.AiCreation;
            InputLabel.Text = "실험 메모"; Placeholder.Text = "비교하려는 작업과 관찰할 차이를 적어 두세요.";
            DraftInput.Text = session.Drafts.BenchmarkNote;
            Availability.Text = "벤치 실행 기능은 준비 중이에요. 측정 방식 선택과 메모만 초안으로 보관해요. 실측 데이터는 아직 없어요.";
        }
        AutomationProperties.SetName(DraftInput, InputLabel.Text);
        SecondFieldLabel.Text = "참고 릴리스 · 설치 상태와 별개";
        ready = true; UpdatePlaceholder();
    }
    public void UpdateTarget(TargetSummary target)
    {
        Target = target; ProjectValue.Text = target.Project; ProjectValue.ToolTip = target.ProjectPath;
        SecondFieldValue.Text = target.Release;
    }
    private void CatalogClick(object sender, RoutedEventArgs e) => CatalogRequested?.Invoke();
    public void UpdateActivity()
    {
        ReadyChip.Text = session.Activity(Tool) == "작업 진행 중" ? "작업 진행 중" : executionAvailable?"사용 가능":"준비 중";
    }
    public void FocusInput()
    {
        if (lastFocusedInput is { IsVisible: true, IsEnabled: true }) lastFocusedInput.Focus();
        else if (DraftSurface.IsVisible) DraftInput.Focus();
        else ExecutionSurface.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void RememberFocus(object sender, KeyboardFocusChangedEventArgs e) => lastFocusedInput = e.NewFocus as UIElement;
    private void DraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        var drafts = session.Drafts;
        session.SetDrafts(Tool switch
        {
            ToolKind.Installation => drafts with { InstallationNote = DraftInput.Text },
            ToolKind.AiWork => drafts with { AiPrompt = DraftInput.Text },
            _ => drafts with { BenchmarkNote = DraftInput.Text }
        });
        UpdatePlaceholder();
    }
    private void UpdatePlaceholder() => Placeholder.Visibility = string.IsNullOrEmpty(DraftInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    private void ModesChanged(object sender, RoutedEventArgs e)
    {
        if (ready) session.SetDrafts(session.Drafts with { FixedCommands = FixedMode.IsChecked == true, AiCreation = AiMode.IsChecked == true });
    }
}
