using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GameTranslate.Controls;

/// <summary>
/// Renders crisp cinema/TV-style yellow subtitles with a smooth round black outline
/// and optional soft drop shadow, fully DPI-aware with automatic word wrapping and
/// tight bounding-box measurement so text never clips at any DPI or paragraph length.
/// </summary>
public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x00)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(4.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(new FontFamily("Arial"), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontWeights.Bold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
        nameof(FontStyle), typeof(FontStyle), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(FontStyles.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(TextAlignment.Center, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DropShadowEnabledProperty = DependencyProperty.Register(
        nameof(DropShadowEnabled), typeof(bool), typeof(OutlinedTextBlock),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private FormattedText? _formattedText;
    private Geometry? _textGeometry;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public bool DropShadowEnabled
    {
        get => (bool)GetValue(DropShadowEnabledProperty);
        set => SetValue(DropShadowEnabledProperty, value);
    }

    private double ComputePadding() => Math.Ceiling(StrokeThickness) + 6.0;

    /// <summary>
    /// Measures the exact wrapped dimensions (in DIPs) for a given text, max width, and font size.
    /// Used by SubtitleOverlayWindow to auto-fit long RPG paragraphs without ever clipping text.
    /// </summary>
    public Size MeasureTextForFontSize(string text, double maxAvailableWidth, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return new Size(0, 0);

        double pad = ComputePadding();
        double safeMaxWidth = double.IsInfinity(maxAvailableWidth) || double.IsNaN(maxAvailableWidth)
            ? 0
            : Math.Max(40, maxAvailableWidth - pad * 2);

        FormattedText ft = BuildFormattedText(text, fontSize, safeMaxWidth);
        double actualW = safeMaxWidth > 0 ? Math.Min(safeMaxWidth, ft.WidthIncludingTrailingWhitespace) : ft.WidthIncludingTrailingWhitespace;
        return new Size(Math.Ceiling(actualW + pad * 2), Math.Ceiling(ft.Height + pad * 2));
    }

    private FormattedText BuildFormattedText(string text, double fontSize, double maxTextWidth)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal);
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(8, fontSize),
            Brushes.Black,
            dpi)
        {
            TextAlignment = TextAlignment,
            Trimming = TextTrimming.None,
            LineHeight = Math.Max(10, fontSize * 1.18)
        };

        if (maxTextWidth > 0)
            ft.MaxTextWidth = maxTextWidth;

        return ft;
    }

    private void EnsureFormattedText(double maxWidth)
    {
        string text = Text ?? string.Empty;
        double pad = ComputePadding();
        double safeMaxWidth = double.IsInfinity(maxWidth) || double.IsNaN(maxWidth)
            ? 0
            : Math.Max(40, maxWidth - pad * 2);

        _formattedText = BuildFormattedText(text, FontSize, safeMaxWidth);

        // When centered within a tight box, align MaxTextWidth to the actual measured text width
        // so single/multi-line subtitles center cleanly inside BackdropBorder with zero edge clipping.
        if (safeMaxWidth > 0 && TextAlignment == TextAlignment.Center)
        {
            double tightWidth = Math.Min(safeMaxWidth, Math.Ceiling(_formattedText.WidthIncludingTrailingWhitespace) + 2);
            if (tightWidth > 20 && tightWidth < safeMaxWidth)
            {
                _formattedText.MaxTextWidth = tightWidth;
            }
        }

        _textGeometry = _formattedText.BuildGeometry(new Point(pad, pad));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (string.IsNullOrEmpty(Text))
            return new Size(0, 0);

        EnsureFormattedText(availableSize.Width);
        if (_formattedText == null)
            return new Size(0, 0);

        double pad = ComputePadding();
        double desiredW = Math.Ceiling(_formattedText.WidthIncludingTrailingWhitespace + pad * 2);
        double desiredH = Math.Ceiling(_formattedText.Height + pad * 2);

        double finalW = double.IsInfinity(availableSize.Width) ? desiredW : Math.Min(availableSize.Width, desiredW);
        double finalH = double.IsInfinity(availableSize.Height) ? desiredH : Math.Min(availableSize.Height, desiredH);

        return new Size(Math.Max(0, finalW), Math.Max(0, finalH));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text))
            return;

        EnsureFormattedText(ActualWidth > 0 ? ActualWidth : double.PositiveInfinity);
        if (_textGeometry == null)
            return;

        // 1. Soft broadcast drop shadow
        if (DropShadowEnabled)
        {
            double shadowOffset = Math.Max(1.8, StrokeThickness * 0.55);
            var shadowBrush = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0));
            var shadowPen = new Pen(shadowBrush, Math.Max(1.0, StrokeThickness * 1.15))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            shadowPen.Freeze();

            dc.PushTransform(new TranslateTransform(shadowOffset, shadowOffset));
            dc.DrawGeometry(shadowBrush, shadowPen, _textGeometry);
            dc.Pop();
        }

        // 2. Crisp thick black outline (drawn behind fill so glyphs remain full-width and ultra legible)
        if (StrokeThickness > 0 && Stroke != null)
        {
            var outlinePen = new Pen(Stroke, StrokeThickness * 2.0)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            outlinePen.Freeze();
            dc.DrawGeometry(null, outlinePen, _textGeometry);
        }

        // 3. Vibrant yellow subtitle fill on top
        dc.DrawGeometry(Fill, null, _textGeometry);
    }
}
