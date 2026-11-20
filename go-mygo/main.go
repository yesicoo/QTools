package main

import (
	"context"
	"fmt"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/egoist/mygo"
)

const appVersion = "1.3.0"

type cliOptions struct {
	port           int
	hasPort        bool
	root           string
	openTool       string
	openSettings   bool
	selfTest       bool
	dev            bool
	notifyTitle    string
	notifyBody     string
	hasNotify      bool
	generatePrompt string
	hasGenerate    bool
	deleteTool     string
	deleteStorage  bool
	yes            bool
}

func main() {
	options, err := parseCLI(os.Args[1:])
	if err != nil {
		fmt.Fprintf(os.Stderr, "QTools: %v\n\n%s", err, usageText())
		os.Exit(2)
	}
	if options == nil {
		return
	}
	paths, err := NewAppPaths(options.root)
	if err != nil {
		fmt.Fprintf(os.Stderr, "QTools: %v\n", err)
		os.Exit(1)
	}
	log.Initialize(paths.LogFile(), options.dev)
	logInfo("QTools Go/mygo 启动 | 数据目录=%s | PID=%d", paths.Root, os.Getpid())
	seedSampleTools(paths)
	state := NewAppState(paths)
	globalState = state
	if key := strings.TrimSpace(os.Getenv("QTOOLS_AI_KEY")); key != "" {
		if err := state.Secrets.Set(key); err == nil {
			logInfo("已从环境变量注入 AI Key 到系统保护存储")
		}
	}

	if options.selfTest {
		port := 0
		if options.hasPort {
			port = options.port
		}
		os.Exit(runSelfTest(state, port))
	}
	if options.deleteTool != "" {
		os.Exit(runHeadlessDelete(state, options.deleteTool, options.deleteStorage))
	}
	if options.hasGenerate {
		toolID, code, done := runHeadlessGenerate(state, options.generatePrompt, options.yes)
		if done {
			os.Exit(code)
		}
		options.openTool = toolID
	}
	if options.openTool != "" {
		if tool := state.Tools.Find(options.openTool); tool == nil || !tool.IsValid {
			fmt.Fprintf(os.Stderr, "QTools: 工具不存在或 manifest 无效：%s\n", options.openTool)
			os.Exit(3)
		}
	}

	if !mygo.App.RequestSingleInstanceLock() {
		return
	}
	mygo.App.SetName("QTools")
	mygo.App.SetVersion(appVersion)
	mygo.App.OnSecondInstance(func(args []string, workingDirectory string) {
		mygo.App.WhenReady(func() { handleSecondInstance(args) })
	})
	mygo.BindAs("Bridge", &BridgeService{state: state})
	applyTheme(state, state.Config.Snapshot().Shell.Theme)

	if options.hasNotify {
		runNotifyOnly(options.notifyTitle, options.notifyBody)
		return
	}

	server, err := startLocalServer(state, options.port)
	if err != nil {
		logError("本地服务启动失败：%v", err)
		fmt.Fprintf(os.Stderr, "QTools 启动失败：%v\n", err)
		os.Exit(1)
	}
	defer server.server.Close()
	logInfo("本地服务端口 %d", state.Port())

	mygo.App.WhenReady(func() {
		globalState = state
		if err := initTray(state); err != nil {
			logWarn("初始化托盘失败：%v", err)
		}
		if state.Config.Snapshot().Shell.HotkeyEnabled && runtime.GOOS == "windows" {
			setHotkeyEnabled(true)
		}
		createMainWindow(state, options.dev)
		if options.openSettings {
			if err := openSettingsWindow(state); err != nil {
				logError("打开设置窗口失败：%v", err)
			}
		}
		if options.openTool != "" {
			if tool := state.Tools.Find(options.openTool); tool != nil && tool.IsValid {
				if err := openToolWindow(state, tool); err == nil {
					state.Usage.Record(tool.Manifest.ID)
				}
			}
		}
		go state.refreshPrograms()
	})
	if err := mygo.App.Run(); err != nil {
		logError("应用运行失败：%v", err)
		fmt.Fprintf(os.Stderr, "QTools 启动失败：%v\n", err)
		os.Exit(1)
	}
}

func runNotifyOnly(title, body string) {
	mygo.App.WhenReady(func() {
		if err := notifyUser(title, body); err != nil {
			fmt.Fprintf(os.Stderr, "QTools: %v\n", err)
			mygo.App.Exit(8)
			return
		}
		go func() {
			time.Sleep(6 * time.Second)
			mygo.App.Quit()
		}()
	})
	if err := mygo.App.Run(); err != nil {
		os.Exit(8)
	}
}

