using System.Windows;
using UnityBridgeDesk.Desktop.Shell;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityBridgeDesk");
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-dir" || !Path.IsPathFullyQualified(e.Args[1]))
                    throw new ArgumentException("Use --data-dir with an absolute directory.");
                dataRoot = Path.GetFullPath(e.Args[1]);
            }
            var persistence = new ShellPersistence(dataRoot);
            var loaded = await persistence.LoadAsync();
            var catalog = new CatalogService(dataRoot);
            await catalog.LoadAsync();
            string worker=Path.Combine(AppContext.BaseDirectory,"worker","UnityBridgeDesk.Worker.exe");
            if(!File.Exists(worker))worker=Path.Combine(AppContext.BaseDirectory,"UnityBridgeDesk.Worker.exe");
            var runtime=new DeskRuntime(dataRoot,new WorkerRunner(worker));
            MainWindow = new MainWindow(loaded.CreateSession(), persistence, loaded, catalog,runtime);
            MainWindow.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show("작업실을 열지 못했습니다. 저장 위치와 실행 옵션을 확인해 주세요.", "UnityBridge Desk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
