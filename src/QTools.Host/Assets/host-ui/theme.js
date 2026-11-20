/* ============================================================================
 * 主题实时切换（宿主页面专用）
 *
 * 首屏配色由宿主在 HTML 的 <html> 上注入 data-theme（设置里选了 light/dark 时），
 * 或者由 CSS 的 prefers-color-scheme 决定（system）。这里负责运行期切换：
 *   - 启动时向宿主核对一次当前主题（防止页面打开后设置已在别处变化）
 *   - 收到 host-event: theme-changed 时重新核对并改写 data-theme
 *
 * 只作用于宿主页面（index / settings / tray-menu）；工具页面有各自的配色。
 * ============================================================================ */
(function () {
  "use strict";

  function apply(theme) {
    try {
      var root = document.documentElement;
      if (theme === "light" || theme === "dark") {
        root.setAttribute("data-theme", theme);
      } else {
        root.removeAttribute("data-theme");
      }
    } catch (error) { }
  }

  function refresh() {
    try {
      if (!window.host || typeof window.host.callManager !== "function") { return; }
      window.host.callManager("manager.getTheme", {}).then(function (result) {
        if (result && typeof result.theme === "string") {
          apply(result.theme);
        }
      }).catch(function () { });
    } catch (error) { }
  }

  try {
    window.addEventListener("host-event", function (event) {
      if (event && event.detail === "theme-changed") { refresh(); }
    });
  } catch (error) { }

  refresh();
})();
