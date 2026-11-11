/* ============================================================================
 * QTools window.host 桥接垫层
 * 由宿主在 HTML 响应中注入（<script src="/__host__/bridge.js?tool=...&v=...">）。
 *
 * 平台差异统一在此吸收（均已从 PhotinoX.Native 二进制提取注入原文验证）：
 *   - PhotinoX 在文档起始注入 window.external = {
 *       sendMessage:  m => window.chrome.webview.postMessage(m)                       (Windows/WebView2)
 *                     m => window.webkit.messageHandlers.Photinointerop.postMessage(m) (Linux/macOS WebKit)
 *       receiveMessage: cb => ... 由原生层回调
 *     }
 *   - 兜底：直接探测 chrome.webview.postMessage / webkit.messageHandlers.Photinointerop
 *
 * 工具页面只应使用 window.host.*，不应直接依赖上述任何平台对象。
 *
 * 运行环境（两种，传输层自动适配）：
 *   1) 顶层窗口（独立工具窗口 / 宿主管理界面）：直连原生 sendMessage；
 *      同时充当「内嵌中继站」——内嵌 iframe 的桥接请求经 window.postMessage 转发到这里，
 *      再由这里发给原生层；原生响应中未被本页认领的，回投给发起中继的 iframe。
 *      （原因：WebView2 的 SendWebMessage 只投递到顶层 frame，iframe 无法直接收到原生回包。）
 *   2) iframe（主窗口右侧内嵌的工具页）：requests 经 window.parent.postMessage（同源）中继。
 *
 * 信封字段 token：主窗口内嵌场景下的「内嵌凭证」（iframe URL 的 ?bt= 由宿主签发），
 *   分发器据此把身份解析为对应 toolId；独立窗口场景宿主忽略该字段。
 *
 * 结构说明（顺序很重要）：
 *   1) 先把 window.host API 表面暴露出去——即使传输层还没就绪，页面也拿得到对象；
 *   2) 再做传输探测与响应注册，且全程容错：任何一步失败都不能让本文件中断，
 *      否则页面会看到 window.host 为 undefined（曾导致启动器空白、设置打不开）。
 * ============================================================================ */
