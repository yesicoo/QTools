"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");

const samplePath = `${__dirname}/../src/QTools.Host/Assets/samples/qrcode-generator`;
const qrScript = new vm.Script(fs.readFileSync(`${samplePath}/qrcode.js`, "utf8"), {
  filename: `${samplePath}/qrcode.js`
});
const html = fs.readFileSync(`${samplePath}/index.html`, "utf8");

// QR 字节模式、纠错级别 M 的标准容量；不复刻产品的版本选择或编码算法。
const capacities = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];
const finder = ["1111111", "1000001", "1011101", "1011101", "1011101", "1000001", "1111111"];

function createCoder() {
  const context = vm.createContext({});
  qrScript.runInContext(context, { timeout: 1000 });
  return context.QRCoder;
}

function assertMatrix(matrix, version) {
  const size = 17 + version * 4;
  assert.equal(matrix.version, version, "应选择能容纳文本的最小版本");
  assert.equal(matrix.size, size);
  assert.equal(matrix.modules.length, size);
  for (let row = 0; row < size; row += 1) {
    assert.equal(matrix.modules[row].length, size, "模块矩阵必须为正方形");
    for (let column = 0; column < size; column += 1) {
      const value = matrix.modules[row][column];
      assert.ok(value === 0 || value === 1, "模块必须已填充为 0 或 1");
      assert.equal(matrix.get(row, column), value);
    }
  }
  // 固定探测图形是规范不变量，不依赖纠错、掩码选择或数据布局的实现。
  for (const [top, left] of [[0, 0], [0, size - 7], [size - 7, 0]]) {
    for (let row = 0; row < 7; row += 1) {
      assert.equal(matrix.modules[top + row].slice(left, left + 7).join(""), finder[row]);
    }
  }
  assert.equal(matrix.get(size - 8, 8), 1, "固定深色模块不能被掩码覆盖");
}

function snapshot(matrix) {
  return Array.from(matrix.modules, row => Array.from(row));
}

class Element {
  constructor(tag = "") {
    this.hidden = /\shidden\b/.test(tag);
    this.disabled = /\sdisabled\b/.test(tag);
    this.checked = /\schecked\b/.test(tag);
    this.value = /\bvalue="([^"]*)"/.exec(tag)?.[1] || "";
    this.textContent = "";
    this.className = /\bclass="([^"]*)"/.exec(tag)?.[1] || "";
    this.handlers = new Map();
  }
  addEventListener(type, callback) {
    const callbacks = this.handlers.get(type) || [];
    callbacks.push(callback);
    this.handlers.set(type, callbacks);
  }
  click() {
    const callbacks = this.handlers.get("click") || [];
    assert.ok(callbacks.length > 0, "页面按钮必须已绑定真实点击事件");
    if (!this.disabled) callbacks.forEach(callback => callback.call(this, { target: this }));
  }
}

// 仅模拟画布绘图接口，编码和页面处理函数始终使用磁盘上的真实脚本。
class Canvas extends Element {
  constructor(tag = "") {
    super(tag);
    this.draws = [];
    this.exports = [];
    this.context = {
      fillStyle: "#000000",
      fillRect: (x, y, width, height) => {
        this.draws.push({ color: this.context.fillStyle, x, y, width, height });
      }
    };
    this.width = Number(/\bwidth="(\d+)"/.exec(tag)?.[1] || 300);
    this.height = Number(/\bheight="(\d+)"/.exec(tag)?.[1] || 150);
  }
  // 浏览器在修改任一画布尺寸时都会清空位图并重置绘图状态。
  reset() { this.draws = []; this.context.fillStyle = "#000000"; }
  set width(value) { this._width = value; this.reset(); }
  get width() { return this._width; }
  set height(value) { this._height = value; this.reset(); }
  get height() { return this._height; }
  getContext(type) { assert.equal(type, "2d"); return this.context; }
  toDataURL(type) {
    this.exports.push(type);
    // 此处只观察下载行为，不声称验证浏览器的 PNG 编码器。
    return "data:image/png;base64,AA==";
  }
}

