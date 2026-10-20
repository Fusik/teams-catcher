using System;
using System.Windows;
using System.Windows.Media;

namespace TeamsCatcher.Controls
{
    /// <summary>
    /// Ikona UI z Assets/WF/WF.Icons.xaml (siatka 24 px): kontur WF.Icon.&lt;Kind&gt; + szczegol WF.Icon.&lt;Kind&gt;.Detail (cyjan).
    /// Uzycie: &lt;controls:WfIcon Kind="generuj" Width="18" Height="18"/&gt;
    /// </summary>
    public class WfIcon : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(WfIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(WfIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DetailStrokeProperty = DependencyProperty.Register(
            nameof(DetailStroke), typeof(Brush), typeof(WfIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        static WfIcon()
        {
            WidthProperty.OverrideMetadata(typeof(WfIcon), new FrameworkPropertyMetadata(18.0));
            HeightProperty.OverrideMetadata(typeof(WfIcon), new FrameworkPropertyMetadata(18.0));
        }

        public string Kind
        {
            get => (string)GetValue(KindProperty);
            set => SetValue(KindProperty, value);
        }

        /// <summary>Kolor konturu (domyslnie WF.Brush.Blue300).</summary>
        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        /// <summary>Kolor szczegolu (domyslnie WF.Brush.Cyan).</summary>
        public Brush DetailStroke
        {
            get => (Brush)GetValue(DetailStrokeProperty);
            set => SetValue(DetailStrokeProperty, value);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (string.IsNullOrEmpty(Kind))
            {
                return;
            }

            var main = TryFindResource("WF.Icon." + Kind) as Geometry;
            var detail = TryFindResource("WF.Icon." + Kind + ".Detail") as Geometry;
            var strokeBrush = Stroke ?? TryFindResource("WF.Brush.Blue300") as Brush ?? Brushes.SteelBlue;
            var detailBrush = DetailStroke ?? TryFindResource("WF.Brush.Cyan") as Brush ?? Brushes.Cyan;

            var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
            dc.PushTransform(new ScaleTransform(scale, scale));
            if (main != null)
            {
                dc.DrawGeometry(null, CreatePen(strokeBrush), main);
            }

            if (detail != null)
            {
                dc.DrawGeometry(null, CreatePen(detailBrush), detail);
            }

            dc.Pop();
        }

        private static Pen CreatePen(Brush brush) => new Pen(brush, 1.6)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square,
            LineJoin = PenLineJoin.Miter,
        };
    }
}
