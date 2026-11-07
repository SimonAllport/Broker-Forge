'use strict';
const { test, before, after, beforeEach } = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const path = require('node:path');

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');

const KEY = 'bf-test-dummy-key';
const INV_KEY = 'bf-test-dummy-inventory-key';
// Tickets (phase 3): dummy client secret and user passwords; a throwaway RSA key stands in for Entra.
const TK = { secret: 'bf-test-tk-client-secret', alice: 'bf-test-alice-pw', bob: 'bf-test-bob-pw', redirect: 'https://bf-host.example/connect/callback' };
const ENTRA = { aud: 'api://bf-test-tickets', aliceOid: 'a11ce000-0000-0000-0000-000000000001', bobOid: 'b0b00000-0000-0000-0000-000000000002' };
const signing = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
const jwksFile = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'bf-jwks-')), 'jwks.json');
fs.writeFileSync(jwksFile, JSON.stringify({ keys: [{ ...signing.publicKey.export({ format: 'jwk' }), kid: 'bf-test', use: 'sig' }] }));

function entraToken(claims, key = signing.privateKey, kid = 'bf-test') {
  const enc = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const now = Math.floor(Date.now() / 1000);
  const body = `${enc({ alg: 'RS256', typ: 'JWT', kid })}.${enc({ aud: ENTRA.aud, exp: now + 600, nbf: now - 10, ...claims })}`;
  return `${body}.${crypto.sign('RSA-SHA256', Buffer.from(body), key).toString('base64url')}`;
}
let child;
let base;

