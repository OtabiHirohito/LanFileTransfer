using System.Windows;

namespace LanFileTransfer
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // .exe にファイルをドロップして起動すると、そのパスがコマンドライン引数で渡される
            var window = new MainWindow(e.Args);
            MainWindow = window;
            window.Show();
        }
    }
}
