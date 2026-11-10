"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const NS = "http://www.w3.org/2000/svg";
  const A = "#68cbd9", B = "#ff8e61";
  const state = { catalog: null, a: null, b: null, impactA: null, impactB: null, land: null,
    tab: "profiles", selected: null, flight: null, ids: { a: [], b: [] }, generation: 0, catalogGeneration: 0, catalogPending: false, selection: null,
    playing: false, animationFrame: null, plots: [], replayPlots: [] };
  const valid = value => typeof value === "number" && Number.isFinite(value);
  const fmt = (value, digits = 1) => valid(value) ? value.toLocaleString("en-GB", { maximumFractionDigits: digits }) : "—";
  const metric = {
    fpm: ["Aircraft descent at contact", "ft/min"], g: ["Peak vertical load", "G"], weight: ["Landing weight", "lb"],
    ias: ["Indicated airspeed at contact", "kt"], pitch: ["Pitch at contact (+ nose up)", "°"], bank: ["Bank at contact", "°"],
    crosswind: ["Crosswind (+ from right)", "kt"], crab: ["Heading relative to runway", "°"],
    stickY: ["Physical yaw input at contact", "%"], cmdY: ["Yaw command at contact", "%"], latG: ["Lateral load at contact", "G"],
    along: ["Distance past landing threshold", "m"], cross: ["Right of runway centreline", "m"], flap: ["Flap position", "%"]
  };
  const sets = { approach: ["agl", "ias", "sink", "pitch", "bank", "throttle", "n1", "stickP"],
    controls: ["stickP", "stickR", "stickY", "cmdP", "cmdR", "cmdY"],
    response: ["stickP", "cmdP", "elev", "pitch", "aoa", "sink"],
    "roll-response": ["stickR", "cmdR", "ail", "bank"],
    "yaw-response": ["stickY", "cmdY", "rud", "beta", "crab", "latG"],
    flare: ["sink", "pitch", "stickP", "throttle", "ias", "g"],
    wind: ["crosswind", "crab", "bank", "stickR", "stickY", "latG"] };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function svg(tag, attrs = {}, text) {
    const node = document.createElementNS(NS, tag);
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, String(value));
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function options(id, values, label = "All", selected) {
    const node = $(id), previous = selected === undefined ? node.value : selected;
    node.replaceChildren(new Option(label, ""));
    [...new Set(values.filter(Boolean))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }))
      .forEach(value => node.add(new Option(value, value)));
    if ([...node.options].some(option => option.value === previous)) node.value = previous;
  }
  function error(message) { $("flight-error").hidden = !message; $("flight-error").textContent = message || ""; }
  async function request(path) {
    const response = await fetch(path, { headers: { Accept: "application/json" }, cache: "no-store" });
    if (!response.ok) {
      let detail;
      try { detail = (await response.json()).detail; } catch { detail = null; }
      throw new Error(typeof detail === "string" ? detail : `Request failed (${response.status})`);
    }
    return response.json();
  }
  function get(path) {
    if (!/^\/api\/analytics\/cohort\?/.test(path)) return request(path);
    // The server intentionally admits one aggregate query at a time. Views in
    // this browser share that lane, including tab changes during a refresh.
    const job = (state.aggregateRequest || Promise.resolve()).then(() => request(path));
    state.aggregateRequest = job.then(() => undefined, () => undefined);
    return job;
  }
  function query(axis = $("alignment").value, cohortB = false) {
    const params = new URLSearchParams({ axis });
    for (const key of ["days", "aircraft", "airport", "runway", "creator", "device", "wind_min", "wind_max", "weight_min", "weight_max"]) {
      if ($(key).value !== "") params.set(key, $(key).value);
    }
    const criterion = $("compare_by").value;
    if (cohortB && (criterion === "wind" || criterion === "weight")) {
      for (const bound of ["min", "max"]) {
        params.delete(`${criterion}_${bound}`);
        if ($(`compare_${bound}`).value !== "") params.set(`${criterion}_${bound}`, $(`compare_${bound}`).value);
      }
    } else if (cohortB && criterion && $("compare_value").value) {
      if (criterion === "runway") {
        const [airport, runway] = JSON.parse($("compare_value").value);
        params.set("airport", airport); params.set("runway", runway);
      } else {
        params.set(criterion, $("compare_value").value);
        if (criterion === "airport") params.delete("runway");
      }
    }
    if ($("individuals").checked) params.set("channels", profileKeys().join(","));
    const ids = state.ids[cohortB ? "b" : "a"];
    if (ids.length) params.set("ids", ids.join(","));
    return params;
  }
  function draftCohortLabel(isB = false) {
    const criterion = $("compare_by").value;
    const aircraft = $("aircraft").value || "All aircraft";
    if (["wind", "weight"].includes(criterion)) {
      const prefix = isB ? "compare" : criterion, unit = criterion === "wind" ? "kt absolute crosswind" : "lb landing weight";
      return `${aircraft} · ${$(`${prefix}_min`).value || "0"}–${$(`${prefix}_max`).value || "∞"} ${unit}`;
    }
    return isB ? $("compare_value").selectedOptions[0]?.textContent : `${aircraft}${["airport", "runway"].includes(criterion) ? ` · ${$("airport").value || "All airports"}/${$("runway").value || "All runways"}` : ""}`;
  }
  function cohortLabel(isB = false) { return state.selection.labels[isB ? 1 : 0]; }
  function comparisonEnabled() { return ["wind", "weight"].includes($("compare_by").value) || Boolean($("compare_by").value && $("compare_value").value); }
  function profileKeys() { return sets[$("channel-set").value].map(key => key === "agl" && $("alignment").value === "agl" ? "gs" : key); }
  function date(value) {
    const match = typeof value === "string" && value.match(/\/Date\((-?\d+)/);
    const d = new Date(match ? Number(match[1]) : value);
    return Number.isFinite(d.getTime()) ? d.toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" }) : "Unknown time";
  }
  async function loadCatalog() {
    const generation = ++state.catalogGeneration;
    ++state.generation; state.catalogPending = true;
    error(""); $("reload").disabled = true;
    $("flight-filters").classList.add("busy"); stop();
    try {
      const firstLoad = !state.catalog;
      const days = encodeURIComponent($("days").value);
      const catalog = await get(`/api/analytics/catalog?days=${days}&page_size=1`);
      if (generation !== state.catalogGeneration) return;
      state.catalog = catalog;
      const filters = catalog.filters;
      options("aircraft", filters.aircraft.map(([name]) => name), "All aircraft");
      if (firstLoad) {
        $("aircraft").value = [...filters.aircraft].filter(([, count]) => count > 0).sort((a, b) => b[1] - a[1])[0]?.[0] || "";
      }
      options("airport", filters.airports, "All airports");
      options("creator", filters.creators, "All recording app versions");
      options("device", filters.devices, "All devices");
      updateRunways(); updateComparison();
      if (!catalog.total) {
        $("flight-status").textContent = state.catalog.index.pending ? `Preparing flight analytics · ${state.catalog.index.indexed} archives indexed, ${state.catalog.index.pending} pending. Refresh to check progress.` : "No recordings with flight analytics in this period.";
      }
      state.catalogPending = false;
      await loadCohorts();
    } catch (e) {
      if (generation === state.catalogGeneration) { error(e.message); $("flight-status").textContent = "Recordings could not be refreshed."; }
    } finally {
      if (generation === state.catalogGeneration) {
        state.catalogPending = false; $("reload").disabled = false; $("flight-filters").classList.remove("busy");
      }
    }
  }
  function updateRunways() {
    options("runway", (state.catalog?.filters?.runways || []).filter(([airport]) => !$("airport").value || airport === $("airport").value).map(([, runway]) => runway), "All runways");
  }
  function updateComparison() {
    const key = $("compare_by").value, previousValue = $("compare_value").value;
    const numeric = ["wind", "weight"].includes(key);
    const filters = state.catalog?.filters;
    const choices = { aircraft: filters?.aircraft.map(([name]) => name), airport: filters?.airports, creator: filters?.creators, device: filters?.devices };
    options("compare_value", choices[key] || [], "Select comparison");
    if (key === "runway") {
      const pairs = (filters?.runways || []).filter(([airport, runway]) => airport && runway).map(pair => JSON.stringify(pair)).sort();
      $("compare_value").replaceChildren(new Option("Select comparison", ""), ...pairs.map(value => new Option(JSON.parse(value).join(" / "), value)));
      if (pairs.includes(previousValue)) $("compare_value").value = previousValue;
    }
    $("compare_value").disabled = !key;
    $("compare_value_field").hidden = numeric;
    for (const bound of ["min", "max"]) {
      $(`compare_${bound}_field`).hidden = !numeric;
      $(`compare_${bound}_label`).textContent = `Cohort B · ${bound === "min" ? "minimum" : "maximum"} ${key === "wind" ? "crosswind · kt" : "weight · lb"}`;
      $(`compare_${bound}`).max = key === "wind" ? 500 : 10000000;
      $(`compare_${bound}`).disabled = !numeric;
    }
  }
  async function loadCohorts() {
    if (!state.catalog || state.catalogPending) return;
    const generation = ++state.generation;
    const queryA = query(), queryB = comparisonEnabled() ? query($("alignment").value, true) : null;
    const selection = { queryA, queryB, labels: [draftCohortLabel(), draftCohortLabel(true)] };
    const previous = state.loading;
    let complete; state.loading = new Promise(resolve => { complete = resolve; });
    $("flight-status").textContent = "Calculating profiles from retained recordings…";
    $("flight-filters").classList.add("busy"); error(""); stop();
    try {
      if (previous) await previous;
      if (generation !== state.generation) return;
      const a = await get(`/api/analytics/cohort?${queryA}`);
      if (generation !== state.generation) return;
      const b = queryB ? await get(`/api/analytics/cohort?${queryB}`) : null;
      if (generation !== state.generation) return;
      state.a = a; state.b = b; state.selection = selection; state.impactA = null; state.impactB = null;
      const scope = `${a.included} of ${a.matched} matching approaches${a.matched > a.limit ? ` · newest ${a.limit}` : ""}`;
      const other = b ? ` · B: ${b.included} of ${b.matched}` : "";
      const progress = a.index.pending ? ` · Indexing ${a.index.pending} remaining archives` : a.index.stale ? " · Metadata snapshot is stale" : "";
      $("flight-status").textContent = scope + other + progress + (state.catalog.analytics_errors ? ` · ${state.catalog.analytics_errors} archives have unavailable analytics` : "");
      $("clear-selection").hidden = !state.ids.a.length && !state.ids.b.length;
      renderKpis(); renderLegend(); populateFlights();
      await render();
    } catch (e) {
      if (generation === state.generation) { error(e.message); $("flight-status").textContent = "Selection could not be loaded. Any charts below retain the previous selection."; }
    } finally { complete(); if (generation === state.generation) $("flight-filters").classList.remove("busy"); }
  }
  const median = FlightMath.median;
  function renderKpis() {
    const members = state.a.members;
    const values = [["Approaches", members.length, `${new Set(members.map(r => r.airport).filter(Boolean)).size} airports`],
      ["Median descent at contact", median(members.map(r => r.fpm)), "ft/min · positive down"],
      ["Median peak load", median(members.map(r => r.g)), "G · adaptive contact window"],
      ["Median recorded height", median(members.map(r => r.height_max)), "ft AGL · available coverage"],
      ["Physical control coverage", members.filter(r => r.channels.includes("stickP")).length, "approaches with pitch input"]];
    $("flight-kpis").replaceChildren(...values.map(([label, value, note]) => {
      const node = element("article", "flight-kpi"); node.append(element("span", "label", label), element("span", "value", fmt(value, label.includes("load") ? 2 : 0)), element("span", "note", note)); return node;
    }));
  }
  function renderLegend() {
    $("cohort-legend").replaceChildren(element("span", "cohort-a", `● A · ${cohortLabel()} · ${state.a.included} approaches`));
    if (state.b) $("cohort-legend").append(element("span", "cohort-b", `● B · ${cohortLabel(true)} · ${state.b.included} approaches`));
    $("cohort-legend").append(element("span", "", "Line: median · inner band: 10–90% · outer band: min–max"));
    if ($("individuals").checked) $("cohort-legend").append(element("span", "", `Thin paths: newest ${state.a.individuals.length} A${state.b ? ` / ${state.b.individuals.length} B` : ""} · click to inspect`));
    if (state.selected) $("cohort-legend").append(element("span", "selected-label", "Gold: selected flight"));
  }
  function domain(values, includeZero = false) {
    values = values.filter(valid);
    if (!values.length) return [0, 1];
    let low = Math.min(...values), high = Math.max(...values);
    if (includeZero) { low = Math.min(low, 0); high = Math.max(high, 0); }
    const pad = (high - low || Math.abs(high) || 1) * .07;
    return [low - pad, high + pad];
  }
  function chartFrame(parent, xDomain, yDomain, xLabel, yLabel, wide = false) {
    const width = wide ? 1120 : 640, height = wide ? 440 : 280;
    const margin = { left: 62, right: 24, top: 20, bottom: 48 };
    const root = svg("svg", { viewBox: `0 0 ${width} ${height}`, class: "flight-plot", role: "img", "aria-label": `${yLabel} by ${xLabel}` });
    const x = value => margin.left + (value - xDomain[0]) / (xDomain[1] - xDomain[0]) * (width - margin.left - margin.right);
    const y = value => height - margin.bottom - (value - yDomain[0]) / (yDomain[1] - yDomain[0]) * (height - margin.top - margin.bottom);
    for (let i = 0; i <= 5; i++) {
      const vx = xDomain[0] + i / 5 * (xDomain[1] - xDomain[0]), vy = yDomain[0] + i / 5 * (yDomain[1] - yDomain[0]);
      root.append(svg("line", { x1: x(vx), x2: x(vx), y1: margin.top, y2: height - margin.bottom, class: "gridline" }), svg("text", { x: x(vx), y: height - margin.bottom + 19, "text-anchor": "middle" }, fmt(vx, Math.abs(xDomain[1] - xDomain[0]) < 10 ? 2 : 0)));
      root.append(svg("line", { x1: margin.left, x2: width - margin.right, y1: y(vy), y2: y(vy), class: "gridline" }), svg("text", { x: margin.left - 10, y: y(vy) + 3, "text-anchor": "end" }, fmt(vy, Math.abs(yDomain[1] - yDomain[0]) < 10 ? 2 : 0)));
    }
    if (yDomain[0] <= 0 && yDomain[1] >= 0) root.append(svg("line", { x1: margin.left, x2: width - margin.right, y1: y(0), y2: y(0), class: "zero-line" }));
    root.append(svg("text", { x: (width + margin.left) / 2, y: height - 7, "text-anchor": "middle" }, xLabel));
    parent.append(root);
    const pointer = event => { const point = root.createSVGPoint(); point.x = event.clientX; point.y = event.clientY; return point.matrixTransform(root.getScreenCTM().inverse()); };
    return { root, x, y, width, height, margin, xDomain, pointer,
      valueX: px => xDomain[0] + (px - margin.left) / (width - margin.left - margin.right) * (xDomain[1] - xDomain[0]) };
  }
  function segments(values, predicate) {
    const groups = []; let group = [];
    for (let i = 0; i < values.length; i++) { if (predicate(values[i], i)) group.push(i); else if (group.length) { groups.push(group); group = []; } }
    if (group.length) groups.push(group); return groups;
  }
  function pathLine(chart, xs, values, predicate = value => valid(value)) {
    return segments(values, predicate).map(group => group.map((i, j) => `${j ? "L" : "M"}${chart.x(xs[i]).toFixed(2)},${chart.y(values[i]).toFixed(2)}`).join(" ")).join(" ");
  }
  function bandPath(chart, grid, bands, lower, upper) {
    return segments(bands, value => value !== null).map(group => group.map((i, j) => `${j ? "L" : "M"}${chart.x(grid[i]).toFixed(2)},${chart.y(bands[i][lower]).toFixed(2)}`).join(" ") + " " + [...group].reverse().map(i => `L${chart.x(grid[i]).toFixed(2)},${chart.y(bands[i][upper]).toFixed(2)}`).join(" ") + " Z").join(" ");
  }
  function axisLabel(axis) { return { agl: "Height above ground · ft (descending →)", t: "Time from first contact · s", along: "Distance past landing threshold · m", wind: "Crosswind at contact · kt (+ from right)" }[axis]; }
  function bandChart(parent, key, a, b, xDomain) {
    const info = (a.axis === "wind" && metric[key] ? { label: metric[key][0], unit: metric[key][1] } : state.catalog.channels[key]) || { label: key.startsWith("gear_") ? `Contact point ${key.slice(5)} compression` : key, unit: key.startsWith("gear_") ? "recorded fraction" : "" };
    const card = element("article", "panel plot-card"), heading = element("div", "panel-heading");
    heading.append(element("h3", "", info.label), element("span", "plot-unit", info.unit)); card.append(heading); parent.append(card);
    const aBands = a.bands[key] || a.grid.map(() => null), bBands = b?.bands[key] || [];
    const selected = state.flight?.profiles?.[a.axis]?.[key] || [];
    const individuals = $("individuals").checked ? [...(a.individuals || []).map(row => ({ ...row, cohort: "a" })), ...(b?.individuals || []).map(row => ({ ...row, cohort: "b" }))] : [];
    const shown = values => values.filter((_, i) => a.grid[i] >= Math.min(...xDomain) && a.grid[i] <= Math.max(...xDomain));
    const values = [...shown(aBands), ...shown(bBands)].filter(Boolean).flatMap(v => $("extremes").checked ? [v[1], v[5]] : [v[2], v[4]]);
    values.push(...shown(selected).filter(valid), ...individuals.flatMap(row => shown(row.channels[key] || []).filter(valid)));
    if (a.axis === "wind") values.push(...[...a.members, ...(b?.members || [])].filter(row => valid(row.crosswind)).map(row => row[key]).filter(valid));
    if (!values.length) { card.append(element("div", "plot-empty", "No valid observations for this channel")); return; }
    const chart = chartFrame(card, xDomain, domain(values), axisLabel(a.axis), info.label);
    const trim = values => values.map((value, i) => a.grid[i] >= Math.min(...xDomain) && a.grid[i] <= Math.max(...xDomain) ? value : null);
    for (const [bands, code] of [[aBands, "a"], [bBands, "b"]]) {
      if (!bands.length) continue;
      const trimmed = bands.map((v, i) => a.grid[i] >= Math.min(...xDomain) && a.grid[i] <= Math.max(...xDomain) ? v : null);
      if ($("extremes").checked) chart.root.append(svg("path", { d: bandPath(chart, a.grid, trimmed, 1, 5), class: `extent-${code}` }));
      chart.root.append(svg("path", { d: bandPath(chart, a.grid, trimmed, 2, 4), class: `band-${code}` }), svg("path", { d: pathLine(chart, a.grid, trimmed.map(v => v?.[3] ?? null)), class: `median-${code}` }));
    }
    for (const row of individuals) {
      const path = svg("path", { d: pathLine(chart, a.grid, trim(row.channels[key] || [])), class: `individual-path cohort-${row.cohort}`, tabindex: 0, role: "button", "aria-label": `Inspect flight ${row.id}` });
      path.addEventListener("click", () => selectFlight(row.id)); path.addEventListener("keydown", event => { if (event.key === "Enter") selectFlight(row.id); }); chart.root.append(path);
    }
    if (selected.length) chart.root.append(svg("path", { d: pathLine(chart, a.grid, trim(selected)), class: "selected-path" }));
    if (a.axis === "wind") for (const [cohort, code] of [[a, "a"], [b, "b"]]) {
      for (const row of cohort?.members || []) if (valid(row.crosswind) && valid(row[key])) {
        const dot = svg("circle", { cx: chart.x(row.crosswind), cy: chart.y(row[key]), r: 4, fill: row.id === state.selected ? "#f2cf85" : code === "a" ? A : B, class: "flight-point", tabindex: 0, role: "button", "aria-label": `Inspect ${row.aircraft}, ${fmt(row.crosswind)} kt crosswind` });
        dot.append(svg("title", {}, `${code.toUpperCase()} · ${row.aircraft} · ${date(row.timestamp)}\nCrosswind ${fmt(row.crosswind)} kt · ${info.label} ${fmt(row[key], 2)} ${info.unit}`));
        activate(dot, () => selectFlight(row.id)); chart.root.append(dot);
      }
    }
    const line = svg("line", { y1: chart.margin.top, y2: chart.height - chart.margin.bottom, class: "hover-line", visibility: "hidden" }); chart.root.append(line);
    const readout = element("div", "plot-readout", "Move over the chart to inspect median, range and sample count."); card.append(readout);
    const update = value => {
      let i = 0; for (let j = 1; j < a.grid.length; j++) if (Math.abs(a.grid[j] - value) < Math.abs(a.grid[i] - value)) i = j;
      line.setAttribute("x1", chart.x(a.grid[i])); line.setAttribute("x2", chart.x(a.grid[i])); line.setAttribute("visibility", "visible");
      const describe = (v, name) => v ? `${name} ${fmt(v[3], 2)} ${info.unit} · min ${fmt(v[1], 2)} / max ${fmt(v[5], 2)} · n=${v[0]}` : `${name} no observations`;
      const coordinate = a.axis === "wind" ? `${fmt(a.grid[i] - a.windWidth / 2)}–${fmt(a.grid[i] + a.windWidth / 2)} kt` : `${fmt(a.grid[i])} ${a.axis === "agl" ? "ft" : a.axis === "t" ? "s" : "m"}`;
      readout.textContent = `${coordinate} | ${describe(aBands[i], "A")}${b ? " | " + describe(bBands[i], "B") : ""}${selected.length ? ` | Selected ${fmt(selected[i], 2)} ${info.unit}` : ""}`;
    };
    state.plots.push(update);
    chart.root.addEventListener("pointermove", event => { const p = chart.pointer(event); if (p.x >= chart.margin.left && p.x <= chart.width - chart.margin.right) state.plots.forEach(fn => fn(chart.valueX(p.x))); });
  }
  function renderProfiles() {
    state.plots = []; $("profiles").replaceChildren();
    const a = state.a, b = state.b;
    const active = a.grid.filter((x, i) => [...Object.values(a.bands), ...Object.values(b?.bands || {})].some(bands => bands[i]));
    let limits = active.length ? [Math.min(...active), Math.max(...active)] : [0, 1];
    if (limits[0] === limits[1]) limits[1] += 1;
    if ($("channel-set").value === "flare") {
      for (const option of $("flare-window").options) {
        const value = Number(option.value);
        option.textContent = a.axis === "agl" ? `0–${value} ft` : a.axis === "t" ? `−${value / 10} to +2 s` : `−${value * 10} to +${value * 5} m`;
      }
      const zoom = Number($("flare-window").value);
      limits = a.axis === "agl" ? [0, zoom] : a.axis === "t" ? [-zoom / 10, 2] : [-zoom * 10, zoom * 5];
    }
    if (a.axis === "agl") limits.reverse();
    renderLegend();
    for (const key of profileKeys()) bandChart($("profiles"), key, a, b, limits);
  }
  function chooseTab(tab) {
    state.tab = tab; stop();
    const presets = { profiles: "approach", flare: "flare", response: $("response-axis").value };
    const profile = tab in presets;
    if (profile) $("channel-set").value = presets[tab];
    $("profile-title").textContent = { profiles: "Aircraft profile", flare: "Flare close-up", response: "Control input → command → aircraft response" }[tab] || "Aircraft profile";
    $("profile-description").textContent = tab === "flare" ? "Compare sink arrest, pitch, thrust and stick through the final seconds and metres." : tab === "response" ? "Recorded hardware, simulator command and control surface retain their timing and amplitude." : "One contribution per approach at each coordinate.";
    $("profile-channels").hidden = tab !== "profiles"; $("flare-options").hidden = tab !== "flare"; $("response-options").hidden = tab !== "response";
    document.querySelectorAll("[data-tab]").forEach(node => node.setAttribute("aria-current", node.dataset.tab === tab ? "page" : "false"));
    document.querySelectorAll(".flight-view").forEach(node => { node.hidden = node.id !== `view-${profile ? "profiles" : tab}`; });
    if (profile && $("individuals").checked) { loadCohorts(); return; }
    render().catch(e => error(e.message));
  }
  async function render() {
    if (!state.a) return;
    if (["profiles", "flare", "response"].includes(state.tab)) renderProfiles();
    if (state.tab === "approaches") { await renderWorld(); renderLocalMap(); renderCorridor(); }
    if (state.tab === "touchdown") renderFootprint();
    if (state.tab === "signature") { renderContactTimeline(); await renderImpact(); }
    if (state.tab === "wind") renderWind();
    if (state.tab === "relationships") renderScatter();
    if (state.tab === "comparison") renderDistributions();
    if (state.tab === "replay") renderReplay();
  }
  async function renderWorld() {
    if (!state.land) state.land = await get("/static/land-110m.geojson");
    const root = svg("svg", { viewBox: "0 0 1000 460", role: "img", "aria-label": "Recorded landing airports on a world map" });
    const x = lon => (lon + 180) / 360 * 1000, y = lat => (90 - lat) / 180 * 460;
    for (let lon = -180; lon <= 180; lon += 30) root.append(svg("line", { x1: x(lon), x2: x(lon), y1: 0, y2: 460, class: "graticule" }));
    for (let lat = -60; lat <= 60; lat += 30) root.append(svg("line", { x1: 0, x2: 1000, y1: y(lat), y2: y(lat), class: "graticule" }));
    for (const feature of state.land.features) {
      const polygons = feature.geometry.type === "Polygon" ? [feature.geometry.coordinates] : feature.geometry.coordinates;
      for (const polygon of polygons) root.append(svg("path", { class: "land", d: polygon.map(ring => ring.map(([lon, lat], i) => `${i ? "L" : "M"}${x(lon).toFixed(2)},${y(lat).toFixed(2)}`).join(" ") + " Z").join(" ") }));
    }
    const airports = new Map();
    const members = [...new Map([...state.a.members, ...(state.b?.members || [])].map(member => [member.id, member])).values()];
    for (const member of members) if (valid(member.lat) && valid(member.lon)) {
      const key = member.airport || `${member.lat.toFixed(2)},${member.lon.toFixed(2)}`;
      if (!airports.has(key)) airports.set(key, []); airports.get(key).push(member);
    }
    const label = svg("text", { x: 20, y: 28, class: "map-tooltip" }, "Select an airport to focus the analysis");
    for (const [airport, members] of airports) {
      const point = svg("circle", { cx: x(members[0].lon), cy: y(members[0].lat), r: Math.min(14, 3 + Math.sqrt(members.length)), class: "airport-dot", tabindex: 0, role: "button", "aria-label": `${airport}: ${members.length} approaches` });
      point.append(svg("title", {}, `${airport} · ${members.length} approaches`));
      point.addEventListener("pointerenter", () => { label.textContent = `${airport} · ${members.length} approaches · median ${fmt(median(members.map(m => m.fpm)))} ft/min`; });
      const select = () => { if (members[0].airport) { $("airport").value = members[0].airport; $("runway").value = ""; updateRunways(); state.ids = { a: [], b: [] }; return loadCohorts(); } };
      point.addEventListener("click", select); point.addEventListener("keydown", e => { if (e.key === "Enter") select(); }); root.append(point);
    }
    root.append(label); $("world-map").replaceChildren(root);
  }
  function trackPath(chart, track, xKey, yKey) {
    const values = track[yKey];
    let path = "", previous = -1;
    values.forEach((value, i) => {
      if (!valid(value) || !valid(track[xKey][i])) { previous = -1; return; }
      const connected = previous >= 0 && track.run[i] === track.run[previous] && track.t[i] > track.t[previous];
      path += `${connected ? "L" : "M"}${chart.x(track[xKey][i]).toFixed(2)},${chart.y(value).toFixed(2)} `; previous = i;
    });
    return path;
  }
  function cohortTracks() { return [...state.a.tracks.map(track => ({ ...track, cohort: "a" })), ...(state.b?.tracks || []).map(track => ({ ...track, cohort: "b" }))]; }
  function activate(node, action) {
    node.addEventListener("click", action);
    node.addEventListener("keydown", event => { if (event.key === "Enter") action(); });
  }
  function selectPoints(ids, cohort = "a") { state.ids[cohort] = ids; return loadCohorts(); }
  function heatCells(chart, cells, colourKey, bounds, population, cohort) {
    for (const cell of cells) {
      const value = colourKey === "count" ? cell.count / population * 100 : cell.value;
      const rect = svg("rect", { x: chart.x(cell.x), y: chart.y(cell.y + cell.height), width: Math.max(1, chart.x(cell.x + cell.width) - chart.x(cell.x)), height: Math.max(1, chart.y(cell.y) - chart.y(cell.y + cell.height)), fill: colour(value, bounds), class: "density-cell", tabindex: 0, role: "button", "aria-label": `${cell.count} approaches in cell; select these flights` });
      rect.append(svg("title", {}, `${cell.count} approaches · ${fmt(cell.count / population * 100)}% of this view\n${colourKey === "count" ? "One vote per approach" : `Median ${fmt(cell.value, 2)} · ${cell.valueCount} valid observations`}`));
      activate(rect, () => selectPoints(cell.ids, cohort)); chart.root.append(rect);
    }
  }
  function drawTrajectory(chart, track, colourKey, bounds) {
    const attrs = { tabindex: 0, role: "button", "aria-label": `Inspect flight ${track.id}` };
    const add = (d, stroke) => {
      const path = svg("path", { d, class: `trajectory cohort-${track.cohort}${track.id === state.selected ? " active" : ""}`, ...attrs });
      if (stroke) { path.setAttribute("stroke", stroke); path.classList.remove("trajectory"); path.classList.add("metric-path"); }
      activate(path, () => selectFlight(track.id)); chart.root.append(path);
    };
    if (colourKey === "cohort" || track.id === state.selected) { add(trackPath(chart, track, "u", "v")); return; }
    const segments = new Map();
    for (let i = 1; i < track.t.length; i++) {
      if (![track.u[i - 1], track.u[i], track.v[i - 1], track.v[i]].every(valid) || track.run[i] !== track.run[i - 1] || track.t[i] <= track.t[i - 1]) continue;
      const values = [track[colourKey]?.[i - 1], track[colourKey]?.[i]], value = values.every(valid) ? (values[0] + values[1]) / 2 : null;
      const bucket = valid(value) ? Math.max(0, Math.min(15, Math.floor((value - bounds[0]) / (bounds[1] - bounds[0] || 1) * 16))) : -1;
      const d = `M${chart.x(track.u[i - 1])},${chart.y(track.v[i - 1])} L${chart.x(track.u[i])},${chart.y(track.v[i])} `;
      segments.set(bucket, (segments.get(bucket) || "") + d);
    }
    for (const [bucket, d] of segments) add(d, colour(bucket < 0 ? null : bounds[0] + (bucket + .5) / 16 * (bounds[1] - bounds[0]), bounds));
  }
  function trajectoryView(parent, tracks, xDomain, yDomain, xLabel, yLabel, backdrop) {
    tracks = tracks.filter(track => track.u.some((value, i) => valid(value) && valid(track.v[i])));
    if (!tracks.length) { parent.append(element("div", "plot-empty", "No complete trajectories for this projection.")); return; }
    const colourKey = $("corridor-colour").value, density = $("corridor-display").value === "density";
    const metricBounds = domain(tracks.flatMap(track => track[colourKey] || []));
    const groups = density ? ["a", ...(state.b ? ["b"] : [])] : [null];
    for (const cohort of groups) {
      const display = cohort ? tracks.filter(track => track.cohort === cohort) : tracks;
      if (cohort) parent.append(element("p", `density-heading cohort-${cohort}`, `${cohort.toUpperCase()} · ${cohortLabel(cohort === "b")} · ${display.length} trajectories`));
      const chart = chartFrame(parent, xDomain, yDomain, xLabel, yLabel, true);
      if (backdrop) backdrop(chart);
      if (density) {
        const cells = FlightMath.density(display.map(track => ({ id: track.id, points: track.t.map((t, i) => ({ t, run: track.run[i], x: track.u[i], y: track.v[i], value: track[colourKey]?.[i] })) })), xDomain, yDomain);
        heatCells(chart, cells, colourKey === "cohort" ? "count" : colourKey, colourKey === "cohort" ? [0, 100] : metricBounds, display.length, cohort);
      } else for (const track of display) drawTrajectory(chart, track, colourKey, metricBounds);
      const note = colourKey === "cohort" ? density ? "Colour: share of approaches crossing each cell · 0–100% · click a cell to filter" : "Cyan: A · orange: B · gold: selected flight" : `${state.catalog.channels[colourKey].label}: ${fmt(metricBounds[0])}–${fmt(metricBounds[1])} ${state.catalog.channels[colourKey].unit} · ${density ? "median per occupied cell" : "colour along each trajectory"}`;
      parent.append(element("p", "plot-footnote", note));
    }
  }
  function renderLocalMap() {
    const parent = $("local-map"); parent.replaceChildren();
    const members = [...state.a.members, ...(state.b?.members || [])], airports = new Set(members.map(member => member.airport).filter(Boolean));
    const origin = members.find(member => valid(member.lat) && valid(member.lon));
    if (airports.size !== 1 || !origin) { parent.append(element("div", "plot-empty", "Choose one airport to see its geographic approach tracks.")); return; }
    const radians = Math.PI / 180, radius = 6371008.8;
    const project = (lat, lon) => valid(lat) && valid(lon) ? [((lon - origin.lon + 540) % 360 - 180) * radians * radius * Math.cos(origin.lat * radians), (lat - origin.lat) * radians * radius] : [null, null];
    const tracks = cohortTracks().map(track => {
      const positions = track.lat.map((lat, i) => project(lat, track.lon[i]));
      return { ...track, u: positions.map(p => p[0]), v: positions.map(p => p[1]) };
    }).filter(track => track.u.some(valid));
    const runways = [...new Map(members.filter(m => m.frame).map(m => [`${m.airport}/${m.runway}`, m])).values()].map(member => {
      const f = member.frame, centre = project(f.lat, f.lon), h = f.heading * radians;
      return { name: member.runway, corners: [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([a, c]) => [centre[0] + a * f.length / 2 * Math.sin(h) + c * f.width / 2 * Math.cos(h), centre[1] + a * f.length / 2 * Math.cos(h) - c * f.width / 2 * Math.sin(h)]) };
    });
    const xDomain = domain([...tracks.flatMap(t => t.u), ...runways.flatMap(r => r.corners.map(p => p[0]))]), yDomain = domain([...tracks.flatMap(t => t.v), ...runways.flatMap(r => r.corners.map(p => p[1]))]);
    const spanX = xDomain[1] - xDomain[0], spanY = yDomain[1] - yDomain[0], ratio = 1034 / 372;
    if (spanX / spanY < ratio) { const pad = (spanY * ratio - spanX) / 2; xDomain[0] -= pad; xDomain[1] += pad; }
    else { const pad = (spanX / ratio - spanY) / 2; yDomain[0] -= pad; yDomain[1] += pad; }
    trajectoryView(parent, tracks, xDomain, yDomain, "East · m", "North · m", chart => {
      for (const runway of runways) {
        const path = svg("path", { d: runway.corners.map(([x, y], i) => `${i ? "L" : "M"}${chart.x(x)},${chart.y(y)}`).join(" ") + " Z", class: "runway-outline" });
        path.append(svg("title", {}, `Recorded runway geometry · ${runway.name}`)); chart.root.append(path);
      }
      chart.root.append(svg("text", { x: 75, y: 32, class: "map-tooltip" }, `${origin.airport} · north ↑ · equal horizontal and vertical scale`));
    });
    parent.append(element("p", "plot-footnote", `Local geographic projection, origin ${fmt(origin.lat, 5)}°, ${fmt(origin.lon, 5)}°. Runway outlines use recorded geometry; no external map requests.`));
  }
  function renderCorridor() {
    const parent = $("corridor"); parent.replaceChildren();
    const mode = $("corridor-mode").value;
    const tracks = cohortTracks().filter(t => t.along.some(valid));
    if (!tracks.length) { parent.append(element("div", "plot-empty", "No trajectories with complete runway geometry in this selection")); return; }
    const display = tracks.map(track => {
      const u = [], v = [];
      track.along.forEach((along, i) => {
        const cross = track.cross[i], height = track.agl[i];
        u.push(mode === "3d" ? valid(along) && valid(cross) ? along * .85 + cross * .5 : null : along);
        v.push(mode === "side" ? height : mode === "3d" ? valid(height) && valid(cross) && valid(along) ? height * .3048 * 6 + cross * .85 - along * .18 : null : cross);
      }); return { ...track, u, v };
    });
    trajectoryView(parent, display, domain(display.flatMap(t => t.u)), domain(display.flatMap(t => t.v), true), mode === "3d" ? "Runway-relative 3D projection · vertical scale ×6" : "Distance past threshold · m", mode === "plan" ? "Right of centreline · m" : mode === "3d" ? "Projected position · m" : "Height above ground · ft");
  }
  function colour(value, bounds) {
    if (!valid(value)) return "#74858b";
    const fraction = Math.max(0, Math.min(1, (value - bounds[0]) / (bounds[1] - bounds[0] || 1)));
    return `rgb(${Math.round(92 + fraction * 163)},${Math.round(204 - fraction * 65)},${Math.round(218 - fraction * 120)})`;
  }
  function scatterChart(parent, members, xKey, yKey, colourKey, brush = false) {
    parent.replaceChildren();
    const usable = members.filter(m => valid(m[xKey]) && valid(m[yKey]));
    if (!usable.length) { parent.append(element("div", "plot-empty", "No paired observations for the selected axes")); return; }
    const chart = chartFrame(parent, domain(usable.map(m => m[xKey])), domain(usable.map(m => m[yKey])), `${metric[xKey][0]} · ${metric[xKey][1]}`, metric[yKey][0], true);
    const bounds = domain(usable.map(m => m[colourKey]));
    const label = svg("text", { x: chart.margin.left, y: 13, class: "map-tooltip" }, `${metric[yKey][0]} · ${metric[yKey][1]} | colour: ${metric[colourKey][0]} (${fmt(bounds[0])}–${fmt(bounds[1])} ${metric[colourKey][1]})`);
    chart.root.append(label);
    for (const member of usable) {
      const point = svg("circle", { cx: chart.x(member[xKey]), cy: chart.y(member[yKey]), r: 5, fill: colour(member[colourKey], bounds), class: `flight-point${state.selected === member.id ? " active" : ""}`, tabindex: 0, role: "button", "aria-label": `${member.aircraft}, ${member.airport}, ${fmt(member[xKey])}, ${fmt(member[yKey])}` });
      point.append(svg("title", {}, `${member.aircraft} · ${member.airport}/${member.runway} · ${date(member.timestamp)}\n${metric[xKey][0]}: ${fmt(member[xKey], 2)}\n${metric[yKey][0]}: ${fmt(member[yKey], 2)}\n${metric[colourKey][0]}: ${fmt(member[colourKey], 2)}`));
      point.addEventListener("click", () => selectFlight(member.id)); point.addEventListener("keydown", e => { if (e.key === "Enter") selectFlight(member.id); }); chart.root.append(point);
    }
    if (brush) {
      let start = null, rectangle = null;
      chart.root.addEventListener("pointerdown", event => {
        if (event.target.classList.contains("flight-point")) return;
        start = chart.pointer(event); rectangle = svg("rect", { x: start.x, y: start.y, width: 0, height: 0, class: "brush" }); chart.root.append(rectangle); chart.root.setPointerCapture(event.pointerId);
      });
      chart.root.addEventListener("pointermove", event => {
        if (!start) return; const p = chart.pointer(event);
        for (const [key, value] of Object.entries({ x: Math.min(start.x, p.x), y: Math.min(start.y, p.y), width: Math.abs(p.x - start.x), height: Math.abs(p.y - start.y) })) rectangle.setAttribute(key, value);
      });
      chart.root.addEventListener("pointerup", event => {
        if (!start) return; const p = chart.pointer(event), first = start; start = null; rectangle.remove();
        if (Math.abs(p.x - first.x) < 5 || Math.abs(p.y - first.y) < 5) return;
        const ids = usable.filter(m => chart.x(m[xKey]) >= Math.min(first.x, p.x) && chart.x(m[xKey]) <= Math.max(first.x, p.x) && chart.y(m[yKey]) >= Math.min(first.y, p.y) && chart.y(m[yKey]) <= Math.max(first.y, p.y)).map(m => m.id);
        if (ids.length) selectPoints(ids);
      });
      chart.root.addEventListener("pointercancel", () => { start = null; rectangle?.remove(); });
    }
  }
  function renderFootprint() {
    const parent = $("footprint"); parent.replaceChildren();
    const groups = [state.a, ...(state.b ? [state.b] : [])].map(cohort => cohort.members.filter(member => valid(member.along) && valid(member.cross)));
    const all = groups.flat(), key = $("touch-colour").value;
    $("footprint-count").textContent = `${groups[0].length} A${state.b ? ` / ${groups[1].length} B` : ""} georeferenced contacts`;
    if (!all.length) { parent.append(element("div", "plot-empty", "No contacts with complete runway geometry in this selection.")); return; }
    const singleRunway = new Set(all.map(member => `${member.airport}/${member.runway}`)).size === 1;
    const frame = singleRunway ? all.find(member => member.frame)?.frame : null;
    const xDomain = domain([...all.map(member => member.along), ...(frame ? [-frame.offset, frame.length - frame.offset] : [])]);
    const yDomain = domain([...all.map(member => member.cross), ...(frame ? [-frame.width / 2, frame.width / 2] : [])]);
    const bounds = key === "count" ? [0, 100] : domain(all.map(member => member[key]));
    groups.forEach((members, group) => {
      const code = group ? "b" : "a";
      if (state.b) parent.append(element("p", `density-heading cohort-${code}`, `${code.toUpperCase()} · ${cohortLabel(Boolean(group))}`));
      const chart = chartFrame(parent, xDomain, yDomain, "Distance past landing threshold · m", "Right of centreline · m", true);
      if (frame) {
        chart.root.append(svg("rect", { x: chart.x(-frame.offset), y: chart.y(frame.width / 2), width: chart.x(frame.length - frame.offset) - chart.x(-frame.offset), height: chart.y(-frame.width / 2) - chart.y(frame.width / 2), class: "runway-outline" }));
        chart.root.append(svg("line", { x1: chart.x(0), x2: chart.x(0), y1: chart.y(-frame.width / 2), y2: chart.y(frame.width / 2), class: "threshold-line" }));
      }
      if ($("touch-display").value === "density") {
        const cells = FlightMath.density(members.map(member => ({ id: member.id, points: [{ x: member.along, y: member.cross, value: member[key] }] })), xDomain, yDomain, 48, 24, false);
        heatCells(chart, cells, key, bounds, members.length, code);
      } else for (const member of members) {
        const dot = svg("circle", { cx: chart.x(member.along), cy: chart.y(member.cross), r: 5, fill: key === "count" ? group ? B : A : colour(member[key], bounds), class: `flight-point${member.id === state.selected ? " active" : ""}`, tabindex: 0, role: "button", "aria-label": `Inspect flight ${member.id}` });
        dot.append(svg("title", {}, `${member.aircraft} · ${member.airport}/${member.runway}\n${fmt(member.along)} m past threshold · ${fmt(member.cross)} m right of centreline${key === "count" ? "" : `\n${metric[key][0]}: ${fmt(member[key], 2)} ${metric[key][1]}`}`));
        activate(dot, () => selectFlight(member.id)); chart.root.append(dot);
      }
      parent.append(element("p", "plot-footnote", key === "count" ? "Heat colour: share of this cohort's georeferenced contacts · 0–100%. Click a cell to filter; switch to individual contacts to open a flight." : `Cell colour: median ${metric[key][0].toLowerCase()} · ${fmt(bounds[0])}–${fmt(bounds[1])} ${metric[key][1]}. Grey means unavailable.`));
    });
    if (frame) parent.append(element("p", "plot-footnote", `Runway outline: ${all[0].airport}/${all[0].runway} · recorded ${fmt(frame.length)} × ${fmt(frame.width)} m · displaced threshold ${fmt(frame.offset)} m.`));
  }
  async function renderImpact() {
    const generation = state.generation;
    if (!state.impactA) {
      const a = state.a, b = state.b;
      const queryA = new URLSearchParams(state.selection.queryA), queryB = new URLSearchParams(state.selection.queryB);
      queryA.set("axis", "t"); queryB.set("axis", "t");
      const channels = ["g", "latG", ...new Set([...Object.keys(a.bands), ...Object.keys(b?.bands || {})].filter(key => key.startsWith("gear_")))].slice(0, 8).join(",");
      if ($("individuals").checked) { queryA.set("channels", channels); queryB.set("channels", channels); }
      const impactA = a.axis === "t" && !$("individuals").checked ? a : await get(`/api/analytics/cohort?${queryA}`);
      const impactB = b ? b.axis === "t" && !$("individuals").checked ? b : await get(`/api/analytics/cohort?${queryB}`) : null;
      if (generation !== state.generation) return;
      state.impactA = impactA; state.impactB = impactB;
    }
    if (generation !== state.generation || state.tab !== "signature") return;
    state.plots = []; $("impact").replaceChildren();
    for (const key of ["g", "latG", ...new Set([...Object.keys(state.impactA.bands), ...Object.keys(state.impactB?.bands || {})].filter(k => k.startsWith("gear_")))]) bandChart($("impact"), key, state.impactA, state.impactB, [-2, 15]);
  }
  function renderScatter() {
    scatterChart($("scatter"), state.a.members, $("scatter-x").value, $("scatter-y").value, $("scatter-colour").value, true);
    $("scatter-note").textContent = `One point per first contact · ${state.a.included} approaches in cohort A · observed associations do not establish causation.`;
  }
  function renderWind() {
    const parent = $("wind-charts"); parent.replaceChildren(); state.plots = [];
    $("wind-legend").replaceChildren(element("span", "cohort-a", `● A · ${cohortLabel()} · ${state.a.included} approaches`));
    if (state.b) $("wind-legend").append(element("span", "cohort-b", `● B · ${cohortLabel(true)} · ${state.b.included} approaches`));
    $("wind-legend").append(element("span", "", "Line: median · inner band: 10–90% · outer band: min–max · gold: selected flight"));
    const keys = ["crab", "bank", "stickY", "cmdY", "latG", "g"];
    const data = FlightMath.windProfiles([state.a.members, ...(state.b ? [state.b.members] : [])], keys);
    if (!data) { parent.append(element("div", "plot-empty", "Crosswind at contact requires a known runway direction and recorded wind.")); return; }
    for (const key of keys) bandChart(parent, key, data.cohorts[0], data.cohorts[1], data.limits);
  }
  function timeline(parent, rows) {
    parent.replaceChildren();
    if (!rows.length) { parent.append(element("div", "plot-empty", "No recorded contact-state observations. Select a flight to inspect its available data.")); return; }
    const values = rows.flatMap(row => [...(row.events || []).flatMap(event => [event.t, event.t + (event.window || 0)]), ...(row.intervals || []).flatMap(interval => [interval.start, interval.end])]);
    const limits = domain(values, true), width = 1100, height = rows.length * 27 + 65, left = 210, right = 30;
    const x = value => left + (value - limits[0]) / (limits[1] - limits[0]) * (width - left - right);
    const root = svg("svg", { viewBox: `0 0 ${width} ${height}`, class: "flight-plot contact-sequence", role: "img", "aria-label": "Observed contact timeline, seconds from first contact" });
    for (let i = 0; i <= 6; i++) {
      const value = limits[0] + i * (limits[1] - limits[0]) / 6;
      root.append(svg("line", { x1: x(value), x2: x(value), y1: 20, y2: height - 36, class: "gridline" }), svg("text", { x: x(value), y: height - 16, "text-anchor": "middle" }, `${fmt(value, 2)} s`));
    }
    rows.forEach((row, i) => {
      const y = 30 + i * 27;
      const label = svg("text", { x: 12, y: y + 4 }, row.label); root.append(label);
      if (row.id) { label.setAttribute("class", "contact-label"); activate(label, () => selectFlight(row.id)); }
      for (const interval of row.intervals || []) {
        const bar = svg("rect", { x: x(interval.start), y: y - 6, width: Math.max(.5, x(interval.end) - x(interval.start)), height: 12, fill: interval.ground ? A : "#354951" });
        bar.append(svg("title", {}, `${row.label}: ${interval.ground ? "On ground" : "Airborne"} · ${fmt(interval.start, 3)}–${fmt(interval.end, 3)} s`)); root.append(bar);
      }
      for (const event of row.events || []) {
        if (valid(event.window) && event.window > 0) root.append(svg("rect", { x: x(event.t), y: y - 5, width: x(event.t + event.window) - x(event.t), height: 10, class: `contact-window cohort-${row.cohort || "a"}` }));
        const dot = svg("circle", { cx: x(event.t), cy: y, r: 5, fill: row.id === state.selected ? "#f2cf85" : row.cohort === "b" ? B : A, class: "flight-point", tabindex: 0, role: "button", "aria-label": `${row.label}, contact ${event.contact} at ${fmt(event.t, 3)} seconds` });
        dot.append(svg("title", {}, `${row.label} · contact ${event.contact}\n${fmt(event.t, 3)} s · ${fmt(event.g, 2)} G\nRecorded analysis window ${fmt(event.window, 3)} s`)); activate(dot, () => selectFlight(row.id)); root.append(dot);
      }
    });
    parent.append(root);
  }
  function renderContactTimeline() {
    const rows = [state.a, ...(state.b ? [state.b] : [])].flatMap((cohort, i) => cohort.members.slice(0, 50).map(member => ({ id: member.id, cohort: i ? "b" : "a", events: member.contact_events || [], label: `${i ? "B" : "A"} · ${date(member.timestamp)} · ${member.contacts} contact(s)` })));
    timeline($("contact-timeline"), rows);
    $("contact-timeline").append(element("p", "plot-footnote", "Newest 50 approaches per cohort. Dots: saved contact times; bars: recorded analysis windows. Missing contact timestamps are not reconstructed. G and compression profiles below cover −2 to +15 s."));
    timeline($("gear-timeline"), (state.flight?.contact_states || []).filter(gear => gear.intervals.length).map(gear => ({ label: `Contact point ${gear.gear}`, intervals: gear.intervals })));
    if (state.flight) $("gear-timeline").append(element("p", "plot-footnote", `Selected ${state.flight.meta.aircraft} · cyan: on ground; grey: airborne; blank: unavailable. Gear labels retain recorded contact-point indices.`));
  }
  function renderDistributions() {
    const parent = $("distributions"); parent.replaceChildren();
    $("comparison-legend").replaceChildren(element("span", "cohort-a", `● A · ${cohortLabel()} · ${state.a.included} approaches`));
    if (state.b) $("comparison-legend").append(element("span", "cohort-b", `● B · ${cohortLabel(true)} · ${state.b.included} approaches`));
    else $("comparison-legend").append(element("span", "", "Use Compare & refine to add a second aircraft, app version, device, airport, runway, wind range or weight range."));
    for (const key of ["fpm", "g", "ias", "weight", "crosswind", "along"]) {
      const card = element("article", "panel plot-card"), heading = element("div", "panel-heading");
      heading.append(element("h3", "", metric[key][0]), element("span", "plot-unit", metric[key][1])); card.append(heading); parent.append(card);
      const distribution = FlightMath.histogram([state.a.members, ...(state.b ? [state.b.members] : [])], key);
      if (!distribution) { card.append(element("div", "plot-empty", "No recorded values for this metric.")); continue; }
      const ymax = Math.max(...distribution.groups.flatMap(group => group.percentages), 1) * 1.1;
      const chart = chartFrame(card, [distribution.low, distribution.high], [0, ymax], `${metric[key][0]} · ${metric[key][1]}`, "Share of available observations · %");
      distribution.groups.forEach((group, cohort) => {
        const code = cohort ? "b" : "a", part = distribution.groups.length;
        group.bins.forEach((ids, i) => {
          if (!ids.length) return;
          const low = distribution.low + i * distribution.width, high = low + distribution.width;
          const x = chart.x(low) + cohort / part * (chart.x(high) - chart.x(low));
          const bar = svg("rect", { x, y: chart.y(group.percentages[i]), width: Math.max(1, (chart.x(high) - chart.x(low)) / part - 1), height: chart.y(0) - chart.y(group.percentages[i]), class: `histogram-bar cohort-${code}`, tabindex: 0, role: "button", "aria-label": `${code.toUpperCase()}: ${ids.length} flights, ${fmt(low)} to ${fmt(high)} ${metric[key][1]}` });
          bar.append(svg("title", {}, `${code.toUpperCase()} · ${fmt(low, 2)}–${fmt(high, 2)} ${metric[key][1]}\n${ids.length} / ${group.n} · ${fmt(group.percentages[i])}% · select these flights`));
          activate(bar, () => selectPoints(ids, code)); chart.root.append(bar);
        });
        if (valid(group.median)) chart.root.append(svg("line", { x1: chart.x(group.median), x2: chart.x(group.median), y1: chart.margin.top, y2: chart.y(0), class: `distribution-median cohort-${code}` }));
      });
      card.append(element("div", "plot-readout", distribution.groups.map((group, i) => `${i ? "B" : "A"}: median ${fmt(group.median, 2)} ${metric[key][1]} · n=${group.n} · ${group.missing} unavailable`).join(" | ")));
    }
  }
  function populateFlights() {
    const members = [...state.a.members, ...(state.b?.members || [])];
    const unique = [...new Map(members.map(m => [m.id, m])).values()];
    $("replay-flight").replaceChildren(new Option("Select a flight", ""), ...unique.map(m => new Option(`${date(m.timestamp)} · ${m.airport || "Unknown"}/${m.runway || "—"} · ${m.aircraft}`, m.id)));
    if (unique.some(m => m.id === state.selected)) $("replay-flight").value = state.selected;
    else { state.selected = null; state.flight = null; $("selected-flight").hidden = true; }
  }
  async function selectFlight(id, openReplay = false) {
    stop(); state.selected = id; state.flight = null; $("replay-flight").value = id;
    const member = [...state.a.members, ...(state.b?.members || [])].find(m => m.id === id);
    if (!member) return;
    const bar = $("selected-flight"); bar.hidden = false;
    const description = element("div"); description.append(element("p", "", `${member.aircraft} · ${member.airport || "Unknown"} / ${member.runway || "—"}`), element("span", "", `${date(member.timestamp)} · ${fmt(member.fpm)} ft/min · ${fmt(member.g, 2)} G · ${member.contacts} contact(s)`));
    const actions = element("div", "selection-actions"), profiles = element("button", "button secondary", "Compare profile"), replay = element("button", "button", "Open flight replay →");
    profiles.addEventListener("click", () => chooseTab("profiles")); replay.addEventListener("click", () => chooseTab("replay")); actions.append(profiles, replay); bar.replaceChildren(description, actions);
    try {
      const flight = await get(`/api/analytics/landings/${encodeURIComponent(id)}`);
      if (state.selected !== id) return;
      state.flight = flight; $("scrubber").max = flight.series.t.length - 1; $("scrubber").value = 0;
      $("scrubber").disabled = false; $("play").disabled = false;
      if (openReplay) chooseTab("replay"); else await render();
    } catch (e) { error(e.message); }
  }
  function renderReplay() {
    state.replayPlots = [];
    for (const id of ["replay-map", "control-state", "replay-charts", "replay-info"]) $(id).replaceChildren();
    if (!state.flight) { $("replay-info").textContent = "Choose a flight from the selector, map or scatter plot."; return; }
    const { meta, series, capture_id: capture } = state.flight;
    $("replay-info").textContent = `${meta.aircraft} · ${meta.airport}/${meta.runway} · ${date(meta.timestamp)} · ${meta.frames.toLocaleString()} source frames · ${meta.quality === "recorded-physics" ? "Pause / motion state checked" : "Historical record: physics-state evidence unavailable"} · freeze / slew provenance checked in ${meta.source_checked_frames || 0} of ${meta.frames} frames · ${meta.devices.join(", ") || "Physical device labels unavailable"}`;
    const link = element("a", "", " · Open source archive in telemetry hub"); link.href = `/#capture=${encodeURIComponent(capture)}`; $("replay-info").append(link);
    const geographic = !series.along.some(valid);
    const xKey = geographic ? "lon" : "along", yKey = geographic ? "lat" : "cross";
    const chart = chartFrame($("replay-map"), domain(series[xKey]), domain(series[yKey]), geographic ? "Longitude · °" : "Distance past threshold · m", geographic ? "Latitude · °" : "Lateral deviation · m");
    chart.root.append(svg("path", { d: trackPath(chart, series, xKey, yKey), class: "median-a" }));
    const dot = svg("circle", { r: 5, class: "replay-cursor" }); chart.root.append(dot);
    state.replayPlots.push(i => { dot.setAttribute("visibility", valid(series[xKey][i]) && valid(series[yKey][i]) ? "visible" : "hidden"); if (valid(series[xKey][i]) && valid(series[yKey][i])) { dot.setAttribute("cx", chart.x(series[xKey][i])); dot.setAttribute("cy", chart.y(series[yKey][i])); } });
    for (const keys of [["agl", "sink"], ["pitch", "bank"], ["stickP", "cmdP"], ["stickR", "cmdR"], ["g", "latG"], ["throttle", "n1"]]) replayChart(keys);
    updateReplay();
  }
  function replayChart(keys) {
    // Different units never share an axis: height and sink receive separate cards.
    if (state.catalog.channels[keys[0]].unit !== state.catalog.channels[keys[1]].unit) { keys.forEach(key => replayChart([key, key])); return; }
    keys = [...new Set(keys)]; const series = state.flight.series;
    const card = element("article", "panel plot-card"), heading = element("div", "panel-heading");
    heading.append(element("h3", "", keys.map(k => state.catalog.channels[k].label).join(" / ")), element("span", "plot-unit", state.catalog.channels[keys[0]].unit)); card.append(heading); $("replay-charts").append(card);
    const values = keys.flatMap(k => series[k] || []).filter(valid);
    if (!values.length) { card.append(element("div", "plot-empty", "Channel unavailable in this recording")); return; }
    const chart = chartFrame(card, domain(series.t), domain(values), "Time from first contact · s", keys.join(" / "));
    keys.forEach((key, j) => chart.root.append(svg("path", { d: trackPath(chart, { ...series, signal: series[key], run: key.startsWith("stick") ? series.run.map((value, i) => `${value}:${series.input_run[i]}`) : series.run }, "t", "signal"), class: `median-${j ? "b" : "a"}` })));
    const line = svg("line", { y1: chart.margin.top, y2: chart.height - chart.margin.bottom, class: "hover-line" }); chart.root.append(line);
    const readout = element("div", "plot-readout"); card.append(readout);
    state.replayPlots.push(i => { line.setAttribute("x1", chart.x(series.t[i])); line.setAttribute("x2", chart.x(series.t[i])); readout.textContent = keys.map((key, j) => `${j ? "Orange" : "Cyan"}: ${state.catalog.channels[key].label} ${fmt(series[key]?.[i], 2)} ${state.catalog.channels[key].unit}`).join(" · "); });
    chart.root.addEventListener("pointermove", event => {
      if (state.playing) return; const value = chart.valueX(chart.pointer(event).x);
      let nearest = 0; series.t.forEach((t, i) => { if (Math.abs(t - value) < Math.abs(series.t[nearest] - value)) nearest = i; });
      $("scrubber").value = nearest; updateReplay();
    });
  }
  function updateReplay() {
    if (!state.flight) return;
    const i = Number($("scrubber").value), series = state.flight.series;
    $("replay-time").textContent = `${fmt(series.t[i], 2)} s`;
    state.replayPlots.forEach(fn => fn(i));
    const root = svg("svg", { viewBox: "0 0 640 280", class: "flight-plot control-glyph", role: "img", "aria-label": "Physical and commanded controls at the replay cursor" });
    root.append(svg("rect", { x: 70, y: 25, width: 200, height: 200, rx: 7, class: "control-grid" }), svg("line", { x1: 170, x2: 170, y1: 25, y2: 225, class: "control-grid" }), svg("line", { x1: 70, x2: 270, y1: 125, y2: 125, class: "control-grid" }));
    for (const [xKey, yKey, code] of [["cmdR", "cmdP", "command"], ["stickR", "stickP", "physical"]]) if (valid(series[xKey]?.[i]) && valid(series[yKey]?.[i])) root.append(svg("circle", { cx: 170 + series[xKey][i], cy: 125 - series[yKey][i], r: code === "physical" ? 6 : 4, class: code }));
    root.append(svg("text", { x: 170, y: 250, "text-anchor": "middle" }, "Roll / pitch · ±100%"));
    ["stickP", "stickR", "stickY", "cmdP", "cmdR", "cmdY"].forEach((key, j) => root.append(svg("text", { x: 320, y: 45 + j * 29 }, `${state.catalog.channels[key].label}: ${fmt(series[key]?.[i])}%`)));
    $("control-state").replaceChildren(root);
  }
  function stop() {
    state.playing = false; $("play").textContent = "Play";
    if (state.animationFrame !== null) cancelAnimationFrame(state.animationFrame);
    state.animationFrame = null;
  }
  function play() {
    if (state.playing) { stop(); return; }
    if (!state.flight) return;
    const times = state.flight.series.t;
    if (Number($("scrubber").value) >= times.length - 1) $("scrubber").value = 0;
    state.playing = true; $("play").textContent = "Pause";
    const started = performance.now(), first = times[Number($("scrubber").value)], speed = Number($("play-speed").value);
    const tick = now => {
      state.animationFrame = null;
      if (!state.playing) return;
      const target = first + (now - started) / 1000 * speed;
      let index = Number($("scrubber").value); while (index < times.length - 1 && times[index + 1] <= target) index++;
      $("scrubber").value = index; updateReplay();
      if (index === times.length - 1) stop(); else state.animationFrame = requestAnimationFrame(tick);
    }; state.animationFrame = requestAnimationFrame(tick);
  }

  $("flight-filters").addEventListener("submit", e => { e.preventDefault(); state.ids = { a: [], b: [] }; loadCohorts(); });
  $("reload").addEventListener("click", loadCatalog); $("days").addEventListener("change", loadCatalog);
  $("airport").addEventListener("change", updateRunways); $("compare_by").addEventListener("change", () => { $("compare_min").value = ""; $("compare_max").value = ""; updateComparison(); });
  $("alignment").addEventListener("change", loadCohorts); $("channel-set").addEventListener("change", () => $("individuals").checked ? loadCohorts() : renderProfiles());
  $("individuals").addEventListener("change", loadCohorts);
  $("flare-window").addEventListener("change", renderProfiles);
  $("response-axis").addEventListener("change", () => { $("channel-set").value = $("response-axis").value; if ($("individuals").checked) loadCohorts(); else renderProfiles(); });
  $("extremes").addEventListener("change", () => render().catch(e => error(e.message)));
  $("corridor-mode").addEventListener("change", renderCorridor); $("touch-colour").addEventListener("change", renderFootprint);
  for (const id of ["corridor-display", "corridor-colour"]) $(id).addEventListener("change", () => { renderLocalMap(); renderCorridor(); });
  $("touch-display").addEventListener("change", renderFootprint);
  $("clear-selection").addEventListener("click", () => { state.ids = { a: [], b: [] }; loadCohorts(); });
  document.querySelectorAll("[data-tab]").forEach(node => node.addEventListener("click", () => chooseTab(node.dataset.tab)));
  for (const id of ["scatter-x", "scatter-y", "scatter-colour"]) {
    $(id).replaceChildren(...Object.entries(metric).map(([key, value]) => new Option(`${value[0]} · ${value[1]}`, key)));
    $(id).addEventListener("change", renderScatter);
  }
  $("scatter-x").value = "fpm"; $("scatter-y").value = "g"; $("scatter-colour").value = "crosswind";
  $("replay-flight").addEventListener("change", () => { if ($("replay-flight").value) selectFlight($("replay-flight").value, true); });
  $("scrubber").addEventListener("input", () => { stop(); updateReplay(); }); $("play").addEventListener("click", play); $("play-speed").addEventListener("change", stop);
  loadCatalog();
})();
