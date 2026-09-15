using System.Windows;

namespace UnityBridgeDesk.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
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
            MainWindow = new SpeedBenchWindow(dataRoot);
            MainWindow.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show("작업실을 열지 못했습니다. 저장 위치와 실행 옵션을 확인해 주세요.", "UnityBridge Desk", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
