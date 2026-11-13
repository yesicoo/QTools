/* 独立工具窗口的收缩菜单：刷新 / 置顶 / 钉住 / 版本管理（图标圆盘）。
   由 ToolStaticFileMiddleware 注入到所有工具页面；内嵌 iframe 与桥接不可用时自动退出。 */
(function () {
  "use strict";

  if (window.top !== window.self) { return; }
  var host = window.host;
  if (!host || !host.window) { return; }

  var CHIP_SIZE = 27;       // 按钮固定尺寸（夹位置与收缩都用它，不依赖未加载样式的布局）
  var HIDE_VISIBLE = 6;     // 收缩到边框后露出的像素
  var DOCK_DISTANCE = 60;   // 距边缘 ≤ 该值视为贴边，可自动收缩
  var WAKE_DISTANCE = 90;   // 指针进入该范围自动弹出
  var NEAR_DISTANCE = 60;   // 指针进入该范围恢复清晰
  var HIDE_DELAY = 650;

  var ICON_STYLE = 'viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
    'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"';
  var ICONS = {
    reload: '<svg ' + ICON_STYLE + '><path d="M20 11a8 8 0 1 0-2.34 5.66"/><path d="M20 4v7h-7"/></svg>',
    topmost: '<svg ' + ICON_STYLE + '><path d="M12 19V5"/><path d="M5 12l7-7 7 7"/></svg>',
    pinned: '<svg ' + ICON_STYLE + '><path d="M9 4h6l-1 5 3 3v2H7v-2l3-3z"/><path d="M12 14v6"/></svg>',
    versions: '<svg ' + ICON_STYLE + '><path d="M3 12a9 9 0 1 0 3-6.7"/><path d="M3 4v5h5"/>' +
      '<path d="M12 7v5l3 2"/></svg>',
  };

  var state = {
    topmost: false,
    pinned: false,
    near: false,
    retracted: false,
    menuOpen: false,
    pos: { left: 0, top: 0 },
    pointer: null,
  };

  var hideTimer = 0;
  var drag = null;
  var suppressClick = false;
  var styleReady = false;
  var savedPosition = null;
  var userMoved = false;

  function ignore() { /* 失败保持原状态 */ }

  function mount() {
    var wrap = document.createElement("qtools-window-controls");
    if (!wrap.attachShadow) { return; }
    var shadow = wrap.attachShadow({ mode: "closed" });

    var link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = "/__host__/window-controls.css";

    var root = document.createElement("div");
    root.className = "wrap";

    var trigger = document.createElement("button");
    trigger.type = "button";
    trigger.className = "trigger";
    trigger.textContent = "⋯";
    trigger.title = "工具菜单：刷新 / 置顶 / 钉住 / 版本管理（可拖动调整位置）";

    var menu = document.createElement("div");
    menu.className = "menu";
    menu.hidden = true;

    var reload = item("刷新", ICONS.reload);
    var topmost = item("置顶", ICONS.topmost);
    var pinned = item("钉住", ICONS.pinned);
    var versions = item("版本管理", ICONS.versions);
    var items = [reload, topmost, pinned, versions];
    items.forEach(function (el) { menu.appendChild(el); });
    root.appendChild(trigger);
    root.appendChild(menu);
    shadow.appendChild(link);
    shadow.appendChild(root);
    document.body.appendChild(wrap);

    function item(label, svg) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "item";
      button.title = label;
      button.setAttribute("aria-label", label);
      button.innerHTML = svg;
      return button;
    }

    /* ================= 圆盘 / 扇形排布 ================= */
    function layoutMenu() {
      var rect = trigger.getBoundingClientRect();
      var edgeThreshold = 100;
      var nearLeft = rect.left < edgeThreshold;
      var nearRight = window.innerWidth - rect.right < edgeThreshold;
      var nearTop = rect.top < edgeThreshold;
      var nearBottom = window.innerHeight - rect.bottom < edgeThreshold;

      /* 朝远离贴边的一侧展开；都不贴边就绕按钮排成一圈 */
      var pushX = nearLeft && nearRight ? 0 : nearLeft ? 1 : nearRight ? -1 : 0;
      var pushY = nearTop && nearBottom ? 0 : nearTop ? 1 : nearBottom ? -1 : 0;

      var angles;
      if (!pushX && !pushY) {
        angles = [-90, 0, 90, 180];
      } else {
        var center = Math.atan2(pushY, pushX) * 180 / Math.PI;
        var span = pushX && pushY ? 96 : 132;
        angles = [center - span / 2, center - span / 6, center + span / 6, center + span / 2];
      }

      /* 圆环贴得近一些；扇形受相邻图标间距限制，半径略大 */
      var radius = !pushX && !pushY ? 36 : (pushX && pushY ? 62 : 46);
      for (var i = 0; i < items.length; i++) {
        var rad = angles[i] * Math.PI / 180;
        items[i].style.setProperty("--tx", Math.round(Math.cos(rad) * radius) + "px");
        items[i].style.setProperty("--ty", Math.round(Math.sin(rad) * radius) + "px");
      }
    }

    function setOpen(open) {
      if (!open) {
        var wasOpen = state.menuOpen;
        state.menuOpen = false;
        menu.hidden = true;
        trigger.classList.remove("open");
        if (wasOpen && state.pointer) {
          scheduleHide();
        }
        return;
      }

      state.menuOpen = true;
      reveal();
      cancelHide();
      layoutMenu();
      menu.hidden = false;
      trigger.classList.add("open");
      menu.classList.remove("menu-anim");
      void menu.offsetWidth;
      menu.classList.add("menu-anim");
    }

    /* ================= 贴边收缩 / 自动弹出 ================= */
    function distanceToChip(x, y) {
      var dx = x - (state.pos.left + CHIP_SIZE / 2);
      var dy = y - (state.pos.top + CHIP_SIZE / 2);
      return Math.sqrt(dx * dx + dy * dy);
    }

    function dockEdge() {
      var rightGap = window.innerWidth - (state.pos.left + CHIP_SIZE);
      var bottomGap = window.innerHeight - (state.pos.top + CHIP_SIZE);
      var edge = "left";
      var distance = state.pos.left;
      if (rightGap < distance) { edge = "right"; distance = rightGap; }
      if (state.pos.top < distance) { edge = "top"; distance = state.pos.top; }
      if (bottomGap < distance) { edge = "bottom"; distance = bottomGap; }
      return distance <= DOCK_DISTANCE ? edge : null;
    }

    function retract() {
      if (!styleReady || state.retracted || state.menuOpen || drag) { return; }
      var edge = dockEdge();
      if (!edge) { return; }

      var dx = 0;
      var dy = 0;
      if (edge === "left") { dx = -(CHIP_SIZE - HIDE_VISIBLE) - state.pos.left; }
      else if (edge === "right") { dx = window.innerWidth - HIDE_VISIBLE - state.pos.left; }
      else if (edge === "top") { dy = -(CHIP_SIZE - HIDE_VISIBLE) - state.pos.top; }
      else { dy = window.innerHeight - HIDE_VISIBLE - state.pos.top; }

      wrap.style.setProperty("--hide-x", dx + "px");
      wrap.style.setProperty("--hide-y", dy + "px");
      state.retracted = true;
      wrap.classList.add("retracted");
      state.near = false;
      root.classList.remove("near");
    }

    function reveal() {
      cancelHide();
      if (!state.retracted) { return; }
      state.retracted = false;
      wrap.classList.remove("retracted");
    }

    function cancelHide() {
      if (hideTimer) { clearTimeout(hideTimer); hideTimer = 0; }
    }

    function scheduleHide(delay) {
      cancelHide();
      hideTimer = setTimeout(function () {
        hideTimer = 0;
        retract();
      }, typeof delay === "number" ? delay : HIDE_DELAY);
    }

    /* ================= 位置 ================= */
    function moveTo(left, top) {
      /* 用固定尺寸而不是 offsetWidth：样式表未加载时布局不可靠，会把位置误夹到左边缘 */
      left = Math.min(Math.max(4, left), Math.max(4, window.innerWidth - CHIP_SIZE - 4));
      top = Math.min(Math.max(4, top), Math.max(4, window.innerHeight - CHIP_SIZE - 4));
      state.pos.left = left;
      state.pos.top = top;
      wrap.style.left = left + "px";
      wrap.style.top = top + "px";
      wrap.style.right = "auto";
    }

    function applySavedPosition() {
      if (!styleReady || userMoved || !savedPosition) { return; }
      moveTo(savedPosition.left, savedPosition.top);
      savedPosition = null;
    }

    /* ================= 置顶 / 钉住状态 ================= */
    function render() {
      topmost.classList.toggle("active", state.topmost);
      pinned.classList.toggle("active", state.pinned);
      topmost.title = state.topmost ? "取消置顶" : "置顶：始终显示在其他窗口之上";
      pinned.title = state.pinned ? "取消钉住" : "钉住：锁定窗口位置和大小";
    }

    function apply(result) {
      state.topmost = !!(result && result.topmost);
      state.pinned = !!(result && result.pinned);
      render();
      if (result && typeof result.chipLeft === "number" && typeof result.chipTop === "number") {
        savedPosition = { left: result.chipLeft, top: result.chipTop };
        applySavedPosition();
      }
    }

    trigger.addEventListener("click", function (event) {
      event.stopPropagation();
      if (suppressClick) { suppressClick = false; return; }
      setOpen(menu.hidden);
    });
    reload.addEventListener("click", function (event) {
      event.stopPropagation();
      location.reload();
    });
    topmost.addEventListener("click", function (event) {
      event.stopPropagation();
      setOpen(false);
      host.window.setTopmost(!state.topmost).then(apply, ignore);
    });
    pinned.addEventListener("click", function (event) {
      event.stopPropagation();
      setOpen(false);
      host.window.setPinned(!state.pinned).then(apply, ignore);
    });
    versions.addEventListener("click", function (event) {
      event.stopPropagation();
      setOpen(false);
      host.window.openVersionManager().catch(ignore);
    });

    document.addEventListener("click", function () { setOpen(false); });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") { setOpen(false); }
    });

    /* ================= 靠近显形（默认 25% 透明） ================= */
    function updateProximity(x, y) {
      state.pointer = { x: x, y: y };
      var distance = distanceToChip(x, y);

      if (distance <= WAKE_DISTANCE || state.menuOpen || drag) {
        reveal();
        cancelHide();
      } else {
        scheduleHide();
      }

      var near = distance <= NEAR_DISTANCE || state.menuOpen || !!drag;
      if (near !== state.near) {
        state.near = near;
        root.classList.toggle("near", near);
      }
    }

    document.addEventListener("pointermove", function (event) {
      updateProximity(event.clientX, event.clientY);
    }, { passive: true });
    document.addEventListener("pointerleave", function () {
      state.pointer = null;
      if (state.near) { state.near = false; root.classList.remove("near"); }
      scheduleHide(250);
    });
    window.addEventListener("blur", function () {
      if (state.near) { state.near = false; root.classList.remove("near"); }
      scheduleHide(250);
    });

    trigger.addEventListener("focus", function () { reveal(); cancelHide(); });

    /* ================= 拖动移位 ================= */
    trigger.addEventListener("pointerdown", function (event) {
      if (event.button !== 0) { return; }
      reveal();
      cancelHide();
      drag = {
        id: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        originLeft: state.pos.left,
        originTop: state.pos.top,
        moved: false,
      };
      try { trigger.setPointerCapture(event.pointerId); } catch (e) { /* 拖拽降级 */ }
    });

    trigger.addEventListener("pointermove", function (event) {
      if (!drag || event.pointerId !== drag.id) { return; }
      var dx = event.clientX - drag.startX;
      var dy = event.clientY - drag.startY;
      if (!drag.moved && Math.abs(dx) < 4 && Math.abs(dy) < 4) { return; }
      drag.moved = true;
      userMoved = true;
      savedPosition = null;
      setOpen(false);
      root.classList.add("dragging");
      moveTo(drag.originLeft + dx, drag.originTop + dy);
    });

    function endDrag(event) {
      if (!drag || event.pointerId !== drag.id) { return; }
      var moved = drag.moved;
      drag = null;
      root.classList.remove("dragging");
      try { trigger.releasePointerCapture(event.pointerId); } catch (e) { /* 已释放 */ }
      if (moved) {
        var target = { left: state.pos.left, top: state.pos.top };
        host.window.saveChipPosition(target.left, target.top).catch(ignore);
        suppressClick = true;
        setTimeout(function () { suppressClick = false; }, 0);
      }
    }

    trigger.addEventListener("pointerup", endDrag);
    trigger.addEventListener("pointercancel", endDrag);

    window.addEventListener("resize", function () {
      /* 页面加载期间窗口尺寸可能先给 0/极小值，此时别夹位置，否则会被钉死在左上角 */
      if (!styleReady || window.innerWidth < 120 || window.innerHeight < 60) { return; }
      moveTo(state.pos.left, state.pos.top);
    });

    function onStyleReady() {
      if (styleReady) { return; }
      styleReady = true;
      /* 把当前默认位置钉成显式 left/top，后续拖动、收缩都用同一坐标 */
      var rect = wrap.getBoundingClientRect();
      moveTo(rect.left, rect.top);
      applySavedPosition();
      scheduleHide(1200);
    }

    link.addEventListener("load", onStyleReady);
    link.addEventListener("error", onStyleReady);
    if (link.sheet) { onStyleReady(); }

    host.window.getState().then(apply, function () { wrap.remove(); });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", mount);
  } else {
    mount();
  }
})();
