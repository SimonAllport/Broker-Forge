'use strict';
// Deterministic seed: 120 orders. state.code rotates open/shipped/cancelled (40 each),
// customer.ref rotates CUST-01..CUST-10 (12 each).
const STATES = ['open', 'shipped', 'cancelled'];
const pad = (n, w) => String(n).padStart(w, '0');

function seed() {
  const orders = [];
  for (let i = 1; i <= 120; i++) {
    orders.push({
      id: `ord_${pad(i, 4)}`,
      reference: `REF-${pad(i, 4)}`,
      state: { code: STATES[(i - 1) % 3] },
      customer: { ref: `CUST-${pad(((i - 1) % 10) + 1, 2)}` },
      placedAt: new Date(Date.UTC(2026, 0, 1) + i * 3600_000).toISOString(),
      lines: [{ sku: `SKU-${pad(i % 5, 2)}`, qty: (i % 4) + 1 }],
      totals: { gross: Math.round(i * 1050) / 100 },
    });
  }
  return orders;
}

module.exports = { seed, pad };
