const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

const ui = `${__dirname}/../src/QTools.Host/Assets/host-ui`;
const source = fs.readFileSync(`${ui}/app.js`, 'utf8');
const html = fs.readFileSync(`${ui}/index.html`, 'utf8');
const themeSource = fs.readFileSync(`${ui}/theme.js`, 'utf8');
const toolThemeSource = fs.readFileSync(`${ui}/tool-theme.js`, 'utf8');
const flush = () => new Promise(setImmediate);
/* vm 沙箱里创建的对象带另一套原型，deepStrictEqual 前先转成宿主 realm 的纯对象 */
const plain = value => JSON.parse(JSON.stringify(value));
/* 列表条目固定为 图标/名称/类型/详情(badge)/使用(uses)，表格列位依赖这些节点始终存在 */
const badgeOf = node => node.children.find(c => c.hasClass('nav-badge'));

const tools = ['a', 'b'].map(id => ({ id, name: `工具${id}`, version: '1.2.3', isValid: true }));
const item = (over = {}) => ({
  id: 'x', kind: 'program', name: '程序', version: null, iconUrl: null, searchKey: '',
  isValid: true, targetExists: true, useCount: 1, category: null, glyph: null, ...over
});
const items = [
  item({ id: 'a', kind: 'tool', name: '工具a', version: '1.2.3', searchKey: '工具a a', useCount: 5 }),
  item({ id: 'editor', kind: 'app', name: '编辑器', searchKey: '编辑器 editor', useCount: 3, category: '开发', glyph: '</>' }),
  item({ id: 'idx-x', name: '索引程序', searchKey: '索引程序', useCount: 1, iconUrl: '/__host__/app-icon/idx-x' }),
  item({ id: 'gone', name: '已卸载', searchKey: '已卸载', useCount: 0, targetExists: false })
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
    this.style = {}; this.offsetWidth = 0; this.offsetHeight = 0;
    const self = this;
    this.classList = {
      add: name => { if (!self.hasClass(name)) self.className += ` ${name}`; },
      remove: name => { self.className = self.className.split(/\s+/).filter(x => x !== name).join(' '); },
      contains: name => self.hasClass(name)
    };
  }
  hasClass(name) { return this.className.split(/\s+/).includes(name); }
  contains(node) {
    const walk = n => n ? [n, ...n.children.flatMap(walk)] : [];
    return walk(this).includes(node);
  }
  set innerHTML(value) { this.html = value; this.children = []; }
  get innerHTML() { return this.html || ''; }
  appendChild(child) { this.children.push(child); return child; }
  addEventListener(event, handler) { (this.handlers[event] ||= []).push(handler); }
  getAttribute(name) { assert.equal(name, 'src'); return this.src || null; }
  getBoundingClientRect() { return { left: 12, top: 20, right: 300, bottom: 46, width: 288, height: 26 }; }
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
  /* 导入页：原生对话框由后端弹出，测试里直接回填选择结果 */
  const pickFile = async payload => { click('import-pick'); await reply('pickImportFile', payload); };
  const view_ = id => {
    for (const name of ['tool-view', 'content-home', 'create-view', 'version-view', 'import-view', 'web-view']) {
      assert.equal(el(name).hidden, name !== id, `视图状态错误：${name}`);
    }
  };
  const fireWindow = (event, arg) => (windowHandlers[event] || []).forEach(handler => handler(arg));
  const fireDocument = (event, arg) => (documentHandlers[event] || []).forEach(handler => handler(arg));
  /* 右键原位菜单：条目按钮是 li.children[0]，菜单是 li 里带 .nav-menu 的兄弟节点 */
  const liOf = key => nodes['nav-list'].children.find(li => li.children[0] === navItem(key));
  const menuOf = li => li.children.find(c => c.hasClass('nav-menu'));
  const menuAction = (li, index) => menuOf(li).children[0].children[index];
  nodes['update-bump'].value = 'patch';
  vm.runInNewContext(source, {
    window: {
      host, location: { search }, innerWidth: 360, innerHeight: 720,
      addEventListener: (event, handler) => { (windowHandlers[event] ||= []).push(handler); }
    },
    document: {
      body, getElementById: id => nodes[id] || null, createElement: tag => new Element(`<${tag}>`),
      querySelectorAll: query, querySelector: selector => query(selector)[0] || null,
      addEventListener: (event, handler) => { (documentHandlers[event] ||= []).push(handler); }
    },
    setTimeout: () => 1, clearTimeout() {}, confirm: options.confirm || (() => true)
  }, { filename: `${ui}/app.js` });
  if (mode === 'content') {
    await reply('listTools', { tools: toolList });
  } else {
    await reply('launcherList', { items: list, index: indexInfo });
  }
  return { el, click, navItem, requests, last, reply, embed, versions, pickFile, view: view_, fireWindow, fireDocument, liOf, menuOf, menuAction, body, nodes };
}

/* ================= 主窗口：统一列表 ================= */

test('主窗口渲染统一列表（小工具+本机程序混排）并显示索引状态', async () => {
  const h = await setup();
  const keys = h.el('nav-list').children.map(li => li.children[0].dataset.key);
  assert.deepEqual(keys, ['tool:a', 'app:editor', 'program:idx-x']);
  assert.equal(h.el('index-status').textContent, '已索引 2 个本机程序');
  assert.equal(h.el('side-empty').hidden, true);
  assert.equal(badgeOf(h.navItem('tool:a')).textContent, 'v1.2.3');
  assert.equal(badgeOf(h.navItem('app:editor')).textContent, '开发');
});