func handleSecondInstance(args []string) {
	state := globalState
	if state == nil {
		return
	}
	if title, body, ok := notifyArgs(args); ok {
		if err := notifyUser(title, body); err != nil {
			logError("单实例通知转发失败：%v", err)
		}
		return
	}
	if containsArg(args, "--open-settings") {
		if err := openSettingsWindow(state); err != nil {
			logError("打开设置失败：%v", err)
		}
		return
	}
	if id := valueOf(args, "--open-tool"); id != "" {
		if tool := state.Tools.Find(id); tool != nil && tool.IsValid {
			if err := openToolWindow(state, tool); err == nil {
				state.Usage.Record(id)
			}
		}
		return
	}
	showMainWindow(state)
}

func parseCLI(args []string) (*cliOptions, error) {
	options := &cliOptions{}
	for index := 0; index < len(args); index++ {
		arg := args[index]
		switch arg {
		case "--help", "-h", "/?":
			fmt.Print(usageText())
			return nil, nil
		case "--version":
			fmt.Printf("QTools (Go + mygo) %s\n", appVersion)
			return nil, nil
		case "--self-test":
			options.selfTest = true
		case "--dev":
			options.dev = true
		case "--yes":
			options.yes = true
		case "--delete-storage":
			options.deleteStorage = true
		case "--open-settings":
			options.openSettings = true
		case "--port":
			value, next, err := nextValue(args, index, "--port")
			if err != nil {
				return nil, err
			}
			port, err := strconv.Atoi(value)
			if err != nil || port < 1 || port > 65535 {
				return nil, fmt.Errorf("--port 需要 1-65535 之间的整数")
			}
			options.port, options.hasPort, index = port, true, next
		case "--root":
			value, next, err := nextValue(args, index, "--root")
			if err != nil {
				return nil, err
			}
			options.root, index = value, next
		case "--open-tool":
			value, next, err := nextValue(args, index, "--open-tool")
			if err != nil {
				return nil, err
			}
			options.openTool, index = value, next
		case "--generate-tool":
			value, next, err := nextValue(args, index, "--generate-tool")
			if err != nil {
				return nil, err
			}
			options.generatePrompt, options.hasGenerate, index = value, true, next
		case "--delete-tool":
			value, next, err := nextValue(args, index, "--delete-tool")
			if err != nil {
				return nil, err
			}
			options.deleteTool, index = value, next
		case "--notify":
			if index+1 >= len(args) || strings.HasPrefix(args[index+1], "--") {
				return nil, fmt.Errorf("--notify 需要非空通知标题")
			}
			options.notifyTitle = args[index+1]
			options.hasNotify = true
			index++
			if index+1 < len(args) && !strings.HasPrefix(args[index+1], "--") {
				options.notifyBody = args[index+1]
				index++
			}
			if len([]rune(options.notifyTitle)) > 64 {
				return nil, fmt.Errorf("--notify 标题不超过 64 字符")
			}
			if len([]rune(options.notifyBody)) > 256 {
				return nil, fmt.Errorf("--notify 正文不超过 256 字符")
			}
		default:
			return nil, fmt.Errorf("未知参数：%s", arg)
		}
	}
	return options, nil
}

func nextValue(args []string, index int, name string) (string, int, error) {
	if index+1 >= len(args) || strings.HasPrefix(args[index+1], "--") {
		return "", index, fmt.Errorf("%s 需要一个值", name)
	}
	return args[index+1], index + 1, nil
}

func containsArg(args []string, target string) bool {
	for _, arg := range args {
		if arg == target {
			return true
		}
	}
	return false
}

func valueOf(args []string, name string) string {
	for index, arg := range args {
		if arg == name && index+1 < len(args) && !strings.HasPrefix(args[index+1], "--") {
			return args[index+1]
		}
	}
	return ""
}

func notifyArgs(args []string) (string, string, bool) {
	for index, arg := range args {
		if arg != "--notify" {
			continue
		}
		if index+1 >= len(args) || strings.HasPrefix(args[index+1], "--") {
			return "", "", false
		}
		title := args[index+1]
		body := ""
		if index+2 < len(args) && !strings.HasPrefix(args[index+2], "--") {
			body = args[index+2]
		}
		return title, body, true
	}
	return "", "", false
}

