//go:build windows

package main

import (
	"encoding/base64"
	"errors"
	"os"
	"path/filepath"
	"sync"
	"syscall"
	"unsafe"
)

const cryptProtectUIForbidden = 0x1

type dataBlob struct {
	cbData uint32
	pbData *byte
}

var (
	crypt32DLL             = syscall.NewLazyDLL("crypt32.dll")
	kernel32DLL            = syscall.NewLazyDLL("kernel32.dll")
	procCryptProtectData   = crypt32DLL.NewProc("CryptProtectData")
	procCryptUnprotectData = crypt32DLL.NewProc("CryptUnprotectData")
	procLocalFree          = kernel32DLL.NewProc("LocalFree")
)

type SecretStore struct {
	mu         sync.Mutex
	path       string
	legacyPath string
}

func NewSecretStore(dir string) *SecretStore {
	return &SecretStore{
		path:       filepath.Join(dir, "dpapi.QTools.AI.default.bin"),
		legacyPath: filepath.Join(dir, "ai-key.bin"),
	}
}

func (s *SecretStore) Mode() string { return "Windows DPAPI（当前用户）" }

func (s *SecretStore) Set(value string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if value == "" {
		return s.clearLocked()
	}
	plain := []byte(value)
	protected, err := dpapiProtect(plain)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(s.path), 0o755); err != nil {
		return err
	}
	temp := s.path + ".tmp"
	if err := os.WriteFile(temp, protected, 0o600); err != nil {
		return err
	}
	return os.Rename(temp, s.path)
}

func (s *SecretStore) Get() (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	raw, err := os.ReadFile(s.path)
	if err == nil {
		if plain, decryptErr := dpapiUnprotect(raw); decryptErr == nil && len(plain) > 0 {
			return string(plain), true
		}
	}
	text, err := os.ReadFile(s.legacyPath)
	if err != nil {
		return "", false
	}
	legacy, err := base64.StdEncoding.DecodeString(string(text))
	if err != nil {
		return "", false
	}
	plain, err := dpapiUnprotect(legacy)
	if err != nil || len(plain) == 0 {
		return "", false
	}
	return string(plain), true
}

func (s *SecretStore) Clear() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.clearLocked()
}

func (s *SecretStore) clearLocked() error {
	err := os.Remove(s.path)
	_ = os.Remove(s.legacyPath)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func (s *SecretStore) IsConfigured() bool {
	value, ok := s.Get()
	return ok && value != ""
}

func dpapiProtect(plain []byte) ([]byte, error) {
	in := blobFrom(plain)
	var out dataBlob
	result, _, err := procCryptProtectData.Call(
		uintptr(unsafe.Pointer(&in)), 0, 0, 0, 0,
		cryptProtectUIForbidden, uintptr(unsafe.Pointer(&out)),
	)
	if result == 0 {
		return nil, err
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(out.pbData)))
	return blobBytes(out), nil
}

func dpapiUnprotect(protected []byte) ([]byte, error) {
	in := blobFrom(protected)
	var out dataBlob
	result, _, err := procCryptUnprotectData.Call(
		uintptr(unsafe.Pointer(&in)), 0, 0, 0, 0,
		cryptProtectUIForbidden, uintptr(unsafe.Pointer(&out)),
	)
	if result == 0 {
		return nil, err
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(out.pbData)))
	return blobBytes(out), nil
}

func blobFrom(data []byte) dataBlob {
	if len(data) == 0 {
		return dataBlob{}
	}
	return dataBlob{cbData: uint32(len(data)), pbData: &data[0]}
}

func blobBytes(blob dataBlob) []byte {
	if blob.cbData == 0 || blob.pbData == nil {
		return nil
	}
	out := make([]byte, int(blob.cbData))
	copy(out, unsafe.Slice(blob.pbData, int(blob.cbData)))
	return out
}
