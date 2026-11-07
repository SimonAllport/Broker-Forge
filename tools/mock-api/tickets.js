'use strict';
// Tickets mock API (phase 3, HLD D-31): a per-user source. Every ticket has an owner, and a caller sees only
// their own. The caller is identified by one of three credentials, one per BrokerForge per-user mode:
//   OAuth        Authorization: Bearer <token issued by /tk/oauth/token below>
//   SSO          Authorization: Basic <user>:<MOCK_TK_<USER>_PASSWORD>
//   Impersonate  Authorization: Bearer <Entra JWT>, RS256, aud in MOCK_ENTRA_AUDIENCE, signing keys from
//                MOCK_ENTRA_JWKS_FILE or MOCK_ENTRA_JWKS_URL, oid mapped to a user by MOCK_ENTRA_USERS
// The OAuth server is authorisation code with PKCE (S256 only) and single-use rotating refresh tokens.
// Node built-ins only.
const crypto = require('node:crypto');
const fs = require('node:fs');

const USERS = ['alice', 'bob'];
const STATUSES = ['open', 'closed'];
const PRIORITIES = ['low', 'normal', 'high'];

const env = (name, def) => (process.env[name] === undefined || process.env[name] === '' ? def : process.env[name]);
const CLIENT_ID = env('MOCK_TK_CLIENT_ID', 'bf-tickets');
const CLIENT_SECRET = env('MOCK_TK_CLIENT_SECRET');
const REDIRECT_URIS = (env('MOCK_TK_REDIRECT_URIS', '') || '').split(',').map((s) => s.trim()).filter(Boolean);
const TOKEN_SECONDS = Number(env('MOCK_TK_TOKEN_SECONDS', '3600'));
const PASSWORDS = Object.fromEntries(USERS.map((u) => [u, env(`MOCK_TK_${u.toUpperCase()}_PASSWORD`)]));
const ENTRA_AUDIENCES = (env('MOCK_ENTRA_AUDIENCE', '') || '').split(',').map((s) => s.trim()).filter(Boolean);
const ENTRA_ISSUER = env('MOCK_ENTRA_ISSUER');
const ENTRA_USERS = Object.fromEntries((env('MOCK_ENTRA_USERS', '') || '').split(',').filter(Boolean)
  .map((pair) => pair.split(':')).map(([oid, user]) => [oid.trim().toLowerCase(), user.trim()]));

let tickets;
let nextTicket;
let codes; // code -> { user, redirectUri, challenge, scope, expires }
let accessTokens; // token -> { user, expires }
let refreshTokens; // token -> { user, scope }
let dataRequests; // every /tk/v1 request, refused or not, so tests can prove nothing reached the source
function resetTickets() {
  dataRequests = 0;
  tickets = [];
  for (let i = 1; i <= 12; i++) {
    tickets.push({
      id: `TK-${i}`,
      owner: i <= 7 ? 'alice' : 'bob',
      title: `Ticket ${i}`,
      status: i % 4 === 0 ? 'closed' : 'open',
      priority: PRIORITIES[i % 3],
      createdAt: new Date(Date.UTC(2026, 9, 1) + i * 3600_000).toISOString(),
    });
  }
  nextTicket = 13;
  codes = new Map();
  accessTokens = new Map();
  refreshTokens = new Map();
}
resetTickets();

const random = () => crypto.randomBytes(32).toString('base64url');
const sha256 = (s) => crypto.createHash('sha256').update(s).digest('base64url');
const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const safeEqual = (a, b) => typeof a === 'string' && typeof b === 'string' && a.length === b.length && crypto.timingSafeEqual(Buffer.from(a), Buffer.from(b));

// ---- Entra token check (Impersonate) ----

let jwks;
let jwksFetched = 0;
async function signingKeys() {
  if (env('MOCK_ENTRA_JWKS_FILE')) return (jwks ??= JSON.parse(fs.readFileSync(env('MOCK_ENTRA_JWKS_FILE'), 'utf8')).keys);
  if (!env('MOCK_ENTRA_JWKS_URL')) return [];
  if (!jwks || Date.now() - jwksFetched > 3600_000) {
    const res = await fetch(env('MOCK_ENTRA_JWKS_URL'));
    if (!res.ok) throw new Error(`JWKS fetch returned ${res.status}`);
    jwks = (await res.json()).keys;
    jwksFetched = Date.now();
  }
  return jwks;
}

