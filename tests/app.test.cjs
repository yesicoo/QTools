const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

const ui = `${__dirname}/../src/QTools.Host/Assets/host-ui`;
const source = fs.readFileSync(`${ui}/app.js`, 'utf8');
const html = fs.readFileSync(`${ui}/index.html`, 'utf8');
const flush = () => new Promise(setImmediate);
/* vm 沙箱里创建的对象带另一套原型，deepStrictEqual 前先转成宿主 realm 的纯对象 */
const plain = value => JSON.parse(JSON.stringify(value));

const tools = ['a', 'b'].map(id => ({ id, name: `工具${id}`, version: '1.2.3', isValid: true }));
const item = (over = {}) => ({
  id: 'x', kind: 'program', name: '程序', version: null, iconUrl: null, searchKey: '',
  isValid: true, targetExists: true, useCount: 0, category: null, glyph: null, ...over
});
const items = [
  item({ id: 'a', kind: 'tool', name: '工具a', version: '1.2.3', searchKey: '工具a a', useCount: 5 }),
  item({ id: 'editor', kind: 'app', name: '编辑器', searchKey: '编辑器 editor', useCount: 3, category: '开发', glyph: '</>' }),
  item({ id: 'idx-x', name: '索引程序', searchKey: '索引程序', useCount: 1, iconUrl: '/__host__/app-icon/idx-x' }),
  item({ id: 'gone', name: '已卸载', searchKey: '已卸载', targetExists: false })
];
const indexInfo = { indexing: false, lastIndexedUtc: '2026-01-01T00:00:00Z', programCount: 2 };
const generated = (id = 'a', version = '1.2.4') => ({
  manifest: { id, name: `工具${id}`, version }, indexHtml: `<h1>${id}</h1>`, findings: [],
  hasFatal: false, requiresExplicitAcceptance: true
});

class Element {
  constructor(tag = '') {
    const name = /^<([\w-]+)/.exec(tag);
    this.tagName = name ? name[1].toUpperCase() : '';
    this.children = []; this.dataset = {}; this.handlers = {}; this.className = '';
    this.hidden = /\shidden\b/.test(tag); this.disabled = /\sdisabled\b/.test(tag);
    this.checked = /\schecked\b/.test(tag); this.value = ''; this.src = ''; this.textContent = '';
    this.offsetParent = {}; this.onclick = null; this.files = null;
    const self = this;
    this.classList = {
      add: name => { if (!self.hasClass(name)) self.className += ` ${name}`; },
      remove: name => { self.className = self.className.split(/\s+/).filter(x => x !== name).join(' '); },
      contains: name => self.hasClass(name)
    };
  }
  hasClass(name) { return this.className.split(/\s+/).includes(name); }
  set innerHTML(value) { this.html = value; this.children = []; }
  get innerHTML() { return this.html || ''; }
  appendChild(child) { this.children.push(child); return child; }
  addEventListener(event, handler) { (this.handlers[event] ||= []).push(handler); }
  getAttribute(name) { assert.equal(name, 'src'); return this.src || null; }
  focus() {}
  select() {}
  blur() {}
  click() {
    if (this.disabled) return;
    (this.handlers.click || []).forEach(handler => handler.call(this));
    if (typeof this.onclick === 'function') this.onclick.call(this);
  }
  fire(event, arg) { (this.handlers[event] || []).forEach(handler => handler.call(this, arg)); }
}

