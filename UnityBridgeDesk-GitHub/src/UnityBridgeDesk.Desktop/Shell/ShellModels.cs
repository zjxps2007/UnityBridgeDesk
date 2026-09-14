using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Desktop.Shell;

public enum DeskPalette { Lilac, Rose, Mint }
public enum WindowPlacement { Free, Tiled }
public enum ShellChange { Layout, Appearance, Draft }

public sealed record DeskBounds(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            Width <= 0 || Height <= 0 || Width > 20000 || Height > 20000 || Math.Abs(X) > 100000 || Math.Abs(Y) > 100000)
            throw new ArgumentException("Invalid window bounds.");
    }
    public DeskBounds Fit(double width, double height)
    {
        var w = Math.Min(Width, Math.Max(1, width));
        var h = Math.Min(Height, Math.Max(1, height));
        return new(Math.Clamp(X, 0, Math.Max(0, width - w)), Math.Clamp(Y, 0, Math.Max(0, height - h)), w, h);
    }
}

public sealed record ToolLayout(ToolKind Tool, int Workspace, DeskBounds Bounds,
    bool IsOpen, bool IsMinimized, bool IsMaximized, int ZOrder);

public sealed record ShellPreferences(DeskPalette Palette, int Workspace, ImmutableArray<WindowPlacement> Placements,
    ImmutableArray<ToolLayout> Windows, bool ShowClock, bool ShowNote, double AppWidth, double AppHeight)
{
    // Older settings retain their geometry for compatibility; the tab shell only uses this selection.
    public ToolKind? ActiveTool { get; init; }
    // Retained only to read earlier settings; the desktop memo has been removed from the UI.
    public bool NoteExpanded { get; init; }
    // Read older documents without applying their detached panel positions to the fixed workspace.
    public ImmutableDictionary<ToolKind,DeskBounds> PanelBounds { get; init; }=ImmutableDictionary<ToolKind,DeskBounds>.Empty;
    public static ShellPreferences Default => new(DeskPalette.Lilac, 0,
        [WindowPlacement.Free, WindowPlacement.Free, WindowPlacement.Free],
        [new(ToolKind.Installation, 0, new(36, 26, 660, 490), false, false, false, 1),
         new(ToolKind.AiWork, 0, new(82, 54, 680, 500), false, false, false, 2),
         new(ToolKind.Benchmark, 0, new(28, 16, 690, 520), true, false, false, 3)],
        true, true, 1280, 860);

    public void Validate()
    {
        if(PanelBounds is null||PanelBounds.Count>3)throw new ArgumentException("Invalid panel positions.");
        foreach(var (kind,bounds) in PanelBounds)
        {
            if(kind is not (ToolKind.Installation or ToolKind.AiWork or ToolKind.Benchmark)||bounds is null)throw new ArgumentException("Invalid panel position.");
            bounds.Validate();
        }
        if (ActiveTool is { } tool && tool is not (ToolKind.Installation or ToolKind.AiWork or ToolKind.Benchmark))
            throw new ArgumentException("Invalid active tool.");
        if (!Enum.IsDefined(Palette) || Workspace is < 0 or > 2 || Placements.IsDefault || Placements.Length != 3 ||
            Placements.Any(x => !Enum.IsDefined(x)) || Windows.IsDefault || Windows.Length != 3 ||
            !double.IsFinite(AppWidth) || !double.IsFinite(AppHeight) || AppWidth is < 640 or > 20000 || AppHeight is < 480 or > 20000)
            throw new ArgumentException("Invalid shell settings.");
        if (!Windows.Select(x => x.Tool).Order().SequenceEqual(new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark }))
            throw new ArgumentException("Exactly three independent tool windows are required.");
        foreach (var window in Windows)
        {
            window.Bounds.Validate();
            if (window.Workspace is < 0 or > 2 || window.ZOrder < 0) throw new ArgumentException("Invalid tool layout.");
        }
    }
}

// Deliberately separate from run plans, results, credentials and theme/layout settings.
public sealed record DeskDrafts(string InstallationNote, string AiPrompt, string BenchmarkNote,
    bool FixedCommands, bool AiCreation, string DesktopNote)
{
    public static DeskDrafts Empty => new("", "", "", true, false, "");
    public void Validate()
    {
        if (InstallationNote is null || AiPrompt is null || BenchmarkNote is null || DesktopNote is null ||
            InstallationNote.Length > 32000 || AiPrompt.Length > 32000 || BenchmarkNote.Length > 32000 || DesktopNote.Length > 1000)
            throw new ArgumentException("Invalid draft data.");
    }
}