test('没有图标的小工具在图标位显示名称首字符', async () => {
  const h = await setup({ list: [
    item({ id: 'imported', kind: 'tool', name: '图片压缩', version: '1.0.0', glyph: null, useCount: 1 }),
    item({ id: 'emoji', kind: 'tool', name: '🎯 打靶', version: '1.0.0', glyph: null, useCount: 1 }),
    item({ id: 'custom', kind: 'tool', name: '工具c', version: '1.0.0', glyph: '🧮', useCount: 1 })
  ] });
  const glyphOf = key => h.navItem(key).children[0].textContent;
  assert.equal(glyphOf('tool:imported'), '图', '无图标时用名称首字符');
  assert.equal(glyphOf('tool:emoji'), '🎯', '按 Unicode 码点取首字符');
  assert.equal(glyphOf('tool:custom'), '🧮', '显式 glyph 优先');
});

test('列表行尾显示调用次数（含 0 次）', async () => {
  const h = await setup();
  const usesOf = key => h.navItem(key).children.find(c => c.hasClass('nav-uses'));
  assert.equal(usesOf('tool:a').textContent, '5次');
  assert.equal(usesOf('tool:a').title, '已使用 5 次');
  assert.equal(usesOf('app:editor').textContent, '3次');
  assert.ok(!usesOf('tool:a').hasClass('zero'));

  const search = h.el('search');
  search.value = '已卸载';
  search.fire('input');
  assert.equal(usesOf('program:gone').textContent, '0次', '搜索到的未使用条目显示 0 次');
  assert.ok(usesOf('program:gone').hasClass('zero'), '0 次用更淡的样式');
});

test('首页隐藏零使用条目，搜索仍可找到', async () => {
  const h = await setup();
  const keys = () => h.el('nav-list').children.map(li => li.children[0].dataset.key);
  assert.deepEqual(keys(), ['tool:a', 'app:editor', 'program:idx-x']);

  const search = h.el('search');
  search.value = '已卸载';
  search.fire('input');
  assert.deepEqual(keys(), ['program:gone'], '搜索时零使用条目也要出现');
  assert.ok(h.navItem('program:gone').hasClass('broken'));

  search.value = '';
  search.fire('input');
  assert.deepEqual(keys(), ['tool:a', 'app:editor', 'program:idx-x'], '清空搜索后恢复首页过滤');
});

