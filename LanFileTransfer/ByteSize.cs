namespace LanFileTransfer
{
    /// <summary>バイト数を読みやすい単位に整形する。</summary>
    public static class ByteSize
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>例: 1536 -> "1.5KB"</summary>
        public static string Format(long bytes)
        {
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:F1}{Units[unit]}";
        }
    }
}
