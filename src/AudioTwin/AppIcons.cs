using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AudioTwin;

/// <summary>
/// The notification-area and window icons.
///
/// The window icon is the AudioTwin logo, loaded from the embedded AudioTwin.ico
/// (the same file the .exe carries, so Alt+Tab, the title bar and Explorer all
/// agree).
///
/// The tray icons are still drawn at run time, deliberately. They are rasterised
/// at exactly the size Windows asks for, so nothing is ever up- or down-scaled,
/// and their colour carries the state: grey = stopped, green = routing, amber =
/// routing with one output down. A detailed logo cannot do that at 16 pixels.
/// </summary>
internal sealed class AppIcons : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly List<IntPtr> _handles = new();

    public Icon Stopped { get; }
    public Icon Running { get; }
    public Icon Degraded { get; }
    public Icon Window { get; }

    public AppIcons()
    {
        // SmallIconSize is already DPI-scaled (16 at 100%, 24 at 150%, ...).
        int tray = Math.Max(16, SystemInformation.SmallIconSize.Width);

        Stopped = Draw(tray, Color.FromArgb(0x86, 0x8B, 0x92), waves: 1);
        Running = Draw(tray, Color.FromArgb(0x21, 0x9A, 0x4B), waves: 2);
        Degraded = Draw(tray, Color.FromArgb(0xD3, 0x84, 0x0B), waves: 2);
        Window = LoadLogo() ?? Draw(32, Color.FromArgb(0x2C, 0x6E, 0xBF), waves: 2);
    }

    /// <summary>
    /// The multi-resolution logo embedded at build time. Returns null rather than
    /// throwing if the resource is ever missing, because a wrong icon is not worth
    /// failing start-up over.
    /// </summary>
    private static Icon? LoadLogo()
    {
        try
        {
            using var stream = typeof(AppIcons).Assembly
                .GetManifestResourceStream("AudioTwin.ico");

            // Icon(Stream) keeps every frame, so Windows can pick the right one
            // for the title bar and for Alt+Tab independently.
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A speaker with 0-2 sound waves, drawn on a 32x32 design grid.</summary>
    private Icon Draw(int size, Color color, int waves)
    {
        float s = size / 32f;

        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var speaker = new GraphicsPath();
            speaker.AddPolygon(new[]
            {
                new PointF(4.0f * s, 12.0f * s),
                new PointF(10.0f * s, 12.0f * s),
                new PointF(17.0f * s, 4.5f * s),
                new PointF(17.0f * s, 27.5f * s),
                new PointF(10.0f * s, 20.0f * s),
                new PointF(4.0f * s, 20.0f * s)
            });

            using var fill = new SolidBrush(color);
            g.FillPath(fill, speaker);

            // A darker outline so the glyph reads on light and dark taskbars alike.
            using var outline = new Pen(Darken(color, 0.55f), Math.Max(1f, 1.2f * s));
            g.DrawPath(outline, speaker);

            using var wave = new Pen(color, Math.Max(1.5f, 3f * s))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            if (waves >= 1) g.DrawArc(wave, 10.0f * s, 9.0f * s, 14.0f * s, 14.0f * s, -55f, 110f);
            if (waves >= 2) g.DrawArc(wave, 6.0f * s, 4.5f * s, 22.0f * s, 23.0f * s, -50f, 100f);
        }

        IntPtr handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    private static Color Darken(Color color, float factor) => Color.FromArgb(
        color.A,
        (int)(color.R * factor),
        (int)(color.G * factor),
        (int)(color.B * factor));

    public void Dispose()
    {
        Stopped.Dispose();
        Running.Dispose();
        Degraded.Dispose();
        Window.Dispose();

        // Icon.FromHandle does not own the handle, so it has to go back by hand.
        foreach (var handle in _handles)
        {
            try { DestroyIcon(handle); } catch { }
        }

        _handles.Clear();
    }
}
