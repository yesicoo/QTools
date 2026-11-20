//go:build !windows

package main

import (
	"os"
	"path/filepath"
	"strings"
)

func collectPrograms() []discoveredProgram {
	programs := []discoveredProgram{}
	for _, root := range []string{"/Applications"} {
		collectApps(root, &programs, "Applications")
	}
	if home, err := os.UserHomeDir(); err == nil {
		collectApps(filepath.Join(home, "Applications"), &programs, "Applications")
	}
	for _, root := range []string{"/usr/share/applications", "/var/lib/snapd/desktop/applications"} {
		collectDesktop(root, &programs)
	}
	if home, err := os.UserHomeDir(); err == nil {
		collectDesktop(filepath.Join(home, ".local", "share", "applications"), &programs)
	}
	return programs
}

func collectApps(root string, result *[]discoveredProgram, source string) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	for _, entry := range entries {
		if !entry.IsDir() || !strings.HasSuffix(strings.ToLower(entry.Name()), ".app") {
			continue
		}
		path := filepath.Join(root, entry.Name())
		*result = append(*result, discoveredProgram{
			Name:   strings.TrimSuffix(entry.Name(), filepath.Ext(entry.Name())),
			Target: path, Source: source,
		})
	}
}

func collectDesktop(root string, result *[]discoveredProgram) {
	entries, err := os.ReadDir(root)
	if err != nil {
		return
	}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(strings.ToLower(entry.Name()), ".desktop") {
			continue
		}
		path := filepath.Join(root, entry.Name())
		*result = append(*result, discoveredProgram{
			Name:   strings.TrimSuffix(entry.Name(), ".desktop"),
			Target: path, Source: "应用程序",
		})
	}
}

func ensureAppIcon(*LoadedApp) {}