async function setup(options = {}) {
  const { mode = 'main', view, toolId, list = items, toolList = tools } = options;
  const search = mode === 'content'
    ? `?mode=content${view ? `&view=${view}` : ''}${toolId ? `&toolId=${toolId}` : ''}`
    : '';
  const nodes = Object.fromEntries([...html.matchAll(/<[\w-]+\b[^>]*\bid="([^"]+)"[^>]*>/g)]
    .map(([tag, id]) => [id, new Element(tag)]));
  const body = new Element('<body>');
  body.className = 'mode-main';
  const el = id => { assert.ok(nodes[id], `页面缺少元素：${id}`); return nodes[id]; };
  const walk = node => [node, ...node.children.flatMap(walk)];
  const all = () => Object.values(nodes).flatMap(walk);
  const query = selector => {
    if (selector === '#nav-list .nav-item') {
      return walk(nodes['nav-list']).filter(node => node.hasClass('nav-item'));
    }
    if (selector === '.nav-item.active') {
      return all().filter(node => node.hasClass('nav-item') && node.hasClass('active'));
    }
    const match = /^\.nav-item\[data-key="([^"]+)"\]$/.exec(selector);
    assert.ok(match, `未实现的选择器：${selector}`);
    return all().filter(node => node.hasClass('nav-item') && node.dataset.key === match[1]);
  };
  const calls = [];
  let streamSeq = 0;
  const host = {
    callManager(method, payload) {
      let resolve, reject;
      const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
      calls.push({ method, payload, resolve, reject });
      return promise;
    },
    callManagerStream(method, payload, onDelta, control) {
      let resolve, reject;
      const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
      control.requestId = `stream-${++streamSeq}`;
      calls.push({ method, payload, onDelta, control, resolve, reject });
      return promise;
    },
    cancelRequest(requestId) {
      calls.push({ method: 'manager.cancelRequest', payload: { requestId } });
      return Promise.resolve();
    }
  };
  host.importTool = payload => host.callManager('manager.importTool', payload);
  const windowHandlers = {};
  const documentHandlers = {};
  const requests = name => calls.filter(call => call.method === `manager.${name}`);
  const last = name => { const call = requests(name).at(-1); assert.ok(call, `缺少请求：${name}`); return call; };
  const reply = async (name, result) => { last(name).resolve(result); await flush(); };
  const click = id => el(id).click();
  const navItem = key => { const node = query(`.nav-item[data-key="${key}"]`)[0]; assert.ok(node, `缺少列表项：${key}`); return node; };
  const embed = async (id, token = '新凭证') => {
    assert.equal(last('embedTool').payload.toolId, id);
    await reply('embedTool', { toolId: id, name: `工具${id}`, permissions: [], url: `/tools/${id}/?bt=${token}` });
  };
  const versions = async () => { click('tool-versions'); await reply('listVersions', { versions: [] }); };
  const view_ = id => {
    for (const name of ['tool-view', 'content-home', 'create-view', 'version-view', 'import-view']) {
      assert.equal(el(name).hidden, name !== id, `视图状态错误：${name}`);
    }
  };
  const fireWindow = (event, arg) => (windowHandlers[event] || []).forEach(handler => handler(arg));
  nodes['update-bump'].value = 'patch';
  vm.runInNewContext(source, {
    window: {
      host, location: { search },
      addEventListener: (event, handler) => { (windowHandlers[event] ||= []).push(handler); }
    },
    document: {
      body, getElementById: id => nodes[id] || null, createElement: tag => new Element(`<${tag}>`),
      querySelectorAll: query, querySelector: selector => query(selector)[0] || null,
      addEventListener: (event, handler) => { (documentHandlers[event] ||= []).push(handler); }
    },
    setTimeout: () => 1, clearTimeout() {}, confirm: options.confirm || (() => true),
    FileReader: class {
      readAsText(file) { this.result = file.html; Promise.resolve().then(() => this.onload()); }
    }
  }, { filename: `${ui}/app.js` });
  if (mode === 'content') {
    await reply('listTools', { tools: toolList });
  } else {
    await reply('launcherList', { items: list, index: indexInfo });
  }
  return { el, click, navItem, requests, last, reply, embed, versions, view: view_, fireWindow, body, nodes };
}

/* ================= 主窗口：统一列表 ================= */

test('主窗口渲染统一列表（小工具+本机程序混排）并显示索引状态', async () => {
  const h = await setup();
  const keys = h.el('nav-list').children.map(li => li.children[0].dataset.key);
  assert.deepEqual(keys, ['tool:a', 'app:editor', 'program:idx-x', 'program:gone']);
  assert.equal(h.el('index-status').textContent, '已索引 2 个本机程序');
  assert.equal(h.el('side-empty').hidden, true);
  assert.ok(h.navItem('program:gone').hasClass('broken'));
  assert.equal(h.navItem('tool:a').children.at(-1).textContent, 'v1.2.3');
  assert.equal(h.navItem('app:editor').children.at(-1).textContent, '开发');
});

test('索引中显示进度文案，空索引显示提示', async () => {
  const h = await setup();
  h.fireWindow('host-event', { detail: 'launcher-changed' });
  await h.reply('launcherList', { items, index: { indexing: true, lastIndexedUtc: null, programCount: 0 } });
  assert.equal(h.el('index-status').textContent, '正在后台索引本机程序…');
  h.fireWindow('host-event', { detail: 'launcher-changed' });
  await h.reply('launcherList', { items, index: { indexing: false, lastIndexedUtc: null, programCount: 0 } });
  assert.equal(h.el('index-status').textContent, '尚未索引到本机程序');
});

