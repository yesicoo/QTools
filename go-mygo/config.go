package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type AppConfig struct {
	AI      AIConfigSection      `json:"ai"`
	Server  ServerConfigSection  `json:"server"`
	Storage StorageConfigSection `json:"storage"`
	Backup  BackupConfigSection  `json:"backup"`
	Shell   ShellConfigSection   `json:"shell"`
}

type AIConfigSection struct {
	BaseURL        string  `json:"baseUrl"`
	Model          string  `json:"model"`
	Temperature    float64 `json:"temperature"`
	TimeoutSeconds int     `json:"timeoutSeconds"`
}

type ServerConfigSection struct {
	MaxRequestBodyBytes int64 `json:"maxRequestBodyBytes"`
	MaxFileBytes        int64 `json:"maxFileBytes"`
}

type StorageConfigSection struct {
	MaxValueBytes        int64 `json:"maxValueBytes"`
	MaxTotalBytesPerTool int64 `json:"maxTotalBytesPerTool"`
}

type BackupConfigSection struct {
	GitRemoteURL   *string `json:"gitRemoteUrl"`
	LastPushUTC    *string `json:"lastPushUtc"`
	LastPushStatus *string `json:"lastPushStatus"`
}

type ShellConfigSection struct {
	HotkeyEnabled        bool                       `json:"hotkeyEnabled"`
	MainWindowWidth      *int                       `json:"mainWindowWidth"`
	MainWindowHeight     *int                       `json:"mainWindowHeight"`
	MainWindowLeft       *int                       `json:"mainWindowLeft"`
	MainWindowTop        *int                       `json:"mainWindowTop"`
	TrayMenuItems        int                        `json:"trayMenuItems"`
	ContentWindowLeft    *int                       `json:"contentWindowLeft"`
	ContentWindowTop     *int                       `json:"contentWindowTop"`
	ContentWindowWidth   *int                       `json:"contentWindowWidth"`
	ContentWindowHeight  *int                       `json:"contentWindowHeight"`
	SettingsWindowLeft   *int                       `json:"settingsWindowLeft"`
	SettingsWindowTop    *int                       `json:"settingsWindowTop"`
	SettingsWindowWidth  *int                       `json:"settingsWindowWidth"`
	SettingsWindowHeight *int                       `json:"settingsWindowHeight"`
	ContentWindowTopmost bool                       `json:"contentWindowTopmost"`
	ContentWindowPinned  bool                       `json:"contentWindowPinned"`
	ToolWindowStates     map[string]ToolWindowState `json:"toolWindowStates"`
	Theme                string                     `json:"theme"`
}

type ToolWindowState struct {
	Topmost  bool     `json:"topmost"`
	Pinned   bool     `json:"pinned"`
	ChipLeft *float64 `json:"chipLeft"`
	ChipTop  *float64 `json:"chipTop"`
	Left     *int     `json:"left"`
	Top      *int     `json:"top"`
	Width    *int     `json:"width"`
	Height   *int     `json:"height"`
}

func defaultConfig() AppConfig {
	return AppConfig{
		AI: AIConfigSection{
			BaseURL: "https://api.deepseek.com", Model: "deepseek-flash",
			Temperature: 0.7, TimeoutSeconds: 60,
		},
		Server: ServerConfigSection{
			MaxRequestBodyBytes: 1024 * 1024, MaxFileBytes: 8 * 1024 * 1024,
		},
		Storage: StorageConfigSection{
			MaxValueBytes: 256 * 1024, MaxTotalBytesPerTool: 5 * 1024 * 1024,
		},
		Shell: ShellConfigSection{
			HotkeyEnabled: true, TrayMenuItems: 10, Theme: "system",
			ToolWindowStates: map[string]ToolWindowState{},
		},
	}
}

type ConfigStore struct {
	mu   sync.Mutex
	path string
	cfg  AppConfig
}

func NewConfigStore(path string) *ConfigStore {
	store := &ConfigStore{path: path, cfg: defaultConfig()}
	store.load()
	return store
}

