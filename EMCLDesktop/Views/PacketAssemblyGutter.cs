using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EMCLDesktop.Services.PacketInspection;

namespace EMCLDesktop.Views;

public sealed class PacketAssemblyGutter : Control
{
    private const double BaseWidth = 16;
    private const double LaneSpacing = 6;
    private static readonly IPen[] pens =
    [
        new Pen(Brush.Parse("#CA9DF0"), 1),
        new Pen(Brush.Parse("#69C4E1"), 1),
        new Pen(Brush.Parse("#E2BC7F"), 1)
    ];

    public static readonly StyledProperty<PacketAssemblyLayout?> AssemblyLayoutProperty =
        AvaloniaProperty.Register<PacketAssemblyGutter, PacketAssemblyLayout?>(nameof(AssemblyLayout));
    public static readonly StyledProperty<long> PacketIdProperty =
        AvaloniaProperty.Register<PacketAssemblyGutter, long>(nameof(PacketId));

    static PacketAssemblyGutter()
    {
        AffectsMeasure<PacketAssemblyGutter>(AssemblyLayoutProperty);
        AffectsRender<PacketAssemblyGutter>(AssemblyLayoutProperty, PacketIdProperty);
    }

    public PacketAssemblyLayout? AssemblyLayout
    {
        get => GetValue(AssemblyLayoutProperty);
        set => SetValue(AssemblyLayoutProperty, value);
    }

    public long PacketId
    {
        get => GetValue(PacketIdProperty);
        set => SetValue(PacketIdProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(BaseWidth + LaneSpacing * Math.Max(0, (AssemblyLayout?.LaneCount ?? 0) - 1), 0);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (AssemblyLayout is not { } layout) return;

        // Keep the branch aligned to the first title line, even when the title wraps.
        double titleY = Math.Min(Bounds.Height, 21.5);
        foreach (var line in layout.LinesAt(PacketId))
        {
            var pen = pens[line.Lane % pens.Length];
            double x = Bounds.Width - 8.5 - LaneSpacing * line.Lane;
            var joint = new Point(x, titleY);
            if (line.Above) context.DrawLine(pen, new Point(x, 0), joint);
            if (line.Below) context.DrawLine(pen, joint, new Point(x, Bounds.Height));
            if (line.Branch) context.DrawLine(pen, joint, new Point(Bounds.Width, titleY));
        }
    }
}
