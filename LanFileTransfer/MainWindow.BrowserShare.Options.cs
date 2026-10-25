using System.Net;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace LanFileTransfer
{
    /// <summary>ブラウザ共有の開始前確認と、セキュリティ欄の表示。</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Wi-Fi・「パブリック」ネットワーク・セキュリティ設定の注意点を確認する。
        /// 注意点が無ければ true を返す。
        /// </summary>
        private bool ConfirmShareWarnings(
            IPAddress listenAddress, int lifetimeSeconds, bool useToken, bool useHttps, bool restrictClients)
        {
            List<LocalAddressInfo> allInfos = NetworkHelper.GetLocalIPv4AddressInfos();
            List<LocalAddressInfo> targets = listenAddress.Equals(IPAddress.Any)
                ? allInfos
                : allInfos.Where(info => info.Address.Equals(listenAddress)).ToList();

            var warnings = new List<string>();
            if (targets.Count > 0)
            {
                Dictionary<int, NetworkProfileInfo> profiles = NetworkProfileHelper.GetProfiles();
                warnings.AddRange(ShareWarnings.Build(targets, profiles));
            }

            if (!useToken)
            {
                warnings.Add(restrictClients
                    ? "トークン付きURLを使わないため、許可した IP アドレス内であれば誰でもダウンロードできます。"
                    : "トークン付きURLを使わないため、アドレスを知っている人は誰でもダウンロードできます。");
            }

            if (!useHttps)
            {
                warnings.Add("HTTPS を使わないため、通信は平文(暗号化なし)になります。");
            }

            if (warnings.Count == 0)
            {
                return true;
            }

            AppendLog("共有開始の確認:");
            foreach (string warning in warnings)
            {
                AppendLog($"  {warning}");
            }

            string message = "次の設定・ネットワークで共有しようとしています。\n\n" +
                             string.Join("\n", warnings) +
                             $"\n\nこのまま共有を開始しますか?（{FormatDuration(lifetimeSeconds)}で自動停止します）";

            return MessageBox.Show(
                message, "共有の確認", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        /// <summary>自動停止までの時間を検証して秒数を返す。</summary>
        private bool TryGetShareLifetime(out int seconds)
        {
            if (int.TryParse(ShareLifetimeTextBox.Text, out seconds)
                && seconds >= MinShareLifetimeSeconds
                && seconds <= MaxShareLifetimeSeconds)
            {
                return true;
            }

            MessageBox.Show(
                $"自動停止までの時間は {MinShareLifetimeSeconds}〜{MaxShareLifetimeSeconds} 秒の範囲で入力してください。",
                "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void ShareSecurityOption_Changed(object sender, RoutedEventArgs e)
        {
            UpdateShareSecurityNote();
        }

        /// <summary>セキュリティ欄の説明文を、現在のチェック状態に合わせる。</summary>
        private void UpdateShareSecurityNote()
        {
            if (ShareSecurityNoteText is null)
            {
                return; // XAML 初期化中はまだ存在しない
            }

            bool useToken = UseShareTokenCheckBox.IsChecked == true;
            bool useHttps = UseShareHttpsCheckBox.IsChecked == true;

            var lines = new List<string>
            {
                useToken
                    ? "共有URLを知っている人だけがダウンロードできます。"
                    : "トークンなし: アドレスを知っている人は誰でもダウンロードできます(接続元IPの制限で絞り込めます)。",
                useHttps
                    ? "HTTPS で暗号化し、改ざんも検出します(ブラウザに証明書の警告が出ます)。"
                    : "HTTPS OFF: 通信は平文です。盗聴・改ざんの対策はありません。"
            };

            ShareSecurityNoteText.Text = string.Join(Environment.NewLine, lines);
        }

        /// <summary>共有URLの下に表示する注意書き。</summary>
        private static string BuildShareUrlHint(bool useToken, bool useHttps, bool restrictClients)
        {
            string security = useHttps
                ? "通信は HTTPS で暗号化されます。ブラウザに証明書の警告が出るので「詳細設定」から続行してください。"
                : "通信は暗号化されません(平文)。信頼できるLAN内でのみ使ってください。";

            if (useToken)
            {
                return security;
            }

            return security + (restrictClients
                ? " トークンなしのため、許可した IP アドレス内であれば誰でもダウンロードできます。"
                : " トークンなしのため、アドレスを知っている人は誰でもダウンロードできます。");
        }

        /// <summary>秒数を読みやすい長さにする(例: 90 →「1 分 30 秒」)。</summary>
        private static string FormatDuration(int totalSeconds)
        {
            if (totalSeconds < 60)
            {
                return $"{totalSeconds} 秒";
            }

            if (totalSeconds < 3600)
            {
                int minutes = totalSeconds / 60;
                int seconds = totalSeconds % 60;
                return seconds == 0 ? $"{minutes} 分" : $"{minutes} 分 {seconds} 秒";
            }

            int hours = totalSeconds / 3600;
            int remainingMinutes = totalSeconds % 3600 / 60;
            return remainingMinutes == 0 ? $"{hours} 時間" : $"{hours} 時間 {remainingMinutes} 分";
        }

        private void CopyShareUrlButton_Click(object sender, RoutedEventArgs e)
        {
            string text = ShareUrlTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // 複数アドレスが並ぶ場合は先頭(使用するアドレス)を使う
            string url = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .FirstOrDefault()?.Trim() ?? string.Empty;
            if (url.Length == 0)
            {
                return;
            }

            try
            {
                System.Windows.Clipboard.SetText(url);
                AppendLog($"共有URLをコピーしました: {url}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"コピーできませんでした。\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
