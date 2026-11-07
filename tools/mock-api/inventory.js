'use strict';
// Inventory mock API (phase 2, HLD D-23): the second source, onboarded with configuration only.
// Differs from Orders on purpose: API key in the `api_key` query parameter, integer keys, offset paging
// with a total, and a stock adjustment action that becomes a Custom API.
const { pad } = require('./data');

const STATES = ['active', 'discontinued'];
const FIELDS = ['name', 'sku', 'onHand', 'price', 'state'];

function seedItems() {
  const items = [];
  for (let i = 1; i <= 45; i++) {
    items.push({
      id: i,
      name: `Item ${pad(i, 3)}`,
      sku: `SKU-${pad(i, 3)}`,
      onHand: (i * 7) % 50,
      price: Math.round(i * 125) / 100,
      state: i % 5 === 0 ? 'discontinued' : 'active',
      updatedAt: new Date(Date.UTC(2026, 5, 1) + i * 3600_000).toISOString(),
    });
  }
  return items;
}

let items;
let nextItemId;
function resetInventory() {
  items = seedItems();
  nextItemId = items.length + 1;
}
resetInventory();

function validate(body, HttpError, creating) {
  if (body === null || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(422, 'the body must be a JSON object');
  for (const key of Object.keys(body)) if (!FIELDS.includes(key)) throw new HttpError(422, `unknown field ${key}`);
  if (creating && (typeof body.name !== 'string' || body.name === '')) throw new HttpError(422, 'name is required');
  if (body.state !== undefined && body.state !== null && !STATES.includes(body.state)) throw new HttpError(422, `state must be one of ${STATES.join(', ')}`);
  if (body.onHand !== undefined && body.onHand !== null && !Number.isInteger(body.onHand)) throw new HttpError(422, 'onHand must be an integer');
  if (body.price !== undefined && body.price !== null && typeof body.price !== 'number') throw new HttpError(422, 'price must be a number');
}

function find(id, HttpError) {
  const n = Number(id);
  const item = Number.isInteger(n) ? items.find((x) => x.id === n) : undefined;
  if (!item) throw new HttpError(404, `Item ${id} not found`);
  return item;
}

// Returns true when it handled the request. `key` is the expected api_key value, or undefined when inventory is off.
async function inventory(req, res, url, key, { send, readBody, parseJson, intParam, HttpError }) {
  const p = url.pathname;
  if (!p.startsWith('/inv/v1/')) return false;
  if (!key) throw new HttpError(503, 'inventory is not configured (MOCK_INVENTORY_KEY)');
  if (url.searchParams.get('api_key') !== key) return send(res, 401, { error: { detail: 'missing or invalid api_key' } }), true;
  const q = url.searchParams;
  const m = req.method;
  let match;

  if (m === 'GET' && p === '/inv/v1/items') {
    const limit = intParam(q, 'limit', 20, 1, 50);
    const offset = intParam(q, 'offset', 0, 0, 1e9);
    let rows = items;
    const state = q.get('state');
    if (state) rows = rows.filter((x) => x.state === state);
    const name = q.get('name');
    if (name) rows = rows.filter((x) => x.name === name);
    send(res, 200, { items: rows.slice(offset, offset + limit), total: rows.length });
    return true;
  }
  if (m === 'POST' && p === '/inv/v1/items') {
    const body = parseJson(await readBody(req));
    validate(body, HttpError, true);
    const item = { id: nextItemId++, name: body.name, sku: body.sku ?? null, onHand: body.onHand ?? 0, price: body.price ?? 0,
      state: body.state ?? 'active', updatedAt: new Date().toISOString() };
    items.push(item);
    send(res, 201, { id: item.id });
    return true;
  }
  if ((match = /^\/inv\/v1\/items\/([^/]+)$/.exec(p))) {
    const item = find(decodeURIComponent(match[1]), HttpError);
    if (m === 'GET') return send(res, 200, item), true;
    if (m === 'PATCH') {
      const body = parseJson(await readBody(req));
      validate(body, HttpError, false);
      Object.assign(item, body, { updatedAt: new Date().toISOString() });
      return send(res, 204), true;
    }
    if (m === 'DELETE') { items.splice(items.indexOf(item), 1); return send(res, 204), true; }
  }
  if (m === 'POST' && (match = /^\/inv\/v1\/items\/([^/]+)\/adjust$/.exec(p))) {
    const item = find(decodeURIComponent(match[1]), HttpError);
    const body = parseJson(await readBody(req));
    if (!body || !Number.isInteger(body.delta)) throw new HttpError(422, 'delta must be an integer');
    if (item.onHand + body.delta < 0) throw new HttpError(422, `cannot remove ${-body.delta}: only ${item.onHand} on hand`);
    item.onHand += body.delta;
    item.updatedAt = new Date().toISOString();
    send(res, 200, { onHand: item.onHand, adjustedAt: item.updatedAt });
    return true;
  }
  throw new HttpError(404, `no route for ${m} ${p}`);
}

module.exports = { inventory, resetInventory };