test('点击小工具走内容窗口而不是本地内嵌', async () => {
  const h = await setup();
  h.navItem('tool:a').click();
  assert.deepEqual(plain(h.last('openContent').payload), { view: 'tool', toolId: 'a' });
  await h.reply('openContent', { ok: true });
  assert.equal(h.requests('launcherList').length, 2);
  assert.equal(h.requests('embedTool').length, 0);
  assert.equal(h.el('tool-view').hidden, true);
});

test('点击本机程序直接启动并刷新排序', async () => {
  const h = await setup();
  h.navItem('app:editor').click();
  assert.deepEqual(plain(h.last('launchApp').payload), { appId: 'editor' });
  await h.reply('launchApp', { started: true, processId: 123 });
  assert.match(h.el('toast').textContent, /已启动 编辑器（PID 123）/);
  assert.equal(h.requests('launcherList').length, 2);
  assert.equal(h.requests('openContent').length, 0);
});

test('目标不存在的程序点击只提示不启动', async () => {
  const h = await setup();
  h.navItem('program:gone').click();
  assert.equal(h.requests('launchApp').length, 0);
  assert.match(h.el('toast').className, /\berr\b/);
  assert.match(h.el('toast').textContent, /目标不存在/);
});

test('manifest 无效的工具点击只提示不打开', async () => {
  const h = await setup({ list: [item({ id: 'bad', kind: 'tool', name: '坏工具', isValid: false })] });
  h.navItem('tool:bad').click();
  assert.equal(h.requests('openContent').length, 0);
  assert.match(h.el('toast').textContent, /manifest 无效/);
  assert.ok(h.navItem('tool:bad').hasClass('broken'));
});

test('主窗口底部创建/导入按钮打开内容窗口对应页面', async () => {
  const h = await setup();
  h.click('create-tool');
  assert.deepEqual(plain(h.last('openContent').payload), { view: 'create' });
  h.click('import-tool');
  assert.deepEqual(plain(h.last('openContent').payload), { view: 'import' });
  assert.equal(h.el('create-view').hidden, true);
  assert.equal(h.el('import-view').hidden, true);
});

test('搜索过滤统一列表，无匹配时显示空状态', async () => {
  const h = await setup();
  const search = h.el('search');
  search.value = '编辑器';
  search.fire('input');
  assert.deepEqual(h.el('nav-list').children.map(li => li.children[0].dataset.key), ['app:editor']);
  search.value = 'zzz不存在';
  search.fire('input');
  assert.equal(h.el('nav-list').children.length, 0);
  assert.equal(h.el('side-empty').hidden, false);
});

test('列表刷新失败保留已有列表并显示错误', async () => {
  const h = await setup();
  const before = h.el('nav-list').children.map(li => li.children[0].dataset.key);
  h.fireWindow('focus');
  h.last('launcherList').reject({ code: 'TEST_ERROR', message: '查询失败' });
  await flush();
  assert.deepEqual(h.el('nav-list').children.map(li => li.children[0].dataset.key), before);
  assert.match(h.el('toast').className, /\berr\b/);
  assert.match(h.el('toast').textContent, /读取列表失败.*TEST_ERROR/);
});

test('launcher-changed 与 focus 事件都触发主窗口刷新', async () => {
  const h = await setup();
  h.fireWindow('host-event', { detail: 'launcher-changed' });
  await h.reply('launcherList', { items, index: indexInfo });
  assert.equal(h.requests('launcherList').length, 2);
  h.fireWindow('focus');
  await h.reply('launcherList', { items, index: indexInfo });
  assert.equal(h.requests('launcherList').length, 3);
  h.fireWindow('host-event', { detail: '别的事件' });
  await flush();
  assert.equal(h.requests('launcherList').length, 3);
});

test('右键本机程序移除启动项（不卸载系统程序）', async () => {
  const h = await setup();
  let prevented = false;
  h.navItem('app:editor').fire('contextmenu', { preventDefault() { prevented = true; } });
  assert.ok(prevented);
  assert.deepEqual(plain(h.last('deleteApp').payload), { appId: 'editor' });
  await h.reply('deleteApp', { ok: true });
  assert.match(h.el('toast').textContent, /已移除 编辑器/);
  assert.equal(h.requests('launcherList').length, 2);
});

test('右键小工具删除工具并刷新列表', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  assert.deepEqual(plain(h.last('deleteTool').payload), { toolId: 'a', deleteStorage: true });
  await h.reply('deleteTool', { ok: true });
  assert.equal(h.requests('launcherList').length, 2);
  assert.equal(h.requests('closeContent').length, 0);
});

/* ================= 内容窗口：路由与内嵌 ================= */

