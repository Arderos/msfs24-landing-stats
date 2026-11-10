const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const FlightMath = require('../app/dashboard_static/explore-math.js');

class Element {
  constructor(value = '', text = '') {
    this.value = value; this.textContent = text; this.checked = false; this.children = [];
    this.attributes = {}; this.handlers = {};
    this.classList = { add() {}, remove() {} };
  }
  get options() { return this.children; }
  get selectedOptions() { return this.children.filter(option => option.value === this.value); }
  append(...children) { this.children.push(...children); }
  add(child) { this.append(child); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, handler) { this.handlers[name] = handler; }
}

function explorer() {
  const nodes = new Map(), requests = [], frames = new Map();
  const node = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  let nextFrame = 0, now = 0, fail = false, hold, respond;
  const response = url => {
    const query = new URL(url, 'https://example.test').searchParams;
    return { axis: query.get('axis'), aircraft: query.get('aircraft'), bands: {}, grid: [], members: [],
      tracks: [], individuals: [], included: 0, matched: 0, limit: 250, index: { pending: 0 } };
  };
  const context = vm.createContext({
    document: { getElementById: node, createElement: () => new Element(), createElementNS: () => new Element(), querySelectorAll: () => [] },
    Option: function(text, value) { return new Element(value, text); },
    URLSearchParams, FlightMath, performance: { now: () => now },
    requestAnimationFrame: callback => { frames.set(++nextFrame, callback); return nextFrame; },
    cancelAnimationFrame: id => frames.delete(id),
    fetch: async url => {
      requests.push(url);
      if (hold) { const wait = hold; hold = null; await wait; }
      return { ok: !fail, status: fail ? 503 : 200, json: async () => fail ? { detail: 'unavailable' } : respond ? respond(url, response(url)) : response(url) };
    }
  });
  const source = fs.readFileSync(path.join(__dirname, '../app/dashboard_static/explore.js'), 'utf8').replace(/\r\n/g, '\n');
  // Keep browser handlers/renderers intact; replace automatic initial loading
  // with access to the controller for deterministic request/animation tests.
  const startup = '  loadCatalog();\n})();';
  assert.ok(source.includes(startup));
  vm.runInContext(source.replace(startup,
    '  globalThis.controller = { state, loadCatalog, loadCohorts, renderWorld, renderDistributions, renderImpact, cohortLabel, play, stop };\n})();'), context);
  node('alignment').value = 'agl'; node('channel-set').value = 'approach';
  const controller = context.controller;
  controller.state.catalog = { analytics_errors: 0, channels: Object.fromEntries(
    ['stickP', 'stickR', 'stickY', 'cmdP', 'cmdR', 'cmdY'].map(key => [key, { label: key, unit: '%' }])) };
  return { ...controller, node, requests, frames,
    respondWith(callback) { respond = callback; },
    failRequests(value) { fail = value; }, holdRequest(promise) { hold = promise; },
    frame(time) { now = time; const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(time)); } };
}

test('signature queries and cohort labels retain applied filters until the next successful apply', async () => {
  const app = explorer();
  app.node('aircraft').value = 'Aircraft A'; app.node('compare_by').value = 'aircraft';
  app.node('compare_value').add(new Element('Aircraft B', 'Aircraft B')); app.node('compare_value').value = 'Aircraft B';
  await app.loadCohorts();
  app.node('aircraft').value = 'Draft A'; app.node('compare_value').value = 'Draft B';
  assert.equal(app.cohortLabel(), 'Aircraft A'); assert.equal(app.cohortLabel(true), 'Aircraft B');
  await app.renderImpact();
  const queries = app.requests.map(url => new URL(url, 'https://example.test').searchParams);
  assert.deepEqual(queries.map(query => query.get('aircraft')), ['Aircraft A', 'Aircraft B', 'Aircraft A', 'Aircraft B']);
  assert.deepEqual(queries.map(query => query.get('axis')), ['agl', 'agl', 't', 't']);
  assert.equal(app.state.selection.queryA.get('axis'), 'agl');
  assert.equal(app.state.a.aircraft, app.state.impactA.aircraft);
  assert.equal(app.state.b.aircraft, app.state.impactB.aircraft);
});

