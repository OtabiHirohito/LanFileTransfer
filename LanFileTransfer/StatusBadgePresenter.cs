using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows;
using Brush = System.Windows.Media.Brush;

namespace LanFileTransfer
{
    /// <summary>
    /// 「転送状況」カードの状態バッジ(色・点滅・文言)を一元管理する。
    /// 状態と文言の組が変わったときだけ XAML 要素を更新する。
    /// </summary>
    public sealed class StatusBadgePresenter
    {
        private static readonly TimeSpan PulsePeriod = TimeSpan.FromMilliseconds(650);

        private readonly Border _badge;
        private readonly Ellipse _dot;
        private readonly TextBlock _text;
        private readonly Func<string, Brush> _resourceLookup;

        private TransferState? _state;
        private string? _label;

        /// <param name="resourceLookup">リソースキーからブラシを引く関数(通常は FindResource)。</param>
        public StatusBadgePresenter(Border badge, Ellipse dot, TextBlock text, Func<string, Brush> resourceLookup)
        {
            _badge = badge;
            _dot = dot;
            _text = text;
            _resourceLookup = resourceLookup;
        }

        /// <summary>バッジの状態と文言を更新する。</summary>
        public void Show(TransferState state, string label)
        {
            if (_state == state && _label == label)
            {
                return;
            }

            _state = state;
            _label = label;

            (string background, string dot, string foreground) = Palette(state);
            _badge.Background = _resourceLookup(background);
            _dot.Fill = _resourceLookup(dot);
            _text.Foreground = _resourceLookup(foreground);
            _text.Text = label;

            UpdatePulse(state != TransferState.Idle);
        }

        private static (string Background, string Dot, string Foreground) Palette(TransferState state) => state switch
        {
            TransferState.Sending => ("StatusActiveBgBrush", "StatusActiveDotBrush", "StatusActiveTextBrush"),
            TransferState.Listening => ("StatusReceiveBgBrush", "StatusReceiveDotBrush", "StatusReceiveTextBrush"),
            TransferState.Receiving => ("StatusReceivingBgBrush", "StatusReceivingDotBrush", "StatusReceivingTextBrush"),
            _ => ("StatusIdleBgBrush", "StatusIdleDotBrush", "StatusIdleTextBrush")
        };

        /// <summary>動作中は点を点滅させて「稼働中」を分かりやすくする。</summary>
        private void UpdatePulse(bool active)
        {
            if (active)
            {
                var pulse = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.25,
                    Duration = PulsePeriod,
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                _dot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else
            {
                _dot.BeginAnimation(UIElement.OpacityProperty, null);
                _dot.Opacity = 1.0;
            }
        }
    }
}
