using System.IO;
using System.IO.Compression;
using System.Text;

namespace LanFileTransfer
{
    public sealed partial class BrowserShareServer
    {
        private async Task ServeFileAsync(
            Stream stream,
            int index,
            IReadOnlyDictionary<string, string> headers,
            string remote,
            bool headOnly)
        {
            ShareEntry entry = _files[index];

            FileStream file;
            try
            {
                file = new FileStream(
                    entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    TransferProtocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await WriteResponseAsync(
                    stream, "404 Not Found", "text/html; charset=utf-8",
                    BuildMessagePage("ファイルを開けません", "送信側でファイルが移動または削除された可能性があります。"),
                    headOnly).ConfigureAwait(false);
                return;
            }

            using (file)
            {
                long length = file.Length;
                long start = 0;
                long end = length - 1;
                bool partial = false;

                if (headers.TryGetValue("Range", out string? rangeHeader) && !string.IsNullOrWhiteSpace(rangeHeader))
                {
                    if (TryParseRange(rangeHeader, length, out long rangeStart, out long rangeEnd, out bool unsatisfiable))
                    {
                        if (unsatisfiable)
                        {
                            await WriteRangeNotSatisfiableAsync(stream, length).ConfigureAwait(false);
                            return;
                        }

                        start = rangeStart;
                        end = rangeEnd;
                        partial = true;
                    }
                }

                if (length == 0)
                {
                    start = 0;
                    end = -1;
                }

                await WriteFileHeadersAsync(stream, entry, length, start, end, partial).ConfigureAwait(false);

                if (headOnly || end < start)
                {
                    return;
                }

                long remaining = end - start + 1;
                long rangeLength = remaining;
                long sent = 0;
                DateTime lastReport = DateTime.UtcNow;

                file.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[TransferProtocol.BufferSize];

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await file.ReadAsync(buffer.AsMemory(0, toRead), _lifetimeCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), _lifetimeCts.Token).ConfigureAwait(false);
                    remaining -= read;
                    sent += read;

                    if (DateTime.UtcNow - lastReport >= ProgressInterval)
                    {
                        lastReport = DateTime.UtcNow;
                        ReportProgress(entry.FileName, rangeLength, sent, remote);
                    }
                }

                ReportProgress(entry.FileName, rangeLength, sent, remote);

                if (sent >= rangeLength)
                {
                    _onDownloadCompleted(entry.FileName);
                }
            }
        }

        /// <summary>進捗を UI 側へ通知する。</summary>
        private void ReportProgress(string fileName, long totalBytes, long sentBytes, string remote)
        {
            _onProgress(new BrowserShareProgress
            {
                FileName = fileName,
                TotalBytes = totalBytes,
                SentBytes = sentBytes,
                ClientAddress = remote
            });
        }

        private async Task ServeZipAsync(
            Stream stream,
            IReadOnlyList<int> indexes,
            string remote,
            bool headOnly)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/zip\r\n");
            AppendContentDisposition(sb, "ファイル.zip", "files.zip");
            AppendNoStoreHeaders(sb);
            sb.Append("Connection: close\r\n");

