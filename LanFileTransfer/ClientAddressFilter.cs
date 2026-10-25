using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace LanFileTransfer
{
    /// <summary>
    /// ブラウザ共有で接続を許可する IPv4 アドレスの集合。
    /// 単一アドレス(例: 192.168.1.20)と CIDR(例: 192.168.1.0/24)をカンマ区切りで指定できる。
    /// 指定が空の場合はすべてのアドレスを許可する。
    /// </summary>
    public sealed class ClientAddressFilter
    {
        private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

        private readonly AddressRange[] _ranges;

        private ClientAddressFilter(AddressRange[] ranges)
        {
            _ranges = ranges;
        }

        /// <summary>制限なし(すべて許可)。</summary>
        public static ClientAddressFilter AllowAll { get; } = new(Array.Empty<AddressRange>());

        public bool IsAllowAll => _ranges.Length == 0;

        /// <summary>接続元アドレスが許可対象かどうか。</summary>
        public bool IsAllowed(IPAddress? address)
        {
            if (_ranges.Length == 0)
            {
                return true;
            }

            if (address is null)
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            uint value = ToUInt32(address.GetAddressBytes());
            foreach (AddressRange range in _ranges)
            {
                if (range.Contains(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>ログ表示用の文字列(正規化した指定内容)。</summary>
        public string Describe()
            => string.Join(", ", _ranges.Select(range => range.ToString()));

        /// <summary>
        /// 入力文字列を解析する。空文字なら <see cref="AllowAll"/>。
        /// 書式が不正な項目があれば false を返し、その項目を <paramref name="invalidEntry"/> に入れる。
        /// </summary>
        public static bool TryParse(string? text, out ClientAddressFilter filter, out string? invalidEntry)
        {
            invalidEntry = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                filter = AllowAll;
                return true;
            }

            var ranges = new List<AddressRange>();
            string[] entries = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Length == 0)
            {
                // 区切り文字だけが入力された場合
                invalidEntry = text.Trim();
                filter = AllowAll;
                return false;
            }

            foreach (string entry in entries)
            {
                if (!AddressRange.TryParse(entry, out AddressRange range))
                {
                    invalidEntry = entry;
                    filter = AllowAll;
                    return false;
                }

                ranges.Add(range);
            }

            filter = ranges.Count == 0 ? AllowAll : new ClientAddressFilter(ranges.ToArray());
            return true;
        }

        private static uint ToUInt32(byte[] bytes)
            => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        private static IPAddress ToAddress(uint value)
            => new(new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            });

        /// <summary>1 つのアドレス範囲(ネットワークアドレスとマスク)。</summary>
        private readonly struct AddressRange
        {
            private AddressRange(uint network, uint mask, int prefixLength)
            {
                Network = network;
                Mask = mask;
                PrefixLength = prefixLength;
            }

            private uint Network { get; }

            private uint Mask { get; }

            private int PrefixLength { get; }

            public bool Contains(uint value) => (value & Mask) == Network;

            public static bool TryParse(string entry, out AddressRange range)
            {
                range = default;

                string[] parts = entry.Split('/');
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out IPAddress? address))
                {
                    return false;
                }

                if (address.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }

                int prefixLength = 32;
                if (parts.Length == 2
                    && !(int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength)
                         && prefixLength is >= 0 and <= 32))
                {
                    return false;
                }

                uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
                uint network = ToUInt32(address.GetAddressBytes()) & mask;
                range = new AddressRange(network, mask, prefixLength);
                return true;
            }

            public override string ToString()
            {
                string address = ToAddress(Network).ToString();
                return PrefixLength == 32 ? address : $"{address}/{PrefixLength}";
            }
        }
    }
}
