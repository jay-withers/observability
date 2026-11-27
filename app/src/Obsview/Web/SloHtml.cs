using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace Obsview.Web;

/// <summary>The badge, meter and burn-rate fragments both SLO pages draw.</summary>
public static class SloHtml
{
    public static IHtmlContent Badge(string status, string label)
    {
        var css = status switch
        {
            "exhausted" => "st-failed",
            "at-risk" => "st-overdue",
            "ok" => "st-ok",
            _ => "st-removed",
        };
        var html = new HtmlContentBuilder();
        html.AppendHtml($"<span class=\"badge {css}\">");
        html.Append(label);
        html.AppendHtml("</span>");
        return html;
    }

    public static IHtmlContent Meter(Sli sli)
    {
        ArgumentNullException.ThrowIfNull(sli);
        var fill = sli.Status switch
        {
            "exhausted" => "st-fill-failed",
            "at-risk" => "st-fill-expiring",
            _ => "st-fill-ok",
        };
        var width = Math.Round(Math.Min(Math.Max(100 * (sli.BudgetLeft ?? 0), 0), 100), 1, MidpointRounding.ToEven);
        return new HtmlString($"<span class=\"meter\" aria-hidden=\"true\"><i class=\"{fill}\" style=\"width: {width.ToString(CultureInfo.InvariantCulture)}%\"></i></span>");
    }

    public static string Burn(double? rate) => rate is { } r ? Fmt.Fixed(r, 1) + "×" : "–";
}
