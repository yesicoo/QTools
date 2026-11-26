# QTools 工具源服务

独立的小工具 / 在线工具目录、提交审核与沙箱预览服务。使用 **.NET 10** 重写，采用自研 TCP HTTP/1.1 服务，不依赖 ASP.NET Core/Kestrel，也没有第三方运行库；静态页面内嵌在单文件中，数据持久化为 JSON 文件。

## 功能

- 公开目录：搜索、分类、分页、详情、历史版本、SHA256 校验和 `.qtools` 下载；小工具与在线工具统一展示（在线工具带「打开网址」入口，不提供沙箱预览）。首页与工具详情会显示当前部署的源网址（按访问域名动态生成）和「配置到 QTools」步骤，可一键复制。
- 提交审核：接收 QTools 导出的当前运行版本（小工具或在线工具），检查包结构、路径、manifest / web.json、实际解压长度与风险。
- 管理后台：管理员登录、审核通过/拒绝、下架/重新发布、审核前隔离预览；可编辑工具描述与分类、目录置顶，并查看下载次数与排行。
- 在线预览：`iframe sandbox="allow-scripts"` 加响应头沙箱，不开放同源权限；不提供 AI、文件、硬件或窗口控制能力。
- 版本规则：小工具同版本不可重复发布、新版本必须更高；在线工具允许同版本替换网址；审核通过后自动公开。
- 桌面同步：QTools 设置页填写源地址后，可浏览、预览、直接安装/更新、下载 `.qtools` 安装包和直接提交本地小工具/在线工具（也可在列表右键「上传到工具源」），无需经过浏览器下载再导入。

## 技术形态

- **Native AOT + Trimmed + SelfContained + SingleFile**，发布后不需要安装 .NET 运行时。
- Release 使用体积优先优化、相同方法体折叠，并关闭 Metrics、EventSource、调试器与栈追踪等非必要组件。
- System.Text.Json 全部走源生成，禁用反射序列化。
- 单个自包含可执行文件，页面资源从程序集内嵌读取；运行期只需一个可写数据目录。Windows x64 实测产物为 `3,671,040` 字节（约 `3.50 MiB`，v1.2.0 含在线工具支持）。
- Windows / Linux / macOS 使用相同代码与发布参数；目前仅 Windows 完成真实验收，Linux / macOS 已提供发布配置但**未真机验证**。

## 快速部署

### 单文件发布

在仓库根目录执行，按目标平台选择 RID：

```bash
# Windows x64
dotnet publish source-service/QTools.Source.csproj -c Release -r win-x64 -o publish/source/win-x64

# Linux x64
dotnet publish source-service/QTools.Source.csproj -c Release -r linux-x64 -o publish/source/linux-x64

# macOS x64
dotnet publish source-service/QTools.Source.csproj -c Release -r osx-x64 -o publish/source/osx-x64
```

Apple Silicon 使用 `osx-arm64`，Linux ARM64 使用 `linux-arm64`。Native AOT 需要目标平台的本机编译工具：Windows 安装 MSVC C++ Build Tools，Linux 安装 `clang` 与 `zlib1g-dev`，macOS 安装 Xcode Command Line Tools。`global.json` 锁定 .NET SDK 10.0.400 或更高的 10.0 feature band。

框架依赖发布（目标机器需安装 .NET 10 运行时）：

```bash
dotnet publish source-service/QTools.Source.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o publish/source/framework-dependent
```

Windows PowerShell 启动（默认读取当前目录的 `source-config.json`）：

```powershell
Copy-Item .\source-config.example.json .\source-config.json
# source-config.json 默认密码为 234567；对外部署请修改
.\QTools.Source.exe
```

Linux / macOS 启动：

```bash
cp source-config.example.json source-config.json
# source-config.json 默认密码为 234567；对外部署请修改
./QTools.Source
```

环境变量方式：

