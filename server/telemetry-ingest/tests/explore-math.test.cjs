const { test } = require('node:test');
const assert = require('node:assert/strict');
const { density, histogram, windProfiles } = require('../app/dashboard_static/explore-math.js');

test('density gives each flight one vote regardless of repeated samples or uploads', () => {
  const a = { id: 'a', points: Array.from({ length: 1000 }, (_, i) => ({ x: .2, y: .2, value: 10, t: i, run: 0 })) };
  const b = { id: 'b', points: [{ x: .2, y: .2, value: 100, t: 0, run: 0 }] };
  const [cell] = density([a, a, b], [0, 1], [0, 1], 1, 1);
  assert.equal(cell.count, 2); assert.equal(cell.value, 55); assert.equal(cell.valueCount, 2);
});

test('density fills traversed cells while respecting gaps, segments and reverse time', () => {
  const first = { x: .1, y: .5, t: 0, run: 0, value: 10 };
  const last = { x: .9, y: .5, t: 1, run: 0, value: 30 };
  const cells = points => density([{ id: 'a', points }], [0, 1], [0, 1], 4, 1);
  assert.equal(cells([first, last]).length, 4);
  assert.equal(cells([first, { x: null, y: .5, t: .5, run: 0 }, last]).length, 2);
  assert.equal(cells([first, { ...last, run: 1 }]).length, 2);
  assert.equal(cells([first, { ...last, t: -1 }]).length, 2);
});

test('unavailable colour values stay null and upper-bound coordinates remain visible', () => {
  const [cell] = density([{ id: 'a', points: [{ x: 1, y: 1, value: null }] }], [0, 1], [0, 1], 4, 4, false);
  assert.equal(cell.count, 1); assert.equal(cell.value, null); assert.equal(cell.valueCount, 0);
  assert.equal(cell.x, .75); assert.equal(cell.y, .75);
});

test('histograms share bins, normalize each cohort and exclude missing observations', () => {
  const rows = values => values.map((v, i) => ({ id: String(i), v }));
  const result = histogram([rows([0, 10, null]), rows([0, 0, 0, 10])], 'v', 2);
  assert.deepEqual([result.low, result.high], [0, 10]);
  assert.deepEqual(result.groups.map(g => g.percentages), [[50, 50], [75, 25]]);
  assert.deepEqual(result.groups.map(g => [g.n, g.missing, g.median]), [[2, 1, 5], [4, 0, 0]]);
  assert.deepEqual(result.groups[0].bins, [['0'], ['1']]);
});

test('constant and entirely missing distributions remain well-defined', () => {
  const result = histogram([[{ id: 'a', v: 0 }], []], 'v');
  assert.ok(result.low < 0 && result.high > 0);
  assert.equal(result.groups[0].percentages.reduce((a, b) => a + b), 100);
  assert.equal(result.groups[1].median, null);
  assert.equal(histogram([[{ v: null }], []], 'v'), null);
});


test('wind bins pair contact observations, preserve signed wind and give one vote per flight', () => {
  const a = { id: 'a', crosswind: -10, bank: 2 };
  const result = windProfiles([[a, a, { id: 'b', crosswind: -10, bank: 8 }, { id: 'c', crosswind: 10, bank: null }],
    [{ id: 'd', crosswind: 10, bank: -4 }, { id: 'e', crosswind: null, bank: 999 }]], ['bank'], 2);
  assert.deepEqual(result.limits, [-10, 10]);
  assert.deepEqual(result.cohorts[0].grid, [-5, 5]);
  assert.deepEqual(result.cohorts[0].bands.bank[0], [2, 2, 2.6, 5, 7.4, 8]);
  assert.equal(result.cohorts[0].bands.bank[1], null);
  assert.deepEqual(result.cohorts[1].bands.bank[1], [1, -4, -4, -4, -4, -4]);
});

test('constant wind stays finite; absent wind has no fabricated profile', () => {
  assert.equal(windProfiles([[{ id: 'a', crosswind: null, bank: 5 }]], ['bank']), null);
  const result = windProfiles([[{ id: 'a', crosswind: 0, bank: 5 }]], ['bank']);
  assert.equal(result.cohorts[0].bands.bank.filter(Boolean).length, 1);
  assert.ok(result.cohorts[0].grid.every(Number.isFinite));
});
