# QTools · 桌面启动器 & 小工具宿主

> 一个桌面应用，同时是三样东西：**系统程序启动器** + **小工具宿主** + **AI 工具工厂**。
> 用 .NET 10 Native AOT 构建为**单文件自包含可执行程序**（win-x64 实测约 13 MB），无安装器、无运行时依赖，拷走即用。

- **启动器**：小工具、手动添加的程序、自动索引的本机程序混排在同一个细长条列表里，按使用频次排序，支持拼音首字母 / 全拼搜索与真实程序图标；窗口拉宽后自动排成多列网格卡片，`Alt+Space` 全局热键随时呼出 / 隐藏。
- **小工具宿主**：托管纯静态 HTML/CSS/JS 小工具，本地 Kestrel 服务 + 系统 WebView 打开，通过注入的 `window.host` 垫层向页面提供 **AI / 存储 / 二进制文件 / 硬件捕获**能力——**API Key 只存在于宿主侧，页面永远拿不到**。
- **AI 工具工厂**：用一句自然语言生成完整小工具（流式输出、可取消），可对已有工具「AI 优化更新」，带版本管理与一键回滚；生成内容经静态安全审查，高危项需你显式确认。
- **备份与恢复**：一键 zip 备份 / 恢复 / 导入，也可推送到你自己的 Git 远程仓库做版本化备份。

完整设计（架构、威胁模型、桥接协议、里程碑记录）见 [docs/01-方案A-设计.md](docs/01-方案A-设计.md)。

## 下载

