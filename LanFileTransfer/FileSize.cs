using System.IO;

namespace LanFileTransfer
{
    /// <summary>ファイルサイズの取得を共通化するヘルパー。</summary>
    internal static class FileSize
    {
        /// <summary>
        /// ファイルサイズを返す。存在しない・アクセスできない場合は 0 を返す。
        /// (表示用のため、失敗しても例外にはしない)
        /// </summary>
        public static long GetLengthSafe(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
