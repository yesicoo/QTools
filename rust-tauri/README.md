# QTools · Rust + Tauri 2 移植版

这是 QTools 的 Rust 重写版本：用 **Rust + Tauri 2** 重新实现 .NET 版宿主，
前端仍复用仓库里的 `src/QTools.Host/Assets`（`rust-embed` 编译期内嵌），
数据目录、桥接协议、权限模型与 .NET 版保持一致。

> 目标：验证「同样一套前端 + 同一份数据，换成 Rust/Tauri 能把二进制做小、启动更轻」。
> 目前以 **Windows** 为主要验证平台；macOS / Linux 代码路径存在但**未真机验证**。

---

## 1. 已完成的功能

| 模块 | 状态 |
|---|---|
| 主窗口统一列表（小工具 / 手动启动项 / 自动索引程序 / 在线工具） | ✅ Windows 验证 |
| 拼音首字母 + 全拼搜索、使用频次排序、首页只显示用过的条目 | ✅ |
| 小工具独立窗口、窗口置顶 / 钉住（Win32 子类化锁位置）、收缩菜单、几何记忆 | ✅ |
| 内容窗口（创建 / 导入 / 版本管理）、设置窗口、几何与置顶/钉住状态 | ✅ |
| 浅色 / 深色 / 跟随系统主题；原生标题栏 DWM 跟随；工具页实时换肤 | ✅ |
| AI 生成 / 优化更新 / 修复：OpenAI 与 Anthropic 协议、SSE 流式思考与正文分离、取消 | ✅ |
| 工具存储、二进制文件桥接、工具清单校验、脚本安全审查、版本快照与回滚 | ✅ |
| 导入 HTML、单条目 `.qtools` 导出/导入（tool / app / web） | ✅ |
| Windows 真实程序图标、开始菜单与桌面索引（.lnk 字节解析、真实目标去重、垃圾名过滤） | ✅ |
| 在线工具独立窗口，**不注入宿主桥接**；即使网址指向 127.0.0.1 也被分发器拒绝 | ✅ |
| 全局热键 `Alt+Space` 呼出 / 隐藏主窗口 | ✅ |
| 关闭主窗口收起到托盘；左键显示主窗口；右键 HTML 条目弹层（前 N 项 + 退出） | ✅ |
| Windows 系统通知；`--notify`；已运行实例经 Tauri 单实例转发 | ✅（通知视觉受系统通知设置影响） |
| 创建桌面快捷方式（目标 `QTools.exe --open-tool <id>`）；单实例转发打开 | ✅ |
| zip 备份 / 恢复 / 导入；Git 远程推送 / 恢复（先 ls-remote 再浅克隆） | ✅ 代码完成 |
| CLI：`--self-test` / `--port` / `--root` / `--open-tool` / `--open-settings` / `--generate-tool` / `--yes` / `--delete-tool` / `--delete-storage` / `--notify` | ✅ |
| Windows DPAPI 密钥存储（文件名与 .NET 版一致）；AES-GCM 文件兜底 | ✅ Windows |
| Release 单 exe | ✅ Windows x64 约 7.04 MB（.NET AOT 版 8.89 MB，小约 21%） |
| 安装包发布 | ⏳ 目前直接运行 `cargo build --release` 产物，未接入 CI 打包 |

---

## 2. 与 .NET 版的关键差异

- **本地 HTTP 服务**：.NET 版是 Kestrel；移植版是手写的回环 TCP HTTP/1.1 服务
  （只支持 GET/HEAD，Host 校验、防穿越、点号目录拒绝、CSP、HTML 注入与 .NET 版对齐）。
- **桥接传输**：Tauri 无法原样使用 PhotinoX 的 `window.external`，因此注入
  `tauri-shim.js`，在 `bridge.js` 之前提供兼容的 `window.external.sendMessage/receiveMessage`，
  页面代码与 .NET 版**完全共用**。Tauri 命令名是 `bridge_message`，由
  `capabilities/qtools-remote.json` 只对回环来源放行。
