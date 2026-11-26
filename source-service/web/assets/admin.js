(function () {
  "use strict";
  var csrf = "";
  var activeTab = "pending";

  async function request(path, options) {
    options = options || {};
    options.headers = Object.assign({}, options.headers || {});
    if (csrf) { options.headers["X-CSRF-Token"] = csrf; }
    return Site.api(path, options);
  }

  function showAdmin() {
    Site.byId("login-card").classList.add("hidden");
    Site.byId("admin-main").classList.remove("hidden");
    Site.byId("logout").classList.remove("hidden");
  }

  function showLogin() {
    csrf = "";
    Site.byId("login-card").classList.remove("hidden");
    Site.byId("admin-main").classList.add("hidden");
    Site.byId("logout").classList.add("hidden");
  }

  async function loadStats() {
    var stats = await request("/admin/api/stats");
    Site.byId("stats").innerHTML = [
      [stats.pending, "待审核"], [stats.approved, "已通过提交"], [stats.rejected, "已拒绝提交"],
      [stats.tools, "目录工具"], [Number(stats.downloads || 0), "累计下载"]
    ].map(function (item) { return '<div class="card stat"><strong>' + item[0] + "</strong><span>" + item[1] + "</span></div>"; }).join("");
  }

  function warnings(submission) {
    if (!submission.warnings || !submission.warnings.length) { return '<div class="notice">未发现服务端风险提示</div>'; }
    return '<ul class="warning-list">' + submission.warnings.map(function (warning) {
      return '<li class="' + (warning.level === "high" ? "high" : "") + '"><strong>' + Site.escape(warning.code) + "</strong> · " + Site.escape(warning.message) + "</li>";
    }).join("") + "</ul>";
  }

  function submissionCard(submission) {
    var article = document.createElement("article");
    var isWeb = submission.kind === "web";
    article.className = "card submission";
    var body = isWeb
      ? '<div class="notice web-link">在线工具网址：<a href="' + Site.escape(submission.webUrl || "") + '" target="_blank" rel="noopener">' + Site.escape(submission.webUrl || "") + "</a></div>"
      : '<iframe class="admin-preview" title="待审核工具预览" sandbox="allow-scripts" referrerpolicy="no-referrer"></iframe>';
    article.innerHTML = '<div class="submission-head"><div><h2>' + Site.escape(submission.manifest.name) + '</h2><div class="tool-footer">' + (isWeb ? '<span class="badge web">在线工具</span>' : "") + '<span class="badge">v' + Site.escape(submission.manifest.version) + '</span><span class="badge">' + Site.escape(submission.manifest.id) + '</span><span class="badge">' + Site.formatBytes(submission.sizeBytes) + '</span></div></div><div>' + Site.statusBadge(submission.status) + "</div></div>" +
      '<p class="tool-description">' + Site.escape(submission.manifest.description || "无描述") + "</p>" +
      '<dl class="meta-list"><dt>权限</dt><dd>' + Site.escape(Site.permissions(submission.manifest)) + "</dd><dt>SHA256</dt><dd>" + Site.escape(submission.sha256) + "</dd></dl>" +
      warnings(submission) +
      '<label style="margin-top:14px">审核备注<input class="review-note" type="text" maxlength="500" placeholder="拒绝原因或审核说明"></label>' +
      '<div class="form-actions"><button class="primary approve" type="button">通过并发布</button><button class="danger reject" type="button">拒绝</button></div>' +
      body;
    if (!isWeb) {
      var frame = Site.qs(".admin-preview", article);
      window.QToolsPreview.attach(frame, submission.id, submission.manifest);
      frame.src = "/admin/preview/" + encodeURIComponent(submission.id) + "/";
    }
    Site.qs(".approve", article).addEventListener("click", function () { review(submission.id, "approve", article); });
    Site.qs(".reject", article).addEventListener("click", function () { review(submission.id, "reject", article); });
    return article;
  }

  async function review(id, action, card) {
    var note = Site.qs(".review-note", card).value.trim();
    if (action === "reject" && !note) {
      Site.showError(Site.byId("error"), new Error("拒绝时请填写原因"));
      return;
    }
    var buttons = card.querySelectorAll("button");
    buttons.forEach(function (button) { button.disabled = true; });
    try {
      await request("/admin/api/submissions/" + encodeURIComponent(id) + "/review", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action: action, note: note })
      });
      await loadAll();
    } catch (error) {
      buttons.forEach(function (button) { button.disabled = false; });
      Site.showError(Site.byId("error"), error);
    }
  }

  async function loadPending() {
    var result = await request("/admin/api/submissions?status=pending");
    var panel = Site.byId("pending-panel");
    panel.innerHTML = "";
    if (!result.items.length) {
      var empty = document.createElement("div");
      empty.className = "card empty";
      empty.textContent = "当前没有待审核提交";
      panel.appendChild(empty);
      return;
    }
    result.items.forEach(function (submission) { panel.appendChild(submissionCard(submission)); });
  }

  function toolRow(tool, rank) {
    var current = tool.versions && tool.versions.length ? tool.versions[0] : null;
    var category = tool.category ? '<span class="badge category">' + Site.escape(tool.category) + "</span>" : '<span class="badge">未分类</span>';
    var pinned = tool.pinned ? '<span class="badge pin">置顶</span>' : "";
    var kind = tool.kind === "web" ? '<span class="badge web">在线工具</span>' : '<span class="badge">小工具</span>';
    var rankText = rank ? "#" + rank : "—";
    return '<tr class="tool-row" data-id="' + Site.escape(tool.id) + '">' +
      '<td><div class="admin-tool"><strong>' + Site.escape(tool.name) + "</strong><span>" + Site.escape(tool.id) + "</span><span class=\"admin-kind\">" + kind + "</span></div></td>" +
      '<td data-label="分类">' + category + "</td>" +
      '<td data-label="下载"><div class="download-cell"><strong>' + Number(tool.downloads || 0) + "</strong><span>排名 " + rankText + "</span></div></td>" +
      '<td data-label="版本">v' + Site.escape(current ? current.version : "—") + "</td>" +
      '<td data-label="状态"><div class="cell-badges">' + (tool.published ? '<span class="badge ok">已发布</span>' : '<span class="badge danger">已下架</span>') + pinned + "</div></td>" +
      '<td class="actions-cell"><div class="admin-actions">' +
        '<button class="button small edit" type="button">编辑</button>' +
        '<button class="button small pin" type="button">' + (tool.pinned ? "取消置顶" : "置顶") + "</button>" +
        '<button class="danger small toggle" type="button" data-published="' + (tool.published ? "1" : "0") + '">' + (tool.published ? "下架" : "重新发布") + "</button>" +
      "</div></td></tr>" +
      '<tr class="editor-row hidden"><td colspan="6"><div class="tool-editor"><div class="editor-grid">' +
        '<label>工具描述<textarea class="edit-description" maxlength="500" placeholder="展示在公开目录与工具详情页">' + Site.escape(tool.description || "") + "</textarea></label>" +
        '<label>分类<input class="edit-category" type="text" maxlength="40" list="category-options" placeholder="例如：效率" value="' + Site.escape(tool.category || "") + '"></label>' +
        '<label class="editor-pin"><input class="edit-pinned" type="checkbox"' + (tool.pinned ? " checked" : "") + "> 目录置顶</label>" +
      '</div><div class="form-actions"><button class="primary small save" type="button">保存修改</button><button class="small cancel" type="button">取消</button></div></div></td></tr>';
  }

  async function loadTools() {
    var result = await request("/admin/api/tools");
    var items = result.items || [];
    var published = items.filter(function (tool) { return tool.published; }).sort(function (a, b) {
      return (Number(b.downloads) || 0) - (Number(a.downloads) || 0) ||
        new Date(b.updatedAtUtc).getTime() - new Date(a.updatedAtUtc).getTime();
    });
    var ranks = {};
    published.forEach(function (tool, index) { ranks[tool.id] = index + 1; });
    var categories = [];
    items.forEach(function (tool) {
      if (tool.category && categories.indexOf(tool.category) < 0) { categories.push(tool.category); }
    });
    Site.byId("category-options").innerHTML = categories.map(function (name) {
      return '<option value="' + Site.escape(name) + '"></option>';
    }).join("");
    Site.byId("tools-summary").textContent = "共 " + items.length + " 个工具 · 已发布 " + published.length + " 个";
    Site.byId("tools-body").innerHTML = items.map(function (tool) { return toolRow(tool, ranks[tool.id]); }).join("");

    Site.byId("tools-body").querySelectorAll(".tool-row").forEach(function (row) {
      var id = row.dataset.id;
      var editor = row.nextElementSibling;
      Site.qs(".edit", row).addEventListener("click", function () {
        editor.classList.toggle("hidden");
        if (!editor.classList.contains("hidden")) { Site.qs(".edit-description", editor).focus(); }
      });
      Site.qs(".cancel", editor).addEventListener("click", function () { editor.classList.add("hidden"); });
      Site.qs(".save", editor).addEventListener("click", async function () {
        var button = this;
        button.disabled = true;
        try {
          await request("/admin/api/tools/" + encodeURIComponent(id) + "/metadata", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              description: Site.qs(".edit-description", editor).value,
              category: Site.qs(".edit-category", editor).value,
              pinned: Site.qs(".edit-pinned", editor).checked
            })
          });
          await Promise.all([loadTools(), loadStats()]);
        } catch (error) {
          button.disabled = false;
          Site.showError(Site.byId("error"), error);
        }
      });
      Site.qs("button.pin", row).addEventListener("click", async function () {
        var button = this;
        var pinned = button.textContent !== "取消置顶";
        button.disabled = true;
        try {
          await request("/admin/api/tools/" + encodeURIComponent(id) + "/metadata", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ pinned: pinned })
          });
          await Promise.all([loadTools(), loadStats()]);
        } catch (error) {
          button.disabled = false;
          Site.showError(Site.byId("error"), error);
        }
      });
      Site.qs(".toggle", row).addEventListener("click", async function () {
        var button = this;
        button.disabled = true;
        try {
          if (button.dataset.published === "1") {
            if (!confirm("下架后公开目录和下载将立即不可用，确定继续？")) { button.disabled = false; return; }
            await request("/admin/api/tools/" + encodeURIComponent(id) + "/unpublish", { method: "POST" });
          } else {
            await request("/admin/api/tools/" + encodeURIComponent(id) + "/publish", { method: "POST" });
          }
          await Promise.all([loadTools(), loadStats()]);
        } catch (error) {
          button.disabled = false;
          Site.showError(Site.byId("error"), error);
        }
      });
    });
  }

  function setTab(tab) {
    activeTab = tab;
    Site.byId("pending-tab").classList.toggle("active", tab === "pending");
    Site.byId("tools-tab").classList.toggle("active", tab === "tools");
    Site.byId("pending-panel").classList.toggle("hidden", tab !== "pending");
    Site.byId("tools-panel").classList.toggle("hidden", tab !== "tools");
  }

  async function loadAll() {
    Site.byId("error").classList.add("hidden");
    try {
      await Promise.all([loadStats(), activeTab === "pending" ? loadPending() : loadTools()]);
      var state = Site.byId("session-state");
      state.textContent = "已登录 · 修改会自动保存";
      state.classList.remove("hidden");
    } catch (error) {
      if (error.code === "AUTH_REQUIRED") { showLogin(); return; }
      Site.showError(Site.byId("error"), error);
    }
  }

  Site.byId("login-form").addEventListener("submit", async function (event) {
    event.preventDefault();
    Site.byId("login-error").classList.add("hidden");
    Site.byId("login-button").disabled = true;
    try {
      var result = await Site.api("/admin/api/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password: Site.byId("password").value })
      });
      csrf = result.csrfToken;
      Site.byId("password").value = "";
      showAdmin();
      await loadAll();
    } catch (error) {
      Site.showError(Site.byId("login-error"), error);
    } finally {
      Site.byId("login-button").disabled = false;
    }
  });
  Site.byId("logout").addEventListener("click", async function () {
    try { await request("/admin/api/logout", { method: "POST" }); } catch (error) { }
    showLogin();
  });
  Site.byId("refresh").addEventListener("click", loadAll);
  Site.byId("pending-tab").addEventListener("click", function () { setTab("pending"); loadPending(); });
  Site.byId("tools-tab").addEventListener("click", function () { setTab("tools"); loadTools(); });

  Site.api("/admin/api/session").then(function (session) {
    if (session.authenticated) { csrf = session.csrfToken; showAdmin(); loadAll(); } else { showLogin(); }
  }).catch(function (error) { showLogin(); Site.showError(Site.byId("login-error"), error); });
})();
