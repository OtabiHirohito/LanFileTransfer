using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanFileTransfer
{
    /// <summary>
    /// 選んだファイルをブラウザからダウンロードできるようにする、最小限の HTTP/HTTPS サーバー。
    ///
    /// - <see cref="TcpListener"/> 上に HTTP/1.1 の GET/HEAD だけを実装する。
    ///   暗号化を有効にすると TLS(<see cref="SslStream"/>) で包む。
    ///   (ASP.NET Core の追加ランタイムも、管理者権限が必要な HttpListener の URL ACL も使わない)
    /// - 推測できないトークン付き URL (任意) を知っているクライアントだけが取得できる。
    /// - 接続元 IP を限定できる(任意)。許可されていないアドレスには 403 を返す。
    /// - HTTPS を有効にすると自己署名証明書を使うため、ブラウザには証明書の警告が出る。
    /// - 有効期限を過ぎると新しい接続の受け付けを止める。
    ///   ただし開始済みのダウンロードは中断しない(大きいファイルを取りこぼさないため)。
    /// </summary>
    public sealed partial class BrowserShareServer : IDisposable
    {
        private const int MaxHeaderBytes = 8 * 1024;
        private const int MaxConcurrentClients = 32;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

        private readonly TcpListener _listener;
        private readonly IReadOnlyList<ShareEntry> _files;
        private readonly string _token;
        private readonly byte[] _tokenBytes;
        private readonly bool _useToken;
        private readonly bool _useHttps;
        private readonly ClientAddressFilter _clientFilter;
        private readonly IPAddress _listenAddress;
        private readonly int _port;
        private readonly DateTimeOffset _expiresAt;
        private readonly Action<BrowserShareProgress> _onProgress;
        private readonly Action<string> _onDownloadCompleted;
        private readonly Action<string> _onLog;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly List<Task> _clientTasks = new();
        private readonly object _clientsLock = new();
        private readonly X509Certificate2? _certificate;
        private readonly SslServerAuthenticationOptions? _sslOptions;

        private Task _acceptLoop = Task.CompletedTask;
        private Task _expiryTimer = Task.CompletedTask;
        private int _activeClients;
        private int _stopped;

        private BrowserShareServer(
            IPAddress listenAddress,
            int port,
            IReadOnlyList<string> filePaths,
            TimeSpan lifetime,
            bool useToken,
            bool useHttps,
            ClientAddressFilter clientFilter,
            Action<BrowserShareProgress> onProgress,
            Action<string> onDownloadCompleted,
            Action<string> onLog)
        {
            _port = port;
            _listenAddress = listenAddress;
            _useToken = useToken;
            _useHttps = useHttps;
            _clientFilter = clientFilter;
            _expiresAt = DateTimeOffset.UtcNow + lifetime;
            _token = CreateToken();
            _tokenBytes = Encoding.ASCII.GetBytes(_token);
            _onProgress = onProgress;
            _onDownloadCompleted = onDownloadCompleted;
            _onLog = onLog;

            _files = filePaths
                .Select(path => new ShareEntry(Path.GetFileName(path), Path.GetFullPath(path), FileSize.GetLengthSafe(path)))
                .ToList();

            if (_useHttps)
            {
                _certificate = BrowserShareCertificate.Create();
                _sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 }
                };
            }

            _listener = new TcpListener(listenAddress, port);
        }

        /// <summary>待ち受けを開始する。ポートが使用中の場合は <see cref="SocketException"/> を投げる。</summary>
        public static BrowserShareServer Start(
            IPAddress listenAddress,
            int port,
            IReadOnlyList<string> filePaths,
            TimeSpan lifetime,
            bool useToken,
            bool useHttps,
            ClientAddressFilter clientFilter,
            Action<BrowserShareProgress> onProgress,
            Action<string> onDownloadCompleted,
            Action<string> onLog)
        {
            var server = new BrowserShareServer(
                listenAddress, port, filePaths, lifetime, useToken, useHttps, clientFilter,
                onProgress, onDownloadCompleted, onLog);

            try
            {
                server._listener.Start();
            }
            catch
            {
                server.Dispose();
                throw;
            }

            server._acceptLoop = Task.Run(server.AcceptLoopAsync);
            server._expiryTimer = Task.Run(() => server.ExpireAsync(lifetime));
            return server;
        }

        /// <summary>共有URL。特定アドレスを選んだ場合はそのアドレスのURLだけを返す。</summary>
        public IReadOnlyList<string> BuildShareUrls()
        {
            var urls = new List<string>();
            string scheme = _useHttps ? "https" : "http";
            string path = _useToken ? $"/s/{_token}/" : "/";

            if (_listenAddress.Equals(IPAddress.Any))
            {
                urls.AddRange(NetworkHelper.GetLocalIPv4Addresses()
                    .OrderBy(RankAddress)
                    .Select(ip => $"{scheme}://{ip}:{_port}{path}"));
                urls.Add($"{scheme}://localhost:{_port}{path}");
            }
            else
            {
                urls.Add($"{scheme}://{_listenAddress}:{_port}{path}");
                if (IPAddress.IsLoopback(_listenAddress))
                {
                    urls.Add($"{scheme}://localhost:{_port}{path}");
                }
            }

            return urls;
        }

        /// <summary>よく使うプライベートアドレスを先に並べるための順位。</summary>
        public static int RankAddress(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return 9;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return 0; // 家庭・社内LANで最も一般的
            }
            if (bytes[0] == 10)
            {
                return 1;
            }
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return 2;
            }
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return 5; // Tailscale など
            }
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return 6; // リンクローカル(未設定)
            }

            return 3;
        }

        public TimeSpan Remaining => _expiresAt - DateTimeOffset.UtcNow;

        public bool IsExpired => DateTimeOffset.UtcNow >= _expiresAt;

        /// <summary>
        /// 新しい接続の受け付けを止める。開始済みのダウンロードは完了まで継続する。
        /// 何度呼んでも安全。
        /// </summary>
        public void StopAccepting()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
                // すでに閉じている場合は無視する
            }
        }

        /// <summary>受け付け停止後、実行中のダウンロードがすべて終わるまで待つ。</summary>
        public async Task WaitForShutdownAsync()
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // 待ち受け終了時の例外は無視する
            }

            Task[] pending;
            lock (_clientsLock)
            {
                pending = _clientTasks.ToArray();
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                // 個々の失敗は各接続内で処理済み
            }
        }

        /// <summary>強制終了。ダウンロード中の接続も中断する。</summary>
        public void Dispose()
        {
            StopAccepting();
            _lifetimeCts.Cancel();

            try
            {
                _certificate?.Dispose();
            }
            catch
            {
                // 終了時の失敗は無視する
            }
        }

        // ===== 待ち受け =====

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (Volatile.Read(ref _stopped) != 0)
                {
                    client.Dispose();
                    break;
                }

                if (Interlocked.Increment(ref _activeClients) > MaxConcurrentClients)
                {
                    Interlocked.Decrement(ref _activeClients);
                    client.Dispose();
                    continue;
                }

                Task task = HandleClientAsync(client);
                lock (_clientsLock)
                {
                    _clientTasks.RemoveAll(t => t.IsCompleted);
                    _clientTasks.Add(task);
                }
            }
        }

        /// <summary>有効期限が来たら自分で受け付けを止める(UI 側のタイマーに依存しない)。</summary>
        private async Task ExpireAsync(TimeSpan lifetime)
        {
            try
            {
                await Task.Delay(lifetime, _lifetimeCts.Token).ConfigureAwait(false);
                StopAccepting();
            }
            catch (OperationCanceledException)
            {
                // 手動停止
            }
        }

        // ===== 1接続の処理 =====

        private async Task HandleClientAsync(TcpClient client)
        {
            string remote = "?";
            IPAddress? remoteAddress = null;
            try
            {
                IPEndPoint? remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                remoteAddress = remoteEndPoint?.Address;
                remote = remoteAddress?.ToString() ?? "?";
                using (client)
                {
                    client.NoDelay = true;
                    using NetworkStream network = client.GetStream();
                    SslStream? ssl = null;

                    try
                    {
                        Stream stream = network;

                        if (_useHttps)
                        {
                            ssl = new SslStream(network, leaveInnerStreamOpen: false);

                            // TLS ハンドシェイク(証明書の警告を無視して続行したブラウザだけが先へ進める)
                            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token))
                            {
                                handshakeCts.CancelAfter(RequestTimeout);
                                try
                                {
                                    await ssl.AuthenticateAsServerAsync(_sslOptions!, handshakeCts.Token).ConfigureAwait(false);
                                }
                                catch (AuthenticationException ex)
                                {
                                    // 平文で接続してきた場合など。ハンドシェイク失敗は静かに切る
                                    _onLog($"TLS 接続に失敗しました ({remote})。URL が https:// で始まっているか確認してください。({ex.Message})");
                                    return;
                                }
                                catch (IOException)
                                {
                                    return;
                                }
                                catch (OperationCanceledException)
                                {
                                    return;
                                }
                            }

                            stream = ssl;
                        }

                        HttpRequest? request = await ReadRequestAsync(stream).ConfigureAwait(false);
                        if (request is null)
                        {
                            return;
                        }

                        bool headOnly = request.Method == "HEAD";
                        if (request.Method != "GET" && !headOnly)
                        {
                            await WriteResponseAsync(
                                stream, "405 Method Not Allowed", "text/plain; charset=utf-8",
                                "GET / HEAD のみ対応しています。", false).ConfigureAwait(false);
                            return;
                        }

                        if (!_clientFilter.IsAllowed(remoteAddress))
                        {
                            _onLog($"接続元 {remote} からのアクセスを拒否しました(接続元IPの制限)。");
                            await WriteResponseAsync(
                                stream, "403 Forbidden", "text/html; charset=utf-8",
                                BuildMessagePage(
                                    "アクセスが拒否されました",
                                    "この共有は、送信側で許可された IP アドレスからのみ利用できます。送信側の設定を確認してください。"),
                                headOnly).ConfigureAwait(false);
                            return;
                        }

                        await DispatchAsync(stream, request, remote, headOnly).ConfigureAwait(false);
                    }
                    finally
                    {
                        ssl?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 停止による中断
            }
            catch (IOException)
            {
                // ブラウザが途中で切断した
            }
            catch (SocketException)
            {
                // 同上
            }
            catch (Exception ex)
            {
                _onLog($"ブラウザ共有エラー ({remote}): {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeClients);
            }
        }

        private static string CreateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(12);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private sealed record ShareEntry(string FileName, string FullPath, long Length);

        private sealed record HttpRequest(
            string Method, string Target, string Version, Dictionary<string, string> Headers);
    }
}
