// Draws every [data-chart] element from the JSON in #chart-data.
//
// A static file rather than an inline script, so the CSP's script-src 'self'
// covers it with no nonce. Colours come from the CSS custom properties in
// app.css, read at draw time, so light and dark are defined once there and a
// change of the OS theme redraws with the other set.
(function () {
  "use strict";
  var source = document.getElementById("chart-data");
  if (!source || typeof echarts === "undefined") return;
  var payload = JSON.parse(source.textContent);
  var now = new Date(payload.now);
  var instances = [];

  var OUTCOMES = ["success", "untested", "rolled_back", "failed"];
  var OUTCOME_LABEL = { success: "Success", untested: "Untested", rolled_back: "Rolled back", failed: "Failed" };
  var OUTCOME_TOKEN = { success: "--status-good", untested: "--status-neutral", rolled_back: "--status-warning", failed: "--status-critical" };
  // Colour follows the trigger's name, never its rank, so a range where
  // merges outnumber Renovate does not swap their colours.
  var TRIGGER_TOKEN = { renovate: "--series-1", merge: "--series-2", manual: "--series-3" };

  function token(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }
  function outcomeColour(o) { return token(OUTCOME_TOKEN[o] || "--status-neutral"); }
  function shortDate(iso) {
    var d = new Date(iso + (iso.length === 10 ? "T00:00:00Z" : ""));
    return d.toLocaleDateString(undefined, { day: "numeric", month: "short", timeZone: "UTC" });
  }
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  function base() {
    var ink2 = token("--ink-2");
    return {
      animationDuration: 400,
      textStyle: { fontFamily: getComputedStyle(document.body).fontFamily, color: ink2 },
      tooltip: {
        backgroundColor: token("--surface"),
        borderColor: token("--axis"),
        textStyle: { color: token("--ink"), fontSize: 12 },
        confine: true,
      },
    };
  }
  function axisStyle() {
    return {
      axisLine: { lineStyle: { color: token("--axis") } },
      axisTick: { show: false },
      axisLabel: { color: token("--muted"), fontSize: 11 },
      splitLine: { lineStyle: { color: token("--grid") } },
    };
  }
  function merge(a, b) { return Object.assign({}, a, b); }

  // The builders for one set of chart data. An app's page carries several
  // views' sets, keyed by group, and each chart element names its group with
  // data-group; the estate views carry one set. Each builder (and every
  // tooltip formatter it leaves behind) closes over its own set.
  function buildersFor(charts) { return {
    outcomes: function () {
      var gap = token("--surface");
      return merge(base(), {
        grid: { left: 28, right: 8, top: 8, bottom: 24 },
        tooltip: merge(base().tooltip, { trigger: "axis", axisPointer: { type: "shadow" } }),
        xAxis: merge(axisStyle(), { type: "category", data: charts.dates.map(shortDate), splitLine: { show: false } }),
        yAxis: merge(axisStyle(), { type: "value", minInterval: 1 }),
        series: OUTCOMES.map(function (o) {
          return {
            name: OUTCOME_LABEL[o], type: "bar", stack: "all", barMaxWidth: 18,
            data: charts.outcomes[o],
            itemStyle: { color: outcomeColour(o), borderColor: gap, borderWidth: 1 },
            emphasis: { focus: "series" },
          };
        }),
      });
    },

    triggers: function () {
      return merge(base(), {
        tooltip: merge(base().tooltip, { trigger: "item", formatter: "{b}: {c} ({d}%)" }),
        legend: { bottom: 0, icon: "roundRect", itemWidth: 10, itemHeight: 10, textStyle: { color: token("--ink-2") } },
        series: [{
          type: "pie", radius: ["52%", "78%"], center: ["50%", "44%"],
          padAngle: 2, itemStyle: { borderRadius: 4 },
          label: { show: true, position: "center", formatter: function () {
            var total = charts.triggers.reduce(function (s, t) { return s + t.value; }, 0);
            return "{n|" + total + "}\n{l|deploys}";
          }, rich: { n: { fontSize: 24, fontWeight: 650, color: token("--ink") }, l: { fontSize: 12, color: token("--muted") } } },
          labelLine: { show: false },
          emphasis: { scale: false, label: { show: true } },
          data: charts.triggers.map(function (t) {
            return { name: t.name, value: t.value, itemStyle: { color: token(TRIGGER_TOKEN[t.name] || "--series-4") } };
          }),
        }],
      });
    },

    timeline: function (el) {
      var t = charts.timeline;
      var start = new Date(charts.start);
      // On a phone the full "repo / app" label takes half the width; the repo
      // alone is enough to tell the rows apart, and the tooltip has the rest.
      var narrow = el.clientWidth < 520;
      return merge(base(), {
        grid: { left: 8, right: 16, top: 8, bottom: 28, containLabel: true },
        tooltip: merge(base().tooltip, {
          trigger: "item",
          formatter: function (p) {
            var v = p.value;
            return "<b>" + escapeHtml(t.apps[v[1]]) + "</b> " + escapeHtml(v[3]) + "<br>" +
              escapeHtml(OUTCOME_LABEL[v[2]] || v[2]) + " · " + escapeHtml(v[4]) + "<br>" +
              new Date(v[0]).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
          },
        }),
        xAxis: merge(axisStyle(), {
          type: "time", min: start, max: now, splitLine: { show: false },
          axisLabel: { color: token("--muted"), fontSize: 11, hideOverlap: true },
        }),
        yAxis: merge(axisStyle(), {
          type: "category", data: t.apps,
          axisLabel: {
            color: token("--ink-2"), fontSize: 12, width: narrow ? 96 : 170, overflow: "truncate",
            formatter: function (label) { return narrow ? label.split(" / ")[0] : label; },
          },
          splitLine: { show: true, lineStyle: { color: token("--grid") } },
        }),
        series: [{
          type: "scatter", symbolSize: 10, data: t.points,
          itemStyle: { color: function (p) { return outcomeColour(p.value[2]); }, borderColor: token("--surface"), borderWidth: 2 },
          emphasis: { scale: 1.6 },
        }],
      });
    },

    calendar: function (el) {
      var c = charts.calendar;
      var weeks = 14;
      var cell = Math.max(10, Math.min(22, Math.floor((el.clientWidth - 40) / weeks)));
      return merge(base(), {
        tooltip: merge(base().tooltip, {
          formatter: function (p) { return shortDate(p.value[0]) + ": " + p.value[1] + " deploy" + (p.value[1] === 1 ? "" : "s"); },
        }),
        visualMap: {
          type: "piecewise", show: false,
          pieces: [
            { value: 0, color: token("--grid") },
            { min: 1, max: 1, color: token("--seq-2") },
            { min: 2, max: 3, color: token("--seq-3") },
            { min: 4, max: 5, color: token("--seq-4") },
            { min: 6, color: token("--seq-5") },
          ],
        },
        calendar: {
          range: [c.start, c.end], top: 22, left: 30, right: 4, cellSize: [cell, cell],
          splitLine: { show: false },
          itemStyle: { color: "transparent", borderColor: token("--surface"), borderWidth: 3 },
          dayLabel: { firstDay: 1, nameMap: ["S", "M", "T", "W", "T", "F", "S"], color: token("--muted"), fontSize: 10 },
          monthLabel: { color: token("--muted"), fontSize: 11 },
          yearLabel: { show: false },
        },
        series: [{ type: "heatmap", coordinateSystem: "calendar", data: c.data, itemStyle: { borderRadius: 3 } }],
      });
    },

    // One row per check, one column per day, coloured by the worst status
    // that day. Empty cells are days with no row.
    grid: function (el) {
      var g = charts.grids[el.getAttribute("data-key")];
      var narrow = el.clientWidth < 520;
      var SEV = ["--status-good", "--status-neutral", "--status-warning", "--status-critical"];
      return merge(base(), {
        grid: { left: 8, right: 8, top: 4, bottom: 24, containLabel: true },
        tooltip: merge(base().tooltip, {
          formatter: function (p) {
            var v = p.value;
            return "<b>" + escapeHtml(g.names[v[1]]) + "</b><br>" +
              escapeHtml(shortDate(charts.dates[v[0]])) + ": " + escapeHtml(v[3]);
          },
        }),
        xAxis: merge(axisStyle(), {
          type: "category", data: charts.dates.map(shortDate), splitLine: { show: false },
          axisLabel: { color: token("--muted"), fontSize: 11, hideOverlap: true },
        }),
        yAxis: merge(axisStyle(), {
          type: "category", data: g.names, splitLine: { show: false },
          axisLabel: { color: token("--ink-2"), fontSize: 12, width: narrow ? 110 : 200, overflow: "truncate" },
        }),
        visualMap: {
          type: "piecewise", show: false, dimension: 2,
          pieces: SEV.map(function (t, i) { return { value: i, color: token(t) }; }),
        },
        series: [{
          type: "heatmap", data: g.cells,
          itemStyle: { borderColor: token("--surface"), borderWidth: 2, borderRadius: 3 },
        }],
      });
    },

    spend: function () {
      var s = charts.spend;
      var symbol = payload.symbol || "";
      return merge(base(), {
        grid: { left: 8, right: 8, top: 8, bottom: 24, containLabel: true },
        tooltip: merge(base().tooltip, {
          trigger: "axis",
          valueFormatter: function (v) { return symbol + Number(v).toFixed(2); },
        }),
        xAxis: merge(axisStyle(), { type: "category", boundaryGap: false, data: s.dates.map(shortDate), splitLine: { show: false } }),
        yAxis: merge(axisStyle(), { type: "value", axisLabel: { color: token("--muted"), fontSize: 11, formatter: symbol + "{value}" } }),
        series: charts.series_keys.map(function (a, i) {
          var colour = token("--series-" + (i + 1));
          return {
            name: a, type: "line", stack: "spend", symbol: "none", smooth: 0.2,
            data: s.series[a],
            lineStyle: { width: 1.5, color: colour },
            itemStyle: { color: colour },
            areaStyle: { color: colour, opacity: 0.85 },
            emphasis: { focus: "series" },
          };
        }),
      });
    },

    treemap: function () {
      var symbol = payload.symbol || "";
      // The estate's top level is its areas, coloured as the daily chart's;
      // a slice's is its resource groups, coloured in turn.
      var data = charts.treemap.map(function (node, n) {
        var i = charts.by_area ? charts.series_keys.indexOf(node.name) : n;
        return merge(node, { itemStyle: { color: token("--series-" + ((i % 5) + 1)) } });
      });
      return merge(base(), {
        tooltip: merge(base().tooltip, {
          formatter: function (p) {
            var path = (p.treePathInfo || []).slice(1).map(function (n) { return escapeHtml(n.name); }).join(" › ");
            return path + "<br><b>" + symbol + Number(p.value).toFixed(2) + "</b>";
          },
        }),
        series: [{
          type: "treemap", name: "All", data: data, roam: false, nodeClick: "zoomToNode",
          top: 0, left: 0, right: 0, bottom: 28, leafDepth: 2,
          breadcrumb: { bottom: 0, itemStyle: { color: token("--grid"), borderColor: token("--grid"), textStyle: { color: token("--ink") } } },
          label: { fontSize: 12, color: "#fff", overflow: "truncate" },
          upperLabel: { show: true, height: 20, color: "#fff", fontSize: 12 },
          itemStyle: { borderColor: token("--surface"), borderWidth: 2, gapWidth: 2, borderRadius: 4 },
          levels: [
            { itemStyle: { borderWidth: 0, gapWidth: 3 } },
            { colorSaturation: [0.35, 0.6], itemStyle: { gapWidth: 2, borderColorSaturation: 0.5 } },
            { colorSaturation: [0.35, 0.5], itemStyle: { gapWidth: 1 } },
          ],
        }],
      });
    },

    lead: function () {
      return merge(base(), {
        grid: { left: 28, right: 8, top: 8, bottom: 24 },
        tooltip: merge(base().tooltip, { trigger: "axis", axisPointer: { type: "shadow" } }),
        xAxis: merge(axisStyle(), { type: "category", data: charts.lead.labels, splitLine: { show: false } }),
        yAxis: merge(axisStyle(), { type: "value", minInterval: 1 }),
        series: [{
          name: "Deploys", type: "bar", data: charts.lead.counts, barMaxWidth: 36,
          itemStyle: { color: token("--series-1"), borderRadius: [4, 4, 0, 0] },
        }],
      });
    },
  }; }

  function draw() {
    instances.forEach(function (i) { i.dispose(); });
    instances = [];
    document.querySelectorAll("[data-chart]").forEach(function (el) {
      var group = el.getAttribute("data-group");
      var data = group ? (payload.groups || {})[group] : payload.charts;
      if (!data) return;
      var build = buildersFor(data)[el.getAttribute("data-chart")];
      if (!build) return;
      var chart = echarts.init(el, null, { renderer: "svg" });
      chart.setOption(build(el));
      instances.push(chart);
    });
  }

  draw();
  var pending;
  var width = window.innerWidth;
  window.addEventListener("resize", function () {
    // Phones fire resize as the URL bar slides in and out on scroll; only a
    // change of width (rotation, a desktop window) needs a redraw.
    if (window.innerWidth === width) return;
    width = window.innerWidth;
    clearTimeout(pending);
    // The calendar's cell size depends on the width, so it is rebuilt, not resized.
    pending = setTimeout(draw, 150);
  });
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", draw);
})();