```powershell
$env:QTOOLS_SOURCE_ADMIN_PASSWORD = "请替换为至少6位的强密码"
.\QTools.Source.exe -addr 0.0.0.0:58088 -data .\data
```

打开 `http://127.0.0.1:58088`，管理后台位于 `/admin`。默认监听所有网卡的 `58088` 端口，请确认防火墙范围。管理员密码只在第一次启动、`admin.json` 尚不存在时需要；初始化后重启不再要求密码。首次密码可来自 `source-config.json` 的 `adminPassword` 或环境变量 `QTOOLS_SOURCE_ADMIN_PASSWORD`，优先级为环境变量 > 配置文件 > 默认密码 `234567`。默认密码仅适合本机试用，对外提供服务前必须修改。密码只以 PBKDF2-SHA256（120000 轮、随机盐）保存到 `admin.json`。

### Docker Compose

```bash
cd source-service
cp .env.example .env
# 编辑 .env，填写 QTOOLS_SOURCE_ADMIN_PASSWORD
docker compose up -d --build
```

Dockerfile 在 .NET 10 SDK 镜像内完成 Linux x64 Native AOT 发布，运行镜像只包含 AOT 单文件与原生依赖。默认只把端口发布到宿主回环地址 `127.0.0.1:58088`，数据保存在命名卷 `source-data`；容器以非 root 用户、只读根文件系统运行，并移除全部 Linux capabilities。

需要局域网访问时，把 `docker-compose.yml` 的端口绑定改为 `"58088:58088"`。**公网部署必须在反向代理上启用 HTTPS**；代理应将站点部署在域名根路径，并把上游指向 `127.0.0.1:58088`，同时在 `.env` 设置 `QTOOLS_SOURCE_SECURE_COOKIE=true`。服务只支持根路径部署，不解析 `X-Forwarded-*`；反代后的客户端全部按代理连接 IP 计入限流。

## QTools 接入

1. 启动源站并部署至少一个审核通过的工具。
2. 打开 QTools → 设置 → 工具源，填写例如 `http://127.0.0.1:58088`，保存并测试连接。
3. 返回主窗口 → 工具源，浏览、沙箱预览、直接安装/更新；也可“下载 .qtools”，通过原生保存对话框离线保存或分享。
4. 在“提交本地工具或新版本”中选择小工具或在线工具（或在列表右键「上传到工具源」），QTools 直接导出当前版本并上传；源站管理员审核后发布。

安装与下载时桌面端都会再次校验 SHA256、大小、manifest / web.json、工具身份、ZIP 结构和实际解压长度；安装还会校验本地安全风险。在线工具安装只写入网址条目，不落任何网页代码。高危项需要显式确认；同版本或本地更高版本不会覆盖。更新保留 `files/` 用户数据和 `.versions/` 历史，并建立回滚副本。

## 配置

| 配置文件 / 环境变量 / 参数 | 默认值 | 说明 |
|---|---|---|
| `source-config.json` / `-config` / `QTOOLS_SOURCE_CONFIG` | `./source-config.json` | 配置文件；不填参数时自动读取当前目录 |
| `adminPassword` / `QTOOLS_SOURCE_ADMIN_PASSWORD` | `234567` | 仅首次初始化需要，6~256 字符；对外部署必须修改 |
| `addr` / `QTOOLS_SOURCE_ADDR` / `-addr` / `--addr` | `0.0.0.0:58088` | 监听地址；容器默认相同 |
| `data` / `QTOOLS_SOURCE_DATA` / `-data` / `--data` | `./data` | 数据目录；容器默认 `/data` |
| `secureCookie` / `QTOOLS_SOURCE_SECURE_COOKIE` | `false` | `true` / `1` 时为会话 Cookie 添加 `Secure` |

优先级为命令行 > 环境变量 > 配置文件 > 默认值；首次密码的环境变量优先于配置文件。`adminPassword` 会以明文保存在配置文件中，建议初始化后清空或改用环境变量；初始化完成后服务只读取 `admin.json` 中的 PBKDF2 哈希。配置文件中的相对 `data` 路径按配置文件所在目录解析。

