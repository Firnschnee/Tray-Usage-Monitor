using System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>User-selectable accent color for the "normal" (below-warn) state.</summary>
public enum AccentColor
{
    Green,
    Amber,
}

/// <summary>Maps an <see cref="AccentColor"/> to the fill used below the warn threshold.</summary>
internal static class AccentPalette
{
    private static readonly Color Green = Color.FromArgb(34, 197, 94);   // #22C55E
    private static readonly Color Amber = Color.FromArgb(250, 189, 47);  // #FABD2F

    public static Color Ok(AccentColor a) => a == AccentColor.Amber ? Amber : Green;
}
