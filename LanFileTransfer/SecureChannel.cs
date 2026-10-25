using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanFileTransfer
{
    /// <summary>
    /// ファイル転送用の簡易セキュアチャネル。
    ///
    /// - 接続の先頭で双方向のハンドシェイクを行い、受信側の設定不一致や
    ///   合言葉の不一致を「本文を送る前」に検出できるようにする
    /// - 暗号化が有効な場合、合言葉から PBKDF2 で鍵を導出し、AES-256-GCM で
    ///   「書き込み単位」を1レコードとして暗号化する
    /// - 平文の場合も同じ読み書きAPIで透過的に扱えるようにする
    ///
    /// ハンドシェイク:
    ///   送信→受信: [4byte マジック('LFT2')][1byte フラグ(bit0=暗号化)][暗号化時のみ 16byte ソルト]
    ///   受信→送信: [1byte ステータス(0=OK, 1=拒否)]
    ///              拒否時は続けて [2byte 理由長][理由 UTF-8]
    ///              OK かつ暗号化時は続けて [32byte 鍵確認値]
    ///
    /// 暗号化レコード(1書き込み単位):
    ///   [4byte 平文長][平文長byte の暗号文][16byte 認証タグ]
    /// </summary>
    internal sealed class SecureChannel : IDisposable
    {
        private const int Magic = 0x4C465432; // 'L','F','T','2'
        private const byte EncryptionFlag = 1;
        private const byte StatusOk = 0;
        private const byte StatusRejected = 1;
        private const int SaltSize = 16;
        private const int TagSize = 16;
        private const int NonceSize = 12;
        private const int MaxRecordSize = 128 * 1024;
        private const int MaxRejectReasonBytes = 4096;
        private const int Pbkdf2Iterations = 100_000;

        private static readonly byte[] StatusOkBuffer = { StatusOk };

        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

        private readonly Stream _inner;
        private readonly AesGcm? _aes;
        private readonly byte[] _nonce = new byte[NonceSize];
        private long _counter;

        private readonly byte[] _readPlain = new byte[MaxRecordSize];
        private int _readOffset;
        private int _readCount;

        private SecureChannel(Stream inner, AesGcm? aes)
        {
            _inner = inner;
            _aes = aes;
        }

        /// <summary>送信側: ハンドシェイクを行ってチャネルを用意する。</summary>
        public static async Task<SecureChannel> CreateWriterAsync(Stream inner, string? password, CancellationToken cancellationToken)
        {
            bool encrypted = !string.IsNullOrEmpty(password);

            byte[] handshake;
            byte[]? key = null;

            if (encrypted)
            {
                byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
                key = DeriveKey(password!, salt);
                handshake = BuildHandshake(salt);
            }
            else
            {
                handshake = BuildHandshake(null);
            }

            await WriteHandshakeAsync(inner, handshake, cancellationToken);

            // 受信側の応答(続行 or 拒否理由)を待つ
            byte status = await ReadWriterStatusAsync(inner, cancellationToken);
            if (status != StatusOk)
            {
                string reason = await TryReadRejectReasonAsync(inner, cancellationToken);
                throw new InvalidDataException(
                    "受信側が接続を拒否しました。" + (string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})"));
            }

            if (key != null)
            {
                // 受信側が返した鍵確認値を照合し、合言葉が一致しているかを本文送信前に確認する
                await VerifyConfirmationAsync(inner, handshake, key, cancellationToken);
            }

            AesGcm? aes = key != null ? new AesGcm(key, TagSize) : null;
            return new SecureChannel(inner, aes);
        }

        private static async Task WriteHandshakeAsync(
            Stream inner, byte[] handshake, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(handshake, cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        private static async Task<byte> ReadWriterStatusAsync(Stream inner, CancellationToken cancellationToken)
        {
            byte[] statusBuffer = new byte[1];
            try
            {
                await ReadRawExactAsync(inner, statusBuffer, HandshakeTimeout, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                throw new InvalidDataException(
                    "受信側と通信できませんでした。受信側が旧バージョン、または受信待機していない可能性があります。", ex);
            }

            return statusBuffer[0];
        }

        private static async Task VerifyConfirmationAsync(
            Stream inner, byte[] handshake, byte[] key, CancellationToken cancellationToken)
        {
            byte[] expected = ComputeConfirmation(handshake, key);
            byte[] actual = new byte[expected.Length];
            await ReadRawExactAsync(inner, actual, HandshakeTimeout, cancellationToken);

            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException(
                    "合言葉が一致しません。送信側と受信側で同じ合言葉を設定してください。");
            }
        }

        /// <summary>受信側: ハンドシェイクを行ってチャネルを用意する。</summary>
        public static async Task<SecureChannel> CreateReaderAsync(Stream inner, string? password, CancellationToken cancellationToken)
        {
            // まずマジック値だけを読む(旧バージョンや別プロトコルを早い段階で弾く)
            byte[] magicBuffer = new byte[sizeof(int)];
            await ReadRawExactAsync(inner, magicBuffer, HandshakeTimeout, cancellationToken);
            if (BitConverter.ToInt32(magicBuffer, 0) != Magic)
            {
                throw new InvalidDataException("プロトコルが一致しません。(相手が古いバージョンの可能性があります)");
            }

            byte[] flagBuffer = new byte[1];
            await ReadRawExactAsync(inner, flagBuffer, HandshakeTimeout, cancellationToken);

            bool peerEncrypted = flagBuffer[0] == EncryptionFlag;
            bool havePassword = !string.IsNullOrEmpty(password);

            // 相手が暗号化を指定している場合は、判定より前に必ずソルトまで読み切る。
            // (未読データを残したまま切断すると RST になり、拒否理由が送信側に届かなくなる)
            byte[]? salt = null;
            if (peerEncrypted)
            {
                salt = new byte[SaltSize];
                await ReadRawExactAsync(inner, salt, HandshakeTimeout, cancellationToken);
            }

            // 合言葉(暗号化)のON/OFFが食い違う場合は、理由を送り返してから切断する
            if (peerEncrypted != havePassword)
            {
                string reason = peerEncrypted
                    ? "相手は合言葉(暗号化)を指定していますが、受信側で合言葉が未設定です。"
                    : "相手が合言葉(暗号化)を指定していません。受信側の合言葉設定と一致しません。";
                await TrySendRejectAsync(inner, reason, cancellationToken);
                throw new InvalidDataException(reason);
            }

            if (!peerEncrypted)
            {
                await SendStatusOkAsync(inner, cancellationToken);
                return new SecureChannel(inner, null);
            }

            byte[] key = DeriveKey(password!, salt!);
            var aes = new AesGcm(key, TagSize);

            byte[] handshake = BuildHandshake(salt);

            // 続行を通知し、鍵確認値を送る
            await SendStatusOkAsync(inner, cancellationToken);
            await inner.WriteAsync(ComputeConfirmation(handshake, key), cancellationToken);
            await inner.FlushAsync(cancellationToken);

            return new SecureChannel(inner, aes);
        }

        /// <summary>ハンドシェイク先頭部を組み立てる(暗号化時はソルトを含む)。</summary>
        private static byte[] BuildHandshake(byte[]? salt)
        {
            byte[] handshake = new byte[5 + (salt?.Length ?? 0)];
            BitConverter.GetBytes(Magic).CopyTo(handshake, 0);
            handshake[4] = salt == null ? (byte)0 : EncryptionFlag;
            salt?.CopyTo(handshake, 5);
            return handshake;
        }

        private static async Task SendStatusOkAsync(Stream inner, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(StatusOkBuffer, cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        /// <summary>受信側: 拒否理由を送り返す(送れなくても致命的ではない)。</summary>
        private static async Task TrySendRejectAsync(Stream inner, string reason, CancellationToken cancellationToken)
        {
            try
            {
                byte[] message = Encoding.UTF8.GetBytes(reason);
                if (message.Length > MaxRejectReasonBytes)
                {
                    message = message.AsSpan(0, MaxRejectReasonBytes).ToArray();
                }

                byte[] buffer = new byte[1 + 2 + message.Length];
                buffer[0] = StatusRejected;
                BitConverter.GetBytes((ushort)message.Length).CopyTo(buffer, 1);
                message.CopyTo(buffer, 3);

                await inner.WriteAsync(buffer, cancellationToken);
                await inner.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 理由を伝えられなくても、切断すれば受信ループは継続できる
            }
        }

        /// <summary>送信側: 拒否理由を読み取る(読めなければ空文字)。</summary>
        private static async Task<string> TryReadRejectReasonAsync(Stream inner, CancellationToken cancellationToken)
        {
            try
            {
                byte[] lengthBuffer = new byte[2];
                await ReadRawExactAsync(inner, lengthBuffer, HandshakeTimeout, cancellationToken);

                int length = BitConverter.ToUInt16(lengthBuffer, 0);
                if (length <= 0 || length > MaxRejectReasonBytes)
                {
                    return string.Empty;
                }

                byte[] message = new byte[length];
                await ReadRawExactAsync(inner, message, HandshakeTimeout, cancellationToken);
                return Encoding.UTF8.GetString(message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>合言葉が一致しているかを確認するための値(HMAC)。</summary>
        private static byte[] ComputeConfirmation(byte[] handshake, byte[] key)
        {
            return HMACSHA256.HashData(key, handshake);
        }

        /// <summary>書き込み(暗号化時は1レコードにまとめる)。</summary>
        public async Task WriteAsync(ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            if (plaintext.Length == 0)
            {
                return;
            }

            if (_aes == null)
            {
                await _inner.WriteAsync(plaintext, cancellationToken);
                return;
            }

            if (plaintext.Length > MaxRecordSize)
            {
                throw new ArgumentException($"1レコードの最大サイズを超えています。({plaintext.Length})");
            }

            byte[] record = new byte[sizeof(int) + plaintext.Length + TagSize];
            BitConverter.GetBytes(plaintext.Length).CopyTo(record, 0);
            _aes.Encrypt(
                NextNonce(),
                plaintext.Span,
                record.AsSpan(sizeof(int), plaintext.Length),
                record.AsSpan(sizeof(int) + plaintext.Length, TagSize));

            await _inner.WriteAsync(record, cancellationToken);
        }

        public async Task FlushAsync(CancellationToken cancellationToken) => await _inner.FlushAsync(cancellationToken);

        /// <summary>平文をちょうど destination の長さ分だけ読み切る。</summary>
        public async Task ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            int written = 0;
            while (written < destination.Length)
            {
                if (_readOffset >= _readCount)
                {
                    await FillReadBufferAsync(cancellationToken);
                }

                int take = Math.Min(_readCount - _readOffset, destination.Length - written);
                _readPlain.AsSpan(_readOffset, take).CopyTo(destination.Span.Slice(written, take));
                _readOffset += take;
                written += take;
            }
        }

        private async Task FillReadBufferAsync(CancellationToken cancellationToken)
        {
            _readOffset = 0;
            _readCount = 0;

            if (_aes == null)
            {
                // 平文: 届いている分をまとめて読む
                int n = await ReadRawSomeAsync(_inner, _readPlain, IdleTimeout, cancellationToken);
                if (n == 0)
                {
                    throw new IOException("接続が予期せず切断されました。");
                }
                _readCount = n;
                return;
            }

            // 暗号化: 1レコードを読んで復号する
            byte[] lengthBuffer = new byte[sizeof(int)];
            await ReadRawExactAsync(_inner, lengthBuffer, IdleTimeout, cancellationToken);
            int length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length <= 0 || length > MaxRecordSize)
            {
                throw new InvalidDataException($"暗号化レコードの長さが不正です。({length})");
            }

            byte[] record = new byte[length + TagSize];
            await ReadRawExactAsync(_inner, record, IdleTimeout, cancellationToken);

            try
            {
                _aes.Decrypt(
                    NextNonce(),
                    record.AsSpan(0, length),
                    record.AsSpan(length, TagSize),
                    _readPlain.AsSpan(0, length));
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("復号に失敗しました。(合言葉が違う可能性があります)");
            }

            _readCount = length;
        }

        private byte[] NextNonce()
        {
            // 鍵は接続ごとにランダムソルトで導出されるため、単純なカウンタで一意になる
            ulong c = (ulong)_counter++;
            for (int i = 0; i < 8; i++)
            {
                _nonce[NonceSize - 1 - i] = (byte)(c >> (8 * i));
            }
            return _nonce;
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        }

        /// <summary>固定長を、読み取りごとにリセットする制限時間付きで読み切る。</summary>
        private static async Task ReadRawExactAsync(Stream stream, Memory<byte> destination, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int done = 0;

            while (done < destination.Length)
            {
                timeoutCts.CancelAfter(timeout);

                int n;
                try
                {
                    n = await stream.ReadAsync(destination.Slice(done), timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("受信がタイムアウトしました。");
                }

                if (n == 0)
                {
                    throw new IOException("接続が予期せず切断されました。");
                }

                done += n;
            }
        }

        /// <summary>1回だけ、制限時間付きで読む。</summary>
        private static async Task<int> ReadRawSomeAsync(Stream stream, Memory<byte> destination, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await stream.ReadAsync(destination, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("受信がタイムアウトしました。");
            }
        }

        public void Dispose()
        {
            _aes?.Dispose();
        }
    }
}
