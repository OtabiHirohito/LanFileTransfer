using System.IO;
using System.Text.Json;

namespace LanFileTransfer
{
    /// <summary>
    /// ウィンドウサイズや入力値など、アプリ終了時に保存して次回起動時に復元する設定。
    /// %LOCALAPPDATA%\LanFileTransfer\settings.json に保存する。
    /// </summary>
    public class AppSettings
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public double WindowWidth { get; set; } = 800;
        public double WindowHeight { get; set; } = 780;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public bool IsMaximized { get; set; }

        public string TargetIp { get; set; } = "192.168.1.2";
        public int SendPort { get; set; } = 5000;
        public int ReceivePort { get; set; } = 5000;
        public string ListenAddress { get; set; } = "0.0.0.0";
        public string SaveFolderPath { get; set; } = string.Empty;
        public bool ReceiveMode { get; set; }

        // ブラウザ共有モード
        public int SharePort { get; set; } = 5000;
        public string ShareAddress { get; set; } = string.Empty;
        public string ShareAllowedClients { get; set; } = string.Empty;
        public bool BrowserShareMode { get; set; }

        // ブラウザ共有のセキュリティ設定
        public int ShareLifetimeSeconds { get; set; } = 60;
        public bool ShareUseToken { get; set; } = true;
        public bool ShareUseHttps { get; set; } = true;

        private static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanFileTransfer");

        private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

        /// <summary>
        /// 保存済みの設定を読み込む。存在しない/壊れている場合は既定値を返す。
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // 読み込み失敗時は既定値で動作する
            }

            return new AppSettings();
        }

        /// <summary>
        /// 現在の設定をファイルに保存する。失敗してもアプリの動作は継続する。
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                string json = JsonSerializer.Serialize(this, SerializerOptions);
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // 保存失敗は無視する
            }
        }
    }
}
