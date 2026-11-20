package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

type toolStorageFile map[string]json.RawMessage

type ToolStorage struct {
	mu       sync.Mutex
	dir      string
	maxValue int64
	maxTotal int64
}

func NewToolStorage(dir string, maxValue, maxTotal int64) *ToolStorage {
	return &ToolStorage{dir: dir, maxValue: maxValue, maxTotal: maxTotal}
}

func (s *ToolStorage) path(toolID string) string {
	return filepath.Join(s.dir, toolID+".json")
}

func (s *ToolStorage) load(toolID string) toolStorageFile {
	data, err := os.ReadFile(s.path(toolID))
	if err != nil {
		return toolStorageFile{}
	}
	var file toolStorageFile
	if json.Unmarshal(data, &file) != nil {
		return toolStorageFile{}
	}
	return file
}

func (s *ToolStorage) save(toolID string, file toolStorageFile) error {
	data, err := json.Marshal(file)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(s.dir, 0o755); err != nil {
		return err
	}
	temp := s.path(toolID) + ".tmp"
	if err := os.WriteFile(temp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(temp, s.path(toolID))
}

func (s *ToolStorage) Get(toolID, key string) (json.RawMessage, bool, error) {
	if err := validateStorageKey(key); err != nil {
		return nil, false, err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	value, ok := s.load(toolID)[key]
	return value, ok, nil
}

func (s *ToolStorage) Set(toolID, key string, value json.RawMessage) error {
	if err := validateStorageKey(key); err != nil {
		return err
	}
	if int64(len(value)) > s.maxValue {
		return bridgeErr("STORAGE_VALUE_TOO_LARGE", "单个值超过大小限制")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	file := s.load(toolID)
	file[key] = value
	var total int64
	for _, item := range file {
		total += int64(len(item))
	}
	if total > s.maxTotal {
		return bridgeErr("STORAGE_QUOTA_EXCEEDED", "该工具存储空间已满")
	}
	if err := s.save(toolID, file); err != nil {
		return bridgeErr("STORAGE_WRITE_FAILED", fmt.Sprintf("保存失败：%v", err))
	}
	return nil
}

func (s *ToolStorage) Remove(toolID, key string) error {
	if err := validateStorageKey(key); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	file := s.load(toolID)
	delete(file, key)
	if err := s.save(toolID, file); err != nil {
		return bridgeErr("STORAGE_WRITE_FAILED", fmt.Sprintf("保存失败：%v", err))
	}
	return nil
}

func (s *ToolStorage) List(toolID string) []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	file := s.load(toolID)
	keys := make([]string, 0, len(file))
	for key := range file {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func (s *ToolStorage) Delete(toolID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	_ = os.Remove(s.path(toolID))
}

func validateStorageKey(key string) error {
	if strings.TrimSpace(key) == "" || len(key) > 128 || strings.ContainsRune(key, 0) {
		return bridgeErr("STORAGE_BAD_KEY", "key 必填且不超过 128 字符")
	}
	return nil
}

func (s *ToolStorage) SizeBytes(toolID string) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	var total int64
	for _, value := range s.load(toolID) {
		total += int64(len(value))
	}
	return total
}
