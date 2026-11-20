//go:build windows

package main

import (
	"os"
	"path/filepath"
	"strings"
)

func collectPrograms() []discoveredProgram {
	programs := []discoveredProgram{}
	if programData := os.Getenv("ProgramData"); programData != "" {
		root := filepath.Join(programData, "Microsoft", "Windows", "Start Menu", "Programs")
		collectLnk(root, &programs, "开始菜单", 3)
	}
	if appData := os.Getenv("APPDATA"); appData != "" {
		root := filepath.Join(appData, "Microsoft", "Windows", "Start Menu", "Programs")
		collectLnk(root, &programs, "开始菜单", 3)
	}
	if profile := os.Getenv("USERPROFILE"); profile != "" {
		root := filepath.Join(profile, "Desktop")
		collectLnk(root, &programs, "桌面", 1)
	}
	return programs
}

func collectLnk(root string, result *[]discoveredProgram, source string, maxDepth int) {
	info, err := os.Stat(root)
	if err != nil || !info.IsDir() {
		return
	}
	stack := []struct {
		dir   string
		depth int
	}{{root, 0}}
	for len(stack) > 0 {
		item := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		entries, err := os.ReadDir(item.dir)
		if err != nil {
			continue
		}
		for _, entry := range entries {
			path := filepath.Join(item.dir, entry.Name())
			if entry.IsDir() {
				if item.depth < maxDepth {
					stack = append(stack, struct {
						dir   string
						depth int
					}{path, item.depth + 1})
				}
				continue
			}
			extension := strings.ToLower(filepath.Ext(path))
			if extension != ".lnk" && extension != ".exe" {
				continue
			}
			name := strings.TrimSuffix(filepath.Base(path), filepath.Ext(path))
			*result = append(*result, discoveredProgram{Name: name, Target: path, Source: source})
		}
	}
}

func ensureAppIcon(app *LoadedApp) {
	if app == nil {
		return
	}
	iconPath := filepath.Join(app.Directory, "icon.png")
	ensureIconAt(app.Entry.Target, iconPath)
}