test('首页全部为未使用条目时显示搜索引导', async () => {
  const h = await setup({ list: [item({ id: 'p', name: '程序p', searchKey: '程序p', useCount: 0 })] });
  assert.equal(h.el('nav-list').children.length, 0);
  assert.equal(h.el('side-empty').hidden, false);
  assert.match(h.el('side-empty').textContent, /搜索/);
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

test('点击小工具直接打开独立窗口', async () => {
  const h = await setup();
  h.navItem('tool:a').click();
  assert.deepEqual(plain(h.last('openTool').payload), { toolId: 'a' });
  await h.reply('openTool', { ok: true });
  assert.equal(h.requests('openContent').length, 0, '不再打开管理窗口');
  assert.equal(h.requests('embedTool').length, 0);
  assert.equal(h.requests('launcherList').length, 2, '打开后刷新列表（使用次数）');
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
  const search = h.el('search');
  search.value = '已卸载';
  search.fire('input');
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

test('搜索支持首字母与全拼（searchKey 由后端组合）', async () => {
  const list = [
    item({ id: 'web-x', kind: 'web', name: '图片压缩', searchKey: '图片压缩 web-x tpys tupianyasuo tu pian ya suo' }),
    item({ id: 'tool-y', kind: 'tool', name: '计算器', searchKey: '计算器 calc jsq jisuanqi ji suan qi' })
  ];
  const h = await setup({ list });
  const search = h.el('search');
  const keys = () => h.el('nav-list').children.map(li => li.children[0].dataset.key);
  for (const term of ['tpys', 'tupian', 'tupianyasuo', 'tu pian', '图片']) {
    search.value = term;
    search.fire('input');
    assert.deepEqual(keys(), ['web:web-x'], `全拼/首字母未命中：${term}`);
  }
  search.value = 'JISUANQI';
  search.fire('input');
  assert.deepEqual(keys(), ['tool:tool-y'], '全拼应忽略大小写');
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

test('右键本机程序：浮层菜单「移除」走 deleteApp', async () => {
  const h = await setup();
  let prevented = false;
  h.navItem('app:editor').fire('contextmenu', { preventDefault() { prevented = true; } });
  assert.ok(prevented);
  const li = h.liOf('app:editor');
  const btn = h.navItem('app:editor');
  const menu = h.menuOf(li);
  assert.ok(menu, '右键后应出现浮层菜单');
  assert.equal(btn.hidden, false, '条目按钮不再隐藏（菜单是浮层，不占布局）');
  assert.equal(menu.hidden, false);
  assert.match(menu.style.left, /^\d+px$/, '浮层菜单用 fixed 坐标定位');
  assert.match(menu.style.top, /^\d+px$/);
  h.menuAction(li, 3).click();
  assert.deepEqual(plain(h.last('deleteApp').payload), { appId: 'editor' });
  await h.reply('deleteApp', { ok: true });
  assert.match(h.el('toast').textContent, /已移除 编辑器/);
  assert.equal(h.requests('launcherList').length, 2);
});

test('右键索引程序：原位菜单「隐藏」走 hideProgram', async () => {
  const h = await setup();
  let prevented = false;
  h.navItem('program:idx-x').fire('contextmenu', { preventDefault() { prevented = true; } });
  assert.ok(prevented);
  const li = h.liOf('program:idx-x');
  h.menuAction(li, 2).click();
  assert.deepEqual(plain(h.last('hideProgram').payload), { programId: 'idx-x' });
  await h.reply('hideProgram', { ok: true });
  assert.match(h.el('toast').textContent, /已隐藏 索引程序/);
  assert.equal(h.requests('launcherList').length, 2);
});

test('在线工具：点击独立窗口打开，浮层菜单「打开网址/删除」', async () => {
  const list = [
    ...items,
    item({ id: 'site', kind: 'web', name: '资讯站', searchKey: '资讯站', category: '资讯' }),
    item({ id: 'bad', kind: 'web', name: '坏站', searchKey: '坏站', isValid: false })
  ];
  const h = await setup({ list });
  assert.equal(badgeOf(h.navItem('web:site')).textContent, '资讯');
  assert.ok(h.navItem('web:bad').hasClass('broken'));
  assert.match(badgeOf(h.navItem('web:bad')).textContent, /配置无效/);

  h.navItem('web:site').click();
  assert.deepEqual(plain(h.last('openWebTool').payload), { webId: 'site' });
  await h.reply('openWebTool', { ok: true });

  h.navItem('web:site').fire('contextmenu', { preventDefault() {} });
  let li = h.liOf('web:site');
  h.menuAction(li, 1).click();
  assert.deepEqual(plain(h.last('openWebTool').payload), { webId: 'site' });
  await h.reply('openWebTool', { ok: true });

  h.navItem('web:site').fire('contextmenu', { preventDefault() {} });
  li = h.liOf('web:site');
  h.menuAction(li, 4).click();
  assert.deepEqual(plain(h.last('deleteWebTool').payload), { webId: 'site' });
  await h.reply('deleteWebTool', { ok: true });
  assert.match(h.el('toast').textContent, /已删除 资讯站/);
});

test('在线工具菜单含「重命名」，原位表单走 renameWebTool', async () => {
  const list = [...items, item({ id: 'site', kind: 'web', name: '资讯站', searchKey: '资讯站', category: '资讯' })];
  const h = await setup({ list });
  h.navItem('web:site').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('web:site');
  const labels = h.menuOf(li).children[0].children.map(b => b.children[0].textContent);
  assert.deepEqual(labels, ['详情', '打开网址', '重命名', '导出', '删除']);

  h.menuAction(li, 2).click();
  const form = h.menuOf(li).children[2];
  assert.equal(form.children[0].textContent, '重命名在线工具');
  assert.equal(form.children[1].value, '资讯站', '输入框预填当前名称');

  form.children[1].value = '  新资讯站  ';
  form.children[2].children[0].click();
  assert.deepEqual(plain(h.last('renameWebTool').payload), { webId: 'site', name: '新资讯站' });
  await h.reply('renameWebTool', { ok: true });
  assert.match(h.el('toast').textContent, /已重命名为「新资讯站」/);
  assert.equal(h.requests('launcherList').length, 2, '成功后刷新统一列表');
});

test('配置无效的在线工具点击只提示不打开', async () => {
  const list = [...items, item({ id: 'bad', kind: 'web', name: '坏站', searchKey: '坏站', isValid: false })];
  const h = await setup({ list });
  h.navItem('web:bad').click();
  assert.equal(h.requests('openWebTool').length, 0);
  assert.match(h.el('toast').textContent, /配置无效/);
});

test('添加网址：内容窗口保存自动补 https，主模式按钮打开内容窗口', async () => {
  const h = await setup({ mode: 'content', view: 'web' });
  h.view('web-view');
  h.el('web-name').value = ' 资讯站 ';
  h.el('web-url').value = 'example.com/news';
  h.el('web-desc').value = ' 行业资讯 ';
  h.el('web-category').value = ' 资讯 ';
  h.click('web-save');
  assert.deepEqual(plain(h.last('addWebTool').payload), {
    name: '资讯站', url: 'https://example.com/news', description: '行业资讯', category: '资讯', glyph: null
  });
  await h.reply('addWebTool', { ok: true });
  assert.match(h.el('toast').textContent, /已添加在线工具「资讯站」/);

  const main = await setup();
  main.click('add-web');
  assert.deepEqual(plain(main.last('openContent').payload), { view: 'web' });
});

test('右键小工具：浮层菜单「删除」走 deleteTool', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  h.menuAction(li, 7).click();
  assert.deepEqual(plain(h.last('deleteTool').payload), { toolId: 'a', deleteStorage: true });
  await h.reply('deleteTool', { ok: true });
  assert.equal(h.requests('launcherList').length, 2);
  assert.equal(h.requests('closeContent').length, 0);
});

test('右键小工具：创建桌面快捷方式走 createToolShortcut', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  h.menuAction(li, 6).click();
  assert.deepEqual(plain(h.last('createToolShortcut').payload), { toolId: 'a' });
  await h.reply('createToolShortcut', { path: 'C:\\Desktop\\工具a.lnk', fileName: '工具a.lnk' });
  assert.match(h.el('toast').textContent, /已创建桌面快捷方式：工具a\.lnk/);
  assert.equal(h.menuOf(li).hidden, true, '成功后菜单应收起');
  assert.equal(h.navItem('tool:a').hidden, false, '条目按钮始终可见');
});

test('右键小工具：版本管理打开内容窗口版本面板', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  const labels = h.menuOf(li).children[0].children.map(b => b.children[0].textContent);
  assert.equal(labels[2], '版本管理');
  h.menuAction(li, 2).click();
  assert.deepEqual(plain(h.last('openContent').payload), { view: 'version', toolId: 'a' });
  assert.equal(h.menuOf(li).hidden, true, '点击后菜单应收起');
});

test('右键小工具：置顶/取消置顶走 setItemPinned 并刷新列表', async () => {
  const list = [
    item({ id: 'a', kind: 'tool', name: '工具a', version: '1.2.3', searchKey: '工具a a', useCount: 5 }),
    item({ id: 'b', kind: 'tool', name: '工具b', version: '1.2.3', searchKey: '工具b b', useCount: 1, pinned: true })
  ];
  const h = await setup({ list });
  assert.ok(h.navItem('tool:b').hasClass('pinned'), '置顶条目带 pinned 标记');
  const pinOf = btn => btn.children.filter(c => c.hasClass('nav-pin'))[0];
  assert.ok(pinOf(h.navItem('tool:b')), '置顶条目显示图钉');
  assert.equal(pinOf(h.navItem('tool:a')), undefined);

  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  const labels = h.menuOf(li).children[0].children.map(b => b.children[0].textContent);
  assert.equal(labels[4], '置顶');
  h.menuAction(li, 4).click();
  assert.deepEqual(plain(h.last('setItemPinned').payload), { kind: 'tool', id: 'a', pinned: true });
  await h.reply('setItemPinned', { ok: true });
  assert.match(h.el('toast').textContent, /已置顶「工具a」/);
  assert.equal(h.requests('launcherList').length, 2);
});

test('已置顶的小工具菜单显示「取消置顶」', async () => {
  const list = [item({ id: 'b', kind: 'tool', name: '工具b', version: '1.2.3', searchKey: '工具b b', useCount: 1, pinned: true })];
  const h = await setup({ list });
  h.navItem('tool:b').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:b');
  h.menuAction(li, 4).click();
  assert.deepEqual(plain(h.last('setItemPinned').payload), { kind: 'tool', id: 'b', pinned: false });
  await h.reply('setItemPinned', { ok: true });
  assert.match(h.el('toast').textContent, /已取消置顶「工具b」/);
});

test('右键菜单：导出走 exportItem，取消保存不提示成功', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  let li = h.liOf('tool:a');
  h.menuAction(li, 5).click();
  assert.deepEqual(plain(h.last('exportItem').payload), { kind: 'tool', id: 'a' });
  await h.reply('exportItem', { path: 'C:\\tmp\\工具a.qtools', fileName: '工具a.qtools', sizeBytes: 2048 });
  assert.match(h.el('toast').textContent, /已导出：工具a\.qtools/);

  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  li = h.liOf('app:editor');
  h.menuAction(li, 2).click();
  await h.reply('exportItem', { path: null, fileName: null, sizeBytes: 0 });
  assert.match(h.el('toast').textContent, /已取消导出/);
});

test('浮层菜单定位在鼠标处并夹取在视口内', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {}, clientX: 350, clientY: 700 });
  const menu = h.menuOf(h.liOf('tool:a'));
  assert.equal(menu.style.left, '136px', '右侧不足时向左夹取（360-216-8）');
  assert.equal(menu.style.top, '472px', '底部不足时向上夹取（720-240-8）');

  h.navItem('app:editor').fire('contextmenu', { preventDefault() {}, clientX: 40, clientY: 60 });
  const menu2 = h.menuOf(h.liOf('app:editor'));
  assert.equal(menu2.style.left, '40px');
  assert.equal(menu2.style.top, '60px');
});

