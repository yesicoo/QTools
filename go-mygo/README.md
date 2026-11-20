# QTools · Go + mygo 移植版

这是 QTools 的 **Go 语言重写版**：用 [mygo](https://github.com/egoist/mygo)（纯 Go、无 cgo 的桌面框架）
重新实现 .NET 版宿主，前端复用同一份 `host-ui`、桥接协议与数据目录。

> 目标：验证同一套前端、同一份数据与同一套桥接契约，换到 Go + mygo 后能否保持功能完整。
> 当前仅 **Windows 真机验证**；macOS / Linux 代码路径存在，但未构建、未验证。
>
> 本项目所有工作（含本移植分支）均由 DeepSeek 开发完成。

版本：**1.3.0** · 对应 `main` 分支的 .NET 版与 `rust-tauri-port` 分支的 Rust 版。

---

## 1. 已完成的功能

| 模块 | 状态 |
|---|---|
| 主窗口统一列表（小工具 / 手动启动项 / 自动索引程序 / 在线工具） | ✅ Windows 验证 |
| 拼音首字母 + 全拼搜索、使用频次排序、首页只显示用过的条目 | ✅ |
| 小工具独立窗口、置顶 / 钉住、收缩菜单（刷新 / 置顶 / 钉住 / 版本管理）、几何记忆 | ✅ |
| 内容窗口（创建 / 导入 / 版本管理）、设置窗口、几何与置顶 / 钉住状态 | ✅ |
| 浅色 / 深色 / 跟随系统主题；品牌图标写入窗口与托盘；工具页实时换肤 | ✅ |
| AI 生成 / 优化更新 / 修复：OpenAI 与 Anthropic 协议、SSE 流式思考与正文分离、取消 | ✅ |
| 工具存储、二进制文件桥接、清单校验、脚本安全审查、版本快照与回滚 | ✅ |
| 导入 HTML、单条目 `.qtools` 导出 / 导入（tool / app / web） | ✅ |
| Windows 真实程序图标、开始菜单与桌面索引（.lnk 字节解析、真实目标去重、垃圾名过滤） | ✅ |
| 在线工具独立窗口，**不注入宿主桥接**；即使网址指向 127.0.0.1 也被分发器拒绝 | ✅ |
| 全局热键 `Alt+Space` 呼出 / 隐藏主窗口 | ✅ |
| 关闭主窗口收起到托盘；左键显示主窗口；右键原生菜单（列表前 N 项 + 打开 / 设置 / 退出） | ✅ |
| Windows 系统通知（mygo Notification → 托盘气泡）；`--notify`；已运行实例经单实例管道转发 | ✅（通知视觉受系统通知设置影响） |
| 创建桌面快捷方式（目标 `QTools.exe --open-tool <id>`）；单实例转发打开 | ✅ |
| zip 备份 / 恢复 / 导入；Git 远程推送 / 恢复（先 `ls-remote` 再浅克隆） | ✅ |
| 命令行：`--self-test` / `--port` / `--root` / `--open-tool` / `--open-settings` / `--generate-tool` / `--yes` / `--delete-tool` / `--delete-storage` / `--notify` / `--dev` / `--version` | ✅ |
| Windows DPAPI 密钥存储（与 .NET 版同文件：`dpapi.QTools.AI.default.bin`）；AES-GCM 文件兜底 | ✅ Windows |
| 单 exe 与 NSIS 安装包 | ✅ 本地 `mygo build` 产出 |

---

## 2. 与 .NET 版的关键差异

- **桌面壳**：.NET 版是 PhotinoX；本版是 mygo（Windows 使用 WebView2）。
- **桥接传输**：注入 `go-shim.js`，在 `bridge.js` 之前提供兼容的
  `window.external.sendMessage / receiveMessage`；底层调用 mygo 的绑定服务
  `mygo.BindAs("Bridge", ...)`。前端 `bridge.js`、`window.host` 与宿主页面**完全复用**。
- **流式输出**：AI 生成 / 更新与 Git 操作通过 `*mygo.Channel[string]` 回传增量，
  再包装成与 .NET 版相同的 `{"id":...,"stream":{"delta":...,"reasoning":...}}` 信封。
- **本地 HTTP**：仍保留回环 HTTP 服务（Go 标准库 `net/http`，只监听 `127.0.0.1`），
  负责提供宿主页面、工具静态文件、CSP 与 HTML 注入；桥接能力不在 HTTP 面上。
- **系统托盘**：使用 mygo 原生托盘与原生菜单（列表前 N 项 + 打开 QTools / 设置 / 退出），
  **没有实现** .NET 版与 Rust 版的 HTML 圆角弹层。
- **系统通知**：使用 `mygo.NewNotification`，Windows 后端为托盘气泡；不依赖 Tauri 通知插件。
- **窗口几何**：按 mygo 的逻辑坐标（DIP）保存，而 .NET 版 Windows 保存物理像素；
  共用 `%APPDATA%\QTools\data\config.json` 时，非 100% 缩放下位置可能不同。
- **安装包**：`mygo build` 会同时产出单 exe 与 NSIS 安装包；CI 尚未接入 Go 构建。

---

## 3. 三种实现的体积对比

| 实现 | 桌面壳 | win-x64 单文件 | 说明 |
|---|---|---|---|
| `main`（.NET） | .NET 10 Native AOT + PhotinoX | **8.89 MB**（8,888,832 字节） | 基准实现 |
| `rust-tauri-port` | Rust + Tauri 2 | **7.04 MB**（7,039,488 字节） | 当前最小 |
| `go-mygo-port`（本分支） | Go + mygo | **15.5 MB**（15,475,200 字节 / 14.8 MiB） | 纯 Go 运行时 + mygo + 拼音数据，体积最大 |

本版体积更大主要来自 Go 运行时、mygo 的 WebView2 绑定与 `go-pinyin` 全量表；
换取的是纯 Go、无 cgo、无需 MSVC 的构建链路。

---

## 4. mygo 的使用方式

- 应用入口、窗口、托盘、通知、全局热键、原生对话框都走 mygo API。
- `mygo.json` 提供应用名、图标、版本与输出目录；`mygo build` 会生成
  Windows 图标、清单（Per-Monitor DPI、Common Controls 6）与版本资源。
- 宿主页面与示例工具通过 `//go:embed all:assets` 编进二进制。
- 桥接服务注册：

```go
mygo.BindAs("Bridge", &BridgeService{state: state})
```

页面侧由 `go-shim.js` 转换为：

```js
window.mygo.call("Bridge.Call", rawMessage, channel)
```

内部窗口身份以宿主侧「窗口 → 角色 / toolId」绑定为权威；在线工具窗口与未知窗口
一律拒绝宿主桥接；iframe 内嵌凭证只在主窗口下解析。

---

## 5. 数据目录与共存

默认目录与 .NET / Rust 版相同：

- Windows：`%APPDATA%\QTools`
- macOS：`~/Library/Application Support/QTools`
- Linux：`${XDG_DATA_HOME:-~/.local/share}/QTools`

工具、启动项、在线工具、存储、配置、密钥、备份可直接共用。
**不要同时运行多个版本**：单实例保护各自独立，同时运行会争用数据目录与本地端口。

可用 `--root <目录>` 指定独立数据目录（测试 / 便携场景）。

---

## 6. 构建与运行（Windows）

工具链：Go **1.27.4**、WebView2 Runtime。

```bash
cd go-mygo

# 依赖（首次）
go mod download

# 直接构建（无图标 / 版本资源，适合开发）
go build -trimpath -ldflags="-H windowsgui -s -w" -o qtools-mygo.exe .

# 推荐：mygo 构建（生成图标、清单、版本资源、单 exe 与 NSIS 安装包）
go run github.com/egoist/mygo/cmd/mygo@v0.10.1-0.20261120015749-71f6c08b24a3 build
```

`mygo build` 产物：

```text
dist/windows-amd64/
├── QTools.exe                     约 15.5 MB
└── qtools-setup-1.3.0-amd64.exe   约 4.7 MB（NSIS 安装包）
```

运行：

```bash
# 开发运行（--dev 输出调试日志）
./qtools-mygo.exe --dev

# 固定本地端口（便于 CDP 调试）
WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9225 --remote-debugging-address=127.0.0.1" \
  ./qtools-mygo.exe --port 53177
```

---

## 7. 验证

```bash
# 静态检查与单元测试
gofmt -l .
go vet ./...
go test ./...

# 无 GUI 自检：起本地服务 → 26 项 HTTP 断言
./dist/windows-amd64/QTools.exe --root C:/Work/qtools-selftest --self-test
```

当前基线（Windows 本机，2026-11-20）：

- `go vet ./...`：无输出
- `go test ./...`：**9 个测试全部通过**（AI 输出解析、拼音检索键、清单 CSP 注入、安全审查、快捷方式命名、.lnk 解析、索引 id 稳定性、Anthropic 端点识别）
- `--self-test`：**26 PASS / 0 FAIL**（数量随已装工具数浮动，每个有效工具贡献 3 项）
- 真实 GUI 已验证：主窗口 / 工具窗口 / 设置 / 内容窗口、主题实时切换、存储、
  置顶 / 钉住、iframe 内嵌凭证、在线工具隔离、关闭到托盘、`Alt+Space`、
  单实例转发、窗口几何恢复、备份往返、Git 推送 / 恢复、通知、桌面快捷方式、
  AI 对话、流式生成与取消。

---

## 8. 已知限制

- **仅 Windows 真机验证**。macOS / Linux 未构建、未验证；托盘、通知、快捷方式、
  DPAPI、Win32 位置锁定都是 Windows 能力，非 Windows 返回不可用。
- 托盘使用 mygo 原生菜单，没有 HTML 弹层的外观与「就绪后显示」逻辑。
- 窗口几何按 DIP 保存；与 .NET 版共用配置时，非 100% DPI 下位置可能不一致。
- 系统通知是否出现取决于 Windows 通知设置 / 勿扰模式；API 调用与错误链路已验证。
- 没有内置自动更新器；升级仍需替换可执行文件。
- 尚未接入 GitHub Actions，没有公开的 Go Release 资产；上表体积为本机 `mygo build` 实测。

---

## 9. 安全边界

与 .NET 版保持一致，没有放宽：

- 桥接方法白名单 + manifest 权限（`storage` / `ai` / `files` / `media`）+ 1 MB 消息上限。
- `hostOnly` 方法只允许主窗口 / 内容窗口调用；在线工具窗口全部拒绝。
- `toolId` 以宿主侧窗口绑定为权威，客户端自述值不可信。
- 工具页面 CSP 默认禁止 eval；`allowEval` 必须由用户显式确认。
- API Key 只进 DPAPI / AES-GCM 保护存储，不写日志、不返回页面。
- 静态文件服务保留路径穿越、符号链接、点号目录（`.versions`）防护。

---

## 10. 目录结构

```text
go-mygo/
├── main.go                 入口：CLI、单实例、服务启动、主窗口 / 托盘 / 热键
├── state.go                全局状态：端口、origin、窗口角色、取消、内嵌凭证
├── server.go               回环 HTTP、CSP、HTML 注入、go-shim.js
├── bridge.go               桥接分发器、方法表、权限与业务处理
├── shell.go                窗口生命周期、托盘、通知、几何、置顶 / 钉住
├── ai.go                   OpenAI / Anthropic 兼容与 SSE 流式
├── creator.go              AI 生成 / 更新 / 导入 / 安全审查
├── tools.go                manifest、版本、示例、拼音检索键
├── launcher.go             启动项、在线工具、使用频次、列表排序
├── program_index.go        开始菜单 / 桌面索引与 .lnk 解析
├── backup.go / git_backup.go / item_package.go
├── secret_windows.go       DPAPI（与 .NET 版同文件）
├── assets/                 宿主页面、品牌图标、示例工具（go:embed）
└── mygo.json               mygo 构建配置（图标 / 版本 / 输出）
```
