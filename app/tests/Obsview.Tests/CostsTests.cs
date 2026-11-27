using System.Text.Json;
using Obsview.Web;

namespace Obsview.Tests;

public class CostsTests
{
    private static readonly DateTimeOffset Now = R.At("2026-11-17T12:00:00Z");

    internal static Row Row(string day, string group, string service, double cost, string currency = "USD", string resource = "") => R.Of(
        ("UsageDate", Times.Midnight(R.Day(day))),
        ("ResourceGroup", group),
        ("ServiceName", service),
        ("ResourceId", resource.Length > 0 ? $"/subscriptions/s/resourceGroups/{group}/providers/p/{resource}" : ""),
        ("Cost", cost),
        ("Currency", currency));

    private static CostsView Build(int days, params Row[] rows) => Costs.Build(Costs.Parse(rows), days, Now, 150.0);

    private static CostsView Build(params Row[] rows) => Build(30, rows);

    private static CostsView Slice(int days, params Row[] rows) => Costs.Build(Costs.Parse(rows), days, Now, 150.0, byArea: false);

    private static string Json(object value) => JsonSerializer.Serialize(value, Queries.Json);

    [Fact]
    public void Month_to_date_counts_this_month_only()
    {
        var view = Build(
            Row("2026-10-31", "rg-platform-dev", "Log Analytics", 99.0),
            Row("2026-11-01", "rg-platform-dev", "Log Analytics", 2.0),
            Row("2026-11-16", "rg-gymlog-dev", "Storage", 1.0));
        Assert.Equal(3.0, view.MonthToDate, 9);
        Assert.Equal(2.0, view.BudgetUsed, 9);
        Assert.Equal("November", view.MonthLabel);
    }

    [Fact]
    public void Forecast_divides_by_days_with_data_not_days_elapsed()
    {
        // Data through the 10th (the feed lags), $1 a day: $10 over 10 days of
        // data, so $30 for November's 30 days. Dividing by the 17th would say $17.6.
        var view = Build([.. Enumerable.Range(1, 10).Select(d => Row($"2026-11-{d:00}", "rg-platform-dev", "x", 1.0))]);
        Assert.Equal(R.Day("2026-11-10"), view.DataThrough);
        Assert.Equal(30.0, view.Forecast!.Value, 9);
    }

    [Fact]
    public void No_data_this_month_means_no_forecast()
    {
        var view = Build(Row("2026-10-30", "rg-platform-dev", "x", 5.0));
        Assert.Equal(0, view.MonthToDate);
        Assert.Null(view.Forecast);
    }

    [Fact]
    public void Areas_are_fixed_by_resource_group_case_insensitively()
    {
        Assert.Equal("platform", Costs.AreaOf("rg-platform-dev"));
        Assert.Equal("apps", Costs.AreaOf("RG-GYMLOG-DEV"));
        Assert.Equal(Costs.Other, Costs.AreaOf("rg-something-new"));
    }

    [Fact]
    public void Daily_series_stack_by_area_over_the_range()
    {
        var spend = Build(
            7,
            Row("2026-11-17", "rg-platform-dev", "a", 1.0),
            Row("2026-11-17", "rg-gymlog-dev", "b", 0.5),
            Row("2026-11-17", "rg-hub-dev", "c", 0.25),
            Row("2026-11-01", "rg-platform-dev", "a", 9.0)).Charts.Spend; // outside a 7-day range
        Assert.Equal(7, spend.Dates.Count);
        Assert.Equal(1.0, spend.Series["platform"][^1]);
        Assert.Equal(0.5, spend.Series["apps"][^1]);
        Assert.Equal(0.25, spend.Series[Costs.Other][^1]);
        Assert.Equal(1.0, spend.Series["platform"].Sum());
    }

    [Fact]
    public void Bars_fold_the_tail_into_everything_else()
    {
        var services = Build([.. Enumerable.Range(0, 10).Select(i => Row("2026-11-10", "rg-platform-dev", $"svc{i}", 10 - i))]).Services;
        Assert.Equal(8, services.Count);
        Assert.Equal("everything else", services[^1].Label);
        Assert.Equal(3 + 2 + 1, services[^1].Value, 9);
        Assert.Equal(100.0, services[0].Width);
    }

