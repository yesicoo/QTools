package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"strconv"
	"sync"
	"time"

	"github.com/egoist/mygo"
)

type AppState struct {
	Paths        *AppPaths
	Config       *ConfigStore
	Secrets      *SecretStore
	Tools        *ToolRegistry
	Apps         *AppRegistry
	WebTools     *WebToolRegistry
	Storage      *ToolStorage
	Usage        *UsageStore
	Hidden       *HiddenProgramStore
	Pinned       *PinnedItemStore
	ProgramIndex *ProgramIndex

	mu            sync.RWMutex
	port          int
	windows       map[string]*mygo.Window
	windowRoles   map[*mygo.Window]string
	pendingCancel map[string]context.CancelFunc
	embedTokens   map[string]embedToken
	exitRequested bool
	tray          *mygo.Tray
}

func NewAppState(paths *AppPaths) *AppState {
	cfg := NewConfigStore(paths.ConfigFile())
	state := &AppState{
		Paths: paths, Config: cfg, Secrets: NewSecretStore(paths.SecretsDir()),
		Tools: NewToolRegistry(paths.ToolsDir()), Apps: NewAppRegistry(paths.AppsDir()),
		WebTools: NewWebToolRegistry(paths.WebToolsDir()),
		Storage:  NewToolStorage(paths.StorageDir(), cfg.Snapshot().Storage.MaxValueBytes, cfg.Snapshot().Storage.MaxTotalBytesPerTool),
		Usage:    NewUsageStore(paths.UsageFile()), Hidden: NewHiddenProgramStore(paths.HiddenProgramsFile()),
		Pinned:       NewPinnedItemStore(paths.PinnedItemsFile()),
		ProgramIndex: NewProgramIndex(paths.ProgramIndexFile()),
		windows:      map[string]*mygo.Window{}, windowRoles: map[*mygo.Window]string{},
		pendingCancel: map[string]context.CancelFunc{}, embedTokens: map[string]embedToken{},
	}
	state.seedUsage()
	return state
}

func (s *AppState) seedUsage() {
	ids := []string{}
	for _, tool := range s.Tools.All() {
		ids = append(ids, tool.Manifest.ID)
	}
	for _, app := range s.Apps.All() {
		ids = append(ids, app.Entry.ID)
	}
	for _, web := range s.WebTools.All() {
		ids = append(ids, web.Entry.ID)
	}
	s.Usage.EnsureSeeded(ids)
}

func (s *AppState) SetPort(port int) {
	s.mu.Lock()
	s.port = port
	s.mu.Unlock()
}

func (s *AppState) Port() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.port
}

func (s *AppState) Origin() string {
	port := s.Port()
	return "http://127.0.0.1:" + strconv.Itoa(port)
}

func (s *AppState) RegisterWindow(window *mygo.Window, label, role string) {
	s.mu.Lock()
	s.windows[label] = window
	s.windowRoles[window] = role
	s.mu.Unlock()
}

func (s *AppState) UnregisterWindow(label string) {
	s.mu.Lock()
	if window := s.windows[label]; window != nil {
		delete(s.windowRoles, window)
	}
	delete(s.windows, label)
	s.mu.Unlock()
}

func (s *AppState) Window(label string) *mygo.Window {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.windows[label]
}

func (s *AppState) RoleOf(window *mygo.Window) string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.windowRoles[window]
}

func (s *AppState) AllWindows() []*mygo.Window {
	s.mu.RLock()
	defer s.mu.RUnlock()
	result := make([]*mygo.Window, 0, len(s.windows))
	for _, window := range s.windows {
		result = append(result, window)
	}
	return result
}

func (s *AppState) AddCancel(id string, cancel context.CancelFunc) {
	s.mu.Lock()
	if previous := s.pendingCancel[id]; previous != nil {
		previous()
	}
	s.pendingCancel[id] = cancel
	s.mu.Unlock()
}

func (s *AppState) RemoveCancel(id string) {
	s.mu.Lock()
	delete(s.pendingCancel, id)
	s.mu.Unlock()
}

func (s *AppState) CancelRequest(id string) bool {
	s.mu.Lock()
	cancel := s.pendingCancel[id]
	s.mu.Unlock()
	if cancel != nil {
		cancel()
		return true
	}
	return false
}

func (s *AppState) RequestExit() {
	s.mu.Lock()
	s.exitRequested = true
	s.mu.Unlock()
}

func (s *AppState) ExitRequested() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.exitRequested
}

func (s *AppState) SetTray(tray *mygo.Tray) {
	s.mu.Lock()
	s.tray = tray
	s.mu.Unlock()
}

func (s *AppState) Tray() *mygo.Tray {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.tray
}

type embedToken struct {
	toolID  string
	expires time.Time
}

func (s *AppState) RegisterEmbedToken(toolID string) string {
	buf := make([]byte, 16)
	_, _ = rand.Read(buf)
	token := hex.EncodeToString(buf)
	s.mu.Lock()
	if len(s.embedTokens) >= 64 {
		for key, item := range s.embedTokens {
			if time.Now().After(item.expires) {
				delete(s.embedTokens, key)
			}
		}
	}
	s.embedTokens[token] = embedToken{toolID: toolID, expires: time.Now().Add(24 * time.Hour)}
	s.mu.Unlock()
	return token
}

func (s *AppState) ResolveEmbedToken(token string) string {
	s.mu.Lock()
	defer s.mu.Unlock()
	item, ok := s.embedTokens[token]
	if !ok || time.Now().After(item.expires) {
		delete(s.embedTokens, token)
		return ""
	}
	return item.toolID
}

func (s *AppState) RevokeEmbedTokens(toolID string) {
	s.mu.Lock()
	for token, item := range s.embedTokens {
		if item.toolID == toolID {
			delete(s.embedTokens, token)
		}
	}
	s.mu.Unlock()
}
