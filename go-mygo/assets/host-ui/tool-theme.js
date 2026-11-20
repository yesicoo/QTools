/* ============================================================================
 * 工具页面主题（宿主自动注入，配合 tool-theme.css）
 *
 * 首屏的 data-theme / data-qtools-auto-dark 已由服务端写好，不会闪烁。
 * 这里只负责运行期同步：
 *   - 收到 host-event: theme-changed → window.host.theme.get() 取最新主题
 *   - 支持主题的页面：只改 data-theme，交给页面自己的深色样式
 *   - 旧工具（data-qtools-themed="0"）：按需增删 data-qtools-auto-dark 反色兜底
 *   - 跟随系统时监听 prefers-color-scheme，系统切换深浅后同步兜底
 * ============================================================================ */
(function () {
  "use strict";

  var root = document.documentElement;
  var themed = root.getAttribute("data-qtools-themed") !== "0";
  var theme = root.getAttribute("data-qtools-theme") || "system";
  var media = null;
  try { media = window.matchMedia("(prefers-color-scheme: dark)"); } catch (error) { media = null; }

  function applyAutoDark(dark) {
    if (themed) { return; }
    if (dark) {
      root.setAttribute("data-qtools-auto-dark", "1");
    } else {
      root.removeAttribute("data-qtools-auto-dark");
    }
  }

  function apply(state) {
    if (!state || typeof state.theme !== "string") { return; }
    theme = state.theme;

    if (theme === "system") {
      root.removeAttribute("data-theme");
    } else {
      root.setAttribute("data-theme", theme);
    }

    root.setAttribute("data-qtools-theme", theme);
    var dark = theme === "dark" || (theme === "system" && (state.dark === true || (media && media.matches)));
    applyAutoDark(dark);
  }

  function refresh() {
    try {
      if (!window.host || !window.host.theme || typeof window.host.theme.get !== "function") { return; }
      window.host.theme.get().then(apply).catch(function () { });
    } catch (error) { }
  }

  try {
    window.addEventListener("host-event", function (event) {
      if (event && event.detail === "theme-changed") { refresh(); }
    });
  } catch (error) { }

  try {
    if (media && typeof media.addEventListener === "function") {
      media.addEventListener("change", function () {
        if (theme !== "system" || themed) { return; }
        applyAutoDark(media.matches);
      });
    }
  } catch (error) { }

  refresh();
})();