    [Fact]
    public void Resource_group_bars_carry_their_areas_colour()
    {
        var view = Build(Row("2026-11-10", "rg-aks-dev", "AKS", 4.0));
        var aks = Costs.AreaOrder.ToList().IndexOf("aks") + 1;
        Assert.Equal([("rg-aks-dev", aks)], view.Groups.Select(b => (b.Label, b.Colour)));
        Assert.Equal([("aks", aks)], view.Areas.Select(b => (b.Label, b.Colour)));
    }

    [Fact]
    public void A_slice_stacks_its_own_services_and_names_no_other_area()
    {
        var view = Slice(7, Row("2026-11-17", "rg-gymlog-dev", "Container Apps", 1.0), Row("2026-11-17", "rg-gymlog-dev", "Storage", 0.25));
        Assert.Equal(["Container Apps", "Storage"], view.Charts.SeriesKeys);
        Assert.Equal(1.0, view.Charts.Spend.Series["Container Apps"][^1]);
        Assert.Equal(["Container Apps", "Storage"], view.Charts.Spend.Series.Keys.Order());
        // The treemap starts at the slice's resource groups, not the estate's areas.
        Assert.Equal(
            """[{"name":"rg-gymlog-dev","children":[{"name":"(not broken down)","children":[{"name":"Container Apps","value":1},{"name":"Storage","value":0.25}]}]}]""",
            Json(view.Charts.Treemap));
        Assert.All(view.Groups, b => Assert.Equal(0, b.Colour));
    }

    [Fact]
    public void A_slices_daily_series_fold_the_tail_and_still_sum_to_the_total()
    {
        var view = Slice(1, [.. Enumerable.Range(0, 8).Select(i => Row("2026-11-17", "rg-platform-dev", $"svc{i}", 10 - i))]);
        var keys = view.Charts.SeriesKeys;
        Assert.Equal(Costs.ServiceSeries, keys.Count);
        Assert.Equal(Costs.Rest, keys[^1]);
        var series = view.Charts.Spend.Series;
        Assert.Equal(view.Total, series.Values.Sum(v => v[^1]), 9);
        Assert.Equal(6 + 5 + 4 + 3, series[Costs.Rest][^1], 9);
    }

    [Fact]
    public void Treemap_is_area_then_group_then_service_in_area_order()
    {
        var tree = Build(Row("2026-11-10", "rg-gymlog-dev", "Storage", 1.0), Row("2026-11-10", "rg-platform-dev", "Log Analytics", 2.0)).Charts.Treemap;
        Assert.Equal(["platform", "apps"], tree.Select(n => n.Name));
        Assert.Equal(
            """{"name":"rg-gymlog-dev","children":[{"name":"(not broken down)","children":[{"name":"Storage","value":1}]}]}""",
            Json(tree[1].Children![0]));
    }

    [Fact]
    public void Currency_symbol()
    {
        Assert.Equal("£", Build(Row("2026-11-10", "rg", "s", 1.0, "GBP")).Symbol);
        Assert.Equal("CHF ", Build(Row("2026-11-10", "rg", "s", 1.0, "CHF")).Symbol);
        Assert.Equal("", Costs.Symbol(""));
    }

    [Fact]
    public void Gauge_scale_fits_the_largest_of_budget_forecast_and_spend()
    {
        // $100 so far, $300 forecast, $150 budget.
        var view = Build([.. Enumerable.Range(1, 10).Select(d => Row($"2026-11-{d:00}", "rg-platform-dev", "x", 10.0))]);
        Assert.Equal(100 / 1.05, view.Scale(view.Forecast ?? 0), 9);
        Assert.True(view.Scale(view.Budget) < view.Scale(view.Forecast ?? 0));
    }

    [Fact]
    public void Window_covers_the_month_or_two_ranges_and_the_repost_week()
    {
        Assert.Equal(TimeSpan.FromDays(39), Costs.Window(1));
        Assert.Equal(TimeSpan.FromDays(39), Costs.Window(7));
        Assert.Equal(TimeSpan.FromDays(188), Costs.Window(90));
    }

