(function () {
  "use strict";
  var pending = Object.create(null);
  var sequence = 0;
  var currentScript = document.currentScript;
  var previewSid = currentScript ? new URL(currentScript.src).searchParams.get("sid") : "";

  function send(method, payload) {
    return new Promise(function (resolve, reject) {
      var id = "p" + (++sequence);
      var timer = setTimeout(function () {
        delete pending[id];
        reject({ code: "PREVIEW_TIMEOUT", message: "预览宿主响应超时" });
      }, 5000);
      pending[id] = { resolve: resolve, reject: reject, timer: timer };
      try {
        parent.postMessage({
          mark: "__qtoolsPreviewHost",
          sid: previewSid,
          id: id,
          method: method,
          payload: payload || {}
        }, "*");
      } catch (error) {
        clearTimeout(timer);
        delete pending[id];
        reject(error);
      }
    });
  }

  function unavailable() {
    return Promise.reject({ code: "PREVIEW_PERMISSION_DENIED", message: "该能力仅在 QTools 本地运行时可用" });
  }

  window.host = {
    version: "source-preview",
    server: { toolId: "", port: location.port ? Number(location.port) : 0 },
    isAvailable: function () { return true; },
    storage: {
      get: function (key) { return send("storage.get", { key: key }).then(function (value) { return { value: value }; }); },
      set: function (key, value) { return send("storage.set", { key: key, value: value }); },
      remove: function (key) { return send("storage.remove", { key: key }); },
      list: function () { return send("storage.list", {}); }
    },
    theme: { get: function () { return send("theme.get", {}); } },
    tool: { getManifest: function () { return send("tool.getManifest", {}); } },
    ai: { chat: unavailable },
    files: { save: unavailable, list: unavailable, read: unavailable, delete: unavailable },
    window: { getState: unavailable, setTopmost: unavailable, setPinned: unavailable, openVersionManager: unavailable, saveChipPosition: unavailable },
    callManager: unavailable,
    callManagerStream: unavailable,
    cancelRequest: unavailable,
    importTool: unavailable
  };

  window.addEventListener("message", function (event) {
    if (event.source !== parent) { return; }
    var data = event.data;
    if (!data || data.mark !== "__qtoolsPreviewResponse" || data.sid !== previewSid || typeof data.id !== "string") { return; }
    var item = pending[data.id];
    if (!item) { return; }
    delete pending[data.id];
    clearTimeout(item.timer);
    if (data.error) { item.reject(data.error); } else { item.resolve(data.result === undefined ? null : data.result); }
  });
})();
