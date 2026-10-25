using System.Management;

namespace LanFileTransfer
{
    /// <summary>Windows のネットワーク種別(パブリック / プライベート / ドメイン)。</summary>
    public enum NetworkProfileCategory
    {
        Unknown = -1,
        Public = 0,
        Private = 1,
        DomainAuthenticated = 2
    }

    /// <summary>ネットワークプロファイル1件分の情報。</summary>
    public sealed class NetworkProfileInfo
    {
        public NetworkProfileInfo(NetworkProfileCategory category, string name)
        {
            Category = category;
            Name = name;
        }

        public NetworkProfileCategory Category { get; }

        /// <summary>プロファイル名(例: ネットワーク 3)。</summary>
        public string Name { get; }

        public bool IsPublic => Category == NetworkProfileCategory.Public;
    }

    /// <summary>
    /// インターフェースインデックスからネットワーク種別を取得する。
    /// WMI (root\StandardCimv2 の MSFT_NetConnectionProfile) を参照する。
    /// 取得できない環境では空の辞書を返し、呼び出し側は警告を出さない。
    /// </summary>
    public static class NetworkProfileHelper
    {
        public static Dictionary<int, NetworkProfileInfo> GetProfiles()
        {
            var result = new Dictionary<int, NetworkProfileInfo>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\StandardCimv2",
                    "SELECT InterfaceIndex, NetworkCategory, Name FROM MSFT_NetConnectionProfile");

                using ManagementObjectCollection profiles = searcher.Get();
                foreach (ManagementObject profile in profiles)
                {
                    using (profile)
                    {
                        int index = Convert.ToInt32(profile["InterfaceIndex"]);
                        int category = Convert.ToInt32(profile["NetworkCategory"]);
                        string name = Convert.ToString(profile["Name"]) ?? string.Empty;

                        result[index] = new NetworkProfileInfo((NetworkProfileCategory)category, name);
                    }
                }
            }
            catch
            {
                // WMI が使えない場合は判定しない(警告も出さない)
            }

            return result;
        }
    }
}
