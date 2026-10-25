using System.Windows;

namespace LanFileTransfer
{
    /// <summary>ブラウザ共有の進捗表示と、共有中/停止中の画面状態。</summary>
    public partial class MainWindow
    {
        private void UpdateBrowserShareProgress(BrowserShareProgress info)
        {
            if (!_isSharing)
            {
                return;
            }

            TransferProgressBar.Value = info.Percentage;
            ProgressLabel.Text = $"{info.FileName} → {info.ClientAddress}  " +
                                 $"{ByteSize.Format(info.SentBytes)} / {ByteSize.Format(info.TotalBytes)}  " +
                                 $"({info.Percentage:F1}%)";
            SetStatus(TransferState.Sending, "送信中(ブラウザ)");
        }

        private void OnBrowserDownloadCompleted(string fileName)
        {
            if (!_isSharing)
            {
                return;
            }

            AppendLog($"ブラウザへの送信完了: {fileName}");
            TransferProgressBar.Value = 0;
            ProgressLabel.Text = $"配信しました: {fileName}（続けて共有中...）";
            SetStatus(TransferState.Listening, "共有中");
        }

        private void UpdateShareCountdownUi()
        {
            ShareCountdownText.Text = $"残り {FormatDuration(_shareRemainingSeconds)}で自動停止します。";
        }

        /// <summary>サーバーのスレッドから UI を更新する。終了処理中は何もしない。</summary>
        private void InvokeOnUi(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.Invoke(action);
            }
            catch (TaskCanceledException)
            {
                // ウィンドウ終了と競合した
            }
            catch (InvalidOperationException)
            {
                // 同上
            }
        }

        private void SetBrowserSharingUi(bool sharing)
        {
            SetToggleButton(ShareToggleButton, sharing, "共有を開始", "共有を停止");

            SelectShareFileButton.IsEnabled = !sharing;
            SharePortTextBox.IsEnabled = !sharing;
            ShareAddressComboBox.IsEnabled = !sharing;
            ShareAllowedClientsTextBox.IsEnabled = !sharing;
            ShareLifetimeTextBox.IsEnabled = !sharing;
            UseShareTokenCheckBox.IsEnabled = !sharing;
            UseShareHttpsCheckBox.IsEnabled = !sharing;
            SetModeSelectionEnabled(!sharing);

            ShareUrlPanel.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
