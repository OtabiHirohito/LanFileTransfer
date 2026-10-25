using System.Net;
using System.Net.NetworkInformation;

namespace LanFileTransfer
{
    /// <summary>
    /// 自機のIPv4アドレス1つ分の情報(どのインターフェースのアドレスか)。
    /// ブラウザ共有で「Wi-Fi経由かどうか」を判断するために使う。
    /// </summary>
    public sealed class LocalAddressInfo
    {
        public LocalAddressInfo(
            string interfaceName,
            string description,
            IPAddress address,
            NetworkInterfaceType interfaceType,
            int interfaceIndex)
        {
            InterfaceName = interfaceName;
            Description = description;
            Address = address;
            InterfaceType = interfaceType;
            InterfaceIndex = interfaceIndex;
        }

        /// <summary>インターフェース名(例: Wi-Fi / イーサネット)。</summary>
        public string InterfaceName { get; }

        /// <summary>アダプターの説明。</summary>
        public string Description { get; }

        public IPAddress Address { get; }

        public NetworkInterfaceType InterfaceType { get; }

        /// <summary>Windows のインターフェースインデックス(ネットワーク種別の照合に使う)。</summary>
        public int InterfaceIndex { get; }

        /// <summary>Wi-Fi(無線LAN)のインターフェースかどうか。</summary>
        public bool IsWireless =>
            InterfaceType == NetworkInterfaceType.Wireless80211 ||
            InterfaceName.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
            InterfaceName.Contains("wifi", StringComparison.OrdinalIgnoreCase) ||
            InterfaceName.Contains("無線", StringComparison.Ordinal);

        public override string ToString() => $"{InterfaceName}: {Address}";
    }
}
