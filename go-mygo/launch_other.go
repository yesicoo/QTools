//go:build !windows

package main

import (
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
)

func launchAppEntry(app *LoadedApp) (uint32, error) {
	if !app.IsValid {
		return 0, bridgeErr("APP_INVALID", "启动项配置无效")
	}
	if !app.TargetExists() {
		return 0, bridgeErr("APP_TARGET_NOT_FOUND", fmt.Sprintf("找不到目标程序：%s", app.Entry.Target))
	}
	command := exec.Command(app.Entry.Target, splitCommandLine(deref(app.Entry.Arguments))...)
	if app.Entry.WorkingDirectory != nil {
		command.Dir = *app.Entry.WorkingDirectory
	}
	if err := command.Start(); err != nil {
		return 0, bridgeErr("APP_LAUNCH_FAILED", fmt.Sprintf("启动失败：%v", err))
	}
	return uint32(command.Process.Pid), nil
}

func revealTarget(target string) error {
	command := exec.Command("open", "-R", target)
	if err := command.Start(); err != nil {
		return bridgeErr("REVEAL_FAILED", fmt.Sprintf("打开所在位置失败：%v", err))
	}
	return nil
}

func splitCommandLine(value string) []string {
	return strings.Fields(value)
}

func openPath(path string) error {
	return exec.Command("open", path).Start()
}

func absoluteTarget(target string) string {
	if abs, err := filepath.Abs(target); err == nil {
		return abs
	}
	return target
}