test('内容窗口按 URL 直接内嵌工具并切换 body 模式', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  assert.ok(h.body.hasClass('mode-content'));
  assert.ok(!h.body.hasClass('mode-main'));
  await h.embed('a');
  h.view('tool-view');
  assert.equal(h.el('tool-frame').src, '/tools/a/?bt=新凭证');
  assert.equal(h.el('tool-name').textContent, '工具a');
  assert.match(h.el('tool-meta').textContent, /^v1\.2\.3/);
});

for (const [view, panel] of [['create', 'create-view'], ['import', 'import-view'], [undefined, 'content-home']]) {
  test(`内容窗口 view=${view || '无'} 路由到 ${panel}`, async () => {
    const h = await setup({ mode: 'content', view });
    h.view(panel);
    assert.equal(h.requests('embedTool').length, 0);
  });
}

test('内容窗口收起/取消都调用 closeContent', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  h.click('tool-close');
  assert.equal(h.requests('closeContent').length, 1);

  const c = await setup({ mode: 'content', view: 'create' });
  c.click('create-cancel');
  assert.equal(c.requests('closeContent').length, 1);

  const i = await setup({ mode: 'content', view: 'import' });
  i.click('import-cancel');
  assert.equal(i.requests('closeContent').length, 1);
});

test('内容窗口里 launcher-changed/focus 不触发主窗口式刷新', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  h.fireWindow('host-event', { detail: 'launcher-changed' });
  h.fireWindow('focus');
  await flush();
  assert.equal(h.requests('launcherList').length, 0);
});

test('版本管理关闭后重新申请内嵌凭证恢复工具', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  const before = h.el('tool-frame').src;
  await h.versions();
  h.view('version-view');
  assert.equal(h.el('tool-frame').src, 'about:blank');
  h.click('version-close');
  assert.equal(h.requests('embedTool').length, 2);
  await h.embed('a', '返回凭证');
  h.view('tool-view');
  assert.equal(h.el('tool-frame').src, '/tools/a/?bt=返回凭证');
  assert.notEqual(h.el('tool-frame').src, before);
});

test('快速刷新两次时反序响应只展示最新凭证', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  h.click('tool-reload');
  const old = h.last('embedTool');
  h.click('tool-reload');
  await h.embed('a', '新凭证');
  old.resolve({ toolId: 'a', name: '工具a', permissions: [], url: '/tools/a/?bt=旧凭证' });
  await flush();
  h.view('tool-view');
  assert.equal(h.el('tool-frame').src, '/tools/a/?bt=新凭证');
});

test('内容窗口删除当前工具后关闭窗口', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  h.click('tool-delete');
  assert.deepEqual(plain(h.last('deleteTool').payload), { toolId: 'a', deleteStorage: true });
  await h.reply('deleteTool', { ok: true });
  assert.equal(h.requests('closeContent').length, 1);
});

/* ================= AI 创建 / 更新 / 导入（内容窗口） ================= */

test('AI 创建流式输出可取消，取消后不报错', async () => {
  const h = await setup({ mode: 'content', view: 'create' });
  h.el('create-prompt').value = '做一个待办清单';
  h.click('create-generate');
  const stream = h.last('generateTool');
  assert.equal(h.el('create-stream').hidden, false);
  stream.onDelta('第一段');
  assert.equal(h.el('create-stream-text').textContent, '第一段');
  h.click('create-stream-cancel');
  assert.deepEqual(h.last('cancelRequest').payload, { requestId: stream.control.requestId });
  assert.equal(h.el('create-stream-cancel').disabled, true);
  stream.reject({ code: 'AI_CANCELLED', message: '已取消' });
  await flush();
  assert.equal(h.el('create-status').textContent, '已取消生成');
  assert.equal(h.el('create-generate').disabled, false);
  assert.equal(h.el('create-result').hidden, true);
});

test('AI 创建新一轮清除旧结果和确认', async () => {
  const h = await setup({ mode: 'content', view: 'create' });
  h.el('create-prompt').value = '增加搜索';
  h.click('create-generate');
  await h.reply('generateTool', generated());
  assert.equal(h.el('create-result').hidden, false);
  h.el('create-accept').checked = true;
  h.click('create-generate');
  assert.equal(h.el('create-result').hidden, true);
  assert.equal(h.el('create-accept').checked, false);
  h.last('generateTool').reject({ message: '第二轮失败' });
  await flush();
  assert.equal(h.el('create-result').hidden, true);
  h.click('create-save');
  assert.equal(h.requests('createTool').length, 0);
});

