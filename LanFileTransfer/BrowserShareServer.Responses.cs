using System.IO;
using System.Text;

namespace LanFileTransfer
{
    public sealed partial class BrowserShareServer
    {
        // ===== 応答 =====

        private static async Task WriteResponseAsync(
            Stream stream,
            string status,
            string? contentType,
            string body,
            bool headOnly)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            if (contentType is not null)
            {
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            }
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            AppendNoStoreHeaders(sb);
            sb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()).AsMemory()).ConfigureAwait(false);

            if (!headOnly && bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes.AsMemory()).ConfigureAwait(false);
            }
        }

        private static async Task WriteExpiredAsync(Stream stream, bool headOnly)
        {
            await WriteResponseAsync(
                stream, "410 Gone", "text/html; charset=utf-8",
                BuildMessagePage("共有は終了しました", "この共有の有効期限が切れています。送信側にもう一度共有を開始してもらってください。"),
                headOnly).ConfigureAwait(false);
        }

        private static async Task WriteRangeNotSatisfiableAsync(Stream stream, long length)
        {
            string header = "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                            $"Content-Range: bytes */{length}\r\n" +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header).AsMemory()).ConfigureAwait(false);
        }

        private static void AppendNoStoreHeaders(StringBuilder sb)
        {
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
        }

        /// <summary>ダウンロード用の Content-Disposition を追加する。</summary>
        private static void AppendContentDisposition(
            StringBuilder sb, string fileName, string asciiFileName)
        {
            sb.Append("Content-Disposition: attachment; filename=\"")
              .Append(asciiFileName)
              .Append("\"; filename*=UTF-8''")
              .Append(EscapeRfc5987(fileName))
              .Append("\r\n");
        }

        private static string ToAsciiFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c : '_');
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? "download" : result;
        }

        /// <summary>RFC 5987 の ext-value 用にパーセントエンコードする。</summary>
        private static string EscapeRfc5987(string value)
        {
            return Uri.EscapeDataString(value)
                .Replace("'", "%27")
                .Replace("(", "%28")
                .Replace(")", "%29")
                .Replace("*", "%2A");
        }

        private static string EscapeHtml(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }
    }
}
