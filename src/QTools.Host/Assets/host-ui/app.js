/* QTools 双模式页面：
 * 主窗口（细长条）：统一列表（小工具 + 本机程序，按使用频次排序）；
 *   点工具 → 内容窗口贴边打开；点本机程序 → 直接启动。
 * 内容窗口（?mode=content&view=…）：内嵌工具 / AI 创建 / 导入 / 版本管理。
 * 约束：本页面 CSP 为 connect-src 'none'，禁止 fetch/XHR——一切能力调用都走 window.host 桥接。
 * 约束：一律用 on(id, event, handler) 绑事件（元素缺失静默跳过，避免单点缺失中断整个脚本）。 */
(function () {
  "use strict";

  var host = window.host || null;
  var lastGenerated = null;
  var pendingUpdate = null;
  var pendingImport = null;
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

  function iconFor(toolId, name) {
    var id = (toolId + " " + name).toLowerCase();
    if (id.indexOf("json") >= 0) { return "{ }"; }
    if (id.indexOf("qr") >= 0 || id.indexOf("二维码") >= 0) { return "▦"; }
    if (id.indexOf("note") >= 0 || id.indexOf("todo") >= 0) { return "✎"; }
    if (id.indexOf("calc") >= 0) { return "＋"; }
    return "◆";
  }

  function appGlyph(app) {
    if (app.glyph) { return app.glyph; }
    var name = (app.name || app.id || "?").trim();
    return name ? name[0].toUpperCase() : "?";
  }

  function matchesSearch(text) {
    if (!searchTerm) { return true; }
    return (text || "").toLowerCase().indexOf(searchTerm) >= 0;
  }

  function severityLabel(s) {
    return s === 0 ? "致命" : s === 1 ? "高危" : "提示";
  }

  /* ================= 视图切换 ================= */
  var VIEWS = ["tool-view", "content-home", "create-view", "version-view", "import-view"];

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

    ul.innerHTML = "";
    var total = 0;

    allItems.forEach(function (item) {
      if (!matchesSearch(item.searchKey || (item.name + " " + item.id))) { return; }
      total += 1;
      ul.appendChild(buildNavItem(item));
    });

    if (empty) { empty.hidden = total !== 0; }

    /* 当前内嵌工具仍存在时保持高亮 */
    markActive();
  }

  function kindLabel(kind) {
    return kind === "tool" ? "小工具" : "本机程序";
  }

  function buildNavItem(item) {
    var isTool = item.kind === "tool";
    var broken = isTool ? !item.isValid : !item.targetExists;
    var glyph = isTool ? iconFor(item.id, item.name) : appGlyph(item);
    var badge = isTool
      ? (item.isValid ? "v" + (item.version || "") : "manifest 无效")
      : (!item.targetExists ? "目标不存在" : (item.category || ""));

    var li = document.createElement("li");
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "nav-item" + (!isTool ? " app-item" : "") + (broken ? " broken" : "");
    btn.dataset.key = item.kind + ":" + item.id;
    btn.title = item.name + "（" + kindLabel(item.kind) + "，右键管理）";

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
    btn.addEventListener("click", function () { openItem(item); });
    btn.addEventListener("contextmenu", function (e) {
      e.preventDefault();
      if (item.kind === "app") { deleteApp(item); }
      else if (isTool) { deleteTool(item); }
    });
    li.appendChild(btn);
    return li;
  }

  /** 列表点击：工具 → 内容窗口贴边打开；本机程序 → 直接启动。 */
  function openItem(item) {
    if (!host) { toast("桥接不可用", true); return; }
    if (item.kind === "tool") {
      if (!item.isValid) { toast("manifest 无效，不能打开", true); return; }
      openContentPanel("tool", item.id);
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

      $("tool-name").textContent = r.name;
      $("tool-glyph").textContent = iconFor(r.toolId, r.name);
      var listedTool = allTools.filter(function (tool) { return tool.id === r.toolId; })[0] || t;
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
    /* 从哪来回哪去：内嵌中的工具回内嵌视图，否则回首页 */
    if (currentTool) { showView("tool-view"); }
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
  /** 打开流式预览区，返回 { control, onDelta, finish }。
   *  control.requestId 由 bridge.js 同步填入，取消按钮据此调用 manager.cancelRequest。 */
  function beginStream(prefix) {
    var box = $(prefix + "-stream");
    var text = $(prefix + "-stream-text");
    var cancelBtn = $(prefix + "-stream-cancel");
    var state = { control: {} };
    if (text) { text.textContent = ""; }
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
    state.onDelta = function (delta) {
      if (!text) { return; }
      text.textContent += delta;
      text.scrollTop = text.scrollHeight;
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

  /* ================= 导入本地 HTML ================= */
  function setImportBusy(value) {
    importing = value;
    ["import-run", "import-confirm", "import-file", "import-name", "import-desc"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = value; }
    });
  }

  function showImportView() {
    if (importing) { toast("正在导入，请稍候"); return; }
    $("import-review").hidden = true;
    $("import-status").textContent = "";
    $("import-findings").innerHTML = "";
    $("import-assets-warning").hidden = true;
    $("import-file").value = "";
    $("import-file-name").textContent = "未选择文件";
    $("import-file-name").classList.remove("set");
    $("import-name").value = "";
    $("import-desc").value = "";
    pendingImport = null;
    showView("import-view");
  }

  function readSelectedFile() {
    var input = $("import-file");
    var file = input.files && input.files[0];
    if (!file) {
      toast("请先选择一个 HTML 文件", true);
      return null;
    }

    var lower = file.name.toLowerCase();
    if (!lower.endsWith(".html") && !lower.endsWith(".htm")) {
      toast("请选择 .html 或 .htm 文件", true);
      return null;
    }

    setImportBusy(true);
    var reader = new FileReader();
    reader.onload = function () {
      pendingImport = { name: $("import-name").value.trim(), html: String(reader.result || "") };
      setImportBusy(false);
      runImport(false);
    };
    reader.onerror = function () { setImportBusy(false); toast("读取文件失败", true); };
    reader.readAsText(file, "utf-8");
    return "reading";
  }

  function runImport(acceptHighSeverity) {
    if (!host || importing || (acceptHighSeverity && !pendingImport)) { return; }

    if (!acceptHighSeverity) {
      var name = $("import-name").value.trim();
      if (!name) { toast("请填写工具名称", true); $("import-name").focus(); return; }
      if (!pendingImport) {
        var started = readSelectedFile();
        if (started !== "reading") { return; }
        return;   // 文件读取完成后会再次进入本函数
      }
      pendingImport.name = name;
    }

    $("import-status").textContent = acceptHighSeverity ? "正在导入…" : "正在检查…";
    setImportBusy(true);

    host.importTool({
      name: pendingImport.name,
      html: pendingImport.html,
      description: $("import-desc").value.trim() || null,
      acceptHighSeverity: !!acceptHighSeverity
    }).then(function (r) {
      if (r.saved) {
        toast("已导入 " + r.toolName + "（已在内容窗口打开）");
        embedSavedTool(r.toolId, r.toolName);
        return;
      }

      // 需要确认高危项
      $("import-status").textContent = "存在高危项，请审查后确认";
      renderFindingsInto($("import-findings"), r.findings);
      $("import-toolid").textContent = r.toolId;
      $("import-perms").textContent = (r.permissions && r.permissions.length) ? r.permissions.join("、") : "无";
      var warn = $("import-assets-warning");
      if (r.externalAssets && r.externalAssets.length) {
        warn.hidden = false;
        warn.innerHTML = "检测到对工具目录外资源的引用，导入后这些资源将无法加载：<ul><li>" +
          r.externalAssets.map(esc).join("</li><li>") + "</li></ul>";
      } else {
        warn.hidden = true;
      }
      $("import-review").hidden = false;
      toast("需要确认后才能导入", true);
    }, function (e) {
      $("import-status").textContent = "";
      toast("导入失败：" + errText(e), true);
    }).finally(function () { setImportBusy(false); });
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
  on("import-run", "click", function () { runImport(false); });
  on("import-confirm", "click", function () { runImport(true); });

  on("import-file", "change", function () {
    var input = $("import-file");
    var file = input.files && input.files[0];
    var label = $("import-file-name");
    if (file) {
      label.textContent = file.name + "（" + Math.max(1, Math.round(file.size / 1024)) + " KB）";
      label.classList.add("set");
      // 用文件名预填工具名（去掉扩展名）
      if (!$("import-name").value.trim()) {
        $("import-name").value = file.name.replace(/\.(html?|htm)$/i, "");
      }
    } else {
      label.textContent = "未选择文件";
      label.classList.remove("set");
    }
    pendingImport = null;
    $("import-review").hidden = true;
  });
  ["import-name", "import-desc"].forEach(function (id) {
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
        toast("已保存 " + r.toolId + "（已在内容窗口打开）");
        embedSavedTool(r.toolId, null);
      }, function (e) {
        if (requestNumber !== createSequence) { return; }
        $("create-save").disabled = false;
        toast("保存失败：" + errText(e), true);
      });
  }

  /** 保存/导入成功后：刷新工具清单并直接在当前内容窗口内嵌打开。 */
  function embedSavedTool(toolId, fallbackName) {
    refreshTools().then(function () {
      var listTool = allTools.filter(function (tool) { return tool.id === toolId; })[0] ||
        { id: toolId, name: fallbackName || toolId, version: "", isValid: true };
      embedTool(listTool);
    });
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

  on("create-tool", "click", function () {
    if (isContentMode) { showCreateView(); }
    else { openContentPanel("create"); }
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
    if (!isContentMode && host) { refreshLauncher(); }
  });

  if (host) {
    if (isContentMode) {
      refreshTools().then(function () {
        var view = pageQuery.view;
        if (view === "tool" && pageQuery.toolId) {
          var t = allTools.filter(function (x) { return x.id === pageQuery.toolId; })[0] ||
            { id: pageQuery.toolId, name: pageQuery.toolId, version: "", isValid: true };
          embedTool(t);
        } else if (view === "create") {
          showCreateView();
        } else if (view === "import") {
          showImportView();
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
