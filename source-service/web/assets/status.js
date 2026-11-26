(function () {
  "use strict";
  var id = decodeURIComponent(location.pathname.split("/").filter(Boolean).pop() || "");
  var stopped = false;

  function render(status) {
    var card = Site.byId("status-card");
    var title = Site.qs(".status-title", card);
    title.innerHTML = "<h1>" + Site.escape(status.toolName || "提交状态") + "</h1>" + Site.statusBadge(status.status);
    var rows = [["提交编号", status.id], ["版本", status.version || "—"], ["提交时间", Site.formatDate(status.createdAtUtc)], ["审核时间", status.reviewedAtUtc ? Site.formatDate(status.reviewedAtUtc) : "等待审核"]];
    if (status.reviewNote) { rows.push(["审核备注", status.reviewNote]); }
    var html = '<dl class="meta-list">' + rows.map(function (row) {
      return "<dt>" + Site.escape(row[0]) + "</dt><dd>" + Site.escape(row[1]) + "</dd>";
    }).join("") + "</dl>";
    if (status.status === "pending") { html += '<p class="notice">页面每 5 秒自动刷新一次。</p>'; }
    if (status.status === "approved" && status.toolId) { html += '<div class="form-actions"><a class="button primary" href="/tools/' + encodeURIComponent(status.toolId) + '">查看公开页面</a></div>'; }
    html += '<div class="form-actions"><a class="button" href="/submit">提交另一个工具</a><a class="button" href="/">返回目录</a></div>';
    card.innerHTML = title.outerHTML + html;
    document.title = (status.toolName || "提交状态") + " · QTools 工具源";
    if (status.status !== "pending") { stopped = true; }
  }

  async function load() {
    try {
      var status = await Site.api("/api/v1/submissions/" + encodeURIComponent(id));
      render(status);
      if (!stopped) { setTimeout(load, 5000); }
    } catch (error) {
      stopped = true;
      Site.showError(Site.byId("error"), error);
    }
  }
  load();
})();