test('右键菜单：详情展开与返回、再右键同一条目收起还原', async () => {
  const h = await setup();
  const btn = h.navItem('app:editor');
  btn.fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('app:editor');
  const menu = h.menuOf(li);

  h.menuAction(li, 0).click();
  const detail = menu.children[1];
  assert.equal(menu.children[0].hidden, true, '展开详情时应隐藏动作区');
  assert.equal(detail.hidden, false);
  const text = detail.children
    .filter(c => c.hasClass('menu-kv'))
    .map(c => `${c.children[0].textContent}=${c.children[1].textContent}`)
    .join('|');
  assert.match(text, /名称=编辑器/);
  assert.match(text, /类型=本机程序/);
  assert.match(text, /ID=editor/);
  assert.match(text, /使用次数=3 次/);
  assert.match(text, /状态=正常/);

  detail.children.at(-1).click();
  assert.equal(menu.children[0].hidden, false, '点返回应恢复动作区');
  assert.equal(detail.hidden, true);

  btn.fire('contextmenu', { preventDefault() {} });
  assert.equal(btn.hidden, false, '条目按钮始终可见');
  assert.equal(h.menuOf(li).hidden, true, '再次右键同一条目收起菜单');
});

test('一次只开一个菜单：右键另一条目时前一个收起', async () => {
  const h = await setup();
  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  const liA = h.liOf('app:editor');
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const liB = h.liOf('tool:a');
  assert.equal(h.menuOf(liA).hidden, true, '前一个应收起');
  assert.equal(h.menuOf(liB).hidden, false);
  assert.equal(h.navItem('tool:a').hidden, false, '条目按钮始终可见');
});

test('Escape 与点击外部收起菜单，点菜单内部不收起', async () => {
  const h = await setup();
  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('app:editor');
  const menu = h.menuOf(li);

  h.fireDocument('click', { target: menu.children[0].children[0] });
  assert.equal(menu.hidden, false, '点菜单内部不应收起');

  h.fireDocument('click', { target: h.el('search') });
  assert.equal(menu.hidden, true, '点击外部应收起');

  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  h.fireDocument('keydown', { key: 'Escape', target: null });
  assert.equal(h.menuOf(li).hidden, true, 'Escape 应收起');
});

test('列表滚动时浮层菜单自动收起', async () => {
  const h = await setup();
  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  const menu = h.menuOf(h.liOf('app:editor'));
  assert.equal(menu.hidden, false);
  h.fireDocument('scroll', {});
  assert.equal(menu.hidden, true);
});

