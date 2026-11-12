# AGENTS.md

给在本仓库工作的 AI agent 的项目约定。**先读这一页再动手**；产品用法见 [README.md](README.md)，完整设计见 [docs/01-方案A-设计.md](docs/01-方案A-设计.md)。

---

## 1. 项目是什么

**QTools** —— 一个 Windows/Linux/macOS 桌面应用，同时是：

1. **系统程序启动器**：用户手动添加系统里已安装的程序（`.exe` / `.lnk` / `.app` / `.desktop`），点一下就启动；也支持「添加网址」把常用 URL 保存为**在线工具**，点击用独立 WebView 窗口在线打开（不注入 `window.host`，与宿主能力隔离）。
2. **小工具宿主**：托管纯静态 HTML/CSS/JS 小工具，经 `127.0.0.1` 上的 Kestrel 提供服务，用系统 WebView 打开，并通过注入的 `window.host` 向页面提供 AI / 存储 / 二进制文件 / 硬件捕获能力。
3. **AI 工具工厂**：用自然语言生成工具、对已有工具做 AI 优化更新，带版本管理与回滚。

代码、注释、日志、UI 文案、文档**全部用中文**。新增代码请保持一致。

---

## 2. 技术栈事实（已固定，不要重新比较方案）

| 项 | 取值 | 说明 |
|---|---|---|
| 运行时 | **.NET 10** | `global.json` 锁 `10.0.100` + `rollForward: latestMinor` |
| 发布 | **Native AOT + 单文件 + SelfContained** | `PublishAot` / `PublishSingleFile` / `SelfContained` |
| 桌面壳 | **PhotinoX 5.4.0** | Photino.NET 的活跃维护分叉；命名空间仍是 `Photino.NET` |
| 本地服务 | **Kestrel**（ASP.NET Core） | 只绑 `127.0.0.1`，随机端口 |
| JSON | **System.Text.Json 源生成，仅此一条路** | 反射序列化在 csproj 里已被硬禁用 |
| 前端 | **原生 HTML/CSS/JS** | 无框架、无构建工具、无 CDN（指宿主自身页面；工具页面允许 CDN/外部资源） |
| 目标 RID 默认 | `win-x64` | 可由 `-r` 覆盖 |

> Avalonia 曾被评估并**否决**（单文件不打包原生库导致缺 `libSkiaSharp`，体积 19.4MB + 18MB 边车）。不要再提议换 UI 框架。

---

## 3. 常用命令

```bash
# 构建（Debug，够日常开发）
dotnet build

# 单元测试（当前基线 412 个）
dotnet test

# 开发运行（会开 GUI 窗口）
dotnet run --project src/QTools.Host -r win-x64

# 自检：起服务 → 打 27 项 HTTP 断言 → 退出码 0=全过
dotnet run --project src/QTools.Host -r win-x64 -- --self-test

# AOT 单文件发布（这是用户实际运行的形态，必须 0 warning）
dotnet publish src/QTools.Host/QTools.Host.csproj -c Release -r win-x64 -o publish/win-x64
```

**改动后的最低验证线**：`dotnet build` 0 error 0 warning → `dotnet test` 全绿 → `--self-test` 26/26 → `dotnet publish` **0 AOT warning**。

常用 CLI 开关（`Cli/CommandLine.cs` 是唯一真源）：`--self-test` `--port` `--root` `--open-tool <id>` `--open-settings` `--generate-tool` `--yes` `--delete-tool` `--delete-storage` `--dev` `--help`。

---

## 4. 目录地图

