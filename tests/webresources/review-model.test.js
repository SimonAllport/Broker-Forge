// The review page's logic, without the browser (plan 4 task 6). Run: node --test tests/webresources
const test = require("node:test");
const assert = require("node:assert/strict");
const model = require("../../src/BrokerForge.WebResources/review-model.js");

const ready = () => ({
  status: "Ready",
  review: { endpoints: [], problems: [] },
  preview: { fingerprint: "0123456789abcdef", changes: ["add table ext_item"], refused: [], blocked: [], parts: [], pending: [], needsConfirmation: false },
  environment: { baseUrlSet: true, secrets: [{ name: "key", set: false }] },
});

test("canGenerate is true when the review is ready and nothing is blocked, with or without the key", () => {
  assert.equal(model.canGenerate(ready()), true);
});

test("canGenerate false while a question is open", () => {
  assert.equal(model.canGenerate({ ...ready(), status: "NeedsAnswers", preview: null }), false);
});

test("canGenerate false when blocked", () => {
  const read = ready();
  read.preview.blocked = ["remove column ext_sku is blocked: view Stock uses it"];
  assert.equal(model.canGenerate(read), false);
});

test("canGenerate false when refused", () => {
  const read = ready();
  read.preview.refused = ["the primary name changed; delete by hand"];
  assert.equal(model.canGenerate(read), false);
});

test("canGenerate false for a read-only connection", () => {
  assert.equal(model.canGenerate({ ...ready(), readOnly: true }), false);
});

test("missingKeys names keys that are not set", () => {
  assert.deepEqual(model.missingKeys(ready()), ["key"]);
});

test("answersFrom keeps untouched answers and writes op.<op>.use=no for an unticked endpoint", () => {
  const saved = { "op.listItems.paging": "offset", "column.$.sku.displayName": "SKU" };
  const result = model.answersFrom(saved, { "op.adjustStock.use": "no", "column.$.sku.displayName": "" });
  assert.deepEqual(result, { "op.listItems.paging": "offset", "op.adjustStock.use": "no" });
  assert.deepEqual(saved, { "op.listItems.paging": "offset", "column.$.sku.displayName": "SKU" }, "the saved answers are not changed in place");
});

test("answersFrom keeps keys sorted, so a save of the same answers writes the same text", () => {
  assert.deepEqual(Object.keys(model.answersFrom({ b: "1" }, { a: "2" })), ["a", "b"]);
});

const at = (iso) => new Date(iso);

test("statusLine flags a stale Running job", () => {
  const row = { bf_generationstate: "Running", bf_generationstarted: "2026-11-05T09:00:00Z" };
  assert.match(model.statusLine(row, at("2026-11-05T09:21:00Z")), /stopped without a report; Generate again/);
  assert.match(model.statusLine(row, at("2026-11-05T09:05:00Z")), /^Generating/);
});

test("statusLine reports success, pending role and failures", () => {
  const ok = { bf_generationstate: "Succeeded", bf_generationresult: JSON.stringify({ writes: ["POST EntityDefinitions"], pending: ["create role X user"], designVersion: 2, needsConfirmation: false }) };
  assert.match(model.statusLine(ok, at("2026-11-05T09:00:00Z")), /design v2/);
  assert.equal(model.needsRole(ok), true);

  const changed = { bf_generationstate: "Failed", bf_generationresult: JSON.stringify({ writes: [], pending: [], failure: "The changes are not the ones confirmed", needsConfirmation: true }) };
  assert.match(model.statusLine(changed, at("2026-11-05T09:00:00Z")), /changed since you reviewed them/);
  assert.equal(model.needsRole(changed), false);

  const failed = { bf_generationstate: "Failed", bf_generationresult: JSON.stringify({ writes: [], pending: [], failure: "Generation failed at step 'table'", needsConfirmation: false }) };
  assert.match(model.statusLine(failed, at("2026-11-05T09:00:00Z")), /Generation failed at step 'table'/);

  assert.equal(model.statusLine({}, at("2026-11-05T09:00:00Z")), "Not generated yet.");
});

