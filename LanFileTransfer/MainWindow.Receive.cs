using System.IO;
using System.Net;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace LanFileTransfer
{
    /// <summary>受信側の操作(保存先選択・受信待機・停止)。</summary>
    public partial class MainWindow
    {
        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            // WPF標準にはフォルダ選択ダイアログが無いため WinForms のものを利用する
            using var dialog = new FolderBrowserDialog
            {
                Description = "保存先フォルダを選択"
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _saveFolderPath = dialog.SelectedPath;
                SaveFolderText.Text = _saveFolderPath;
            }
        }

        private async void ReceiveToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isReceiving)
            {
                // 受信待機中は「停止」ボタンとして動作する
                ReceiveToggleButton.IsEnabled = false;
                ReceiveToggleButton.Content = "停止中...";
                _receiveCts?.Cancel();
                AppendLog("受信待機の停止を要求しました。");
                return;
            }

            if (string.IsNullOrEmpty(_saveFolderPath))
            {
                MessageBox.Show("保存先フォルダを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetPort(ReceivePortTextBox.Text, out int port))
            {
                MessageBox.Show("ポート番号が不正です。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetPassword(out string? password))
            {
                return;
            }

            IPAddress listenAddress = GetSelectedListenAddress();

            _receiveCts = new CancellationTokenSource();
            _isReceiving = true;
            SetReceivingUi(true);
            SetStatus(TransferState.Listening, "受信待機中");
            TransferProgressBar.Value = 0;
            ProgressLabel.Text = $"待機中... ポート {port} で接続を待っています";
            AppendLog($"{(listenAddress.Equals(IPAddress.Any) ? "すべてのアドレス" : listenAddress.ToString())} ポート {port} で受信待機を開始しました。保存先: {_saveFolderPath}{(password != null ? " / 暗号化あり" : "")}");

            var progress = new DispatcherProgress<TransferProgressInfo>(Dispatcher, UpdateProgressUi);

            try
            {
                await FileTransferService.StartReceivingAsync(
                    listenAddress,
                    port,
                    _saveFolderPath,
                    password,
                    progress,
                    savedPath => Dispatcher.Invoke(() =>
                    {
                        AppendLog($"受信完了: {savedPath}");
                        ProgressLabel.Text = $"受信完了: {Path.GetFileName(savedPath)}（続けて待機中...）";
                        SetStatus(TransferState.Listening, "受信待機中");
                    }),
                    _receiveCts.Token,
                    errorMessage => Dispatcher.Invoke(() =>
                    {
                        // 1件の失敗で待機は止めず、内容だけログに残す
                        // (受信途中で失敗した場合に「受信中」表示が残らないよう待機中へ戻す)
                        AppendLog($"受信エラー: {errorMessage}（待機を継続します）");
                        TransferProgressBar.Value = 0;
                        ProgressLabel.Text = $"待機中... ポート {port} で接続を待っています";
                        SetStatus(TransferState.Listening, "受信待機中");
                    }));
            }
            catch (OperationCanceledException)
            {
                // 停止ボタンによる正常なキャンセル
            }
            catch (Exception ex)
            {
                AppendLog($"受信エラー: {ex.Message}");
                MessageBox.Show($"受信中にエラーが発生しました。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isReceiving = false;
                _receiveCts?.Dispose();
                _receiveCts = null;
                SetReceivingUi(false);
                ResetProgress();
                AppendLog("受信待機を停止しました。");
            }
        }

        private void SetReceivingUi(bool receiving)
        {
            SetToggleButton(ReceiveToggleButton, receiving, "受信待機を開始", "受信待機を停止");

            SelectFolderButton.IsEnabled = !receiving;
            ReceivePortTextBox.IsEnabled = !receiving;
            SetModeSelectionEnabled(!receiving);
        }
    }
}
