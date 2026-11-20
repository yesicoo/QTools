package main

import (
	"encoding/base64"
	"fmt"
	"mime"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

type ToolFiles struct {
	dir string
}

type StoredFile struct {
	Name        string `json:"name"`
	SizeBytes   int64  `json:"sizeBytes"`
	ModifiedUTC string `json:"modifiedUtc"`
}

func newToolFiles(toolDir string) *ToolFiles {
	return &ToolFiles{dir: filepath.Join(toolDir, "files")}
}

func safeFileName(name string) (string, error) {
	name = strings.TrimSpace(name)
	if name == "" || name == "." || name == ".." || strings.ContainsAny(name, "/\\\x00") ||
		len(name) > 200 {
		return "", bridgeErr("FILES_BAD_NAME", "文件名不合法")
	}
	return name, nil
}

func (f *ToolFiles) Save(name string, data []byte, contentType string) (int64, error) {
	name, err := safeFileName(name)
	if err != nil {
		return 0, err
	}
	if len(data) > 50*1024*1024 {
		return 0, bridgeErr("FILES_TOO_LARGE", "单个文件不超过 50MB")
	}
	if err := os.MkdirAll(f.dir, 0o755); err != nil {
		return 0, bridgeErr("FILES_WRITE_FAILED", err.Error())
	}
	if err := os.WriteFile(filepath.Join(f.dir, name), data, 0o644); err != nil {
		return 0, bridgeErr("FILES_WRITE_FAILED", err.Error())
	}
	_ = contentType
	return int64(len(data)), nil
}

func (f *ToolFiles) List() []StoredFile {
	entries, err := os.ReadDir(f.dir)
	if err != nil {
		return nil
	}
	result := []StoredFile{}
	for _, entry := range entries {
		if entry.IsDir() || strings.HasPrefix(entry.Name(), ".") {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			continue
		}
		result = append(result, StoredFile{
			Name: entry.Name(), SizeBytes: info.Size(),
			ModifiedUTC: info.ModTime().UTC().Format(time.RFC3339),
		})
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Name < result[j].Name })
	return result
}

func (f *ToolFiles) Read(name string) ([]byte, string, error) {
	name, err := safeFileName(name)
	if err != nil {
		return nil, "", err
	}
	path := filepath.Join(f.dir, name)
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, "", bridgeErr("FILES_NOT_FOUND", "文件不存在")
	}
	contentType := mime.TypeByExtension(filepath.Ext(name))
	if contentType == "" {
		contentType = "application/octet-stream"
	}
	return data, contentType, nil
}

func (f *ToolFiles) Delete(name string) (bool, error) {
	name, err := safeFileName(name)
	if err != nil {
		return false, err
	}
	err = os.Remove(filepath.Join(f.dir, name))
	if os.IsNotExist(err) {
		return false, nil
	}
	if err != nil {
		return false, bridgeErr("FILES_DELETE_FAILED", fmt.Sprintf("删除失败：%v", err))
	}
	return true, nil
}

func decodeBase64(value string) ([]byte, error) {
	data, err := base64.StdEncoding.DecodeString(value)
	if err != nil {
		if raw, rawErr := base64.RawStdEncoding.DecodeString(value); rawErr == nil {
			return raw, nil
		}
		return nil, bridgeErr("FILES_BAD_DATA", "dataBase64 不是合法 Base64")
	}
	return data, nil
}

func encodeBase64(data []byte) string { return base64.StdEncoding.EncodeToString(data) }
