(function () {
  var host = window.host;
  var listEl = document.getElementById("tray-list");
  var emptyEl = document.getElementById("tray-empty");
  var exitEl = document.getElementById("tray-exit");
  if (!host || !listEl || !emptyEl || !exitEl) { return; }

  function hide() {
    host.callManager("manager.hideTrayMenu", {});
  }

  function activate(item) {
    var method = item.kind === "tool" ? "manager.openContent"
      : item.kind === "web" ? "manager.openWebTool" : "manager.launchApp";
    var payload = item.kind === "tool" ? { view: "tool", toolId: item.id }
      : item.kind === "web" ? { webId: item.id } : { appId: item.id };
    host.callManager(method, payload).catch(function () { /* 失败由主界面日志/提示负责 */ });
    hide();
  }

  function buildItem(item) {
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "tray-item";

    var icon = document.createElement("span");
    icon.className = "tray-icon";
    if (item.iconUrl) {
      var img = document.createElement("img");
      img.src = item.iconUrl;
      img.alt = "";
      icon.appendChild(img);
    } else {
      var name = (item.name || "?").trim();
      icon.textContent = item.glyph || name.charAt(0) || "?";
    }

    var nameEl = document.createElement("span");
    nameEl.className = "tray-name";
    nameEl.textContent = item.name || item.id;

    var kindEl = document.createElement("span");
    kindEl.className = "tray-kind";
    kindEl.textContent = item.kindLabel || "";

    btn.appendChild(icon);
    btn.appendChild(nameEl);
    btn.appendChild(kindEl);
    btn.addEventListener("click", function () { activate(item); });
    return btn;
  }

  function render(items) {
    listEl.innerHTML = "";
    items = items || [];
    emptyEl.hidden = items.length !== 0;
    items.forEach(function (item) { listEl.appendChild(buildItem(item)); });
  }

  function notifyReady() {
    // 宿主收到后把离屏窗口移到光标处显示；复用窗口不会再触发 InitialContentLoaded
    host.callManager("manager.trayMenuReady", {}).catch(function () { /* 宿主有超时兜底 */ });
  }

  host.callManager("manager.trayMenuList", {}).then(function (r) {
    render(r && r.items);
    notifyReady();
  }, function () {
    render([]);
    notifyReady();
  });

  exitEl.addEventListener("click", function () {
    host.callManager("manager.exitApp", {});
  });

  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape") { hide(); }
  });

  document.addEventListener("contextmenu", function (e) {
    e.preventDefault();
    hide();
  });
})();