test("isRunning only for Queued or Running", () => {
  assert.equal(model.isRunning({ bf_generationstate: "Queued" }), true);
  assert.equal(model.isRunning({ bf_generationstate: "Succeeded" }), false);
});

test("doneLinks encodes the table name", () => {
  const links = model.doneLinks("https://contoso.crm4.dynamics.com/", "ext_item");
  assert.equal(links.open, "https://contoso.crm4.dynamics.com/main.aspx?pagetype=entitylist&etn=ext_item");
  assert.equal(model.doneLinks("https://contoso.crm4.dynamics.com", "a&b").open, "https://contoso.crm4.dynamics.com/main.aspx?pagetype=entitylist&etn=a%26b");
});

test("canGenerate false when the change list could not be worked out", () => {
  assert.equal(model.canGenerate({ ...ready(), preview: { error: "SecLib::CheckPrivilege failed" } }), false);
});

// Found while recording plan 5: Xrm.WebApi sends a bare string for an Edm.Guid parameter as `undefined`.
test("action parameters: the connection ID goes as Xrm's Guid shape, text as text", () => {
  const id = "00000000-0000-0000-0000-00000000c001";
  assert.deepEqual(model.actionParameter("ConnectionId", id), { typeName: "Edm.Guid", value: { guid: id } });
  assert.deepEqual(model.actionParameter("Fingerprint", "ab-cd"), { typeName: "Edm.String", value: "ab-cd" });
});

// Plan 5: actions are left to the administrator's step with the role, so the line says what is left.
test("status line names what an administrator still has to do", () => {
  const row = { bf_generationstate: "Succeeded", bf_generationresult: JSON.stringify({ designVersion: 1, pending: ["add action Adjust stock (ext_AdjustStock)", "create role X user"] }) };
  assert.equal(model.statusLine(row, new Date()), "Generated (design v1). Left for an administrator: add action Adjust stock (ext_AdjustStock); create role X user.");
});

// Found while recording plan 5: right after bf_Generate the row may not show the new job yet, and the page stopped polling.
test("a job just started counts as running until the row shows it", () => {
  const job = "11111111-1111-1111-1111-111111111111";
  assert.equal(model.isRunning({ bf_generationstate: null }, job), true);
  assert.equal(model.isRunning({ bf_generationstate: "Succeeded", bf_generationjobid: "22222222-2222-2222-2222-222222222222" }, job), true);
  assert.equal(model.isRunning({ bf_generationstate: "Succeeded", bf_generationjobid: job }, job), false);
  assert.equal(model.isRunning({ bf_generationstate: "Running", bf_generationjobid: job }, job), true);
  assert.equal(model.isRunning({ bf_generationstate: "Succeeded" }), false);
});

// Plan 5 deferred minor: if the row never shows the job the page started, the page stopped waiting only on a report.
test("a job just started stops counting as running when the row has not shown it for two minutes", () => {
  const job = "11111111-1111-1111-1111-111111111111";
  const at = new Date("2026-11-07T10:00:00Z");
  const row = { bf_generationstate: "Succeeded", bf_generationjobid: "22222222-2222-2222-2222-222222222222" };
  assert.equal(model.isRunning(row, job, at, new Date("2026-11-07T10:01:59Z")), true);
  assert.equal(model.isRunning(row, job, at, new Date("2026-11-07T10:02:01Z")), false);
  assert.equal(model.neverShown(row, job, at, new Date("2026-11-07T10:02:01Z")), true);
  assert.equal(model.neverShown({ bf_generationstate: "Running", bf_generationjobid: job }, job, at, new Date("2026-11-07T10:30:00Z")), false);
  assert.equal(model.neverShown(row, undefined, at, new Date("2026-11-07T10:30:00Z")), false);
});
