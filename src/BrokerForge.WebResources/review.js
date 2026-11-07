// BrokerForge review page (spec 5, screens 2 and 3), on the connection form's Review tab. The form's OnLoad
// (connection.js) hands it the form context and Xrm. It calls bf_ReadDefinition and bf_Generate through Xrm.WebApi as
// the signed-in user, saves answers to bf_answers, and polls the row while a Generate runs. Values from the review are
// written with textContent only: the review carries the pasted definition's names.
(function () {
  "use strict";
  var model = window.BrokerForgeReview;
  var form, xrm, read, edits = {}, poll, startedJob, startedAt;

  function $(id) { return document.getElementById(id); }

  function el(tag, text, cls) {
    var e = document.createElement(tag);
    if (text !== undefined && text !== null) e.textContent = String(text);
    if (cls) e.className = cls;
    return e;
  }

  function list(items, cls) {
    var ul = el("ul");
    items.forEach(function (i) { ul.appendChild(el("li", i, cls)); });
    return ul;
  }

  function message(text, cls) {
    $("message").textContent = text;
    $("message").className = cls || "muted";
  }

  function rowId() { return form.data.entity.getId().replace(/[{}]/g, "").toLowerCase(); }

  // Every admin API is an unbound action taking the row's ID (the Custom APIs in tools/dev/setup_dataverse.py).
  function call(name, inputs) {
    var request = { getMetadata: function () {
      var types = {};
      Object.keys(inputs).forEach(function (k) { types[k] = { typeName: model.actionParameter(k, inputs[k]).typeName, structuralProperty: 1 }; });
      return { boundParameter: null, parameterTypes: types, operationType: 0, operationName: name };
    } };
    Object.keys(inputs).forEach(function (k) { request[k] = model.actionParameter(k, inputs[k]).value; });
    return xrm.WebApi.online.execute(request).then(function (r) { return r.json(); });
  }

  function failed(e) {
    message((e && e.message) || String(e), "bad");
  }

  // bf_answers is not on the form: it is read and written on the row through Xrm.WebApi.
  function parseAnswers(text) {
    try { return JSON.parse(text || "{}"); } catch (e) { return {}; }
  }

  function readDefinition() {
    if (!rowId()) { message("Save the connection first."); return Promise.resolve(); }
    message("Reading the definition…");
    return call("bf_ReadDefinition", { ConnectionId: rowId() }).then(function (r) {
      read = JSON.parse(r.Review);
      edits = {};
      render();
      return refreshRow();
    }, failed);
  }

  function setAnswer(key, value) { edits[key] = value; }

  function questionInput(q) {
    var wrap = el("div", null, "question");
    // The answer key, so a script (the how-to recording) can find a question without matching its wording.
    wrap.setAttribute("data-key", q.key);
    var label = el("label", q.text);
    var input;
    if (q.kind === "Choice") {
      input = el("select");
      input.appendChild(el("option", q.required ? "Choose…" : "(as proposed)"));
      input.options[0].value = "";
      q.choices.forEach(function (c) {
        var o = el("option", c.label);
        o.value = c.value;
        input.appendChild(o);
      });
    } else {
      input = el("input");
      input.type = q.kind === "Number" ? "number" : "text";
      if (q.proposed) input.placeholder = q.proposed;
    }
    input.value = q.answer || "";
    input.addEventListener("change", function () { setAnswer(q.key, input.value); });
    label.appendChild(input);
    wrap.appendChild(label);
    if (q.error) wrap.appendChild(el("div", q.error, "bad"));
    else if (q.required && !q.answer) wrap.appendChild(el("div", "needs an answer", "bad"));
    return wrap;
  }

  function renderTable(t) {
    var box = $("table");
    box.textContent = "";
    if (!t) { box.appendChild(el("p", "No table: only actions are in use, or the definition has no collection.", "muted")); return; }
    box.appendChild(el("p", t.displayName + " (" + t.logicalName + "), key " + (t.keyPath || "not known yet") + ", primary name " + (t.primaryName || "not known yet")));
    var table = el("table");
    var head = el("tr");
    ["Source", "Column", "Display name", "Type"].forEach(function (h) { head.appendChild(el("th", h)); });
    table.appendChild(head);
    t.columns.forEach(function (c) {
      var tr = el("tr");
      tr.appendChild(el("td", c.sourcePath));
      tr.appendChild(el("td", c.logicalName));
      var td = el("td");
      var input = el("input");
      input.type = "text";
      input.value = c.displayName;
      input.addEventListener("change", function () { setAnswer("column." + c.sourcePath + ".displayName", input.value); });
      td.appendChild(input);
      tr.appendChild(td);
      tr.appendChild(el("td", c.type));
      table.appendChild(tr);
    });
    box.appendChild(table);
    t.questions.forEach(function (q) { box.appendChild(questionInput(q)); });
  }

  function renderEndpoints(endpoints) {
    var box = $("endpoints");
    box.textContent = "";
    endpoints.forEach(function (e) {
      var row = el("div");
      var label = el("label");
      var tick = el("input");
      tick.type = "checkbox";
      tick.checked = e.status !== "NotUsed";
      tick.disabled = e.status === "Unsupported";
      tick.addEventListener("change", function () { setAnswer("op." + e.operation + ".use", tick.checked ? "" : "no"); });
      label.appendChild(tick);
      label.appendChild(document.createTextNode(" " + e.method + " " + e.path + " → " + e.becomes + " "));
      var status = e.status === "Ready" ? "Ready" : e.status === "NeedsAnswers" ? e.questions.filter(function (q) { return q.open; }).length + " questions"
        : e.status === "NotUsed" ? "Not used" : e.status;
      label.appendChild(el("strong", status, e.status === "Ready" ? "good" : e.status === "NotUsed" ? "muted" : "bad"));
      row.appendChild(label);
      if (e.reason) row.appendChild(el("div", e.reason, "muted"));
      if (e.problems && e.problems.length) row.appendChild(list(e.problems, "bad"));
      if (e.status !== "NotUsed") e.questions.forEach(function (q) { row.appendChild(questionInput(q)); });
      box.appendChild(row);
    });
  }

  function renderEnvironment() {
    var box = $("environment");
    box.textContent = "";
    box.appendChild(el("p", read.environment.baseUrlSet ? "Base URL: set." : "Base URL: needs URL.", read.environment.baseUrlSet ? "good" : "bad"));
    read.environment.secrets.forEach(function (s) {
      var p = el("p", s.name + ": " + (s.set ? "key set " : "needs key "), s.set ? "good" : "bad");
      if (read.secretUrl) {
        var a = el("a", s.set ? "Replace key" : "Set key");
        a.href = read.secretUrl;
        a.target = "_blank";
        a.rel = "noopener";
        p.appendChild(a);
      }
      box.appendChild(p);
    });
  }

  function renderPreview() {
    var box = $("preview");
    box.textContent = "";
    var p = read.preview;
    if (!p) { box.appendChild(el("p", "Answer every question above to see what Generate will do.", "muted")); }
    else if (p.error) { box.appendChild(el("p", "What Generate would change could not be worked out: " + p.error, "bad")); }
    else {
      if (p.changes.length) { box.appendChild(el("p", "Changes:")); box.appendChild(list(p.changes)); }
      else box.appendChild(el("p", "No changes to the table or actions.", "muted"));
      if (p.parts.length) box.appendChild(list(p.parts, "muted"));
      if (p.pending.length) { box.appendChild(el("p", "Left to an administrator:")); box.appendChild(list(p.pending)); }
      if (p.refused.length) { box.appendChild(el("p", "Refused:", "bad")); box.appendChild(list(p.refused, "bad")); }
      if (p.blocked.length) { box.appendChild(el("p", "Blocked, so nothing is applied:", "bad")); box.appendChild(list(p.blocked, "bad")); }
    }
    $("generate").disabled = !model.canGenerate(read);
  }

  function render() {
    $("page").hidden = false;
    $("saveAnswers").disabled = false;
    message(read.status === "Ready" ? "Ready to generate." : read.status === "Invalid" ? "The definition has problems." : "Some questions need answers.",
      read.status === "Ready" ? "good" : "bad");
    var problems = $("problems");
    problems.textContent = "";
    if (read.review.problems.length) problems.appendChild(list(read.review.problems, "bad"));
    if (read.review.notes.length) problems.appendChild(list(read.review.notes, "muted"));
    renderTable(read.review.table);
    renderEndpoints(read.review.endpoints);
    renderEnvironment();
    renderPreview();
  }

  function refreshRow() {
    return xrm.WebApi.retrieveRecord("bf_connection", rowId(), "?$select=bf_generationstate,bf_generationstarted,bf_generationresult,bf_generationjobid").then(function (row) {
      var status = $("status");
      status.textContent = "";
      var now = new Date();
      var waiting = model.isRunning(row, startedJob, startedAt, now) && !model.isRunning(row);
      var lost = model.neverShown(row, startedJob, startedAt, now);
      status.appendChild(el("p", lost ? "Generate started but has not reported in two minutes. Read the definition again to see where it got to."
        : waiting ? "Generate is starting…" : model.statusLine(row, now)));
      if (model.needsRole(row) && read && read.roleUrl) {
        var a = el("a", "Finish as administrator");
        a.href = read.roleUrl;
        a.target = "_blank";
        a.rel = "noopener";
        status.appendChild(a);
      }
      var done = $("done");
      done.textContent = "";
      if (row.bf_generationstate === "Succeeded" && read && read.review.table) {
        var links = model.doneLinks(xrm.Utility.getGlobalContext().getClientUrl(), read.review.table.logicalName);
        [["Open the table", links.open], ["Add it to an app", links.addToApp]].forEach(function (l) {
          var a = el("a", l[0]);
          a.href = l[1];
          a.target = "_blank";
          a.rel = "noopener";
          done.appendChild(a);
          done.appendChild(document.createTextNode(" "));
        });
      }
      clearTimeout(poll);
      if (model.isRunning(row, startedJob, startedAt, now) && (waiting || model.statusLine(row, now).indexOf("stopped") < 0))
        poll = setTimeout(refreshRow, 5000);
      else if (row.bf_generationstate === "Succeeded" && poll !== undefined) {
        poll = undefined;
        startedJob = undefined;
        readDefinition();
      }
      else startedJob = undefined;
    }, failed);
  }

  function saveThenRead() {
    return form.data.save().then(readDefinition, failed);
  }

  function saveAnswers() {
    message("Saving answers…");
    return form.data.save().then(function () {
      return xrm.WebApi.retrieveRecord("bf_connection", rowId(), "?$select=bf_answers");
    }).then(function (row) {
      return xrm.WebApi.updateRecord("bf_connection", rowId(), { bf_answers: JSON.stringify(model.answersFrom(parseAnswers(row.bf_answers), edits)) });
    }).then(readDefinition, failed);
  }

  function generate() {
    $("generate").disabled = true;
    message("Starting Generate…");
    return call("bf_Generate", { ConnectionId: rowId(), Fingerprint: read.preview.fingerprint }).then(function (r) {
      message("Generate started. This page follows it.");
      startedJob = r.JobId;
      startedAt = new Date();
      poll = 0;
      return refreshRow();
    }, failed);
  }

  window.BrokerForgeReviewPage = {
    init: function (formContext, xrmApi) {
      form = formContext;
      xrm = xrmApi;
      $("read").onclick = saveThenRead;
      $("saveAnswers").onclick = saveAnswers;
      $("generate").onclick = generate;
      if (!rowId()) { message("Fill in the connection, then save it and read the definition."); return; }
      readDefinition();
    },
  };
})();
