/* QTools 双模式页面：
 * 主窗口（细长条）：统一列表（小工具 + 本机程序，按使用频次排序）；
 *   点工具 → 内容窗口贴边打开；点本机程序 → 直接启动。
 * 内容窗口（?mode=content&view=…）：内嵌工具 / AI 创建 / 导入 / 在线工具 / 版本管理。
 * 约束：本页面 CSP 为 connect-src 'none'，禁止 fetch/XHR——一切能力调用都走 window.host 桥接。
 * 约束：一律用 on(id, event, handler) 绑事件（元素缺失静默跳过，避免单点缺失中断整个脚本）。 */
(function () {
  "use strict";

  var host = window.host || null;
  var lastGenerated = null;
  var pendingUpdate = null;
  var pendingImport = null;      /* 已选 HTML：{ name, html } */
  var pendingPackage = null;     /* 已选导出包：{ path, itemKind, name } */
  var importing = false;

  /* 页面模式：主窗口（默认）或内容窗口（宿主 OpenContentAsync 以 ?mode=content 打开） */
  var pageQuery = (function () {
    var q = {};
    var s = (window.location && window.location.search) || "";
    if (s.charAt(0) === "?") { s = s.slice(1); }
    s.split("&").forEach(function (pair) {
      if (!pair) { return; }
      var kv = pair.split("=");
      q[decodeURIComponent(kv[0])] = decodeURIComponent((kv[1] || "").replace(/\+/g, " "));
    });
    return q;
  })();
  var isContentMode = pageQuery.mode === "content";
  /* 管理窗口由哪个入口打开（tool/create/import/web/version）；版本管理直达时关闭按钮直接收起窗口 */
  var contentEntryView = isContentMode ? (pageQuery.view || "") : "";
  if (isContentMode && document.body) {
    document.body.classList.remove("mode-main");
    document.body.classList.add("mode-content");
  }

  var allItems = [];        /* 统一列表（manager.launcherList，已按使用频次排序） */
  var indexStatus = null;   /* 后台索引状态 */
  var allTools = [];        /* 内容窗口用的工具清单（manager.listTools） */
  var searchTerm = "";
  var currentTool = null;       /* 内嵌中的工具（toolListItem 形状） */
  var currentVersionTool = null;
  var embedSequence = 0;
  var updateSequence = 0;
  var createSequence = 0;
  var sourceState = {
    query: "", category: "", page: 1, catalog: null, categories: [],
    detail: null, version: "", availableVersions: [], review: null, detailSequence: 0,
    loading: false, loadingDetail: false, installing: false, downloading: false,
    installSequence: 0, downloadSequence: 0, timer: null, config: null
  };

  /* 右键浮层菜单：一次只开一个；菜单 fixed 定位浮在列表上方，不占行内布局 */
  var openMenuState = null;         /* { key, item, li, btn, menuEl, actionsEl, detailEl } */

  function $(id) { return document.getElementById(id); }

  /** 绑定事件：元素不存在时静默跳过，避免单个缺失节点中断整个脚本。 */
  function on(id, event, handler) {
    var el = document.getElementById(id);
    if (el) { el.addEventListener(event, handler); }
    return el;
  }

  function esc(s) {
    return String(s === null || s === undefined ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function toast(message, isError) {
    var el = $("toast");
    if (!el) { return; }
    el.textContent = message;
    el.className = "toast show" + (isError ? " err" : "");
    el.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(function () { el.hidden = true; }, 3200);
  }

  function errText(e) {
    return (e && e.code ? e.code + "：" : "") + (e && e.message ? e.message : e);
  }

  function firstGlyph(name) {
    var text = (name || "").trim();
    return text ? Array.from(text)[0].toUpperCase() : "?";
  }

  /** 工具图标：显式 glyph 优先；内置常见工具保留语义图标；否则显示名称首字符。 */
  function toolGlyph(tool) {
    if (tool.glyph) { return tool.glyph; }
    var id = (tool.id + " " + (tool.name || "")).toLowerCase();
    if (id.indexOf("json") >= 0) { return "{ }"; }
    if (id.indexOf("qr") >= 0 || id.indexOf("二维码") >= 0) { return "▦"; }
    if (id.indexOf("note") >= 0 || id.indexOf("todo") >= 0) { return "✎"; }
    if (id.indexOf("calc") >= 0) { return "＋"; }
    return firstGlyph(tool.name || tool.id);
  }

  function appGlyph(app) {
    return app.glyph || firstGlyph(app.name || app.id);
  }

  function matchesSearch(text) {
    if (!searchTerm) { return true; }
    return (text || "").toLowerCase().indexOf(searchTerm) >= 0;
  }

  function severityLabel(s) {
    return s === 0 ? "致命" : s === 1 ? "高危" : "提示";
  }

  /* ================= 视图切换 ================= */
  var VIEWS = ["tool-view", "content-home", "create-view", "version-view", "import-view", "web-view", "source-view"];

  function showView(name) {
    VIEWS.forEach(function (id) {
      var el = $(id);
      if (el) { el.hidden = id !== name; }
    });

    if (name !== "tool-view") { embedSequence += 1; }
    if (name === "tool-view" && currentTool) {
      var frame = $("tool-frame");
      if (frame && (!frame.getAttribute("src") || frame.src === "about:blank")) { reloadTool(); }
    } else if (currentTool) {
      var hiddenFrame = $("tool-frame");
      if (hiddenFrame) { hiddenFrame.src = "about:blank"; }
    }
  }

  function closeToolView() {
    currentTool = null;
    var frame = $("tool-frame");
    if (frame) { frame.src = "about:blank"; }
    showView("content-home");
    renderNav();
  }

  /* ================= 数据刷新 ================= */
  function refreshLauncher() {
    if (!host) { return; }
    return host.callManager("manager.launcherList", {}).then(function (r) {
      allItems = (r && r.items) || [];
      indexStatus = (r && r.index) || null;
      renderNav();
      renderIndexStatus();
    }, function (e) {
      toast("读取列表失败：" + errText(e), true);
    });
  }

  function renderIndexStatus() {
    var el = $("index-status");
    if (!el) { return; }
    if (!indexStatus) { el.textContent = ""; return; }
    if (indexStatus.indexing) {
      el.textContent = "正在后台索引本机程序…";
    } else if (indexStatus.programCount > 0) {
      el.textContent = "已索引 " + indexStatus.programCount + " 个本机程序";
    } else {
      el.textContent = "尚未索引到本机程序";
    }
  }

  function refreshTools() {
    if (!host) { return; }
    return host.callManager("manager.listTools", {}).then(function (result) {
      allTools = (result && result.tools) || [];
      var activeTool = allTools.filter(function (tool) { return currentTool && tool.id === currentTool.id; })[0];
      if (activeTool && $("tool-meta")) { $("tool-meta").textContent = toolMeta(activeTool); }
      renderNav();
    }, function (e) {
      toast("读取工具列表失败：" + errText(e), true);
    });
  }

  /* ================= 统一列表渲染（小工具 + 本机程序，按使用频次排序） ================= */
  function renderNav() {
    var ul = $("nav-list");
    var empty = $("side-empty");
    if (!ul) { return; }

    openMenuState = null;
    ul.innerHTML = "";
    var total = 0;

    allItems.forEach(function (item) {
      if (!matchesSearch(item.searchKey || (item.name + " " + item.id))) { return; }
      /* 首页只显示用过的条目（索引程序默认 0 次）；搜索时不限，点开即计次并回到首页 */
      if (!searchTerm && !(item.useCount > 0)) { return; }
      total += 1;
      ul.appendChild(buildNavItem(item));
    });

    if (empty) {
      empty.hidden = total !== 0;
      if (total === 0) {
        empty.textContent = searchTerm ? "没有匹配的条目" : "还没有使用记录，搜索可直接打开全部条目";
      }
    }

    /* 当前内嵌工具仍存在时保持高亮 */
    markActive();
  }

  function kindLabel(kind) {
    return kind === "tool" ? "小工具" : kind === "web" ? "在线工具" : "本机程序";
  }

  function buildNavItem(item) {
    var isTool = item.kind === "tool";
    var isWeb = item.kind === "web";
    var broken = isWeb ? !item.isValid : (isTool ? !item.isValid : !item.targetExists);
    var glyph = isTool ? toolGlyph(item) : appGlyph(item);
    var badge = isTool
      ? (item.isValid ? (item.category || "v" + (item.version || "")) : "manifest 无效")
      : isWeb
        ? (!item.isValid ? "配置无效" : (item.category || ""))
        : (!item.targetExists ? "目标不存在" : (item.category || ""));

    var li = document.createElement("li");
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "nav-item" + (!isTool ? " app-item" : "") + (broken ? " broken" : "") + (item.pinned ? " pinned" : "");
    btn.dataset.key = item.kind + ":" + item.id;
    btn.title = (item.description ? item.description + "\n" : "") +
      item.name + "（" + kindLabel(item.kind) + "，右键管理）";

    var g = document.createElement("span");
    g.className = "nav-glyph";
    if (item.iconUrl) {
      g.classList.add("has-icon");
      var img = document.createElement("img");
      img.className = "nav-icon";
      img.alt = "";
      img.src = item.iconUrl;
      img.addEventListener("error", function () {
        g.classList.remove("has-icon");
        g.textContent = glyph;
      });
      g.appendChild(img);
    } else {
      g.textContent = glyph;
    }

    var label = document.createElement("span");
    label.className = "nav-label";
    label.innerHTML = "<strong>" + esc(item.name) + "</strong><span>" + esc(kindLabel(item.kind)) + "</span>";

    btn.appendChild(g);
    btn.appendChild(label);
    if (badge) {
      var b = document.createElement("span");
      b.className = "nav-badge";
      b.textContent = badge;
      btn.appendChild(b);
    }
    if (item.pinned) {
      var pin = document.createElement("span");
      pin.className = "nav-pin";
      pin.title = "已置顶";
      pin.innerHTML = menuIcon("pin");
      btn.appendChild(pin);
    }
    /* 调用次数：行尾胶囊，放在最后一个保证数字右对齐（0 次更淡） */
    var uses = document.createElement("span");
    uses.className = "nav-uses" + (item.useCount > 0 ? "" : " zero");
    uses.textContent = (item.useCount || 0) + "次";
    uses.title = "已使用 " + (item.useCount || 0) + " 次";
    btn.appendChild(uses);
    btn.addEventListener("click", function () { openItem(item); });
    btn.addEventListener("contextmenu", function (e) {
      e.preventDefault();
      toggleItemMenu(item, li, btn, e);
    });
    li.appendChild(btn);
    return li;
  }

  /** 列表点击：小工具 → 独立工具窗口；本机程序 → 直接启动；在线工具 → 独立窗口在线打开。 */
  function openItem(item) {
    if (!host) { toast("桥接不可用", true); return; }
    if (item.kind === "tool") {
      if (!item.isValid) { toast("manifest 无效，不能打开", true); return; }
      host.callManager("manager.openTool", { toolId: item.id }).then(function () {
        refreshLauncher();
      }, function (e) { toast("打开失败：" + errText(e), true); });
    } else if (item.kind === "web") {
      if (!item.isValid) { toast("配置无效，不能打开", true); return; }
      openWebTool(item);
    } else {
      if (!item.targetExists) { toast("目标不存在，可能已被卸载", true); return; }
      launchApp(item);
    }
  }

  /* ================= 内容窗口 ================= */
  function openContentPanel(view, toolId) {
    if (!host) { toast("桥接不可用", true); return; }
    var payload = { view: view };
    if (toolId) { payload.toolId = toolId; }
    host.callManager("manager.openContent", payload).then(function () {
      if (!isContentMode) { refreshLauncher(); }
    }, function (e) { toast("打开失败：" + errText(e), true); });
  }

  function closeContent() {
    if (!host) { return; }
    host.callManager("manager.closeContent", {}).catch(function () { /* 窗口可能已被用户关闭 */ });
  }

  function markActive() {
    var items = document.querySelectorAll(".nav-item.active");
    for (var i = 0; i < items.length; i++) { items[i].classList.remove("active"); }
    if (!currentTool || currentTool.id === undefined) { return; }
    var node = document.querySelector('.nav-item[data-key="tool:' + currentTool.id + '"]');
    if (node) { node.classList.add("active"); }
  }

  /* ================= 内嵌打开工具 ================= */
  /* 内容窗口的置顶 / 钉住状态（钉住 = 锁定位置和大小），由宿主持久化 */
  var contentWindowState = { topmost: false, pinned: false };

  function renderContentWindowState() {
    var top = $("tool-topmost");
    if (top) { top.className = "btn ghost sm" + (contentWindowState.topmost ? " active" : ""); }
    var pin = $("tool-pinned");
    if (pin) { pin.className = "btn ghost sm" + (contentWindowState.pinned ? " active" : ""); }
  }

  function loadContentWindowState() {
    if (!host || !isContentMode) { return; }
    host.callManager("manager.getContentWindowState", {}).then(function (state) {
      contentWindowState.topmost = !!(state && state.topmost);
      contentWindowState.pinned = !!(state && state.pinned);
      renderContentWindowState();
    }, function () { /* 内容窗口可能已关闭 */ });
  }

  function setContentWindowFlag(method, enabled) {
    if (!host || !isContentMode) { return; }
    host.callManager(method, { enabled: enabled }).then(function (state) {
      contentWindowState.topmost = !!(state && state.topmost);
      contentWindowState.pinned = !!(state && state.pinned);
      renderContentWindowState();
    }, function (e) { toast("操作失败：" + errText(e), true); });
  }

  on("tool-topmost", "click", function () {
    setContentWindowFlag("manager.setContentWindowTopmost", !contentWindowState.topmost);
  });
  on("tool-pinned", "click", function () {
    setContentWindowFlag("manager.setContentWindowPinned", !contentWindowState.pinned);
  });

  function toolMeta(t) {
    return "v" + (t.version || "") +
      ((t.permissions && t.permissions.length) ? " · " + t.permissions.join(" / ") : "") +
      "（内嵌上传/摄像头等受限，需要时用独立窗口）";
  }

  function embedTool(t) {
    if (!host) { toast("桥接不可用", true); return; }
    if (!t.isValid) { toast("manifest 无效，不能打开", true); return; }

    var requestNumber = ++embedSequence;
    host.callManager("manager.embedTool", { toolId: t.id }).then(function (r) {
      if (requestNumber !== embedSequence) { return; }
      currentTool = { id: r.toolId, name: r.name, permissions: r.permissions };

      var frame = $("tool-frame");
      if (frame) {
        frame.onload = function () { markActive(); };
        frame.src = r.url;
      }

      var listedTool = allTools.filter(function (tool) { return tool.id === r.toolId; })[0] || t;
      $("tool-name").textContent = r.name;
      $("tool-glyph").textContent = toolGlyph(listedTool);
      $("tool-meta").textContent = toolMeta({ version: listedTool.version, permissions: r.permissions });
      $("tool-versions").disabled = !t.isValid;
      showView("tool-view");
      markActive();
      renderNav();
    }, function (e) {
      if (requestNumber === embedSequence) { toast("打开失败：" + errText(e), true); }
    });
  }

  function reloadTool() {
    if (!currentTool) { return; }
    var t = allTools.filter(function (x) { return x.id === currentTool.id; })[0];
    if (t) { embedTool(t); }
  }

  function deleteTool(t) {
    var deleteStorage = confirm(
      "确定删除工具「" + t.name + "」？\n\n" +
      "点击「确定」= 同时删除其存储数据；\n点击「取消」= 仅删除工具、保留存储。");
    if (!deleteStorage) {
      if (!confirm("将保留存储数据，仅删除工具本身。继续？")) { return; }
    }
    host.callManager("manager.deleteTool", { toolId: t.id, deleteStorage: deleteStorage })
      .then(function () {
        toast(deleteStorage ? "已删除工具及其存储" : "已删除工具（保留存储）");
        if (isContentMode) { closeContent(); }
        else { refreshLauncher(); }
      }, function (e) { toast("删除失败：" + errText(e), true); });
  }

  /* ================= 本机程序：直接启动 / 移除 ================= */
  function launchApp(item) {
    if (!host) { return; }
    host.callManager("manager.launchApp", { appId: item.id }).then(function (r) {
      toast("已启动 " + item.name + (r.processId ? "（PID " + r.processId + "）" : ""));
      refreshLauncher();
    }, function (e) { toast("启动失败：" + errText(e), true); });
  }

  function deleteApp(item) {
    if (!confirm("确定从启动器移除「" + item.name + "」？\n（只移除启动配置，不会卸载系统里的程序）")) { return; }
    host.callManager("manager.deleteApp", { appId: item.id }).then(function () {
      toast("已移除 " + item.name);
      refreshLauncher();
    }, function (e) { toast("移除失败：" + errText(e), true); });
  }

  /* ================= 索引程序：右键从列表隐藏 ================= */
  function hideProgram(item) {
    if (!confirm("确定从列表隐藏「" + item.name + "」？\n（只影响启动器列表，不会改动程序本身；记录会保留，重新索引也不会再出现）")) { return; }
    host.callManager("manager.hideProgram", { programId: item.id }).then(function () {
      toast("已隐藏 " + item.name);
      refreshLauncher();
    }, function (e) { toast("操作失败：" + errText(e), true); });
  }

  /* ================= 右键浮层菜单（详情 / 属性 / 版本管理 / 置顶 / 导出 / 删除，一次只开一个） ================= */
  function itemMenuKey(item) { return item.kind + ":" + item.id; }

  /** 收起菜单：浮层直接隐藏；条目按钮始终可见（菜单不占列表布局）。 */
  function closeItemMenu() {
    var state = openMenuState;
    if (!state) { return; }
    openMenuState = null;
    state.menuEl.hidden = true;
  }

  function toggleItemMenu(item, li, btn, event) {
    var key = itemMenuKey(item);
    if (openMenuState && openMenuState.key === key) { closeItemMenu(); return; }
    closeItemMenu();
    openItemMenu(item, li, btn, event);
  }

  /** 浮层定位：优先鼠标位置，缺坐标时贴条目按钮下方；贴近视口右/下边缘时向内夹取。 */
  function placeItemMenu(menuEl, btn, event) {
    var margin = 8;
    var hasPoint = event && typeof event.clientX === "number" && typeof event.clientY === "number";
    var x = margin;
    var y = margin;
    if (hasPoint) {
      x = event.clientX;
      y = event.clientY;
    } else if (btn && typeof btn.getBoundingClientRect === "function") {
      var rect = btn.getBoundingClientRect();
      x = (rect && rect.left ? rect.left : 0) + 12;
      y = (rect && rect.bottom ? rect.bottom : 0) + 4;
    }

    var vw = typeof window.innerWidth === "number" ? window.innerWidth : 0;
    var vh = typeof window.innerHeight === "number" ? window.innerHeight : 0;
    var w = menuEl.offsetWidth || 216;
    var h = menuEl.offsetHeight || 240;
    if (vw > 0) { x = Math.max(margin, Math.min(x, vw - w - margin)); }
    if (vh > 0) { y = Math.max(margin, Math.min(y, vh - h - margin)); }

    menuEl.style.left = x + "px";
    menuEl.style.top = y + "px";
  }

  /* 菜单图标：内联 SVG（feather 风格，stroke 随文字色），与底部 dock 的图标语言一致 */
  var MENU_ICONS = {
    detail: '<circle cx="12" cy="12" r="9"/><path d="M12 8h.01M12 11v5"/>',
    edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a15 15 0 0 1 0 18 15 15 0 0 1 0-18z"/>',
    trash: '<path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M6 7l1 13h10l1-13"/>',
    shortcut: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
    minus: '<circle cx="12" cy="12" r="9"/><path d="M8 12h8"/>',
    eyeOff: '<path d="M3 3l18 18M10.6 10.6a2 2 0 0 0 2.8 2.8M9.9 5.2A9.6 9.6 0 0 1 12 5c7 0 10 7 10 7a17 17 0 0 1-3 3.9M6.6 6.6A16.6 16.6 0 0 0 2 12s3 7 10 7a9.7 9.7 0 0 0 5.4-1.6"/>',
    pin: '<path d="M9 4h6l-1 6 3 3H7l3-3-1-6z"/><path d="M12 13v7"/>',
    history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/><path d="M12 8v4l3 2"/>',
    download: '<path d="M12 3v12m0 0 4-4m-4 4-4-4"/><path d="M4 19h16"/>',
    upload: '<path d="M12 16V4m0 0-4 4m4-4 4 4"/><path d="M4 20h16"/>',
    back: '<path d="M19 12H5m7-7-7 7 7 7"/>'
  };

  function menuIcon(name) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
      MENU_ICONS[name] + '</svg>';
  }

  function openItemMenu(item, li, btn, event) {
    if (!host) { toast("桥接不可用", true); return; }

    var actionsEl = document.createElement("div");
    actionsEl.className = "menu-actions";

    var detailEl = buildItemDetail(item);
    detailEl.hidden = true;

    var backBtn = document.createElement("button");
    backBtn.type = "button";
    backBtn.className = "menu-action back";
    backBtn.innerHTML = menuIcon("back");
    var backText = document.createElement("span");
    backText.textContent = "返回";
    backBtn.appendChild(backText);
    backBtn.addEventListener("click", function () {
      detailEl.hidden = true;
      actionsEl.hidden = false;
    });
    detailEl.appendChild(backBtn);

    var renameEl = null;   /* 小工具 / 在线工具专用：原位重命名表单 */

    function menuAction(label, cls, handler, icon) {
      var a = document.createElement("button");
      a.type = "button";
      a.className = "menu-action" + (cls ? " " + cls : "");
      a.innerHTML = menuIcon(icon);
      var t = document.createElement("span");
      t.textContent = label;
      a.appendChild(t);
      a.addEventListener("click", handler);
      actionsEl.appendChild(a);
    }

    menuAction("详情", "", function () {
      actionsEl.hidden = true;
      detailEl.hidden = false;
    }, "detail");

    if (item.kind === "web") {
      menuAction("打开网址", "", function () { closeItemMenu(); openWebTool(item); }, "globe");
    } else {
      menuAction("属性", "", function () { revealItem(item); }, "folder");
    }

    /* 版本管理：快速进入该工具的版本面板（独立的宿主管理窗口） */
    if (item.kind === "tool") {
      menuAction("版本管理", "", function () {
        closeItemMenu();
        openContentPanel("version", item.id);
      }, "history");
    }

    if (item.kind === "tool" || item.kind === "web") {
      var isWeb = item.kind === "web";
      renameEl = document.createElement("div");
      renameEl.className = "menu-rename";
      renameEl.hidden = true;

      var renameTitle = document.createElement("div");
      renameTitle.className = "menu-rename-title";
      renameTitle.textContent = isWeb ? "重命名在线工具" : "重命名小工具";
      renameEl.appendChild(renameTitle);

      var renameInput = document.createElement("input");
      renameInput.type = "text";
      renameInput.className = "menu-rename-input";
      renameInput.value = item.name;
      renameInput.maxLength = 60;
      renameInput.placeholder = "工具名称";
      renameEl.appendChild(renameInput);

      var renameRow = document.createElement("div");
      renameRow.className = "menu-rename-row";
      var renameOk = document.createElement("button");
      renameOk.type = "button";
      renameOk.className = "btn primary sm menu-rename-ok";
      renameOk.textContent = "确认";
      var renameCancel = document.createElement("button");
      renameCancel.type = "button";
      renameCancel.className = "btn ghost sm menu-rename-cancel";
      renameCancel.textContent = "取消";
      renameRow.appendChild(renameOk);
      renameRow.appendChild(renameCancel);
      renameEl.appendChild(renameRow);

      var submitRename = function () {
        var name = renameInput.value.trim();
        if (!name) { toast("名称不能为空", true); renameInput.focus(); return; }
        if (name.length > 60) { toast("名称不超过 60 字符", true); renameInput.focus(); return; }
        if (name === item.name) { closeItemMenu(); return; }
        renameOk.disabled = true;
        host.callManager(
          isWeb ? "manager.renameWebTool" : "manager.renameTool",
          isWeb ? { webId: item.id, name: name } : { toolId: item.id, name: name }
        ).then(function () {
          closeItemMenu();
          toast("已重命名为「" + name + "」");
          refreshLauncher();
        }, function (e) {
          renameOk.disabled = false;
          toast("重命名失败：" + errText(e), true);
        });
      };

      renameOk.addEventListener("click", submitRename);
      renameCancel.addEventListener("click", function () {
        renameEl.hidden = true;
        actionsEl.hidden = false;
      });
      renameInput.addEventListener("keydown", function (e) {
        if (e.key === "Enter") { if (e.preventDefault) { e.preventDefault(); } submitRename(); }
      });

      menuAction("重命名", "", function () {
        actionsEl.hidden = true;
        detailEl.hidden = true;
        renameEl.hidden = false;
        renameInput.focus();
        renameInput.select();
      }, "edit");
    }

    /* 列表置顶：置顶条目固定在统一列表最前（只影响排序，与窗口级「置顶」无关） */
    if (item.kind === "tool") {
      menuAction(item.pinned ? "取消置顶" : "置顶", "", function () {
        closeItemMenu();
        host.callManager("manager.setItemPinned", {
          kind: item.kind, id: item.id, pinned: !item.pinned
        }).then(function () {
          toast(item.pinned ? "已取消置顶「" + item.name + "」" : "已置顶「" + item.name + "」");
          refreshLauncher();
        }, function (e) { toast("操作失败：" + errText(e), true); });
      }, "pin");
    }

    /* 导出当前运行版本为 .qtools 包（小工具含清单与当前版本文件，不含历史版本） */
    if (item.kind === "tool" || item.kind === "app" || item.kind === "web") {
      menuAction("导出", "", function () {
        closeItemMenu();
        host.callManager("manager.exportItem", { kind: item.kind, id: item.id }).then(function (r) {
          if (r && r.path) { toast("已导出：" + ((r && r.fileName) || r.path)); }
          else { toast("已取消导出"); }
        }, function (e) { toast("导出失败：" + errText(e), true); });
      }, "download");
    }

    /* 配置工具源后可直接上传分享（小工具 / 在线工具，需管理员审核） */
    if ((item.kind === "tool" || item.kind === "web") &&
        sourceState.config && sourceState.config.baseUrl) {
      menuAction("上传到工具源", "", function () {
        closeItemMenu();
        submitItemToSource(item);
      }, "upload");
    }

    /* 桌面快捷方式：双击直达独立工具窗口（应用已在托盘时由单实例转发打开） */
    if (item.kind === "tool") {
      menuAction("创建桌面快捷方式", "", function () {
        closeItemMenu();
        host.callManager("manager.createToolShortcut", { toolId: item.id }).then(function (r) {
          toast("已创建桌面快捷方式：" + ((r && r.fileName) || item.name));
        }, function (e) {
          toast("创建失败：" + errText(e), true);
        });
      }, "shortcut");
    }

    menuAction(item.kind === "app" ? "移除" : item.kind === "program" ? "隐藏" : "删除", "danger", function () {
      closeItemMenu();
      if (item.kind === "app") { deleteApp(item); }
      else if (item.kind === "web") { deleteWebTool(item); }
      else if (item.kind === "program") { hideProgram(item); }
      else { deleteTool(item); }
    }, item.kind === "app" ? "minus" : item.kind === "program" ? "eyeOff" : "trash");

    var menuEl = document.createElement("div");
    menuEl.className = "nav-menu";
    menuEl.hidden = true;
    /* 在展开的菜单上再右键 = 收起还原，不弹浏览器原生菜单 */
    menuEl.addEventListener("contextmenu", function (e) {
      e.preventDefault();
      closeItemMenu();
    });
    menuEl.appendChild(actionsEl);
    menuEl.appendChild(detailEl);
    if (renameEl) { menuEl.appendChild(renameEl); }

    /* 同一条目可能留有上次收起的旧菜单：先清掉，保证每次右键都是新菜单 */
    for (var i = li.children.length - 1; i >= 0; i--) {
      if (li.children[i].hasClass && li.children[i].hasClass("nav-menu")) {
        li.children.splice(i, 1);
      }
    }

    li.appendChild(menuEl);
    openMenuState = { key: itemMenuKey(item), item: item, li: li, btn: btn, menuEl: menuEl, actionsEl: actionsEl, detailEl: detailEl };
    menuEl.hidden = false;
    placeItemMenu(menuEl, btn, event);
  }

  /** 条目详情（原位展开）：名称/类型/ID/版本/类别/目标或网址/使用次数/状态。 */
  function buildItemDetail(item) {
    var detailEl = document.createElement("div");
    detailEl.className = "menu-detail";

    var status = item.kind === "web"
      ? (item.isValid ? "正常" : "配置无效")
      : item.kind === "tool"
        ? (item.isValid ? "正常" : "manifest 无效")
        : (item.targetExists ? "正常" : "目标不存在");

    var rows = [["名称", item.name], ["类型", kindLabel(item.kind)]];
    if (item.description) { rows.push(["描述", item.description]); }
    rows.push(["ID", item.id]);
    if ((item.kind === "tool" || item.kind === "web") && item.version) { rows.push(["版本", "v" + item.version]); }
    if (item.category) { rows.push(["类别", item.category]); }
    if (item.target) { rows.push([item.kind === "web" ? "网址" : "目标", item.target]); }
    rows.push(["使用次数", (item.useCount || 0) + " 次"], ["状态", status]);

    rows.forEach(function (row) {
      var kv = document.createElement("div");
      kv.className = "menu-kv";
      var k = document.createElement("span");
      k.textContent = row[0];
      var v = document.createElement("b");
      v.textContent = row[1];
      kv.appendChild(k);
      kv.appendChild(v);
      detailEl.appendChild(kv);
    });

    return detailEl;
  }

  /** 右键菜单「属性」：在系统文件管理器中定位条目（成功后菜单保持收起）。 */
  function revealItem(item) {
    if (!host) { toast("桥接不可用", true); return; }
    closeItemMenu();
    host.callManager("manager.revealItem", { kind: item.kind, id: item.id }).then(function () {
      /* 文件管理器已在前台打开，无需提示 */
    }, function (e) { toast("定位失败：" + errText(e), true); });
  }

  /* ================= 在线工具：独立窗口在线打开 / 移除 ================= */
  function openWebTool(item) {
    if (!host) { return; }
    host.callManager("manager.openWebTool", { webId: item.id }).then(function () {
      refreshLauncher();
    }, function (e) { toast("打开失败：" + errText(e), true); });
  }

  function deleteWebTool(item) {
    if (!confirm("确定删除在线工具「" + item.name + "」？\n（只删除本条网址，不影响目标网站）")) { return; }
    host.callManager("manager.deleteWebTool", { webId: item.id }).then(function () {
      toast("已删除 " + item.name);
      refreshLauncher();
    }, function (e) { toast("删除失败：" + errText(e), true); });
  }

  /* ================= 添加在线工具（录入网址） ================= */
  function showWebView() {
    $("web-status").textContent = "";
    ["web-name", "web-url", "web-desc", "web-category", "web-glyph"].forEach(function (id) {
      var el = $(id);
      if (el) { el.value = ""; }
    });
    showView("web-view");
    $("web-name").focus();
  }

  function submitWebTool() {
    if (!host) { toast("桥接不可用", true); return; }
    var name = $("web-name").value.trim();
    var url = $("web-url").value.trim();
    if (!name) { toast("请填写名称", true); $("web-name").focus(); return; }
    if (!url) { toast("请填写网址", true); $("web-url").focus(); return; }
    if (!/^https?:\/\//i.test(url)) { url = "https://" + url; }

    $("web-status").textContent = "保存中…";
    host.callManager("manager.addWebTool", {
      name: name,
      url: url,
      description: $("web-desc").value.trim() || null,
      category: $("web-category").value.trim() || null,
      glyph: $("web-glyph").value.trim() || null
    }).then(function () {
      toast("已添加在线工具「" + name + "」");
      if (isContentMode) { closeContent(); }
      else { showView("content-home"); }
      refreshLauncher();
    }, function (e) {
      $("web-status").textContent = "";
      toast("保存失败：" + errText(e), true);
    });
  }

  /* ================= 工具源：目录 / 安装更新 / 提交 ================= */
  function sourceBytes(bytes) {
    var value = Number(bytes) || 0;
    if (value < 1024) { return value + " B"; }
    if (value < 1024 * 1024) { return (value / 1024).toFixed(1) + " KiB"; }
    return (value / (1024 * 1024)).toFixed(1) + " MiB";
  }

  function sourcePermissions(permissions) {
    var names = { storage: "存储", ai: "AI", media: "媒体", files: "文件" };
    return (permissions || []).map(function (name) { return names[name] || name; }).join(" / ") || "无额外权限";
  }

  function sourceEmpty(kind) {
    var content = kind === "unconfigured"
      ? { icon: "⌁", title: "还没有配置工具源", text: "填写源地址后即可浏览、直接安装和提交小工具。", action: "settings", label: "打开源地址设置" }
      : kind === "error"
        ? { icon: "!", title: "目录读取失败", text: "检查源地址和网络连接后重试。", action: "refresh", label: "重新加载" }
        : kind === "empty"
          ? { icon: "◇", title: "工具源还没有公开工具", text: "审核通过的工具会显示在这里。", action: "refresh", label: "刷新目录" }
          : { icon: "⌕", title: "没有找到匹配的工具", text: "换个关键词或分类试试。", action: "clear", label: "清除筛选" };
    return '<div class="source-empty"><span class="source-empty-icon">' + esc(content.icon) + "</span>" +
      "<h3>" + esc(content.title) + "</h3><p>" + esc(content.text) + "</p>" +
      '<button class="btn ghost sm" type="button" data-source-action="' + content.action + '">' + esc(content.label) + "</button></div>";
  }

  function sourceLoading() {
    return new Array(4).fill(
      '<div class="source-skeleton"><div class="source-skeleton-top"><span></span><i></i></div><b></b><b></b><b class="wide"></b></div>'
    ).join("");
  }

  function applySourceEntry(config) {
    sourceState.config = config || null;
    var button = $("source-tools");
    if (button) { button.hidden = !(config && config.baseUrl); }
  }

  function refreshSourceEntry() {
    if (!host) { return; }
    host.callManager("manager.getSourceConfig", {}).then(applySourceEntry, function () {
      applySourceEntry(null);
    });
  }

  /** 连接状态：state 来自实际目录请求（pending/online/offline），缺省时使用配置里的探测结果。 */
  function renderSourceConnection(config, state) {
    var text = $("source-connection");
    if (!text) { return; }
    if (!config || !config.baseUrl) {
      text.dataset.state = "empty";
      text.textContent = "尚未配置源地址";
      return;
    }
    if (state === "pending") {
      text.dataset.state = "pending";
      text.textContent = config.baseUrl + " · 正在连接…";
      return;
    }
    var online = state === "online" || (state !== "offline" && !!config.connected);
    text.dataset.state = online ? "online" : "offline";
    text.textContent = config.baseUrl + (online
      ? " · 已连接" + (config.serviceVersion ? " · 服务 v" + config.serviceVersion : "")
      : " · 连接失败");
  }

  /** 本地可提交到源站的条目：小工具 + 在线工具（添加网址）。 */
  function sourceLocalEntries() {
    var entries = allTools.map(function (tool) {
      return { kind: "tool", id: tool.id, name: tool.name, version: tool.version };
    });
    allItems.forEach(function (item) {
      if (item.kind === "web") {
        entries.push({ kind: "web", id: item.id, name: item.name, version: item.version || "1.0.0" });
      }
    });
    return entries.sort(function (a, b) {
      if (a.kind !== b.kind) { return a.kind === "tool" ? -1 : 1; }
      return a.name.localeCompare(b.name, "zh-CN");
    });
  }

  function localSourceItem(kind, id) {
    if (kind === "web") {
      return allItems.filter(function (item) { return item.kind === "web" && item.id === id; })[0] || null;
    }
    return allTools.filter(function (tool) { return tool.id === id; })[0] || null;
  }

  function sourceLocalValue(entry) { return entry.kind + ":" + entry.id; }

  /** 右侧提交卡的选中条目预览：类型、名称、版本 / 网址。 */
  function updateSourcePickPreview() {
    var select = $("source-local-tools");
    var box = $("source-pick-preview");
    if (!select || !box) { return; }
    var value = select.value || "";
    var split = value.indexOf(":");
    var local = split > 0 ? localSourceItem(value.slice(0, split), value.slice(split + 1)) : null;
    if (!local) { box.hidden = true; return; }
    var isWeb = value.slice(0, split) === "web";
    var kindEl = $("source-pick-kind");
    kindEl.textContent = isWeb ? "在线工具" : "小工具";
    kindEl.dataset.kind = isWeb ? "web" : "tool";
    $("source-pick-name").textContent = local.name;
    $("source-pick-meta").textContent = "v" + (local.version || "1.0.0") +
      (isWeb ? (local.target ? " · " + local.target : "") : (local.category ? " · " + local.category : ""));
    box.hidden = false;
  }

  function refreshSourceLocalTools() {
    var select = $("source-local-tools");
    if (!select) { return; }
    var selected = select.value;
    var entries = sourceLocalEntries();
    var tools = entries.filter(function (entry) { return entry.kind === "tool"; });
    var webs = entries.filter(function (entry) { return entry.kind === "web"; });
    function options(list) {
      return list.map(function (entry) {
        return '<option value="' + esc(sourceLocalValue(entry)) + '">' + esc(entry.name) +
          (entry.kind === "web" ? " · 在线" : " · v" + esc(entry.version)) + "</option>";
      }).join("");
    }
    select.innerHTML = entries.length
      ? (tools.length ? '<optgroup label="小工具">' + options(tools) + "</optgroup>" : "") +
        (webs.length ? '<optgroup label="在线工具">' + options(webs) + "</optgroup>" : "")
      : '<option value="">暂无可提交条目</option>';
    if (entries.some(function (entry) { return sourceLocalValue(entry) === selected; })) { select.value = selected; }
    $("source-submit-local").disabled = entries.length === 0;
    var count = $("source-local-count");
    if (count) { count.textContent = entries.length + " 个"; }
    updateSourcePickPreview();
  }

  function updateSourceDetailControls() {
    var busy = sourceState.loadingDetail || sourceState.installing || sourceState.downloading;
    $("source-detail-back").disabled = busy;
    $("source-version").disabled = busy;
    $("source-detail-preview").disabled = busy;
    $("source-detail-download").disabled = busy;
    $("source-detail-install").disabled = busy;
    $("source-detail-submit").disabled = busy;
  }

  function resetSourceDetail() {
    sourceState.detailSequence += 1;
    sourceState.detail = null;
    sourceState.version = "";
    sourceState.availableVersions = [];
    sourceState.review = null;
    sourceState.loadingDetail = false;
    updateSourceDetailControls();
  }

  function isCurrentSourceRequest(sequence, id, version) {
    return sourceState.detailSequence === sequence && sourceState.detail &&
      sourceState.detail.tool.id === id && sourceState.version === version;
  }

  function showSourceView() {
    if (!host) { toast("桥接不可用", true); return; }
    resetSourceDetail();
    $("source-detail").hidden = true;
    $("source-catalog").hidden = false;
    $("source-grid").hidden = false;
    $("source-submit-status").textContent = "";
    showView("source-view");
    function start() {
      refreshSourceLocalTools();
      host.callManager("manager.getSourceConfig", {}).then(function (config) {
        applySourceEntry(config);
        var configured = !!(config && config.baseUrl);
        renderSourceConnection(config, configured ? "pending" : undefined);
        $("source-toolbar").hidden = !configured;
        $("source-results-meta").hidden = !configured;
        if (configured) { loadSourceCatalog(); }
        else {
          $("source-grid").innerHTML = sourceEmpty("unconfigured");
          $("source-page").textContent = "尚未配置源地址";
          $("source-pagination").hidden = true;
        }
      }, function (error) { toast("读取工具源配置失败：" + errText(error), true); });
      refreshTools().then(refreshSourceLocalTools, refreshSourceLocalTools);
    }
    start();
    /* 内容窗口首次进入时还没有统一列表（在线工具、已安装徽标依赖它） */
    if (isContentMode) {
      refreshLauncher().then(function () {
        refreshSourceLocalTools();
        if (sourceState.catalog) { renderSourceCatalog(sourceState.catalog); }
      }, function () { });
    }
  }

  function loadSourceCatalog() {
    if (!host || sourceState.loading) { return Promise.resolve(); }
    sourceState.loading = true;
    $("source-refresh").disabled = true;
    $("source-grid").innerHTML = sourceLoading();
    $("source-page").textContent = "正在读取工具源…";
    $("source-pagination").hidden = true;
    renderSourceConnection(sourceState.config, "pending");
    return host.callManager("manager.sourceCatalog", {
      q: sourceState.query,
      category: sourceState.category,
      page: sourceState.page,
      pageSize: 24
    }).then(function (catalog) {
      sourceState.catalog = catalog;
      sourceState.categories = catalog.categories || [];
      renderSourceCatalog(catalog);
      renderSourceConnection(sourceState.config, "online");
    }, function (error) {
      $("source-grid").innerHTML = sourceEmpty("error");
      $("source-page").textContent = "读取失败";
      renderSourceConnection(sourceState.config, "offline");
      toast("读取工具源失败：" + errText(error), true);
    }).finally(function () {
      sourceState.loading = false;
      $("source-refresh").disabled = false;
    });
  }

  function renderSourceCatalog(catalog) {
    var category = $("source-category");
    var selected = sourceState.category;
    category.innerHTML = '<option value="">全部分类</option>' + (catalog.categories || []).map(function (name) {
      return '<option value="' + esc(name) + '">' + esc(name) + "</option>";
    }).join("");
    category.value = selected;
    var grid = $("source-grid");
    grid.innerHTML = "";
    if (!catalog.items || !catalog.items.length) {
      grid.innerHTML = sourceEmpty(sourceState.query || sourceState.category ? "no-match" : "empty");
    } else {
      catalog.items.forEach(function (tool) {
        var isWeb = tool.kind === "web";
        var installed = localSourceItem(tool.kind || "tool", tool.id);
        var card = document.createElement("article");
        card.className = "source-card";
        card.innerHTML = '<div class="source-card-top"><div class="source-card-icon">' + esc(tool.glyph || firstGlyph(tool.name)) + '</div><div class="source-card-title"><div class="source-card-tags"><span class="badge">' + esc(tool.category || "未分类") + '</span><span class="badge ver">v' + esc(tool.version) + '</span>' +
          (isWeb ? '<span class="badge web">在线工具</span>' : "") +
          (installed ? '<span class="badge source-installed">已安装' + (installed.version ? " v" + esc(installed.version) : "") + "</span>" : "") + '</div><h3>' + esc(tool.name) + "</h3></div></div>" +
          '<p class="source-card-desc">' + esc(tool.description || "暂无描述") + "</p>" +
          '<div class="source-card-meta"><span>' + esc(isWeb ? "网址直达" : sourceBytes(tool.sizeBytes)) + "</span><span>" + esc(sourcePermissions(tool.permissions)) + "</span></div>" +
          '<div class="source-card-actions"><button class="btn primary sm" type="button" data-source-action="detail" data-source-id="' + esc(tool.id) + '">查看 / 安装</button><button class="btn ghost sm" type="button" data-source-action="preview" data-source-id="' + esc(tool.id) + '" data-source-version="' + esc(tool.version) + '">' + (isWeb ? "在线打开" : "沙箱预览") + "</button></div>";
        grid.appendChild(card);
      });
    }
    var pages = Math.max(1, Math.ceil(catalog.total / catalog.pageSize));
    $("source-page").textContent = "第 " + catalog.page + " / " + pages + " 页 · 共 " + catalog.total + " 个工具";
    $("source-prev").disabled = catalog.page <= 1;
    $("source-next").disabled = catalog.page >= pages;
    $("source-pagination").hidden = pages <= 1;
  }

  function sourceAction(target) {
    var node = target;
    while (node && node !== document) {
      if (node.dataset && node.dataset.sourceAction) { return node; }
      node = node.parentNode;
    }
    return null;
  }

  function previewSourceTool(id, version) {
    if (!host || sourceState.installing || sourceState.downloading || sourceState.loadingDetail) { return; }
    host.callManager("manager.sourcePreview", { id: id, version: version || "" }).then(function () {
      toast("已在隔离预览窗口打开");
    }, function (error) { toast("预览失败：" + errText(error), true); });
  }

  function showSourceDetail(tool) {
    if (!host || sourceState.installing || sourceState.downloading || sourceState.loadingDetail) { return; }
    var sequence = ++sourceState.detailSequence;
    sourceState.detail = null;
    sourceState.review = null;
    sourceState.loadingDetail = true;
    updateSourceDetailControls();
    $("source-detail-status").textContent = "读取详情…";
    host.callManager("manager.sourceTool", { id: tool.id, version: tool.version || "" }).then(function (detail) {
      if (sourceState.detailSequence !== sequence) { return; }
      sourceState.detail = detail;
      sourceState.version = tool.version || detail.tool.version;
      sourceState.availableVersions = detail.tool.versions && detail.tool.versions.length ? detail.tool.versions.slice() : [detail.tool.version];
      sourceState.loadingDetail = false;
      renderSourceDetail(detail);
    }, function (error) {
      if (sourceState.detailSequence !== sequence) { return; }
      sourceState.loadingDetail = false;
      updateSourceDetailControls();
      $("source-detail-status").textContent = "";
      toast("读取工具详情失败：" + errText(error), true);
    });
  }

  function renderSourceDetail(detail) {
    var tool = detail.tool;
    var isWeb = tool.kind === "web";
    var versions = sourceState.availableVersions.length ? sourceState.availableVersions.slice() : [tool.version];
    if (versions.indexOf(sourceState.version) < 0) { versions = [sourceState.version]; }
    $("source-detail-glyph").textContent = tool.glyph || firstGlyph(tool.name);
    $("source-detail-title").textContent = tool.name;
    $("source-detail-description").textContent = tool.description || "暂无描述";
    $("source-detail-version-badge").textContent = "v" + sourceState.version;
    var rows = [
      [isWeb ? "在线工具 ID" : "工具 ID", tool.id], ["版本", sourceState.version], ["分类", tool.category || "未分类"],
      ["权限", sourcePermissions(tool.permissions)]
    ];
    if (isWeb && tool.url) { rows.push(["网址", tool.url]); }
    rows.push(["安装包", sourceBytes(tool.sizeBytes)], ["SHA256", tool.sha256]);
    $("source-detail-meta").innerHTML = rows.map(function (row) {
      var cls = row[0] === "SHA256" ? ' class="mono"' : "";
      return "<div><dt>" + esc(row[0]) + "</dt><dd" + cls + ">" + esc(row[1]) + "</dd></div>";
    }).join("");
    $("source-version").innerHTML = versions.map(function (version) {
      return '<option value="' + esc(version) + '">v' + esc(version) + "</option>";
    }).join("");
    $("source-version").value = sourceState.version;
    $("source-review").hidden = true;
    $("source-accept").checked = false;
    $("source-detail-preview").textContent = isWeb ? "在线打开" : "沙箱预览";
    $("source-detail-install").textContent = isWeb ? "保存到我的在线工具" : "直接安装 / 更新";
    $("source-detail-status").textContent = "";
    $("source-detail-submit").hidden = !localSourceItem(tool.kind || "tool", tool.id);
    $("source-grid").hidden = true;
    $("source-catalog").hidden = true;
    $("source-detail").hidden = false;
    updateSourceDetailControls();
  }

  function changeSourceVersion() {
    if (!sourceState.detail || !host || sourceState.loadingDetail || sourceState.installing || sourceState.downloading) { return; }
    var sequence = ++sourceState.detailSequence;
    var id = sourceState.detail.tool.id;
    var version = $("source-version").value;
    sourceState.review = null;
    sourceState.loadingDetail = true;
    updateSourceDetailControls();
    $("source-detail-status").textContent = "读取版本…";
    host.callManager("manager.sourceTool", { id: id, version: version }).then(function (detail) {
      if (sourceState.detailSequence !== sequence || !sourceState.detail || sourceState.detail.tool.id !== id) { return; }
      sourceState.detail = detail;
      sourceState.version = version;
      sourceState.loadingDetail = false;
      renderSourceDetail(detail);
    }, function (error) {
      if (sourceState.detailSequence !== sequence || !sourceState.detail || sourceState.detail.tool.id !== id) { return; }
      sourceState.loadingDetail = false;
      $("source-version").value = sourceState.version;
      updateSourceDetailControls();
      $("source-detail-status").textContent = "";
      toast("读取版本失败：" + errText(error), true);
    });
  }

  function renderSourceReview(result, id, version) {
    renderFindingsInto($("source-findings"), result.findings);
    var assets = $("source-external-assets");
    assets.hidden = !result.externalAssets || !result.externalAssets.length;
    assets.textContent = result.externalAssets && result.externalAssets.length
      ? "引用了工具目录外的相对资源：" + result.externalAssets.join("、") : "";
    sourceState.review = { id: id, version: version, sha256: result.packageSha256 || "" };
    $("source-accept").checked = false;
    $("source-review").hidden = false;
    $("source-detail-install").textContent = "确认风险并安装";
    $("source-detail-status").textContent = "检测到高危项，请审查后勾选确认。";
  }

  function installSourceDetail() {
    if (!host || !sourceState.detail || sourceState.loadingDetail || sourceState.installing || sourceState.downloading) { return; }
    var sequence = sourceState.detailSequence;
    var detail = sourceState.detail;
    var id = detail.tool.id;
    var version = sourceState.version;
    var review = sourceState.review;
    if (review && review.sha256 !== (detail.tool.sha256 || "")) {
      sourceState.review = null;
      $("source-accept").checked = false;
      $("source-review").hidden = true;
    }
    var accepted = !!(sourceState.review && sourceState.review.id === id &&
      sourceState.review.version === version && sourceState.review.sha256 === detail.tool.sha256 &&
      $("source-accept").checked);
    if (!$("source-review").hidden && !accepted) {
      toast("请先勾选已审查风险", true);
      return;
    }
    var installSequence = ++sourceState.installSequence;
    sourceState.installing = true;
    updateSourceDetailControls();
    $("source-detail-status").textContent = "下载、校验并执行本地安全审查…";
    host.callManager("manager.sourceInstall", {
      id: id,
      version: version,
      acceptHighSeverity: accepted,
      reviewedSha256: accepted ? sourceState.review.sha256 : ""
    }).then(function (result) {
      if (!isCurrentSourceRequest(sequence, id, version)) { return; }
      if (result.requiresExplicitAcceptance) {
        renderSourceReview(result, id, version);
        return;
      }
      var isWeb = detail.tool.kind === "web";
      if (result.action === "unchanged") {
        toast("本地已是相同或更高版本，未覆盖");
        $("source-detail-status").textContent = "本地版本无需更新";
      } else {
        toast((result.action === "updated" ? "已更新 " : (isWeb ? "已添加在线工具 " : "已安装 ")) +
          result.toolName + " v" + result.version);
        $("source-detail-status").textContent = isWeb
          ? "已保存到在线工具列表，可从主窗口直接打开"
          : result.action === "updated" ? "更新完成，用户数据与历史版本已保留" : "安装完成";
      }
      var refreshed = refreshTools();
      if (isWeb) { refreshed = refreshed.then(refreshLauncher); }
      refreshed.then(function () {
        if (!isCurrentSourceRequest(sequence, id, version)) { return; }
        refreshSourceLocalTools();
        $("source-detail-submit").hidden = !localSourceItem(detail.tool.kind || "tool", result.toolId);
      }, function () { });
      loadSourceCatalog();
    }, function (error) {
      if (!isCurrentSourceRequest(sequence, id, version)) { return; }
      $("source-detail-status").textContent = "";
      toast("安装失败：" + errText(error), true);
    }).finally(function () {
      if (sourceState.installSequence !== installSequence) { return; }
      sourceState.installing = false;
      updateSourceDetailControls();
    });
  }

  function downloadSourceDetail() {
    if (!host || !sourceState.detail || sourceState.loadingDetail || sourceState.installing || sourceState.downloading) { return; }
    var sequence = sourceState.detailSequence;
    var detail = sourceState.detail;
    var id = detail.tool.id;
    var version = sourceState.version;
    var downloadSequence = ++sourceState.downloadSequence;
    sourceState.downloading = true;
    updateSourceDetailControls();
    $("source-detail-status").textContent = "正在准备下载，请在保存对话框中选择位置…";
    host.callManager("manager.sourceDownload", {
      id: id,
      version: version
    }).then(function (result) {
      if (!isCurrentSourceRequest(sequence, id, version)) { return; }
      if (result.path) {
        toast("已保存安装包：" + (result.fileName || result.path));
        $("source-detail-status").textContent = "安装包已保存，可离线分享或日后导入";
      } else {
        $("source-detail-status").textContent = "已取消保存";
      }
    }, function (error) {
      if (!isCurrentSourceRequest(sequence, id, version)) { return; }
      $("source-detail-status").textContent = "";
      toast("下载失败：" + errText(error), true);
    }).finally(function () {
      if (sourceState.downloadSequence !== downloadSequence) { return; }
      sourceState.downloading = false;
      updateSourceDetailControls();
    });
  }

  function submitSourceLocal(kind, id) {
    if (!host || !id) { return; }
    var button = $("source-submit-local");
    var status = $("source-submit-status");
    button.disabled = true;
    status.className = "source-side-status";
    status.textContent = kind === "web" ? "正在导出在线工具并上传…" : "正在导出并上传…";
    host.callManager("manager.sourceSubmit", { kind: kind, id: id }).then(function (result) {
      status.className = "source-side-status ok";
      status.textContent = "已提交 " + (result.toolName || id) + " · 审核编号 " + result.id;
      toast("已提交工具源审核");
    }, function (error) {
      status.className = "source-side-status error";
      status.textContent = "提交失败：" + errText(error);
      toast("提交失败：" + errText(error), true);
    }).finally(function () { button.disabled = !$("source-local-tools").value; });
  }

  /** 右键「上传到工具源」：确认后直接提交当前运行版本。 */
  function submitItemToSource(item) {
    if (!host) { return; }
    var kind = item.kind === "web" ? "web" : "tool";
    var version = item.version || "1.0.0";
    var what = kind === "web" ? "在线工具" : "小工具";
    if (!confirm("把" + what + "「" + item.name + "」v" + version + " 上传到工具源？\n提交后需管理员审核通过才会公开。")) { return; }
    toast("正在提交到工具源…");
    host.callManager("manager.sourceSubmit", { kind: kind, id: item.id }).then(function (result) {
      toast("已提交审核，编号 " + (result.id || "—"));
      if ($("source-view") && !$("source-view").hidden) { loadSourceCatalog(); }
    }, function (e) { toast("提交失败：" + errText(e), true); });
  }

  /* ================= 版本管理 ================= */
  function showVersionView(tool) {
    cancelVisibleStream("update");
    currentVersionTool = tool;
    updateSequence += 1;
    pendingUpdate = null;
    $("update-accept").checked = false;
    $("update-generate").disabled = false;
    $("version-title").textContent = "版本管理 · " + tool.name;
    $("version-subtitle").textContent =
      "每个工具都可以选择运行哪个版本；AI 更新前会自动备份当前版本，随时可以回滚。";
    $("update-form").hidden = true;
    $("update-result").hidden = true;
    $("update-stream").hidden = true;
    $("version-status").textContent = "";
    showView("version-view");
    loadVersions();
  }

  function loadVersions() {
    if (!host || !currentVersionTool) { return; }
    var tool = currentVersionTool;
    $("version-status").textContent = "加载中…";
    host.callManager("manager.listVersions", { toolId: tool.id }).then(function (r) {
      if (currentVersionTool !== tool) { return; }
      $("version-status").textContent = "";
      renderVersions(r);
    }, function (e) {
      $("version-status").textContent = "";
      toast("读取版本失败：" + errText(e), true);
    });
  }

  function renderVersions(r) {
    var ul = $("version-list");
    ul.innerHTML = "";
    var versions = (r && r.versions) || [];

    if (versions.length === 0) {
      var empty = document.createElement("li");
      empty.innerHTML = '<span class="v-meta">暂无历史版本</span>';
      ul.appendChild(empty);
      return;
    }

    versions.forEach(function (v) {
      var li = document.createElement("li");
      if (v.isActive) { li.className = "active"; }

      var num = document.createElement("span");
      num.className = "v-num";
      num.textContent = "v" + v.version + (v.isActive ? "（运行中）" : "");

      var meta = document.createElement("span");
      meta.className = "v-meta";
      meta.textContent = (v.sizeBytes / 1024).toFixed(1) + " KB · " +
        new Date(v.createdAt).toLocaleString() + (v.note ? " · " + v.note : "");

      var actions = document.createElement("div");
      actions.className = "v-actions";

      if (!v.isActive) {
        var rollback = document.createElement("button");
        rollback.type = "button";
        rollback.className = "btn ghost sm";
        rollback.textContent = "回滚到此版本";
        rollback.addEventListener("click", function () { switchVersion(v.version); });
        actions.appendChild(rollback);

        var del = document.createElement("button");
        del.type = "button";
        del.className = "btn ghost sm danger";
        del.textContent = "删除";
        del.addEventListener("click", function () { deleteVersion(v.version); });
        actions.appendChild(del);
      }

      li.appendChild(num);
      li.appendChild(meta);
      li.appendChild(actions);
      ul.appendChild(li);
    });
  }

  function switchVersion(version) {
    if (!host || !currentVersionTool) { return; }
    if (!confirm("回滚到 v" + version + "？\n当前版本会先自动备份，之后仍可切回。")) { return; }
    $("version-status").textContent = "切换中…";
    host.callManager("manager.switchVersion", { toolId: currentVersionTool.id, version: version })
      .then(function () {
        toast("已切换到 v" + version);
        loadVersions();
        refreshTools();
      }, function (e) {
        $("version-status").textContent = "";
        toast("切换失败：" + errText(e), true);
      });
  }

  function deleteVersion(version) {
    if (!host || !currentVersionTool) { return; }
    if (!confirm("确定删除版本 v" + version + "？此操作不可恢复。")) { return; }
    host.callManager("manager.deleteVersion", { toolId: currentVersionTool.id, version: version })
      .then(function () { toast("已删除 v" + version); loadVersions(); },
        function (e) { toast("删除失败：" + errText(e), true); });
  }

  /* ---------- AI 优化更新 ---------- */
  on("version-update", "click", function () {
    $("update-form").hidden = false;
    $("update-result").hidden = true;
    $("update-instruction").focus();
  });
  on("update-cancel", "click", function () {
    cancelVisibleStream("update");
    $("update-form").hidden = true;
  });

  on("update-generate", "click", function () {
    if (!host || !currentVersionTool) { return; }
    var instruction = $("update-instruction").value.trim();
    if (!instruction) { toast("请描述想怎么改", true); return; }

    var requestNumber = ++updateSequence;
    pendingUpdate = null;
    $("update-accept").checked = false;
    $("update-result").hidden = true;
    $("version-status").textContent = "AI 生成中（下方实时输出）…";
    $("update-generate").disabled = true;
    var stream = beginStream("update");

    host.callManagerStream("manager.generateUpdate", {
      toolId: currentVersionTool.id,
      instruction: instruction,
      versionBump: $("update-bump").value
    }, stream.onDelta, stream.control).then(function (view) {
      if (requestNumber !== updateSequence) { return; }
      stream.finish(false);
      pendingUpdate = view;
      $("version-status").textContent = "生成完成，请审查";
      renderFindingsInto($("update-findings"), view.findings);
      $("update-manifest").textContent = JSON.stringify(view.manifest, null, 2);
      $("update-html").textContent = view.indexHtml;
      $("update-result").hidden = false;
      $("update-apply").disabled = view.hasFatal;
      if (view.hasFatal) { toast("生成内容存在致命问题，不能应用", true); }
      else if (view.requiresExplicitAcceptance) { toast("包含高危项，需勾选确认", true); }
      else { toast("审查通过"); }
    }, function (e) {
      if (requestNumber !== updateSequence) { return; }
      stream.finish(true);
      if (isCancelled(e)) {
        $("version-status").textContent = "已取消生成";
      } else {
        $("version-status").textContent = "生成失败：" + errText(e);
        toast("生成失败：" + errText(e), true);
      }
    }).then(function () {
      if (requestNumber === updateSequence) { $("update-generate").disabled = false; }
    });
  });

  on("update-apply", "click", function () {
    if (!host || !currentVersionTool || !pendingUpdate) { return; }
    if (pendingUpdate.hasFatal) { toast("存在致命问题，不能应用", true); return; }
    var accept = $("update-accept").checked;
    if (pendingUpdate.requiresExplicitAcceptance && !accept) { toast("请先勾选确认", true); return; }

    var generated = pendingUpdate;
    var requestNumber = updateSequence;
    $("version-status").textContent = "应用中…";
    $("update-apply").disabled = true;
    host.callManager("manager.applyUpdate", {
      toolId: currentVersionTool.id,
      generated: generated,
      acceptHighSeverity: accept
    }).then(function () {
      if (requestNumber !== updateSequence) { refreshTools(); return; }
      toast("已应用新版本 " + (generated.manifest && generated.manifest.version));
      $("update-form").hidden = true;
      $("update-result").hidden = true;
      pendingUpdate = null;
      loadVersions();
      refreshTools();
    }, function (e) {
      if (requestNumber !== updateSequence) { return; }
      $("version-status").textContent = "";
      $("update-apply").disabled = false;
      toast("应用失败：" + errText(e), true);
    });
  });

  on("version-close", "click", function () {
    cancelVisibleStream("update");
    currentVersionTool = null;
    updateSequence += 1;
    pendingUpdate = null;
    /* 从哪来回哪去：内嵌中的工具回内嵌视图，版本管理直达则收起管理窗口，否则回首页 */
    if (currentTool) { showView("tool-view"); }
    else if (isContentMode && contentEntryView === "version") { closeContent(); }
    else { showView("content-home"); }
  });

  function renderFindingsInto(ul, findings) {
    ul.innerHTML = "";
    if (!findings || findings.length === 0) {
      var li = document.createElement("li");
      li.className = "ok";
      li.textContent = "未发现安全问题";
      ul.appendChild(li);
      return;
    }
    findings.forEach(function (f) {
      var item = document.createElement("li");
      item.className = "sev-" + f.severity;
      item.textContent = "[" + severityLabel(f.severity) + "] " + f.code + "：" + f.message;
      ul.appendChild(item);
    });
  }

  /* ================= AI 流式输出预览 ================= */
  /** AI 原始输出是 JSON 源码，换行/引号/中文都可能是转义序列；
   *  预览区只做显示层还原（\n → 换行、\uXXXX → 字符），不影响最终解析结果。
   *  不完整的尾部转义保持原样，下一段增量到达后整体重算。 */
  function prettyStreamText(raw) {
    var out = "";
    for (var i = 0; i < raw.length; i++) {
      var ch = raw.charAt(i);
      if (ch !== "\\" || i + 1 >= raw.length) { out += ch; continue; }
      var next = raw.charAt(i + 1);
      if (next === "n") { out += "\n"; i += 1; }
      else if (next === "r") { i += 1; }
      else if (next === "t") { out += "  "; i += 1; }
      else if (next === "u" && i + 5 < raw.length && /^[0-9a-fA-F]{4}$/.test(raw.slice(i + 2, i + 6))) {
        out += String.fromCharCode(parseInt(raw.slice(i + 2, i + 6), 16));
        i += 5;
      } else if (next === '"' || next === "\\" || next === "/") { out += next; i += 1; }
      else { out += ch; }
    }
    return out;
  }

  /** 打开流式预览区，返回 { control, onDelta, finish }。
   *  control.requestId 由 bridge.js 同步填入，取消按钮据此调用 manager.cancelRequest。 */
  function beginStream(prefix) {
    var box = $(prefix + "-stream");
    var text = $(prefix + "-stream-text");
    var reasoning = $(prefix + "-stream-reasoning");
    var reasoningText = $(prefix + "-stream-reasoning-text");
    var cancelBtn = $(prefix + "-stream-cancel");
    var state = { control: {}, rawOutput: "" };
    if (text) { text.textContent = ""; }
    if (reasoningText) { reasoningText.textContent = ""; }
    if (reasoning) { reasoning.hidden = true; }
    if (box) { box.hidden = false; }
    if (cancelBtn) {
      cancelBtn.hidden = false;
      cancelBtn.disabled = false;
      cancelBtn.textContent = "取消生成";
      cancelBtn.onclick = function () {
        if (!host || !state.control.requestId) { return; }
        cancelBtn.disabled = true;
        cancelBtn.textContent = "取消中…";
        host.cancelRequest(state.control.requestId).catch(function () { /* 取消失败时原请求继续 */ });
      };
    }
    state.onDelta = function (delta, isReasoning) {
      if (isReasoning) {
        if (reasoning) { reasoning.hidden = false; }
        if (reasoningText) {
          reasoningText.textContent += delta;
          reasoningText.scrollTop = reasoningText.scrollHeight;
        }
        return;
      }
      if (text) {
        state.rawOutput += delta;
        text.textContent = prettyStreamText(state.rawOutput);
        text.scrollTop = text.scrollHeight;
      }
    };
    /** keepVisible=true 时保留已输出的内容（失败/取消后供查看），仅收起取消按钮。 */
    state.finish = function (keepVisible) {
      if (cancelBtn) { cancelBtn.onclick = null; cancelBtn.hidden = true; }
      if (box && !keepVisible) { box.hidden = true; }
    };
    return state;
  }

  function isCancelled(e) {
    return !!(e && (e.code === "REQUEST_CANCELLED" || e.code === "AI_CANCELLED"));
  }

  /** 离开页面/重开表单时，顺带取消仍在进行的生成，避免后台空耗 AI 请求。 */
  function cancelVisibleStream(prefix) {
    var btn = $(prefix + "-stream-cancel");
    if (btn && !btn.hidden && !btn.disabled) { btn.click(); }
  }

  /* ================= 导入（HTML / .qtools 导出包，原生对话框自动识别） ================= */
  function setImportBusy(value) {
    importing = value;
    ["import-pick", "import-run", "import-confirm"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = value; }
    });
    setPackageLock(value || !!pendingPackage);
  }

  /** 锁定名称/描述/分类/图标：导出包自带元数据；忙碌期间同样锁定。 */
  function setPackageLock(locked) {
    ["import-name", "import-desc", "import-category", "import-glyph"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = locked; }
    });
    var hint = $("import-package-hint");
    if (hint) { hint.hidden = !pendingPackage; }
  }

  function showImportView() {
    if (importing) { toast("正在导入，请稍候"); return; }
    pendingImport = null;
    pendingPackage = null;
    $("import-review").hidden = true;
    $("import-status").textContent = "";
    $("import-findings").innerHTML = "";
    $("import-assets-warning").hidden = true;
    $("import-file-name").textContent = "未选择文件";
    $("import-file-name").classList.remove("set");
    $("import-name").value = "";
    $("import-desc").value = "";
    $("import-category").value = "";
    $("import-glyph").value = "";
    setPackageLock(false);
    showView("import-view");
  }

  /** 原生对话框选择文件：HTML 读回内容走既有审查流程，导出包记录路径与自带元数据。 */
  function pickImportFile() {
    if (!host || importing) { return; }
    host.callManager("manager.pickImportFile", {}).then(function (r) {
      if (!r || !r.path) { return; }   // 用户取消

      pendingImport = null;
      pendingPackage = null;
      $("import-review").hidden = true;
      var label = $("import-file-name");
      label.textContent = r.fileName + (r.kind === "package" ? "（导出包）" : "");
      label.classList.add("set");

      if (r.kind === "package") {
        pendingPackage = { path: r.path, itemKind: r.itemKind || "", name: r.name || "" };
        $("import-name").value = r.name || "";
        $("import-desc").value = "";
        $("import-category").value = "";
        $("import-glyph").value = "";
        setPackageLock(true);
        return;
      }

      pendingImport = { name: r.name || "", html: r.html || "" };
      setPackageLock(false);
      if (!$("import-name").value.trim()) { $("import-name").value = r.name || ""; }
    }, function (e) { toast("选择文件失败：" + errText(e), true); });
  }

  function runImport(acceptHighSeverity) {
    if (!host || importing) { return; }
    if (acceptHighSeverity && !pendingImport && !pendingPackage) { return; }

    if (!acceptHighSeverity && !pendingImport && !pendingPackage) {
      toast("请先选择文件", true);
      pickImportFile();
      return;
    }

    if (pendingPackage) { runPackageImport(acceptHighSeverity); return; }

    var name = $("import-name").value.trim();
    if (!name) { toast("请填写工具名称", true); $("import-name").focus(); return; }
    pendingImport.name = name;

    $("import-status").textContent = acceptHighSeverity ? "正在导入…" : "正在检查…";
    setImportBusy(true);

    host.importTool({
      name: pendingImport.name,
      html: pendingImport.html,
      description: $("import-desc").value.trim() || null,
      category: $("import-category").value.trim() || null,
      glyph: $("import-glyph").value.trim() || null,
      acceptHighSeverity: !!acceptHighSeverity
    }).then(function (r) {
      if (r.saved) {
        toast("已导入 " + r.toolName + "（已打开独立窗口）");
        openSavedTool(r.toolId);
        return;
      }

      // 需要确认高危项
      $("import-status").textContent = "存在高危项，请审查后确认";
      renderFindingsInto($("import-findings"), r.findings);
      $("import-toolid").textContent = r.toolId;
      $("import-perms").textContent = (r.permissions && r.permissions.length) ? r.permissions.join("、") : "无";
      showExternalAssetsWarning(r.externalAssets);
      $("import-review").hidden = false;
      toast("需要确认后才能导入", true);
    }, function (e) {
      $("import-status").textContent = "";
      toast("导入失败：" + errText(e), true);
    }).finally(function () { setImportBusy(false); });
  }

  /** 导出包导入：小工具带安全审查（高危需二次确认）；启动项/在线工具直接保存。 */
  function runPackageImport(acceptHighSeverity) {
    $("import-status").textContent = acceptHighSeverity ? "正在导入…" : "正在检查…";
    setImportBusy(true);

    host.callManager("manager.importItemPackage", {
      path: pendingPackage.path,
      acceptHighSeverity: !!acceptHighSeverity
    }).then(function (r) {
      if (!r) { $("import-status").textContent = ""; return; }

      if (r.saved) {
        if (r.kind === "tool") {
          toast("已导入 " + r.itemName + "（已打开独立窗口）");
          openSavedTool(r.itemId);
          return;
        }

        toast("已导入" + (r.kind === "web" ? "在线工具" : "启动项") + "「" + r.itemName + "」");
        refreshLauncher();
        if (isContentMode) { closeContent(); } else { showView("content-home"); }
        return;
      }

      $("import-status").textContent = "存在高危项，请审查后确认";
      renderFindingsInto($("import-findings"), r.findings);
      $("import-toolid").textContent = r.itemId;
      $("import-perms").textContent = (r.permissions && r.permissions.length) ? r.permissions.join("、") : "无";
      showExternalAssetsWarning(r.externalAssets);
      $("import-review").hidden = false;
      toast("需要确认后才能导入", true);
    }, function (e) {
      $("import-status").textContent = "";
      toast("导入失败：" + errText(e), true);
    }).finally(function () { setImportBusy(false); });
  }

  function showExternalAssetsWarning(assets) {
    var warn = $("import-assets-warning");
    if (assets && assets.length) {
      warn.hidden = false;
      warn.innerHTML = "检测到对工具目录外资源的引用，导入后这些资源将无法加载：<ul><li>" +
        assets.map(esc).join("</li><li>") + "</li></ul>";
    } else {
      warn.hidden = true;
    }
  }

  on("import-tool", "click", function () {
    if (isContentMode) { showImportView(); }
    else { openContentPanel("import"); }
  });
  on("import-cancel", "click", function () {
    if (isContentMode) { closeContent(); return; }
    if (currentTool) { showView("tool-view"); }
    else { showView("content-home"); }
  });
  on("import-abort", "click", function () {
    if (isContentMode) { closeContent(); return; }
    if (currentTool) { showView("tool-view"); }
    else { showView("content-home"); }
  });
  on("import-pick", "click", pickImportFile);
  on("import-run", "click", function () { runImport(false); });
  on("import-confirm", "click", function () { runImport(true); });

  ["import-name", "import-desc", "import-category", "import-glyph"].forEach(function (id) {
    on(id, "input", function () {
      pendingImport = null;
      $("import-review").hidden = true;
    });
  });

  /* ================= AI 创建工具 ================= */
  function showCreateView() {
    cancelVisibleStream("create");
    createSequence += 1;
    lastGenerated = null;
    $("create-generate").disabled = false;
    $("create-result").hidden = true;
    $("create-stream").hidden = true;
    $("create-findings").innerHTML = "";
    $("create-status").textContent = "";
    $("create-accept").checked = false;
    showView("create-view");
    $("create-prompt").focus();
  }

  function generate() {
    if (!host) { return; }
    var prompt = $("create-prompt").value.trim();
    if (!prompt) { toast("请先描述需求", true); return; }

    var requestNumber = ++createSequence;
    lastGenerated = null;
    $("create-result").hidden = true;
    $("create-accept").checked = false;
    $("create-status").textContent = "AI 生成中（下方实时输出）…";
    $("create-generate").disabled = true;
    var stream = beginStream("create");

    host.callManagerStream("manager.generateTool", { prompt: prompt }, stream.onDelta, stream.control).then(function (view) {
      if (requestNumber !== createSequence) { return; }
      stream.finish(false);
      lastGenerated = view;
      $("create-status").textContent = "生成完成，请审查";
      renderFindingsInto($("create-findings"), view.findings);
      $("create-manifest").textContent = JSON.stringify(view.manifest, null, 2);
      $("create-html").textContent = view.indexHtml;
      $("create-result").hidden = false;
      $("create-save").disabled = view.hasFatal;
      if (view.hasFatal) { toast("生成内容存在致命问题，不能保存", true); }
      else if (view.requiresExplicitAcceptance) { toast("包含高危项，需勾选确认后才能保存", true); }
      else { toast("审查通过"); }
    }, function (e) {
      if (requestNumber !== createSequence) { return; }
      stream.finish(true);
      if (isCancelled(e)) {
        $("create-status").textContent = "已取消生成";
      } else {
        $("create-status").textContent = "生成失败：" + errText(e);
        toast("生成失败：" + errText(e), true);
      }
    }).then(function () {
      if (requestNumber === createSequence) { $("create-generate").disabled = false; }
    });
  }

  function saveGenerated() {
    if (!host || !lastGenerated) { return; }
    if (lastGenerated.hasFatal) { toast("存在致命问题，不能保存", true); return; }
    var accept = $("create-accept").checked;
    if (lastGenerated.requiresExplicitAcceptance && !accept) { toast("请先勾选确认", true); return; }

    var requestNumber = createSequence;
    $("create-save").disabled = true;
    host.callManager("manager.createTool", { generated: lastGenerated, acceptHighSeverity: accept })
      .then(function (r) {
        if (requestNumber !== createSequence) { refreshTools(); return; }
        lastGenerated = null;
        toast("已保存 " + r.toolId + "（已打开独立窗口）");
        openSavedTool(r.toolId);
      }, function (e) {
        if (requestNumber !== createSequence) { return; }
        $("create-save").disabled = false;
        toast("保存失败：" + errText(e), true);
      });
  }

  /** 保存/导入成功后：打开独立工具窗口，并收起当前管理窗口。 */
  function openSavedTool(toolId) {
    if (!host) { return; }
    host.callManager("manager.openTool", { toolId: toolId }).then(function () {
      if (isContentMode) { closeContent(); }
    }, function (e) { toast("打开失败：" + errText(e), true); });
  }

  /* ================= 搜索 + 键盘流 ================= */
  on("search", "input", function () {
    searchTerm = this.value.trim().toLowerCase();
    renderNav();
  });

  /** 当前可见的导航按钮（统一列表，已按使用频次排序）。 */
  function visibleNavItems() {
    var nodes = document.querySelectorAll("#nav-list .nav-item");
    var list = [];
    for (var i = 0; i < nodes.length; i++) {
      if (nodes[i].offsetParent !== null) { list.push(nodes[i]); }
    }
    return list;
  }

  function focusNavItem(index) {
    var items = visibleNavItems();
    if (!items.length) { return; }
    var clamped = Math.max(0, Math.min(items.length - 1, index));
    items[clamped].focus();
  }

  on("search", "keydown", function (e) {
    if (e.key === "ArrowDown") { e.preventDefault(); focusNavItem(0); }
    else if (e.key === "ArrowUp") { e.preventDefault(); focusNavItem(visibleNavItems().length - 1); }
    else if (e.key === "Enter") {
      e.preventDefault();
      var items = visibleNavItems();
      if (items.length) { items[0].click(); }
    } else if (e.key === "Escape") {
      if (this.value) { this.value = ""; searchTerm = ""; renderNav(); }
      else { this.blur(); }
    }
  });

  document.addEventListener("keydown", function (e) {
    var target = e.target;
    var tag = target && target.tagName;
    var typing = tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || (target && target.isContentEditable);

    /* 右键原位菜单优先响应 Escape（其余键盘流不动） */
    if (e.key === "Escape" && openMenuState) { closeItemMenu(); return; }

    /* 任意位置按 / 快速聚焦搜索（输入框内除外） */
    if (e.key === "/" && !typing && !e.ctrlKey && !e.metaKey && !e.altKey) {
      var search = $("search");
      if (search) { e.preventDefault(); search.focus(); search.select(); }
      return;
    }

    if (target && target.classList && target.classList.contains("nav-item")) {
      var items = visibleNavItems();
      var index = items.indexOf(target);
      if (e.key === "ArrowDown") { e.preventDefault(); focusNavItem(index + 1); }
      else if (e.key === "ArrowUp") {
        e.preventDefault();
        if (index <= 0) { var s = $("search"); if (s) { s.focus(); } }
        else { focusNavItem(index - 1); }
      } else if (e.key === "Escape") {
        var s2 = $("search"); if (s2) { s2.focus(); }
      }
    }
  });

  /* 点击菜单外的第一下只收起菜单，不触发下面条目的动作（捕获阶段拦截） */
  document.addEventListener("click", function (e) {
    if (!openMenuState) { return; }
    var menuEl = openMenuState.menuEl;
    if (menuEl && typeof menuEl.contains === "function" && menuEl.contains(e.target)) { return; }
    closeItemMenu();
    if (e.preventDefault) { e.preventDefault(); }
    if (e.stopPropagation) { e.stopPropagation(); }
  }, true);

  /* 列表滚动时浮层会脱离条目位置：直接收起，避免悬空 */
  document.addEventListener("scroll", function () {
    if (openMenuState) { closeItemMenu(); }
  }, true);

  /* ================= 内嵌工具栏 / 取消按钮 ================= */
  on("tool-close", "click", function () {
    if (isContentMode) { closeContent(); }
    else { closeToolView(); }
  });
  on("tool-reload", "click", reloadTool);
  on("tool-versions", "click", function () {
    var t = allTools.filter(function (x) { return currentTool && x.id === currentTool.id; })[0];
    if (t) { showVersionView(t); }
  });
  on("tool-delete", "click", function () {
    var t = allTools.filter(function (x) { return currentTool && x.id === currentTool.id; })[0];
    if (t) { deleteTool(t); }
  });
  on("tool-window", "click", function () {
    if (!currentTool) { return; }
    host.callManager("manager.openTool", { toolId: currentTool.id }).then(function () {
      toast("已在独立窗口打开（摄像头/麦克风等能力在这里可用）");
    }, function (e) { toast("打开失败：" + errText(e), true); });
  });

  on("source-tools", "click", function () {
    if (isContentMode) { showSourceView(); }
    else { openContentPanel("source"); }
  });
  on("source-settings", "click", function () {
    host.callManager("manager.openSettings", {}).catch(function (error) { toast("打开设置失败：" + errText(error), true); });
  });
  on("source-close", "click", function () {
    if (isContentMode) { closeContent(); }
    else { showView("content-home"); }
  });
  on("source-refresh", "click", loadSourceCatalog);
  on("source-search", "input", function () {
    clearTimeout(sourceState.timer);
    sourceState.timer = setTimeout(function () {
      sourceState.query = $("source-search").value.trim();
      sourceState.page = 1;
      loadSourceCatalog();
    }, 250);
  });
  on("source-category", "change", function () {
    sourceState.category = this.value;
    sourceState.page = 1;
    loadSourceCatalog();
  });
  on("source-prev", "click", function () {
    if (sourceState.page > 1) { sourceState.page -= 1; loadSourceCatalog(); }
  });
  on("source-next", "click", function () {
    var catalog = sourceState.catalog;
    if (catalog && sourceState.page < Math.ceil(catalog.total / catalog.pageSize)) {
      sourceState.page += 1;
      loadSourceCatalog();
    }
  });
  on("source-grid", "click", function (event) {
    var action = sourceAction(event.target);
    if (!action) { return; }
    var kind = action.dataset.sourceAction;
    if (kind === "settings") {
      host.callManager("manager.openSettings", {}).catch(function (error) { toast("打开设置失败：" + errText(error), true); });
      return;
    }
    if (kind === "refresh") { loadSourceCatalog(); return; }
    if (kind === "clear") {
      sourceState.query = "";
      sourceState.category = "";
      sourceState.page = 1;
      $("source-search").value = "";
      $("source-category").value = "";
      loadSourceCatalog();
      return;
    }
    var id = action.dataset.sourceId;
    if (kind === "preview") { previewSourceTool(id, action.dataset.sourceVersion); }
    else {
      var tool = (sourceState.catalog && sourceState.catalog.items || []).filter(function (item) { return item.id === id; })[0];
      if (tool) { showSourceDetail(tool); }
    }
  });
  on("source-detail-back", "click", function () {
    if (sourceState.loadingDetail || sourceState.installing || sourceState.downloading) { return; }
    resetSourceDetail();
    $("source-review").hidden = true;
    $("source-accept").checked = false;
    $("source-detail").hidden = true;
    $("source-catalog").hidden = false;
    $("source-grid").hidden = false;
  });
  on("source-version", "change", changeSourceVersion);
  on("source-detail-preview", "click", function () {
    if (sourceState.detail && !sourceState.loadingDetail && !sourceState.installing && !sourceState.downloading) {
      previewSourceTool(sourceState.detail.tool.id, sourceState.version);
    }
  });
  on("source-detail-install", "click", installSourceDetail);
  on("source-detail-download", "click", downloadSourceDetail);
  on("source-detail-submit", "click", function () {
    if (sourceState.detail && !sourceState.loadingDetail && !sourceState.installing && !sourceState.downloading) {
      submitSourceLocal(sourceState.detail.tool.kind || "tool", sourceState.detail.tool.id);
    }
  });
  on("source-submit-local", "click", function () {
    var value = $("source-local-tools").value || "";
    var split = value.indexOf(":");
    if (split <= 0) { return; }
    submitSourceLocal(value.slice(0, split), value.slice(split + 1));
  });
  on("source-local-tools", "change", function () {
    $("source-submit-local").disabled = !this.value;
    updateSourcePickPreview();
  });

  on("create-tool", "click", function () {
    if (isContentMode) { showCreateView(); }
    else { openContentPanel("create"); }
  });
  on("add-web", "click", function () {
    if (isContentMode) { showWebView(); }
    else { openContentPanel("web"); }
  });
  on("web-cancel", "click", function () {
    if (isContentMode) { closeContent(); return; }
    if (currentTool) { showView("tool-view"); }
    else { showView("content-home"); }
  });
  on("web-save", "click", submitWebTool);
  on("web-name", "keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); $("web-url").focus(); }
  });
  on("web-url", "keydown", function (e) {
    if (e.key === "Enter") { e.preventDefault(); submitWebTool(); }
  });
  on("create-cancel", "click", function () {
    cancelVisibleStream("create");
    if (isContentMode) { closeContent(); return; }
    if (currentTool) { showView("tool-view"); }
    else { showView("content-home"); }
  });
  on("create-generate", "click", generate);
  on("create-save", "click", saveGenerated);
  on("open-settings", "click", function () {
    if (!host) { toast("桥接不可用", true); return; }
    host.callManager("manager.openSettings", {}).then(function () {
      toast("已打开设置");
    }, function (e) { toast("打开设置失败：" + errText(e), true); });
  });

  /* ================= 启动 ================= */
  /* 后台索引完成 → 宿主推送 launcher-changed → 主窗口刷新统一列表 */
  window.addEventListener("host-event", function (e) {
    if (e.detail === "launcher-changed" && !isContentMode) { refreshLauncher(); }
  });

  /* 回到主窗口时刷新：内容窗口里创建/导入/删除工具、启动程序后保持最新排序 */
  window.addEventListener("focus", function () {
    refreshSourceEntry();
    if (!isContentMode && host) { refreshLauncher(); }
  });

  if (host) {
    refreshSourceEntry();
    if (isContentMode) {
      loadContentWindowState();
      refreshTools().then(function () {
        var view = pageQuery.view;
        if (view === "tool" && pageQuery.toolId) {
          var t = allTools.filter(function (x) { return x.id === pageQuery.toolId; })[0] ||
            { id: pageQuery.toolId, name: pageQuery.toolId, version: "", isValid: true };
          embedTool(t);
        } else if (view === "version" && pageQuery.toolId) {
          var versionTool = allTools.filter(function (x) { return x.id === pageQuery.toolId; })[0] ||
            { id: pageQuery.toolId, name: pageQuery.toolId, version: "", isValid: true };
          showVersionView(versionTool);
        } else if (view === "create") {
          showCreateView();
        } else if (view === "import") {
          showImportView();
        } else if (view === "web") {
          showWebView();
        } else if (view === "source") {
          showSourceView();
        } else {
          showView("content-home");
        }
      });
    } else {
      refreshLauncher();
    }
  } else {
    toast("宿主桥接不可用", true);
  }
})();
