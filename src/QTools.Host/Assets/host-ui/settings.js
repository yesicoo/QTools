/* QTools 设置窗口：运行环境 + AI 接口配置
 * 约束：本页面 CSP 为 connect-src 'none'，禁止 fetch/XHR——一切能力调用都走 window.host 桥接。 */
(function () {
  "use strict";

  var host = window.host || null;
  var saving = false;

  function $(id) { return document.getElementById(id); }
  function on(id, event, handler) {
    var el = $(id);
    if (el) { el.addEventListener(event, handler); }
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

  /* ================= 运行环境 ================= */
  function renderServerInfo(info) {
    $("set-platform").textContent = info.platform;
    $("set-webview").textContent = info.webView;
    $("set-webview-version").textContent = info.webViewRuntimeVersion || "未知";
    $("set-address").textContent = "http://127.0.0.1:" + info.port + "/";
    $("set-datadir").textContent = info.dataDirectory || "—";
    $("set-version").textContent = "v" + info.version;
  }

  function refreshServerInfo() {
    if (!host) { return; }
    host.callManager("manager.getServerInfo", {}).then(renderServerInfo, function (e) {
      toast("读取运行环境失败：" + errText(e), true);
    });
  }

  /* ================= AI 接口 ================= */
  function loadAiConfig(refreshForm) {
    if (!host) { return; }
    return host.callManager("manager.getAiConfig", {}).then(function (c) {
      if (refreshForm !== false) {
        $("ai-baseurl").value = c.baseUrl || "";
        $("ai-model").value = c.model || "";
        $("ai-temperature").value = c.temperature;
        $("ai-timeout").value = c.timeoutSeconds;
      }
      $("ai-secret-mode").textContent = c.secretStoreMode || "未知";
      var badge = $("ai-status");
      badge.textContent = c.configured ? "已配置" : "未配置";
      badge.className = "badge" + (c.configured ? "" : " none");
    }, function (e) { toast("读取 AI 配置失败：" + errText(e), true); });
  }

  function setSaving(value) {
    saving = value;
    ["ai-save", "ai-clear"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = value; }
    });
  }

  function readAiForm() {
    var baseUrl = $("ai-baseurl").value.trim();
    var url;
    try { url = new URL(baseUrl); } catch (_) { url = null; }
    if (!url || !/^https?:\/\//i.test(baseUrl) || !/^https?:$/.test(url.protocol) || !url.hostname) {
      throw new Error("Base URL 必须是绝对 HTTP(S) 地址");
    }

    var model = $("ai-model").value.trim();
    if (!model) { throw new Error("模型不能为空"); }
    var temperatureText = $("ai-temperature").value.trim();
    var temperature = Number(temperatureText);
    if (!temperatureText || !Number.isFinite(temperature) || temperature < 0 || temperature > 2) {
      throw new Error("Temperature 必须是 0~2 之间的有限数值");
    }
    var timeoutText = $("ai-timeout").value.trim();
    var timeoutSeconds = Number(timeoutText);
    if (!timeoutText || !Number.isInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 600) {
      throw new Error("超时必须是 1~600 之间的整数");
    }

    var payload = { baseUrl: baseUrl, model: model, temperature: temperature, timeoutSeconds: timeoutSeconds };
    var key = $("ai-key").value;
    if (key) { payload.apiKey = key; }
    return payload;
  }

  function saveAiConfig(clearKey) {
    if (!host || saving) { return; }
    var payload;
    try {
      // 清除只发送标记，未保存的表单既不参与校验也不被写入。
      payload = clearKey ? { clearKey: true } : readAiForm();
    } catch (e) { toast(e.message, true); return; }

    setSaving(true);
    Promise.resolve().then(function () {
      return host.callManager("manager.setAiConfig", payload);
    }).then(function () {
      $("ai-key").value = "";
      toast(clearKey ? "已清除 API Key" : "AI 配置已保存并生效");
      return loadAiConfig(!clearKey);
    }).catch(function (e) {
      toast("保存失败：" + errText(e), true);
    }).finally(function () { setSaving(false); });
  }

  /* ================= 全局热键 ================= */
  function renderHotkey(c) {
    var checkbox = $("hotkey-enabled");
    var badge = $("hotkey-status");
    if (!checkbox || !badge) { return; }
    checkbox.checked = !!c.hotkeyEnabled;
    checkbox.disabled = !c.hotkeySupported;
    if (!c.hotkeySupported) {
      badge.textContent = "当前平台不支持";
      badge.className = "badge none";
    } else if (c.hotkeyRegistered) {
      badge.textContent = "已注册 · Alt+Space";
      badge.className = "badge";
    } else {
      badge.textContent = "未注册";
      badge.className = "badge none";
    }
  }

  on("hotkey-enabled", "change", function () {
    if (!host) { return; }
    var checkbox = this;
    var enabled = checkbox.checked;
    checkbox.disabled = true;
    host.callManager("manager.setHotkeyEnabled", { enabled: enabled }).then(function (c) {
      renderHotkey(c);
      toast(enabled ? "已启用：Alt+Space 呼出 / 隐藏主窗口" : "已关闭全局热键");
    }, function (e) {
      checkbox.checked = !enabled;
      checkbox.disabled = false;
      toast("设置失败：" + errText(e), true);
    });
  });

  /* ================= 本机程序索引 ================= */
  var indexPollTimer = null;

  function renderIndexInfo(index) {
    var el = $("index-info");
    if (!el) { return; }
    if (!index) { el.textContent = "未知"; return; }
    if (index.indexing) {
      el.textContent = "正在后台索引…（旧索引仍可用）";
    } else if (index.programCount > 0) {
      el.textContent = "已索引 " + index.programCount + " 个程序" +
        (index.lastIndexedUtc ? " · " + new Date(index.lastIndexedUtc).toLocaleString() : "");
    } else {
      el.textContent = "尚未索引到程序";
    }
  }

  function loadIndexInfo() {
    if (!host) { return; }
    return host.callManager("manager.launcherList", {}).then(function (r) {
      renderIndexInfo(r && r.index);
      if (r && r.index && r.index.indexing) { scheduleIndexPoll(); }
    }, function () { /* 状态读取失败不打扰用户 */ });
  }

  /* 索引进行中每 2s 刷新一次状态，完成后自动停止 */
  function scheduleIndexPoll() {
    if (indexPollTimer) { return; }
    indexPollTimer = setTimeout(function () {
      indexPollTimer = null;
      loadIndexInfo();
    }, 2000);
  }

  on("index-rebuild", "click", function () {
    if (!host) { return; }
    var btn = this;
    btn.disabled = true;
    host.callManager("manager.reindexPrograms", {}).then(function (status) {
      renderIndexInfo(status);
      toast("已在后台重建索引，完成后自动覆盖旧索引");
      scheduleIndexPoll();
    }, function (e) { toast("重建索引失败：" + errText(e), true); })
      .finally(function () { btn.disabled = false; });
  });

  /* ================= 备份与恢复 ================= */
  var backupBusy = false;

  function setBackupBusy(value) {
    backupBusy = value;
    ["backup-create", "backup-import", "backup-open-dir", "backup-note"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = value; }
    });
  }

  function fmtSize(bytes) {
    if (bytes >= 1024 * 1024) { return (bytes / 1024 / 1024).toFixed(1) + " MB"; }
    return Math.max(1, Math.round(bytes / 1024)) + " KB";
  }

  function renderBackups(view) {
    var ul = $("backup-list");
    if (!ul) { return; }
    ul.innerHTML = "";
    var backups = (view && view.backups) || [];
    if (backups.length === 0) {
      var empty = document.createElement("li");
      empty.innerHTML = '<span class="v-meta">暂无备份，点「创建备份」生成第一个</span>';
      ul.appendChild(empty);
      return;
    }
    backups.forEach(function (b) {
      var li = document.createElement("li");

      var num = document.createElement("span");
      num.className = "v-num";
      num.textContent = b.name;

      var meta = document.createElement("span");
      meta.className = "v-meta";
      meta.textContent = new Date(b.createdAtUtc).toLocaleString() + " · " + fmtSize(b.sizeBytes) +
        (b.note ? " · " + b.note : "");

      var actions = document.createElement("div");
      actions.className = "v-actions";

      var restore = document.createElement("button");
      restore.type = "button";
      restore.className = "btn ghost sm";
      restore.textContent = "恢复";
      restore.addEventListener("click", function () { restoreBackup(b.name); });
      actions.appendChild(restore);

      var del = document.createElement("button");
      del.type = "button";
      del.className = "btn ghost sm danger";
      del.textContent = "删除";
      del.addEventListener("click", function () { deleteBackup(b.name); });
      actions.appendChild(del);

      li.appendChild(num);
      li.appendChild(meta);
      li.appendChild(actions);
      ul.appendChild(li);
    });
  }

  function loadBackups() {
    if (!host) { return; }
    return host.callManager("manager.listBackups", {}).then(renderBackups, function (e) {
      toast("读取备份列表失败：" + errText(e), true);
    });
  }

  function createBackup() {
    if (!host || backupBusy) { return; }
    setBackupBusy(true);
    $("backup-status").textContent = "正在创建备份…";
    host.callManager("manager.createBackup", { note: $("backup-note").value.trim() || null })
      .then(function (r) {
        $("backup-status").textContent = "";
        $("backup-note").value = "";
        toast("已创建备份：" + r.name + "（" + fmtSize(r.sizeBytes) + "）");
        return loadBackups();
      }, function (e) {
        $("backup-status").textContent = "";
        toast("创建备份失败：" + errText(e), true);
      }).finally(function () { setBackupBusy(false); });
  }

  function restoreBackup(name) {
    if (!host || backupBusy) { return; }
    if (!confirm("恢复备份「" + name + "」？\n\n当前程序、工具与数据将被备份内容覆盖；\n恢复前会自动创建安全快照，之后仍可回滚。")) { return; }
    setBackupBusy(true);
    $("backup-status").textContent = "正在恢复…";
    host.callManager("manager.restoreBackup", { name: name }).then(function (r) {
      $("backup-status").textContent = "";
      toast("已恢复" + (r.safetyBackupName ? "（恢复前快照：" + r.safetyBackupName + "）" : ""));
      loadBackups();
      loadAiConfig();   /* 配置可能随备份一起恢复 */
    }, function (e) {
      $("backup-status").textContent = "";
      toast("恢复失败：" + errText(e), true);
    }).finally(function () { setBackupBusy(false); });
  }

  function deleteBackup(name) {
    if (!host || backupBusy) { return; }
    if (!confirm("确定删除备份「" + name + "」？此操作不可恢复。")) { return; }
    setBackupBusy(true);
    host.callManager("manager.deleteBackup", { name: name }).then(function () {
      toast("已删除 " + name);
      return loadBackups();
    }, function (e) {
      toast("删除失败：" + errText(e), true);
    }).finally(function () { setBackupBusy(false); });
  }

  function importBackup() {
    if (!host || backupBusy) { return; }
    setBackupBusy(true);
    $("backup-status").textContent = "正在选择文件…";
    host.callManager("manager.importBackupFile", {}).then(function (r) {
      $("backup-status").textContent = "";
      if (r && r.name) {
        toast("已导入备份：" + r.name);
        return loadBackups();
      }
      /* 用户在原生对话框里取消了选择 */
    }, function (e) {
      $("backup-status").textContent = "";
      toast("导入失败：" + errText(e), true);
    }).finally(function () { setBackupBusy(false); });
  }

  on("backup-create", "click", createBackup);
  on("backup-import", "click", importBackup);
  on("backup-open-dir", "click", function () {
    if (!host) { return; }
    host.callManager("manager.openBackupDirectory", {}).catch(function (e) {
      toast("打开目录失败：" + errText(e), true);
    });
  });

  /* ================= Git 远程备份 ================= */
  var gitBusy = false;

  function setGitBusy(value) {
    gitBusy = value;
    ["git-save", "git-push", "git-restore", "git-url"].forEach(function (id) {
      var el = $(id);
      if (el) { el.disabled = value; }
    });
  }

  function renderGitLastPush(c) {
    var el = $("git-last-push");
    if (!el) { return; }
    el.textContent = c.lastPushUtc
      ? "上次推送：" + new Date(c.lastPushUtc).toLocaleString() + " · " + (c.lastPushStatus || "")
      : "尚未推送过";
  }

  function loadBackupConfig() {
    if (!host) { return; }
    return host.callManager("manager.getBackupConfig", {}).then(function (c) {
      $("git-url").value = c.gitRemoteUrl || "";
      renderGitLastPush(c);
      renderHotkey(c);
    }, function (e) { toast("读取备份配置失败：" + errText(e), true); });
  }

  on("git-save", "click", function () {
    if (!host || gitBusy) { return; }
    setGitBusy(true);
    host.callManager("manager.setBackupConfig", { gitRemoteUrl: $("git-url").value.trim() || null })
      .then(function (c) {
        renderGitLastPush(c);
        toast($("git-url").value.trim() ? "仓库地址已保存" : "已清除仓库地址");
      }, function (e) { toast("保存失败：" + errText(e), true); })
      .finally(function () { setGitBusy(false); });
  });

  /** Git 推送 / 恢复共用：流式进度 + 可取消。 */
  function runGit(method) {
    if (!host || gitBusy) { return; }
    var isPush = method === "manager.gitPush";
    if (!isPush && !confirm(
      "从远程仓库恢复？\n\n当前程序、工具与数据将被远程内容覆盖；\n恢复前会自动创建安全快照，之后仍可回滚。")) { return; }
    var url = $("git-url").value.trim();
    if (!url) { toast("请先填写远程仓库地址", true); $("git-url").focus(); return; }

    setGitBusy(true);
    $("git-status").textContent = isPush ? "正在推送…" : "正在恢复…";
    var box = $("git-stream");
    var text = $("git-stream-text");
    var cancelBtn = $("git-stream-cancel");
    var control = {};
    if (text) { text.textContent = ""; }
    if (box) { box.hidden = false; }
    if (cancelBtn) {
      cancelBtn.hidden = false;
      cancelBtn.disabled = false;
      cancelBtn.textContent = "取消";
      cancelBtn.onclick = function () {
        if (!control.requestId) { return; }
        cancelBtn.disabled = true;
        cancelBtn.textContent = "取消中…";
        host.cancelRequest(control.requestId).catch(function () { /* 取消失败时原请求继续 */ });
      };
    }

    host.callManagerStream(method, { gitRemoteUrl: url }, function (delta) {
      if (!text) { return; }
      text.textContent += delta;
      text.scrollTop = text.scrollHeight;
    }, control).then(function (r) {
      if (cancelBtn) { cancelBtn.onclick = null; cancelBtn.hidden = true; }
      $("git-status").textContent = "";
      if (isPush) {
        toast(r && r.message ? r.message : "推送完成");
      } else {
        toast("已从远程恢复" + (r && r.safetyBackupName ? "（恢复前快照：" + r.safetyBackupName + "）" : ""));
        loadBackups();
        loadAiConfig();
      }
      loadBackupConfig();
    }, function (e) {
      if (cancelBtn) { cancelBtn.onclick = null; cancelBtn.hidden = true; }
      $("git-status").textContent = "";
      if (e && e.code === "REQUEST_CANCELLED") { toast("已取消"); }
      else { toast((isPush ? "推送失败：" : "恢复失败：") + errText(e), true); }
      loadBackupConfig();   /* 失败状态也会写回配置 */
    }).finally(function () { setGitBusy(false); });
  }

  on("git-push", "click", function () { runGit("manager.gitPush"); });
  on("git-restore", "click", function () { runGit("manager.gitRestore"); });

  /* ================= 事件绑定 ================= */
  on("ai-save", "click", function () { saveAiConfig(false); });
  on("ai-clear", "click", function () {
    if (!saving && confirm("确定清除已保存的 API Key？")) { saveAiConfig(true); }
  });

  /* ================= 启动 ================= */
  if (host) {
    refreshServerInfo();
    loadAiConfig();
    loadBackups();
    loadBackupConfig();
    loadIndexInfo();
  } else {
    toast("宿主桥接不可用", true);
  }
})();
