using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Desktop.Shell;

// Only view state lives here. Observed executions are owned elsewhere and never stopped by window operations.
public sealed class ShellSession
{
    public ShellPreferences Preferences { get; private set; }
    public DeskDrafts Drafts { get; private set; }
    public event Action<ShellChange>? Changed;
    public bool BackgroundOnly { get; private set; }
    public double ViewportWidth { get; private set; } = 1200;
    public double ViewportHeight { get; private set; } = 590;
    private readonly Dictionary<RunId, ExecutionState> observedExecutions = [];
    public IEnumerable<ExecutionState> ActiveExecutions => observedExecutions.Values.Where(x => x.Snapshot.Outcome is null);
    public ToolLayout? Focused => Preferences.Windows.Where(IsVisible).MaxBy(x => x.ZOrder);
    public ToolKind ActiveTool => Preferences.ActiveTool
        ?? Focused?.Tool
        ?? Preferences.Windows.Where(x => x.IsOpen).MaxBy(x => x.ZOrder)?.Tool
        ?? ToolKind.Benchmark;

    public ShellSession(ShellPreferences? preferences = null, DeskDrafts? drafts = null)
    {
        Preferences = preferences ?? ShellPreferences.Default;
        Drafts = drafts ?? DeskDrafts.Empty;
        Preferences.Validate(); Drafts.Validate();
    }
    public ToolLayout Window(ToolKind tool) => Preferences.Windows.Single(x => x.Tool == tool);
    public void SelectTool(ToolKind tool)
    {
        if (tool is not (ToolKind.Installation or ToolKind.AiWork or ToolKind.Benchmark)) throw new ArgumentException("Unknown tool.");
        if (Preferences.ActiveTool == tool) return;
        Preferences = Preferences with { ActiveTool = tool };
        Notify();
    }
    public void CycleTool(bool backwards = false)
    {
        ToolKind[] tools = [ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark];
        int index = Array.IndexOf(tools, ActiveTool);
        SelectTool(tools[(index + (backwards ? tools.Length - 1 : 1)) % tools.Length]);
    }
    public bool IsVisible(ToolLayout window) => !BackgroundOnly && window.Workspace == Preferences.Workspace && window.IsOpen && !window.IsMinimized;
    public void Observe(ExecutionState state) => observedExecutions[state.Snapshot.Route.RunId] = state;
    public string Activity(ToolKind tool) => ActiveExecutions.Any(x => x.Snapshot.Route.Tool == tool) ? "작업 진행 중" : "실행 대기";

