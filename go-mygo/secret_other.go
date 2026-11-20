//go:build !windows

package main

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"os"
	"path/filepath"
	"sync"
)

type SecretStore struct {
	mu      sync.Mutex
	dir     string
	keyPath string
	path    string
}

func NewSecretStore(dir string) *SecretStore {
	return &SecretStore{
		dir: dir, keyPath: filepath.Join(dir, "aes-key.bin"), path: filepath.Join(dir, "ai-key.bin"),
	}
}

func (s *SecretStore) Mode() string { return "AES-GCM 本地文件" }

func (s *SecretStore) key() ([]byte, error) {
	if data, err := os.ReadFile(s.keyPath); err == nil && len(data) == 32 {
		return data, nil
	}
	key := make([]byte, 32)
	if _, err := rand.Read(key); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(s.dir, 0o700); err != nil {
		return nil, err
	}
	if err := os.WriteFile(s.keyPath, key, 0o600); err != nil {
		return nil, err
	}
	return key, nil
}

func (s *SecretStore) Set(value string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if value == "" {
		return s.clearLocked()
	}
	key, err := s.key()
	if err != nil {
		return err
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return err
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return err
	}
	sealed := gcm.Seal(nonce, nonce, []byte(value), nil)
	temp := s.path + ".tmp"
	if err := os.WriteFile(temp, []byte(base64.StdEncoding.EncodeToString(sealed)), 0o600); err != nil {
		return err
	}
	return os.Rename(temp, s.path)
}

func (s *SecretStore) Get() (string, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	text, err := os.ReadFile(s.path)
	if err != nil {
		return "", false
	}
	raw, err := base64.StdEncoding.DecodeString(string(text))
	if err != nil {
		return "", false
	}
	key, err := s.key()
	if err != nil {
		return "", false
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return "", false
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil || len(raw) < gcm.NonceSize() {
		return "", false
	}
	plain, err := gcm.Open(nil, raw[:gcm.NonceSize()], raw[gcm.NonceSize():], nil)
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
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func (s *SecretStore) IsConfigured() bool {
	value, ok := s.Get()
	return ok && value != ""
}