test('菜单「属性」调用 revealItem 并收起菜单', async () => {
  const h = await setup();
  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('app:editor');
  h.menuAction(li, 1).click();
  assert.deepEqual(plain(h.last('revealItem').payload), { kind: 'app', id: 'editor' });
  assert.equal(h.menuOf(li).hidden, true);
});

/* ================= 小工具原位重命名 ================= */

const renameForm = (h, li) => h.menuOf(li).children[2];
const renameInput = (h, li) => renameForm(h, li).children[1];
const renameOk = (h, li) => renameForm(h, li).children[2].children[0];
const renameCancel = (h, li) => renameForm(h, li).children[2].children[1];
const openRename = h => {
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  h.menuAction(li, 3).click();   /* 详情0 / 属性1 / 版本管理2 / 重命名3 / 置顶4 / 导出5 / 创建桌面快捷方式6 / 删除7 */
  return li;
};

test('小工具菜单含「重命名」，点击展开预填表单', async () => {
  const h = await setup();
  h.navItem('tool:a').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  const labels = h.menuOf(li).children[0].children.map(b => b.children[0].textContent);
  assert.deepEqual(labels, ['详情', '属性', '版本管理', '重命名', '置顶', '导出', '创建桌面快捷方式', '删除']);

  h.menuAction(li, 3).click();
  const menu = h.menuOf(li);
  assert.equal(menu.children[0].hidden, true, '展开重命名时隐藏动作区');
  assert.equal(menu.children[1].hidden, true, '展开重命名时隐藏详情');
  assert.equal(renameForm(h, li).hidden, false);
  assert.equal(renameInput(h, li).value, '工具a', '输入框预填当前名称');
});

test('重命名：确认 trim 后调用 renameTool，成功收起并刷新列表', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '  新名称  ';
  renameOk(h, li).click();
  assert.deepEqual(plain(h.last('renameTool').payload), { toolId: 'a', name: '新名称' });
  await h.reply('renameTool', { ok: true });
  assert.match(h.el('toast').textContent, /已重命名为「新名称」/);
  assert.equal(h.menuOf(li).hidden, true, '成功后应收起菜单');
  assert.equal(h.requests('launcherList').length, 2, '成功后刷新统一列表');
});

test('重命名：取消返回动作区，不发起请求', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '改了但不提交';
  renameCancel(h, li).click();
  assert.equal(renameForm(h, li).hidden, true);
  assert.equal(h.menuOf(li).children[0].hidden, false, '取消应恢复动作区');
  assert.equal(h.requests('renameTool').length, 0);
});

test('重命名：空名拦截不发起请求', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '   ';
  renameOk(h, li).click();
  assert.equal(h.requests('renameTool').length, 0);
  assert.match(h.el('toast').textContent, /名称不能为空/);
  assert.equal(renameForm(h, li).hidden, false, '拦截后表单仍在');
});

test('重命名：输入框 Enter 提交', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '回车名';
  renameInput(h, li).fire('keydown', { key: 'Enter', preventDefault() {} });
  assert.deepEqual(plain(h.last('renameTool').payload), { toolId: 'a', name: '回车名' });
});

test('重命名：与原名相同直接收起，不发起请求', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '工具a';
  renameOk(h, li).click();
  assert.equal(h.requests('renameTool').length, 0);
  assert.equal(h.navItem('tool:a').hidden, false);
});

test('重命名：失败恢复确认按钮并提示', async () => {
  const h = await setup();
  const li = openRename(h);
  renameInput(h, li).value = '新名';
  renameOk(h, li).click();
  assert.equal(renameOk(h, li).disabled, true, '提交中禁用确认');
  h.last('renameTool').reject({ code: 'MANIFEST_INVALID', message: '校验失败' });
  await flush();
  assert.equal(renameOk(h, li).disabled, false, '失败后恢复确认');
  assert.match(h.el('toast').textContent, /重命名失败/);
  assert.equal(renameForm(h, li).hidden, false, '失败后表单仍在');
});

test('列表悬停提示描述，小工具徽标/图标使用分类与自定义图标', async () => {
  const list = [
    item({ id: 'a', kind: 'tool', name: '工具a', version: '1.2.3', category: '效率', glyph: '🧮', description: '四则运算', searchKey: '工具a a' }),
    item({ id: 'site', kind: 'web', name: '资讯站', description: '每日要闻', searchKey: '资讯站', category: '资讯' })
  ];
  const h = await setup({ list });

  const toolBtn = h.navItem('tool:a');
  assert.match(toolBtn.title, /^四则运算\n工具a（小工具，右键管理）$/);
  assert.equal(toolBtn.children[0].textContent, '🧮', '自定义图标优先于内置图标');
  assert.equal(badgeOf(toolBtn).textContent, '效率', '有分类时徽标显示分类而不是版本');

  const webBtn = h.navItem('web:site');
  assert.match(webBtn.title, /^每日要闻\n资讯站（在线工具，右键管理）$/);

  // 详情原位展开也展示描述
  toolBtn.fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('tool:a');
  h.menuAction(li, 0).click();
  const detail = h.menuOf(li).children[1];
  const descRow = detail.children.find(kv => kv.children[0] && kv.children[0].textContent === '描述');
  assert.equal(descRow.children[1].textContent, '四则运算');
});

