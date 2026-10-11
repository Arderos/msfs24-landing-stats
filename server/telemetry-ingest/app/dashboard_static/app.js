"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const state = { days: 0, page: 1, pageSize: 25, filters: {}, summary: null, list: null, loading: false, generation: 0, detailGeneration: 0, lastGood: null, expanded: new Set() };
  const number = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 0 });
  const decimal = new Intl.NumberFormat("en-GB", { maximumFractionDigits: 2 });
  const dateTime = new Intl.DateTimeFormat("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" });
  const fullDate = new Intl.DateTimeFormat("en-GB", { year: "numeric", month: "long", day: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit" });
  const svgNS = "http://www.w3.org/2000/svg";
  const kindLabels = { automatic_landing: "Automatic upload", bug_report: "Bug report", raw_debug: "Raw diagnostics", raw_recording: "Raw recording", raw_telemetry: "Raw recording", manual: "Manual" };
  const outcomeLabels = { contacts: "Recorded contacts", diagnostic: "Diagnostic capture · no landing", no_contacts: "No contacts detected", results_unavailable: "Landing results unavailable", invalid_contacts: "Invalid historical contacts", unreadable: "Archive unreadable" };
  const filterLabels = { airport: "Airport", aircraft: "Aircraft", version: "Version", kind: "Type", outcome: "Outcome", q: "Search" };
  const metricLabels = { aircraft_fpm: ["Aircraft descent (+ down)", "fpm"], surface_fpm: ["Surface closure (+ down)", "fpm"], peak_g: ["Peak vertical G", "G"], legacy_peak_2s_g: ["Legacy 2-second peak", "G"], impact_window_s: ["Contact assessment window", "s"], reconstruction_residual_fpm: ["Reconstruction residual", "fpm"], reconstruction_uncertainty_fpm: ["Reconstruction uncertainty", "fpm"], sample_rate_hz: ["Telemetry sample rate", "Hz"], duration_s: ["Recording duration", "s"], max_gap_s: ["Maximum sample interval", "s"], compression_ratio: ["Compression ratio", "×"] };
  const contactMetricLabels = { ...metricLabels, pitch_deg: ["Pitch (+ nose down)", "°"], bank_deg: ["Roll", "°"], ground_speed_kt: ["Ground speed", "kt"], airspeed_kt: ["Indicated airspeed", "kt"] };
  metricLabels.axis_correlation = ["Selected axis correlation (absolute)", "coefficient"];
  metricLabels.axis_lag_s = ["Selected axis lag", "s"];
  for (const key of ["pitch_deg", "bank_deg", "ground_speed_kt", "airspeed_kt"]) metricLabels[key] = contactMetricLabels[key];

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }
  function svg(tag, attributes = {}, text) {
    const node = document.createElementNS(svgNS, tag);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
    if (text !== undefined) node.textContent = String(text);
    return node;
  }
  const arr = (value) => Array.isArray(value) ? value : [];
  const known = (value) => value !== null && value !== undefined && value !== "";
  const fmt = (value, fractional = false) => known(value) && Number.isFinite(Number(value)) ? (fractional ? decimal : number).format(Number(value)) : "—";
  function bytes(value) {
    if (!known(value) || !Number.isFinite(Number(value))) return "—";
    const units = ["B", "KiB", "MiB", "GiB", "TiB"];
    let amount = Number(value), unit = 0;
    while (Math.abs(amount) >= 1024 && unit < units.length - 1) { amount /= 1024; unit += 1; }
    return `${fmt(amount, unit > 0)} ${units[unit]}`;
  }
  function duration(value) {
    if (!known(value) || !Number.isFinite(Number(value))) return "—";
    const seconds = Math.round(Number(value));
    if (seconds < 60) return `${fmt(seconds)} s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${seconds % 60} s`;
    return `${fmt(Math.floor(seconds / 3600))} h ${Math.floor((seconds % 3600) / 60)} min`;
  }
  function date(value, full = false) { const d = new Date(value); return value && !Number.isNaN(d.getTime()) ? (full ? fullDate : dateTime).format(d) : "—"; }
  function percent(part, total) { return total > 0 && known(part) ? `${fmt(100 * part / total, true)}%` : "—"; }
  function row(label, value, className = "stat-row") {
    const result = el("div", className);
    result.append(el(className === "stat-row" ? "dt" : "span", "label", label), el(className === "stat-row" ? "dd" : "span", "value", value));
    return result;
  }
  function badge(label, kind = "") { return el("span", `badge ${kind}`, label); }
  function empty(message) { return el("div", "empty", message); }
  function setSync(message, variant = "") { const dot = el("span", `status-dot ${variant}`); $("sync-status").replaceChildren(dot, document.createTextNode(message)); }
  function params(includeFilters = false) {
    const query = new URLSearchParams({ days: String(state.days) });
    if (includeFilters) {
      query.set("page", String(state.page)); query.set("page_size", String(state.pageSize));
      Object.entries(state.filters).forEach(([key, value]) => { if (value) query.set(key, value); });
    }
    return query;
  }
  async function request(path) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 20000);
    try {
      const response = await fetch(path, { signal: controller.signal, cache: "no-store", headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`Server returned HTTP ${response.status}`);
      return await response.json();
    } catch (error) {
      if (error.name === "AbortError") throw new Error("Server did not respond within 20 seconds");
      throw error;
    } finally { clearTimeout(timeout); }
  }

  function initialSkeletons() {
    for (let i = 0; i < 6; i += 1) {
      const item = el("article", "kpi");
      item.append(el("div", "skeleton"), el("div", "skeleton big"), el("div", "skeleton"));
      $("kpis").append(item);
    }
    $("timeline").append(empty("Loading history…"));
  }
  function renderKpis(summary) {
    const t = summary.totals || {};
    const kpis = [
      ["Retained archives", fmt(t.captures), "In the selected period"],
      ["Unique contacts", fmt(t.unique_landings), `${fmt(t.invalid_historical_contacts)} invalid historical contacts · ${fmt(t.duplicate_landing_records)} duplicate records excluded`],
      ["App installations", fmt(t.installations), "With uploads in this period"],
      ["Airports", fmt(t.airports), `${fmt(t.aircraft)} aircraft variants`],
      ["Telemetry samples", fmt(t.samples), "Across all retained archives"],
      ["Archive storage", bytes(t.compressed_bytes), `${bytes(t.uncompressed_bytes)} uncompressed`]
    ];
    $("kpis").replaceChildren(...kpis.map(([label, value, note]) => {
      const card = el("article", "kpi");
      card.append(el("p", "kpi-label", label), el("p", "kpi-value", value), el("p", "kpi-note", note));
      return card;
    }));
    $("kpis").setAttribute("aria-busy", "false");
  }

  function renderTimeline(summary) {
    const sourceDays = arr(summary.daily);
    const daily = [];
    if (sourceDays.length) {
      const lookup = new Map(sourceDays.map((entry) => [entry.date, entry]));
      const first = Date.parse(`${sourceDays[0].date}T00:00:00Z`), last = Date.parse(`${sourceDays[sourceDays.length - 1].date}T00:00:00Z`);
      if (Number.isFinite(first) && Number.isFinite(last) && last - first <= 366 * 86400000) {
        for (let timestamp = first; timestamp <= last; timestamp += 86400000) {
          const day = new Date(timestamp).toISOString().slice(0, 10);
          daily.push(lookup.get(day) || { date: day, captures: 0, compressed_bytes: 0, samples: 0 });
        }
      } else daily.push(...sourceDays);
    }
    $("timeline-total").textContent = `${fmt(summary.totals?.captures)} archives`;
    if (!daily.length) { $("timeline").replaceChildren(empty("No uploads in this period")); $("timeline-legend").replaceChildren(); return; }
    const width = 840, height = 190, left = 34, right = 10, top = 15, bottom = 29;
    const plotWidth = width - left - right, plotHeight = height - top - bottom;
    const maximum = Math.ceil(Math.max(3, ...daily.map((day) => Number(day.captures) || 0)) / 3) * 3;
    const chart = svg("svg", { viewBox: `0 0 ${width} ${height}`, role: "img", "aria-label": "Uploaded archives per day (UTC)" });
    chart.append(svg("title", {}, "Retained archives by upload date (UTC)"));
    for (let step = 0; step <= 3; step += 1) {
      const y = top + plotHeight * step / 3;
      chart.append(svg("line", { x1: left, y1: y, x2: width - right, y2: y, class: "grid-line" }));
      chart.append(svg("text", { x: left - 8, y: y + 3, "text-anchor": "end" }, fmt(maximum * (1 - step / 3))));
    }
    const step = plotWidth / daily.length;
    const barWidth = Math.max(1, Math.min(36, step * .58));
    const labelEvery = Math.max(1, Math.ceil(daily.length / 7));
    daily.forEach((day, index) => {
      const x = left + step * (index + .5), barHeight = plotHeight * (Number(day.captures) || 0) / maximum;
      const bar = svg("rect", { x: x - barWidth / 2, y: top + plotHeight - barHeight, width: barWidth, height: barHeight, rx: 2, class: "chart-bar" });
      bar.append(svg("title", {}, `${day.date}: ${fmt(day.captures)} archives · ${bytes(day.compressed_bytes)} · ${fmt(day.samples)} samples`));
      chart.append(bar);
      if (index % labelEvery === 0 || index === daily.length - 1 && daily.length > 1 && daily.length % labelEvery > 1) {
        const parts = String(day.date).split("-");
        chart.append(svg("text", { x, y: height - 6, "text-anchor": "middle" }, parts.length === 3 ? `${parts[2]}/${parts[1]}` : day.date));
      }
    });
    $("timeline").replaceChildren(chart);
    const legend = el("span", "legend-item");
    const swatch = el("span", "legend-swatch"); swatch.style.backgroundColor = "var(--orange)";
    legend.append(swatch, document.createTextNode("Archives · UTC days"));
    $("timeline-legend").replaceChildren(legend, el("span", "", "Repeat reports are separate archives"));
  }

  function renderHealth(summary) {
    const i = summary.index || {}, storage = summary.storage || {};
    const items = [row("Indexed", `${fmt(i.indexed)} / ${fmt(i.total)}`, "health-row")];
    const meter = el("div", "meter"), fill = el("span");
    fill.style.width = `${i.total > 0 ? Math.min(100, 100 * i.indexed / i.total) : 0}%`; meter.append(fill); items.push(meter);
    items.push(row("Pending indexing", fmt(i.pending), "health-row"), row("Indexing errors", fmt(i.errors), "health-row"));
    items.push(row("Last scan", date(i.last_success_at || i.last_scan_at), "health-row"));
    if (known(storage.retention_days)) items.push(row("Retention period", `${fmt(storage.retention_days)} days`, "health-row"));
    if (known(storage.used_bytes)) items.push(row("All retained archives", bytes(storage.used_bytes), "health-row"));
    if (known(storage.free_bytes)) items.push(row("Free disk space", bytes(storage.free_bytes), "health-row"));
    if (known(storage.quota_bytes)) items.push(row("Archive quota", bytes(storage.quota_bytes), "health-row"));
    items.push(el("p", "help-note", "Archive counts and index status reflect current storage. Period metrics use records that have already been indexed."));
    $("health").replaceChildren(...items);
    const banner = $("index-banner");
    const indexing = i.pending > 0 || ["starting", "scanning", "indexing"].includes(i.state);
    if (indexing && !i.last_success_at && !i.error) {
      banner.textContent = `Initial archive indexing in progress: ${fmt(i.indexed)} of ${fmt(i.total)}. Statistics update automatically; new uploads are received independently.`; banner.hidden = false;
    } else if (i.error || i.stale) {
      banner.textContent = `Index updates are temporarily unavailable. Showing the latest available data.${i.error ? ` ${i.error}` : ""}`; banner.hidden = false;
    } else if (indexing) {
      banner.textContent = `Initial archive indexing in progress: ${fmt(i.indexed)} of ${fmt(i.total)}. Statistics update automatically; new uploads are received independently.`; banner.hidden = false;
    } else if (i.errors > 0) {
      banner.textContent = `${fmt(i.errors)} archives could not be parsed. They are retained; see the archive list for details.`; banner.hidden = false;
    } else { banner.hidden = true; }
  }

  function setFilter(key, value) {
    state.filters[key] = value; state.page = 1;
    renderFilters(); refresh(false, true);
    $("captures").scrollIntoView({ behavior: "smooth", block: "start" });
  }
  function breakdown(title, items, filter, color, countLabel) {
    const panel = el("article", "panel breakdown-panel");
    const header = el("div", "panel-heading"); header.append(el("h3", "", title), el("span", "panel-meta", `${items.length} ${countLabel || "groups"}`));
    const list = el("div", "breakdown-list");
    const max = Math.max(1, ...items.map((item) => Number(item.count) || 0));
    let expanded = state.expanded.has(title);
    function render() {
      const shown = expanded ? items : items.slice(0, 7);
      list.classList.toggle("expanded", expanded);
      list.replaceChildren(...shown.map((item) => {
        const clickable = filter && item.name && (item.name !== "Unknown" || filter === "airport");
        const r = el(clickable ? "button" : "div", `breakdown-row ${color || ""} ${clickable ? "clickable" : ""}`);
        if (clickable) { r.type = "button"; r.addEventListener("click", () => setFilter(filter, String(item.name))); }
        const bar = el("span", "bar"); bar.style.width = `${Math.max(1, 100 * item.count / max)}%`;
        const name = item.name === "Unknown" ? (filter === "airport" ? "Unresolved airport" : "Unknown") : filter === "kind" ? kindLabels[item.name] || item.name : filter === "outcome" ? outcomeLabels[item.name] || item.name : item.name;
        r.title = `${name || "Unknown"}: ${fmt(item.count)}`;
        r.append(bar, el("span", "name", name || "Unknown"), el("span", "count", fmt(item.count)));
        return r;
      }));
      if (!items.length) list.append(el("div", "breakdown-empty", "No data in this period"));
      if (items.length > 7) {
        const toggle = el("button", "text-button", expanded ? "Show less ↑" : `Show all (${fmt(items.length)}) ↓`);
        toggle.addEventListener("click", () => { expanded = !expanded; if (expanded) state.expanded.add(title); else state.expanded.delete(title); render(); }); list.append(toggle);
      }
    }
    render(); panel.append(header, list); return panel;
  }
  function renderBreakdowns(summary) {
    const b = summary.breakdowns || {};
    $("breakdowns").replaceChildren(
      breakdown("Airports · unique contacts", arr(b.airports), "airport", "orange"),
      breakdown("Aircraft · unique contacts", arr(b.aircraft), "aircraft", ""),
      breakdown("App versions", arr(b.versions), "version", "green"),
      breakdown("Runways · unique contacts", arr(b.runways), null, "orange"),
      breakdown("Upload types", arr(b.kinds), "kind", "green"),
      breakdown("Capture outcomes", arr(b.outcomes), "outcome", "orange"),
      breakdown("Telemetry schemas", arr(b.schemas), null, "")
    );
  }

  function diagnostic(title, values, note) {
    const panel = el("article", "panel diagnostic-panel"), dl = el("dl");
    panel.append(el("h3", "", title));
    values.forEach(([key, value]) => dl.append(row(key, value)));
    panel.append(dl); if (note) panel.append(el("p", "help-note", note)); return panel;
  }
  function listDetails(title, items) {
    const details = el("details"), dl = el("dl"); details.append(el("summary", "", `${title} (${fmt(items.length)})`));
    items.forEach((item) => dl.append(row(item.name || "Unknown", fmt(item.count))));
    if (!items.length) dl.append(empty("No data yet"));
    details.append(dl); persistentExpansion(details, title); return details;
  }
  function persistentExpansion(details, key) {
    details.open = state.expanded.has(key);
    details.addEventListener("toggle", () => { if (details.open) state.expanded.add(key); else state.expanded.delete(key); });
  }
  function renderDiagnostics(summary) {
    const q = summary.quality || {}, j = summary.journals || {}, a = summary.axis || {}, r = summary.replay || {}, m = summary.metrics || {};
    const roles = new Map(arr(a.selected_roles).map((v) => [String(v.name).toLowerCase(), v.count]));
    const nodes = [
      diagnostic("Decision journal", [["Archives with a journal", fmt(j.available)], ["Without a journal", fmt(j.missing)], ["Decision entries", fmt(j.entries)], ["Truncated journals", fmt(j.truncated)]], "Older versions may not include a journal."),
      diagnostic("Physical axes", [["Archives with diagnostics", fmt(a.journal_captures)], ["Pitch · axis selections", fmt(roles.get("pitch") ?? 0)], ["Roll · axis selections", fmt(roles.get("roll") ?? 0)], ["Yaw · axis selections", fmt(roles.get("yaw") ?? 0)]], "Counts of selection decisions, not proof of matching accuracy."),
      diagnostic("3D and reconstruction", [["Recorded position channels", fmt(r.pose_landings)], ["Confirmed replay tracks", fmt(r.confirmed_track_landings)], ["Contacts with a runway", fmt(r.runway_landings)], ["Surface closure available", fmt(r.closure_available)]], "Position channels alone do not prove a usable replay. Tracks are confirmed by the per-contact decision journal."),
      diagnostic("Stream quality", [["Median sample rate", `${fmt(m.sample_rate_hz?.p50, true)} Hz`], ["Archives with gaps > 1 s", fmt(q.archives_with_gaps)], ["Duplicate timestamps", fmt(q.duplicate_timestamps)], ["Backward timestamps", fmt(q.backward_timestamps)]], "Gaps may be simulator pauses, not necessarily packet loss.")
    ];
    const technical = el("article", "panel technical-panel");
    const heading = el("div", "panel-heading"); heading.append(el("h3", "", "Technical overview"), el("span", "panel-meta", "distributions and decisions"));
    const content = el("div", "technical-content"), details = el("details");
    details.append(el("summary", "", "Metric distributions and full diagnostics")); persistentExpansion(details, "technical");
    const scroll = el("div", "table-scroll"), table = el("table"), head = el("thead"), tr = el("tr");
    ["Metric", "N", "Min.", "Mean", "Median", "P90", "P95", "Max."].forEach((name, i) => tr.append(el("th", i ? "numeric" : "", name))); head.append(tr); table.append(head);
    const body = el("tbody");
    for (const [key, [label, unit]] of Object.entries(metricLabels)) {
      const metric = m[key] || {}, line = el("tr"); line.append(el("td", "", `${label}, ${unit}`));
      ["count", "min", "mean", "p50", "p90", "p95", "max"].forEach((k) => line.append(el("td", "numeric mono", fmt(metric[k], k !== "count")))); body.append(line);
    }
    table.append(body); scroll.append(table); details.append(scroll);
    const quality = el("dl", "quality-extra");
    [["Intervals > 1 s", q.gaps_over_1s], ["Sample count mismatches", q.sample_count_mismatches], ["NaN / Infinity in telemetry", q.nonfinite_values], ["Malformed numeric values", q.malformed_values], ["Sequence errors", q.sequence_errors], ["Sample delivery intervals < 1 ms", q.delivery_intervals_under_1ms], ["Archive parsing errors", q.index_errors], ["Contacts without an ID (not deduplicated)", summary.totals?.landings_without_id], ["Invalid historical contacts (excluded)", summary.totals?.invalid_historical_contacts], ["Unresolved airport contacts", summary.totals?.unresolved_airport_contacts], ["Replay track availability unknown", r.track_availability_unknown], ["Surface closure unavailable", r.closure_unavailable], ["Surface closure availability unknown", r.closure_unknown]].forEach(([label, value]) => quality.append(row(label, fmt(value))));
    details.append(quality);
    details.append(listDetails("Journal components", arr(j.components)), listDetails("Decision codes", arr(j.codes)), listDetails("Axis decisions", arr(a.selection_codes)), listDetails("3D decisions", arr(r.codes)), listDetails("Geometry sources", arr(summary.breakdowns?.geometry_sources)), listDetails("Impact window status", arr(summary.breakdowns?.impact_status)), listDetails("Simulators", arr(summary.breakdowns?.simulators)), listDetails("Devices · archives containing the device", arr(summary.breakdowns?.controllers)));
    if (summary.registry) {
      const registry = el("dl", "quality-extra"); registry.append(row("Active registrations", fmt(summary.registry.active)), row("Revoked registrations", fmt(summary.registry.revoked))); details.append(registry);
    }
    if (arr(summary.signed_attempt_byte_budgets).length) {
      const budgets = el("details"), list = el("dl"); budgets.append(el("summary", "", "Quota reservations for signed upload attempts"));
      summary.signed_attempt_byte_budgets.forEach((entry) => list.append(row(date(Number(entry.window_start) * 1000), bytes(entry.bytes))));
      budgets.append(el("p", "help-note", "Reserved bytes by rate-limit window. These are not counts of successful or rejected requests."), list); persistentExpansion(budgets, "budgets"); details.append(budgets);
    }
    arr(summary.notes).forEach((note) => details.append(el("p", "help-note", note)));
    content.append(details); technical.append(heading, content); nodes.push(technical);
    $("diagnostics").replaceChildren(...nodes);
  }

  function renderFilters() {
    const chips = Object.entries(state.filters).filter(([, value]) => value).map(([key, value]) => {
      const label = key === "kind" ? kindLabels[value] || value : key === "outcome" ? outcomeLabels[value] || value : value;
      const chip = el("button", "filter-chip"); chip.append(el("span", "", `${filterLabels[key] || key}: ${label}`), document.createTextNode("×"));
      chip.setAttribute("aria-label", `Remove filter ${filterLabels[key] || key}`);
      chip.addEventListener("click", () => { delete state.filters[key]; if (key === "q") $("search").value = ""; state.page = 1; renderFilters(); refresh(false, true); });
      return chip;
    });
    $("active-filters").replaceChildren(...chips); $("clear-filters").hidden = !chips.length;
  }
  function renderCaptures(data) {
    state.list = data;
    const items = arr(data.items);
    $("capture-count").textContent = fmt(data.total);
    $("capture-rows").replaceChildren(...items.map((capture) => {
      const tr = el("tr", "capture-row");
      const uploaded = el("td", "mono", date(capture.received_at)); uploaded.title = date(capture.received_at, true); uploaded.append(el("small", "", String(capture.capture_id || "").slice(0, 12)));
      const airportText = arr(capture.airports).join(", ") || (capture.unresolved_contact_count > 0 ? "Unresolved airport" : outcomeLabels[capture.capture_outcome] || "Awaiting analysis");
      const identity = el("td"); identity.append(el("span", "airport", airportText), el("small", "aircraft", capture.aircraft || "Unknown aircraft")); identity.title = `${airportText} · ${capture.aircraft || "—"}`;
      const type = el("td", "", kindLabels[capture.capture_kind] || capture.capture_kind || "—");
      const version = el("td", "mono", capture.app_version || "—"); version.append(el("small", "", `schema ${capture.schema_version ?? "—"}`));
      const samples = el("td", "numeric mono", fmt(capture.sample_count)); samples.append(el("small", "", duration(capture.duration_s)));
      const size = el("td", "numeric mono", bytes(capture.compressed_bytes));
      const flags = el("td");
      if (capture.index_error || capture.index_status === "error") flags.append(badge("Parsing error", "warning"));
      else if (!capture.index_status || ["pending", "indexing"].includes(capture.index_status)) flags.append(badge("Indexing", "dim"));
      else {
        if (capture.journal_entries > 0) flags.append(badge(`${fmt(capture.journal_entries)} decisions`));
        else flags.append(badge("No journal", "dim"));
        if (capture.landing_count > 1) flags.append(badge(`${fmt(capture.landing_count)} contacts`, "blue"));
        if (capture.unresolved_contact_count > 0) flags.append(badge(`${fmt(capture.unresolved_contact_count)} unresolved airport`, "warning"));
        if (capture.invalid_contact_count > 0) flags.append(badge(`${fmt(capture.invalid_contact_count)} excluded from statistics`, "warning"));
        if (capture.max_gap_s > 1) flags.append(badge("Stream gap", "warning"));
      }
      const open = el("td"), button = el("button", "capture-open", "↗"); button.setAttribute("aria-label", `Open archive ${capture.capture_id}`); button.addEventListener("click", () => openDetail(capture.capture_id)); open.append(button);
      tr.addEventListener("click", (event) => { if (!event.target.closest("button")) openDetail(capture.capture_id); });
      tr.append(uploaded, identity, type, version, samples, size, flags, open); return tr;
    }));
    $("table-state").hidden = items.length > 0;
    if (!items.length) $("table-state").textContent = Object.values(state.filters).some(Boolean) ? "No archives match these filters. Try a different search." : "No archives in the selected period.";
    const first = data.total ? (data.page - 1) * data.page_size + 1 : 0;
    $("page-description").textContent = `${fmt(first)}–${fmt(first ? first + items.length - 1 : 0)} of ${fmt(data.total)}`;
    $("page-number").textContent = `${fmt(data.page || 1)} / ${fmt(Math.max(1, data.pages || 1))}`;
    $("page-prev").disabled = data.page <= 1; $("page-next").disabled = data.page >= data.pages || !data.total;
  }

  function detailSection(title, content) { const section = el("section", "detail-section"); section.append(el("h3", "", title), content); return section; }
  function jsonDetails(title, value) { const details = el("details"); details.append(el("summary", "", title), el("pre", "detail-json", JSON.stringify(value, null, 2))); return details; }
  function detailGrid(values) { const dl = el("dl", "detail-grid"); values.forEach(([key, value]) => dl.append(row(key, value))); return dl; }
  function renderDetail(data) {
    $("detail-title").textContent = `${arr(data.airports).join(" / ") || (data.unresolved_contact_count > 0 ? "Unresolved airport" : outcomeLabels[data.capture_outcome] || "Telemetry archive")} · ${data.aircraft || "unknown aircraft"}`;
    const nodes = [el("p", "detail-id", `${data.capture_id}\nSHA-256: ${data.sha256 || "—"}`)];
    nodes.push(detailGrid([["Uploaded", date(data.received_at, true)], ["Application", data.app_version || "—"], ["Upload type", kindLabels[data.capture_kind] || data.capture_kind || "—"], ["Raw schema", data.schema_version ?? "—"], ["Compressed archive", bytes(data.compressed_bytes)], ["Uncompressed size", bytes(data.uncompressed_bytes)], ["Samples", fmt(data.sample_count)], ["Duration", duration(data.duration_s)], ["Sample rate", `${fmt(data.sample_rate_hz, true)} Hz`], ["Maximum interval", `${fmt(data.max_gap_s, true)} s`], ["Contacts", fmt(data.landing_count)], ["Decision entries", fmt(data.journal_entries)]]));
    const warnings = arr(data.warnings).slice(); if (data.index_error) warnings.unshift(data.index_error);
    nodes.push(el("p", "help-note", outcomeLabels[data.capture_outcome] || "Capture outcome unavailable"));
    warnings.forEach((warning) => nodes.push(el("p", "detail-warning", warning)));
    if (arr(data.interruptions).length) nodes.push(jsonDetails("Recorded flight interruptions", data.interruptions));
    if (arr(data.landings).length) {
      const contacts = el("div");
      data.landings.forEach((landing, index) => {
        const card = el("div", "detail-metric-card"); card.append(el("h4", "", `Contact ${landing.contact_number ?? index + 1} · ${landing.airport || "—"}${landing.runway ? ` / ${landing.runway}` : ""}`));
        if (landing.statistics_eligible === false) card.append(el("p", "detail-warning", "Invalid historical contact: every recorded raw frame had inactive physics. Preserved here, excluded from landing statistics."));
        const metrics = landing.metrics || {};
        const values = Object.entries(metrics).map(([key, value]) => {
          const spec = contactMetricLabels[key];
          return [spec ? spec[0] : key, typeof value === "number" ? `${fmt(value, true)}${spec ? ` ${spec[1]}` : ""}` : typeof value === "object" ? JSON.stringify(value) : value ?? "—"];
        });
        if (values.length) card.append(detailGrid(values));
        card.append(jsonDetails("Saved contact data", landing)); contacts.append(card);
      }); nodes.push(detailSection("Landings and measurements", contacts));
    }
    if (arr(data.axis_bindings).length) {
      const bindings = el("div"), labels = { pitch: "Pitch", roll: "Roll", yaw: "Yaw", 0: "Pitch", 1: "Roll", 2: "Yaw" };
      data.axis_bindings.forEach((binding) => {
        const card = el("div", "detail-metric-card"); card.append(el("h4", "", labels[binding.role] || "Control axis"));
        card.append(detailGrid([["Source index", fmt(binding.source_index)], ["Correlation", fmt(binding.correlation, true)], ["Lag", `${fmt(binding.lag_seconds, true)} s`]]));
        card.append(jsonDetails("Binding decision", binding)); bindings.append(card);
      });
      bindings.append(el("p", "help-note", "Showing journal decisions. A decision may repeat across contacts; correlation alone does not prove that the selected axis is correct."));
      nodes.push(detailSection("Physical axis matching", bindings));
    }
    const members = el("dl"); arr(data.members).forEach((member) => members.append(row(member.name, `${bytes(member.bytes)} / ZIP ${bytes(member.compressed_bytes)}`)));
    nodes.push(detailSection("Archive contents", members));
    const raw = el("div"); raw.append(jsonDetails("Session", data.session || {}), jsonDetails("Stream diagnostics", data.telemetry || {})); nodes.push(detailSection("Metadata", raw));
    const journal = data.journal || {};
    const journalSection = el("section", "detail-section"), heading = el("div", "decision-header");
    heading.append(el("h3", "", "Decision journal"));
    if (journal.available) {
      const input = el("input"); input.type = "search"; input.placeholder = "Component, code, reason…"; input.setAttribute("aria-label", "Filter decision journal"); heading.append(input);
      const count = el("p", "decision-count"), list = el("div"), more = el("button", "button secondary", "Show 100 more");
      const entries = arr(journal.entries); const texts = entries.map((entry) => JSON.stringify(entry).toLowerCase()); let shown = 100;
      function renderJournal() {
        const query = input.value.trim().toLowerCase();
        const matched = entries.filter((_, index) => !query || texts[index].includes(query));
        count.textContent = `${fmt(matched.length)} entries${journal.truncated ? " · journal truncated by the application" : ""}`;
        list.replaceChildren(...matched.slice(0, shown).map((entry) => {
          const component = entry.component ?? entry.Component ?? entry.category ?? "Decision";
          const code = entry.code ?? entry.Code ?? entry.decision ?? entry.name ?? "";
          const reason = entry.reason ?? entry.Reason ?? "";
          return jsonDetails([component, code, reason].filter(Boolean).join(" · "), entry);
        }));
        more.hidden = matched.length <= shown;
        if (!matched.length) list.append(empty("No matching entries"));
      }
      input.addEventListener("input", () => { shown = 100; renderJournal(); });
      more.addEventListener("click", () => { shown += 100; renderJournal(); });
      journalSection.append(heading, count, list, more); renderJournal();
    } else { journalSection.append(heading, el("p", "subdued", "This archive has no decision journal. This is expected for older app versions.")); }
    nodes.push(journalSection); $("detail-content").replaceChildren(...nodes);
  }
  async function openDetail(id) {
    if (!id) return;
    const generation = ++state.detailGeneration;
    $("detail-title").textContent = "Telemetry archive"; $("detail-content").replaceChildren(empty("Loading details…"));
    if (!$("capture-dialog").open) $("capture-dialog").showModal();
    try { const data = await request(`/api/captures/${encodeURIComponent(id)}`); if (generation === state.detailGeneration) renderDetail(data); }
    catch (error) { if (generation === state.detailGeneration) { const note = el("p", "detail-warning", `Could not load the archive. ${error.message}`), retry = el("button", "button", "Retry"); retry.addEventListener("click", () => openDetail(id)); $("detail-content").replaceChildren(note, retry); } }
  }
  async function refresh(manual = false, listOnly = false) {
    if (!state.summary || state.summary.days !== state.days) listOnly = false;
    const generation = ++state.generation;
    state.loading = true; $("refresh").classList.add("busy"); $("refresh").disabled = true;
    if (manual) setSync("Updating data…", "pending");
    try {
      const [summary, captures] = await Promise.all([listOnly ? Promise.resolve(null) : request(`/api/summary?${params()}`), request(`/api/captures?${params(true)}`)]);
      if (generation !== state.generation) return;
      if (summary) { state.summary = summary; renderKpis(summary); renderTimeline(summary); renderHealth(summary); renderBreakdowns(summary); renderDiagnostics(summary); }
      renderCaptures(captures); state.lastGood = new Date();
      setSync(`Updated ${state.lastGood.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`);
      $("error-banner").hidden = true;
    } catch (error) {
      if (generation !== state.generation) return;
      const banner = $("error-banner");
      banner.textContent = `Could not update data. ${error.message}.${state.lastGood ? " The last successfully loaded data remains on screen." : " Check the server connection and press refresh."}`; banner.hidden = false;
      setSync(state.lastGood ? "Disconnected · showing the latest available data" : "Data unavailable", "failed");
      if (!state.list) { $("table-state").textContent = "Could not load the archive list"; $("table-state").hidden = false; }
      if (!state.summary) { $("kpis").setAttribute("aria-busy", "false"); $("kpis").replaceChildren(empty("Statistics will appear when the server connection is restored")); $("timeline").replaceChildren(empty("No connection to the server")); }
    } finally { if (generation === state.generation) { state.loading = false; $("refresh").classList.remove("busy"); $("refresh").disabled = false; } }
  }

  document.querySelectorAll("[data-days]").forEach((button) => {
    button.setAttribute("aria-pressed", String(Number(button.dataset.days) === state.days));
    button.addEventListener("click", () => {
      state.days = Number(button.dataset.days); state.page = 1;
      document.querySelectorAll("[data-days]").forEach((item) => { const selected = item === button; item.classList.toggle("selected", selected); item.setAttribute("aria-pressed", String(selected)); });
      refresh(true);
    });
  });
  $("refresh").addEventListener("click", () => refresh(true));
  let searchTimer;
  function search() { const value = $("search").value.trim(); if (value) state.filters.q = value; else delete state.filters.q; state.page = 1; renderFilters(); refresh(false, true); }
  $("search-form").addEventListener("submit", (event) => { event.preventDefault(); clearTimeout(searchTimer); search(); });
  $("search").addEventListener("input", () => { clearTimeout(searchTimer); searchTimer = setTimeout(search, 400); });
  $("clear-filters").addEventListener("click", () => { clearTimeout(searchTimer); state.filters = {}; state.page = 1; $("search").value = ""; renderFilters(); refresh(false, true); });
  $("page-prev").addEventListener("click", () => { if (state.page > 1) { state.page -= 1; refresh(false, true); } });
  $("page-next").addEventListener("click", () => { if (state.list && state.page < state.list.pages) { state.page += 1; refresh(false, true); } });
  $("close-detail").addEventListener("click", () => $("capture-dialog").close());
  $("capture-dialog").addEventListener("click", (event) => { if (event.target === $("capture-dialog")) { const rect = event.target.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) event.target.close(); } });
  $("capture-dialog").addEventListener("close", () => { state.detailGeneration += 1; });
  document.addEventListener("visibilitychange", () => { if (!document.hidden && !state.loading) refresh(); });
  setInterval(() => { if (!document.hidden && !state.loading) refresh(); }, 30000);
  initialSkeletons(); refresh();
})();
