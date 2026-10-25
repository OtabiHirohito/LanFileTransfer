using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace LanFileTransfer
{
    /// <summary>
    /// メイン画面。共通処理と入出力の入口を持ち、
    /// 送信・受信・設定・状態表示は partial ファイルに分割している。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly List<string> _selectedFilePaths = new();
        private readonly List<string> _shareFilePaths = new();
        private readonly StatusBadgePresenter _statusBadge;

        private string? _saveFolderPath;
        private CancellationTokenSource? _sendCts;
        private CancellationTokenSource? _receiveCts;
        private bool _isSending;
        private bool _isReceiving;

        public MainWindow()
        {
            InitializeComponent();

            // 状態バッジの表示(色・点滅・文言)は StatusBadgePresenter に任せる
            _statusBadge = new StatusBadgePresenter(
                StatusBadge, StatusDot, StatusText, key => (Brush)FindResource(key));

            ApplySettings();
            LoadLocalInfo();
        }

        // ===== タイトルバー / ログ =====

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void RefreshInfoButton_Click(object sender, RoutedEventArgs e)
        {
            LoadLocalInfo();
            RefreshListenAddressOptions();

            // 共有中は使用中のアドレスを変えない
            if (!_isSharing)
            {
                RefreshShareAddressOptions();
            }

            AppendLog("自機情報を更新しました。");
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            // XAML初期化中にもイベントが飛ぶため null チェック
            if (SendPanel == null || ReceivePanel == null || SharePanel == null)
            {
                return;
            }

            if (_isSharing)
            {
                // 通常はモード切替を禁止しているが、念のため共有を止める
                _ = StopBrowserShareAsync("モードを切り替えたためブラウザ共有を停止しました。");
            }

            bool sendMode = SendModeRadio.IsChecked == true;
            bool receiveMode = ReceiveModeRadio.IsChecked == true;
            bool shareMode = BrowserModeRadio.IsChecked == true;

            SendPanel.Visibility = sendMode ? Visibility.Visible : Visibility.Collapsed;
            ReceivePanel.Visibility = receiveMode ? Visibility.Visible : Visibility.Collapsed;
            SharePanel.Visibility = shareMode ? Visibility.Visible : Visibility.Collapsed;

            // セキュリティ欄は、モードに応じて合言葉(アプリ同士)とブラウザ共有の設定を切り替える
            SecurityPanel.Visibility = Visibility.Visible;
            PasswordSecurityPanel.Visibility = shareMode ? Visibility.Collapsed : Visibility.Visible;
            BrowserShareSecurityPanel.Visibility = shareMode ? Visibility.Visible : Visibility.Collapsed;

            ResetProgress();
        }

        private void LoadLocalInfo()
        {
            HostNameText.Text = NetworkHelper.GetHostName();
            IpListText.Text = string.Join(Environment.NewLine, NetworkHelper.GetLocalIPv4List());
        }

        private void AppendLog(string message)
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        }

        /// <summary>
        /// 選択したファイルの表示を更新する(送信側・ブラウザ共有側で共通)。
        /// 1件ならファイル名、複数なら件数と合計サイズを表示する。
        /// </summary>
        private static void UpdateFileSelectionDisplay(TextBlock target, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
            {
                target.Text = "(未選択)";
                target.ToolTip = null;
            }
            else if (paths.Count == 1)
            {
                target.Text = Path.GetFileName(paths[0]);
                target.ToolTip = paths[0];
            }
            else
            {
                long totalBytes = paths.Sum(FileSize.GetLengthSafe);
                target.Text = $"{paths.Count}件のファイル ({ByteSize.Format(totalBytes)})";
                target.ToolTip = string.Join(Environment.NewLine, paths);
            }
        }

        // ===== 入力チェック =====

        private static bool TryGetPort(string text, out int port)
            => int.TryParse(text, out port) && port is > 0 and <= 65535;

        /// <summary>
        /// 合言葉の指定を検証して取得する。チェックOFFなら null(平文)。
        /// チェックONで未入力の場合は警告を出して false を返す。
        /// </summary>
        private bool TryGetPassword(out string? password)
        {
            password = null;

            if (UsePasswordCheckBox.IsChecked != true)
            {
                return true;
            }

            string value = PasswordInput.Password;
            if (string.IsNullOrEmpty(value))
            {
                MessageBox.Show("合言葉を入力してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            password = value;
            return true;
        }

        /// <summary>接続が相手側から切断された(リセット/中止)エラーかどうか。</summary>
        private static bool IsConnectionAborted(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is SocketException socketException &&
                    (socketException.SocketErrorCode == SocketError.ConnectionReset ||
                     socketException.SocketErrorCode == SocketError.ConnectionAborted ||
                     socketException.SocketErrorCode == SocketError.Shutdown))
                {
                    return true;
                }
            }

            return false;
        }

        // ===== ボタン状態 =====

        /// <summary>送信/受信の開始・停止ボタンの見た目と文言を切り替える。</summary>
        private void SetToggleButton(Button button, bool running, string startText, string stopText)
        {
            button.Content = running ? stopText : startText;
            button.Style = (Style)FindResource(running ? "DangerButtonStyle" : "PrimaryButtonStyle");
            button.IsEnabled = true;
        }

        /// <summary>動作中はモード切り替え(送信側/受信側/ブラウザ共有)を禁止する。</summary>
        private void SetModeSelectionEnabled(bool enabled)
        {
            SendModeRadio.IsEnabled = enabled;
            ReceiveModeRadio.IsEnabled = enabled;
            BrowserModeRadio.IsEnabled = enabled;
        }

        // ===== 終了処理 =====

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveSettings();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _shareTimer?.Stop();
            _shareServer?.Dispose();
            _shareServer = null;

            _receiveCts?.Cancel();
            _sendCts?.Cancel();
            base.OnClosed(e);
        }
    }
}