最新单文件版本见 [Releases](https://github.com/yesicoo/QTools/releases)：下载 `QTools-<版本>-win-x64.exe`（附 SHA256），双击即可运行。
发布由推送 `v*` 标签自动触发（`.github/workflows/release.yml`，构建 + 测试 + AOT + 自检通过后才上传），也可在 Actions 页手动触发。

## 平台支持（如实标注验证状态）

| 平台 | WebView 引擎 | 是否 WebKit | 状态 |
|---|---|---|---|
| Windows | **WebView2（Chromium）** | ❌ 不是 WebKit | ✅ 唯一真机验证过的平台 |
| macOS | WKWebView | 是 | ⚠️ 代码已写，未真机验证 |
| Linux | WebKitGTK 4.1 | 是 | ⚠️ 需系统 `libwebkit2gtk-4.1`，未真机验证 |

Windows 下需要 [WebView2 Runtime（Evergreen）](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10/11 一般已内置）。

系统托盘目前**仅 Windows 已实现**：关闭主窗口默认收起到托盘；macOS/Linux 暂无托盘实现，关闭主窗口即退出（与旧行为一致）。

## 统一启动器

主窗口是一个细长条列表，四类入口混排、统一搜索：

| 来源 | 说明 |
|---|---|
| **小工具** | 宿主托管的工具（内置示例 + AI 创建 + 导入） |
| **手动启动项** | 「添加程序」手动挑选：可执行文件路径 + 可选启动参数 / 工作目录 / 分类 / 图标字符 |
| **自动索引程序** | 启动后后台扫描开始菜单（含所有用户）与桌面，自动出现在列表里 |
| **在线工具** | 「添加网址」录入 URL 保存（名称 / 描述 / 分类 / 自定义图标与导入工具同一套字段）；点击用独立 WebView 窗口在线打开（不注入宿主桥接，与本地能力完全隔离） |

- **使用频次排序**：用得多的排前面；次数相同按最近使用，没用过的按名称。
- **首页只显示用过的条目**：小工具、手动添加的程序、在线工具创建时默认计 1 次并直接显示；自动索引的本机程序从 0 次开始、不占首页，但搜索随时能搜到，点开一次即计次并出现在首页。
- **拼音搜索**：首字母与全拼都行——输入 `wx` / `weixin` 都能搜到「微信」；导入工具与在线工具同样生效（如「图片压缩」→ `tpys` / `tupian` / `tupianyasuo`）。
- **自适应布局**：主窗口默认是细长条单列列表，拉宽后按宽度自动排成多列网格卡片（每列最小 240px），条目的右键原位菜单保持不变。
- **窗口尺寸记忆**：手动拖拽调整主窗口大小后，下次启动按上次的尺寸打开（存 `data/config.json` 的 `shell.mainWindowWidth/Height`，越界值自动回退默认 340×720）；工具/AI 创建/导入共用的内容窗口也会记住上次的位置和大小（`shell.contentWindowLeft/Top/Width/Height`），下次打开原位恢复，显示器变动导致位置失效时回退贴主窗口打开。
- **系统托盘（Windows）**：点主窗口关闭按钮默认只是**收起到托盘**；左键托盘图标显示主窗体，右键弹出**与主界面同风格的圆角菜单**——统一列表前 N 个工具（默认 10，可在设置页改为 1–20；图标 + 名称 + 类型，点击直接打开），以及红色「退出 QTools」——只有它才真正结束进程。菜单失焦或按 Esc 自动收起，条目过多时列表内部滚动。
- **真实图标**：自动提取程序图标并缓存；失败回退字母头像；小工具与在线工具支持自定义图标字符。
- **属性字段统一**：导入小工具与添加在线工具都填「名称 / 描述 / 分类 / 自定义图标」（在线工具多一个网址），列表徽标显示分类、自定义图标优先展示；**鼠标悬停条目会弹出描述提示**，右键「详情」里也能看到完整描述。
- **索引不空窗**：后台重新扫描期间旧索引始终可用，扫描完成后原子覆盖；同一程序的多个快捷方式（开始菜单 / 桌面 / 厂商子目录）会按**解析出的真实目标**去重，卸载器、帮助文档等垃圾快捷方式自动过滤。
- 手动启动项只保存**启动配置**，「移除」不会卸载系统里的程序；在线工具同样只存一条网址，删除不影响目标网站。
- **右键原位菜单**：右键任意条目，原位置带动画切换为「详情 / 属性 / 重命名（小工具、在线工具）/ 删除（移除 / 隐藏）」菜单；一次只开一个，点别处或按 Esc 还原。详情原位展开名称、目标、使用次数等；「属性」在资源管理器中定位文件；小工具与在线工具的「重命名」原位展开输入框，只改显示名（小工具的 id / 目录 / 版本不变，在线工具的 id / 网址 / 目录不变）；索引程序的「隐藏」持久化（`data/hidden-programs.json`），重新索引也不会再出现，且不会改动程序本身。

## AI 创建工具

主窗口点「AI 创建工具」→ 输入需求（如「做一个录音机，能录音、保存 webm、回放历史」）→ 流式生成 → 审查安全报告 → 保存并打开。

AI 接口自动识别 **OpenAI 兼容**与 **Anthropic 兼容**两种端点：DeepSeek 可填 `https://api.deepseek.com` 或 `https://api.deepseek.com/anthropic`；模型 `deepseek-flash` / `deepseek-v4-pro`。思考型模型的推理过程（`reasoning_content` / `thinking_delta`）会实时显示在生成预览里，不再出现长时间“卡住后突然出结果”。

- 提示词会**自动补全宿主规范**：单文件 HTML（便于导入导出）、中文界面、按需声明权限、优先用 `window.host` 能力，你只需要描述想要什么。
- 生成内容是**单文件 `index.html`**，允许引用 CDN / 外部资源与联网——单文件的目的是支持导入导出与备份，不是限制不能联网。
- 不满意可「修复此工具」：错误信息与现有源码回传 AI 继续改，**工具 ID 强制不变**。

安全审查分级：

| 级别 | 规则 |
|---|---|
| 致命（直接拒绝） | manifest 结构非法：id 规则、name/version/createdAt、entry 路径穿越、未知权限、window 尺寸越界 |
| 高危（需勾选确认） | `eval(` / `new Function(` / 动态 `import(`；疑似硬编码 Key（`sk-…`、`apiKey=`、`AKIA…`、`Bearer …`）；`allowEval`；自定义 CSP |
| 提示（仅告知） | 远程 `<script src="http…">`、远程 iframe；`fetch`/`XHR`/`WebSocket`/`sendBeacon` 外部请求；`document.write(`、`srcdoc`、声明了 `ai` 权限 |

### 导入本地 HTML

「导入工具」→ 选一个 `.html` → 填写名称与可选的描述 / 分类 / 自定义图标 → 宿主自动检测实际用到的能力写入 manifest（权限按需最小化）、自动生成工具 ID，走同一套安全审查。外部相对资源（如 `style.css`）会在导入前明确提示无法加载。

### 版本管理

每次 AI 优化更新前自动把当前版本存为快照；版本面板可**回滚到任意历史版本**、删除旧版本；`files/` 用户数据（录音、图片等）跨版本保留。

## 备份与恢复

- **zip 备份**：一键备份工具与数据，可恢复、可导入备份文件。
- **Git 备份**：把工具与配置推送到你自己的 Git 远程仓库（凭据由 git 自己管理，宿主不接触），也可从远程恢复——恢复前先探测分支，避免空仓库误报。

## window.host 能力

宿主在 HTML 响应的 `<head>` 之后注入 `bridge.js`（早于页面脚本），页面直接使用 `window.host`：

| 命名空间 | 权限 | 能力 |
|---|---|---|
| `window.host.storage.*` | `storage` | 键值存储（任意 JSON），按工具隔离：`get/set/remove/list` |
| `window.host.ai.chat` | `ai` | 宿主代理的 AI 对话（流式增量），Key 不出宿主 |
| `window.host.files.*` | `files` | 二进制文件存取（录音/照片/PDF…），落在 `tools/{id}/files/`，返回可直接用于 `<audio src>` 的 url |
| `getUserMedia` / `getDisplayMedia` | `media` | 麦克风 / 摄像头 / 屏幕捕获（标准 Web API，按权限授权） |
| `window.host.manager.*` | 仅主窗口 | 工具/启动项管理、AI 创建与修复、版本、备份、热键、内容窗口等 40+ 方法（`hostOnly`，普通工具页面不可调用） |

所有方法返回 Promise，失败 reject `{ code, message }`；流式方法（AI 生成/更新、git 推送/恢复）的增量信封与最终结果共用同一 id，可用 `manager.cancelRequest` 取消。

**桥接安全语义**：

- **桥接不在 HTTP 面上**——页面无法用 `fetch`/`XHR` 触达任何宿主能力；传输走 WebView 原生消息通道。
- `toolId` **以宿主侧窗口绑定为权威**，客户端自述值只是日志参考，恶意工具无法伪称别的 toolId 越权。
- 方法白名单 + manifest 权限校验 + 消息 ≤ 1 MB + 按方法超时；内部异常只进日志，页面只收到 `INTERNAL_ERROR`。
- 主窗口 iframe 内嵌工具走一次性内嵌凭证，随换页作废、随工具删除吊销。

## 安全模型

| 边界 | 措施 |
|---|---|
| 网络面 | 仅绑 `127.0.0.1` 随机端口、Host 校验、路径穿越多级防护、体量限制、扩展名白名单、`nosniff`、无写端点 |
| 桥接面 | 方法白名单、权限校验、toolId 以窗口绑定为准、payload 上限、按方法超时、错误不泄露内部细节 |
| Key | 只在宿主内存 + 系统保护存储（Windows DPAPI / 其他平台 AES-GCM 加密文件回退）；不进 `config.json`、不进日志（自动脱敏）、不进桥接响应 |
| 生成内容 | 保存前静态审查 + 高危项用户显式确认 + CSP 默认禁 `unsafe-eval` |

**CSP 边界**：工具页面允许引用 CDN / 外部资源、允许 `fetch`/XHR/WebSocket 访问外部接口（`connect-src 'self' https: wss:`）——远程脚本与外部请求只做提示不阻断；`'unsafe-eval'` 默认禁用（仅 `allowEval: true` 且用户显式确认才放开）。宿主自身页面仍是 `connect-src 'none'`，一切能力走 `window.host`。

## 快速开始

环境要求：**.NET 10 SDK**（`global.json` 锁定 10.0.100+）。

```bash
# 构建 + 开发运行（GUI 窗口）
dotnet build
dotnet run --project src/QTools.Host -r win-x64

# 无头自检：起真实 Kestrel 服务，27 项 HTTP 断言，退出码 0 = 全过
dotnet run --project src/QTools.Host -r win-x64 -- --self-test

dotnet test                      # 412 个单元/集成测试
node --test tests/app.test.cjs   # 前端行为级测试（vm 沙箱 + DOM shim）

# Native AOT 单文件发布（用户实际运行的形态）
dotnet publish src/QTools.Host/QTools.Host.csproj -c Release -r win-x64 -o publish/win-x64
```

发布产物是**单个 exe**：ASP.NET Core + PhotinoX + 全部前端资源全部内嵌，PhotinoX 原生库首次运行解压到数据目录并经 `DllImportResolver` 加载——「只拷一个 exe 到空目录」即可完整运行。

### 命令行开关

| 开关 | 说明 |
|---|---|
| `--self-test` | 无头自检（27 项 HTTP 断言后退出） |
| `--port <n>` | 固定服务端口（默认随机） |
| `--root <dir>` | 覆盖运行期数据根目录 |
| `--open-tool <id>` | 启动后直接打开指定工具 |
| `--open-settings` | 启动后直接打开设置窗口 |
| `--generate-tool <描述>` | 无头生成工具（`--yes` 跳过确认直接保存） |
| `--delete-tool <id>` | 删除工具（`--delete-storage` 连存储一起删） |
| `--dev` | 开发者模式（右键菜单 + DevTools + 详细日志） |
| `--help` | 帮助 |

退出码：`0` 成功；`1` 自检未通过；`2` 参数错误；`3` 工具不存在；`4` AI 未配置或调用失败；`5` 内容被安全审查拒绝；`6` 保存/删除失败；`7` 删除时工具不存在。

### 运行期数据目录

不在安装目录，只读位置也能正常工作：

| 平台 | 位置 |
|---|---|
| Windows | `%APPDATA%\QTools` |
| macOS | `~/Library/Application Support/QTools` |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/QTools` |

```
<数据根目录>/
├── apps/                          # 手动启动项（每个一个 app.json）
├── webtools/                      # 在线工具（每个一个 web.json，只存名称与网址）
├── tools/                         # 用户工具（tools/{id}/manifest.json + index.html + files/ + .versions/）
└── data/
    ├── config.json                # 宿主配置（不含 API Key）
    ├── program-index.json         # 本机程序自动索引（原子覆盖写）
    ├── usage.json                 # 使用频次统计
    ├── storage/{toolId}.json      # 按 toolId 隔离的工具存储
    ├── secrets/                   # DPAPI 密文 / AES-GCM 加密文件 + 密钥
    ├── icon-cache/                # 程序图标缓存
    ├── native/{rid}/              # 首次运行解压的 PhotinoX 原生库
    └── logs/qtools-YYYYMMDD.log   # 滚动日志（Key 自动脱敏）
```

## manifest 规范

每个工具一个目录，`manifest.json` 描述元数据：

```json
{
  "id": "hello-notes",
  "name": "Hello Notes",
  "version": "1.0.0",
  "description": "最小示例：通过 window.host.storage 存取本地记事本",
  "entry": "index.html",
  "permissions": ["storage"],
  "window": { "width": 520, "height": 720, "resizable": true },
  "createdAt": "2026-11-03T00:00:00+08:00",
  "allowEval": false,
  "csp": null
}
```

| 字段 | 规则 |
|---|---|
| `id` | `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`，必须与目录名一致 |
| `name` / `version` / `description` | ≤ 60 字符 / 语义化版本 / ≤ 300 字符 |
| `entry` | 工具目录内相对路径，禁止 `..`/绝对路径/盘符 |
| `permissions` | ⊆ `{ "storage", "ai", "media", "files" }`，**按需最小化** |
| `window` | `width`/`height` 320~3840；`resizable` 默认 true |
| `createdAt` | ISO-8601 带偏移 |
| `allowEval` / `csp` | 默认关闭 / null；两者在创建流程中都是高危项，需显式确认 |

校验失败的工具在列表中标注「损坏」并显示原因，不阻塞其他工具。

## 默认工具

| 工具 | 权限 | 说明 |
|---|---|---|
| **JSON 格式化** | 无 | 格式化 / 压缩 / 校验 JSON，语法错误定位到行列 |
| **二维码生成** | storage | 内置 QR 编码器（经 jsQR 端到端解码验证），可保存最近记录 |

## 测试

| 层 | 命令 | 数量 | 覆盖 |
|---|---|---|---|
| 单元/集成 | `dotnet test` | 412 | manifest 校验、桥接协议（真实 `BridgeDispatcher`）、权限、存储隔离、AI 解析容错、安全审查、备份/Git、索引原子覆盖与去重、流式取消 |
| 前端行为 | `node --test tests/*.cjs` | 129 | `app.js` / `bridge.js` / 示例工具页面（vm 沙箱 + DOM shim） |
| HTTP 自检 | `--self-test` | 27 | 真起 Kestrel：Host 校验、路径穿越、工具资源、设置页、品牌图标 |

## 已知 AOT 约束

| 约束 | 说明 |
|---|---|
| **原生库不进单文件 bundle** | .NET 10 实测 `IncludeNativeLibrariesForSelfExtract` 无效；本项目用「内嵌资源 + 启动解压 + `DllImportResolver`」解决 |
| 反射式 JSON 不可用 | `JsonSerializerIsReflectionEnabledByDefault=false`，所有序列化必须走 `AppJsonContext` 源生成，误用运行时抛异常 |
| 配置绑定禁用 | 不用 `IOptions<T>`/`ConfigurationBinder`；`config.json` 手写源生成读取 |
| 正则必须源生成 | 全部 `[GeneratedRegex]`，禁用 `RegexOptions.Compiled` |
| Release 不带 PDB | AOT 的 PDB 可能是 exe 的数倍，`DebugType=none` |
| `data/` 需可写 | 只读位置下原生库解压与配置写入会失败，用 `--root` 指到可写目录 |

## 故障排查

- **Windows 窗口起不来**：安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)；宿主启动前会预检并写日志。
- **Linux 启动即退**：确认 `libwebkit2gtk-4.1` 已安装；Wayland 有问题时用 `GDK_BACKEND=x11` 运行。
- **macOS 提示「已损坏」**：未签名产物被 Gatekeeper 拦截，执行 `xattr -dr com.apple.quarantine QTools`。
- **看日志**：`data/logs/qtools-YYYYMMDD.log` 按日期滚动（单文件超 5MB 轮转），Key/Authorization 已脱敏。
- **重复启动没反应**：宿主是单实例模式，第二个实例会被忽略（避免端口/窗口/存储混乱）。
- **索引里少了某个程序**：它可能只有「解析不出目标的快捷方式」（如 MSI 广告快捷方式），或名字命中了垃圾过滤（卸载/帮助/官网等）；可用「添加程序 → 浏览文件」手动添加。

## 项目结构

```
src/QTools.Host/
├── Program.cs              组合根：CLI → 路径 → 日志 → 原生库 → 单实例 → 起服务
├── Ai/                     AiClient（OpenAI 兼容，含 SSE 流式）、配置容错读取
├── Backup/                 zip 备份/恢复/导入 + Git 推送/远程恢复
├── Bridge/                 桥接协议：分发器 / 方法表 / 载荷校验
├── Creator/                AI 生成/更新 + 导入 + 内容安全审查
├── Launcher/               启动项 / 程序发现 / .lnk 目标解析 / 后台索引 / 使用频次
├── Server/                 本地服务 / 工具静态文件 / HTML 注入 / 自检
├── Security/               DPAPI / AES-GCM 密钥存储
├── Shell/                  窗口生命周期 / 窗口↔工具绑定 / 全局热键 / 系统托盘
├── Storage/                按 toolId 隔离的 JSON 键值存储
├── Text/                   拼音检索键（静态汉字→拼音表：首字母 + 全拼 + 多音字变体）
├── Tools/                  manifest 校验 / 版本管理 / 扫描 / 示例工具释放
└── Assets/host-ui/         主窗口与设置页（原生 HTML/CSS/JS，无框架无构建）
tests/QTools.Tests/         412 个单元 + 内存级集成测试
tests/*.test.cjs            129 个前端行为级测试
```

## 给开发者

- [AGENTS.md](AGENTS.md) —— 本仓库的工程约定：AOT 硬约束、桥接协议契约、真实踩过的回归陷阱（§7）、提交前检查清单。**动手前必读。**
- [docs/01-方案A-设计.md](docs/01-方案A-设计.md) —— 完整设计：架构决策、威胁模型、里程碑记录。

代码、注释、日志、UI 文案、文档全部使用中文，新增内容请保持一致。