func runHeadlessDelete(state *AppState, toolID string, deleteStorage bool) int {
	tool := state.Tools.Find(toolID)
	if tool == nil {
		fmt.Fprintf(os.Stderr, "工具不存在：%s\n", toolID)
		return 7
	}
	storagePath := state.Paths.StorageDir() + string(os.PathSeparator) + toolID + ".json"
	fmt.Printf("删除工具 %s（目录 %s）\n", toolID, tool.Directory)
	if err := os.RemoveAll(tool.Directory); err != nil {
		fmt.Fprintf(os.Stderr, "删除失败：%v\n", err)
		return 6
	}
	if deleteStorage {
		state.Storage.Delete(toolID)
	}
	state.Tools.Remove(toolID)
	state.Pinned.Forget("tool", toolID)
	state.RevokeEmbedTokens(toolID)
	fmt.Printf("  工具目录已删除：%v\n", !fileExists(tool.Directory))
	if deleteStorage {
		fmt.Printf("  存储文件已删除：%v\n", !fileExists(storagePath))
	}
	remaining := []string{}
	for _, item := range state.Tools.All() {
		remaining = append(remaining, item.Manifest.ID)
	}
	fmt.Printf("  剩余工具：%s\n", strings.Join(remaining, ", "))
	if !fileExists(tool.Directory) && state.Tools.Find(toolID) == nil {
		fmt.Println("删除完成")
		return 0
	}
	return 6
}

func runHeadlessGenerate(state *AppState, prompt string, accept bool) (string, int, bool) {
	key, cfg, err := requireAIConfig(state)
	if err != nil {
		fmt.Fprintf(os.Stderr, "生成失败：%s %s\n", errorCode(err), errorMessage(err))
		return "", 4, true
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	raw, err := chatOnce(ctx, cfg, key, []ChatMessage{
		{Role: "system", Content: systemPrompt},
		{Role: "user", Content: generateRequirements + strings.TrimSpace(prompt)},
	}, nil)
	if err != nil {
		fmt.Fprintf(os.Stderr, "生成失败：%s %s\n", errorCode(err), errorMessage(err))
		return "", 4, true
	}
	result, err := buildGenerated(raw, nil, "")
	if err != nil {
		fmt.Fprintf(os.Stderr, "生成失败：%s %s\n", errorCode(err), errorMessage(err))
		return "", 4, true
	}
	fmt.Println("=== 安全审查结果 ===")
	if len(result.Findings) == 0 {
		fmt.Println("  （未发现安全问题）")
	}
	for _, finding := range result.Findings {
		level := "提示"
		switch finding.Severity {
		case 0:
			level = "致命"
		case 1:
			level = "高危"
		}
		fmt.Printf("  [%s] %s: %s\n", level, finding.Code, finding.Message)
	}
	fmt.Println("=== manifest.json ===")
	fmt.Printf("%s / %s v%s\n", result.Manifest.ID, result.Manifest.Name, result.Manifest.Version)
	fmt.Printf("  permissions: %s\n", strings.Join(result.Manifest.Permissions, ","))
	fmt.Printf("  indexHtml: %d 字节\n", len(result.IndexHTML))
	if result.HasFatal {
		fmt.Fprintln(os.Stderr, "存在致命问题，拒绝保存")
		return "", 5, true
	}
	if !accept {
		fmt.Println("未指定 --yes，仅打印审查报告，不保存")
		return "", 0, true
	}
	toolID, err := saveGeneratedTool(state, result, true)
	if err != nil {
		fmt.Fprintf(os.Stderr, "保存失败：%s %s\n", errorCode(err), errorMessage(err))
		return "", 6, true
	}
	fmt.Printf("已保存工具 %s\n", toolID)
	return toolID, 0, false
}

func usageText() string {
	return `QTools（Go + mygo 移植版）

用法: QTools.exe [选项]

选项:
  --root <目录>          指定数据根目录
  --port <端口>          固定本地 HTTP 服务端口
  --open-tool <id>       启动后打开指定小工具
  --open-settings        启动后打开设置窗口
  --generate-tool <需求>  调用 AI 生成工具；配合 --yes 直接保存
  --delete-tool <id>     无头删除小工具（可加 --delete-storage）
  --notify <标题> [正文]  发送系统通知（已运行实例会转发）
  --self-test            无 GUI 启动本地服务并执行 HTTP 自检
  --dev                  开发模式（输出调试日志）
  --version              显示版本
  --help                 显示本帮助
`
}
