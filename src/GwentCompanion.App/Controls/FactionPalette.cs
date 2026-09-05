using System.Windows.Media;

namespace GwentCompanion.App.Controls;

/// <summary>Restrained faction colors for deck-result headings and the artwork inspector.</summary>
internal sealed record FactionPalette(string Surface, string Accent, string Edge)
{
    public static FactionPalette For(string? faction) => faction switch
    {
        "Monsters" => new("#291C20", "#F0A49E", "#745052"),
        "Nilfgaard" => new("#25251F", "#E4D7A1", "#71694F"),
        "Northern Realms" => new("#192735", "#9AC9F0", "#476781"),
        "Scoia'tael" => new("#1C2B24", "#AED2A1", "#506F51"),
        "Skellige" => new("#262136", "#C8B2F0", "#675881"),
        "Syndicate" => new("#30251B", "#EDBF87", "#7B6446"),
        _ => new("#20252B", "#DDD3BA", "#59616A")
    };
    public static SolidColorBrush Brush(string color)
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