- **窗口身份**：以 Tauri 窗口 label 为准（`tool-{id}`、`main` / `content` / `settings` / `tray-menu`、
  `web-{id}`）。`web-*` 明确拒绝全部宿主桥接；未知窗口也拒绝。
- **托盘弹层**：Tauri 托盘事件 → 隐藏 WebView 窗口 → 页面经 `manager.trayMenuReady`
  回传后按光标/工作区定位显示；失焦 800ms 宽限后收起。
- **通知**：使用 `tauri-plugin-notification`（Windows 通知中心），不再手工 `Shell_NotifyIcon` 气泡。
- **桌面快捷方式**：在临时 PowerShell 进程里用 `WScript.Shell` 创建 `.lnk`，
  不改写同名他人快捷方式，冲突时顺延「名称 (2).lnk」。
- **PDF / 打印 / 更新器**：未实现（.NET 版也没有）。

---

## 3. 数据目录与共存

默认数据目录与 .NET 版相同：

- Windows：`%APPDATA%\QTools`
- macOS：`~/Library/Application Support/QTools`
- Linux：`${XDG_DATA_HOME:-~/.local/share}/QTools`

因此工具、启动项、在线工具、存储、配置、密钥、备份可以被两个版本直接共用。
**不要同时运行 .NET 版与 Rust 版**：单实例保护分别使用各自的机制，同时运行会争用
`tools/`、`data/config.json` 与本地端口。

可用 `--root <目录>` 指定独立数据目录（测试/便携场景）。

---

## 4. 构建与运行（Windows）

本机工具链：Rust 1.99.1 + MSVC（VS 2026 Build Tools），WebView2 Runtime。

```bash
cd rust-tauri

# 开发构建
cargo build

# 运行（随机回环端口；--dev 打开调试日志）
./target/debug/qtools-tauri.exe --dev

# 固定端口 + CDP 调试（WebView2）
WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9223 --remote-debugging-address=127.0.0.1" \
  ./target/debug/qtools-tauri.exe --port 53177

# 发布构建（opt-level=z + LTO + strip + panic=abort）
cargo build --release
```

发布产物：`target/release/qtools-tauri.exe`（当前 Windows x64 实测约 **7.04 MB**（8,888,832 → 7,039,488 字节，约小 21%），opt-level=z + LTO）。若需要 NSIS 安装包，可安装 Tauri CLI 后
`cargo tauri build`（`tauri.conf.json` 已配置 `nsis` 目标与共享品牌图标；本仓库 CI 尚未接入这一步）。

---

## 5. 验证命令

```bash
# 单元测试（纯函数：解析器、托盘定位、路径白名单、快捷方式命名、拼音检索、主题）
cargo test

# 无 GUI 自检：起本地服务 → HTTP 断言（Host 校验 / CSP / 资源 / 每工具入口 / 穿越 / 点号目录）
./target/debug/qtools-tauri.exe --self-test
```

当前基线（Windows 本机，2026-11-20）：

- `cargo test`：**11 passed**
- `--self-test`：**29 PASS / 0 FAIL**（随已装工具数量变化，每个有效工具贡献 3 项）
- 真实 GUI 已验证：主窗口 / 工具窗口 / 设置 / 内容窗口、主题、热键、托盘弹层、
  通知转发、桌面快捷方式、`.qtools` 导出/导入、AI 生成流式与取消、窗口钉住后无法拖动、
  在线工具窗口调用宿主桥接被拒绝。

---

## 6. 已知限制

- **仅 Windows 真机验证**。macOS / Linux 未构建、未验证；托盘、通知、快捷方式、
  DPAPI、Win32 位置锁定都明确是 Windows-only（非 Windows 上相关能力返回不可用）。
- 系统通知是否出现取决于 Windows 通知设置/勿扰模式；API 调用与兜底链路已通过日志确认。
- 托盘弹层的定位/失焦收起逻辑与 .NET 版一致，但没有实现 .NET 版的 `IsWindowVisible` 级自动化断言。
- 没有内置自动更新器；升级仍需替换可执行文件。
