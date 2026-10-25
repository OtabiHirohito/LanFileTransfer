using System.Net;
using System.Windows;
using ComboBox = System.Windows.Controls.ComboBox;

namespace LanFileTransfer
{
    /// <summary>設定の復元・保存と、待受アドレスの選択。</summary>
    public partial class MainWindow
    {
        // ===== 設定の復元・保存 =====

        private void ApplySettings()
        {
            ApplyWindowSettings();
            ApplyInputSettings();

            RefreshListenAddressOptions();
            RefreshShareAddressOptions();
            UpdateShareSecurityNote();
            SetStatus(TransferState.Idle, "待機中");
        }

        private void ApplyWindowSettings()
        {
            if (_settings.WindowWidth >= MinWidth)
            {
                Width = _settings.WindowWidth;
            }
            if (_settings.WindowHeight >= MinHeight)
            {
                Height = _settings.WindowHeight;
            }

            if (_settings.WindowLeft is double left && _settings.WindowTop is double top
                && IsOnScreen(left, top, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (_settings.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void ApplyInputSettings()
        {
            if (!string.IsNullOrWhiteSpace(_settings.TargetIp))
            {
                TargetIpTextBox.Text = _settings.TargetIp;
            }
            if (_settings.SendPort > 0)
            {
                SendPortTextBox.Text = _settings.SendPort.ToString();
            }
            if (_settings.ReceivePort > 0)
            {
                ReceivePortTextBox.Text = _settings.ReceivePort.ToString();
            }
            if (!string.IsNullOrWhiteSpace(_settings.SaveFolderPath))
            {
                _saveFolderPath = _settings.SaveFolderPath;
                SaveFolderText.Text = _saveFolderPath;
            }
            if (_settings.SharePort > 0)
            {
                SharePortTextBox.Text = _settings.SharePort.ToString();
            }
            if (_settings.ShareLifetimeSeconds >= MinShareLifetimeSeconds
                && _settings.ShareLifetimeSeconds <= MaxShareLifetimeSeconds)
            {
                ShareLifetimeTextBox.Text = _settings.ShareLifetimeSeconds.ToString();
            }

            ShareAllowedClientsTextBox.Text = _settings.ShareAllowedClients ?? string.Empty;

            UseShareTokenCheckBox.IsChecked = _settings.ShareUseToken;
            UseShareHttpsCheckBox.IsChecked = _settings.ShareUseHttps;

            if (_settings.BrowserShareMode)
            {
                BrowserModeRadio.IsChecked = true;
            }
            else if (_settings.ReceiveMode)
            {
                ReceiveModeRadio.IsChecked = true;
            }
        }

        private void SaveSettings()
        {
            SaveWindowSettings();
            SaveInputSettings();
            _settings.Save();
        }

        private void SaveWindowSettings()
        {
            // 最大化中は通常時のサイズ・位置を上書きしない
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
            }
            _settings.IsMaximized = WindowState == WindowState.Maximized;
        }

        private void SaveInputSettings()
        {
            _settings.TargetIp = TargetIpTextBox.Text.Trim();
            if (int.TryParse(SendPortTextBox.Text, out int sendPort))
            {
                _settings.SendPort = sendPort;
            }
            if (int.TryParse(ReceivePortTextBox.Text, out int receivePort))
            {
                _settings.ReceivePort = receivePort;
            }
            if (int.TryParse(SharePortTextBox.Text, out int sharePort))
            {
                _settings.SharePort = sharePort;
            }
            if (int.TryParse(ShareLifetimeTextBox.Text, out int shareLifetime)
                && shareLifetime >= MinShareLifetimeSeconds
                && shareLifetime <= MaxShareLifetimeSeconds)
            {
                _settings.ShareLifetimeSeconds = shareLifetime;
            }

            _settings.ShareUseToken = UseShareTokenCheckBox.IsChecked == true;
            _settings.ShareUseHttps = UseShareHttpsCheckBox.IsChecked == true;
            _settings.ShareAllowedClients = ShareAllowedClientsTextBox.Text.Trim();

            _settings.SaveFolderPath = _saveFolderPath ?? string.Empty;
            _settings.ListenAddress = GetSelectedListenAddress().ToString();
            _settings.ShareAddress = GetSelectedShareAddress().ToString();
            _settings.ReceiveMode = ReceiveModeRadio.IsChecked == true;
            _settings.BrowserShareMode = BrowserModeRadio.IsChecked == true;
        }

        /// <summary>保存された位置が現在の画面内(仮想スクリーンと重なる)かどうか。</summary>
        private static bool IsOnScreen(double left, double top, double width, double height)
        {
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vWidth = SystemParameters.VirtualScreenWidth;
            double vHeight = SystemParameters.VirtualScreenHeight;

            return left + width > vLeft && left < vLeft + vWidth
                && top + height > vTop && top < vTop + vHeight;
        }

        // ===== 待受アドレス =====

        private void RefreshListenAddressOptions()
        {
            string? wanted = GetSelectedAddress(ListenAddressComboBox) ?? _settings.ListenAddress;

            ListenAddressComboBox.Items.Clear();
            AddAnyAddressOption(ListenAddressComboBox);

            foreach (IPAddress ip in NetworkHelper.GetLocalIPv4Addresses())
            {
                ListenAddressComboBox.Items.Add(new ListenAddressOption(ip.ToString(), ip));
            }

            TrySelectAddress(ListenAddressComboBox, wanted);
            EnsureAddressSelected(ListenAddressComboBox);
        }

        private IPAddress GetSelectedListenAddress() => GetSelectedAddressOrAny(ListenAddressComboBox);

        // ===== 共有アドレス =====

        private void RefreshShareAddressOptions()
        {
            string? wanted = GetSelectedAddress(ShareAddressComboBox) ?? _settings.ShareAddress;

            ShareAddressComboBox.Items.Clear();
            AddAnyAddressOption(ShareAddressComboBox);

            foreach (LocalAddressInfo info in NetworkHelper.GetLocalIPv4AddressInfos()
                         .OrderBy(i => BrowserShareServer.RankAddress(i.Address)))
            {
                ShareAddressComboBox.Items.Add(
                    new ListenAddressOption($"{info.InterfaceName}: {info.Address}", info.Address));
            }

            if (!TrySelectAddress(ShareAddressComboBox, wanted))
            {
                // 保存されたアドレスが無い/見つからない場合は、LAN側の先頭を既定にする
                ShareAddressComboBox.SelectedIndex = ShareAddressComboBox.Items.Count > 1 ? 1 : 0;
            }

            EnsureAddressSelected(ShareAddressComboBox);
        }

        private IPAddress GetSelectedShareAddress() => GetSelectedAddressOrAny(ShareAddressComboBox);

        // ===== アドレス選択の共通処理 =====

        private static string? GetSelectedAddress(ComboBox combo)
            => (combo.SelectedItem as ListenAddressOption)?.Address.ToString();

        private static bool TrySelectAddress(ComboBox combo, string? address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return false;
            }

            foreach (object item in combo.Items)
            {
                if (item is ListenAddressOption option && option.Address.ToString() == address)
                {
                    combo.SelectedItem = item;
                    return true;
                }
            }

            return false;
        }

        private static IPAddress GetSelectedAddressOrAny(ComboBox combo)
            => (combo.SelectedItem as ListenAddressOption)?.Address ?? IPAddress.Any;

        private static void AddAnyAddressOption(ComboBox combo)
            => combo.Items.Add(new ListenAddressOption("すべてのアドレス (0.0.0.0)", IPAddress.Any));

        private static void EnsureAddressSelected(ComboBox combo)
        {
            if (combo.SelectedIndex < 0)
            {
                combo.SelectedIndex = 0;
            }
        }
    }
}
