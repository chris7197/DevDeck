using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Renders a full-bleed icon with a prominent central title overlay for a 144x144 Stream Dock key.
/// Shared by the SOOMFON plugin and simulator. No UI-specific resources are required at runtime.
/// </summary>
public static class DevDeckButtonRenderer
{
    private const int KeySize = 144;
    private const float TitleFontSize = 23f;
    private const float SubtitleFontSize = 15f;

    public static byte[] RenderPng(DevDeckButtonState state)
    {
        using var bitmap = new Bitmap(KeySize, KeySize);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        Color accent = AccentFor(state.Status);
        graphics.Clear(Color.FromArgb(23, 29, 42));

        using (Image? icon = DevDeckIcons.TryLoad(state.IconName))
        {
            if (icon is not null)
                graphics.DrawImage(icon, new Rectangle(0, 0, KeySize, KeySize));
            else
                DrawFallbackTile(graphics, state.IconName, accent);
        }

        DrawLabelBackdrop(graphics, state);
        DrawOverlaidLabels(graphics, state, accent);
        DrawStatusIndicator(graphics, state.Status, accent);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static Color AccentFor(DeckStatus status) => status switch
    {
        DeckStatus.Success => Color.FromArgb(103, 240, 159),
        DeckStatus.Warning => Color.FromArgb(255, 209, 103),
        DeckStatus.Error => Color.FromArgb(255, 125, 139),
        DeckStatus.Busy => Color.FromArgb(116, 194, 255),
        DeckStatus.Unavailable => Color.FromArgb(186, 194, 207),
        _ => Color.FromArgb(190, 218, 255)
    };

    private static void DrawLabelBackdrop(Graphics graphics, DevDeckButtonState state)
    {
        bool hasSubtitle = !string.IsNullOrWhiteSpace(state.Subtitle);

        // Prominent translucent band through the centre, because the title is the main identifier.
        Rectangle bandRect = hasSubtitle
            ? new Rectangle(8, 42, 128, 62)
            : new Rectangle(8, 49, 128, 48);

        using GraphicsPath path = RoundedRect(bandRect, 16);
        using var fill = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        using var edge = new Pen(Color.FromArgb(55, 255, 255, 255), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(edge, path);

        // Soft lower fade for subtitle readability without hiding the icon artwork.
        using var fade = new LinearGradientBrush(
            new Rectangle(0, 92, KeySize, 52),
            Color.Transparent, Color.Black, LinearGradientMode.Vertical);
        fade.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Color.FromArgb(0, 0, 0, 0),
                Color.FromArgb(80, 0, 0, 0),
                Color.FromArgb(160, 0, 0, 0),
                Color.FromArgb(215, 0, 0, 0)
            ],
            Positions = [0f, 0.25f, 0.65f, 1f]
        };
        graphics.FillRectangle(fade, 0, 92, KeySize, 52);
    }

    private static void DrawOverlaidLabels(Graphics graphics, DevDeckButtonState state, Color accent)
    {
        string title = state.Title.Trim();
        string subtitle = state.Subtitle.Trim();
        bool hasSubtitle = subtitle.Length != 0;
        bool twoLineTitle = title.Contains(Environment.NewLine) || title.Length > 14;

        float titleSize = title.Length > 24 ? 17.5f : title.Length > 17 ? 20f : TitleFontSize;
        float subtitleSize = subtitle.Length > 17 ? 13.5f : SubtitleFontSize;
        if (!twoLineTitle)
            titleSize = FitSingleLineFont(graphics, title, titleSize, 16f, 116);
        if (hasSubtitle)
            subtitleSize = FitSingleLineFont(graphics, subtitle, subtitleSize, 11.5f, 126);

        RectangleF titleRect = hasSubtitle
            ? new RectangleF(14, twoLineTitle ? 46 : 51, 116, twoLineTitle ? 36 : 28)
            : new RectangleF(14, twoLineTitle ? 49 : 57, 116, twoLineTitle ? 42 : 28);
        RectangleF subtitleRect = new(10, 104, 124, 20);

        using var titleFont = new Font("Segoe UI", titleSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subtitleFont = new Font("Segoe UI", subtitleSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var accentBrush = new SolidBrush(accent);
        using var titleFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = twoLineTitle ? (StringFormatFlags)0 : StringFormatFlags.NoWrap
        };
        using var subtitleFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        DrawWithShadow(graphics, title, titleFont, white, titleRect, titleFormat, 2f);
        if (hasSubtitle)
            DrawWithShadow(graphics, subtitle, subtitleFont, accentBrush, subtitleRect, subtitleFormat, 1.5f);
    }

    private static float FitSingleLineFont(Graphics graphics, string text, float preferredSize,
        float smallestSize, float availableWidth)
    {
        for (float size = preferredSize; size >= smallestSize; size -= 0.5f)
        {
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            if (graphics.MeasureString(text, font).Width <= availableWidth) return size;
        }
        return smallestSize;
    }

    private static void DrawWithShadow(Graphics graphics, string text, Font font,
        Brush foreground, RectangleF rect, StringFormat format, float shadowOffset)
    {
        using var shadow = new SolidBrush(Color.FromArgb(225, 0, 0, 0));
        graphics.DrawString(text, font, shadow, new RectangleF(rect.X, rect.Y + shadowOffset, rect.Width, rect.Height), format);
        graphics.DrawString(text, font, foreground, rect, format);
    }

    private static void DrawStatusIndicator(Graphics graphics, DeckStatus status, Color accent)
    {
        if (status == DeckStatus.Neutral) return;
        using var dark = new SolidBrush(Color.FromArgb(170, 12, 17, 27));
        using var colour = new SolidBrush(accent);
        graphics.FillEllipse(dark, 121, 8, 17, 17);
        graphics.FillEllipse(colour, 125, 12, 9, 9);
    }

    private static void DrawFallbackTile(Graphics graphics, string? iconName, Color accent)
    {
        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, KeySize, KeySize),
            Color.FromArgb(64, 84, 119), Color.FromArgb(32, 39, 65),
            LinearGradientMode.ForwardDiagonal);
        graphics.FillRectangle(background, 0, 0, KeySize, KeySize);

        string badge = DevDeckIcons.FallbackBadgeText(iconName) ?? "•";
        float badgeSize = badge.Length <= 2 ? 55f : badge.Length <= 4 ? 43f : 34f;
        using var font = new Font("Segoe UI", badgeSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(235, accent));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(badge, font, brush, new RectangleF(5, 13, 134, 86), format);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static string RenderDataUrl(DevDeckButtonState state) =>
        "data:image/png;base64," + Convert.ToBase64String(RenderPng(state));
}
