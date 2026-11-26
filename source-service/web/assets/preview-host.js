(function () {
  "use strict";
  var pending = Object.create(null);
  var sequence = 0;

  function reply(frame, sid, id, result, error) {
    try {
      frame.contentWindow.postMessage({
        mark: "__qtoolsPreviewResponse",
        sid: sid,
        id: id,
        result: result,
        error: error || null
      }, "*");
    } catch (ignored) { }
  }

  function findFrame(source) {
    return Array.prototype.find.call(document.querySelectorAll("iframe[data-preview-host]"), function (frame) {
      return frame.contentWindow === source;
    }) || null;
  }

  function storageKey(sid, key) { return "qtools-preview:" + sid + ":" + key; }

  function handle(frame, data) {
    var sid = frame.dataset.sid || "";
    var method = data.method;
    var payload = data.payload || {};
    var id = data.id;
    try {
      if (typeof id !== "string" || !sid || sid !== data.sid) {
        reply(frame, sid, id, null, { code: "PREVIEW_BAD_REQUEST", message: "预览请求不合法" });
        return;
      }
      if (method === "theme.get") {
        reply(frame, sid, id, {
          theme: document.documentElement.dataset.theme || "system",
          dark: matchMedia("(prefers-color-scheme: dark)").matches
        });
        return;
      }
      if (method === "tool.getManifest") {
        reply(frame, sid, id, frame.dataset.manifest ? JSON.parse(frame.dataset.manifest) : null);
        return;
      }
      if (method === "storage.get" || method === "storage.set" || method === "storage.remove" || method === "storage.list") {
        if (method !== "storage.list" && !/^[A-Za-z0-9_.-]{1,64}$/.test(payload.key || "")) {
          throw new Error("存储键只能包含字母、数字、点、下划线和短横线");
        }
        var prefix = storageKey(sid, "");
        if (method === "storage.get") {
          var value = localStorage.getItem(storageKey(sid, payload.key));
          reply(frame, sid, id, value === null ? null : JSON.parse(value));
        } else if (method === "storage.set") {
          var encoded = JSON.stringify(payload.value === undefined ? null : payload.value);
          if (encoded.length > 131072) { throw new Error("预览存储单值不能超过 128 KiB"); }
          localStorage.setItem(storageKey(sid, payload.key), encoded);
          reply(frame, sid, id, { ok: true });
        } else if (method === "storage.remove") {
          localStorage.removeItem(storageKey(sid, payload.key));
          reply(frame, sid, id, { ok: true });
        } else {
          var keys = [];
          for (var i = 0; i < localStorage.length; i++) {
            var key = localStorage.key(i);
            if (key && key.indexOf(prefix) === 0) { keys.push(key.slice(prefix.length)); }
          }
          reply(frame, sid, id, { keys: keys.slice(0, 200) });
        }
        return;
      }
      reply(frame, sid, id, null, { code: "PREVIEW_PERMISSION_DENIED", message: "在线预览不提供该能力" });
    } catch (error) {
      reply(frame, sid, id, null, { code: "PREVIEW_STORAGE_ERROR", message: error.message });
    }
  }

  window.addEventListener("message", function (event) {
    var data = event.data;
    if (!data || data.mark !== "__qtoolsPreviewHost" || event.origin !== "null") { return; }
    var frame = findFrame(event.source);
    if (!frame) { return; }
    handle(frame, data);
  });

  window.QToolsPreview = {
    attach: function (frame, sid, manifest) {
      frame.dataset.previewHost = "";
      frame.dataset.sid = sid;
      if (manifest) { frame.dataset.manifest = JSON.stringify(manifest); }
    }
  };
})();