```
src/QTools.Host/
├── Program.cs              组合根：CLI 解析 → 路径 → 日志 → 原生库 → 单实例 → 起服务 → 分派
├── AppInfo.cs / PlatformInfo.cs
├── Ai/                     AiClient（OpenAI 兼容，含 SSE 流式）、AiOutputParser（容错解析）
├── Backup/                 BackupService（zip 备份/恢复/导入）/ GitBackup（git 推送/远程恢复）
├── Bridge/                 桥接协议：BridgeDispatcher / Methods / BridgeMessage / PayloadReader
├── Cli/CommandLine.cs      手写参数解析（不引 System.CommandLine）
├── Configuration/          AppPaths（路径真源）/ AppConfig
├── Creator/                ToolCreator（AI 生成+更新）、ToolImporter（本地 HTML 导入）、ContentSecurityScanner
├── Launcher/               AppEntry / WebToolEntry / AppRegistry / AppStore / WebToolStore / AppLauncher / ProgramDiscovery
├── Logging/Log.cs          滚动文件日志 + Key 脱敏
├── Native/NativeLibraries 内嵌原生库解压 + DllImportResolver
├── Security/               ISecretStore + DPAPI / AES-GCM 两套实现
├── Serialization/          AppJsonContext —— 全宿主唯一 JSON 上下文
├── Server/                 LocalServer / ToolStaticFileMiddleware / HtmlRewriter / HostAssetEndpoint / SelfTest
├── Shell/                  ShellHost（窗口生命周期）/ WindowRegistry（窗口→toolId 绑定）
├── Storage/ToolStorage.cs  按 toolId 隔离的 JSON 键值存储
├── Text/                   PinyinInitials（静态汉字→拼音表：首字母 + 全拼检索）
├── Tools/                  ToolManifest+校验 / ToolScanner(注册表) / ToolVersionStore / ToolFiles / SampleTools
└── Assets/
    ├── host-ui/            index.html + app.js + app.css + settings.html + settings.js + bridge.js
    └── samples/            默认工具：json-formatter、qrcode-generator

tests/QTools.Tests/         22 个测试文件，纯单元 + 内存级集成
```

运行期数据根目录（**不在仓库内**）：Windows `%APPDATA%\QTools`，macOS `~/Library/Application Support/QTools`，Linux `${XDG_DATA_HOME:-~/.local/share}/QTools`。

---

## 5. 硬约束（Native AOT，写代码前必须知道）

1. **JSON 只能走源生成**。任何需要序列化的类型都必须加进 `Serialization/AppJsonContext.cs` 的 `[JsonSerializable]`。反射序列化已被 `JsonSerializerIsReflectionEnabledByDefault=false` 关死，误用会**运行时抛异常**。
2. **不要用 `IOptions<T>` / `ConfigurationBinder`**（反射 binder 在 AOT 下会炸）。`config.json` 用手写源生成读取（见 `Ai/ConfigStore`）。
3. **原生库不会进单文件 bundle**（.NET 10 实测，`IncludeNativeLibrariesForSelfExtract` 无效）。PhotinoX 原生库由 csproj 以 `EmbeddedResource` 嵌进程序集、`NativeLibraries.Bootstrap` 在**任何 PhotinoX API 之前**解压到 `data/native/{rid}/` 并注册 `DllImportResolver`。改动启动顺序时保持这个先后关系。
4. **`TreatWarningsAsErrors=true`**。自己的代码必须零警告；依赖包的 AOT 警告在 publish 阶段单独评估。
5. 不要引入需要反射、动态代码生成、`Assembly.Load`、运行时编译的库。

---

## 6. 桥接协议契约

**入口唯一**：`Bridge/BridgeDispatcher.DispatchAsync(authoritativeToolId, rawMessage)`。校验顺序（改动时不要打乱）：

1. 消息体 ≤ 1 MB
2. 信封反序列化非空，`id` / `method` 必填
3. 方法必须存在方法表里，否则 `UNKNOWN_METHOD`
4. **权限校验**：`hostOnly` 方法只允许 `toolId == "__host__"`；其余按方法声明的 `ToolPermission` 与 manifest 比对，不足即 `PERMISSION_DENIED`
5. 业务载荷校验（`PayloadReader`）
6. 执行 + 回包

**关键安全语义**：

- `toolId` **永远以宿主侧窗口绑定为准**（`WindowRegistry` 的 `Guid → toolId`），客户端字段只是日志参考，不可信。
- 响应信封：成功 `{"id":..., "result":...}`，失败 `{"id":..., "error":{code,message}}`。
- 内部异常只进日志，页面只收到 `INTERNAL_ERROR`（不泄露堆栈）。
- **主窗口 iframe 内嵌工具走「内嵌凭证」**：`manager.embedTool`（hostOnly）签发一次性凭证（`Shell/EmbedTokenRegistry`，随机 128 位 hex），前端把 `?bt=` 从 iframe URL 带进桥接信封的 `token` 字段；分发器仅在「窗口绑定 == `__host__`」时用凭证把身份解析为对应 toolId，独立工具窗口忽略 `token` 字段。凭证随换页被替换而作废、随工具删除被吊销（见 `ShellHost.DeleteToolFilesAsync`）。
- iframe 内接收不到原生响应（WebView2 `SendWebMessage` 只投递顶层 frame），`bridge.js` 内置「iframe ↔ 宿主页」同源 postMessage 中继层；修改传输逻辑时保持 `window.host` 先赋值的顺序（见 7.1）。
- `window.host` 的公开面在 `Assets/host-ui/bridge.js`；新增方法必须同时改三处：`BridgeDispatcher.Register`、`Bridge/Methods.cs`、`bridge.js`。

