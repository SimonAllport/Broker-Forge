// BrokerForge connection form OnLoad (spec 5, screen 1): hands the form context and Xrm to the review page on the
// Review tab. Registered in tools/dev/setup_dataverse.py as BrokerForge.Connection.onLoad, passing the execution context.
var BrokerForge = window.BrokerForge || {};
BrokerForge.Connection = {
  onLoad: function (executionContext) {
    "use strict";
    var form = executionContext.getFormContext();
    var control = form.getControl("WebResource_review");
    if (!control) return;
    control.getContentWindow().then(function (page) {
      if (page && page.BrokerForgeReviewPage) page.BrokerForgeReviewPage.init(form, Xrm);
    });
  },
};
window.BrokerForge = BrokerForge;
