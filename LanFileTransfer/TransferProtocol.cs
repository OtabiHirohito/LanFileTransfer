using System.IO;
using System.Text;

namespace LanFileTransfer
{
    /// <summary>
    /// 転送のワイヤ形式(ヘッダ部)の読み書きと、受信時に検証する上限値。
    ///
    /// 論理ストリーム:
    ///   [4byte ファイル名長][ファイル名 UTF8][8byte ファイルサイズ][本体][32byte SHA-256]
    /// (この前段に <see cref="SecureChannel"/> のハンドシェイクが入る)
    /// </summary>
    internal static class TransferProtocol
    {
        /// <summary>読み書きに使うバッファサイズ。</summary>
        public const int BufferSize = 81920; // 80KB

        /// <summary>本体末尾に付ける検証用ハッシュの長さ (SHA-256)。</summary>
        public const int HashSize = 32;

        /// <summary>ファイル名(UTF8)として許容する最大バイト数。</summary>
        public const int MaxFileNameBytes = 4096;

        /// <summary>1ファイルとして許容する最大サイズ。</summary>
        public const long MaxFileSize = 100L * 1024 * 1024 * 1024; // 100 GiB

        /// <summary>ヘッダ(ファイル名長 -> ファイル名 -> ファイルサイズ)を送信する。</summary>
        public static async Task WriteHeaderAsync(
            SecureChannel channel,
            string fileName,
            long fileSize,
            CancellationToken cancellationToken)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
            await channel.WriteAsync(BitConverter.GetBytes(nameBytes.Length), cancellationToken);
            await channel.WriteAsync(nameBytes, cancellationToken);
            await channel.WriteAsync(BitConverter.GetBytes(fileSize), cancellationToken);
        }

        /// <summary>
        /// ヘッダを受信し、上限値を検証してから返す。
        /// 不正な値(過大なファイル名長・サイズ)はこの時点で拒否する。
        /// </summary>
        public static async Task<(string FileName, long FileSize)> ReadHeaderAsync(
            SecureChannel channel,
            CancellationToken cancellationToken)
        {
            byte[] lengthBuffer = new byte[sizeof(int)];
            await channel.ReadExactAsync(lengthBuffer, cancellationToken);
            int nameLength = BitConverter.ToInt32(lengthBuffer, 0);
            if (nameLength <= 0 || nameLength > MaxFileNameBytes)
            {
                throw new InvalidDataException($"ファイル名長が不正なため接続を拒否しました。({nameLength})");
            }

            byte[] nameBuffer = new byte[nameLength];
            await channel.ReadExactAsync(nameBuffer, cancellationToken);
            string fileName = Encoding.UTF8.GetString(nameBuffer);

            byte[] sizeBuffer = new byte[sizeof(long)];
            await channel.ReadExactAsync(sizeBuffer, cancellationToken);
            long fileSize = BitConverter.ToInt64(sizeBuffer, 0);
            if (fileSize < 0 || fileSize > MaxFileSize)
            {
                throw new InvalidDataException($"ファイルサイズが不正なため接続を拒否しました。({fileSize})");
            }

            return (fileName, fileSize);
        }
    }
}
