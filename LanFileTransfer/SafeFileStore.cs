using System.IO;

namespace LanFileTransfer
{
    /// <summary>
    /// 受信したファイルの保存先を安全に扱うためのヘルパー。
    /// (ファイル名の無害化・重複回避・空き容量チェック・不完全ファイルの削除)
    /// </summary>
    internal static class SafeFileStore
    {
        /// <summary>ファイル名(文字数)の上限。</summary>
        private const int MaxFileNameLength = 200;

        /// <summary>
        /// 受信したファイル名から危険な文字・パス区切り・予約名を除去し、安全な名前にする。
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            // 曲者な末尾のドット・空白を除去(. や .. もここで弾く)
            name = name.Trim().TrimEnd('.');

            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            {
                return "received_file";
            }

            string baseName = Path.GetFileNameWithoutExtension(name);
            if (IsReservedDeviceName(baseName))
            {
                name = "_" + name;
            }

            if (name.Length > MaxFileNameLength)
            {
                string ext = Path.GetExtension(name);
                string stem = Path.GetFileNameWithoutExtension(name);
                int keep = Math.Max(1, MaxFileNameLength - ext.Length);
                if (stem.Length > keep)
                {
                    stem = stem.Substring(0, keep);
                }
                name = stem + ext;
            }

            return name;
        }

        /// <summary>
        /// Windows の予約デバイス名(CON, PRN, AUX, NUL, COM1-9, LPT1-9)かどうか。
        /// </summary>
        private static bool IsReservedDeviceName(string baseName)
        {
            string n = baseName.Trim().ToUpperInvariant();

            if (n is "CON" or "PRN" or "AUX" or "NUL")
            {
                return true;
            }

            return n.Length == 4
                && (n.StartsWith("COM") || n.StartsWith("LPT"))
                && n[3] is >= '1' and <= '9';
        }

        /// <summary>同名ファイルが既にある場合は "名前(1).拡張子" のように採番して返す。</summary>
        public static string GetUniquePath(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return path;
            }

            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(directory, $"{nameOnly}({i}){ext}");
                i++;
            } while (File.Exists(candidate));

            return candidate;
        }

        /// <summary>
        /// 保存先ドライブの空き容量が不足している場合は受信を拒否する(ディスク枯渇対策)。
        /// 判定できない環境(ネットワーク共有など)では安全側で続行する。
        /// </summary>
        public static void EnsureDiskSpace(string directory, long fileSize)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(directory));
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }

                var drive = new DriveInfo(root);
                if (drive.IsReady && fileSize > drive.AvailableFreeSpace)
                {
                    throw new IOException("保存先の空き容量が不足しているため受信を中止しました。");
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // DriveInfo が使えない場合は判定をスキップする
            }
        }

        /// <summary>途中で失敗した不完全なファイルを残さないように削除する。</summary>
        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 削除できなくても致命的ではない
            }
        }
    }
}