async function entraUser(token) {
  const parts = token.split('.');
  if (parts.length !== 3 || ENTRA_AUDIENCES.length === 0) return null;
  let header;
  let claims;
  try {
    header = JSON.parse(Buffer.from(parts[0], 'base64url'));
    claims = JSON.parse(Buffer.from(parts[1], 'base64url'));
  } catch { return null; }
  if (header.alg !== 'RS256') return null;
  const jwk = (await signingKeys()).find((k) => k.kid === header.kid);
  if (!jwk) return null;
  const key = crypto.createPublicKey({ key: { kty: jwk.kty, n: jwk.n, e: jwk.e }, format: 'jwk' });
  const ok = crypto.verify('RSA-SHA256', Buffer.from(`${parts[0]}.${parts[1]}`), key, Buffer.from(parts[2], 'base64url'));
  const now = Math.floor(Date.now() / 1000);
  if (!ok || !ENTRA_AUDIENCES.includes(claims.aud) || !(claims.exp > now) || (claims.nbf && claims.nbf > now + 60)) return null;
  if (ENTRA_ISSUER && claims.iss !== ENTRA_ISSUER) return null;
  return ENTRA_USERS[String(claims.oid || '').toLowerCase()] || null;
}

async function caller(req) {
  const h = req.headers.authorization || '';
  if (h.startsWith('Basic ')) {
    const [user, ...rest] = Buffer.from(h.slice(6), 'base64').toString('utf8').split(':');
    return USERS.includes(user) && PASSWORDS[user] && safeEqual(rest.join(':'), PASSWORDS[user]) ? user : null;
  }
  if (h.startsWith('Bearer ')) {
    const token = h.slice(7);
    const issued = accessTokens.get(token);
    if (issued) return issued.expires > Date.now() ? issued.user : null;
    return entraUser(token);
  }
  return null;
}

// ---- OAuth server ----

function authorizeParams(q, HttpError) {
  const p = Object.fromEntries(['response_type', 'client_id', 'redirect_uri', 'state', 'code_challenge', 'code_challenge_method', 'scope'].map((k) => [k, q.get(k) || '']));
  if (p.client_id !== CLIENT_ID) throw new HttpError(400, 'unknown client_id');
  if (!REDIRECT_URIS.includes(p.redirect_uri)) throw new HttpError(400, 'redirect_uri is not registered');
  if (p.response_type !== 'code') throw new HttpError(400, 'response_type must be code');
  if (p.code_challenge_method !== 'S256' || !/^[A-Za-z0-9_-]{43}$/.test(p.code_challenge)) throw new HttpError(400, 'PKCE S256 code_challenge is required');
  return p;
}

function signInPage(res, p, problem) {
  const hidden = Object.entries(p).map(([k, v]) => `<input type="hidden" name="${k}" value="${escapeHtml(v)}">`).join('');
  res.writeHead(problem ? 401 : 200, { 'content-type': 'text/html; charset=utf-8' });
  res.end(`<!doctype html><title>Tickets sign-in</title><h1>Tickets mock sign-in</h1>${problem ? `<p>${escapeHtml(problem)}</p>` : ''}
<form method="post" action="/tk/oauth/authorize">${hidden}
<label>User <select name="user">${USERS.map((u) => `<option>${u}</option>`).join('')}</select></label>
<label>Password <input type="password" name="password"></label><button>Sign in</button></form>`);
}

function issue(user, scope) {
  const access = 'tk-at-' + random();
  const refresh = 'tk-rt-' + random();
  accessTokens.set(access, { user, expires: Date.now() + TOKEN_SECONDS * 1000 });
  refreshTokens.set(refresh, { user, scope });
  return { access_token: access, refresh_token: refresh, token_type: 'Bearer', expires_in: TOKEN_SECONDS, scope };
}

async function token(req, res, form, send) {
  const oauthError = (status, error) => send(res, status, { error });
  if (!CLIENT_SECRET) return oauthError(503, 'temporarily_unavailable');
  let id = form.get('client_id');
  let secret = form.get('client_secret');
  const basic = /^Basic (.+)$/.exec(req.headers.authorization || '');
  if (basic) [id, secret] = Buffer.from(basic[1], 'base64').toString('utf8').split(':');
  if (id !== CLIENT_ID || !safeEqual(secret || '', CLIENT_SECRET)) return oauthError(401, 'invalid_client');

  if (form.get('grant_type') === 'authorization_code') {
    const entry = codes.get(form.get('code') || '');
    codes.delete(form.get('code') || '');
    if (!entry || entry.expires < Date.now() || entry.redirectUri !== form.get('redirect_uri')) return oauthError(400, 'invalid_grant');
    if (sha256(form.get('code_verifier') || '') !== entry.challenge) return oauthError(400, 'invalid_grant');
    return send(res, 200, issue(entry.user, entry.scope));
  }
  if (form.get('grant_type') === 'refresh_token') {
    const entry = refreshTokens.get(form.get('refresh_token') || '');
    if (!entry) return oauthError(400, 'invalid_grant');
    refreshTokens.delete(form.get('refresh_token'));
    return send(res, 200, issue(entry.user, entry.scope));
  }
  return oauthError(400, 'unsupported_grant_type');
}