test('本机程序菜单不含「重命名/置顶」（小工具才有）', async () => {
  const h = await setup();
  h.navItem('app:editor').fire('contextmenu', { preventDefault() {} });
  const li = h.liOf('app:editor');
  const labels = h.menuOf(li).children[0].children.map(b => b.children[0].textContent);
  assert.deepEqual(labels, ['详情', '属性', '导出', '移除']);
  assert.equal(h.menuOf(li).children.length, 2, '非小工具不构建重命名表单');
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

test('内容窗口 view=version 直达版本管理，关闭即收起窗口', async () => {
  const h = await setup({ mode: 'content', view: 'version', toolId: 'a' });
  h.view('version-view');
  assert.equal(h.el('version-title').textContent, '版本管理 · 工具a');
  await h.reply('listVersions', { versions: [] });
  h.click('version-close');
  assert.equal(h.requests('closeContent').length, 1);
});

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

test('内容窗口置顶与钉住开关加载已有状态并可切换', async () => {
  const h = await setup({ mode: 'content', view: 'tool', toolId: 'a' });
  await h.reply('getContentWindowState', { topmost: true, pinned: false });
  await h.embed('a');
  assert.ok(h.el('tool-topmost').hasClass('active'), '启动时应显示已置顶');
  assert.ok(!h.el('tool-pinned').hasClass('active'));

  h.click('tool-topmost');
  assert.deepEqual(plain(h.last('setContentWindowTopmost').payload), { enabled: false });
  await h.reply('setContentWindowTopmost', { topmost: false, pinned: false });
  assert.ok(!h.el('tool-topmost').hasClass('active'));

  h.click('tool-pinned');
  assert.deepEqual(plain(h.last('setContentWindowPinned').payload), { enabled: true });
  await h.reply('setContentWindowPinned', { topmost: false, pinned: true });
  assert.ok(h.el('tool-pinned').hasClass('active'));

  h.click('tool-pinned');
  await h.reply('setContentWindowPinned', { topmost: false, pinned: false });
  assert.ok(!h.el('tool-pinned').hasClass('active'));
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

test('AI 流式思考与正文分区显示', async () => {
  const h = await setup({ mode: 'content', view: 'create' });
  h.el('create-prompt').value = '做一个待办清单';
  h.click('create-generate');
  const stream = h.last('generateTool');
  assert.equal(h.el('create-stream-reasoning').hidden, true, '没有思考时不显示分区');

  stream.onDelta('先分析需求', true);
  assert.equal(h.el('create-stream-reasoning').hidden, false);
  assert.equal(h.el('create-stream-reasoning-text').textContent, '先分析需求');
  assert.equal(h.el('create-stream-text').textContent, '', '思考内容不能混进正文');

  stream.onDelta('正式输出', false);
  assert.equal(h.el('create-stream-reasoning-text').textContent, '先分析需求');
  assert.equal(h.el('create-stream-text').textContent, '正式输出');

  stream.reject({ code: 'TEST_ERROR', message: '结束' });
  await flush();
});

test('AI 流式输出把 JSON 转义显示为真实换行', async () => {
  const h = await setup({ mode: 'content', view: 'create' });
  h.el('create-prompt').value = '生成';
  h.click('create-generate');
  const stream = h.last('generateTool');

  /* 转义序列可能被拆在两段增量里：先原样显示，补齐后再整体还原 */
  stream.onDelta('{"indexHtml":"<html>\\', false);
  assert.equal(h.el('create-stream-text').textContent, '{"indexHtml":"<html>\\');
  stream.onDelta('n<body>\\n</html>","x":"\\u4e2d\\"y\\""}', false);
  assert.equal(
    h.el('create-stream-text').textContent,
    '{"indexHtml":"<html>\n<body>\n</html>","x":"中"y""}');

  /* 思考分区不做转义还原 */
  stream.onDelta('第一行\\n第二行', true);
  assert.equal(h.el('create-stream-reasoning-text').textContent, '第一行\\n第二行');

  stream.reject({ code: 'TEST_ERROR', message: '结束' });
  await flush();
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

test('创建保存后打开独立窗口并收起管理窗口', async () => {
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
  assert.equal(h.requests('openTool').length, 1);
  assert.deepEqual(plain(h.last('openTool').payload), { toolId: 'c' });
  await h.reply('openTool', { ok: true });
  assert.equal(h.requests('closeContent').length, 1);
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
  await h.pickFile({
    path: 'C:\\tmp\\old.html', fileName: 'old.html', kind: 'html',
    html: '<h1>旧</h1>', name: 'old', version: null, itemKind: null
  });
  assert.equal(h.el('import-file-name').textContent, 'old.html');
  h.click('import-cancel');
  assert.equal(h.requests('closeContent').length, 1);
  const h2 = await setup({ mode: 'content', view: 'import' });
  assert.equal(h2.el('import-file-name').textContent, '未选择文件');
});

test('导入页：选择 HTML 文件后由后端读回内容并预填名称', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  await h.pickFile({
    path: 'C:\\tmp\\test.html', fileName: 'test.html', kind: 'html',
    html: '<h1>测试</h1>', name: 'test', version: null, itemKind: null
  });
  assert.equal(h.el('import-file-name').textContent, 'test.html');
  assert.equal(h.el('import-name').value, 'test', '用文件名预填工具名');
  assert.equal(h.el('import-name').disabled, false);
  assert.equal(h.el('import-package-hint').hidden, true);
  h.click('import-run');
  assert.equal(h.last('importTool').payload.html, '<h1>测试</h1>');
});

test('导入页：取消原生选择对话框不改动表单', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  h.el('import-name').value = '已有名称';
  await h.pickFile({ path: null, fileName: null, kind: '', html: null, name: null, version: null, itemKind: null });
  assert.equal(h.el('import-file-name').textContent, '未选择文件');
  assert.equal(h.el('import-name').value, '已有名称');
});

test('导入页：选择导出包后锁定元数据输入并调用 importItemPackage', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  await h.pickFile({
    path: 'C:\\tmp\\题库.qtools', fileName: '题库.qtools', kind: 'package',
    html: null, name: '题库工具', version: '2.1.0', itemKind: 'tool'
  });
  assert.equal(h.el('import-file-name').textContent, '题库.qtools（导出包）');
  assert.equal(h.el('import-name').value, '题库工具');
  assert.equal(h.el('import-name').disabled, true, '导出包自带元数据');
  assert.equal(h.el('import-category').disabled, true);
  assert.equal(h.el('import-package-hint').hidden, false);

  h.click('import-run');
  assert.deepEqual(plain(h.last('importItemPackage').payload), {
    path: 'C:\\tmp\\题库.qtools', acceptHighSeverity: false
  });
  await h.reply('importItemPackage', {
    kind: 'tool', itemId: 'tiku', itemName: '题库工具', saved: true,
    permissions: ['storage'], findings: [], hasFatal: false,
    requiresExplicitAcceptance: false, externalAssets: []
  });
  assert.equal(h.requests('openTool').length, 1);
  assert.deepEqual(plain(h.last('openTool').payload), { toolId: 'tiku' });
});

test('导入导出包：在线工具直接保存并收起管理窗口', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  await h.pickFile({
    path: 'C:\\tmp\\site.qtools', fileName: 'site.qtools', kind: 'package',
    html: null, name: '站点', version: null, itemKind: 'web'
  });
  h.click('import-run');
  await h.reply('importItemPackage', {
    kind: 'web', itemId: 'site', itemName: '站点', saved: true,
    permissions: [], findings: [], hasFatal: false,
    requiresExplicitAcceptance: false, externalAssets: []
  });
  assert.match(h.el('toast').textContent, /已导入在线工具「站点」/);
  assert.equal(h.requests('closeContent').length, 1);
  assert.equal(h.requests('openTool').length, 0);
});

test('导入导出包：小工具高危项审查后二次确认才保存', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  await h.pickFile({
    path: 'C:\\tmp\\x.qtools', fileName: 'x.qtools', kind: 'package',
    html: null, name: 'X', version: '1.0.0', itemKind: 'tool'
  });
  h.click('import-run');
  assert.equal(h.el('import-run').disabled, true);
  assert.equal(h.el('import-confirm').disabled, true, '忙碌期间确认按钮禁用');
  await h.reply('importItemPackage', {
    kind: 'tool', itemId: 'x', itemName: 'X', saved: false,
    permissions: [], findings: [{ severity: 1, code: 'ALLOW_EVAL', message: '启用了 allowEval' }],
    hasFatal: false, requiresExplicitAcceptance: true, externalAssets: []
  });
  assert.equal(h.el('import-review').hidden, false);
  assert.match(h.el('import-findings').children[0].textContent, /ALLOW_EVAL/);
  h.click('import-confirm');
  assert.deepEqual(plain(h.last('importItemPackage').payload), {
    path: 'C:\\tmp\\x.qtools', acceptHighSeverity: true
  });
});

test('读取文件及导入期间禁用重复提交并锁定审查输入', async () => {
  const h = await setup({ mode: 'content', view: 'import' });
  h.el('import-name').value = '导入测试';
  h.el('import-desc').value = ' 测试工具 ';
  h.el('import-category').value = ' 效率 ';
  h.el('import-glyph').value = ' 🧮 ';
  await h.pickFile({
    path: 'C:\\tmp\\test.html', fileName: 'test.html', kind: 'html',
    html: '<h1>测试</h1>', name: 'test', version: null, itemKind: null
  });
  h.el('import-name').value = '导入测试';
  h.click('import-run');
  assert.equal(h.el('import-run').disabled, true);
  h.click('import-run');
  await flush();
  assert.equal(h.requests('importTool').length, 1);
  assert.equal(h.last('importTool').payload.description, '测试工具');
  assert.equal(h.last('importTool').payload.category, '效率');
  assert.equal(h.last('importTool').payload.glyph, '🧮');
  assert.equal(h.el('import-pick').disabled, true);
  assert.equal(h.el('import-category').disabled, true);
  assert.equal(h.el('import-confirm').disabled, true);
  await h.reply('importTool', { saved: false, findings: [], permissions: [], toolId: 'test' });
  assert.equal(h.el('import-run').disabled, false);
  assert.equal(h.el('import-name').disabled, false, 'HTML 模式解锁元数据');
  assert.equal(h.el('import-review').hidden, false);
  h.click('import-confirm');
  h.click('import-confirm');
  assert.equal(h.requests('importTool').length, 2);
  assert.equal(h.last('importTool').payload.acceptHighSeverity, true);
  await h.reply('importTool', { saved: true, toolId: 'test', toolName: '导入测试' });
  assert.equal(h.el('import-run').disabled, false);
  assert.equal(h.requests('openTool').length, 1);
  assert.deepEqual(plain(h.last('openTool').payload), { toolId: 'test' });
  await h.reply('openTool', { ok: true });
  assert.equal(h.requests('closeContent').length, 1);
});

for (const id of ['import-name', 'import-desc', 'import-category', 'import-glyph']) {
  test(`修改${id}后隐藏旧审查并禁止直接确认`, async () => {
    const h = await setup({ mode: 'content', view: 'import' });
    h.el('import-name').value = '旧名称';
    await h.pickFile({
      path: 'C:\\tmp\\test.html', fileName: 'test.html', kind: 'html',
      html: '<h1>测试</h1>', name: 'test', version: null, itemKind: null
    });
    h.click('import-run');
    await flush();
    await h.reply('importTool', { saved: false, findings: [], permissions: [], toolId: 'test' });
    const input = h.el(id);
    input.fire('input');
    assert.equal(h.el('import-review').hidden, true);
    h.click('import-confirm');
    assert.equal(h.requests('importTool').length, 1);
  });
}

/* ================= 主题实时切换（theme.js） ================= */

async function setupTheme(replyOf) {
  const attrs = {};
  const handlers = {};
  const calls = [];
  const documentElement = {
    setAttribute(name, value) { attrs[name] = value; },
    removeAttribute(name) { delete attrs[name]; }
  };
  const host = {
    callManager(method) {
      calls.push(method);
      return Promise.resolve({ theme: replyOf() });
    }
  };
  vm.runInNewContext(themeSource, {
    window: {
      host,
      addEventListener: (event, handler) => { (handlers[event] ||= []).push(handler); }
    },
    document: { documentElement }
  }, { filename: `${ui}/theme.js` });
  await flush();
  const fire = (event, arg) => (handlers[event] || []).forEach(handler => handler(arg));
  return { attrs, calls, fire };
}

test('theme.js 启动时按宿主主题写入 html 的 data-theme', async () => {
  const h = await setupTheme(() => 'dark');
  assert.deepEqual(h.calls, ['manager.getTheme']);
  assert.equal(h.attrs['data-theme'], 'dark');
});

test('theme.js 收到 theme-changed 后重新取主题，system 时移除属性', async () => {
  let current = 'light';
  const h = await setupTheme(() => current);
  assert.equal(h.attrs['data-theme'], 'light');

  current = 'system';
  h.fire('host-event', { detail: 'theme-changed' });
  await flush();
  assert.equal(h.calls.length, 2);
  assert.equal(h.attrs['data-theme'], undefined, '跟随系统时不写死 data-theme');

  h.fire('host-event', { detail: 'launcher-changed' });
  await flush();
  assert.equal(h.calls.length, 2, '其它事件不触发主题刷新');
});

/* ================= 工具页主题（tool-theme.js） ================= */

async function setupToolTheme({ themed, theme, dark, mediaDark = false }) {
  const attrs = {
    'data-qtools-themed': themed ? '1' : '0',
    'data-qtools-theme': theme
  };
  if (theme === 'light' || theme === 'dark') { attrs['data-theme'] = theme; }
  if (!themed && (theme === 'dark' || (theme === 'system' && dark))) {
    attrs['data-qtools-auto-dark'] = '1';
  }

  const windowHandlers = {};
  const mediaHandlers = [];
  const state = { theme, dark };
  let mediaMatches = mediaDark;
  const host = {
    theme: { get: () => Promise.resolve({ theme: state.theme, dark: state.dark }) }
  };
  const root = {
    getAttribute: name => (name in attrs ? attrs[name] : null),
    setAttribute: (name, value) => { attrs[name] = value; },
    removeAttribute: name => { delete attrs[name]; }
  };
  vm.runInNewContext(toolThemeSource, {
    window: {
      host,
      matchMedia: () => ({
        get matches() { return mediaMatches; },
        addEventListener: (event, handler) => { if (event === 'change') mediaHandlers.push(handler); }
      }),
      addEventListener: (event, handler) => { (windowHandlers[event] ||= []).push(handler); }
    },
    document: { documentElement: root }
  }, { filename: `${ui}/tool-theme.js` });
  await flush();
  const fire = (event, arg) => (windowHandlers[event] || []).forEach(handler => handler(arg));
  const setMedia = matches => { mediaMatches = matches; mediaHandlers.forEach(handler => handler()); };
  return { attrs, state, fire, setMedia };
}

test('工具页主题：自带深色样式的页面只切 data-theme，不做反色', async () => {
  const h = await setupToolTheme({ themed: true, theme: 'light', dark: false });
  assert.equal(h.attrs['data-theme'], 'light');

  h.state.theme = 'dark';
  h.state.dark = true;
  h.fire('host-event', { detail: 'theme-changed' });
  await flush();
  assert.equal(h.attrs['data-theme'], 'dark');
  assert.equal(h.attrs['data-qtools-auto-dark'], undefined);

  h.state.theme = 'system';
  h.state.dark = false;
  h.fire('host-event', { detail: 'theme-changed' });
  await flush();
  assert.equal(h.attrs['data-theme'], undefined, '跟随系统时移除显式主题');
});

test('工具页主题：旧工具在深色时加反色兜底，切浅色后移除', async () => {
  const h = await setupToolTheme({ themed: false, theme: 'dark', dark: true });
  assert.equal(h.attrs['data-qtools-auto-dark'], '1');

  h.state.theme = 'light';
  h.state.dark = false;
  h.fire('host-event', { detail: 'theme-changed' });
  await flush();
  assert.equal(h.attrs['data-qtools-auto-dark'], undefined);
  assert.equal(h.attrs['data-theme'], 'light');

  h.state.theme = 'dark';
  h.state.dark = true;
  h.fire('host-event', { detail: 'theme-changed' });
  await flush();
  assert.equal(h.attrs['data-qtools-auto-dark'], '1');
});

test('工具页主题：跟随系统时随系统深浅同步反色兜底', async () => {
  const h = await setupToolTheme({ themed: false, theme: 'system', dark: false });
  assert.equal(h.attrs['data-qtools-auto-dark'], undefined);

  h.setMedia(true);
  assert.equal(h.attrs['data-qtools-auto-dark'], '1', '系统切到深色后自动反色');

  h.setMedia(false);
  assert.equal(h.attrs['data-qtools-auto-dark'], undefined);
});
