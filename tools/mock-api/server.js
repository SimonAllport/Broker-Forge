'use strict';
// Orders mock API for the BrokerForge contract tests (descriptor spec 13 shapes), plus the Inventory API
// under /inv/v1 (inventory.js) and the per-user Tickets API under /tk (tickets.js). Node built-ins only. Requires MOCK_API_KEY; every Orders request must send
// it as X-Api-Key. Inventory requests send MOCK_INVENTORY_KEY as the api_key query parameter instead;
// Tickets requests carry a per-user credential (see tickets.js).
const http = require('node:http');
const { seed, pad } = require('./data');
const { inventory, resetInventory } = require('./inventory');
const { tickets, resetTickets, ticketStats } = require('./tickets');

const API_KEY = process.env.MOCK_API_KEY;
if (!API_KEY) {
  console.error('MOCK_API_KEY is required');
  process.exit(2);
}
const INVENTORY_KEY = process.env.MOCK_INVENTORY_KEY;
const SLOW_MS = Number(process.env.MOCK_SLOW_MS || 5000);
// Which APIs this process serves (default all). The dev environment runs the Tickets API alone on an external
// app, because the browser must reach its sign-in page, and Orders and Inventory on an internal one.
const APIS = new Set((process.env.MOCK_APIS || 'orders,inventory,tickets').split(',').map((a) => a.trim()));
const SORT_FIELDS = { reference: (o) => o.reference, placedAt: (o) => o.placedAt };

let orders;
let nextId;
let idempotency;
let failed503;
let lastEcho;
function reset() {
  orders = seed();
  nextId = orders.length + 1;
  idempotency = new Map();
  failed503 = false;
  lastEcho = undefined;
  resetInventory();
  resetTickets();
}
reset();

class HttpError extends Error {
  constructor(status, detail) { super(detail); this.status = status; }
}

function send(res, status, body, headers = {}) {
  const text = body === undefined ? '' : JSON.stringify(body);
  res.writeHead(status, { ...(text ? { 'content-type': 'application/json' } : {}), ...headers });
  res.end(text);
}

function intParam(q, name, def, min, max) {
  const raw = q.get(name);
  if (raw === null || raw === '') return def;
  const n = Number(raw);
  if (!Number.isInteger(n) || n < min || n > max) throw new HttpError(400, `${name} must be an integer from ${min} to ${max}`);
  return n;
}

// Filters and sort shared by every list route. Fault values arrive through `customer`.
async function listOrders(q) {
  const customer = q.get('customer');
  if (customer === '__fail500') throw new HttpError(500, 'internal failure (injected)');
  if (customer === '__teapot') throw new HttpError(418, 'teapot (injected)');
  if (customer === '__fail503once' && !failed503) { failed503 = true; throw new HttpError(503, 'unavailable (injected)'); }
  if (customer === '__slow') await new Promise((r) => setTimeout(r, SLOW_MS));

  let rows = orders;
  const status = q.get('status');
  if (status) { const set = new Set(status.split(',')); rows = rows.filter((o) => set.has(o.state.code)); }
  if (customer && !customer.startsWith('__')) rows = rows.filter((o) => o.customer.ref === customer);
  const sort = q.get('sort');
  if (sort) {
    const desc = sort.startsWith('-');
    const key = SORT_FIELDS[desc ? sort.slice(1) : sort];
    if (!key) throw new HttpError(400, `cannot sort by ${sort}`);
    rows = [...rows].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0) * (desc ? -1 : 1));
  }
  return rows;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function parseJson(text) {
  if (!text) return {};
  try { return JSON.parse(text); } catch { throw new HttpError(400, 'body is not valid JSON'); }
}

function merge(target, patch) {
  for (const [k, v] of Object.entries(patch)) {
    if (v && typeof v === 'object' && !Array.isArray(v) && target[k] && typeof target[k] === 'object') merge(target[k], v);
    else target[k] = v;
  }
  return target;
}

const STATES = ['open', 'shipped', 'cancelled'];

function create(body, idemKey) {
  if (idemKey && idempotency.has(idemKey)) return idempotency.get(idemKey);
  if (!body.customer || !body.customer.ref) throw new HttpError(422, 'customer.ref is required');
  const code = body.state && body.state.code != null ? body.state.code : 'open';
  if (!STATES.includes(code)) throw new HttpError(422, `state.code must be one of ${STATES.join(', ')}`);
  const n = nextId++;
  const order = {
    id: `ord_${pad(n, 4)}`,
    reference: `REF-${pad(n, 4)}`,
    state: { code },
    customer: { ref: body.customer.ref },
    placedAt: new Date().toISOString(),
    lines: Array.isArray(body.lines) ? body.lines : [],
    totals: { gross: body.totals && typeof body.totals.gross === 'number' ? body.totals.gross : 0 },
    ...(body.placedBy ? { placedBy: body.placedBy } : {}),
  };
  orders.push(order);
  if (idemKey) idempotency.set(idemKey, order.id);
  return order.id;
}

function findIndex(id) {
  const i = orders.findIndex((o) => o.id === id);
  if (i < 0) throw new HttpError(404, `Order ${id} not found`);
  return i;
}