    [Fact]
    public void Daily_series_end_at_the_last_day_with_data()
    {
        // The feed lags: nothing yet for the 16th or 17th, so the chart ends on
        // the 15th rather than dropping to zero for two days.
        var spend = Build(7, Row("2026-11-15", "rg-platform-dev", "a", 1.0)).Charts.Spend;
        Assert.Equal("2026-11-15", spend.Dates[^1]);
        Assert.Equal(1.0, spend.Series["platform"][^1]);
    }

    [Fact]
    public void Only_the_billing_currency_is_summed()
    {
        var view = Build(
            Row("2026-11-10", "rg-platform-dev", "a", 10.0, "USD"),
            Row("2026-11-11", "rg-platform-dev", "a", 10.0, "USD"),
            Row("2026-11-11", "rg-platform-dev", "b", 99.0, "GBP"));
        Assert.Equal(20.0, view.MonthToDate, 9);
        Assert.Equal("$", view.Symbol);
        Assert.Equal(1, view.LeftOut);
    }

    [Fact]
    public void Early_in_the_month_data_through_shows_last_month()
    {
        // 1 Nov, with the feed through 31 Oct: nothing this month yet, but the
        // page still says how fresh the data is.
        var view = Costs.Build(Costs.Parse([Row("2026-10-31", "rg-platform-dev", "a", 1.0)]), 30, R.At("2026-11-01T09:00:00Z"), 150.0);
        Assert.Equal(0, view.MonthToDate);
        Assert.Null(view.Forecast);
        Assert.Equal(R.Day("2026-10-31"), view.DataThrough);
    }

    [Fact]
    public void A_malformed_date_skips_the_row() =>
        Assert.Empty(Costs.Parse([R.Of(("UsageDate", "not a date"), ("ResourceGroup", "rg"), ("ServiceName", "s"), ("Cost", 1.0))]));

    [Fact]
    public void A_cost_that_is_not_a_number_skips_the_row() =>
        Assert.Empty(Costs.Parse([R.Of(("UsageDate", "2026-11-10T00:00:00Z"), ("Cost", "1.0"))]));

    [Fact]
    public void Breakdowns_follow_the_range_not_the_month()
    {
        Row[] rows =
        [
            Row("2026-11-15", "rg-platform-dev", "Log Analytics", 1.0),
            Row("2026-11-10", "rg-gymlog-dev", "Storage", 5.0), // outside 3 days
            Row("2026-10-30", "rg-aks-dev", "AKS", 7.0), // last month, inside 30 days
        ];
        var shortRange = Build(3, rows);
        Assert.Equal(1.0, shortRange.Total, 9);
        Assert.Equal(["rg-platform-dev"], shortRange.Groups.Select(b => b.Label));
        Assert.Equal(["platform"], shortRange.Charts.Treemap.Select(n => n.Name));
        var wide = Build(30, rows);
        Assert.Equal(13.0, wide.Total, 9);
        Assert.Equal(["AKS", "Log Analytics", "Storage"], wide.Services.Select(b => b.Label).Order());
        Assert.Equal(6.0, wide.MonthToDate, 9); // the budget card stays the month
    }

    [Fact]
    public void Range_ends_on_the_last_day_with_data()
    {
        // The feed is through the 15th; a one-day range is the 15th, not an empty 17th.
        var view = Build(1, Row("2026-11-15", "rg-platform-dev", "a", 2.0));
        Assert.Equal((R.Day("2026-11-15"), R.Day("2026-11-15")), (view.RangeStart, view.RangeEnd));
        Assert.Equal(2.0, view.Total, 9);
    }

    [Fact]
    public void An_explicit_end_is_kept_for_a_quiet_app()
    {
        var view = Costs.Build(Costs.Parse([Row("2026-11-10", "rg-gymlog-dev", "Storage", 1.0)]), 3, Now, 150.0, R.Day("2026-11-16"));
        Assert.Equal(R.Day("2026-11-16"), view.RangeEnd);
        Assert.Equal(0, view.Total);
    }