function assertCanvas(canvas, matrix, requestedSize) {
  const total = matrix.size + 8;
  const scale = canvas.width / total;
  assert.ok(Number.isInteger(scale) && scale >= 1, "每模块至少一个完整像素");
  assert.equal(scale, Math.max(1, Math.floor(requestedSize / total)), "应按整数倍缩放");
  assert.equal(canvas.width, total * scale);
  assert.equal(canvas.height, canvas.width);
  assert.deepEqual(canvas.draws[0], {
    color: "#ffffff", x: 0, y: 0, width: canvas.width, height: canvas.height
  }, "必须先绘制不透明白底");

  const pixels = new Uint8Array(canvas.width * canvas.height).fill(2);
  for (const [index, draw] of canvas.draws.entries()) {
    const { x, y, width, height, color } = draw;
    for (const value of [x, y, width, height]) assert.ok(Number.isInteger(value), "绘图坐标不能是小数");
    assert.ok(x >= 0 && y >= 0 && width > 0 && height > 0);
    assert.ok(x + width <= canvas.width && y + height <= canvas.height, "绘图不能超出画布");
    if (index > 0) {
      assert.equal(color, "#000000");
      assert.equal(width, scale);
      assert.equal(height, scale);
      assert.equal(x % scale, 0);
      assert.equal(y % scale, 0);
    }
    const value = color === "#ffffff" ? 0 : 1;
    for (let row = y; row < y + height; row += 1) {
      pixels.fill(value, row * canvas.width + x, row * canvas.width + x + width);
    }
  }
  const darkModules = matrix.modules.reduce((sum, row) => sum + row.reduce((a, b) => a + b, 0), 0);
  assert.equal(canvas.draws.length, darkModules + 1, "每个深色模块应恰好绘制一次");

  // 核对实际绘图记录覆盖的所有像素，包括四边各四模块的白边与内部明暗模块。
  // 预期模块来自真实 encode 返回值，而不是在测试中重写编码器。
  for (let y = 0; y < canvas.height; y += 1) {
    for (let x = 0; x < canvas.width; x += 1) {
      const row = Math.floor(y / scale) - 4;
      const column = Math.floor(x / scale) - 4;
      const inside = row >= 0 && row < matrix.size && column >= 0 && column < matrix.size;
      const expected = inside ? matrix.get(row, column) : 0;
      if (pixels[y * canvas.width + x] !== expected) {
        assert.equal(pixels[y * canvas.width + x], expected, `画布像素 (${x}, ${y}) 与模块或白边不符`);
      }
    }
  }
}

for (const [name, text, bytes, version] of [
  ["ASCII 文本", "Hello, QTools!", 14, 1],
  ["中文文本", "你好，世界", 15, 2],
  ["混合 UTF-8 文本", "Aé汉\u{1D11E}", 10, 1],
  ["UTF-8 双字节边界", "é".repeat(7), 14, 1],
  ["UTF-8 双字节跨版本", "é".repeat(7) + "a", 15, 2],
  ["UTF-8 三字节边界", "汉".repeat(4) + "ab", 14, 1],
  ["UTF-8 四字节边界", "\u{1D11E}".repeat(3) + "ab", 14, 1],
  ["UTF-8 四字节跨版本", "\u{1D11E}".repeat(3) + "abc", 15, 2],
  ["中文最大容量", "汉".repeat(71), 213, 10],
  ["四字节字符最大容量", "\u{1D11E}".repeat(53) + "a", 213, 10]
]) {
  test(`${name}按 UTF-8 字节数生成有效模块矩阵`, () => {
    assert.equal(Buffer.byteLength(text, "utf8"), bytes, "测试样本的字节数必须准确");
    assertMatrix(createCoder().encode(text), version);
  });
}

test("相同文本编码确定且不同 UTF-8 内容不会被当成相同文本", () => {
  const coder = createCoder();
  const text = "Aé汉\u{1D11E}";
  const first = coder.encode(text);
  const original = snapshot(first);
  assert.deepEqual(snapshot(coder.encode(text)), original);
  assert.notDeepEqual(snapshot(coder.encode("Bé汉\u{1D11E}")), original);
  assert.notDeepEqual(snapshot(coder.encode("Aè汉\u{1D11E}")), original);
  assert.notDeepEqual(snapshot(coder.encode("Aé字\u{1D11E}")), original);
  assert.notDeepEqual(snapshot(coder.encode("Aé汉\u{1D11F}")), original);
  assert.deepEqual(snapshot(first), original, "后续编码不能修改先前返回的矩阵");
});