function descendants(node) { return [node, ...node.children.flatMap(descendants)]; }

test('selecting a B histogram preserves cohort A and only narrows B', async () => {
  const app = explorer();
  const records = [{ id: 'a1', aircraft: 'Aircraft A', g: 1.1 },
    { id: 'b1', aircraft: 'Aircraft B', g: 1.2 }, { id: 'b2', aircraft: 'Aircraft B', g: 2.3 }];
  app.respondWith((url, response) => {
    const query = new URL(url, 'https://example.test').searchParams;
    const ids = query.get('ids')?.split(',');
    const members = records.filter(record => record.aircraft === query.get('aircraft') && (!ids || ids.includes(record.id)));
    return { ...response, members, included: members.length, matched: members.length };
  });
  app.node('aircraft').value = 'Aircraft A'; app.node('compare_by').value = 'aircraft';
  app.node('compare_value').add(new Element('Aircraft B', 'Aircraft B')); app.node('compare_value').value = 'Aircraft B';
  await app.loadCohorts(); app.renderDistributions();
  const bar = descendants(app.node('distributions')).find(node => node.attributes.class === 'histogram-bar cohort-b');
  assert.ok(bar, 'the rendered comparison contains a clickable B histogram');
  await bar.handlers.click();
  assert.equal(app.state.a.included, 1); assert.equal(app.state.b.included, 1);
  const queries = app.requests.slice(-2).map(url => new URL(url, 'https://example.test').searchParams);
  assert.equal(queries[0].get('ids'), null); assert.ok(queries[1].get('ids'));
});

test('map airport selection clears an identically named runway', async () => {
  const app = explorer();
  app.state.catalog.filters = { runways: [['AAAA', '27'], ['BBBB', '27']] };
  app.node('airport').value = 'AAAA'; app.node('runway').value = '27';
  app.state.land = { features: [] };
  app.state.a = { members: [{ id: 'b1', airport: 'BBBB', lat: 10, lon: 20 }] };
  await app.renderWorld();
  const airport = descendants(app.node('world-map')).find(node => node.attributes.class === 'airport-dot');
  await airport.handlers.click();
  const query = new URL(app.requests[0], 'https://example.test').searchParams;
  assert.equal(query.get('airport'), 'BBBB'); assert.equal(query.get('runway'), null);
});

test('catalog filters cover later pages without loading their recording metadata', async () => {
  const app = explorer();
  app.respondWith((url, response) => {
    const parsed = new URL(url, 'https://example.test');
    if (!parsed.pathname.endsWith('/catalog')) return response;
    return { channels: app.state.catalog.channels, analytics_errors: 0, index: { pending: 0 },
      next_page: parsed.searchParams.has('page') ? null : 2,
      filters: { aircraft: [['First aircraft', 250], ['Late aircraft', 1]], airports: ['AAAA'],
        runways: [['AAAA', '27']], creators: [], devices: [] },
      landings: [{ aircraft: 'First aircraft', contact: 1, devices: [], airport: 'AAAA', runway: '27' }] };
  });
  await app.loadCatalog();
  assert.deepEqual(app.node('aircraft').options.map(option => option.value), ['', 'First aircraft', 'Late aircraft']);
  assert.equal(app.requests.length, 2);
  assert.match(app.requests[1], /cohort/);
});

test('failed initial catalog leaves comparison and apply controls usable', async () => {
  const app = explorer(), channels = app.state.catalog.channels;
  app.state.catalog = null;
  app.failRequests(true); await app.loadCatalog();
  for (const key of ['aircraft', 'airport', 'runway', 'creator', 'device']) {
    app.node('compare_by').value = key;
    assert.doesNotThrow(() => app.node('compare_by').handlers.change());
    assert.deepEqual(app.node('compare_value').options.map(option => option.value), ['']);
  }
  await app.loadCohorts();
  assert.equal(app.node('reload').disabled, false);
  assert.match(app.node('flight-error').textContent, /unavailable/);
  app.failRequests(false);
  app.respondWith((url, response) => url.includes('/catalog?') ? {
    channels, analytics_errors: 0, index: { pending: 0 }, total: 1250, next_page: 2,
    filters: { aircraft: [['First page aircraft', 250], ['Later aircraft', 1000]], airports: [],
      runways: [], creators: [], devices: [] },
    landings: [{ aircraft: 'First page aircraft', contact: 1, devices: [] }]
  } : response);
  await app.node('reload').handlers.click();
  assert.equal(app.node('aircraft').value, 'Later aircraft');
  assert.equal(app.state.a.aircraft, 'Later aircraft');
  assert.equal(app.node('flight-error').hidden, true);
});

