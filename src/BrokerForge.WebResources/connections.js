// BrokerForge connections page (spec 9): this environment's connections from bf_ListConnections, with their status.
// A connection that arrived by solution import has no row and is read-only here; its base URL is set on its environment
// variable and its key on the host's page. Values are written with textContent only.
(function () {
  "use strict";
  var xrm = (window.parent && window.parent.Xrm) || window.Xrm;

  function el(tag, text, cls) {
    var e = document.createElement(tag);
    if (text !== undefined && text !== null) e.textContent = String(text);
    if (cls) e.className = cls;
    return e;
  }

  function link(text, href, newTab) {
    var a = el("a", text);
    a.href = href;
    if (newTab) { a.target = "_blank"; a.rel = "noopener"; }
    return a;
  }

  function show(connections) {
    var table = document.getElementById("list");
    var org = xrm.Utility.getGlobalContext().getClientUrl();
    connections.forEach(function (c) {
      var tr = el("tr");
      var name = el("td");
      if (c.rowId) name.appendChild(link(c.name, org + "/main.aspx?pagetype=entityrecord&etn=bf_connection&id=" + encodeURIComponent(c.rowId)));
      else name.appendChild(el("span", c.name + " (imported, read-only)"));
      tr.appendChild(name);
      tr.appendChild(el("td", c.status, c.status === "ready" ? "good" : c.status === "not generated" ? "muted" : "bad"));
      tr.appendChild(el("td", c.designVersion ? "v" + c.designVersion : ""));
      var actions = el("td");
      if (c.status === "needs URL")
        actions.appendChild(el("span", "Set environment variable " + c.baseUrlVariable + " ", "muted"));
      if (c.secretUrl && c.status !== "not generated") actions.appendChild(link(c.status === "needs key" ? "Set key" : "Keys", c.secretUrl, true));
      tr.appendChild(actions);
      table.appendChild(tr);
    });
    table.hidden = false;
    document.getElementById("message").textContent = connections.length ? "" : "No connections yet.";
  }

  document.getElementById("new").onclick = function (e) {
    e.preventDefault();
    xrm.Navigation.openForm({ entityName: "bf_connection" });
  };

  xrm.WebApi.online.execute({ getMetadata: function () {
    return { boundParameter: null, parameterTypes: {}, operationType: 0, operationName: "bf_ListConnections" };
  } }).then(function (r) { return r.json(); }).then(function (r) { show(JSON.parse(r.Connections)); }, function (e) {
    var m = document.getElementById("message");
    m.textContent = (e && e.message) || String(e);
    m.className = "bad";
  });
})();
