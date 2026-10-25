namespace LanFileTransfer
{
    /// <summary>「転送状況」バッジが表す状態。</summary>
    public enum TransferState
    {
        /// <summary>まだ送受信を開始していない。</summary>
        Idle,

        /// <summary>ファイルを送信中。</summary>
        Sending,

        /// <summary>受信待機中(接続待ち)。</summary>
        Listening,

        /// <summary>ファイルを受信中。</summary>
        Receiving
    }
}