function start(env) {
  return new Promise((resolve, reject) => {
    const proc = spawn(process.execPath, [path.join(__dirname, 'server.js')], {
      env: { ...process.env, PORT: '0', MOCK_SLOW_MS: '300', ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let out = '';
    proc.stdout.on('data', (d) => {
      out += d;
      const m = /listening (\d+)/.exec(out);
      if (m) resolve({ proc, port: Number(m[1]) });
    });
    proc.on('exit', (code) => reject(Object.assign(new Error(`exited ${code}`), { code })));
  });
}

async function call(method, p, { body, headers = {}, key = KEY } = {}) {
  const h = { ...headers };
  if (key) h['x-api-key'] = key;
  if (body !== undefined) h['content-type'] = 'application/json';
  const res = await fetch(base + p, { method, headers: h, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' });
  const text = await res.text();
  let json;
  try { json = text ? JSON.parse(text) : undefined; } catch { json = undefined; }
  return { status: res.status, headers: res.headers, json, text };
}

before(async () => {
  const s = await start({
    MOCK_API_KEY: KEY, MOCK_INVENTORY_KEY: INV_KEY,
    MOCK_TK_CLIENT_SECRET: TK.secret, MOCK_TK_ALICE_PASSWORD: TK.alice, MOCK_TK_BOB_PASSWORD: TK.bob, MOCK_TK_REDIRECT_URIS: TK.redirect,
    MOCK_ENTRA_AUDIENCE: ENTRA.aud, MOCK_ENTRA_JWKS_FILE: jwksFile, MOCK_ENTRA_USERS: `${ENTRA.aliceOid}:alice,${ENTRA.bobOid}:bob`,
  });
  child = s.proc;
  base = `http://127.0.0.1:${s.port}`;
});
after(() => child.kill());
beforeEach(async () => { assert.equal((await call('POST', '/__reset')).status, 204); });

test('exits when MOCK_API_KEY is missing', async () => {
  await assert.rejects(start({ MOCK_API_KEY: '' }), (e) => e.code !== 0);
});

test('rejects missing api key', async () => {
  assert.equal((await call('GET', '/v2/orders', { key: null })).status, 401);
  assert.equal((await call('GET', '/v2/orders', { key: 'wrong' })).status, 401);
});

test('cursor pages cover all 120 orders exactly once', async () => {
  const seen = new Set();
  let cursor = null;
  let calls = 0;
  do {
    const r = await call('GET', `/v2/orders?limit=50${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
    assert.equal(r.status, 200);
    for (const o of r.json.data) { assert.ok(!seen.has(o.id), `duplicate ${o.id}`); seen.add(o.id); }
    cursor = r.json.meta.nextCursor;
    calls++;
  } while (cursor);
  assert.equal(seen.size, 120);
  assert.equal(calls, 3);
});

test('limit over 100 is rejected', async () => {
  assert.equal((await call('GET', '/v2/orders?limit=101')).status, 400);
});

test('status csv filters', async () => {
  const r = await call('GET', '/v2/orders?status=open,shipped&limit=100');
  assert.ok(r.json.data.every((o) => ['open', 'shipped'].includes(o.state.code)));
  const all = await call('GET', '/v2/orders-all?status=open');
  assert.equal(all.json.data.length, 40);
  const cust = await call('GET', '/v2/orders-all?customer=CUST-01');
  assert.equal(cust.json.data.length, 12);
});

test('sort descending', async () => {
  const r = await call('GET', '/v2/orders?sort=-reference&limit=3');
  assert.deepEqual(r.json.data.map((o) => o.reference), ['REF-0120', 'REF-0119', 'REF-0118']);
  assert.equal((await call('GET', '/v2/orders?sort=bogus')).status, 400);
});

test('offset, page number and link variants page through all orders', async () => {
  const off = await call('GET', '/v2/orders-offset?offset=100&limit=50');
  assert.equal(off.json.items.length, 20);
  assert.equal(off.json.total, 120);
  const paged = await call('GET', '/v2/orders-paged?page=3&size=50');
  assert.equal(paged.json.results.length, 20);
  let url = '/v2/orders-link?limit=50';
  let n = 0;
  while (url) {
    const r = await call('GET', url);
    assert.ok(Array.isArray(r.json));
    n += r.json.length;
    const link = r.headers.get('link');
    url = link ? new URL(/<([^>]+)>;\s*rel="next"/.exec(link)[1]).pathname + new URL(/<([^>]+)>/.exec(link)[1]).search : null;
  }
  assert.equal(n, 120);
});

test('retrieve, update and delete', async () => {
  const got = await call('GET', '/v2/orders/ord_0001');
  assert.equal(got.json.reference, 'REF-0001');
  assert.equal((await call('GET', '/v2/orders/ord_9999')).json.error.detail.includes('ord_9999'), true);
  assert.equal((await call('PATCH', '/v2/orders/ord_0001', { body: { customer: { ref: 'CUST-99' } } })).status, 204);
  const after = await call('GET', '/v2/orders/ord_0001');
  assert.equal(after.json.customer.ref, 'CUST-99');
  assert.equal(after.json.reference, 'REF-0001');
  assert.equal((await call('DELETE', '/v2/orders/ord_0001')).status, 204);
  assert.equal((await call('GET', '/v2/orders/ord_0001')).status, 404);
  assert.equal((await call('DELETE', '/v2/orders/ord_0001')).status, 404);
});

test('idempotency key replays', async () => {
  const body = { customer: { ref: 'CUST-02' }, lines: [{ sku: 'A', qty: 1 }] };
  const a = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k1' } });
  const b = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k1' } });
  assert.equal(a.status, 201);
  assert.equal(a.json.id, b.json.id);
  const c = await call('POST', '/v2/accounts/acc1/orders', { body, headers: { 'idempotency-key': 'k2' } });
  assert.notEqual(c.json.id, a.json.id);
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.customer.ref, 'CUST-02');
  const bad = await call('POST', '/v2/accounts/acc1/orders', { body: { lines: [] } });
  assert.equal(bad.status, 422);
  assert.match(bad.json.error.detail, /customer\.ref/);
});

test('create keeps a numeric totals.gross and defaults it to 0', async () => {
  const a = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, totals: { gross: 12.5 } } });
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.totals.gross, 12.5);
  const b = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, totals: { gross: null } } });
  assert.equal((await call('GET', `/v2/orders/${b.json.id}`)).json.totals.gross, 0);
});

test('create takes state.code and defaults it to open', async () => {
  const a = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, state: { code: 'shipped' } } });
  assert.equal((await call('GET', `/v2/orders/${a.json.id}`)).json.state.code, 'shipped');
  const b = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' } } });
  assert.equal((await call('GET', `/v2/orders/${b.json.id}`)).json.state.code, 'open');
  const c = await call('POST', '/v2/accounts/acc1/orders', { body: { customer: { ref: 'CUST-04' }, state: { code: 'lost' } } });
  assert.equal(c.status, 422);
});

test('location create', async () => {
  const r = await call('POST', '/v2/accounts/acc1/orders-location', { body: { customer: { ref: 'CUST-03' } } });
  assert.equal(r.status, 201);
  assert.match(r.headers.get('location'), /^\/v2\/orders\/ord_\d{4}$/);
  assert.equal(r.text, '');
});

test('fault values', async () => {
  assert.equal((await call('GET', '/v2/orders?customer=__fail500')).status, 500);
  assert.equal((await call('GET', '/v2/orders?customer=__teapot')).status, 418);
  assert.equal((await call('GET', '/v2/orders?customer=__fail503once')).status, 503);
  assert.equal((await call('GET', '/v2/orders?customer=__fail503once')).status, 200);
  const t0 = Date.now();
  assert.equal((await call('GET', '/v2/orders?customer=__slow')).status, 200);
  assert.ok(Date.now() - t0 >= 280);
  assert.deepEqual((await call('GET', '/v2/broken')).json, { notData: [] });
  assert.equal((await call('GET', '/v2/soft-error')).json.ok, false);
});

test('echo returns the request as received without the api key', async () => {
  const r = await call('POST', '/v2/echo/a%2Fb?x=1&y=two', { body: { hello: 'world' }, headers: { 'x-custom': 'v' } });
  assert.equal(r.json.method, 'POST');
  assert.equal(r.json.path, '/v2/echo/a%2Fb');
  assert.deepEqual(r.json.query, { x: '1', y: 'two' });
  assert.equal(r.json.rawQuery, 'x=1&y=two');
  assert.equal(r.json.headers['x-custom'], 'v');
  assert.equal(r.json.headers['x-api-key'], undefined);
  assert.equal(r.json.contentType, 'application/json');
  assert.equal(r.json.body, '{"hello":"world"}');
  assert.equal(r.json.id, 'a/b');
});

test('last echo is kept for contract tests and echo carries an empty rows list', async () => {
  assert.equal((await call('GET', '/__echo/last')).status, 404);
  const r = await call('PUT', '/v2/echo/x?q=1', { body: { a: 1 } });
  assert.deepEqual(r.json.rows, []);
  const last = await call('GET', '/__echo/last');
  assert.equal(last.json.method, 'PUT');
  assert.equal(last.json.rawQuery, 'q=1');
  await call('POST', '/__reset');
  assert.equal((await call('GET', '/__echo/last')).status, 404);
});

test('reset restores seed', async () => {
  await call('DELETE', '/v2/orders/ord_0002');
  await call('POST', '/v2/accounts/a/orders', { body: { customer: { ref: 'X' } } });
  await call('POST', '/__reset');
  assert.equal((await call('GET', '/v2/orders/ord_0002')).status, 200);
  assert.equal((await call('GET', '/v2/orders-all')).json.data.length, 120);
});

// ---- Inventory API (/inv/v1, api_key query parameter) ----

const inv = (method, p, body) => call(method, `/inv/v1${p}${p.includes('?') ? '&' : '?'}api_key=${INV_KEY}`, { body, key: null });

test('inventory requires api_key query', async () => {
  assert.equal((await call('GET', '/inv/v1/items', { key: null })).status, 401);
  assert.equal((await call('GET', '/inv/v1/items?api_key=wrong', { key: null })).status, 401);
  // The Orders header key is not an inventory credential.
  assert.equal((await call('GET', '/inv/v1/items')).status, 401);
  assert.equal((await inv('GET', '/items')).status, 200);
});

test('inventory offset paging with total covers all items once', async () => {
  const seen = new Set();
  let offset = 0;
  let total;
  for (;;) {
    const r = await inv('GET', `/items?limit=20&offset=${offset}`);
    assert.equal(r.status, 200);
    total = r.json.total;
    for (const item of r.json.items) { assert.ok(!seen.has(item.id)); seen.add(item.id); }
    offset += 20;
    if (offset >= total) break;
  }
  assert.equal(total, 45);
  assert.equal(seen.size, 45);
  assert.equal((await inv('GET', '/items?limit=51')).status, 400);
  const discontinued = await inv('GET', '/items?state=discontinued&limit=50');
  assert.equal(discontinued.json.total, 9);
  assert.ok(discontinued.json.items.every((x) => x.state === 'discontinued'));
});

test('inventory retrieve, create, update, delete', async () => {
  const one = await inv('GET', '/items/7');
  assert.deepEqual(Object.keys(one.json).sort(), ['id', 'name', 'onHand', 'price', 'sku', 'state', 'updatedAt']);
  assert.equal(one.json.id, 7);
  assert.equal((await inv('GET', '/items/999')).status, 404);

  const created = await inv('POST', '/items', { name: 'Widget', sku: 'W-1', onHand: 3, price: 2.5, state: 'active' });
  assert.equal(created.status, 201);
  assert.equal(created.json.id, 46);
  assert.equal((await inv('POST', '/items', { sku: 'no name' })).status, 422);
  assert.equal((await inv('POST', '/items', { name: 'x', state: 'lost' })).status, 422);

  assert.equal((await inv('PATCH', '/items/46', { price: 3.75, state: 'discontinued' })).status, 204);
  const after = await inv('GET', '/items/46');
  assert.equal(after.json.price, 3.75);
  assert.equal(after.json.state, 'discontinued');
  assert.equal(after.json.name, 'Widget');

  assert.equal((await inv('DELETE', '/items/46')).status, 204);
  assert.equal((await inv('GET', '/items/46')).status, 404);
});

test('inventory adjust returns onHand and adjustedAt', async () => {
  const before = (await inv('GET', '/items/3')).json.onHand;
  const r = await inv('POST', '/items/3/adjust', { delta: 5, reason: 'count' });
  assert.equal(r.status, 200);
  assert.equal(r.json.onHand, before + 5);
  assert.match(r.json.adjustedAt, /^\d{4}-\d\d-\d\dT/);
  assert.equal((await inv('POST', '/items/3/adjust', { delta: -1000 })).status, 422);
  assert.equal((await inv('POST', '/items/3/adjust', { reason: 'no delta' })).status, 422);
  assert.equal((await inv('POST', '/items/999/adjust', { delta: 1 })).status, 404);
});

test('reset restores the inventory seed', async () => {
  await inv('DELETE', '/items/1');
  assert.equal((await call('POST', '/__reset')).status, 204);
  assert.equal((await inv('GET', '/items/1')).status, 200);
});

// ---- Tickets (phase 3) ----

async function tk(method, p, { auth, form, body } = {}) {
  const headers = {};
  if (auth) headers.authorization = auth;
  let payload;
  if (form) { headers['content-type'] = 'application/x-www-form-urlencoded'; payload = new URLSearchParams(form).toString(); }
  if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body); }
  const res = await fetch(base + p, { method, headers, body: payload, redirect: 'manual' });
  const text = await res.text();
  let json;
  try { json = text ? JSON.parse(text) : undefined; } catch { json = undefined; }
  return { status: res.status, headers: res.headers, json, text };
}

const basic = (user, pw) => 'Basic ' + Buffer.from(`${user}:${pw}`).toString('base64');

/** Signs in through the authorize form and redeems the code: the browser half of the flow, done by hand. */
async function oauthTokens(user, password, { verifier = crypto.randomBytes(32).toString('base64url'), sentVerifier } = {}) {
  const challenge = crypto.createHash('sha256').update(verifier).digest('base64url');
  const params = { response_type: 'code', client_id: 'bf-tickets', redirect_uri: TK.redirect, state: 's1', code_challenge: challenge, code_challenge_method: 'S256', scope: 'tickets.read' };
  assert.equal((await tk('GET', '/tk/oauth/authorize?' + new URLSearchParams(params))).status, 200);
  const signedIn = await tk('POST', '/tk/oauth/authorize', { form: { ...params, user, password } });
  assert.equal(signedIn.status, 302);
  const location = new URL(signedIn.headers.get('location'));
  assert.equal(location.searchParams.get('state'), 's1');
  return tk('POST', '/tk/oauth/token', { form: { grant_type: 'authorization_code', code: location.searchParams.get('code'), redirect_uri: TK.redirect, code_verifier: sentVerifier ?? verifier, client_id: 'bf-tickets', client_secret: TK.secret } });
}

const ids = (r) => r.json.items.map((t) => t.id);

test('tickets: each user sees only their own', async () => {
  const alice = await tk('GET', '/tk/v1/tickets?limit=100', { auth: basic('alice', TK.alice) });
  const bob = await tk('GET', '/tk/v1/tickets?limit=100', { auth: basic('bob', TK.bob) });
  assert.deepEqual(ids(alice), ['TK-1', 'TK-2', 'TK-3', 'TK-4', 'TK-5', 'TK-6', 'TK-7']);
  assert.deepEqual(ids(bob), ['TK-8', 'TK-9', 'TK-10', 'TK-11', 'TK-12']);
  assert.equal(alice.json.items[0].owner, undefined);
});

test('tickets: cursor paging and status filter', async () => {
  const first = await tk('GET', '/tk/v1/tickets?limit=3', { auth: basic('alice', TK.alice) });
  assert.deepEqual(ids(first), ['TK-1', 'TK-2', 'TK-3']);
  const second = await tk('GET', `/tk/v1/tickets?limit=10&cursor=${first.json.nextCursor}`, { auth: basic('alice', TK.alice) });
  assert.deepEqual(ids(second), ['TK-4', 'TK-5', 'TK-6', 'TK-7']);
  assert.equal(second.json.nextCursor, null);
  const closed = await tk('GET', '/tk/v1/tickets?status=closed', { auth: basic('alice', TK.alice) });
  assert.deepEqual(ids(closed), ['TK-4']);
});

test("tickets: other user's id is 404", async () => {
  assert.equal((await tk('GET', '/tk/v1/tickets/TK-8', { auth: basic('alice', TK.alice) })).status, 404);
  assert.equal((await tk('GET', '/tk/v1/tickets/TK-8', { auth: basic('bob', TK.bob) })).json.title, 'Ticket 8');
});

test('tickets: create is owned by the caller', async () => {
  const created = await tk('POST', '/tk/v1/tickets', { auth: basic('bob', TK.bob), body: { title: 'New one', priority: 'high' } });
  assert.equal(created.status, 201);
  assert.ok(ids(await tk('GET', '/tk/v1/tickets?limit=100', { auth: basic('bob', TK.bob) })).includes(created.json.id));
  assert.ok(!ids(await tk('GET', '/tk/v1/tickets?limit=100', { auth: basic('alice', TK.alice) })).includes(created.json.id));
});

test('no credential is 401', async () => {
  assert.equal((await tk('GET', '/tk/v1/tickets')).status, 401);
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: basic('alice', 'wrong') })).status, 401);
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: 'Bearer tk-at-made-up' })).status, 401);
});

test('oauth: code flow gives a token that lists the user\'s tickets', async () => {
  const t = await oauthTokens('bob', TK.bob);
  assert.equal(t.status, 200);
  assert.equal(t.json.expires_in, 3600);
  assert.deepEqual(ids(await tk('GET', '/tk/v1/tickets', { auth: 'Bearer ' + t.json.access_token })), ['TK-8', 'TK-9', 'TK-10', 'TK-11', 'TK-12']);
});

test('oauth: code requires matching PKCE verifier', async () => {
  const t = await oauthTokens('alice', TK.alice, { sentVerifier: 'x'.repeat(43) });
  assert.equal(t.status, 400);
  assert.equal(t.json.error, 'invalid_grant');
});

test('oauth: wrong password re-shows the form, unregistered redirect is refused', async () => {
  const challenge = crypto.createHash('sha256').update('v'.repeat(43)).digest('base64url');
  const params = { response_type: 'code', client_id: 'bf-tickets', redirect_uri: TK.redirect, state: 's', code_challenge: challenge, code_challenge_method: 'S256' };
  assert.equal((await tk('POST', '/tk/oauth/authorize', { form: { ...params, user: 'alice', password: 'nope' } })).status, 401);
  assert.equal((await tk('GET', '/tk/oauth/authorize?' + new URLSearchParams({ ...params, redirect_uri: 'https://evil.example/cb' }))).status, 400);
});

test('oauth: refresh token is single-use', async () => {
  const t = await oauthTokens('alice', TK.alice);
  const form = { grant_type: 'refresh_token', refresh_token: t.json.refresh_token, client_id: 'bf-tickets', client_secret: TK.secret };
  const first = await tk('POST', '/tk/oauth/token', { form });
  assert.equal(first.status, 200);
  assert.notEqual(first.json.refresh_token, t.json.refresh_token);
  const again = await tk('POST', '/tk/oauth/token', { form });
  assert.equal(again.status, 400);
  assert.equal(again.json.error, 'invalid_grant');
});

test('oauth: revoked refresh token gives invalid_grant', async () => {
  const t = await oauthTokens('alice', TK.alice);
  assert.equal((await tk('POST', '/tk/oauth/revoke', { form: { token: t.json.refresh_token } })).status, 200);
  const r = await tk('POST', '/tk/oauth/token', { form: { grant_type: 'refresh_token', refresh_token: t.json.refresh_token, client_id: 'bf-tickets', client_secret: TK.secret } });
  assert.equal(r.json.error, 'invalid_grant');
});

test('oauth: wrong client secret is invalid_client', async () => {
  const r = await tk('POST', '/tk/oauth/token', { form: { grant_type: 'refresh_token', refresh_token: 'x', client_id: 'bf-tickets', client_secret: 'wrong' } });
  assert.equal(r.status, 401);
  assert.equal(r.json.error, 'invalid_client');
});

test('entra: a valid token maps oid to the user', async () => {
  const r = await tk('GET', '/tk/v1/tickets?limit=100', { auth: 'Bearer ' + entraToken({ oid: ENTRA.bobOid }) });
  assert.deepEqual(ids(r), ['TK-8', 'TK-9', 'TK-10', 'TK-11', 'TK-12']);
});

test('entra: wrong audience is 401', async () => {
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: 'Bearer ' + entraToken({ oid: ENTRA.aliceOid, aud: 'api://other' }) })).status, 401);
});

test('entra: expired, unknown oid or wrong key is 401', async () => {
  const now = Math.floor(Date.now() / 1000);
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: 'Bearer ' + entraToken({ oid: ENTRA.aliceOid, exp: now - 5 }) })).status, 401);
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: 'Bearer ' + entraToken({ oid: 'c0000000-0000-0000-0000-000000000003' }) })).status, 401);
  const other = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  assert.equal((await tk('GET', '/tk/v1/tickets', { auth: 'Bearer ' + entraToken({ oid: ENTRA.aliceOid }, other.privateKey) })).status, 401);
});

test('tickets: /__tk/stats counts data requests, including refused ones', async () => {
  const stats = async () => (await call('GET', '/__tk/stats')).json.dataRequests;
  assert.equal(await stats(), 0);
  await tk('GET', '/tk/v1/tickets');
  await tk('GET', '/tk/v1/tickets', { auth: basic('alice', TK.alice) });
  assert.equal(await stats(), 2);
  assert.equal((await call('GET', '/__tk/stats', { key: null })).status, 401);
});

test('MOCK_APIS=tickets serves only the Tickets API, even with valid keys', async () => {
  const s = await start({ MOCK_API_KEY: KEY, MOCK_INVENTORY_KEY: INV_KEY, MOCK_APIS: 'tickets', MOCK_TK_CLIENT_SECRET: TK.secret, MOCK_TK_REDIRECT_URIS: TK.redirect });
  try {
    const at = (p, headers) => fetch(`http://127.0.0.1:${s.port}${p}`, { method: p === '/__reset' ? 'POST' : 'GET', headers });
    assert.equal((await at('/v2/orders', { 'x-api-key': KEY })).status, 404);
    assert.equal((await at(`/inv/v1/items?api_key=${INV_KEY}`)).status, 404);
    assert.equal((await at('/__reset', { 'x-api-key': KEY })).status, 404);
    assert.equal((await at('/tk/v1/tickets')).status, 401);
  } finally {
    s.proc.kill();
  }
});
