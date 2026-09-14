using System.Windows;
using System.Windows.Controls;
using UnityBridgeDesk.Infrastructure.Catalog;

namespace UnityBridgeDesk.Desktop.Catalog;

public partial class CatalogWindow
{
    private readonly CancellationTokenSource discoveryCancellation = new();
    private bool discovering;
    private sealed record DiscoveryChoice(LocalCandidate Item, LocalCandidate? Connector);
    private async Task DiscoverAsync(string? folder = null)
    {
        if (discovering || busy || discoveryCancellation.IsCancellationRequested) return;
        discovering = true; DiscoverRefresh.IsEnabled = DiscoverFolder.IsEnabled = UseDiscovered.IsEnabled = false;
        DiscoveryStatus.Text = "기본 위치와 이전에 지정한 폴더를 찾고 있어요…";
        try
        {
            var setup = new LocalSetupStore(catalog.DataRoot);
            var remembered = await setup.LoadAsync();
            var folders = folder is null ? remembered.Folders.ToArray() : new[] { folder }.Concat(remembered.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var result = await new LocalDiscovery().ScanAsync(catalog.Document, folders, remembered.Paths.Values, discoveryCancellation.Token);
            if (discoveryCancellation.IsCancellationRequested) return;
            var choices = new List<CatalogRow>();
            foreach (var project in result.Candidates.Where(x => x.Kind == DiscoveryKind.Project))
                choices.Add(new("프로젝트 · " + project.Label, (project.Version ?? "버전 미확인") + " · " + project.Source, new DiscoveryChoice(project, null)));
            foreach (var cli in result.Candidates.Where(x => x.Kind == DiscoveryKind.BridgeCli))
            {
                var connectors = result.Candidates.Where(x => x.Kind == DiscoveryKind.Connector &&
                    string.Equals(Path.GetDirectoryName(x.Path), Path.GetDirectoryName(cli.Path), StringComparison.OrdinalIgnoreCase)).ToArray();
                // Never guess across releases or silently pick one of several adjacent Connectors.
                if (connectors.Length == 1)
                    choices.Add(new("Bridge 조합 · " + connectors[0].Version, cli.Label + " + " + connectors[0].Label + " · " + cli.Source, new DiscoveryChoice(cli, connectors[0])));
                else choices.Add(new(cli.Label, "CLI만 등록 · " + cli.Source, new DiscoveryChoice(cli, null)));
            }
            DiscoveredItems.ItemsSource = choices;
            if (choices.Count > 0) DiscoveredItems.SelectedIndex = 0;
            else DiscoveryDetail.Text = "‘다른 위치의 폴더 추가’에서 프로젝트나 Bridge가 들어 있는 폴더를 한 번 골라 주세요.";
            DiscoveryStatus.Text = choices.Count == 0 ? "기본 위치에서 사용할 항목을 찾지 못했어요." : $"{choices.Count}개를 찾았어요. 사용할 항목을 선택하세요.";
            if (result.UnreadableLocations > 0) DiscoveryStatus.Text += " 일부 위치는 읽을 수 없어 건너뛰었습니다.";
            if (result.Limited) DiscoveryStatus.Text += " 폴더가 많으면 대상 폴더를 더 좁혀 추가하세요.";
            if (folder is not null && !await setup.RememberAsync(folder: folder)) DiscoveryStatus.Text += " 추가한 위치를 저장하지 못했어요.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!discoveryCancellation.IsCancellationRequested) DiscoveryStatus.Text = "위치를 찾지 못했습니다. 다른 폴더를 지정해 주세요."; }
        finally
        {
            discovering = false;
            if (!discoveryCancellation.IsCancellationRequested) { DiscoverRefresh.IsEnabled = DiscoverFolder.IsEnabled = true; DiscoverySelected(this, null!); }
        }
    }
    private async void DiscoverAgain(object sender, RoutedEventArgs e) => await DiscoverAsync();
    private async void AddDiscoveryFolder(object sender, RoutedEventArgs e)
    { if (PickFolder("프로젝트 또는 압축을 푼 Bridge가 있는 폴더") is { } folder) await DiscoverAsync(folder); }
    private void DiscoverySelected(object sender, SelectionChangedEventArgs e)
    {
        if (UseDiscovered is null) return;
        var choice = (DiscoveredItems.SelectedItem as CatalogRow)?.Value as DiscoveryChoice;
        UseDiscovered.IsEnabled = choice is not null && !discovering && !busy && catalog.CanWrite;
        if (choice is null) return;
        UseDiscovered.Content = choice.Item.Kind == DiscoveryKind.Project ? "이 프로젝트 사용" : "이 조합 등록·선택";
        DiscoveryDetail.Text = choice.Item.Kind == DiscoveryKind.Project ? choice.Item.Path + "\n작업 준비에서 이 프로젝트의 Editor 버전을 찾습니다." :
            "CLI: " + choice.Item.Path + "\nConnector: " + (choice.Connector?.Path ?? "없음 · 필요한 경우 로컬 파일 탭에서 별도 지정") +
            "\n" + (choice.Connector is null ? "CLI 전용 조합" : "같은 폴더에서 찾은 CLI + Connector 조합 · 폴더 배치만으로 버전 일치를 보증하지 않습니다.") + "\n등록 시 실제 파일과 해시를 확인합니다.";
    }
    private async void UseDiscovery(object sender, RoutedEventArgs e)
    {
        if ((DiscoveredItems.SelectedItem as CatalogRow)?.Value is not DiscoveryChoice choice) return;
        await Perform(() => choice.Item.Kind == DiscoveryKind.Project ? catalog.UseDiscoveredProjectAsync(choice.Item.Path) :
            catalog.UseDiscoveredReleaseAsync(choice.Item.Path, choice.Connector?.Path,
                choice.Connector is null ? choice.Item.Label : "Bridge " + choice.Connector.Version));
        DiscoverySelected(this, null!);
    }
}
