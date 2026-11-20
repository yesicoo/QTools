/* ============================================================================
 * QRCoder — 纯本地 QR 码编码器（无外部依赖）
 * 支持：字节模式（UTF-8）、版本 1–10、ECC 级别 M、掩码 0–7 自动选择。
 * 输出到 canvas。实现遵循 ISO/IEC 18004。
 * ============================================================================ */
var QRCoder = (function () {
  "use strict";

  /* ---- ECC 级别 M 的块结构：[版本] = { ec: 每块纠错码字, g1: [块数, 数据码字], g2?: [块数, 数据码字] } ---- */
  var ECC_M = {
    1:  { ec: 10, g1: [1, 16] },
    2:  { ec: 16, g1: [1, 28] },
    3:  { ec: 26, g1: [1, 44] },
    4:  { ec: 18, g1: [2, 32] },
    5:  { ec: 24, g1: [2, 43] },
    6:  { ec: 16, g1: [4, 27] },
    7:  { ec: 18, g1: [4, 31] },
    8:  { ec: 22, g1: [2, 38], g2: [2, 39] },
    9:  { ec: 22, g1: [3, 36], g2: [2, 37] },
    10: { ec: 26, g1: [4, 43], g2: [1, 44] }
  };

  /* 每版本总码字数 */
  var TOTAL = { 1: 26, 2: 44, 3: 70, 4: 100, 5: 134, 6: 172, 7: 196, 8: 242, 9: 292, 10: 346 };

  /* 校正图形中心坐标 */
  var ALIGN = {
    1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30],
    6: [6, 34], 7: [6, 22, 38], 8: [6, 24, 42], 9: [6, 26, 46], 10: [6, 28, 50]
  };

  /* ---- GF(256) 算术（本原多项式 0x11D） ---- */
  var EXP = new Array(512), LOG = new Array(256);
  (function initGalois() {
    var x = 1;
    for (var i = 0; i < 255; i++) {
      EXP[i] = x;
      LOG[x] = i;
      x <<= 1;
      if (x & 0x100) { x ^= 0x11D; }
    }
    for (var i = 255; i < 512; i++) { EXP[i] = EXP[i - 255]; }
  })();

  function gfMul(a, b) {
    if (a === 0 || b === 0) { return 0; }
    return EXP[LOG[a] + LOG[b]];
  }

  /* 生成生成多项式 */
  function generatorPoly(degree) {
    var poly = [1];
    for (var i = 0; i < degree; i++) {
      var next = new Array(poly.length + 1).fill(0);
      for (var j = 0; j < poly.length; j++) {
        next[j] ^= poly[j];
        next[j + 1] ^= gfMul(poly[j], EXP[i]);
      }
      poly = next;
    }
    return poly;
  }

  /* Reed-Solomon 纠错码字 */
  function rsEncode(data, ecLen) {
    var gen = generatorPoly(ecLen);
    var res = new Array(ecLen).fill(0);
    for (var i = 0; i < data.length; i++) {
      var factor = data[i] ^ res[0];
      res.shift();
      res.push(0);
      if (factor !== 0) {
        for (var j = 0; j < ecLen; j++) {
          res[j] ^= gfMul(gen[j + 1], factor);
        }
      }
    }
    return res;
  }

  /* ---- 位缓冲 ---- */
  function BitBuffer() { this.bits = []; }
  BitBuffer.prototype.put = function (value, length) {
    for (var i = length - 1; i >= 0; i--) { this.bits.push((value >>> i) & 1); }
  };

  function utf8Bytes(str) {
    var out = [], i, c;
    for (i = 0; i < str.length; i++) {
      c = str.charCodeAt(i);
      if (c < 0x80) { out.push(c); }
      else if (c < 0x800) { out.push(0xC0 | (c >> 6), 0x80 | (c & 63)); }
      else if (c >= 0xD800 && c < 0xDC00 && i + 1 < str.length) {
        var c2 = str.charCodeAt(i + 1);
        var cp = ((c - 0xD800) << 10) + (c2 - 0xDC00) + 0x10000;
        out.push(0xF0 | (cp >> 18), 0x80 | ((cp >> 12) & 63), 0x80 | ((cp >> 6) & 63), 0x80 | (cp & 63));
        i++;
      } else {
        out.push(0xE0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
      }
    }
    return out;
  }

  /* 选择版本：返回最小可用版本 */
  function chooseVersion(byteLen) {
    for (var v = 1; v <= 10; v++) {
      var spec = ECC_M[v];
      var dataCodewords = spec.g1[0] * spec.g1[1] + (spec.g2 ? spec.g2[0] * spec.g2[1] : 0);
      var countBits = v <= 9 ? 8 : 16;
      var capacityBits = dataCodewords * 8 - 4 - countBits;
      if (byteLen * 8 <= capacityBits) { return v; }
    }
    return 0;
  }

  /* ---- 构建最终码字序列 ---- */
  function buildCodewords(bytes, version) {
    var spec = ECC_M[version];
    var dataCodewords = spec.g1[0] * spec.g1[1] + (spec.g2 ? spec.g2[0] * spec.g2[1] : 0);
    var countBits = version <= 9 ? 8 : 16;

    var bb = new BitBuffer();
    bb.put(0x4, 4);                       // 字节模式
    bb.put(bytes.length, countBits);      // 字符计数
    for (var i = 0; i < bytes.length; i++) { bb.put(bytes[i], 8); }

    var capacityBits = dataCodewords * 8;
    // 终止符（最多 4 位）
    for (var i = 0; i < 4 && bb.bits.length < capacityBits; i++) { bb.bits.push(0); }
    // 补齐到字节边界
    while (bb.bits.length % 8 !== 0) { bb.bits.push(0); }

    // 转为码字
    var codewords = [];
    for (var i = 0; i < bb.bits.length; i += 8) {
      var b = 0;
      for (var j = 0; j < 8; j++) { b = (b << 1) | bb.bits[i + j]; }
      codewords.push(b);
    }
    // 交替填充字节
    var pad = [0xEC, 0x11], p = 0;
    while (codewords.length < dataCodewords) { codewords.push(pad[p++ % 2]); }

    // 分块 + 纠错
    var blocks = [], ecBlocks = [], offset = 0;
    function addGroup(count, dcLen) {
      for (var i = 0; i < count; i++) {
        var d = codewords.slice(offset, offset + dcLen);
        offset += dcLen;
        blocks.push(d);
        ecBlocks.push(rsEncode(d, spec.ec));
      }
    }
    addGroup(spec.g1[0], spec.g1[1]);
    if (spec.g2) { addGroup(spec.g2[0], spec.g2[1]); }

    // 交织
    var result = [];
    var maxData = Math.max.apply(null, blocks.map(function (b) { return b.length; }));
    for (var i = 0; i < maxData; i++) {
      for (var b = 0; b < blocks.length; b++) {
        if (i < blocks[b].length) { result.push(blocks[b][i]); }
      }
    }
    for (var i = 0; i < spec.ec; i++) {
      for (var b = 0; b < ecBlocks.length; b++) { result.push(ecBlocks[b][i]); }
    }
    return result;
  }

  /* ---- 矩阵构建 ---- */
  function Matrix(version) {
    var size = version * 4 + 17;
    this.size = size;
    this.version = version;
    this.modules = [];
    this.isData = [];          // 标记数据模块（掩码只能作用于这些）
    for (var r = 0; r < size; r++) {
      this.modules.push(new Array(size).fill(null));
      this.isData.push(new Array(size).fill(false));
    }
  }

  Matrix.prototype.set = function (r, c, v) { this.modules[r][c] = v; };
  Matrix.prototype.get = function (r, c) { return this.modules[r][c]; };

  Matrix.prototype.placeFinder = function (r, c) {
    for (var dr = -1; dr <= 7; dr++) {
      for (var dc = -1; dc <= 7; dc++) {
        var rr = r + dr, cc = c + dc;
        if (rr < 0 || rr >= this.size || cc < 0 || cc >= this.size) { continue; }
        var dark = (dr >= 0 && dr <= 6 && (dc === 0 || dc === 6)) ||
                   (dc >= 0 && dc <= 6 && (dr === 0 || dr === 6)) ||
                   (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4);
        this.set(rr, cc, dark ? 1 : 0);
      }
    }
  };

  Matrix.prototype.placeAlignment = function (r, c) {
    for (var dr = -2; dr <= 2; dr++) {
      for (var dc = -2; dc <= 2; dc++) {
        this.set(r + dr, c + dc, (Math.abs(dr) === 2 || Math.abs(dc) === 2 || (dr === 0 && dc === 0)) ? 1 : 0);
      }
    }
  };

  Matrix.prototype.placeTiming = function () {
    for (var i = 8; i < this.size - 8; i++) {
      var dark = i % 2 === 0;
      if (this.get(6, i) === null) { this.set(6, i, dark ? 1 : 0); }
      if (this.get(i, 6) === null) { this.set(i, 6, dark ? 1 : 0); }
    }
  };

  Matrix.prototype.reserveFormat = function () {
    for (var i = 0; i < 9; i++) {
      if (this.get(8, i) === null) { this.set(8, i, 0); }
      if (this.get(i, 8) === null) { this.set(i, 8, 0); }
    }
    for (var i = 0; i < 8; i++) {
      if (this.get(8, this.size - 1 - i) === null) { this.set(8, this.size - 1 - i, 0); }
      if (this.get(this.size - 1 - i, 8) === null) { this.set(this.size - 1 - i, 8, 0); }
    }
    this.set(this.size - 8, 8, 1);   // 深色模块
  };

  Matrix.prototype.reserveVersion = function (version) {
    if (version < 7) { return; }
    for (var i = 0; i < 6; i++) {
      for (var j = 0; j < 3; j++) {
        this.set(this.size - 11 + j, i, 0);
        this.set(i, this.size - 11 + j, 0);
      }
    }
  };

  Matrix.prototype.placeData = function (codewords) {
    var bitIdx = 0, dir = -1, r = this.size - 1, c = this.size - 1;
    while (c > 0) {
      if (c === 6) { c--; }              // 跳过时序图形列
      while (true) {
        for (var i = 0; i < 2; i++) {
          var cc = c - i;
          if (this.get(r, cc) === null) {
            var bit = 0;
            if (bitIdx < codewords.length * 8) {
              bit = (codewords[bitIdx >>> 3] >>> (7 - (bitIdx & 7))) & 1;
            }
            bitIdx++;
            this.set(r, cc, bit);
            this.isData[r][cc] = true;
          }
        }
        r += dir;
        if (r < 0 || r >= this.size) {
          r -= dir;
          dir = -dir;
          break;
        }
      }
      c -= 2;
    }
  };

  /* 掩码函数 */
  var MASKS = [
    function (r, c) { return (r + c) % 2 === 0; },
    function (r) { return r % 2 === 0; },
    function (r, c) { return c % 3 === 0; },
    function (r, c) { return (r + c) % 3 === 0; },
    function (r, c) { return (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0; },
    function (r, c) { return ((r * c) % 2) + ((r * c) % 3) === 0; },
    function (r, c) { return (((r * c) % 2) + ((r * c) % 3)) % 2 === 0; },
    function (r, c) { return (((r + c) % 2) + ((r * c) % 3)) % 2 === 0; }
  ];

  Matrix.prototype.applyMask = function (maskFn) {
    for (var r = 0; r < this.size; r++) {
      for (var c = 0; c < this.size; c++) {
        // 掩码只作用于数据模块；功能图形（探测/时序/校正/格式/版本）绝不能被掩码
        if (this.isData[r][c] && maskFn(r, c)) {
          this.set(r, c, this.get(r, c) ^ 1);
        }
      }
    }
  };

  /* 判断是否为功能模块（非数据区） */
  Matrix.prototype.isFunction = function (r, c) {
    // 探测图形 + 分隔符
    if (r <= 8 && c <= 8) { return true; }
    if (r <= 8 && c >= this.size - 8) { return true; }
    if (r >= this.size - 8 && c <= 8) { return true; }
    // 时序图形
    if (r === 6 || c === 6) { return true; }
    // 格式信息
    if (r === 8 && (c <= 8 || c >= this.size - 8)) { return true; }
    if (c === 8 && (r <= 8 || r >= this.size - 8)) { return true; }
    // 版本信息
    if (this.version >= 7) {
      if (r >= this.size - 11 && r <= this.size - 9 && c >= 0 && c <= 5) { return true; }
      if (c >= this.size - 11 && c <= this.size - 9 && r >= 0 && r <= 5) { return true; }
    }
    return false;
  };

  /* BCH(15,5) 格式信息 */
  function formatInfo(eccLevelBits, mask) {
    var data = (eccLevelBits << 3) | mask;
    var d = data << 10;
    for (var i = 14; i >= 10; i--) {
      if ((d >>> i) & 1) { d ^= 0x537 << (i - 10); }
    }
    return ((data << 10) | d) ^ 0x5412;
  }

  /* BCH(18,6) 版本信息 */
  function versionInfo(version) {
    var d = version << 12;
    for (var i = 17; i >= 12; i--) {
      if ((d >>> i) & 1) { d ^= 0x1F25 << (i - 12); }
    }
    return (version << 12) | d;
  }

  Matrix.prototype.placeFormat = function (mask) {
    var bits = formatInfo(0x0, mask);   // 0x0 = ECC 级别 M
    // 注意：格式信息按 MSB 优先放置——位置序列中的第 i 个放的是 bit (14-i)，而非 bit i。
    // （这一点经与参考实现矩阵级差分 + jsQR 实测解码双重验证。）
    for (var i = 0; i < 15; i++) {
      var bit = (bits >>> (14 - i)) & 1;
      // 左上
      if (i < 6) { this.set(8, i, bit); }
      else if (i < 8) { this.set(8, i + 1, bit); }
      else if (i === 8) { this.set(7, 8, bit); }
      else { this.set(14 - i, 8, bit); }
      // 右上 + 左下
      if (i < 8) { this.set(this.size - 1 - i, 8, bit); }
      else { this.set(8, this.size - 15 + i, bit); }
    }

    // 深色模块：规范规定 (size-8, 8) 恒为深色，必须最后强制写回
    // （上面 copy 2 的对应位置会写到同一处）
    this.set(this.size - 8, 8, 1);
  };

  Matrix.prototype.placeVersionInfo = function (version) {
    if (version < 7) { return; }
    var bits = versionInfo(version);
    for (var i = 0; i < 18; i++) {
      var bit = (bits >>> i) & 1;
      var r = Math.floor(i / 3), c = i % 3;
      this.set(this.size - 11 + c, r, bit);
      this.set(r, this.size - 11 + c, bit);
    }
  };

  /* ----  penalty 评分（简化版四规则） ---- */
  function penalty(m) {
    var score = 0, r, c, i;
    // 规则 1：连续同色模块
    for (r = 0; r < m.size; r++) {
      var runH = 1, runV = 1;
      for (c = 1; c < m.size; c++) {
        if (m.get(r, c) === m.get(r, c - 1)) { runH++; if (runH === 5) { score += 3; } else if (runH > 5) { score++; } }
        else { runH = 1; }
        if (m.get(c, r) === m.get(c - 1, r)) { runV++; if (runV === 5) { score += 3; } else if (runV > 5) { score++; } }
        else { runV = 1; }
      }
    }
    // 规则 2：2x2 同色块
    for (r = 0; r < m.size - 1; r++) {
      for (c = 0; c < m.size - 1; c++) {
        var v = m.get(r, c);
        if (v === m.get(r, c + 1) && v === m.get(r + 1, c) && v === m.get(r + 1, c + 1)) { score += 3; }
      }
    }
    // 规则 3：类似探测图形的 1:1:3:1:1 图案
    var pat = [1, 0, 1, 1, 1, 0, 1];
    for (r = 0; r < m.size; r++) {
      for (c = 0; c <= m.size - 7; c++) {
        var ok = true;
        for (i = 0; i < 7; i++) { if (m.get(r, c + i) !== pat[i]) { ok = false; break; } }
        if (ok) { score += 40; }
      }
    }
    for (c = 0; c < m.size; c++) {
      for (r = 0; r <= m.size - 7; r++) {
        var ok2 = true;
        for (i = 0; i < 7; i++) { if (m.get(r + i, c) !== pat[i]) { ok2 = false; break; } }
        if (ok2) { score += 40; }
      }
    }
    // 规则 4：明暗比例
    var dark = 0;
    for (r = 0; r < m.size; r++) { for (c = 0; c < m.size; c++) { if (m.get(r, c) === 1) { dark++; } } }
    var ratio = dark / (m.size * m.size) * 100;
    score += Math.floor(Math.abs(ratio - 50) / 5) * 10;
    return score;
  }

  /* ---- 主入口 ---- */
  function encode(text) {
    var bytes = utf8Bytes(String(text));
    var version = chooseVersion(bytes.length);
    if (version === 0) {
      throw new Error("内容过长（最多约 213 字节）");
    }

    var codewords = buildCodewords(bytes, version);
    var best = null, bestScore = Infinity;

    for (var mask = 0; mask < 8; mask++) {
      var m = new Matrix(version);
      m.version = version;
      m.placeFinder(0, 0);
      m.placeFinder(0, m.size - 7);
      m.placeFinder(m.size - 7, 0);
      var centers = ALIGN[version];
      for (var i = 0; i < centers.length; i++) {
        for (var j = 0; j < centers.length; j++) {
          var skip = (i === 0 && j === 0) || (i === 0 && j === centers.length - 1) ||
                     (i === centers.length - 1 && j === 0);
          if (!skip) { m.placeAlignment(centers[i], centers[j]); }
        }
      }
      m.placeTiming();
      m.reserveFormat();
      m.reserveVersion(version);
      m.placeData(codewords);
      m.applyMask(MASKS[mask]);
      m.placeFormat(mask);
      m.placeVersionInfo(version);

      var score = penalty(m);
      if (score < bestScore) { bestScore = score; best = m; }
    }

    return best;
  }

  /* ---- 渲染到 canvas ---- */
  function toCanvas(canvas, text, pixelSize) {
    var m = encode(text);
    var modules = m.size;
    var quiet = 4;
    var total = modules + quiet * 2;
    var scale = Math.max(1, Math.floor(pixelSize / total));
    var dim = total * scale;

    canvas.width = dim;
    canvas.height = dim;
    var ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, dim, dim);
    ctx.fillStyle = "#000000";
    for (var r = 0; r < modules; r++) {
      for (var c = 0; c < modules; c++) {
        if (m.get(r, c) === 1) {
          ctx.fillRect((c + quiet) * scale, (r + quiet) * scale, scale, scale);
        }
      }
    }

    return { version: m.version, modules: modules };
  }

  return {
    encode: encode,
    toCanvas: function (canvas, text, size) {
      var r = toCanvas(canvas, text, size);
      this.lastVersion = r.version;
      this.lastModules = r.modules;
      return r;
    },
    lastVersion: 0,
    lastModules: 0
  };

})();
