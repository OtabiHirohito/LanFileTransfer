using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace LanFileTransfer
{
    /// <summary>
    /// 動作モード「ブラウザに共有する」。
    /// 選んだファイルを、指定したアドレス・ポートで HTTP/HTTPS 配信し、
    /// 設定した時間が経過したら自動的に受け付けを停止する。
    /// (受信側はアプリ不要で、ブラウザで共有URLを開くだけ)
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>自動停止までの時間として入力できる範囲(秒)。</summary>
        private const int MinShareLifetimeSeconds = 10;
        private const int MaxShareLifetimeSeconds = 86400;

        private BrowserShareServer? _shareServer;
        private DispatcherTimer? _shareTimer;
        private int _shareRemainingSeconds;
        private bool _isSharing;
        private bool _isStoppingShare;

        // ===== ファイル選択 =====

        private void SelectShareFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "ブラウザで共有するファイルを選択",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                _shareFilePaths.Clear();
                _shareFilePaths.AddRange(dialog.FileNames);
                UpdateFileSelectionDisplay(ShareFileText, _shareFilePaths);
            }
        }

        // ===== 開始 / 停止 =====

        private async void ShareToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSharing)
            {
                await StopBrowserShareAsync("共有を停止しました。");
                return;
            }

            if (_shareFilePaths.Count == 0)
            {
                MessageBox.Show("共有するファイルを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetPort(SharePortTextBox.Text, out int port))
            {
                MessageBox.Show("ポート番号が不正です。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetShareLifetime(out int lifetimeSeconds))
            {
                return;
            }

            if (!ClientAddressFilter.TryParse(
                    ShareAllowedClientsTextBox.Text, out ClientAddressFilter clientFilter, out string? invalidEntry))
            {
                MessageBox.Show(
                    $"接続元IPの指定が不正です: {invalidEntry}\n\n" +
                    "IPv4 アドレス(例: 192.168.1.20)または CIDR(例: 192.168.1.0/24)を、カンマ区切りで入力してください。\n" +
                    "空欄にすると、すべてのアドレスからの接続を許可します。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool useToken = UseShareTokenCheckBox.IsChecked == true;
            bool useHttps = UseShareHttpsCheckBox.IsChecked == true;

            IPAddress listenAddress = GetSelectedShareAddress();

            if (!ConfirmShareWarnings(
                    listenAddress, lifetimeSeconds, useToken, useHttps, !clientFilter.IsAllowAll))
            {
                AppendLog("ブラウザ共有の開始をキャンセルしました。");
                return;
            }

            try
            {
                BrowserShareServer server = BrowserShareServer.Start(
                    listenAddress,
                    port,
                    _shareFilePaths,
                    TimeSpan.FromSeconds(lifetimeSeconds),
                    useToken,
                    useHttps,
                    clientFilter,
                    info => InvokeOnUi(() => UpdateBrowserShareProgress(info)),
                    fileName => InvokeOnUi(() => OnBrowserDownloadCompleted(fileName)),
                    message => InvokeOnUi(() => AppendLog(message)));

                _shareServer = server;
                _isSharing = true;
                _shareRemainingSeconds = lifetimeSeconds;

                SetBrowserSharingUi(true);

                IReadOnlyList<string> urls = server.BuildShareUrls();
                ShareUrlTextBox.Text = string.Join(Environment.NewLine, urls);
                ShareUrlHeadingText.Text =
                    $"受信側はブラウザで次のURLを開いてください（{FormatDuration(lifetimeSeconds)}で自動停止）";
                ShareUrlHintText.Text = BuildShareUrlHint(useToken, useHttps, !clientFilter.IsAllowAll)
                    + (clientFilter.IsAllowAll
                        ? string.Empty
                        : $"\n接続元IPの制限: {clientFilter.Describe()}");
                UpdateShareCountdownUi();

                SetStatus(TransferState.Listening, "共有中");
                TransferProgressBar.Value = 0;
                ProgressLabel.Text = $"ブラウザ共有中... あと {FormatDuration(lifetimeSeconds)}で自動停止します";

                string addressText = listenAddress.Equals(IPAddress.Any)
                    ? "すべてのアドレス"
                    : listenAddress.ToString();
                AppendLog(
                    $"ブラウザ共有を開始しました。({addressText} / ポート {port} / " +
                    $"{FormatDuration(lifetimeSeconds)}で自動停止 / " +
                    $"{(useToken ? "トークンあり" : "トークンなし")} / " +
                    $"{(useHttps ? "HTTPS" : "HTTP(平文)")} / " +
                    $"接続元: {(clientFilter.IsAllowAll ? "すべて許可" : clientFilter.Describe())})");
                foreach (string url in urls)
                {
                    AppendLog($"  共有URL: {url}");
                }

                _shareTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _shareTimer.Tick += BrowserShareTimer_Tick;
                _shareTimer.Start();
            }
            catch (SocketException ex)
            {
                AppendLog($"ブラウザ共有を開始できませんでした: {ex.Message}");
                MessageBox.Show(
                    $"ブラウザ共有を開始できませんでした。\n{ex.Message}\n\n" +
                    "選択したアドレス・ポート番号が他のアプリで使用されていないか確認してください。",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                AppendLog($"ブラウザ共有を開始できませんでした: {ex.Message}");
                MessageBox.Show(
                    $"ブラウザ共有を開始できませんでした。\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BrowserShareTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isSharing || _shareServer is null)
            {
                return;
            }

            _shareRemainingSeconds = (int)Math.Ceiling(Math.Max(0, _shareServer.Remaining.TotalSeconds));

            if (_shareRemainingSeconds > 0)
            {
                UpdateShareCountdownUi();
                ProgressLabel.Text = $"ブラウザ共有中... あと {FormatDuration(_shareRemainingSeconds)}で自動停止します";
                return;
            }

            _shareTimer?.Stop();
            ShareCountdownText.Text = "設定した時間が経過しました。共有を終了します...";
            await StopBrowserShareAsync("自動停止の時間が経過したためブラウザ共有を停止しました。");
        }

        /// <summary>共有の後片付け。何度呼んでも安全。</summary>
        private async Task StopBrowserShareAsync(string message)
        {
            if (_isStoppingShare || _shareServer is null)
            {
                return;
            }

            _isStoppingShare = true;
            BrowserShareServer server = _shareServer;

            _shareTimer?.Stop();
            _shareTimer = null;

            ShareToggleButton.IsEnabled = false;
            ShareToggleButton.Content = "停止中...";

            // 新しい接続は受け付けない。開始済みのダウンロードは完了まで待つ
            server.StopAccepting();
            AppendLog(message);

            if (_isSharing)
            {
                ShareCountdownText.Text = "共有を終了しました。ダウンロード中のファイルは完了まで続行します...";
                ProgressLabel.Text = "共有を終了しています（ダウンロード中のファイルは完了まで続行）...";
            }

            try
            {
                await server.WaitForShutdownAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"ブラウザ共有の終了処理でエラー: {ex.Message}");
            }
            finally
            {
                server.Dispose();
                _shareServer = null;
                _isSharing = false;
                _isStoppingShare = false;

                SetBrowserSharingUi(false);
                ShareUrlTextBox.Text = string.Empty;
                ShareCountdownText.Text = string.Empty;
                ResetProgress();
                AppendLog("ブラウザ共有を終了しました。");
            }
        }
    }
}
