using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UnityBridgeDesk.Desktop.Controls;

public sealed partial class AshaCompanion
{
    private sealed class PageMemory(AshaExplorer explorer)
    {
        public AshaExplorer Explorer { get; } = explorer;
        public AshaPerch? Perch { get; set; }
        public AshaPerch? Home { get; set; }
    }
    private readonly Dictionary<string, PageMemory> pages = [];
    private string page = "work:0";
    private bool restorePage;
    public string Page => page;

    public void SetPage(string key)
    {
        if (disposed || page == key) return;
        if (map is not null && placed)
        {
            Point? supported = IsStanding ? Position : lastSupport;
            if (supported is { } p && map.Remember(p, mascot.FacesLeft) is { } bookmark) pages[page].Perch = bookmark;
            if (initialSupport is { } first && map.Remember(first, false) is { } initial) pages[page].Home = initial;
        }
        StopRoute(); pressed = null; dragging = false;
        ResetCharts();
        if (mascot.IsMouseCaptured) mascot.ReleaseMouseCapture();
        page = key;
        if (!pages.TryGetValue(key, out var memory))
        {
            // The app has four page contexts. Bound the cache for other embedded callers too.
            if (pages.Count >= 12) pages.Remove(pages.Keys.First());
            pages[key] = memory = new(new AshaExplorer(random.Next(), Character));
        }
        explorer = memory.Explorer;
        map = null; oldObstacles = []; oldLedges = [];
        lastSupport = initialSupport = null; placed = false; restorePage = true;
        host.BeginAnimation(UIElement.OpacityProperty, null); host.Opacity = 0; host.IsHitTestVisible = false;
        mascot.MotionEnabled = false; dirty = true;
        nextDecision = clock.Elapsed.TotalSeconds + .9;
        RefreshAfterLayout();
    }

    private string PerchId(FrameworkElement element)
    {
        // Stable names take priority. Unnamed headings use their structural position, not their changing text.
        if (element.Name.Length > 0) return ScopedName(element);
        var path = new List<string>();
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, surface); node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name.Length: > 0 } named) { path.Add(ScopedName(named)); break; }
            var parent = VisualTreeHelper.GetParent(node); int index = 0;
            if (parent is not null) while (index < VisualTreeHelper.GetChildrenCount(parent) && !ReferenceEquals(VisualTreeHelper.GetChild(parent, index), node)) index++;
            path.Add(node.GetType().Name + ":" + index);
        }
        path.Reverse(); return string.Join("/", path);
    }

    private string ScopedName(FrameworkElement element) => element.TemplatedParent is FrameworkElement owner && !ReferenceEquals(owner, element)
        ? PerchId(owner) + "/template:" + element.Name : element.Name;

    private void RevealPlacement()
    {
        host.BeginAnimation(UIElement.OpacityProperty, null); host.Opacity = 1; host.IsHitTestVisible = true;
        if (CanRun)
            host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.05, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }
}