test('period changes discard old catalog responses before applying filters or cohorts', async () => {
  const app = explorer(), channels = app.state.catalog.channels;
  app.respondWith((url, response) => {
    const parsed = new URL(url, 'https://example.test');
    if (!parsed.pathname.endsWith('/catalog')) return response;
    const days = parsed.searchParams.get('days'), aircraft = `Aircraft ${days}`;
    return { channels, analytics_errors: 0, index: { pending: 0 }, days,
      next_page: days === '7' && !parsed.searchParams.has('page') ? 2 : null,
      filters: { aircraft: [[aircraft, 1]], airports: [], runways: [], creators: [], devices: [] },
      landings: [{ aircraft, contact: 1, devices: [] }] };
  });
  let release;
  app.holdRequest(new Promise(resolve => { release = resolve; }));
  app.node('days').value = '7'; const old = app.loadCatalog();
  await new Promise(setImmediate);
  app.node('days').value = '30'; const current = app.node('days').handlers.change();
  release(); await Promise.all([old, current]);
  assert.equal(app.state.catalog.days, '30');
  assert.deepEqual(app.node('aircraft').options.map(option => option.value), ['', 'Aircraft 30']);
  assert.ok(app.requests.filter(url => url.includes('/cohort?')).every(url => new URL(url, 'https://example.test').searchParams.get('days') === '30'));
  assert.ok(app.requests.every(url => !new URL(url, 'https://example.test').searchParams.has('page')));
});

test('edits during a request and a failed apply cannot relabel the last loaded cohort', async () => {
  const app = explorer();
  app.node('aircraft').value = 'Applied';
  let release;
  app.holdRequest(new Promise(resolve => { release = resolve; }));
  const loading = app.loadCohorts();
  app.node('aircraft').value = 'Edited while loading'; release(); await loading;
  assert.equal(app.state.a.aircraft, 'Applied'); assert.equal(app.cohortLabel(), 'Applied');
  app.failRequests(true); await app.loadCohorts();
  assert.equal(app.state.a.aircraft, 'Applied'); assert.equal(app.cohortLabel(), 'Applied');
  assert.match(app.node('flight-status').textContent, /could not be loaded/);
  app.failRequests(false); await app.renderImpact();
  assert.equal(app.state.impactA.aircraft, 'Applied');
});

test('rapid stop and play leaves one animation and uses the new playback speed', () => {
  const app = explorer();
  app.state.flight = { series: { t: [0, 1, 2, 3] } };
  app.node('scrubber').value = 0; app.node('play-speed').value = 1;
  let updates = 0; app.state.replayPlots.push(() => { updates++; });
  app.play(); app.stop(); app.node('play-speed').value = 2; app.play();
  assert.equal(app.frames.size, 1);
  app.frame(600);
  assert.equal(updates, 1); assert.equal(Number(app.node('scrubber').value), 1); assert.equal(app.frames.size, 1);
  app.stop(); assert.equal(app.frames.size, 0);
  app.frame(1000); assert.equal(updates, 1);
});

test('replay completion clears playback and restarting schedules one frame', () => {
  const app = explorer();
  app.state.flight = { series: { t: [0, 1] } };
  app.node('scrubber').value = 0; app.node('play-speed').value = 1;
  app.play(); app.frame(1000);
  assert.equal(app.state.playing, false); assert.equal(app.frames.size, 0);
  app.play(); assert.equal(Number(app.node('scrubber').value), 0); assert.equal(app.frames.size, 1);
});
