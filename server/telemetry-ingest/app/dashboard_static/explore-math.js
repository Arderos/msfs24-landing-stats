"use strict";
(() => {
  const finite = value => typeof value === "number" && Number.isFinite(value);
  function median(values) {
    const sorted = values.filter(finite).sort((a, b) => a - b), middle = (sorted.length - 1) / 2;
    return sorted.length ? (sorted[Math.floor(middle)] + sorted[Math.ceil(middle)]) / 2 : null;
  }
  function histogram(groups, key, count = 16) {
    const values = groups.map(group => group.filter(row => finite(row[key])));
    const all = values.flat().map(row => row[key]);
    if (!all.length) return null;
    let low = Math.min(...all), high = Math.max(...all);
    if (low === high) { const pad = Math.abs(low) * .05 || .5; low -= pad; high += pad; }
    const width = (high - low) / count;
    return { low, high, width, groups: values.map((rows, group) => {
      const bins = Array.from({ length: count }, () => []);
      rows.forEach(row => bins[Math.min(count - 1, Math.floor((row[key] - low) / width))].push(row.id));
      return { bins, n: rows.length, missing: groups[group].length - rows.length, median: median(rows.map(row => row[key])),
        percentages: bins.map(bin => rows.length ? bin.length / rows.length * 100 : 0) };
    }) };
  }
  // Every flight contributes once to a spatial cell, even when it dwells there,
  // revisits it, or has a higher recording frequency. Breaks are never joined.
  function density(tracks, xDomain, yDomain, nx = 48, ny = 24, connect = true) {
    const cells = new Map(), flights = new Set();
    const dx = (xDomain[1] - xDomain[0]) / nx, dy = (yDomain[1] - yDomain[0]) / ny;
    if (!(dx > 0 && dy > 0)) return [];
    for (const track of tracks) {
      if (flights.has(track.id)) continue;
      flights.add(track.id);
      const visited = new Map();
      function visit(x, y, value) {
        if (x < xDomain[0] || x > xDomain[1] || y < yDomain[0] || y > yDomain[1]) return;
        const ix = Math.min(nx - 1, Math.floor((x - xDomain[0]) / dx)), iy = Math.min(ny - 1, Math.floor((y - yDomain[0]) / dy));
        const id = `${ix}:${iy}`;
        if (!visited.has(id)) visited.set(id, { ix, iy, values: [] });
        if (finite(value)) visited.get(id).values.push(value);
      }
      let previous = null;
      for (const point of track.points) {
        if (!finite(point.x) || !finite(point.y)) { previous = null; continue; }
        if (connect && previous && point.run === previous.run && point.t > previous.t) {
          const steps = Math.max(1, Math.ceil(Math.max(Math.abs(point.x - previous.x) / dx, Math.abs(point.y - previous.y) / dy)));
          for (let i = 1; i < steps; i++) {
            const f = i / steps;
            visit(previous.x + (point.x - previous.x) * f, previous.y + (point.y - previous.y) * f,
              finite(previous.value) && finite(point.value) ? previous.value + (point.value - previous.value) * f : null);
          }
        }
        visit(point.x, point.y, point.value); previous = point;
      }
      for (const [id, cell] of visited) {
        if (!cells.has(id)) cells.set(id, { x: xDomain[0] + cell.ix * dx, y: yDomain[0] + cell.iy * dy, width: dx, height: dy, ids: [], values: [] });
        const result = cells.get(id); result.ids.push(track.id);
        const value = median(cell.values); if (value !== null) result.values.push(value);
      }
    }
    return [...cells.values()].map(cell => ({ ...cell, count: cell.ids.length, value: median(cell.values), valueCount: cell.values.length }));
  }
  function windProfiles(groups, keys, count = 8) {
    const winds = groups.flat().map(row => row.crosswind).filter(finite);
    if (!winds.length) return null;
    let low = Math.min(...winds), high = Math.max(...winds);
    if (low === high) { low -= .5; high += .5; }
    const width = (high - low) / count, grid = Array.from({ length: count }, (_, i) => low + (i + .5) * width);
    const cohorts = groups.map(members => {
      const unique = [...new Map(members.map(row => [row.id, row])).values()];
      const bins = Array.from({ length: count }, () => []);
      unique.filter(row => finite(row.crosswind)).forEach(row => bins[Math.min(count - 1, Math.floor((row.crosswind - low) / width))].push(row));
      const bands = Object.fromEntries(keys.map(key => [key, bins.map(rows => {
        const values = rows.map(row => row[key]).filter(finite).sort((a, b) => a - b);
        if (!values.length) return null;
        const q = fraction => { const i = (values.length - 1) * fraction; return values[Math.floor(i)] * (1 - i + Math.floor(i)) + values[Math.ceil(i)] * (i - Math.floor(i)); };
        return [values.length, values[0], q(.1), q(.5), q(.9), values.at(-1)];
      })]));
      return { axis: "wind", grid, bands, members: unique, windWidth: width };
    });
    return { limits: [low, high], cohorts };
  }
  const api = { median, histogram, density, windProfiles };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else window.FlightMath = api;
})();
