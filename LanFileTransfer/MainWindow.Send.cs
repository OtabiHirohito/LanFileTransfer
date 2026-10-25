using System.IO;
using System.Windows;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace LanFileTransfer
{
    /// <summary>送信側の操作(ファイル選択・送信・停止)。</summary>
    public partial class MainWindow
    {
        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "送信するファイルを選択",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                _selectedFilePaths.Clear();
                _selectedFilePaths.AddRange(dialog.FileNames);
                UpdateFileSelectionDisplay(SelectedFileText, _selectedFilePaths);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending)
            {
                // 送信中は「停止」ボタンとして動作する
                SendButton.IsEnabled = false;
                SendButton.Content = "停止中...";
                SetStatus(TransferState.Sending, "停止中...");
                _sendCts?.Cancel();
                AppendLog("送信の停止を要求しました。");
                return;
            }

            if (_selectedFilePaths.Count == 0)
            {
                MessageBox.Show("送信するファイルを選択してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetPort(SendPortTextBox.Text, out int port))
            {
                MessageBox.Show("ポート番号が不正です。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetIp = TargetIpTextBox.Text.Trim();
            if (string.IsNullOrEmpty(targetIp))
            {
                MessageBox.Show("相手のIPアドレスを入力してください。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryGetPassword(out string? password))
            {
                return;
            }

            await StartSendAsync(targetIp, port, password);
        }

        private async Task StartSendAsync(string targetIp, int port, string? password)
        {
            var files = _selectedFilePaths.ToList();
            long[] fileSizes = files.Select(FileSize.GetLengthSafe).ToArray();
            long totalBytes = fileSizes.Sum();
            long completedBytes = 0;
            int currentIndex = 0;

            _sendCts = new CancellationTokenSource();
            CancellationToken token = _sendCts.Token;

            _isSending = true;
            SetSendingUi(true);
            SetStatus(TransferState.Sending, "送信中");
            TransferProgressBar.Value = 0;

            var progress = new DispatcherProgress<TransferProgressInfo>(Dispatcher, info =>
            {
                long done = completedBytes + info.TransferredBytes;
                double percentage = totalBytes == 0 ? 0 : (double)done / totalBytes * 100.0;
                TransferProgressBar.Value = percentage;
                ProgressLabel.Text = $"{info.FileName}  ({currentIndex}/{files.Count})  " +
                                     $"{ByteSize.Format(done)} / {ByteSize.Format(totalBytes)}  ({percentage:F1}%)";
            });

            int sentCount = 0;

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    currentIndex = i + 1;
                    string filePath = files[i];
                    string fileName = Path.GetFileName(filePath);

                    AppendLog($"送信開始 ({currentIndex}/{files.Count}): {fileName} -> {targetIp}:{port}");

                    // 受信側がまだ「受信待機を開始」していない場合に備え、接続待ちの状況を表示する
                    bool waitLogged = false;
                    var waitStatus = new DispatcherProgress<string>(Dispatcher, message =>
                    {
                        ProgressLabel.Text = message;

                        if (message.Contains("待っています"))
                        {
                            SetStatus(TransferState.Sending, "接続待ち");
                            if (!waitLogged)
                            {
                                waitLogged = true;
                                AppendLog(
                                    "受信側がまだ受信待機を開始していません。" +
                                    $"{FileTransferService.ReceiverWaitTimeout.TotalSeconds:F0}秒間、接続を再試行します。");
                            }
                        }
                        else
                        {
                            SetStatus(TransferState.Sending, "送信中");
                        }
                    });

                    await FileTransferService.SendFileAsync(
                        targetIp, port, filePath, password, progress, token, waitStatus);
                    completedBytes += fileSizes[i];
                    sentCount++;
                    AppendLog($"送信完了 ({currentIndex}/{files.Count}): {fileName}");
                }

                TransferProgressBar.Value = 100;
                ProgressLabel.Text = files.Count > 1 ? "全ファイルの送信が完了" : "送信完了";
                AppendLog(files.Count > 1
                    ? $"{files.Count}件のファイル送信が完了しました。"
                    : "送信が完了しました。");
            }
            catch (OperationCanceledException)
            {
                ProgressLabel.Text = $"送信を停止しました（{sentCount}/{files.Count} 件送信済み）";
                AppendLog($"送信を停止しました。（{sentCount}/{files.Count} 件送信済み）");
            }
            catch (Exception ex)
            {
                string message = IsConnectionAborted(ex)
                    ? ex.Message + "\n\n受信側が接続を拒否または切断しました。次の点を確認してください。\n" +
                      "・送信側と受信側で「合言葉で暗号化する」のON/OFFが一致しているか\n" +
                      "・合言葉が完全に同じか\n" +
                      "・受信側と送信側が同じバージョンのアプリか"
                    : ex.Message;

                AppendLog($"送信エラー: {message}");
                ProgressLabel.Text = "送信エラー";
                MessageBox.Show($"送信中にエラーが発生しました。\n{message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isSending = false;
                _sendCts?.Dispose();
                _sendCts = null;
                SetSendingUi(false);
                SetStatus(TransferState.Idle, "待機中");
            }
        }

        private void SetSendingUi(bool sending)
        {
            SetToggleButton(SendButton, sending, "送信開始", "送信停止");

            SelectFileButton.IsEnabled = !sending;
            TargetIpTextBox.IsEnabled = !sending;
            SendPortTextBox.IsEnabled = !sending;
            SetModeSelectionEnabled(!sending);
        }
    }
}
