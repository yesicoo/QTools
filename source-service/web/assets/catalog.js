(function () {
  "use strict";
  var state = { page: 1, pageSize: 8, total: 0, categories: [], sort: "recommended", timer: 0, sequence: 0, loading: false };
  var grid = Site.byId("tool-grid");
  var errorBox = Site.byId("error");
  var search = Site.byId("search");

  var downloadIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3v12m0 0-4-4m4 4 4-4M5 15v4a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  var previewIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12s3.4-6 9.5-6 9.5 6 9.5 6-3.4 6-9.5 6-9.5-6-9.5-6Z" fill="none" stroke="currentColor" stroke-width="1.7"/><circle cx="12" cy="12" r="2.6" fill="none" stroke="currentColor" stroke-width="1.7"/></svg>';

  function skeleton() {
    return '<div class="tool-skeleton"><div class="skeleton-head"><span class="skeleton-glyph"></span><span class="skeleton-line short"></span></div><span class="skeleton-line title"></span><span class="skeleton-line"></span><span class="skeleton-line"></span><span class="skeleton-actions"></span></div>';
  }

  function card(tool) {
    var isWeb = tool.kind === "web";
    var pinned = tool.pinned ? '<span class="badge pin">置顶</span>' : "";
    var kindBadge = isWeb ? '<span class="badge web">在线工具</span>' : "";
    var category = tool.category ? '<span class="badge category">' + Site.escape(tool.category) + "</span>" : "";
    var permissions = isWeb ? "外部网址" : tool.permissions && tool.permissions.length ? "权限：" + Site.escape(Site.permissions({ permissions: tool.permissions })) : "无额外权限";
    var popularity = "下载 " + Number(tool.downloads || 0) + (tool.rank ? " · 排行 #" + tool.rank : "");
    var openButton = isWeb && tool.url
      ? '<a class="button small" href="' + Site.escape(tool.url) + '" target="_blank" rel="noopener">' + previewIcon + "打开网址</a>"
      : tool.previewUrl ? '<a class="button small" href="' + Site.escape(tool.previewUrl) + '" target="_blank" rel="noopener">' + previewIcon + "沙箱预览</a>" : "";
    return '<article class="card tool-card">' +
      '<div class="tool-card-head"><div class="glyph">' + Site.escape(tool.glyph || (isWeb ? "↗" : "◇")) + "</div><div class=\"tool-card-title\"><div class=\"tool-tags\">" + pinned + kindBadge + category + '<span class="badge version">v' + Site.escape(tool.version) + "</span></div><h2><a href=\"/tools/" + encodeURIComponent(tool.id) + '">' + Site.escape(tool.name) + "</a></h2></div></div>" +
      '<p class="tool-description">' + Site.escape(tool.description || "这个工具还没有填写描述。") + "</p>" +
      '<div class="tool-card-meta"><span>' + popularity + "</span><span>" + Site.formatBytes(tool.sizeBytes) + "</span><span>" + Site.formatDate(tool.updatedAtUtc) + "</span><span>" + permissions + "</span></div>" +
      '<div class="tool-card-actions"><a class="button primary small" href="/tools/' + encodeURIComponent(tool.id) + '">查看详情</a>' +
      openButton +
      (tool.downloadUrl ? '<a class="icon-button download-button" href="' + Site.escape(tool.downloadUrl) + '" title="下载 .qtools" aria-label="下载 ' + Site.escape(tool.name) + ' 安装包">' + downloadIcon + "</a>" : "") +
      "</div></article>";
  }

  function emptyState(filtered) {
    if (filtered) {
      return '<div class="card catalog-empty"><span class="empty-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6.5" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="m16 16 4 4M8.5 10.5h4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg></span><h2>没有找到匹配的工具</h2><p>试试更短的关键词，或切换到其他分类。</p><button id="reset-catalog" class="button" type="button">清除筛选</button></div>';
    }
    return '<div class="card catalog-empty"><span class="empty-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 8.5 12 4l8 4.5v7L12 20l-8-4.5v-7Z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/><path d="m4 8.5 8 4.5 8-4.5M12 13v7" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/></svg></span><h2>目录还是空的</h2><p>成为第一个提交工具的人，审核通过后会在这里公开。</p><a class="button primary" href="/submit">提交你的工具</a></div>';
  }

  function renderCategories() {
    var container = Site.byId("category-list");
    var categories = [""].concat(state.categories || []);
    container.innerHTML = categories.map(function (category) {
      var active = category === state.category;
      return '<button class="category-button' + (active ? " active" : "") + '" type="button" data-category="' + Site.escape(category) + '" aria-pressed="' + (active ? "true" : "false") + '"><span>' + Site.escape(category || "全部分类") + "</span><span class=\"category-arrow\">›</span></button>";
    }).join("");
    container.querySelectorAll("[data-category]").forEach(function (button) {
      button.addEventListener("click", function () {
        var category = button.getAttribute("data-category") || "";
        if (category === state.category) { return; }
        state.category = category;
        state.page = 1;
        renderCategories();
        Site.byId("active-filter").textContent = category ? "分类：" + category : "全部公开工具";
        load();
      });
    });
  }

  function renderPagination() {
    var pages = Math.max(1, Math.ceil(state.total / state.pageSize));
    var container = Site.byId("pagination");
    container.textContent = "";
    if (pages <= 1) { return; }
    var previous = document.createElement("button");
    previous.className = "button";
    previous.textContent = "上一页";
    previous.disabled = state.page <= 1;
    previous.addEventListener("click", function () { state.page--; load(); });
    var label = document.createElement("span");
    label.className = "page-label";
    label.textContent = "第 " + state.page + " / " + pages + " 页";
    var next = document.createElement("button");
    next.className = "button";
    next.textContent = "下一页";
    next.disabled = state.page >= pages;
    next.addEventListener("click", function () { state.page++; load(); });
    container.append(previous, label, next);
  }

  async function load() {
    var sequence = ++state.sequence;
    state.loading = true;
    errorBox.classList.add("hidden");
    Site.byId("result-count").textContent = "正在加载…";
    grid.classList.add("is-loading");
    grid.innerHTML = new Array(4).fill(skeleton()).join("");
    Site.byId("refresh").classList.add("is-loading");
    try {
      var query = new URLSearchParams({
        q: search.value.trim(),
        category: state.category,
        sort: state.sort,
        page: String(state.page),
        pageSize: String(state.pageSize)
      });
      var result = await Site.api("/api/v1/tools?" + query.toString());
      if (sequence !== state.sequence) { return; }
      state.total = result.total;
      state.categories = result.categories || [];
      if (state.categories.indexOf(state.category) < 0 && state.category !== "") {
        state.category = "";
        state.page = 1;
        renderCategories();
        return load();
      }
      var pages = Math.max(1, Math.ceil(state.total / state.pageSize));
      if (state.page > pages) {
        state.page = pages;
        return load();
      }
      grid.innerHTML = result.items.length ? result.items.map(card).join("") : emptyState(!!(search.value.trim() || state.category));
      Site.byId("result-count").textContent = "共 " + result.total + " 个工具";
      Site.byId("active-filter").textContent = state.category ? "分类：" + state.category : "全部公开工具";
      renderCategories();
      renderPagination();
      var reset = Site.byId("reset-catalog");
      if (reset) {
        reset.addEventListener("click", function () {
          state.category = "";
          state.page = 1;
          search.value = "";
          renderCategories();
          load();
        });
      }
    } catch (error) {
      if (sequence !== state.sequence) { return; }
      grid.innerHTML = "";
      Site.byId("result-count").textContent = "目录加载失败";
      Site.showError(errorBox, error);
    } finally {
      if (sequence === state.sequence) {
        state.loading = false;
        grid.classList.remove("is-loading");
        Site.byId("refresh").classList.remove("is-loading");
      }
    }
  }

  search.addEventListener("input", function () {
    clearTimeout(state.timer);
    state.timer = setTimeout(function () { state.page = 1; load(); }, 220);
  });
  Site.byId("category-reset").addEventListener("click", function () {
    state.category = "";
    state.page = 1;
    search.value = "";
    renderCategories();
    load();
  });
  Site.byId("refresh").addEventListener("click", load);
  Site.byId("sort").addEventListener("change", function () {
    state.sort = Site.byId("sort").value;
    state.page = 1;
    load();
  });
  Site.api("/api/v1/meta").then(function (meta) {
    Site.byId("service-version").textContent = "v" + meta.version;
    Site.byId("service-limit").textContent = Site.formatBytes(meta.maxPackageBytes);
  }).catch(function () {
    Site.byId("service-version").textContent = "不可用";
    Site.byId("service-limit").textContent = "不可用";
  });
  renderCategories();
  load();
})();