for (const [index, maximum] of capacities.entries()) {
  const version = index + 1;
  test(`版本 ${version} 的容量上下边界与溢出选择`, () => {
    const coder = createCoder();
    const minimum = index === 0 ? 1 : capacities[index - 1] + 1;
    for (const length of new Set([minimum, maximum - 1, maximum])) {
      assertMatrix(coder.encode("a".repeat(length)), version);
    }
    if (version < 10) assertMatrix(coder.encode("a".repeat(maximum + 1)), version + 1);
    else assert.throws(() => coder.encode("a".repeat(maximum + 1)), /内容过长/);
  });

  test(`版本 ${version} 的 toCanvas 返回值、最近版本和模块像素不随目标尺寸改变`, () => {
    const coder = createCoder();
    const text = "a".repeat(maximum);
    const matrix = coder.encode(text);
    const original = snapshot(matrix);
    const canvas = new Canvas();
    // 包含不足一模块全宽、常用尺寸及不能整除模块总数的尺寸。
    for (const requestedSize of [1, 120, 320, 640]) {
      const result = coder.toCanvas(canvas, text, requestedSize);
      assert.equal(result.version, version, "返回的是 QR 版本，不是掩码编号");
      assert.equal(result.modules, matrix.size, "返回的是每边模块数，不是像素数");
      assert.equal(result.version, (result.modules - 17) / 4);
      assert.equal(coder.lastVersion, result.version, "最近版本不能用画布宽度反推");
      assert.equal(coder.lastModules, result.modules);
      assertCanvas(canvas, matrix, requestedSize);
    }
    assert.deepEqual(snapshot(matrix), original);
  });
}

test("同一编码器先绘制版本 10 再绘制版本 1 会更新最近版本及模块数", () => {
  const coder = createCoder();
  const canvas = new Canvas();
  assert.equal(coder.lastVersion, 0);
  assert.equal(coder.lastModules, 0);
  for (const [text, version, size] of [["a".repeat(213), 10, 120], ["a", 1, 640]]) {
    const result = coder.toCanvas(canvas, text, size);
    assert.equal(result.version, version);
    assert.equal(coder.lastVersion, version);
    assert.equal(coder.lastModules, 17 + version * 4);
  }
});

for (const [name, text] of [
  ["ASCII 超过 213 字节", "a".repeat(214)],
  ["中文超过 213 字节", "汉".repeat(72)],
  ["四字节字符超过容量", "\u{1D11E}".repeat(54)],
  ["远超容量的文本", "a".repeat(10000)]
]) {
  test(`${name}时 encode 和 toCanvas 都明确抛错而非引用错误`, () => {
    const coder = createCoder();
    const canvas = new Canvas();
    assert.throws(() => coder.encode(text), /内容过长/);
    assert.throws(() => coder.toCanvas(canvas, text, 320), /内容过长/);
    assert.equal(canvas.draws.length, 0, "容量校验失败不得绘制部分二维码");
  });
}

function createPage() {
  const nodes = Object.fromEntries([...html.matchAll(/<([\w-]+)\b[^>]*\bid="([^"]+)"[^>]*>/g)]
    .map(([tag, name, id]) => [id, name === "canvas" ? new Canvas(tag) : new Element(tag)]));
  const downloads = [];
  const document = {
    getElementById: id => nodes[id] || null,
    createElement(name) {
      assert.equal(name, "a", "无宿主模式仅需创建下载链接");
      const link = { download: "", href: "", click() {
        downloads.push({ download: this.download, href: this.href });
      } };
      return link;
    }
  };
  // 不提供 window.host，使用页面已支持的无宿主模式，避免无关存储及异步行为。
  const context = vm.createContext({ document });
  context.window = context;
  let externalCount = 0;
  let inlineCount = 0;
  for (const [, attributes, body] of html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)) {
    const src = /\bsrc="([^"]+)"/.exec(attributes)?.[1];
    if (src) {
      assert.equal(src, "qrcode.js");
      qrScript.runInContext(context, { timeout: 1000 });
      externalCount += 1;
    } else if (body.trim()) {
      vm.runInContext(body, context, { filename: `${samplePath}/index.html`, timeout: 1000 });
      inlineCount += 1;
    }
  }
  assert.equal(externalCount, 1, "页面必须加载真实二维码脚本");
  assert.ok(inlineCount > 0, "必须执行页面真实内联脚本而不是复制 render");
  const el = id => { assert.ok(nodes[id], `页面缺少元素：${id}`); return nodes[id]; };
  return {
    el, downloads, coder: context.QRCoder,
    generate(text, size = "320") {
      el("text").value = text;
      el("size").value = size;
      el("gen").click();
    }
  };
}