test('创建保存禁用重复点击并内嵌保存后的工具', async () => {
  const h = await setup({ mode: 'content', view: 'create' });
  h.el('create-prompt').value = '新工具';
  h.click('create-generate');
  await h.reply('generateTool', generated('c', '1.0.0'));
  h.el('create-accept').checked = true;
  h.click('create-save');
  assert.equal(h.el('create-save').disabled, true);
  h.click('create-save');
  assert.equal(h.requests('createTool').length, 1);
  await h.reply('createTool', { toolId: 'c' });
  assert.equal(h.requests('listTools').length, 2);
  await h.reply('listTools', { tools: [...tools, { id: 'c', name: '工具c', version: '9.7.5', isValid: true }] });
  await h.embed('c');
  h.view('tool-view');
  assert.match(h.el('tool-meta').textContent, /^v9\.7\.5/);
});

test('关闭版本管理后忽略旧 AI 更新响应', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  await h.versions();
  h.click('version-update');
  h.el('update-instruction').value = '旧更新';
  h.click('update-generate');
  h.click('version-close');
  await h.embed('a');
  await h.reply('generateUpdate', generated());
  h.view('tool-view');
  assert.equal(h.el('update-result').hidden, true);
  assert.equal(h.el('update-html').textContent, '');
  h.click('update-apply');
  assert.equal(h.requests('applyUpdate').length, 0);
});

test('更新应用后刷新版本列表与工具清单', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.embed('a');
  await h.versions();
  h.click('version-update');
  h.el('update-instruction').value = '升级';
  h.click('update-generate');
  await h.reply('generateUpdate', generated());
  h.el('update-accept').checked = true;
  h.click('update-apply');
  assert.equal(h.el('update-apply').disabled, true);
  h.click('update-apply');
  assert.equal(h.requests('applyUpdate').length, 1);
  await h.reply('applyUpdate', {});
  assert.equal(h.requests('listVersions').length, 2);
  assert.equal(h.requests('listTools').length, 2);
  await h.reply('listVersions', { versions: [{ version: '3.2.1', isActive: true, sizeBytes: 1024, createdAt: '2026-01-01' }] });
  await h.reply('listTools', { tools: tools.map(t => t.id === 'a' ? { ...t, version: '3.2.1' } : t) });
  assert.equal(h.el('version-list').children[0].children[0].textContent, 'v3.2.1（运行中）');
  h.click('version-close');
  await h.embed('a');
  h.view('tool-view');
  assert.match(h.el('tool-meta').textContent, /^v3\.2\.1/);
});

test('重新打开导入面板会清空上次选择的文件', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  h.el('import-file').value = 'old.html';
  h.click('import-cancel');
  assert.equal(h.requests('closeContent').length, 1);
  const h2 = await setup({ mode: 'content', view: 'import' });
  assert.equal(h2.el('import-file').value, '');
  assert.equal(h2.el('import-file-name').textContent, '未选择文件');
});

test('读取文件及导入期间禁用重复提交并锁定审查输入', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  h.el('import-name').value = '导入测试';
  h.el('import-file').files = [{ name: 'test.html', html: '<h1>测试</h1>' }];
  h.click('import-run');
  assert.equal(h.el('import-run').disabled, true);
  h.click('import-run');
  await flush();
  assert.equal(h.requests('importTool').length, 1);
  assert.equal(h.el('import-file').disabled, true);
  assert.equal(h.el('import-confirm').disabled, true);
  await h.reply('importTool', { saved: false, findings: [], permissions: [], toolId: 'test' });
  assert.equal(h.el('import-run').disabled, false);
  assert.equal(h.el('import-review').hidden, false);
  h.click('import-confirm');
  h.click('import-confirm');
  assert.equal(h.requests('importTool').length, 2);
  assert.equal(h.last('importTool').payload.acceptHighSeverity, true);
  await h.reply('importTool', { saved: true, toolId: 'test', toolName: '导入测试' });
  assert.equal(h.el('import-run').disabled, false);
  assert.equal(h.requests('listTools').length, 2);
});

for (const id of ['import-name', 'import-desc', 'import-file']) {
  test(`修改${id}后隐藏旧审查并禁止直接确认`, async () => {
    const h = await setup({ mode: 'content', view: 'import' });
    h.el('import-name').value = '旧名称';
    h.el('import-file').files = [{ name: 'test.html', html: '<h1>测试</h1>', size: 20 }];
    h.click('import-run');
    await flush();
    await h.reply('importTool', { saved: false, findings: [], permissions: [], toolId: 'test' });
    const input = h.el(id);
    input.fire(id === 'import-file' ? 'change' : 'input');
    assert.equal(h.el('import-review').hidden, true);
    h.click('import-confirm');
    assert.equal(h.requests('importTool').length, 1);
  });
}
