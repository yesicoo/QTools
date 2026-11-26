(function () {
  "use strict";
  var maxBytes = 8 * 1024 * 1024;
  var input = Site.byId("package");
  var upload = Site.byId("upload");
  var button = Site.byId("submit-button");
  var fileInfo = Site.byId("file-info");
  var fileError = Site.byId("file-error");
  var consent = Site.byId("consent");
  var validFile = null;
  var busy = false;

  function updateButton() {
    button.disabled = busy || !validFile || !consent.checked;
  }

  function clearFileError() {
    fileError.textContent = "";
    fileError.classList.add("hidden");
  }

  function showFileError(message) {
    fileError.textContent = message;
    fileError.classList.remove("hidden");
  }

  function resetFile() {
    validFile = null;
    input.value = "";
    clearFileError();
    fileInfo.classList.add("hidden");
    upload.classList.remove("hidden");
    updateButton();
  }

  function selectFile(file) {
    clearFileError();
    if (!file) { resetFile(); return; }
    if (!/\.(qtools|zip)$/i.test(file.name)) {
      resetFile();
      showFileError("只支持 .qtools 或 .zip 安装包，请重新选择。");
      return;
    }
    if (file.size > maxBytes) {
      resetFile();
      showFileError("安装包超过 8 MiB，请压缩或精简后重新提交。");
      return;
    }

    validFile = file;
    Site.byId("file-name").textContent = file.name;
    Site.byId("file-size").textContent = Site.formatBytes(file.size) + " · " + (file.name.split(".").pop() || "").toUpperCase() + " 安装包";
    Site.byId("file-state").textContent = "等待提交";
    fileInfo.classList.remove("hidden");
    upload.classList.add("hidden");
    updateButton();
  }

  input.addEventListener("change", function () {
    selectFile(input.files && input.files[0]);
  });
  Site.byId("replace-file").addEventListener("click", function () { input.click(); });
  Site.byId("remove-file").addEventListener("click", resetFile);
  consent.addEventListener("change", updateButton);

  ["dragenter", "dragover"].forEach(function (name) {
    upload.addEventListener(name, function (event) {
      event.preventDefault();
      upload.classList.add("dragging");
    });
  });
  ["dragleave", "drop"].forEach(function (name) {
    upload.addEventListener(name, function (event) {
      event.preventDefault();
      upload.classList.remove("dragging");
    });
  });
  upload.addEventListener("drop", function (event) {
    if (event.dataTransfer.files.length) {
      input.files = event.dataTransfer.files;
      selectFile(input.files[0]);
    }
  });

  Site.byId("submit-form").addEventListener("submit", async function (event) {
    event.preventDefault();
    Site.byId("error").classList.add("hidden");
    clearFileError();
    if (!validFile) {
      showFileError("请先选择要提交的安装包。");
      return;
    }
    if (!consent.checked) {
      showFileError("请确认代码与元数据可以公开后再提交。");
      return;
    }

    busy = true;
    updateButton();
    button.innerHTML = '<span class="button-spinner" aria-hidden="true"></span>正在校验并提交…';
    Site.byId("file-state").textContent = "正在上传并校验…";
    try {
      var data = new FormData();
      data.append("package", validFile);
      var result = await Site.api("/api/v1/submissions", { method: "POST", body: data });
      Site.byId("submission-id").textContent = result.id;
      Site.byId("status-link").href = "/submissions/" + encodeURIComponent(result.id);
      Site.byId("submit-form").classList.add("hidden");
      Site.byId("success").classList.remove("hidden");
      Site.byId("success").focus?.();
    } catch (error) {
      Site.showError(Site.byId("error"), error);
      Site.byId("file-state").textContent = "等待重新提交";
    } finally {
      busy = false;
      button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V4m0 0L8 8m4-4l4 4M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>提交审核';
      updateButton();
    }
  });

  Site.byId("copy-id").addEventListener("click", async function () {
    var text = Site.byId("submission-id").textContent;
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
      } else {
        var area = document.createElement("textarea");
        area.value = text;
        area.style.position = "fixed";
        area.style.opacity = "0";
        document.body.appendChild(area);
        area.select();
        document.execCommand("copy");
        area.remove();
      }
      this.textContent = "已复制";
    } catch (error) {
      this.textContent = "请手动复制";
    }
    setTimeout(() => { this.textContent = "复制"; }, 1600);
  });

  updateButton();
})();
