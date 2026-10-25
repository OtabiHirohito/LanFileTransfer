namespace LanFileTransfer
{
    /// <summary>ブラウザ共有中のダウンロード進捗。1ファイル分の転送量を表す。</summary>
    public sealed class BrowserShareProgress
    {
        public string FileName { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public long SentBytes { get; init; }
        public string ClientAddress { get; init; } = string.Empty;

        public double Percentage => TotalBytes == 0 ? 0 : (double)SentBytes / TotalBytes * 100.0;
    }
}