// ---- data ----

const view = ({ owner, ...t }) => t;

/** Handles /tk/*; returns false for any other path. */
async function ticketsRoute(req, res, url, { send, readBody, parseJson, intParam, HttpError }) {
  const p = url.pathname;
  if (!p.startsWith('/tk/')) return false;
  const m = req.method;
  const q = url.searchParams;

  if (p === '/tk/oauth/authorize' && m === 'GET') { signInPage(res, authorizeParams(q, HttpError)); return true; }
  if (p === '/tk/oauth/authorize' && m === 'POST') {
    const form = new URLSearchParams(await readBody(req));
    const params = authorizeParams(form, HttpError);
    const user = form.get('user');
    if (!USERS.includes(user) || !PASSWORDS[user] || !safeEqual(form.get('password') || '', PASSWORDS[user])) { signInPage(res, params, 'Wrong user or password.'); return true; }
    const code = random();
    codes.set(code, { user, redirectUri: params.redirect_uri, challenge: params.code_challenge, scope: params.scope, expires: Date.now() + 60_000 });
    const target = new URL(params.redirect_uri);
    target.searchParams.set('code', code);
    target.searchParams.set('state', params.state);
    res.writeHead(302, { location: target.toString() });
    res.end();
    return true;
  }
  if (p === '/tk/oauth/token' && m === 'POST') { await token(req, res, new URLSearchParams(await readBody(req)), send); return true; }
  if (p === '/tk/oauth/revoke' && m === 'POST') {
    const form = new URLSearchParams(await readBody(req));
    refreshTokens.delete(form.get('token') || '');
    accessTokens.delete(form.get('token') || '');
    send(res, 200);
    return true;
  }

  dataRequests++;
  const user = await caller(req);
  if (!user) { send(res, 401, { error: 'unauthorized' }, { 'www-authenticate': 'Bearer' }); return true; }

  if (p === '/tk/v1/tickets' && m === 'GET') {
    const limit = intParam(q, 'limit', 25, 1, 100);
    const offset = q.get('cursor') ? Number(/^o(\d+)$/.exec(q.get('cursor'))?.[1] ?? NaN) : 0;
    if (!Number.isInteger(offset)) throw new HttpError(400, 'cursor is not valid');
    const status = q.get('status');
    if (status && !STATUSES.includes(status)) throw new HttpError(400, `status must be one of ${STATUSES.join(', ')}`);
    const rows = tickets.filter((t) => t.owner === user && (!status || t.status === status));
    const page = rows.slice(offset, offset + limit).map(view);
    send(res, 200, { items: page, nextCursor: offset + limit < rows.length ? `o${offset + limit}` : null });
    return true;
  }
  if (p === '/tk/v1/tickets' && m === 'POST') {
    const body = parseJson(await readBody(req));
    if (body === null || typeof body !== 'object' || Array.isArray(body)) throw new HttpError(422, 'the body must be a JSON object');
    for (const key of Object.keys(body)) if (!['title', 'priority', 'status'].includes(key)) throw new HttpError(422, `unknown field ${key}`);
    if (typeof body.title !== 'string' || body.title === '') throw new HttpError(422, 'title is required');
    if (body.priority != null && !PRIORITIES.includes(body.priority)) throw new HttpError(422, `priority must be one of ${PRIORITIES.join(', ')}`);
    if (body.status != null && !STATUSES.includes(body.status)) throw new HttpError(422, `status must be one of ${STATUSES.join(', ')}`);
    const t = { id: `TK-${nextTicket++}`, owner: user, title: body.title, status: body.status || 'open', priority: body.priority || 'normal', createdAt: new Date().toISOString() };
    tickets.push(t);
    send(res, 201, { id: t.id });
    return true;
  }
  const match = /^\/tk\/v1\/tickets\/([^/]+)$/.exec(p);
  if (match && m === 'GET') {
    // Another user's ticket is indistinguishable from one that does not exist.
    const t = tickets.find((x) => x.id === decodeURIComponent(match[1]) && x.owner === user);
    if (!t) throw new HttpError(404, 'no such ticket');
    send(res, 200, view(t));
    return true;
  }
  throw new HttpError(404, `no route for ${m} ${p}`);
}

module.exports = { tickets: ticketsRoute, resetTickets, ticketStats: () => ({ dataRequests }) };
