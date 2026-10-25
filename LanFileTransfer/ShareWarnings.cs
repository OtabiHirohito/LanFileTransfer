namespace LanFileTransfer
{
    /// <summary>
    /// ブラウザ共有を開始する前の注意喚起メッセージを作る。
    /// ・Wi-Fi 経由で共有しようとしている
    /// ・ネットワークが「パブリック」に設定されている
    /// の2点を検出する。
    /// </summary>
    public static class ShareWarnings
    {
        public static List<string> Build(
            IReadOnlyList<LocalAddressInfo> targets,
            IReadOnlyDictionary<int, NetworkProfileInfo> profiles)
        {
            var warnings = new List<string>();

            var wirelessNames = targets
                .Where(info => info.IsWireless)
                .Select(info => info.InterfaceName)
                .Distinct()
                .ToList();

            if (wirelessNames.Count > 0)
            {
                bool allNamedWifi = wirelessNames.All(name =>
                    name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("wifi", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("無線", StringComparison.Ordinal));

                string label = allNamedWifi
                    ? "Wi-Fi"
                    : $"Wi-Fi（{string.Join("、", wirelessNames)}）";

                warnings.Add(
                    $"・{label} で共有しようとしています。" +
                    "同じWi-Fiに接続している他の端末からダウンロードできる可能性があります。");
            }

            var publicNames = targets
                .Where(info => profiles.TryGetValue(info.InterfaceIndex, out NetworkProfileInfo? profile)
                               && profile.IsPublic)
                .Select(info => profiles[info.InterfaceIndex].Name)
                .Where(name => name.Length > 0)
                .Distinct()
                .ToList();

            if (publicNames.Count > 0)
            {
                warnings.Add(
                    $"・ネットワーク「{string.Join("、", publicNames)}」が「パブリック」に設定されています。" +
                    "公共の場では他の利用者からアクセスされるおそれがあります。");
            }

            return warnings;
        }
    }
}
