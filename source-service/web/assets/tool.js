(function () {
  "use strict";
  var parts = location.pathname.split("/");
  var id = decodeURIComponent(parts[parts.length - 1] || "");
  var version = new URLSearchParams(location.search).get("version");
  var frame = Site.byId("preview");
  var query = version ? "?version=" + encodeURIComponent(version) : "";

  function metadata(tool, manifest) {
    var rows = [
      ["工具 ID", tool.id], ["版本", tool.version], ["分类", tool.category || "未分类"]
    ];
    if (tool.kind === "web") { rows.push(["网址", tool.url]); }
    rows.push(
      ["下载次数", Number(tool.downloads || 0) + " 次"],
      ["下载排名", tool.rank ? "第 " + tool.rank + " / " + tool.toolCount : "—"],
      ["权限", Site.permissions(manifest)], ["安装包", Site.formatBytes(tool.sizeBytes)],
      ["SHA256", tool.sha256], ["更新时间", Site.formatDate(tool.updatedAtUtc)],
      ["历史版本", (tool.versions || []).join("、")]
    );
    Site.byId("metadata").innerHTML = rows.map(function (row) {
      return "<dt>" + Site.escape(row[0]) + "</dt><dd>" + Site.escape(row[1]) + "</dd>";
    }).join("");
  }

  function renderRanking(tool) {
    return Site.api("/api/v1/tools?sort=downloads&pageSize=5").then(function (result) {
      var items = result.items || [];
      if (!items.length) { return; }
      Site.byId("ranking-list").innerHTML = items.map(function (item) {
        return '<li class="ranking-item' + (item.id === tool.id ? " current" : "") + '">' +
          '<span class="ranking-no">' + (item.rank || 0) + "</span>" +
          '<div class="ranking-main"><a href="/tools/' + encodeURIComponent(item.id) + '">' + Site.escape(item.name) + "</a>" +
          "<span>" + (item.category ? Site.escape(item.category) + " · " : "") + "下载 " + Number(item.downloads || 0) + " 次</span></div>" +
          (item.pinned ? '<span class="badge pin">置顶</span>' : "") +
          "</li>";
      }).join("");
      Site.byId("ranking").classList.remove("hidden");
    }, function () { });
  }

  Site.api("/api/v1/tools/" + encodeURIComponent(id) + query).then(function (result) {
    var tool = result.tool;
    var isWeb = tool.kind === "web";
    Site.byId("glyph").textContent = tool.glyph || (isWeb ? "↗" : "◇");
    Site.byId("name").textContent = tool.name;
    Site.byId("version").innerHTML = '<span class="badge ok">已审核</span><span class="badge">v' + Site.escape(tool.version) + "</span>" +
      (isWeb ? '<span class="badge web">在线工具</span>' : "") +
      (tool.pinned ? '<span class="badge pin">置顶</span>' : "") +
      (tool.rank ? '<span class="badge rank">下载榜 #' + tool.rank + "</span>" : "");
    Site.byId("description").textContent = tool.description || "这个工具还没有填写描述。";
    Site.byId("download").href = tool.downloadUrl + query;
    if (isWeb) {
      Site.byId("web-open").href = tool.url;
      Site.byId("web-open").hidden = false;
      Site.byId("download").className = "button";
      Site.byId("preview-panel").hidden = true;
      Site.qs(".detail-grid").classList.add("web-detail");
    }
    document.title = tool.name + " · QTools 工具源";
    metadata(tool, result.manifest);
    renderRanking(tool);
    if (!isWeb) {
      frame.dataset.sid = tool.previewUrl.split("/").filter(Boolean).pop();
      window.QToolsPreview.attach(frame, frame.dataset.sid, result.manifest);
      frame.src = tool.previewUrl;
    }
    Site.byId("loading").classList.add("hidden");
    Site.byId("detail").classList.remove("hidden");
  }).catch(function (error) {
    Site.byId("loading").textContent = "";
    Site.showError(Site.byId("error"), error);
  });
})();
