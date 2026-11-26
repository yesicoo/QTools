(function () {
  "use strict";
  window.Site = {
    qs: function (selector, root) { return (root || document).querySelector(selector); },
    byId: function (id) { return document.getElementById(id); },
    escape: function (value) {
      return String(value == null ? "" : value).replace(/[&<>"']/g, function (char) {
        return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char];
      });
    },
    api: async function (path, options) {
      var response = await fetch(path, options || {});
      var data = null;
      try { data = await response.json(); } catch (error) { data = null; }
      if (!response.ok) {
        var message = data && data.error && data.error.message ? data.error.message : "请求失败（HTTP " + response.status + "）";
        var error = new Error(message);
        error.code = data && data.error ? data.error.code : "HTTP_ERROR";
        throw error;
      }
      return data;
    },
    formatBytes: function (value) {
      var number = Number(value || 0);
      if (number < 1024) { return number + " B"; }
      if (number < 1024 * 1024) { return (number / 1024).toFixed(1) + " KiB"; }
      return (number / 1024 / 1024).toFixed(1) + " MiB";
    },
    formatDate: function (value) {
      var date = new Date(value);
      return isNaN(date.getTime()) ? "—" : date.toLocaleString("zh-CN");
    },
    statusBadge: function (status) {
      var names = { pending: "待审核", approved: "已通过", rejected: "已拒绝" };
      var classes = { pending: "warn", approved: "ok", rejected: "danger" };
      return '<span class="badge ' + (classes[status] || "") + '">' + Site.escape(names[status] || status) + "</span>";
    },
    showError: function (element, error) {
      element.textContent = error && error.message ? error.message : String(error);
      element.classList.remove("hidden");
    },
    permissions: function (manifest) {
      var names = { storage: "隔离存储", ai: "AI 调用", media: "硬件捕获", files: "二进制文件" };
      return (manifest.permissions || []).map(function (permission) {
        return names[permission] || permission;
      }).join("、") || "无";
    },
    initSourceConnect: function () {
      var url = window.location.origin;
      Array.prototype.forEach.call(document.querySelectorAll("[data-source-url]"), function (node) {
        node.textContent = url;
      });
      Array.prototype.forEach.call(document.querySelectorAll("[data-copy-source-url]"), function (button) {
        button.addEventListener("click", async function () {
          try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
              await navigator.clipboard.writeText(url);
            } else {
              var area = document.createElement("textarea");
              area.value = url;
              area.style.position = "fixed";
              area.style.opacity = "0";
              document.body.appendChild(area);
              area.select();
              document.execCommand("copy");
              area.remove();
            }
            button.textContent = "已复制";
          } catch (error) {
            button.textContent = "请手动复制";
          }
          setTimeout(function () { button.textContent = "复制"; }, 1600);
        });
      });
    }
  };

  Site.initSourceConnect();
})();