async function route(req, res) {
  const url = new URL(req.url, 'http://mock');
  const q = url.searchParams;
  const p = url.pathname;
  const m = req.method;
  let match;

  if (m === 'POST' && p === '/__reset') { reset(); return send(res, 204); }
  if (m === 'GET' && p === '/__tk/stats') return send(res, 200, ticketStats());

  if (p.startsWith('/v2/echo/')) {
    const headers = { ...req.headers };
    delete headers['x-api-key'];
    const rawQuery = req.url.includes('?') ? req.url.slice(req.url.indexOf('?') + 1) : '';
    lastEcho = {
      method: m,
      path: req.url.split('?')[0],
      query: Object.fromEntries(q),
      rawQuery,
      headers,
      contentType: req.headers['content-type'] ?? null,
      body: await readBody(req),
    };
    // `id` (the decoded last path segment) gives echo descriptors a key; `rows` lets a list read it as empty.
    const segments = lastEcho.path.split('/');
    return send(res, 200, { ...lastEcho, id: decodeURIComponent(segments[segments.length - 1]), rows: [] });
  }
  if (m === 'GET' && p === '/__echo/last') {
    if (!lastEcho) throw new HttpError(404, 'no echo request since the last reset');
    return send(res, 200, lastEcho);
  }

  if (m === 'GET' && p === '/v2/orders') {
    const limit = intParam(q, 'limit', 20, 1, 100);
    const offset = q.get('cursor') ? Number(Buffer.from(q.get('cursor'), 'base64url').toString()) : 0;
    if (!Number.isInteger(offset) || offset < 0) throw new HttpError(400, 'invalid cursor');
    const rows = await listOrders(q);
    const data = rows.slice(offset, offset + limit);
    const more = offset + limit < rows.length;
    return send(res, 200, { data, meta: { nextCursor: more ? Buffer.from(String(offset + limit)).toString('base64url') : null } });
  }
  if (m === 'GET' && p === '/v2/orders-offset') {
    const offset = intParam(q, 'offset', 0, 0, 1e9);
    const limit = intParam(q, 'limit', 20, 1, 100);
    const rows = await listOrders(q);
    return send(res, 200, { items: rows.slice(offset, offset + limit), total: rows.length });
  }
  if (m === 'GET' && p === '/v2/orders-paged') {
    const page = intParam(q, 'page', 1, 1, 1e9);
    const size = intParam(q, 'size', 20, 1, 100);
    const rows = await listOrders(q);
    return send(res, 200, { results: rows.slice((page - 1) * size, page * size) });
  }
  if (m === 'GET' && p === '/v2/orders-link') {
    const limit = intParam(q, 'limit', 20, 1, 100);
    const offset = intParam(q, 'offset', 0, 0, 1e9);
    const rows = await listOrders(q);
    const headers = {};
    if (offset + limit < rows.length) {
      const next = new URL(`http://${req.headers.host}${p}`);
      for (const [k, v] of q) if (k !== 'offset') next.searchParams.set(k, v);
      next.searchParams.set('offset', String(offset + limit));
      headers.link = `<${next}>; rel="next"`;
    }
    return send(res, 200, rows.slice(offset, offset + limit), headers);
  }
  if (m === 'GET' && p === '/v2/orders-all') return send(res, 200, { data: await listOrders(q) });
  if (m === 'GET' && p === '/v2/broken') return send(res, 200, { notData: [] });
  if (m === 'GET' && p === '/v2/soft-error') return send(res, 200, { ok: false, message: 'soft failure inside a 200' });

  if ((match = /^\/v2\/orders\/([^/]+)$/.exec(p))) {
    const id = decodeURIComponent(match[1]);
    if (m === 'GET') return send(res, 200, orders[findIndex(id)]);
    if (m === 'PATCH') {
      const i = findIndex(id);
      const patch = parseJson(await readBody(req));
      delete patch.id;
      merge(orders[i], patch);
      return send(res, 204);
    }
    if (m === 'DELETE') { orders.splice(findIndex(id), 1); return send(res, 204); }
  }
  if (m === 'POST' && (match = /^\/v2\/accounts\/[^/]+\/(orders|orders-location)$/.exec(p))) {
    const id = create(parseJson(await readBody(req)), req.headers['idempotency-key']);
    if (match[1] === 'orders-location') return send(res, 201, undefined, { location: `/v2/orders/${id}` });
    return send(res, 201, { id });
  }
  throw new HttpError(404, `no route for ${m} ${p}`);
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://mock');
    if (APIS.has('inventory') && await inventory(req, res, url, INVENTORY_KEY, { send, readBody, parseJson, intParam, HttpError })) return;
    if (APIS.has('tickets') && await tickets(req, res, url, { send, readBody, parseJson, intParam, HttpError })) return;
    if (!APIS.has('orders')) return send(res, 404, { error: { detail: `no route for ${req.method} ${url.pathname}` } });
    if (req.headers['x-api-key'] !== API_KEY) return send(res, 401, { error: { detail: 'missing or invalid api key' } });
    await route(req, res);
  } catch (e) {
    if (e instanceof HttpError) return send(res, e.status, { error: { detail: e.message } });
    console.error(e);
    send(res, 500, { error: { detail: 'unexpected mock failure' } });
  }
});

server.listen(Number(process.env.PORT || 0), () => console.log(`listening ${server.address().port}`));
