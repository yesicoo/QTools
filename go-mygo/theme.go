package main

import "strings"

func themeIsDark(theme string) bool {
	switch normalizeTheme(theme) {
	case "dark":
		return true
	case "light":
		return false
	default:
		return systemPrefersDark()
	}
}

func isExplicitTheme(value string) bool {
	value = strings.ToLower(strings.TrimSpace(value))
	return value == "light" || value == "dark"
}