**权限枚举**（`Tools/ToolManifest.cs`）：`storage` / `ai` / `media` / `files`。新增权限要同步 `TryParsePermission`、manifest 校验、`ContentSecurityScanner.DetectPermissions`、`BridgeDispatcher` 方法表。

---

## 7. 关键不变量 / 回归陷阱 ⚠️

这一节记录的都是**真实踩过的坑**：编译器不报错、单元测试也不一定覆盖，但会让产品功能整体失效。改动相关代码前请先读对应条目。

### 7.1 `bridge.js` 必须**先**暴露 `window.host`，再做传输探测

历史 bug：设置窗口打不开 + 启动器永久空白。原因是旧实现先探测传输层、先注册 `window.external.receiveMessage`，再赋值 `window.host`。文档创建早期 `receiveMessage` 偶发抛错 → 整个脚本中断 → `window.host` 永远是 `undefined` → 页面所有桥接调用静默失效。

**约束**：`window.host = {...}` 必须在前；传输探测、receiver 注册必须包 `try/catch`；`call()` 内每次未就绪时惰性重探测。**不要**把赋值挪回探测之后。

### 7.2 `app.js` 的事件绑定必须空值安全

历史 bug：头部按钮从 `create-tool` 改名后，`$("create-tool").addEventListener(...)` 命中 `null` → `TypeError` → 整个 IIFE 中断 → 所有 handler 都没注册、启动逻辑也没跑。

**约束**：一律用 `on(id, event, handler)`（元素不存在静默跳过），不要写 `$("x").addEventListener(...)`。同理，`getElementById` 结果在模块顶层使用时必须有判空。

### 7.3 版本目录必须点号前缀，且三处都要挡

`.versions/{version}/` 存历史快照。它**绝不能**出现在静态服务里。

**约束**（三处缺一不可）：
- `ToolVersionStore.VersionsDirectoryName = ".versions"`（点号前缀）
- `Server/ToolStaticFileMiddleware` 规则 3b：拒绝点号前缀路径段
- `ToolRegistry.Rescan` / `SampleTools` / `ProgramDiscovery` 等所有扫描器：跳过点号前缀目录

### 7.4 工具就是 `tools/{id}/` 这一层，**禁止递归扫描**

历史 bug：自检用 `EnumerateFiles(..., SearchOption.AllDirectories)` 找 `manifest.json`，把 `.versions/1.0.0/` 也当成工具，探测 `/tools/1.0.0/index.html` 必然 404。

**约束**：枚举工具 = `EnumerateDirectories(toolsRoot)` 一层 + 每个目录里找 manifest。别用 `AllDirectories`。

### 7.5 AI 输出解析：剥围栏整体解析优先，括号配对只做兜底

历史 bug：`indexHtml` 内含大量引号时，括号配对的引号奇偶性会错位，深度无法归零 → 明明合法的 JSON 被判 `AI_OUTPUT_UNPARSEABLE`。