            if (headOnly)
            {
                sb.Append("Content-Length: 0\r\n\r\n");
            }
            else
            {
                sb.Append("Transfer-Encoding: chunked\r\n\r\n");
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()).AsMemory()).ConfigureAwait(false);

            if (headOnly)
            {
                return;
            }

            var chunked = new ChunkedWriteStream(stream);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var zip = new ZipArchive(chunked, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (int index in indexes)
                {
                    ShareEntry entry = _files[index];

                    FileStream file;
                    try
                    {
                        file = new FileStream(
                            entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            TransferProtocol.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 移動・削除されたファイルは飛ばして、残りをまとめる
                        continue;
                    }

                    using (file)
                    {
                        string entryName = MakeUniqueEntryName(entry.FileName, usedNames);
                        ZipArchiveEntry zipEntry = zip.CreateEntry(entryName, CompressionLevel.Fastest);

                        long total = file.Length;
                        long sent = 0;
                        DateTime lastReport = DateTime.UtcNow;

                        await using (Stream target = zipEntry.Open())
                        {
                            byte[] buffer = new byte[TransferProtocol.BufferSize];

                            while (true)
                            {
                                int read = await file.ReadAsync(buffer.AsMemory(), _lifetimeCts.Token).ConfigureAwait(false);
                                if (read <= 0)
                                {
                                    break;
                                }

                                await target.WriteAsync(buffer.AsMemory(0, read), _lifetimeCts.Token).ConfigureAwait(false);
                                sent += read;

                                if (DateTime.UtcNow - lastReport >= ProgressInterval)
                                {
                                    lastReport = DateTime.UtcNow;
                                    ReportProgress(entryName, total, sent, remote);
                                }
                            }
                        }

                        ReportProgress(entryName, total, sent, remote);

                        _onDownloadCompleted(entryName);
                    }
                }
            }

            await chunked.FinishAsync().ConfigureAwait(false);
        }

        private static string MakeUniqueEntryName(string fileName, HashSet<string> used)
        {
            string name = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "file";
            }

            if (used.Add(name))
            {
                return name;
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);

            for (int i = 2; ; i++)
            {
                string candidate = $"{stem} ({i}){extension}";
                if (used.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        private async Task WriteFileHeadersAsync(
            Stream stream, ShareEntry entry, long length, long start, long end, bool partial)
        {
            long contentLength = end >= start ? end - start + 1 : 0;

            var sb = new StringBuilder();
            sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/octet-stream\r\n");
            sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            AppendContentDisposition(sb, entry.FileName, ToAsciiFileName(entry.FileName));

            if (partial)
            {
                sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end)
                  .Append('/').Append(length).Append("\r\n");
            }

            AppendNoStoreHeaders(sb);
            sb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()).AsMemory()).ConfigureAwait(false);
        }

        private static bool TryParseRange(
            string value, long length, out long start, out long end, out bool unsatisfiable)
        {
            start = 0;
            end = length - 1;
            unsatisfiable = false;

            const string prefix = "bytes=";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string spec = value[prefix.Length..].Trim();
            if (spec.Contains(','))
            {
                // 複数範囲は非対応(全体を返す)
                return false;
            }

            int dash = spec.IndexOf('-');
            if (dash < 0)
            {
                return false;
            }

            string startText = spec[..dash].Trim();
            string endText = spec[(dash + 1)..].Trim();

            if (startText.Length == 0)
            {
                // bytes=-N (末尾 N バイト)
                if (!long.TryParse(endText, out long suffix) || suffix <= 0)
                {
                    return false;
                }

                if (length == 0)
                {
                    unsatisfiable = true;
                    return true;
                }

                start = Math.Max(0, length - suffix);
                end = length - 1;
                return true;
            }

            if (!long.TryParse(startText, out start) || start < 0)
            {
                return false;
            }

            if (start >= length)
            {
                unsatisfiable = true;
                return true;
            }

            if (endText.Length > 0)
            {
                if (!long.TryParse(endText, out end) || end < start)
                {
                    return false;
                }

                end = Math.Min(end, length - 1);
            }
            else
            {
                end = length - 1;
            }

            return true;
        }

        /// <summary>
        /// 書き込まれた内容を HTTP/1.1 のチャンク形式に変換して送るストリーム。
        /// <see cref="ZipArchive"/> のようなサイズ不明の書き込みをそのまま流せる。
        /// </summary>
        private sealed class ChunkedWriteStream : Stream
        {
            private static readonly byte[] CrLf = "\r\n"u8.ToArray();
            private static readonly byte[] LastChunk = "0\r\n\r\n"u8.ToArray();

            private readonly Stream _inner;
            private bool _finished;

            public ChunkedWriteStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
                => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (buffer.IsEmpty)
                {
                    return;
                }

                WriteChunkHeader(buffer.Length);
                _inner.Write(buffer);
                _inner.Write(CrLf);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                {
                    return;
                }

                await _inner.WriteAsync(Encoding.ASCII.GetBytes(buffer.Length.ToString("X") + "\r\n").AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await _inner.WriteAsync(CrLf.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            /// <summary>終端チャンクを送って本文を閉じる。</summary>
            public async Task FinishAsync()
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                await _inner.WriteAsync(LastChunk.AsMemory()).ConfigureAwait(false);
                await _inner.FlushAsync().ConfigureAwait(false);
            }

            private void WriteChunkHeader(int length)
            {
                byte[] header = Encoding.ASCII.GetBytes(length.ToString("X") + "\r\n");
                _inner.Write(header);
            }

            protected override void Dispose(bool disposing)
            {
                // 内側のストリームは呼び出し側が管理する
                base.Dispose(disposing);
            }
        }
    }
}
