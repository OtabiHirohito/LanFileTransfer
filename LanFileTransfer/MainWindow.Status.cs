namespace LanFileTransfer
{
    /// <summary>「転送状況」バッジと進捗バーの表示。</summary>
    public partial class MainWindow
    {
        private void SetStatus(TransferState state, string text) => _statusBadge.Show(state, text);

        private void UpdateProgressUi(TransferProgressInfo info)
        {
            // DispatcherProgress 経由で報告順にUIスレッドで実行される
            TransferProgressBar.Value = info.Percentage;
            ProgressLabel.Text = $"{info.FileName}  {ByteSize.Format(info.TransferredBytes)} / {ByteSize.Format(info.TotalBytes)}  ({info.Percentage:F1}%)";

            if (_isReceiving)
            {
                SetStatus(TransferState.Receiving, "受信中");
            }
        }

        private void ResetProgress()
        {
            TransferProgressBar.Value = 0;
            ProgressLabel.Text = "待機中...";
            SetStatus(TransferState.Idle, "待機中");
        }
    }
}
