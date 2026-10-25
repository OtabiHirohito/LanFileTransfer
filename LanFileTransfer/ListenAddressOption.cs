using System.Net;

namespace LanFileTransfer
{
    /// <summary>待受アドレスの選択肢(表示名と、実際にバインドするアドレス)。</summary>
    public sealed class ListenAddressOption
    {
        public ListenAddressOption(string display, IPAddress address)
        {
            Display = display;
            Address = address;
        }

        public string Display { get; }

        public IPAddress Address { get; }

        public override string ToString() => Display;
    }
}