    public void Open(ToolKind tool)
    {
        var window = Window(tool);
        // Existing windows stay in their workspace; a closed window opens in the current workspace.
        if (window.IsOpen) Preferences = Preferences with { Workspace = window.Workspace };
        else window = window with { Workspace = Preferences.Workspace };
        BackgroundOnly = false;
        Replace(window with { IsOpen = true, IsMinimized = false });
        Focus(tool);
    }
    public void Focus(ToolKind tool)
    {
        var window = Window(tool);
        if (!IsVisible(window)) return;
        // Normalize on every focus so persisted z-order cannot overflow after long sessions.
        var ordered = Preferences.Windows.OrderBy(x => x.ZOrder).Where(x => x.Tool != tool).Append(window).ToArray();
        Preferences = Preferences with { Windows = [.. ordered.Select((x, i) => x with { ZOrder = i + 1 })] };
        Notify();
    }
    public void Close(ToolKind tool) { Replace(Window(tool) with { IsOpen = false, IsMinimized = false }); Notify(); }
    public void Minimize(ToolKind tool) { Replace(Window(tool) with { IsMinimized = true }); Notify(); }
    public void ToggleMaximize(ToolKind tool) { Replace(Window(tool) with { IsMaximized = !Window(tool).IsMaximized }); Focus(tool); }
    public void SwitchWorkspace(int workspace)
    {
        if (workspace is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(workspace));
        Preferences = Preferences with { Workspace = workspace }; BackgroundOnly = false; Notify();
    }
    public void MoveToWorkspace(ToolKind tool, int workspace)
    {
        if (workspace is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(workspace));
        Replace(Window(tool) with { Workspace = workspace }); Notify();
    }
    public void ToggleBackground() { BackgroundOnly = !BackgroundOnly; Notify(); }
    public void SetPlacement(WindowPlacement placement)
    {
        if (!Enum.IsDefined(placement)) throw new ArgumentException("Unknown placement.");
        Preferences = Preferences with { Placements = Preferences.Placements.SetItem(Preferences.Workspace, placement) };
        if (placement == WindowPlacement.Tiled) Preferences = Preferences with
        { Windows = [.. Preferences.Windows.Select(x => x.Workspace == Preferences.Workspace ? x with { IsMaximized = false } : x)] };
        Notify();
    }
    public void SetPalette(DeskPalette palette)
    {
        if (!Enum.IsDefined(palette)) throw new ArgumentException("Unknown palette.");
        Preferences = Preferences with { Palette = palette }; Changed?.Invoke(ShellChange.Appearance);
    }
    public void SetClockVisible(bool visible)
    {
        if (Preferences.ShowClock == visible) return;
        Preferences = Preferences with { ShowClock = visible }; Changed?.Invoke(ShellChange.Appearance);
    }
    public void SetDrafts(DeskDrafts drafts) { drafts.Validate(); Drafts = drafts; Changed?.Invoke(ShellChange.Draft); }
    public void SetAppSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        Preferences = Preferences with { AppWidth = Math.Clamp(width, 640, 20000), AppHeight = Math.Clamp(height, 480, 20000) };
    }
    public void SetViewport(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)) throw new ArgumentException("Invalid viewport.");
        ViewportWidth = Math.Max(1, width); ViewportHeight = Math.Max(1, height); Notify();
    }
    public DeskBounds BoundsFor(ToolKind tool)
    {
        var window = Window(tool);
        if (window.IsMaximized) return new(0, 0, ViewportWidth, ViewportHeight);
        var visible = Preferences.Windows.Where(IsVisible).OrderBy(x => x.Tool).ToArray();
        int index = Array.FindIndex(visible, x => x.Tool == tool);
        if (Preferences.Placements[Preferences.Workspace] == WindowPlacement.Tiled && index >= 0)
        {
            const double gap = 12;
            if (visible.Length == 1) return new(0, 0, ViewportWidth, ViewportHeight);
            var half = Math.Max(1, (ViewportWidth - gap) / 2);
            if (visible.Length == 2) return new(index * (half + gap), 0, half, ViewportHeight);
            if (index == 0) return new(0, 0, half, ViewportHeight);
            var halfHeight = Math.Max(1, (ViewportHeight - gap) / 2);
            return new(half + gap, (index - 1) * (halfHeight + gap), half, halfHeight);
        }
        return window.Bounds.Fit(ViewportWidth, ViewportHeight);
    }
    public void Move(ToolKind tool, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        var bounds = BoundsFor(tool);
        var window = Window(tool);
        if (window.IsMaximized) return;
        Preferences = Preferences with { Placements = Preferences.Placements.SetItem(Preferences.Workspace, WindowPlacement.Free) };
        Replace(window with { Bounds = (bounds with { X = bounds.X + dx, Y = bounds.Y + dy }).Fit(ViewportWidth, ViewportHeight) });
        Focus(tool);
    }
    public void Resize(ToolKind tool, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        if (Window(tool).IsMaximized || Preferences.Placements[Preferences.Workspace] == WindowPlacement.Tiled) return;
        var bounds = BoundsFor(tool);
        Replace(Window(tool) with { Bounds = (bounds with { Width = Math.Max(300, bounds.Width + dx), Height = Math.Max(200, bounds.Height + dy) }).Fit(ViewportWidth, ViewportHeight) });
        Focus(tool);
    }
    public void CycleFocus()
    {
        var windows = Preferences.Windows.Where(IsVisible).OrderBy(x => x.ZOrder).ToArray();
        if (windows.Length > 0) Focus(windows[0].Tool);
    }
    private void Replace(ToolLayout window) => Preferences = Preferences with
    { Windows = Preferences.Windows.SetItem(Preferences.Windows.IndexOf(Window(window.Tool)), window) };
    private void Notify() => Changed?.Invoke(ShellChange.Layout);
}