数据目录包含 `state.json`、`admin.json`、`packages/` 和 `extracted/`。停止服务后备份整个目录即可迁移；包路径按提交编号重新解析，不依赖旧机器的绝对路径。`admin.json` 丢失后会重新要求首次密码，因此必须妥善备份。管理会话保存在内存中，重启后需要重新登录。

## 安全边界

- 管理会话使用随机会话令牌、`HttpOnly`、`SameSite=Strict`；HTTPS 反代模式可添加 `Secure`。写操作同时校验 CSRF Token 与同 Host Origin。
- 登录限流为每 IP 5 次/分钟，提交限流为每 IP 10 次/小时。
- 上传包上限 8 MiB，实际解压总量上限 24 MiB，最多 500 个 ZIP 条目；拒绝 ZIP64、加密条目、符号链接、重复条目、穿越、绝对路径、盘符、反斜杠、空段和点号前缀目录。
- ZIP 按本地文件头直接读取 deflate/stored 压缩流，累计真实输出，不信任中央目录声明的解压长度；校验失败不替换既有工具目录。
- 公开 API 只读，所有写操作来自提交上传或已认证管理接口；没有宿主桥接 HTTP 端点。
- 在线预览使用服务端强制 CSP，忽略提交者自定义 CSP；`allowEval` 也只影响沙箱预览中的 eval 权限。
- 预览存储按提交编号隔离在浏览器 `localStorage`，不会写入 QTools 本地工具存储。
- 服务端风险扫描用于审核提示，不替代桌面端安装时的安全审查。

## API v1

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| GET | `/api/v1/meta` | 服务与包格式信息 |
| GET | `/api/v1/tools?q=&category=&sort=&page=&pageSize=` | 公开目录（sort = recommended / downloads / updated / name） |
| GET | `/api/v1/tools/{id}?version=` | 详情与完整版本列表 |
| GET | `/api/v1/tools/{id}/package?version=` | 下载指定版本 |
| POST | `/api/v1/submissions` | multipart 上传，字段名 `package` |
| GET | `/api/v1/submissions/{id}` | 公开审核状态 |
| GET | `/admin/api/session` | 当前会话与 CSRF Token |
| POST | `/admin/api/login` | 管理员登录 |
| POST | `/admin/api/logout` | 退出登录 |
| GET | `/admin/api/stats` | 审核统计 |
| GET | `/admin/api/submissions?status=` | 提交列表 |
| GET | `/admin/api/submissions/{id}` | 提交详情 |
| POST | `/admin/api/submissions/{id}/review` | 审核通过/拒绝 |
| GET | `/admin/api/tools` | 全部工具，含未公开 |
| POST | `/admin/api/tools/{id}/publish` | 重新发布 |
| POST | `/admin/api/tools/{id}/unpublish` | 下架 |
| POST | `/admin/api/tools/{id}/metadata` | 编辑描述/分类/置顶 |

## 测试与验收

在仓库根目录执行：

```bash
dotnet build
dotnet test tests/QTools.Source.Tests/QTools.Source.Tests.csproj
dotnet publish source-service/QTools.Source.csproj -c Release -r win-x64 -o publish/source/win-x64
```

36 项源站测试覆盖提交审核、历史版本、下载哈希、公开/管理预览、下架失效、恶意路径、重复条目、符号链接、伪造 ZIP 长度、null 权限、审核持久化失败、限流表上限、HTTPS Cookie、数据目录迁移、SemVer、启动配置优先级、默认密码与免密重启、描述分类编辑与置顶、下载计数与排行、在线工具提交审核与同版本替换、首页入口与下载链接、真实 HTTP 解析。Windows AOT 单文件与框架依赖发布均已进行实际启动验收；Docker 与 Linux / macOS 运行态需在对应环境验证。
