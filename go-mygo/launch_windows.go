//go:build windows

package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"
)

var (
	shell32Launch = syscall.NewLazyDLL("shell32.dll")
	procShellExec = shell32Launch.NewProc("ShellExecuteW")
)

func launchAppEntry(app *LoadedApp) (uint32, error) {
	if !app.IsValid {
		return 0, bridgeErr("APP_INVALID", "启动项配置无效")
	}
	if !app.TargetExists() {
		return 0, bridgeErr("APP_TARGET_NOT_FOUND", fmt.Sprintf("找不到目标程序：%s", app.Entry.Target))
	}
	arguments := ""
	if app.Entry.Arguments != nil {
		arguments = strings.TrimSpace(*app.Entry.Arguments)
	}
	workingDir := ""
	if app.Entry.WorkingDirectory != nil {
		workingDir = strings.TrimSpace(*app.Entry.WorkingDirectory)
	}
	if arguments != "" {
		command := exec.Command(app.Entry.Target, splitCommandLine(arguments)...)
		if workingDir != "" && fileExists(workingDir) {
			command.Dir = workingDir
		}
		if err := command.Start(); err != nil {
			return 0, bridgeErr("APP_LAUNCH_FAILED", fmt.Sprintf("启动失败：%v", err))
		}
		return uint32(command.Process.Pid), nil
	}
	target, _ := syscall.UTF16PtrFromString(app.Entry.Target)
	verb, _ := syscall.UTF16PtrFromString("open")
	var cwd *uint16
	if workingDir != "" && fileExists(workingDir) {
		cwd, _ = syscall.UTF16PtrFromString(workingDir)
	}
	result, _, _ := procShellExec.Call(
		0, uintptr(unsafe.Pointer(verb)), uintptr(unsafe.Pointer(target)),
		0, uintptr(unsafe.Pointer(cwd)), 1,
	)
	if result <= 32 {
		return 0, bridgeErr("APP_LAUNCH_FAILED", fmt.Sprintf("启动失败（ShellExecute 代码 %d）", result))
	}
	return 0, nil
}

func revealTarget(target string) error {
	info, err := os.Stat(target)
	command := exec.Command("explorer.exe", "/select,"+target)
	if err == nil && info.IsDir() {
		command = exec.Command("explorer.exe", target)
	}
	if err := command.Start(); err != nil {
		return bridgeErr("REVEAL_FAILED", fmt.Sprintf("打开所在位置失败：%v", err))
	}
	return nil
}

func splitCommandLine(value string) []string {
	args := []string{}
	var current strings.Builder
	quoted := false
	for _, ch := range value {
		switch {
		case ch == '"':
			quoted = !quoted
		case (ch == ' ' || ch == '\t') && !quoted:
			if current.Len() > 0 {
				args = append(args, current.String())
				current.Reset()
			}
		default:
			current.WriteRune(ch)
		}
	}
	if current.Len() > 0 {
		args = append(args, current.String())
	}
	return args
}

func openPath(path string) error {
	verb, _ := syscall.UTF16PtrFromString("open")
	target, _ := syscall.UTF16PtrFromString(path)
	result, _, _ := procShellExec.Call(0, uintptr(unsafe.Pointer(verb)), uintptr(unsafe.Pointer(target)), 0, 0, 1)
	if result <= 32 {
		return fmt.Errorf("ShellExecute 代码 %d", result)
	}
	return nil
}

func absoluteTarget(target string) string {
	if filepath.IsAbs(target) {
		return target
	}
	if abs, err := filepath.Abs(target); err == nil {
		return abs
	}
	return target
}
