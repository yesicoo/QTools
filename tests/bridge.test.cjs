"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const vm = require("node:vm");
const fs = require("node:fs");

const bridgePath = `${__dirname}/../src/QTools.Host/Assets/host-ui/bridge.js`;
const bridgeSource = fs.readFileSync(bridgePath, "utf8");
const ORIGIN = "http://127.0.0.1:18080";
const TOKEN_A = "a".repeat(32);
const TOKEN_B = "b".repeat(32);
const REQUEST_MARK = "__qtoolsRelay";
const RESPONSE_MARK = "__qtoolsRelayResponse";

function createClock() {
  let now = 0;
  let sequence = 0;
  const timers = new Map();
  return {
    timers,
    setTimeout(callback, delay) {
      const id = ++sequence;
      timers.set(id, { callback, delay, due: now + delay });
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    advance(milliseconds) {
      const target = now + milliseconds;
      while (true) {
        const next = [...timers.entries()]
          .filter(([, timer]) => timer.due <= target)
          .sort((a, b) => a[1].due - b[1].due)[0];
        if (!next) break;
        const [id, timer] = next;
        now = timer.due;
        timers.delete(id);
        timer.callback();
      }
      now = target;
    }
  };
}

function createNative({ registrationFailures = 0, sendFailures = 0 } = {}) {
  const native = { attempts: [], sent: [], registrations: 0, receiver: null };
  native.external = {
    sendMessage(raw) {
      native.attempts.push(raw);
      if (sendFailures-- > 0) throw new Error("模拟原生发送失败");
      native.sent.push(raw);
    },
    receiveMessage(receiver) {
      native.registrations += 1;
      if (registrationFailures-- > 0) throw new Error("模拟原生接收器尚未就绪");
      native.receiver = receiver;
    }
  };
  native.respond = (message) => {
    assert.equal(typeof native.receiver, "function", "原生接收器应已注册");
    native.receiver(JSON.stringify(message));
  };
  return native;
}

function createPage(t, options = {}) {
  const clock = createClock();
  const listeners = new Map();
  const posts = [];
  const parentPosts = [];
  const frames = [];
  const warnings = [];
  const location = new URL(options.url || `${ORIGIN}/index.html`);
  const parent = options.parent || {
    postMessage(data, targetOrigin) { parentPosts.push({ data, targetOrigin }); }
  };
  const root = {
    firstChild: null,
    insertBefore(element) { warnings.push(element); }
  };
  const document = {
    currentScript: {
      src: `${ORIGIN}/__host__/bridge.js?tool=${options.toolId || "__host__"}&v=1.0.0&port=18080`
    },
    readyState: "complete",
    body: root,
    documentElement: root,
    createElement() { return { setAttribute() {} }; },
    querySelectorAll(selector) {
      assert.equal(selector, "iframe");
      return frames.filter((frame) => frame.isConnected);
    }
  };
  const window = {
    location,
    document,
    external: options.external,
    chrome: options.chrome,
    addEventListener(type, callback) {
      const callbacks = listeners.get(type) || [];
      callbacks.push(callback);
      listeners.set(type, callbacks);
    },
    postMessage(data, targetOrigin) { posts.push({ data, targetOrigin }); }
  };
  window.self = window;
  window.top = options.iframe ? parent : window;
  window.parent = options.iframe ? parent : window;
  if (options.configure) options.configure(window);
  const math = Object.create(Math);
  math.random = options.random === undefined ? Math.random : () => options.random;
  const context = vm.createContext({
    window, document, URL,
    Math: math,
    Date: class extends Date { static now() { return 1700000000000; } },
    setTimeout: clock.setTimeout,
    clearTimeout: clock.clearTimeout
  });
  // 使用虚拟计时器，失败用例也不会留下真实的十分钟等待。
  t.after(() => {
    const remaining = clock.timers.size;
    clock.timers.clear();
    assert.equal(remaining, 0, "用例结束时桥接计时器应已清理");
  });
  vm.runInContext(bridgeSource, context, { filename: bridgePath, timeout: 1000 });
  return {
    window, host: window.host, clock, frames, posts, parentPosts, warnings,
    dispatch(data, source = window.parent, origin = ORIGIN) {
      for (const callback of listeners.get("message") || []) {
        callback({ data, source, origin });
      }
    }
  };
}

function attachFrame(page, { token = TOKEN_A, child, url } = {}) {
  const posts = child ? child.posts : [];
  const contentWindow = child ? child.window : {
    postMessage(data, targetOrigin) { posts.push({ data, targetOrigin }); }
  };
  const frame = {
    contentWindow, isConnected: true,
    src: url || `${ORIGIN}/tools/example/index.html?bt=${token}`
  };
  page.frames.push(frame);
  return { frame, posts, window: contentWindow };
}

function relayRequest(overrides = {}) {
  return {
    id: "工具页原始请求", toolId: "example", method: "storage.get",
    payload: { key: "测试键" }, token: TOKEN_A, ...overrides
  };
}

function dispatchRelay(page, frame, request = relayRequest(), origin = ORIGIN) {
  page.dispatch({ relay: REQUEST_MARK, payload: JSON.stringify(request) }, frame.window, origin);
}

function readRequest(native, index = 0) {
  return JSON.parse(native.sent[index]);
}

function onlyTimer(page) {
  assert.equal(page.clock.timers.size, 1, "应仅有一个待处理计时器");
  return [...page.clock.timers.values()][0];
}

function observe(promise) {
  const state = { status: "等待" };
  promise.then(
    (value) => { state.status = "成功"; state.value = value; },
    (error) => { state.status = "失败"; state.error = error; }
  );
  return state;
}

async function expectPending(state) {
  await Promise.resolve();
  assert.equal(state.status, "等待");
}

async function expectResolved(state, value) {
  await Promise.resolve();
  assert.equal(state.status, "成功");
  assert.deepEqual(JSON.parse(JSON.stringify(state.value)), value);
}

async function expectRejected(state, code) {
  await Promise.resolve();
  assert.equal(state.status, "失败", "应立即拒绝，而不是等待真实超时");
  assert.equal(state.error.code, code);
}

test("传输属性探测与首次接收器注册前已经暴露完整 window.host", (t) => {
  const native = createNative({ registrationFailures: 1 });
  const observations = [];
  const page = createPage(t, {
    configure(window) {
      Object.defineProperty(window, "external", {
        get() {
          observations.push({ host: window.host, chat: typeof window.host?.ai?.chat });
          return native.external;
        }
      });
    }
  });
  assert.ok(observations.length > 0);
  for (const observation of observations) {
    assert.equal(observation.host, page.host);
    assert.equal(observation.chat, "function");
  }
  assert.equal(typeof page.host.callManager, "function");
  assert.equal(native.registrations, 1);
  assert.equal(page.warnings.length, 1);
});

test("原生接收器首次注册失败后下一次调用恢复且不重复注册", async (t) => {
  const native = createNative({ registrationFailures: 1 });
  const page = createPage(t, { external: native.external });
  assert.equal(native.registrations, 1);
  assert.equal(native.attempts.length, 0);
  const first = observe(page.host.storage.get("测试键"));
  assert.equal(native.registrations, 2);
  native.respond({ id: readRequest(native).id, result: { value: "恢复成功" } });
  await expectResolved(first, { value: "恢复成功" });
  const second = observe(page.host.storage.list());
  assert.equal(native.registrations, 2);
  native.respond({ id: readRequest(native, 1).id, result: [] });
  await expectResolved(second, []);
  assert.equal(page.clock.timers.size, 0);
});

test("callManagerStream 透传思考标记（reasoning）", async (t) => {
  const native = createNative();
  const page = createPage(t, { external: native.external });
  const deltas = [];
  const control = {};
  const state = observe(page.host.callManagerStream(
    "manager.generateTool", { prompt: "时钟" },
    (text, reasoning) => deltas.push({ text, reasoning }), control));

  const id = readRequest(native).id;
  assert.equal(control.requestId, id);
  native.respond({ id, stream: { delta: "思考内容", reasoning: true } });
  native.respond({ id, stream: { delta: "正文内容", reasoning: false } });
  native.respond({ id, result: { ok: true } });

  await expectResolved(state, { ok: true });
  assert.deepEqual(deltas, [
    { text: "思考内容", reasoning: true },
    { text: "正文内容", reasoning: false }
  ]);
});

test("external 接收器失败可以降级到 chrome 接收响应", async (t) => {
  const native = createNative({ registrationFailures: Infinity });
  let chromeReceiver;
  let chromeRegistrations = 0;
  const chromeSent = [];
  const page = createPage(t, {
    external: native.external,
    chrome: { webview: {
      postMessage(raw) { chromeSent.push(raw); },
      addEventListener(type, callback) {
        assert.equal(type, "message");
        chromeRegistrations += 1;
        chromeReceiver = callback;
      }
    } }
  });
  assert.equal(page.host.isAvailable(), true);
  assert.equal(native.registrations, 1);
  assert.equal(chromeRegistrations, 1);
  assert.equal(native.attempts.length, 0);
  assert.equal(chromeSent.length, 0);
  const state = observe(page.host.tool.getManifest());
  chromeReceiver({ data: JSON.stringify({ id: readRequest(native).id, result: { name: "测试工具" } }) });
  await expectResolved(state, { name: "测试工具" });
});

test("初始探测和重复可用性探测不发送空消息或其他探测消息", (t) => {
  const native = createNative({ registrationFailures: 1 });
  const page = createPage(t, { external: native.external });
  assert.equal(page.host.isAvailable(), true);
  assert.equal(page.host.isAvailable(), true);
  assert.equal(native.registrations, 2);
  assert.deepEqual(native.attempts, []);
  assert.equal(page.clock.timers.size, 0);
});

test("仅有 chrome 时探测不发送消息且正常调用可收发", async (t) => {
  const sent = [];
  let receiver;
  const page = createPage(t, { chrome: { webview: {
    postMessage(raw) { sent.push(raw); },
    addEventListener(type, callback) { receiver = callback; }
  } } });
  assert.equal(page.host.isAvailable(), true);
  assert.deepEqual(sent, []);
  const state = observe(page.host.storage.list());
  assert.equal(sent.length, 1);
  receiver({ data: { id: JSON.parse(sent[0]).id, result: [] } });
  await expectResolved(state, []);
});

test("iframe 即使存在原生发送接口也只通过同源 parent.postMessage", async (t) => {
  const native = createNative();
  const page = createPage(t, {
    iframe: true, toolId: "example", external: native.external,
    url: `${ORIGIN}/tools/example/index.html?bt=${TOKEN_A}`
  });
  assert.equal(page.host.isAvailable(), true);
  assert.deepEqual(page.parentPosts, []);
  const state = observe(page.host.storage.get("测试键"));
  assert.equal(native.registrations, 0);
  assert.deepEqual(native.attempts, []);
  assert.equal(page.parentPosts.length, 1);
  const post = page.parentPosts[0];
  assert.equal(post.targetOrigin, ORIGIN);
  assert.equal(post.data.relay, REQUEST_MARK);
  const request = JSON.parse(post.data.payload);
  assert.equal(request.toolId, "example");
  assert.equal(request.token, TOKEN_A);
  assert.equal(request.method, "storage.get");
  page.dispatch({ relay: RESPONSE_MARK, message: { id: request.id, result: "成功" } });
  await expectResolved(state, "成功");
});

test("iframe 只接受同源 parent 的中继响应", async (t) => {
  const page = createPage(t, { iframe: true, toolId: "example" });
  const state = observe(page.host.storage.list());
  const request = JSON.parse(page.parentPosts[0].data.payload);
  const response = { relay: RESPONSE_MARK, message: { id: request.id, result: ["可信"] } };
  const invalid = [
    [response, page.window.parent, "https://example.invalid"],
    [response, page.window.parent, "null"],
    [response, {}, ORIGIN],
    [response, page.window, ORIGIN],
    [{ ...response, relay: REQUEST_MARK }, page.window.parent, ORIGIN],
    ["非 JSON", page.window.parent, ORIGIN]
  ];
  for (const [data, source, origin] of invalid) {
    page.dispatch(data, source, origin);
    await expectPending(state);
    assert.equal(page.clock.timers.size, 1);
  }
  page.dispatch(JSON.stringify(response));
  await expectResolved(state, ["可信"]);
  page.dispatch(response);
  assert.equal(page.clock.timers.size, 0);
});

const rejectedRelays = [
  ["不同源请求", (page, frame, request) => ({ origin: "https://example.invalid" })],
  ["不在当前页面的来源窗口", () => ({ source: {} })],
  ["已经移除的 iframe", (page, frame) => { frame.frame.isConnected = false; }],
  ["URL 没有内嵌凭证", (page, frame) => { frame.frame.src = `${ORIGIN}/tools/example/index.html`; }],
  ["请求没有内嵌凭证", (page, frame, request) => { delete request.token; }],
  ["请求凭证不匹配", (page, frame, request) => { request.token = TOKEN_B; }],
  ["iframe 导航后的旧凭证", (page, frame) => { frame.frame.src += `&revision=2`; frame.frame.src = frame.frame.src.replace(TOKEN_A, TOKEN_B); }],
  ["缺少请求编号", (page, frame, request) => { delete request.id; }],
  ["缺少方法名", (page, frame, request) => { delete request.method; }],
  ["载荷不是合法 JSON", () => ({ payload: "{" })]
];

for (const [name, change] of rejectedRelays) {
  test(`宿主页拒绝转发：${name}`, (t) => {
    const native = createNative();
    const page = createPage(t, { external: native.external });
    const frame = attachFrame(page);
    const request = relayRequest();
    const overrides = change(page, frame, request) || {};
    page.dispatch({ relay: REQUEST_MARK, payload: overrides.payload ?? JSON.stringify(request) },
      overrides.source ?? frame.window, overrides.origin ?? ORIGIN);
    assert.deepEqual(native.attempts, []);
    assert.deepEqual(frame.posts, []);
    assert.equal(page.clock.timers.size, 0);
  });
}

for (const [name, options] of [
  ["独立工具页", { toolId: "example" }],
  ["内嵌页面", { toolId: "__host__", iframe: true }]
]) {
  test(`${name}不能充当宿主页转发中继请求`, (t) => {
    const native = createNative();
    const page = createPage(t, { ...options, external: native.external });
    const frame = attachFrame(page);
    dispatchRelay(page, frame);
    assert.deepEqual(native.attempts, []);
    assert.deepEqual(page.parentPosts, []);
    assert.deepEqual(frame.posts, []);
  });
}

test("宿主页按 iframe 当前 URL 的新凭证转发而不是缓存旧凭证", (t) => {
  const native = createNative();
  const page = createPage(t, { external: native.external });
  const frame = attachFrame(page);
  frame.frame.src = `${ORIGIN}/tools/example/index.html?bt=${TOKEN_B}`;
  const request = relayRequest({ token: TOKEN_B });
  dispatchRelay(page, frame, request);
  const forwarded = readRequest(native);
  assert.notEqual(forwarded.id, request.id);
  assert.deepEqual({ ...forwarded, id: request.id }, request);
  native.respond({ id: forwarded.id, result: "新凭证有效" });
  assert.equal(frame.posts.length, 1);
  assert.equal(frame.posts[0].data.message.id, request.id);
});

test("并发同编号中继分配唯一编号且只回送对应 frame 并清理条目", async (t) => {
  const native = createNative();
  const page = createPage(t, { external: native.external });
  const first = attachFrame(page);
  const second = attachFrame(page, { token: TOKEN_B });
  const local = observe(page.host.callManager("manager.getServerInfo"));
  const localId = readRequest(native).id;
  dispatchRelay(page, first, relayRequest({ id: localId }));
  dispatchRelay(page, second, relayRequest({ id: localId, token: TOKEN_B }));
  const firstId = readRequest(native, 1).id;
  const secondId = readRequest(native, 2).id;
  assert.equal(new Set([localId, firstId, secondId]).size, 3);
  assert.equal(page.clock.timers.size, 3);

  native.respond({ id: "未知响应", result: "忽略" });
  assert.equal(page.clock.timers.size, 3);
  native.respond({ id: secondId, result: "第二页" });
  assert.equal(first.posts.length, 0);
  assert.equal(second.posts.length, 1);
  assert.equal(second.posts[0].targetOrigin, ORIGIN);
  assert.equal(second.posts[0].data.relay, RESPONSE_MARK);
  assert.equal(second.posts[0].data.message.id, localId);
  assert.equal(second.posts[0].data.message.result, "第二页");
  assert.equal(page.clock.timers.size, 2);
  await expectPending(local);
  native.respond({ id: secondId, result: "重复响应" });
  assert.equal(second.posts.length, 1);

  native.respond({ id: localId, result: "宿主页" });
  await expectResolved(local, "宿主页");
  assert.equal(page.clock.timers.size, 1);
  const error = { code: "PERMISSION_DENIED", message: "没有权限" };
  native.respond({ id: firstId, error });
  assert.equal(first.posts.length, 1);
  assert.equal(first.posts[0].targetOrigin, ORIGIN);
  assert.equal(first.posts[0].data.message.id, localId);
  assert.deepEqual(JSON.parse(JSON.stringify(first.posts[0].data.message.error)), error);
  native.respond({ id: firstId, error });
  assert.equal(first.posts.length, 1);
  assert.equal(second.posts.length, 1);
  assert.equal(page.clock.timers.size, 0);
});

for (const [name, change] of [
  ["已导航", (frame) => { frame.src += "&revision=2"; }],
  ["已移除", (frame) => { frame.isConnected = false; }]
]) {
  test(`响应不回送${name}的 iframe 且清理中继条目`, (t) => {
    const native = createNative();
    const page = createPage(t, { external: native.external });
    const frame = attachFrame(page);
    dispatchRelay(page, frame);
    const id = readRequest(native).id;
    const originalUrl = frame.frame.src;
    change(frame.frame);
    native.respond({ id, result: "过期响应" });
    assert.deepEqual(frame.posts, []);
    assert.equal(page.clock.timers.size, 0);
    frame.frame.src = originalUrl;
    frame.frame.isConnected = true;
    native.respond({ id, result: "恢复页面后仍不能重放" });
    assert.deepEqual(frame.posts, []);
  });
}

test("中继等待超过六百秒才过期且过期响应不再回送", (t) => {
  const native = createNative();
  const page = createPage(t, { external: native.external });
  const frame = attachFrame(page);
  dispatchRelay(page, frame, relayRequest({ method: "ai.chat" }));
  const delay = onlyTimer(page).delay;
  assert.ok(delay > 600000);
  page.clock.advance(600000);
  assert.equal(page.clock.timers.size, 1);
  page.clock.advance(delay - 600000);
  assert.equal(page.clock.timers.size, 0);
  native.respond({ id: readRequest(native).id, result: "迟到响应" });
  assert.deepEqual(frame.posts, []);
});

for (const [name, unavailableNative] of [
  ["宿主没有原生传输", () => null],
  ["宿主原生发送失败", () => createNative({ sendFailures: 1 })]
]) {
  test(`${name}时 iframe 收到原请求编号并立即拒绝`, async (t) => {
    const native = unavailableNative();
    const parent = createPage(t, { external: native?.external });
    const child = createPage(t, {
      iframe: true, toolId: "example", parent: parent.window,
      url: `${ORIGIN}/tools/example/index.html?bt=${TOKEN_A}`
    });
    attachFrame(parent, { child });
    const state = observe(child.host.storage.get("测试键"));
    assert.equal(parent.posts.length, 1);
    const request = JSON.parse(parent.posts[0].data.payload);
    parent.dispatch(parent.posts[0].data, child.window);
    assert.equal(child.posts.length, 1);
    const response = child.posts[0];
    assert.equal(response.targetOrigin, ORIGIN);
    assert.equal(response.data.relay, RESPONSE_MARK);
    assert.equal(response.data.message.id, request.id);
    assert.equal(response.data.message.error.code, "HOST_BRIDGE_UNAVAILABLE");
    assert.equal(parent.clock.timers.size, 0);
    child.dispatch(response.data, parent.window);
    await expectRejected(state, "HOST_BRIDGE_UNAVAILABLE");
    assert.equal(child.clock.timers.size, 0);
  });
}

test("原生 sendRaw 返回 false 时立即拒绝并允许下次调用恢复", async (t) => {
  const native = createNative({ sendFailures: 1 });
  const page = createPage(t, { external: native.external });
  const failed = observe(page.host.storage.list());
  assert.equal(page.clock.timers.size, 0);
  await expectRejected(failed, "HOST_BRIDGE_UNAVAILABLE");
  native.respond({ id: JSON.parse(native.attempts[0]).id, result: "失败请求的迟到响应" });
  assert.equal(failed.status, "失败");
  const recovered = observe(page.host.storage.list());
  native.respond({ id: readRequest(native).id, result: [] });
  await expectResolved(recovered, []);
  assert.equal(native.registrations, 1);
  assert.equal(native.attempts.length, 2);
});

test("iframe 的 parent.postMessage 失败时立即拒绝而不等待超时", async (t) => {
  const page = createPage(t, { iframe: true, parent: {
    postMessage() { throw new Error("模拟父窗口发送失败"); }
  } });
  const state = observe(page.host.storage.list());
  assert.equal(page.clock.timers.size, 0);
  await expectRejected(state, "HOST_BRIDGE_UNAVAILABLE");
});

test("普通 callManager 在三十秒超时并清理计时器", async (t) => {
  const native = createNative();
  const page = createPage(t, { external: native.external });
  const state = observe(page.host.callManager("manager.listTools"));
  assert.equal(onlyTimer(page).delay, 30000);
  page.clock.advance(29999);
  await expectPending(state);
  page.clock.advance(1);
  await expectRejected(state, "BRIDGE_TIMEOUT");
  assert.equal(page.clock.timers.size, 0);
  native.respond({ id: readRequest(native).id, result: "超时后不应成功" });
  assert.equal(state.status, "失败");
});

for (const [name, method, call] of [
  ["生成工具", "manager.generateTool", (host) => host.callManager("manager.generateTool", { prompt: "时钟" })],
  ["更新工具", "manager.generateUpdate", (host) => host.callManager("manager.generateUpdate", { toolId: "example" })],
  ["修复工具", "manager.fixTool", (host) => host.callManager("manager.fixTool", { toolId: "example" })],
  ["聊天", "ai.chat", (host) => host.ai.chat({ messages: [] })]
]) {
  test(`AI ${name}桥接等待超过六百秒而不沿用普通超时`, async (t) => {
    const native = createNative();
    const page = createPage(t, { external: native.external });
    const state = observe(call(page.host));
    assert.equal(readRequest(native).method, method);
    const delay = onlyTimer(page).delay;
    assert.ok(delay > 600000, "AI 桥接应留出超过六百秒的等待窗口");
    page.clock.advance(600000);
    await expectPending(state);
    page.clock.advance(delay - 600000);
    await expectRejected(state, "BRIDGE_TIMEOUT");
    assert.equal(page.clock.timers.size, 0);
  });
}

test("同一时刻不同页面及同一页面连续请求的编号互不冲突", async (t) => {
  const firstNative = createNative();
  const secondNative = createNative();
  const first = createPage(t, { external: firstNative.external, random: 0.123456 });
  const second = createPage(t, { external: secondNative.external, random: 0.987654 });
  const states = [
    observe(first.host.storage.list()), observe(second.host.storage.list()),
    observe(first.host.storage.list()), observe(second.host.storage.list())
  ];
  const requests = [
    readRequest(firstNative), readRequest(secondNative),
    readRequest(firstNative, 1), readRequest(secondNative, 1)
  ];
  assert.ok(requests.every((request) => typeof request.id === "string" && request.id.length > 0));
  assert.equal(new Set(requests.map((request) => request.id)).size, 4);
  secondNative.respond({ id: requests[0].id, result: "另一页面的响应" });
  await expectPending(states[1]);
  await expectPending(states[3]);
  for (let index = 0; index < requests.length; index += 1) {
    const native = index % 2 === 0 ? firstNative : secondNative;
    native.respond({ id: requests[index].id, result: index });
    await expectResolved(states[index], index);
  }
});
