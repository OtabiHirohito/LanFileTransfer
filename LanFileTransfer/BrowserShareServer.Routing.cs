using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanFileTransfer
{
    public sealed partial class BrowserShareServer
    {
        private async Task DispatchAsync(Stream stream, HttpRequest request, string remote, bool headOnly)
        {
            string target = request.Target;
            string query = string.Empty;
            int queryIndex = target.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = target[(queryIndex + 1)..];
                target = target[..queryIndex];
            }

            if (target.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "204 No Content", null, string.Empty, true).ConfigureAwait(false);
                return;
            }

            if (!TryAuthorize(target, out string rest))
            {
                await WriteResponseAsync(
                    stream, "404 Not Found", "text/html; charset=utf-8",
                    BuildMessagePage("ページが見つかりません", "送信側の画面に表示されたURLをそのまま開いてください。"),
                    headOnly).ConfigureAwait(false);
                return;
            }

            if (rest.Length == 0 || rest == "/")
            {
                if (IsExpired)
                {
                    await WriteExpiredAsync(stream, headOnly).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", BuildIndexPage(), headOnly)
                    .ConfigureAwait(false);
                return;
            }

            if (rest == "/status")
            {
                long remaining = (long)Math.Max(0, Remaining.TotalSeconds);
                string json = $"{{\"remainingSeconds\":{remaining},\"expired\":{(IsExpired ? "true" : "false")}}}";
                await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, headOnly)
                    .ConfigureAwait(false);
                return;
            }

            if (rest.Equals("/zip", StringComparison.OrdinalIgnoreCase))
            {
                if (IsExpired)
                {
                    await WriteExpiredAsync(stream, headOnly).ConfigureAwait(false);
                    return;
                }

                List<int> indexes = ParseFileIndexes(query);
                if (indexes.Count == 0)
                {
                    await WriteResponseAsync(
                        stream, "400 Bad Request", "text/html; charset=utf-8",
                        BuildMessagePage("ファイルが選ばれていません", "一覧のページに戻って、ZIP にまとめるファイルを選び直してください。"),
                        headOnly).ConfigureAwait(false);
                    return;
                }

                await ServeZipAsync(stream, indexes, remote, headOnly).ConfigureAwait(false);
                return;
            }

            if (rest.StartsWith("/f/", StringComparison.Ordinal))
            {
                if (IsExpired)
                {
                    await WriteExpiredAsync(stream, headOnly).ConfigureAwait(false);
                    return;
                }

                if (!int.TryParse(rest[3..], out int index) || index < 0 || index >= _files.Count)
                {
                    await WriteResponseAsync(
                        stream, "404 Not Found", "text/html; charset=utf-8",
                        BuildMessagePage("ファイルが見つかりません", "一覧のページに戻って選び直してください。"),
                        headOnly).ConfigureAwait(false);
                    return;
                }

                await ServeFileAsync(stream, index, request.Headers, remote, headOnly).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(
                stream, "404 Not Found", "text/html; charset=utf-8",
                BuildMessagePage("ページが見つかりません", "URLを確認してください。"), headOnly).ConfigureAwait(false);
        }

        private List<int> ParseFileIndexes(string query)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();

            foreach (string pair in query.Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0 || !pair[..equals].Equals("f", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string part in pair[(equals + 1)..].Split(','))
                {
                    if (int.TryParse(Uri.UnescapeDataString(part), out int index)
                        && index >= 0 && index < _files.Count && seen.Add(index))
                    {
                        result.Add(index);
                    }
                }
            }

            return result;
        }

        // ===== HTTP 読み取り =====

        private static async Task<HttpRequest?> ReadRequestAsync(Stream stream)
        {
            byte[] buffer = new byte[MaxHeaderBytes];
            int total = 0;
            int headerEnd = -1;

            using var timeout = new CancellationTokenSource(RequestTimeout);

            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                total += read;
                headerEnd = FindHeaderEnd(buffer, total);
                if (headerEnd >= 0)
                {
                    break;
                }
            }

            if (headerEnd <= 0)
            {
                return null;
            }

            string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            string[] lines = headerText.Split("\r\n");
            if (lines.Length == 0)
            {
                return null;
            }

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 3)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            return new HttpRequest(parts[0].ToUpperInvariant(), parts[1], parts[2], headers);
        }

        /// <summary>ヘッダ終端(\r\n\r\n)の先頭位置を返す。見つからなければ -1。</summary>
        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (buffer[i] == (byte)'\n' && buffer[i - 1] == (byte)'\r'
                    && buffer[i - 2] == (byte)'\n' && buffer[i - 3] == (byte)'\r')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private bool TryAuthorize(string path, out string rest)
        {
            rest = string.Empty;

            // トークンなし: パスをそのまま受け付ける
            if (!_useToken)
            {
                rest = path.StartsWith('/') ? path : "/" + path;
                return true;
            }

            const string prefix = "/s/";

            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string tail = path[prefix.Length..];
            if (tail.Length < _token.Length)
            {
                return false;
            }

            string candidate = tail[.._token.Length];
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), _tokenBytes))
            {
                return false;
            }

            rest = tail[_token.Length..];
            return true;
        }
    }
}
