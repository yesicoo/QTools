//go:build !windows

package main

func ensureIconAt(string, string) bool { return false }
