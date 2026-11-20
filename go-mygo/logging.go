package main

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sync"
	"time"
)

type Logger struct {
	mu      sync.Mutex
	file    *os.File
	path    string
	dev     bool
	maxSize int64
}

var log = &Logger{maxSize: 2 * 1024 * 1024}

var secretPattern = regexp.MustCompile(`(?i)(sk-[A-Za-z0-9_-]{8,}|Bearer\s+[A-Za-z0-9._-]{8,})`)

func (l *Logger) Initialize(path string, dev bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.dev = dev
	l.path = path
	_ = os.MkdirAll(filepath.Dir(path), 0o755)
	if info, err := os.Stat(path); err == nil && info.Size() > l.maxSize {
		_ = os.Rename(path, path+".1")
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err == nil {
		l.file = f
	}
}

func (l *Logger) write(level, message string) {
	message = secretPattern.ReplaceAllString(message, "***")
	line := fmt.Sprintf("%s [%s] %s\n", time.Now().Format("2006-01-02 15:04:05.000"), level, message)
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.file != nil {
		_, _ = l.file.WriteString(line)
	}
	if l.dev || level == "ERROR" {
		_, _ = os.Stderr.WriteString(line)
	}
}

func logInfo(format string, args ...any)  { log.write("INFO", fmt.Sprintf(format, args...)) }
func logWarn(format string, args ...any)  { log.write("WARN", fmt.Sprintf(format, args...)) }
func logError(format string, args ...any) { log.write("ERROR", fmt.Sprintf(format, args...)) }
func logDebug(format string, args ...any) { log.write("DEBUG", fmt.Sprintf(format, args...)) }
func redact(value string) string          { return secretPattern.ReplaceAllString(value, "***") }