func (s *ConfigStore) load() {
	text, err := os.ReadFile(s.path)
	if err != nil {
		_ = s.saveLocked()
		return
	}
	var cfg AppConfig
	if err := json.Unmarshal(text, &cfg); err != nil {
		backup := fmt.Sprintf("%s.corrupt-%s", s.path, time.Now().Format("20060102150405"))
		_ = os.Rename(s.path, backup)
		s.cfg = defaultConfig()
		_ = s.saveLocked()
		return
	}
	normalizeConfig(&cfg)
	s.cfg = cfg
}

func normalizeConfig(cfg *AppConfig) {
	d := defaultConfig()
	if strings.TrimSpace(cfg.AI.BaseURL) == "" {
		cfg.AI.BaseURL = d.AI.BaseURL
	}
	if strings.TrimSpace(cfg.AI.Model) == "" {
		cfg.AI.Model = d.AI.Model
	}
	if cfg.AI.Temperature < 0 || cfg.AI.Temperature > 2 {
		cfg.AI.Temperature = d.AI.Temperature
	}
	if cfg.AI.TimeoutSeconds < 1 || cfg.AI.TimeoutSeconds > 600 {
		cfg.AI.TimeoutSeconds = d.AI.TimeoutSeconds
	}
	if cfg.Server.MaxRequestBodyBytes <= 0 {
		cfg.Server.MaxRequestBodyBytes = d.Server.MaxRequestBodyBytes
	}
	if cfg.Server.MaxFileBytes <= 0 {
		cfg.Server.MaxFileBytes = d.Server.MaxFileBytes
	}
	if cfg.Storage.MaxValueBytes <= 0 {
		cfg.Storage.MaxValueBytes = d.Storage.MaxValueBytes
	}
	if cfg.Storage.MaxTotalBytesPerTool <= 0 {
		cfg.Storage.MaxTotalBytesPerTool = d.Storage.MaxTotalBytesPerTool
	}
	if cfg.Shell.TrayMenuItems < 1 || cfg.Shell.TrayMenuItems > 20 {
		cfg.Shell.TrayMenuItems = d.Shell.TrayMenuItems
	}
	cfg.Shell.Theme = normalizeTheme(cfg.Shell.Theme)
	if cfg.Shell.ToolWindowStates == nil {
		cfg.Shell.ToolWindowStates = map[string]ToolWindowState{}
	}
}

func normalizeTheme(value string) string {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "light":
		return "light"
	case "dark":
		return "dark"
	default:
		return "system"
	}
}

func explicitTheme(theme string) string {
	switch normalizeTheme(theme) {
	case "light":
		return "light"
	case "dark":
		return "dark"
	default:
		return ""
	}
}

func (s *ConfigStore) Snapshot() AppConfig {
	s.mu.Lock()
	defer s.mu.Unlock()
	cfg := s.cfg
	if cfg.Shell.ToolWindowStates == nil {
		cfg.Shell.ToolWindowStates = map[string]ToolWindowState{}
	}
	return cfg
}

func (s *ConfigStore) Reload() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.load()
}

func (s *ConfigStore) Update(fn func(*AppConfig)) AppConfig {
	s.mu.Lock()
	defer s.mu.Unlock()
	fn(&s.cfg)
	normalizeConfig(&s.cfg)
	_ = s.saveLocked()
	return s.cfg
}

func (s *ConfigStore) saveLocked() error {
	if err := os.MkdirAll(filepath.Dir(s.path), 0o755); err != nil {
		return err
	}
	data, err := json.MarshalIndent(s.cfg, "", "  ")
	if err != nil {
		return err
	}
	temp := s.path + ".tmp"
	if err := os.WriteFile(temp, append(data, '\n'), 0o644); err != nil {
		return err
	}
	return os.Rename(temp, s.path)
}

func (s *ConfigStore) ToolState(id string) ToolWindowState {
	cfg := s.Snapshot()
	return cfg.Shell.ToolWindowStates[id]
}

func (s *ConfigStore) UpdateToolState(id string, fn func(*ToolWindowState)) ToolWindowState {
	var state ToolWindowState
	s.Update(func(cfg *AppConfig) {
		if cfg.Shell.ToolWindowStates == nil {
			cfg.Shell.ToolWindowStates = map[string]ToolWindowState{}
		}
		state = cfg.Shell.ToolWindowStates[id]
		fn(&state)
		cfg.Shell.ToolWindowStates[id] = state
	})
	return state
}
