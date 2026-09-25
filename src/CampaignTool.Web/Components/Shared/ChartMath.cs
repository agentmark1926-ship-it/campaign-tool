using System.Globalization;

namespace CampaignTool.Web.Components.Shared;

/// <summary>SVG coordinates for the dashboard's hand-drawn charts.</summary>
public static class ChartMath
{
    /// <summary>"x,y x,y …" spreading the values across the width, scaled to the height (with padding), 0 at the bottom.</summary>
    public static string Polyline(IReadOnlyList<int> values, double width, double height, double pad, int? max = null)
    {
        if (values.Count == 0) return "";
        var top = Math.Max(1, max ?? values.Max());
        var step = values.Count == 1 ? 0 : width / (values.Count - 1);
        return string.Join(' ', values.Select((v, i) =>
            $"{F(i * step)},{F(height - pad - (height - 2 * pad) * v / top)}"));
    }

    /// <summary>A closed area under the same line, for a filled chart.</summary>
    public static string Area(IReadOnlyList<int> values, double width, double height, double pad, int? max = null) =>
        values.Count == 0 ? "" : $"0,{F(height - pad)} {Polyline(values, width, height, pad, max)} {F(width)},{F(height - pad)}";

    private static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
}
