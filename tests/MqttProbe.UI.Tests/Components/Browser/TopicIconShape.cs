using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace MqttProbe.UI.Tests.Components.Browser;

// Icons are SVG markup strings and the parsed DOM re-serialises their attributes, so comparing a
// rendered glyph with a LucideIcons constant goes through the drawing attributes themselves.
internal static partial class TopicIconShape
{
    // The glyph as parsed, or the same shape taken from an icon constant.
    public static string Of(IElement icon) =>
        string.Join("|", icon.Children.Select(shape =>
            Describe(shape.LocalName, name => shape.GetAttribute(name) ?? string.Empty)));

    public static string OfConstant(string iconMarkup) =>
        string.Join("|", ShapeTag().Matches(iconMarkup).Select(match =>
            Describe(match.Groups[1].Value,
                name => Attribute(name).Match(match.Groups[2].Value) is { Success: true } value
                    ? value.Groups[1].Value
                    : string.Empty)));

    private static string Describe(string tag, Func<string, string> attribute) => tag switch
    {
        "path" => "path:" + attribute("d"),
        "circle" => "circle:r=" + attribute("r"),
        "rect" => $"rect:{attribute("x")},{attribute("y")},{attribute("width")},{attribute("height")}",
        _ => tag + ":" + attribute("points")
    };

    [GeneratedRegex(@"<(path|circle|rect|polyline)\b([^>]*)/>")]
    private static partial Regex ShapeTag();

    // Anchored on whitespace, so looking up "width" never lands inside "stroke-width".
    private static Regex Attribute(string name) => new(@$"(?:^|\s){Regex.Escape(name)}='([^']*)'");
}
