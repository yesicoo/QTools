package main

import (
	"bytes"
	"encoding/base64"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"unicode/utf16"
)

func createToolShortcut(state *AppState, toolID string) (any, error) {
	if runtime.GOOS != "windows" {
		return nil, bridgeErr("PLATFORM_NOT_SUPPORTED", "桌面快捷方式当前仅在 Windows 可用")
	}
	tool := state.Tools.Find(toolID)
	if tool == nil || !tool.IsValid {
		return nil, bridgeErr("TOOL_NOT_FOUND", "工具不存在或 manifest 无效")
	}
	executable, err := os.Executable()
	if err != nil {
		return nil, bridgeErr("SHORTCUT_FAILED", err.Error())
	}
	executable, _ = filepath.Abs(executable)
	name := sanitizeShortcutName(tool.Name())
	if name == "" {
		name = toolID
	}
	arguments := "--open-tool " + toolID
	workingDirectory := filepath.Dir(executable)
	script := buildShortcutScript(name, executable, arguments, workingDirectory)
	encoded := base64.StdEncoding.EncodeToString(utf16LE(script))
	command := exec.Command("powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded)
	var stdout, stderr bytes.Buffer
	command.Stdout = &stdout
	command.Stderr = &stderr
	if err := command.Run(); err != nil {
		return nil, bridgeErr("SHORTCUT_FAILED",
			"创建桌面快捷方式失败："+strings.TrimSpace(stderr.String()))
	}
	path := strings.TrimSpace(stdout.String())
	if path == "" {
		return nil, bridgeErr("SHORTCUT_FAILED", "快捷方式创建完成但没有返回路径")
	}
	return map[string]any{"path": path, "fileName": filepath.Base(path)}, nil
}

func buildShortcutScript(name, target, arguments, workingDirectory string) string {
	decode := func(value string) string {
		return base64.StdEncoding.EncodeToString([]byte(value))
	}
	return fmt.Sprintf(`
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
function D([string]$b) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b)) }
$name = D '%s'
$target = D '%s'
$arguments = D '%s'
$workdir = D '%s'
$desktop = [Environment]::GetFolderPath('Desktop')
$ws = New-Object -ComObject WScript.Shell
$path = Join-Path $desktop ($name + '.lnk')
$index = 2
while (Test-Path -LiteralPath $path) {
  $existing = $ws.CreateShortcut($path)
  if ($existing.TargetPath -eq $target -and $existing.Arguments -eq $arguments) { break }
  $path = Join-Path $desktop ($name + ' (' + $index + ').lnk')
  $index += 1
}
$shortcut = $ws.CreateShortcut($path)
$shortcut.TargetPath = $target
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = $workdir
$shortcut.IconLocation = $target + ',0'
$shortcut.Save()
Write-Output $path
`, decode(name), decode(target), decode(arguments), decode(workingDirectory))
}

func utf16LE(value string) []byte {
	units := utf16.Encode([]rune(value))
	data := make([]byte, len(units)*2)
	for index, unit := range units {
		data[index*2] = byte(unit)
		data[index*2+1] = byte(unit >> 8)
	}
	return data
}

func sanitizeShortcutName(value string) string {
	replacer := strings.NewReplacer("<", "_", ">", "_", ":", "_", `"`, "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	value = replacer.Replace(value)
	value = strings.Map(func(ch rune) rune {
		if ch < 0x20 {
			return -1
		}
		return ch
	}, value)
	value = strings.Trim(value, ". ")
	if len([]rune(value)) > 100 {
		value = string([]rune(value)[:100])
	}
	reserved := map[string]bool{
		"CON": true, "PRN": true, "AUX": true, "NUL": true,
		"COM1": true, "COM2": true, "COM3": true, "COM4": true, "COM5": true,
		"COM6": true, "COM7": true, "COM8": true, "COM9": true,
		"LPT1": true, "LPT2": true, "LPT3": true, "LPT4": true, "LPT5": true,
		"LPT6": true, "LPT7": true, "LPT8": true, "LPT9": true,
	}
	if reserved[strings.ToUpper(value)] {
		value = "_" + value
	}
	return value
}
