using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanFileTransfer
{
    /// <summary>
    /// 自機のホスト名・IPv4アドレス一覧を取得するためのヘルパー。
    /// </summary>
    public static class NetworkHelper
    {
        public static string GetHostName()
        {
            try
            {
                return Dns.GetHostName();
            }
            catch
            {
                return "(取得不可)";
            }
        }

        /// <summary>
        /// 有効なネットワークインターフェースが持つIPv4アドレス一覧を
        /// "インターフェース名: IPアドレス" の形式で返す。
        /// </summary>
        public static List<string> GetLocalIPv4List()
        {
            var result = GetActiveUnicastAddresses()
                .Select(entry => $"{entry.Interface.Name}: {entry.Address}")
                .ToList();

            if (result.Count == 0)
            {
                result.Add("(有効なIPv4アドレスが見つかりません)");
            }

            return result;
        }

        /// <summary>
        /// 待受アドレス選択用に、有効なネットワークインターフェースが持つ
        /// IPv4アドレスを重複なく返す。
        /// </summary>
        public static List<IPAddress> GetLocalIPv4Addresses()
        {
            var result = new List<IPAddress>();

            foreach (var entry in GetActiveUnicastAddresses())
            {
                if (!result.Contains(entry.Address))
                {
                    result.Add(entry.Address);
                }
            }

            return result;
        }

        /// <summary>
        /// IPv4アドレスごとに、インターフェース名・種類(有線/無線)・インデックスを返す。
        /// ブラウザ共有の警告(Wi-Fiかどうか)に使う。
        /// </summary>
        public static List<LocalAddressInfo> GetLocalIPv4AddressInfos()
        {
            var result = new List<LocalAddressInfo>();

            foreach (var entry in GetActiveUnicastAddresses())
            {
                if (result.Any(info => info.Address.Equals(entry.Address)))
                {
                    continue;
                }

                int index = 0;
                try
                {
                    index = entry.Interface.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                }
                catch
                {
                    // IPv4が無効なインターフェースでは取得できない
                }

                result.Add(new LocalAddressInfo(
                    entry.Interface.Name,
                    entry.Interface.Description,
                    entry.Address,
                    entry.Interface.NetworkInterfaceType,
                    index));
            }

            return result;
        }

        /// <summary>
        /// 有効(Up)かつループバック以外のインターフェースが持つ IPv4 アドレスを列挙する。
        /// 取得に失敗した場合は空を返す。
        /// </summary>
        private static IEnumerable<(NetworkInterface Interface, IPAddress Address)> GetActiveUnicastAddresses()
        {
            var result = new List<(NetworkInterface, IPAddress)>();

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                                 && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var ni in interfaces)
                {
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.Add((ni, addr.Address));
                        }
                    }
                }
            }
            catch
            {
                // 取得失敗時は空リストのまま返す
            }

            return result;
        }
    }
}
