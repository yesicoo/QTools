package main

import (
	"fmt"
	"os"
	"path/filepath"
	"runtime"
)

// AppPaths 是运行期路径的唯一真源，目录布局与 .NET / Rust 版保持一致，
// 三个版本可共用同一份 %APPDATA%\QTools 数据。
type AppPaths struct {
	Root string
}

func NewAppPaths(explicit string) (*AppPaths, error) {
	root := explicit
	if root == "" {
		root = recommendedRoot()
	} else if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	paths := &AppPaths{Root: root}
	for _, dir := range []string{
		paths.ToolsDir(), paths.AppsDir(), paths.WebToolsDir(),
		paths.DataDir(), paths.StorageDir(), paths.LogsDir(),
		paths.SecretsDir(), paths.IconCacheDir(), paths.ImportStagingDir(),
	} {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return nil, fmt.Errorf("创建目录 %s 失败：%w", dir, err)
		}
	}
	return paths, nil
}

func recommendedRoot() string {
	switch runtime.GOOS {
	case "windows":
		if appData := os.Getenv("APPDATA"); appData != "" {
			return filepath.Join(appData, "QTools")
		}
	case "darwin":
		if home, err := os.UserHomeDir(); err == nil {
			return filepath.Join(home, "Library", "Application Support", "QTools")
		}
	default:
		if xdg := os.Getenv("XDG_DATA_HOME"); xdg != "" {
			return filepath.Join(xdg, "QTools")
		}
		if home, err := os.UserHomeDir(); err == nil {
			return filepath.Join(home, ".local", "share", "QTools")
		}
	}
	if home, err := os.UserHomeDir(); err == nil {
		return filepath.Join(home, "QTools")
	}
	return "QTools"
}

func (p *AppPaths) ToolsDir() string         { return filepath.Join(p.Root, "tools") }
func (p *AppPaths) AppsDir() string          { return filepath.Join(p.Root, "apps") }
func (p *AppPaths) WebToolsDir() string      { return filepath.Join(p.Root, "webtools") }
func (p *AppPaths) DataDir() string          { return filepath.Join(p.Root, "data") }
func (p *AppPaths) StorageDir() string       { return filepath.Join(p.DataDir(), "storage") }
func (p *AppPaths) LogsDir() string          { return filepath.Join(p.DataDir(), "logs") }
func (p *AppPaths) SecretsDir() string       { return filepath.Join(p.DataDir(), "secrets") }
func (p *AppPaths) IconCacheDir() string     { return filepath.Join(p.DataDir(), "icon-cache") }
func (p *AppPaths) ImportStagingDir() string { return filepath.Join(p.DataDir(), "import-staging") }
func (p *AppPaths) ConfigFile() string       { return filepath.Join(p.DataDir(), "config.json") }
func (p *AppPaths) LogFile() string          { return filepath.Join(p.LogsDir(), "qtools.log") }
func (p *AppPaths) ProgramIndexFile() string { return filepath.Join(p.DataDir(), "program-index.json") }
func (p *AppPaths) UsageFile() string        { return filepath.Join(p.DataDir(), "usage.json") }
func (p *AppPaths) HiddenProgramsFile() string {
	return filepath.Join(p.DataDir(), "hidden-programs.json")
}
func (p *AppPaths) PinnedItemsFile() string {
	return filepath.Join(p.DataDir(), "pinned-items.json")
}
func (p *AppPaths) ToolDir(id string) string    { return filepath.Join(p.ToolsDir(), id) }
func (p *AppPaths) AppDir(id string) string     { return filepath.Join(p.AppsDir(), id) }
func (p *AppPaths) WebToolDir(id string) string { return filepath.Join(p.WebToolsDir(), id) }
