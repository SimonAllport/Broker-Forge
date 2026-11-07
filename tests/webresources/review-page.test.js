// The review page's markup (plan 5, found while recording): on a new connection the page has no review yet, and its
// "Save and read the definition" button, which saves the row first, must still be there to press.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const html = fs.readFileSync(path.join(__dirname, "../../src/BrokerForge.WebResources/review.html"), "utf8");

test("the read button is outside the part hidden until a review exists", () => {
  const hidden = html.indexOf('<div id="page" hidden>');
  assert.ok(hidden > 0, "review.html has the hidden #page section");
  assert.ok(html.indexOf('id="read"') >= 0 && html.indexOf('id="read"') < hidden, "#read comes before #page, not inside it");
});

// Found while recording plan 5: bf_answers is not a field on the connection form, so the page must not read or write it
// through the form; it goes through Xrm.WebApi on the row.
test("answers are read and saved on the row, not through a form field", () => {
  const js = fs.readFileSync(path.join(__dirname, "../../src/BrokerForge.WebResources/review.js"), "utf8");
  assert.ok(!/getAttribute\("bf_answers"\)/.test(js), "review.js does not use the form attribute bf_answers");
  assert.ok(/updateRecord\("bf_connection"/.test(js), "review.js saves bf_answers with Xrm.WebApi.updateRecord");
});

// Plan 5 deferred minor: the page passes when it started a job, so it stops waiting for a job the row never shows.
test("the page stops waiting for a job the row never shows", () => {
  const js = fs.readFileSync(path.join(__dirname, "../../src/BrokerForge.WebResources/review.js"), "utf8");
  assert.ok(!/isRunning\(row, startedJob\)/.test(js), "every isRunning call with the started job also passes its start time");
  assert.ok(/neverShown\(row, startedJob, startedAt, /.test(js), "review.js reports a job the row never showed");
});