**约束**：`AiOutputParser.Parse` 的路径是「剥 ``` 围栏 → 整体 `JsonDocument.Parse` → 失败才退回括号配对」。不要为了"简单"只留括号配对。回归测试见 `CreatorTests.ParsesPowerShellEscapedPayload`。

### 7.6 AI 更新不得改变工具身份

**约束**：`ToolCreator.UpdateAsync` 里 `manifest with { Id = tool.Id, Entry = "index.html", Name = tool.Name }` 这三行是硬锁，防止 AI 把工具改头换面。回归测试见 `AiUpdateFlowTests.UpdatePreservesToolIdentity`。

### 7.7 `Log.Initialize` 绝不能抛

历史 bug：第二个实例拿不到日志文件锁 → 未捕获 `IOException` → .NET fail-fast → 退出码 `0xC0000409`，Windows 弹「基于堆栈的缓冲区溢出」。那不是真的溢出。

**约束**：`Log.Initialize` 保持非抛（`FileShare.ReadWrite` → PID 后缀兜底 → 仅控制台）。单实例 `Mutex("QTools.Host.SingleInstance")` 保持存在。

### 7.8 删除工具必须拆成「UI 线程部分」和「纯 I/O 部分」

历史 bug：`--delete-tool` 无头模式把删除逻辑 marshal 到 UI 线程，但无头模式没有消息循环 → 死锁。

**约束**：`CloseToolWindowsAsync`（需 UI 线程）与 `DeleteToolFilesAsync`（纯 I/O，可直接调用）保持分离。

### 7.9 工具页面 CSP 的边界（2026-11 政策调整：允许联网/CDN）

单文件 HTML 的目的在于**导入导出与备份，不是禁网**。`ToolStaticFileMiddleware` 里 `DefaultToolCsp` 对 script/style/img/font/media/frame 放开 `https:`，`connect-src 'self' https: wss:`——工具页面可以引用 CDN、可以 fetch/XHR/WebSocket 外部接口。

仍然固定不变：`base-uri 'none'`、`object-src 'none'`、`form-action 'none'`、`frame-ancestors 'self'`，且 **eval 默认禁止**——只有 `manifest.allowEval=true`（需用户显式确认）才切到 `ToolCspAllowEval`。宿主自身页面（host-ui）仍是 `connect-src 'none'`，一切能力走 `window.host`。

安全审查把远程脚本 / 远程 iframe / 外部网络请求列为 **Warning（提示级）**——保存前向用户明示但不阻断；eval / 硬编码 Key 仍是 High，manifest 校验失败仍是 Fatal。不要把 CSP 重新收紧回禁网，也不要把远程资源降为静默。

### 7.10 流式推送信封与请求同 id，取消走 `manager.cancelRequest`

流式方法（AI 生成/更新、gitPush/gitRestore）的增量信封是 `{"id":<请求id>,"stream":{"delta":...}}`，与最终结果共用 id；`bridge.js` 的 `callManagerStream` 按 id 分流。分发器为每个请求挂 CTS，`manager.cancelRequest` 按 id 取消；用户取消在 AI 链路映射 `AI_CANCELLED`、在分发器兜底 `REQUEST_CANCELLED`，前端据此显示「已取消」而非报错。改传输层时不要破坏「同 id 分流」与「取消不泄露为 INTERNAL_ERROR」。

### 7.11 Git 远程恢复必须先 `ls-remote --heads` 再 `clone --branch`

历史 bug：本地 bare 仓库 HEAD 指向未出生分支时，普通 `git clone` 什么都不拉取（"cloned an empty repository"），恢复误报 `GIT_RESTORE_INVALID`。

**约束**：`GitBackup.RestoreAsync` 先 `ls-remote --heads` 取分支列表（优先 main/master），再 `clone --depth 1 --branch <名>`；空仓库给出「需先推送」的明确错误。回归测试见 `BackupAndGitTests.PushAndRestoreRoundTripAgainstLocalBareRepo`。

### 7.12 统一启动列表的四条硬约束（索引 / 频次 / 双模式 / 去重）

2026-11 主窗口重构为细长条统一列表（小工具 + 手动启动项 + 自动索引程序 + 在线工具混排）时确立：

1. **后台索引期间旧索引必须始终可用**：`ProgramIndex` 启动即加载旧索引供列表使用，后台重扫完成后才「临时文件 + 原子 `File.Move` 覆盖 + 整体换内存快照」；扫描失败保留旧索引。绝不允许"正在索引 → 列表为空"。
2. **索引 id 必须稳定**：`BuildId = "idx-" + SHA256(小写目标路径) 前 12 位`，跨次扫描不变——使用频次按 id 记账，id 漂移会清零频次。手动启动项按目标路径优先去重。
3. **主窗口 / 内容窗口是同一页面两种模式**：`?mode=content&view=tool|create|import` 由宿主全 URL 打开内容窗口，`app.js` 里 `isContentMode` 分流（主窗口隐藏 `.content`，内容窗口隐藏侧栏）。改前端路由时不要把两个模式合并回一页。
4. **本机程序必须按解析后的真实目标去重，并过滤垃圾快捷方式**：同一个程序的 .lnk 会同时出现在所有用户开始菜单 / 用户开始菜单 / 桌面 / 厂商子目录，按 .lnk 自身路径去重必然产生重复。`ProgramDiscovery.DedupePrograms` 用 `LnkTargetResolver` 解析 .lnk 的 LocalBasePath（MS-SHLLINK 字节解析，无 COM；LinkInfo 是 7 个 dword，LocalBasePathOffset 在 +16，LocalBasePath 实测恒为 ANSI，仅当字节模式 `x 00 x 00` 时才按 UTF-16 读）得到目标路径作为去重键；解析不出目标的 .lnk 回退用文件名去重；解析出非可执行目标（.chm/.url/文件夹）直接丢弃。`ProgramIndex.BuildEntries` 的 `IsJunkName` 过滤卸载/安装/帮助/手册/官网/许可等垃圾名（中文子串匹配、英文 `\b` 整词匹配——`Helper`/`AppUpdate` 必须存活，`unins000` 必须命中）。改动去重或过滤时不要退回「按 .lnk 路径去重」的旧实现。

回归测试见 `LauncherIndexTests` / `ProgramDedupeTests` / `ShellMathTests` / `UnifiedLauncherBridgeTests` / `tests/app.test.cjs`。

### 7.13 仓库行尾强制 LF（.gitattributes）

历史 bug：GitHub Actions 的 Windows runner checkout 时按全局 `core.autocrlf=true` 把 LF blob 签出成 CRLF，
内嵌示例工具资源随之变 CRLF，`SampleToolsTests` 三个 LF 断言在 CI 全挂（本地全绿，纯环境差异）。

**约束**：仓库根保留 `.gitattributes`（`* text=auto eol=lf`，png/ico 标 binary），所有文本文件以 LF 提交与签出；
不要依赖任何人本地的 `core.autocrlf` 配置，也不要删这个文件。CI（`.github/workflows/dotnet-desktop.yml`）
跑 Release 构建 + 单测 + 前端测试 + AOT 发布 + `--self-test`，push 后以上全部是远端门禁。

发布同样在 Actions 里完成（`.github/workflows/release.yml`）：推送 `v*` 标签（或在 Actions 页手动触发并填版本号）
→ 构建 + 全量测试 + AOT 单文件 + 自检 → `gh release create` 自动创建 Release 并上传
`QTools-<版本>-win-x64.exe` 与同名 `.sha256`（`GITHUB_TOKEN` + `permissions: contents: write`，无需额外 Secret）。
版本号通过 `-p:Version=<标签去 v>` 注入，保证 exe 内 `AppInfo.Version` 与 Release 一致；同版本重复发布会失败，属预期。

### 7.14 拼音检索必须同时覆盖首字母与全拼，且不依赖运行时编码

历史 bug：旧实现用 `Encoding.GetEncoding("GB2312")` 按区位反查声母，只能给首字母、没有全拼；
用户对着「图片压缩」输入 `tupian` / `tupianyasuo` 毫无结果（仅 `tpys` 能命中），导入工具与在线工具受影响最明显。

**约束**：`Text/PinyinInitials.cs` 只依赖静态表 `Text/PinyinData.Generated.cs`（GB2312 6763 字，多音字按常用度排列，
数据源 mozillazg/pinyin-data，MIT），首字母 / 紧凑全拼 / 音节全拼 / 多音字变体全部由 `BuildSearchKey` 写入
`searchKey`，前端仍只做子串匹配。**不要**改回运行时 `Encoding`/ICU 反查（Native AOT 与剪裁下不可靠），
也不要手工编辑生成表。改动后跑 `PinyinInitialsTests`，并用真实数据（「图片压缩」→ `tupian`/`tpys`）回归。

### 7.15 宽窗口是网格布局，靠 CSS 媒体查询自适应列数

主窗口默认是 340px 细长条单列列表；`@media (min-width: 540px)` 下 `ul.nav-list` 改为
`grid-template-columns: repeat(auto-fill, minmax(240px, 1fr))`，按窗口宽度自动排成 2/3/4 列卡片
（条目加边框与底色），整块限宽 1180px 居中。断点取 540px 而不是 520px：侧栏内宽约等于窗口宽减 36px，
520px 时内宽不足 2×240+7=487 只能排 1 列，卡片样式会先于多列出现。**约束**：不要给 `.nav-item` 写死宽度，也不要把
badge / 使用次数拆成表格列——条目结构保持「图标 / `.nav-label` / `.nav-badge`」的自适应卡片，
右键原位菜单、重命名、隐藏/删除都依赖 `li` 内的原位切换。内容窗口（`mode-content`）不受影响。

### 7.16 关闭主窗口 ≠ 退出（Windows 托盘）

Windows 下主窗口 `Closing` 会先取消再收起到托盘（`Shell/TrayIcon.cs`：Shell_NotifyIcon + 独立 STA 消息线程；`ShellHost` 里 `_tray` 非空才拦截），只有菜单「退出 QTools」经 `manager.exitApp` 置 `_exitRequested` 后才放行关闭。左键托盘图标显示主窗体（不切换隐藏）；右键弹出**无边框 HTML 弹层**（`host-ui/tray-menu.html`，圆角 + 主题色，失焦/Esc 收起），前 N 项（默认 10，`shell.trayMenuItems` 配置 1–20，设置页可改）由 `TrayMenuPlanner` 按统一列表顺序实时读取并经 `manager.trayMenuList` 下发，点击走与界面一致的 `manager.openContent / openWebTool / launchApp`；条目过多时弹层高度夹到工作区、列表内部滚动。**约束**：`Closing` 里不要同步隐藏窗口（必须 marshal 到 UI 线程）；托盘线程不得直接操作 Photino 窗口；弹层是独立 Photino 窗口，必须 `AttachBridge` 后才会处理 `manager.*`；非 Windows 无托盘、关闭即退出；无头预览（`--generate-tool --yes` 的 `enableTray: false`）保持关闭即退出，避免自动化脚本挂在托盘上；资源管理器重启（`TaskbarCreated`）后必须重新 `NIM_ADD`。

**弹层显示的坑（2026-11 修复）**：`RegisterInitialContentLoadedHandler` 按 PhotinoX 文档只在窗口**首次**导航完成时触发一次。弹层复用窗口每次右键都会 `Load(url)` 刷新列表，但该事件不再回调，导致页面明明加载了、窗口却一直隐藏（用户表现为「右键没菜单」）。**约束**：弹层显示以页面渲染完成后回传的 `manager.trayMenuReady` 为准（`tray-menu.js` → `ShellHost.NotifyTrayMenuReadyAsync` → `ShowTrayMenuWindowIfPending`），首次的 `InitialContentLoaded` 只当补充；`ShowTrayMenuAsync` 里另有 1.2s 兜底超时防止脚本异常时永远不显示。刚显示的弹层可能因前台锁拿不到焦点而立刻收到 `Deactivated`，`ShowTrayMenuWindowIfPending` 记录 `_trayMenuShownAt`，800ms 宽限期内忽略失焦（并显式 `SetForegroundWindow`）；宽限期后真正的失焦/切窗才收起。回归验证要直接查 Win32 `IsWindowVisible(弹层)`，不能只看 CDP 页面或截图——页面在隐藏窗口里也照样渲染。

**DPI 坐标空间的坑（2026-11 修复）**：托盘消息线程运行在 96-DPI 虚拟化上下文，`GetCursorPos` 返回逻辑坐标；Photino 窗口所在 UI 线程是 DPI 感知的，`GetMonitorInfo` 返回物理像素，`MoveTo` 也按物理像素定位，而 `SetSize` 按 DIP→物理放大（150% 下 288 宽实际 432 像素）。在托盘线程取光标再拿到 UI 线程定位，会让弹层落在约 1/1.5 的位置（缩放屏上表现为「跑到屏幕中间」而不是托盘旁）。另外应用内实测 `GetScaleFactorForMonitor` 可能返回 140 而窗口 DPI 是 144，不可作为换算依据。**约束**：光标位置、显示器工作区、`GetDpiForMonitor` 一律在 UI 线程（`ShowTrayMenuAsync` 内）用物理坐标读取，弹层像素尺寸 = DIP × dpi/96，再交给 `TrayMenuPlacement.Compute` 与 `MoveTo`；不要在托盘线程换算坐标。

### 7.17 窗口几何记忆（主窗口尺寸 + 内容窗口位置/尺寸，config.json）

用户拖拽调整主窗口大小后，`RegisterSizeChangedHandler` 拿到的是 **DIP 尺寸**（与 `SetSize` 同一坐标空间，150% 缩放下 1700 物理像素对应 1133 DIP），防抖 500ms 后写入 `config.json` 的 `shell.mainWindowWidth/Height`；启动时 `MainWindowSize.Resolve` 校验范围（280×480 ～ 8000×6000，越界按维度回退默认 340×720），再按当前显示器工作区（DIP）夹一次。

内容窗口（`OpenContentAsync`）另外记住 **位置（物理像素）+ 尺寸（DIP）**：`LocationChanged` / `SizeChanged` 防抖 500ms、关闭窗口时立即写入 `shell.contentWindowLeft/Top/Width/Height`；恢复时 `ContentWindowBounds.ResolveSize` 校验尺寸（560×420 ～ 8000×6000，默认 920×680），`ResolveContentWindowOrigin` 用 `MonitorFromPoint(MONITOR_DEFAULTTONULL)` 探测保存点是否仍在任一显示器上——失效则回退 `WindowDock` 贴主窗口。位置用物理像素是因为 `MoveTo` 按物理像素定位（见 7.16 DPI 坑），尺寸用 DIP 是因为 `SetSize` 按窗口 DPI 放大；保存几何用 Win32 `GetWindowRect` + 显示器 DPI 折算，不依赖 Photino 的 `Left/Top` 属性单位。**约束**：最大化/最小化/全屏状态不落盘；复用已打开的内容窗口时不要再 `DockContentToMain`（否则用户拖好的位置每次打开工具都会被重置）；所有 `Persist*` 必须「Load → with 更新 → Save」，不要整份覆盖配置。

---

## 8. 平台事实（不要假装全平台都是 WebKit）

| 平台 | WebView 引擎 | 是否 WebKit | 状态 |
|---|---|---|---|
| Windows | **WebView2（Chromium）** | ❌ **不是 WebKit** | ✅ 唯一真机验证过的平台 |
| macOS | WKWebView | ✅ | ⚠️ 代码已写，**未真机验证** |
| Linux | WebKitGTK 4.1 | ✅ | ⚠️ 需系统 `libwebkit2gtk-4.1`，**未真机验证** |

密钥存储同理：Windows 用 DPAPI，macOS/Linux 走 AES-GCM 加密文件兜底（Keychain / libsecret 原生互操作**故意未实现**）。

系统托盘同理：**仅 Windows 实现**（`Shell/TrayIcon.cs`，Shell_NotifyIcon + 独立消息线程）。Windows 下关闭主窗口默认收起到托盘，托盘「退出 QTools」才真正结束进程；macOS/Linux 无托盘，关闭即退出。

**回答"支持哪些平台"时必须带上验证状态**，不要把"代码写了"说成"验证过了"。

---

## 9. 代码约定

- **一个类一个职责，纯函数优先**：校验器（`ToolManifestValidator` / `AppEntryValidator`）、`ContentSecurityScanner` 都是纯静态函数，便于单测。新增规则请沿用。
- **路径唯一真源是 `Configuration/AppPaths.cs`**。不要在别处拼 `%APPDATA%` 或 `AppContext.BaseDirectory`。
- **新增可序列化类型 → 立刻进 `AppJsonContext`**。
- **桥接方法必须 `hostOnly: true`**（除非明确要开放给普通工具），并显式声明所需 `ToolPermission`。
- **日志用 `Log.Info/Warn/Error/Debug`**，`Log.Debug` 只在 `--dev` 下输出。日志**绝不**记录 API Key（已有 `Redact()` 正则，新增敏感字段请补进去）。
- **错误用 `BridgeException(code, message)`**，code 是全大写短横线常量（如 `PERMISSION_DENIED` / `APP_TARGET_NOT_FOUND`），页面按 code 判断。
- **宿主自身页面（host-ui）不写外链、不写 CDN、不引框架**（工具页面允许 CDN/外部资源与联网，见 7.9）。图标用内联 SVG 或字符。

---

## 10. 脚本与测试约定（PowerShell 的坑）

历史上大量时间浪费在这里，写验证脚本前务必看：

1. **PowerShell 5.1 按 GBK 读 `.ps1`**。脚本里出现中文字面量会被解码破坏，正则匹配全部失配（曾导致验收脚本"全部失败"其实产品是好的）。
   → **验证脚本一律纯 ASCII**：用 ASCII 标记（日志里的 `-> ok`、`GET /`、`PASS`/`FAIL`）做断言，别用中文正则。
2. **`$args` 是 PowerShell 自动变量**，不能当函数参数名（会静默破坏参数传递）。
3. **`Start-Job` 会序列化参数**：不能把 `HttpListener` 等活对象传进去，要在 job 内部创建。
4. **`ProcessStartInfo` + `RedirectStandardOutput` 读 `.Result` 会死锁**（进程未退出时管道未关）。要重定向就先 `WaitForExit`，或改用 `Start-Process`（不重定向）。
5. **PowerShell 里 `"$(id)"` 会被当子表达式求值**。生成含 `$(...)` 的代码时用编辑工具写文件，别用字符串拼接。

**测试分层**：
- `dotnet test`：纯单元 + 内存级集成（真实 `BridgeDispatcher.DispatchAsync` + 临时目录）。当前 412 个（含内嵌凭证、索引程序隐藏、拼音首字母/全拼检索、备份/Git、流式取消链路、索引原子覆盖、内容窗口初始定位、在线工具、原位菜单定位、小工具/在线工具重命名、统一属性字段、托盘菜单规划与弹层定位、主窗口尺寸记忆、内容窗口位置/尺寸记忆）。
- `node --test tests/*.cjs`：前端行为级测试（vm 沙箱 + DOM shim，跑 `app.js`/`bridge.js`/示例工具页面）。当前 129 个。注意：vm 沙箱里创建的对象带另一套原型，断言前先用 `JSON.parse(JSON.stringify(...))` 归一化。
- `--self-test`：真起 Kestrel，27 项 HTTP 断言（host 头校验、路径穿越、工具资源、设置页、品牌图标；其中工具资源每工具 3 项，数量随已装工具数浮动）。
- ad-hoc 验收脚本：**不入库**，历史脚本在 `%TEMP%`。GUI 真机验证可用 WebView2 远程调试：启动时设 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port> --remote-debugging-address=127.0.0.1`，再用 CDP（`/json/list` + WebSocket）注入 JS / 截图；日志断言（`-> ok`、`已打开...窗口`）仍是最轻量的手段。

---

## 11. 提交前检查清单

- [ ] `dotnet build` → 0 error / 0 warning
- [ ] `dotnet test` → 全绿（当前基线 **412**）
- [ ] `node --test tests/app.test.cjs` → 全绿（改过 `app.js` 时必须跑）
- [ ] `--self-test` → **27 PASS / 0 FAIL**，退出码 0
- [ ] `dotnet publish -c Release` → **0 AOT warning**
- [ ] 新增可序列化类型已加进 `AppJsonContext`
- [ ] 新增桥接方法三处齐（dispatcher / Methods / bridge.js），且 `hostOnly` + 权限正确
- [ ] 改了 `app.js`/`bridge.js` 后：**没有裸 `$("x").addEventListener`**，`window.host` 赋值仍在探测之前
- [ ] 改了扫描逻辑后：不会把 `.versions/` 当工具
- [ ] 涉及平台相关代码 → 明确标注验证状态

---

## 12. 不要做的事

- ❌ 换 UI 框架（Avalonia 已评估否决）、换 JSON 方案（反射序列化不可用）
- ❌ 引入需要反射 / 动态代码 / 运行时编译的依赖
- ❌ 把反射 JSON 当作"临时方案"——运行时会炸，不是编译期
- ❌ 放宽 `hostOnly` 或跳过权限校验
- ❌ 在工具页面 CSP 里默认放开 `unsafe-eval`（eval 只能经 allowEval 显式确认；联网/CDN 已按 7.9 放行，不要反向重新收紧）
- ❌ 把 API Key 写进日志、命令行参数或桥接响应（只能进系统保护存储）
- ❌ 用中文写 PowerShell 验证脚本
- ❌ 在未真机验证的情况下声称某平台"已支持"