    [Fact]
    public void Delta_against_the_prior_range()
    {
        var view = Build(1, Row("2026-11-15", "rg-platform-dev", "a", 3.0), Row("2026-11-14", "rg-platform-dev", "a", 1.0));
        Assert.Equal(1.0, view.PriorTotal!.Value, 9);
        Assert.Equal("+$2.00 vs prior 1d", view.Delta);
        Assert.Equal("bad", view.Tone);
    }

    [Fact]
    public void A_fall_reads_as_good()
    {
        var view = Build(1, Row("2026-11-15", "rg-platform-dev", "a", 1.0), Row("2026-11-14", "rg-platform-dev", "a", 1234.5));
        Assert.Equal("-$1,233.50 vs prior 1d", view.Delta);
        Assert.Equal("good", view.Tone);
    }

    [Fact]
    public void No_delta_when_the_data_does_not_reach_the_prior_range()
    {
        // Retention: nothing before the 14th, so a 3-day range's prior (9th-11th)
        // would read as a rise from nothing.
        var view = Build(3, Row("2026-11-14", "rg-platform-dev", "a", 1.0), Row("2026-11-15", "rg-platform-dev", "a", 1.0));
        Assert.Null(view.PriorTotal);
        Assert.Null(view.Delta);
    }

    [Fact]
    public void Spend_matches_builds_total()
    {
        var rows = Costs.Parse(
        [
            Row("2026-11-15", "rg-gymlog-dev", "a", 2.0),
            Row("2026-11-10", "rg-gymlog-dev", "a", 3.0),
            Row("2026-11-15", "rg-gymlog-dev", "a", 9.0, "GBP"),
        ]);
        var end = R.Day("2026-11-15");
        Assert.Equal(2.0, Costs.Spend(rows, end, 3));
        Assert.Equal(2.0, Costs.Build(rows, 3, Now, 150.0, end).Total);
    }

    [Fact]
    public void A_slices_chart_keeps_a_service_that_nets_to_a_credit()
    {
        var view = Slice(
            2,
            Row("2026-11-16", "rg-gymlog-dev", "Compute", 5.0),
            Row("2026-11-16", "rg-gymlog-dev", "Storage", 2.0),
            Row("2026-11-17", "rg-gymlog-dev", "Storage", -3.0));
        Assert.Contains("Storage", view.Charts.SeriesKeys);
        Assert.Equal(view.Total, view.Charts.Spend.Series.Values.Sum(v => v.Sum()), 9);
    }

    [Fact]
    public void Resources_are_keyed_by_id_and_labelled_with_their_group()
    {
        var view = Build(
            Row("2026-11-10", "rg-gymlog-dev", "Storage", 2.0, resource: "default"),
            Row("2026-11-10", "rg-career-dev", "Storage", 1.0, resource: "default"),
            Row("2026-11-10", "rg-career-dev", "Key Vault", 0.5));
        Assert.Equal(
            [("default · rg-gymlog-dev", 2.0), ("default · rg-career-dev", 1.0), ($"{Costs.Unattributed} · rg-career-dev", 0.5)],
            view.Resources.Select(b => (b.Label, b.Value)));
    }

    [Fact]
    public void The_treemap_has_a_resource_level() =>
        Assert.Equal(
            """[{"name":"rg-gymlog-dev","children":[{"name":"stgymlog","children":[{"name":"Storage","value":1}]}]}]""",
            Json(Slice(30, Row("2026-11-10", "rg-gymlog-dev", "Storage", 1.0, resource: "STGymlog")).Charts.Treemap));

    [Fact]
    public void Same_named_resources_of_different_types_stay_apart_in_the_treemap()
    {
        var group = Slice(
            30,
            Row("2026-11-10", "rg-x", "Container Apps", 2.0, resource: "containerApps/ca-x"),
            Row("2026-11-10", "rg-x", "Managed Identity", 1.0, resource: "identities/ca-x")).Charts.Treemap[0];
        Assert.Equal([("ca-x", (double?)2.0), ("ca-x", 1.0)], group.Children!.Select(n => (n.Name, n.Children![0].Value)));
    }
}
