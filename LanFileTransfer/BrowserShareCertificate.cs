using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanFileTransfer
{
    /// <summary>
    /// ブラウザ共有用の自己署名証明書を作る。
    ///
    /// ブラウザ共有を HTTPS で配信するために TLS のサーバー証明書が必要になる。
    /// LAN の IP アドレスに正式な証明書は発行できないので、共有のたびに
    /// 自己署名証明書を作る(ブラウザには証明書の警告が出る)。
    /// </summary>
    internal static class BrowserShareCertificate
    {
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        public static X509Certificate2 Create()
        {
            using RSA rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                "CN=LAN File Transfer",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid(ServerAuthOid) },
                    false));

            // 使用する IP アドレスを SAN に入れておく(名前不一致の警告を減らす)
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);

            string hostName = NetworkHelper.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName) && hostName != "(取得不可)")
            {
                san.AddDnsName(hostName);
            }

            foreach (IPAddress address in NetworkHelper.GetLocalIPv4Addresses())
            {
                san.AddIpAddress(address);
            }

            request.CertificateExtensions.Add(san.Build());

            X509Certificate2 ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(1));

            try
            {
                // Windows の SChannel は「一時キー」の証明書を受け付けないため、
                // PFX に書き出して読み込み直し、使用中だけ有効なキーコンテナに載せる。
                // (PersistKeySet を付けないので、証明書を破棄するとキーも消える)
                byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
                return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                ephemeral.Dispose();
            }
        }
    }
}