(function () {
  "use strict";

  /* ---------- 解析注入参数（脚本 URL）与页面 URL（内嵌凭证 bt=） ---------- */
  var query = {};
  var script = document.currentScript;
  if (script && script.src) {
    var qIndex = script.src.indexOf("?");
    if (qIndex >= 0) {
      var pairs = script.src.substring(qIndex + 1).split("&");
      for (var i = 0; i < pairs.length; i++) {
        var kv = pairs[i].split("=");
        if (kv[0]) {
          query[decodeURIComponent(kv[0])] = decodeURIComponent(kv[1] || "");
        }
      }
    }
  }

  var toolId = query.tool || "";
  var version = query.v || "0.0.0";
  var port = parseInt(query.port || "0", 10) || 0;

  /* 页面 URL 携带的内嵌凭证（manager.embedTool 返回的 ?bt=…）；
     hash 形式兼容：优先 location.search，找不到再看 location.hash */
  var embedToken = (function () {
    try {
      var source = String(window.location.search || "") + String(window.location.hash || "");
      var m = source.match(/[?&#]bt=([0-9a-f]{32})/);
      return m ? m[1] : "";
    } catch (e) {
      return "";
    }
  })();

  var inIframe = (function () {
    try { return window.self !== window.top; } catch (e) { return true; }
  })();

  /* ---------- 传输层 ---------- */
  var sendRaw = null;
  var pending = Object.create(null);
  var sequence = 0;
  var requestPrefix = "b" + Math.random().toString(36).slice(2) + "-";
  var relays = Object.create(null);
  var receiverReady = false;
  var AI_TIMEOUT_MS = 610000;
  var RELAY_MARK = "__qtoolsRelay";
  var RELAY_RESPONSE_MARK = "__qtoolsRelayResponse";

  function nativeSend(message) {
    try {
      if (window.external && typeof window.external.sendMessage === "function") {
        window.external.sendMessage(message);
        return true;
      }
      if (window.chrome && window.chrome.webview &&
          typeof window.chrome.webview.postMessage === "function") {
        window.chrome.webview.postMessage(message);
        return true;
      }
      if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.Photinointerop &&
          typeof window.webkit.messageHandlers.Photinointerop.postMessage === "function") {
        window.webkit.messageHandlers.Photinointerop.postMessage(message);
        return true;
      }
    } catch (error) {
      return false;
    }
    return false;
  }

  function receiveNative(raw) {
    try { routeResponse(parseIncoming(raw)); } catch (error) { }
  }

  function registerNativeReceiver() {
    if (receiverReady) { return true; }
    try {
      if (window.external && typeof window.external.receiveMessage === "function") {
        window.external.receiveMessage(receiveNative);
        receiverReady = true;
      }
    } catch (error) { }
    if (!receiverReady) {
      try {
        if (window.chrome && window.chrome.webview &&
            typeof window.chrome.webview.addEventListener === "function") {
          window.chrome.webview.addEventListener("message", function (event) { receiveNative(event.data); });
          receiverReady = true;
        }
      } catch (error) { }
    }
    return receiverReady;
  }

  function detectTransport() {
    if (inIframe) {
      /* iframe 内嵌模式：经顶层窗口（宿主管理界面）同源中继。
         顶层窗口的宿主页在同源（127.0.0.1:port）下，origin 校验足够。 */
      return function (message) {
        try {
          window.parent.postMessage({
            relay: RELAY_MARK,
            payload: message
          }, window.location.origin);
          return true;
        } catch (error) {
          return false;
        }
      };
    }

    try {
      var canSend = (window.external && typeof window.external.sendMessage === "function") ||
        (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === "function") ||
        (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.Photinointerop &&
          typeof window.webkit.messageHandlers.Photinointerop.postMessage === "function");
      if (canSend && registerNativeReceiver()) { return nativeSend; }
    } catch (error) { }
    return null;
  }

  /* ---------- 响应路由（顶层窗口同时做内嵌中继的回投） ---------- */
  function routeResponse(message) {
    if (!message || typeof message.id !== "string") { return; }

    /* 宿主主动事件信封 {"id","event"}（如索引完成 launcher-changed）：
       没有对应请求，派发到页面级 host-event，由宿主 UI 自行订阅 */
    if (typeof message.event === "string") {
      try { window.dispatchEvent(new CustomEvent("host-event", { detail: message.event })); } catch (error) { }
      return;
    }

    var entry = pending[message.id];
    if (entry) {
      /* 流式增量信封 {"id","stream":{"delta"}}：与最终响应共用 id，
         命中 onDelta 时只投递增量、不结束 Promise */
      if (message.stream && typeof message.stream.delta === "string") {
        if (entry.onDelta) {
          try { entry.onDelta(message.stream.delta); } catch (error) { }
        }
        return;
      }

      delete pending[message.id];
      clearTimeout(entry.timer);

      if (message.error) {
        entry.reject(message.error);
      } else {
        entry.resolve(message.result === undefined ? null : message.result);
      }
      return;
    }

    var relay = relays[message.id];
    if (!relay) { return; }
    delete relays[message.id];
    clearTimeout(relay.timer);
    if (!relay.frame.isConnected || relay.frame.src !== relay.url) { return; }
    try {
      relay.window.postMessage({
        relay: RELAY_RESPONSE_MARK,
        message: { id: relay.id, result: message.result, error: message.error }
      }, window.location.origin);
    } catch (error) { }
  }

  function parseIncoming(raw) {
    if (raw && typeof raw === "object") { return raw; }
    if (typeof raw !== "string") { return null; }
    try { return JSON.parse(raw); } catch (error) { return null; }
  }

  /* 非桥接响应的两类入站消息：内嵌 iframe 的请求中继（顶层）、顶层回投（iframe） */
  function handleRelayInbound(raw, sourceWindow, origin) {
    var data = null;
    try {
      data = parseIncoming(raw);
    } catch (error) {
      return;
    }

    if (!data || typeof data !== "object") { return; }

    if (data.relay === RELAY_MARK && typeof data.payload === "string") {
      if (inIframe || toolId !== "__host__" || origin !== window.location.origin) { return; }
      try {
        var frame = Array.prototype.find.call(document.querySelectorAll("iframe"), function (item) {
          return item.contentWindow === sourceWindow;
        });
        var request = parseIncoming(data.payload);
        if (!frame || !request || typeof request.id !== "string" || typeof request.method !== "string") { return; }
        var token = new URL(frame.src).searchParams.get("bt");
        if (!token || request.token !== token) { return; }

        var id = requestPrefix + "r" + (++sequence);
        relays[id] = {
          id: request.id, window: sourceWindow, frame: frame, url: frame.src,
          timer: setTimeout(function () { delete relays[id]; }, AI_TIMEOUT_MS)
        };
        request.id = id;
        if (!sendRaw) { sendRaw = detectTransport(); }
        if (!sendRaw || !sendRaw(JSON.stringify(request))) {
          sendRaw = null;
          routeResponse({ id: id, error: {
            code: "HOST_BRIDGE_UNAVAILABLE", message: "宿主桥接不可用，请重新打开工具"
          } });
        }
      } catch (error) { }
      return;
    }

    if (inIframe && sourceWindow === window.parent && origin === window.location.origin &&
        data.relay === RELAY_RESPONSE_MARK && data.message) {
      routeResponse(parseIncoming(data.message));
    }
  }

  /* ---------- 调用管道（Promise + 超时；可选流式增量回调） ---------- */
  function call(method, payload, timeoutMs, onDelta, control) {
    return new Promise(function (resolve, reject) {
      // 传输层可能尚未就绪（文档创建早期的竞态）：每次调用前再探测一次
      if (!sendRaw) { sendRaw = detectTransport(); }

      if (!sendRaw) {
        reject({
          code: "HOST_BRIDGE_UNAVAILABLE",
          message: "宿主桥接不可用：请通过 QTools 宿主打开本页面"
        });
        return;
      }

      sequence += 1;
      var id = requestPrefix + sequence;
      if (control && typeof control === "object") { control.requestId = id; }

      var timer = setTimeout(function () {
        delete pending[id];
        reject({
          code: "BRIDGE_TIMEOUT",
          message: "桥接调用超时（" + Math.round(timeoutMs / 1000) + "s）：" + method
        });
      }, timeoutMs);

      pending[id] = { resolve: resolve, reject: reject, timer: timer, onDelta: onDelta || null };

      var request = {
        id: id,
        toolId: toolId,
        method: method,
        payload: payload === undefined ? null : payload
      };

      /* 内嵌凭证：iframe 顶部 URL 由宿主 manager.embedTool 签发（?bt=…）。
         独立工具窗口没有该参数 → 字段为空字符串，序列化后宿主侧忽略。 */
      if (embedToken) { request.token = embedToken; }

      try {
        if (!sendRaw(JSON.stringify(request))) {
          sendRaw = null;
          clearTimeout(timer);
          delete pending[id];
          reject({ code: "HOST_BRIDGE_UNAVAILABLE", message: "宿主桥接发送失败，请重试" });
        }
      } catch (error) {
        clearTimeout(timer);
        delete pending[id];
        reject({ code: "BRIDGE_BAD_REQUEST", message: String(error && error.message ? error.message : error) });
      }
    });
  }

  /* ============ 1) 先暴露 API 表面（页面任何时候都能拿到 window.host） ============ */
  window.host = {
    version: version,
    server: { port: port, toolId: toolId },
    isAvailable: function () {
      if (!sendRaw) { sendRaw = detectTransport(); }
      return sendRaw !== null;
    },

    ai: {
      /** { messages, temperature } -> { content }（需 manifest 声明 "ai" 权限） */
      chat: function (options) { return call("ai.chat", options || {}, AI_TIMEOUT_MS); }
    },

    storage: {
      /** -> { value } 或 null（需 manifest 声明 "storage" 权限，按 toolId 隔离） */
      get: function (key) { return call("storage.get", { key: key }, 10000); },
      set: function (key, value) { return call("storage.set", { key: key, value: value }, 10000); },
      remove: function (key) { return call("storage.remove", { key: key }, 10000); },
      list: function () { return call("storage.list", null, 10000); }
    },

    tool: {
      /** -> 本工具的已校验 manifest */
      getManifest: function () { return call("tool.getManifest", null, 10000); }
    },

    files: {
      /** { name, dataBase64, contentType? } -> { name, sizeBytes, url }（需 files 权限） */
      save: function (p) { return call("files.save", p, 120000); },
      /** -> { files: [{ name, sizeBytes, modifiedUtc }] } */
      list: function () { return call("files.list", null, 30000); },
      /** { name } -> { name, contentType, dataBase64 } */
      read: function (p) { return call("files.read", p, 120000); },
      /** { name } -> { deleted } */
      delete: function (p) { return call("files.delete", p, 30000); }
    },

    /**
     * 宿主管理界面专用（仅 toolId == "__host__" 可用，其他工具调用一律 PERMISSION_DENIED）。
     * 方法：manager.getServerInfo / listTools / openTool / deleteTool / getAiConfig / setAiConfig
     *      / generateTool / createTool / fixTool / renameTool / openSettings / importTool
     *      / listVersions / switchVersion / deleteVersion / generateUpdate / applyUpdate
     *      / listApps / launchApp / addApp / deleteApp / discoverPrograms / pickProgram
     *      / launcherList / reindexPrograms / openContent / closeContent
     *      / addWebTool / deleteWebTool / renameWebTool / openWebTool（在线工具：录入网址 → 独立窗口在线打开）
     *      / hideProgram / unhideProgram（索引程序从列表隐藏 / 恢复显示）
     *      / revealItem（在文件管理器中定位条目所在位置）
     *      / embedTool（签发内嵌凭证，返回带 ?bt= 的工具 URL 供 iframe 内嵌）
     *      / listBackups / createBackup / restoreBackup / deleteBackup / importBackupFile
     *      / openBackupDirectory / getBackupConfig / setBackupConfig / gitPush / gitRestore
     *      / setHotkeyEnabled / cancelRequest
     */
    callManager: function (method, payload) {
      var long = ["manager.generateTool", "manager.generateUpdate", "manager.fixTool",
        "manager.pickProgram", "manager.gitPush", "manager.gitRestore", "manager.importBackupFile"];
      var medium = ["manager.createBackup", "manager.restoreBackup"];
      var timeout = long.indexOf(method) >= 0 ? AI_TIMEOUT_MS
        : medium.indexOf(method) >= 0 ? 300000 : 30000;
      return call(method, payload || {}, timeout);
    },

    /**
     * 流式管理调用（仅宿主管理界面）：onDelta(text) 逐段收到 AI 原始输出 / Git 进度；
     * control.requestId 同步回填，供 cancelRequest 取消。最终 Promise 仍解析为完整结果。
     */
    callManagerStream: function (method, payload, onDelta, control) {
      return call(method, payload || {}, AI_TIMEOUT_MS, onDelta, control);
    },

    /** 取消一个正在执行的桥接请求（requestId 来自 callManagerStream 的 control）。 */
    cancelRequest: function (requestId) {
      return call("manager.cancelRequest", { id: requestId }, 10000);
    },

    /** 导入本地 HTML 为工具（仅宿主管理界面可用） */
    importTool: function (options) {
      return call("manager.importTool", options || {}, 60000);
    }
  };

  /* ============ 2) 再做传输探测与响应注册（全程容错，失败不影响上面的 API） ============ */
  sendRaw = detectTransport();

  try {
    /* 同源 window.postMessage 中继通道：
       顶层窗口 → 转发 iframe 的请求给原生；iframe → 接收顶层回投的响应。 */
    window.addEventListener("message", function (event) {
      try {
        if (event.origin !== window.location.origin) { return; }
        var message = parseIncoming(event.data);
        if (!message || !message.relay) { return; }
        handleRelayInbound(message, event.source, event.origin);
      } catch (error) { /* 忽略：跨域或非桥接消息 */ }
    });
  } catch (error) {
    // 注册失败不致命：下一次 call() 会重新探测传输层
  }

  /* ---------- 桥接不可用时：友好提示，不白屏 ---------- */
  if (!sendRaw) {
    var showWarning = function () {
      var bar = document.createElement("div");
      bar.className = "qtools-bridge-warning";
      bar.setAttribute("role", "alert");
      bar.textContent = "宿主桥接不可用：请通过 QTools 宿主打开本页面（window.host 已停用）。";
      var parent = document.body || document.documentElement;
      if (parent) { parent.insertBefore(bar, parent.firstChild); }
    };

    if (document.readyState === "loading") {
      document.addEventListener("DOMContentLoaded", showWarning);
    } else {
      showWarning();
    }
  }
})();