function assertPageSuccess(page, version) {
  const canvas = page.el("qr");
  const modules = 17 + 4 * version;
  assert.equal(canvas.hidden, false, page.el("status").textContent);
  assert.equal(page.el("status").textContent, "已生成");
  assert.equal(page.el("status").className, "ok");
  assert.equal(page.el("meta").textContent,
    `版本 ${version} · 模块 ${modules} × ${modules} · 尺寸 ${canvas.width} px`);
}

test("页面初始隐藏画布且不能下载尚未生成的二维码", () => {
  const page = createPage();
  assert.equal(page.el("qr").hidden, true);
  assert.equal(page.el("meta").textContent, "");
  page.el("dl").click();
  assert.deepEqual(page.downloads, []);
  assert.deepEqual(page.el("qr").exports, []);
  assert.equal(page.el("status").className, "err");
  assert.match(page.el("status").textContent, /请先生成/);
});

test("页面显示整数缩放后的真实像素尺寸并允许下载 PNG", () => {
  const page = createPage();
  for (const [text, version, requested, actual] of [["a", 1, "320", 319], ["汉".repeat(71), 10, "640", 585]]) {
    page.generate(text, requested);
    assert.equal(page.el("qr").width, actual);
    assert.equal(page.el("qr").height, actual);
    assert.notEqual(actual, Number(requested), "测试必须区分请求尺寸与实际尺寸");
    assertPageSuccess(page, version);
    page.el("dl").click();
    assert.equal(page.downloads.at(-1).download, "qrcode.png");
    assert.equal(page.downloads.at(-1).href, "data:image/png;base64,AA==");
  }
  assert.equal(page.downloads.length, 2);
  assert.deepEqual(page.el("qr").exports, ["image/png", "image/png"]);
});

for (const [name, text, error] of [
  ["空输入", "", /请输入内容/],
  ["仅空白输入", " \t\n　", /请输入内容/],
  ["超长 ASCII 输入", "a".repeat(214), /内容过长/],
  ["超长中文输入", "汉".repeat(72), /内容过长/]
]) {
  test(`成功后${name}清除旧画布显示及元信息、禁止下载且可重新生成`, () => {
    const page = createPage();
    page.generate("旧二维码");
    assertPageSuccess(page, 1);
    page.el("dl").click();
    assert.equal(page.downloads.length, 1);

    page.generate(text);
    assert.equal(page.el("qr").hidden, true, "失败后不得继续展示上一次二维码");
    assert.equal(page.el("meta").textContent, "", "失败后不得保留上一次版本及尺寸");
    assert.equal(page.el("status").className, "err");
    assert.match(page.el("status").textContent, error);
    page.el("dl").click();
    assert.equal(page.downloads.length, 1, "失败后不得下载旧二维码");
    assert.deepEqual(page.el("qr").exports, ["image/png"], "失败后不得导出旧位图");
    assert.match(page.el("status").textContent, /请先生成/);

    page.generate("你好，世界");
    assertPageSuccess(page, 2);
    page.el("dl").click();
    assert.equal(page.downloads.length, 2, "重新成功生成后应恢复下载");
  });
}

test("页面重新编码前先隐藏旧画布并清空旧元信息", () => {
  const page = createPage();
  page.generate("旧二维码");
  assertPageSuccess(page, 1);
  const original = page.coder.toCanvas;
  const observations = [];
  // 只观察调用入口，仍然完整执行真实 toCanvas，不替换其结果或异常。
  page.coder.toCanvas = function (...args) {
    observations.push({ hidden: page.el("qr").hidden, meta: page.el("meta").textContent });
    return original.apply(this, args);
  };
  page.generate("新二维码");
  assert.deepEqual(observations, [{ hidden: true, meta: "" }]);
  assertPageSuccess(page, 1);
});
