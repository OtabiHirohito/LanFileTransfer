using System.Windows.Threading;

namespace LanFileTransfer
{
    /// <summary>
    /// 進捗通知を「報告した順序どおり」に UI へ反映する <see cref="IProgress{T}"/>。
    ///
    /// <see cref="Progress{T}"/> はコールバックを非同期で投稿するため、転送完了の処理より
    /// 後に古い進捗が反映されて状態表示が巻き戻ることがある。それを防ぐ。
    /// </summary>
    public sealed class DispatcherProgress<T> : IProgress<T>
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<T> _handler;

        public DispatcherProgress(Dispatcher dispatcher, Action<T> handler)
        {
            _dispatcher = dispatcher;
            _handler = handler;
        }

        public void Report(T value)
        {
            if (_dispatcher.CheckAccess())
            {
                _handler(value);
            }
            else
            {
                _dispatcher.Invoke(() => _handler(value));
            }
        }
    }
}
