using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LanFileTransfer
{
    /// <summary>転送進捗を通知するための情報。</summary>
    public sealed class TransferProgressInfo
    {
        public string FileName { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public long TransferredBytes { get; init; }
        public double Percentage => TotalBytes == 0 ? 0 : (double)TransferredBytes / TotalBytes * 100.0;
    }

    /// <summary>
    /// 自作プロトコルでファイルを送受信するサービス。1接続につきファイル1つを送る。
    /// 合言葉を設定すると <see cref="SecureChannel"/> により AES-256-GCM で暗号化される。
    /// 本体の SHA-256 は「送りながら計算」して最後に送り、受信側で照合する。
    /// </summary>
    public static class FileTransferService
    {
        /// <summary>
        /// 受信側が「受信待機を開始」するまで接続を再試行し続ける時間。
        /// 送信側を先に操作した場合でも、この時間内に受信待機が始まれば送信できる。
        /// </summary>
        public static readonly TimeSpan ReceiverWaitTimeout = TimeSpan.FromSeconds(60);

        private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 指定したIP・ポートへファイルを送信する。
        /// password を指定すると AES-256-GCM で暗号化して送る。
        /// 受信側がまだ待機していない場合は <see cref="ReceiverWaitTimeout"/> の間つなぎ直す。
        /// </summary>
        /// <param name="onWaiting">
        /// 接続を待っている間の状況を通知する(未指定可)。接続できたときは接続完了のメッセージを渡す。
        /// </param>
        public static async Task SendFileAsync(
            string targetIp,
            int port,
            string filePath,
            string? password,
            IProgress<TransferProgressInfo> progress,
            CancellationToken cancellationToken,
            IProgress<string>? onWaiting = null)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("送信対象のファイルが見つかりません。", filePath);
            }

            using var client = await ConnectWithRetryAsync(targetIp, port, onWaiting, cancellationToken);

            using var networkStream = client.GetStream();
            using var channel = await SecureChannel.CreateWriterAsync(networkStream, password, cancellationToken);
            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                TransferProtocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            string fileName = Path.GetFileName(filePath);
            long fileSize = fileStream.Length;

            await TransferProtocol.WriteHeaderAsync(channel, fileName, fileSize, cancellationToken);

            // 本体を送りながら SHA-256 を計算する
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[TransferProtocol.BufferSize];
            long sent = 0;
            int read;

            while ((read = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await channel.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;

                progress.Report(CreateProgress(fileName, fileSize, sent));
            }

            // 最後にハッシュ(32byte)を送る
            await channel.WriteAsync(sha.GetHashAndReset(), cancellationToken);
            await channel.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// 接続を試み、受信側がまだ待機していない場合は一定時間つなぎ直す。
        /// 受信側で「受信待機を開始」してから送信開始を押す順番でなくても送信できるようにする。
        /// </summary>
        private static async Task<TcpClient> ConnectWithRetryAsync(
            string targetIp,
            int port,
            IProgress<string>? onWaiting,
            CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + ReceiverWaitTimeout;
            bool waited = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var client = new TcpClient();
                try
                {
                    await ConnectOnceAsync(client, targetIp, port, cancellationToken);

                    if (waited)
                    {
                        onWaiting?.Report("受信側に接続しました。送信を開始します...");
                    }

                    return client;
                }
                catch (Exception ex) when (IsRetryableConnectError(ex, cancellationToken))
                {
                    client.Dispose();
                    waited = true;

                    TimeSpan remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new IOException(
                            "受信側に接続できませんでした。受信側のアプリで「受信待機を開始」しているか、" +
                            "IPアドレスとポート番号が合っているか確認してください。" +
                            $"（{ReceiverWaitTimeout.TotalSeconds:F0}秒間 接続を待ちました）", ex);
                    }

                    onWaiting?.Report(
                        $"受信側の受信待機を待っています...（あと{Math.Ceiling(remaining.TotalSeconds):F0}秒）");

                    await Task.Delay(
                        remaining < ConnectRetryInterval ? remaining : ConnectRetryInterval,
                        cancellationToken);
                }
            }
        }

        /// <summary>1回分の接続。応答が無い場合に備えて試行ごとの制限時間を設ける。</summary>
        private static async Task ConnectOnceAsync(
            TcpClient client,
            string targetIp,
            int port,
            CancellationToken cancellationToken)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(ConnectAttemptTimeout);

            try
            {
                await client.ConnectAsync(targetIp, port, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 全体のキャンセルではなく、この試行だけが時間切れになった
                throw new TimeoutException("接続がタイムアウトしました。");
            }
        }

        /// <summary>受信側の待機待ちとして再試行してよいエラーか。</summary>
        private static bool IsRetryableConnectError(Exception ex, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (ex is TimeoutException)
            {
                return true;
            }

            // 相手がまだ待機していない(拒否)、一時的に到達できない、などの再接続で解消しうるものだけ再試行する
            return ex is SocketException socketException &&
                   socketException.SocketErrorCode is
                       SocketError.ConnectionRefused or
                       SocketError.TimedOut or
                       SocketError.HostUnreachable or
                       SocketError.NetworkUnreachable or
                       SocketError.HostDown or
                       SocketError.NetworkDown or
                       SocketError.TryAgain;
        }

        private static TransferProgressInfo CreateProgress(
            string fileName, long totalBytes, long transferredBytes)
        {
            return new TransferProgressInfo
            {
                FileName = fileName,
                TotalBytes = totalBytes,
                TransferredBytes = transferredBytes
            };
        }

        /// <summary>
        /// 指定アドレス・ポートで待ち受け、接続を受け付けるたびにファイルを1つ受信して保存フォルダに書き込む。
        /// キャンセルされるまで繰り返し接続を受け付け続ける。
        /// 1件の受信に失敗してもループは止めず、onError に通知して次の接続を待つ。
        /// </summary>
        public static async Task StartReceivingAsync(
            IPAddress listenAddress,
            int port,
            string saveDirectory,
            string? password,
            IProgress<TransferProgressInfo> progress,
            Action<string> onFileReceived,
            CancellationToken cancellationToken,
            Action<string>? onError = null)
        {
            Directory.CreateDirectory(saveDirectory);

            var listener = new TcpListener(listenAddress, port);
            listener.Start();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    try
                    {
                        await ReceiveOneFileAsync(client, saveDirectory, password, progress, onFileReceived, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // 停止ボタンによる正常なキャンセル
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 不正なデータや切断で受信ループ全体を止めない
                        onError?.Invoke(ex.Message);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>1接続分のファイルを受信して保存する。</summary>
        private static async Task ReceiveOneFileAsync(
            TcpClient client,
            string saveDirectory,
            string? password,
            IProgress<TransferProgressInfo> progress,
            Action<string> onFileReceived,
            CancellationToken cancellationToken)
        {
            using (client)
            using (var networkStream = client.GetStream())
            using (var channel = await SecureChannel.CreateReaderAsync(networkStream, password, cancellationToken))
            {
                var (rawFileName, fileSize) = await TransferProtocol.ReadHeaderAsync(channel, cancellationToken);
                string fileName = SafeFileStore.SanitizeFileName(rawFileName);

                SafeFileStore.EnsureDiskSpace(saveDirectory, fileSize);
                string savePath = SafeFileStore.GetUniquePath(saveDirectory, fileName);

                try
                {
                    await ReceiveBodyAsync(channel, savePath, fileName, fileSize, progress, cancellationToken);
                    onFileReceived(savePath);
                }
                catch
                {
                    SafeFileStore.TryDelete(savePath);
                    throw;
                }
            }
        }

        /// <summary>本体を受信しつつ保存し、最後に送られてくる SHA-256 と照合する。</summary>
        private static async Task ReceiveBodyAsync(
            SecureChannel channel,
            string savePath,
            string fileName,
            long fileSize,
            IProgress<TransferProgressInfo> progress,
            CancellationToken cancellationToken)
        {
            using var fileStream = new FileStream(
                savePath, FileMode.Create, FileAccess.Write, FileShare.None,
                TransferProtocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[TransferProtocol.BufferSize];
            long received = 0;

            while (received < fileSize)
            {
                int toRead = (int)Math.Min(buffer.Length, fileSize - received);
                await channel.ReadExactAsync(buffer.AsMemory(0, toRead), cancellationToken);
                await fileStream.WriteAsync(buffer.AsMemory(0, toRead), cancellationToken);
                sha.AppendData(buffer, 0, toRead);
                received += toRead;

                progress.Report(CreateProgress(fileName, fileSize, received));
            }

            // 送信側が最後に送ってきたハッシュと照合する
            byte[] expectedHash = new byte[TransferProtocol.HashSize];
            await channel.ReadExactAsync(expectedHash, cancellationToken);

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, sha.GetHashAndReset()))
            {
                throw new InvalidDataException("ハッシュが一致しません。(データ破損または改ざんの可能性があります)");
            }
        }
    }
}
