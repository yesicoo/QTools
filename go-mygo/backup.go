package main

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"github.com/egoist/mygo"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const maxBackupBytes = 1024 * 1024 * 1024

type backupManifest struct {
	SchemaVersion int     `json:"schemaVersion"`
	CreatedAtUTC  string  `json:"createdAtUtc"`
	AppVersion    string  `json:"appVersion"`
	Note          *string `json:"note,omitempty"`
}

type backupEntry struct {
	Name         string  `json:"name"`
	CreatedAtUTC string  `json:"createdAtUtc"`
	SizeBytes    int64   `json:"sizeBytes"`
	AppVersion   string  `json:"appVersion"`
	Note         *string `json:"note,omitempty"`
}

func backupsDir(state *AppState) string { return filepath.Join(state.Paths.Root, "backups") }

func listBackups(state *AppState) []backupEntry {
	entries, err := os.ReadDir(backupsDir(state))
	if err != nil {
		return []backupEntry{}
	}
	result := []backupEntry{}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(strings.ToLower(entry.Name()), ".zip") {
			continue
		}
		path := filepath.Join(backupsDir(state), entry.Name())
		info, err := entry.Info()
		if err != nil {
			continue
		}
		createdAt := info.ModTime().UTC().Format(time.RFC3339)
		appVersion := "未知"
		var note *string
		if manifest, err := readBackupManifest(path); err == nil {
			createdAt = manifest.CreatedAtUTC
			appVersion = manifest.AppVersion
			note = manifest.Note
		}
		result = append(result, backupEntry{
			Name: entry.Name(), CreatedAtUTC: createdAt, SizeBytes: info.Size(),
			AppVersion: appVersion, Note: note,
		})
	}
	sort.Slice(result, func(i, j int) bool {
		return strings.ToLower(result[i].Name) > strings.ToLower(result[j].Name)
	})
	return result
}

func createBackupWithNote(state *AppState, note string) (string, int64, string, error) {
	dir := backupsDir(state)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", 0, "", bridgeErr("BACKUP_CREATE_FAILED", err.Error())
	}
	createdAt := time.Now().UTC().Format(time.RFC3339)
	base := "qtools-backup-" + time.Now().Format("20060102-150405") + ".zip"
	name := uniqueBackupName(dir, base)
	target := filepath.Join(dir, name)
	temp := target + ".tmp"
	manifest := backupManifest{
		SchemaVersion: 1, CreatedAtUTC: createdAt, AppVersion: appVersion,
		Note: optionalText(note),
	}
	file, err := os.Create(temp)
	if err != nil {
		return "", 0, "", bridgeErr("BACKUP_CREATE_FAILED", err.Error())
	}
	writer := zip.NewWriter(file)
	writeErr := func() error {
		header, _ := zip.FileInfoHeader(mustFileInfo(temp))
		header.Name = "backup.json"
		header.Method = zip.Deflate
		entry, err := writer.CreateHeader(header)
		if err != nil {
			return err
		}
		data, _ := json.Marshal(manifest)
		if _, err := entry.Write(data); err != nil {
			return err
		}
		for _, relative := range backupPayloadFiles(state.Paths.Root) {
			source := filepath.Join(state.Paths.Root, filepath.FromSlash(relative))
			input, err := os.Open(source)
			if err != nil {
				continue
			}
			info, err := input.Stat()
			if err != nil {
				input.Close()
				continue
			}
			header, _ := zip.FileInfoHeader(info)
			header.Name = relative
			header.Method = zip.Deflate
			entry, err := writer.CreateHeader(header)
			if err == nil {
				_, _ = io.Copy(entry, input)
			}
			input.Close()
		}
		return nil
	}()
	if writeErr == nil {
		writeErr = writer.Close()
	} else {
		_ = writer.Close()
	}
	_ = file.Close()
	if writeErr != nil {
		_ = os.Remove(temp)
		return "", 0, "", bridgeErr("BACKUP_CREATE_FAILED", writeErr.Error())
	}
	if err := os.Rename(temp, target); err != nil {
		_ = os.Remove(temp)
		return "", 0, "", bridgeErr("BACKUP_CREATE_FAILED", err.Error())
	}
	info, _ := os.Stat(target)
	var size int64
	if info != nil {
		size = info.Size()
	}
	return name, size, createdAt, nil
}

func backupPayloadFiles(root string) []string {
	result := []string{}
	for _, directory := range []string{"tools", "apps", "data/storage"} {
		base := filepath.Join(root, filepath.FromSlash(directory))
		_ = filepath.WalkDir(base, func(path string, entry os.DirEntry, err error) error {
			if err != nil || entry.IsDir() {
				return nil
			}
			relative, err := filepath.Rel(root, path)
			if err == nil {
				result = append(result, filepath.ToSlash(relative))
			}
			return nil
		})
	}
	if fileExists(filepath.Join(root, "data", "config.json")) {
		result = append(result, "data/config.json")
	}
	return result
}

func restoreBackup(state *AppState, name string) (any, error) {
	safety, err := restoreBackupByName(state, name)
	if err != nil {
		return nil, err
	}
	state.refreshAll()
	return map[string]any{"ok": true, "safetyBackupName": safety}, nil
}

func restoreBackupByName(state *AppState, name string) (string, error) {
	path, err := resolveBackupPath(state, name)
	if err != nil {
		return "", err
	}
	if err := validateBackupArchive(path); err != nil {
		return "", err
	}
	safety, _, _, err := createBackupWithNote(state, "自动快照：恢复前")
	if err != nil {
		return "", err
	}
	staging := filepath.Join(backupsDir(state), fmt.Sprintf(".restore-%d", time.Now().UnixNano()))
	if err := os.MkdirAll(staging, 0o755); err != nil {
		return "", bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
	}
	defer os.RemoveAll(staging)
	if err := extractBackupArchive(path, staging); err != nil {
		return "", err
	}
	for _, directory := range []string{"tools", "apps", "data/storage"} {
		live := filepath.Join(state.Paths.Root, filepath.FromSlash(directory))
		restored := filepath.Join(staging, filepath.FromSlash(directory))
		_ = os.RemoveAll(live)
		if fileExists(restored) {
			if err := os.Rename(restored, live); err != nil {
				return "", bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
			}
		} else {
			_ = os.MkdirAll(live, 0o755)
		}
	}
	restoredConfig := filepath.Join(staging, "data", "config.json")
	if fileExists(restoredConfig) {
		liveConfig := filepath.Join(state.Paths.Root, "data", "config.json")
		_ = os.MkdirAll(filepath.Dir(liveConfig), 0o755)
		_ = os.Remove(liveConfig)
		if err := os.Rename(restoredConfig, liveConfig); err != nil {
			return "", bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
		}
	}
	return safety, nil
}

func deleteBackup(state *AppState, name string) (any, error) {
	path, err := resolveBackupPath(state, name)
	if err != nil {
		return nil, err
	}
	if err := os.Remove(path); err != nil {
		return nil, bridgeErr("BACKUP_DELETE_FAILED", err.Error())
	}
	return map[string]any{"ok": true}, nil
}

func importBackupFile(state *AppState, parent *mygo.Window) (any, error) {
	files, err := mygo.Dialog.Open(mygo.OpenDialogOptions{
		Parent: parent, Title: "选择 QTools 备份包",
		Filters: []mygo.FileFilter{{Name: "QTools 备份包", Extensions: []string{"zip"}}},
	})
	if err != nil {
		return nil, err
	}
	if len(files) == 0 {
		return map[string]any{"name": nil}, nil
	}
	file := files[0]
	info, err := os.Stat(file)
	if err != nil || info.Size() > maxBackupBytes {
		return nil, bridgeErr("BACKUP_TOO_LARGE", "备份文件不存在或超过 1 GB")
	}
	if err := validateBackupArchive(file); err != nil {
		return nil, err
	}
	dir := backupsDir(state)
	_ = os.MkdirAll(dir, 0o755)
	base := filepath.Base(file)
	if !strings.HasSuffix(strings.ToLower(base), ".zip") {
		base = "imported-" + time.Now().Format("20060102-150405") + ".zip"
	}
	name := uniqueBackupName(dir, base)
	if err := copyFile(file, filepath.Join(dir, name)); err != nil {
		return nil, bridgeErr("BACKUP_IMPORT_FAILED", err.Error())
	}
	return map[string]any{"name": name}, nil
}

func resolveBackupPath(state *AppState, name string) (string, error) {
	if name == "" || len(name) > 128 || !strings.HasSuffix(strings.ToLower(name), ".zip") {
		return "", bridgeErr("BACKUP_NAME_INVALID", "备份文件名不合法")
	}
	for _, ch := range name {
		if !(ch == '.' || ch == '_' || ch == '-' || ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || ch >= '0' && ch <= '9') {
			return "", bridgeErr("BACKUP_NAME_INVALID", "备份文件名不合法")
		}
	}
	return filepath.Join(backupsDir(state), name), nil
}

func uniqueBackupName(dir, candidate string) string {
	if !fileExists(filepath.Join(dir, candidate)) {
		return candidate
	}
	stem := strings.TrimSuffix(candidate, ".zip")
	for i := 2; i < 1000; i++ {
		next := fmt.Sprintf("%s-%d.zip", stem, i)
		if !fileExists(filepath.Join(dir, next)) {
			return next
		}
	}
	return fmt.Sprintf("%s-%d.zip", stem, time.Now().Unix())
}

func readBackupManifest(path string) (backupManifest, error) {
	var manifest backupManifest
	reader, err := zip.OpenReader(path)
	if err != nil {
		return manifest, err
	}
	defer reader.Close()
	for _, file := range reader.File {
		if file.Name == "backup.json" {
			stream, err := file.Open()
			if err != nil {
				return manifest, err
			}
			defer stream.Close()
			return manifest, json.NewDecoder(stream).Decode(&manifest)
		}
	}
	return manifest, fmt.Errorf("缺少 backup.json")
}

func validateBackupArchive(path string) error {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return bridgeErr("BACKUP_INVALID", "备份包无法读取")
	}
	defer reader.Close()
	hasManifest := false
	for _, file := range reader.File {
		name := strings.ReplaceAll(file.Name, "\\", "/")
		if name == "backup.json" {
			hasManifest = true
		}
		if !isAllowedBackupEntry(name) {
			return bridgeErr("BACKUP_INVALID", "备份包含非法路径条目："+file.Name)
		}
	}
	if !hasManifest {
		return bridgeErr("BACKUP_INVALID", "不是 QTools 备份包（缺少 backup.json 清单）")
	}
	return nil
}

func isAllowedBackupEntry(name string) bool {
	name = strings.ReplaceAll(name, "\\", "/")
	if name == "" || strings.HasPrefix(name, "/") || strings.Contains(name, ":") {
		return false
	}
	for _, segment := range strings.Split(name, "/") {
		if segment == "" || segment == "." || segment == ".." {
			return false
		}
	}
	return name == "backup.json" || name == "data/config.json" ||
		strings.HasPrefix(name, "tools/") || strings.HasPrefix(name, "apps/") ||
		strings.HasPrefix(name, "data/storage/")
}

func extractBackupArchive(path, staging string) error {
	reader, err := zip.OpenReader(path)
	if err != nil {
		return bridgeErr("BACKUP_INVALID", "备份包无法读取")
	}
	defer reader.Close()
	for _, file := range reader.File {
		name := strings.ReplaceAll(file.Name, "\\", "/")
		if name == "backup.json" || strings.HasSuffix(name, "/") {
			continue
		}
		destination := filepath.Join(staging, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(destination), 0o755); err != nil {
			return bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
		}
		stream, err := file.Open()
		if err != nil {
			return bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
		}
		output, err := os.Create(destination)
		if err != nil {
			stream.Close()
			return bridgeErr("BACKUP_RESTORE_FAILED", err.Error())
		}
		_, copyErr := io.Copy(output, io.LimitReader(stream, maxBackupBytes))
		output.Close()
		stream.Close()
		if copyErr != nil {
			return bridgeErr("BACKUP_RESTORE_FAILED", copyErr.Error())
		}
	}
	return nil
}

func copyFile(source, target string) error {
	input, err := os.Open(source)
	if err != nil {
		return err
	}
	defer input.Close()
	output, err := os.Create(target)
	if err != nil {
		return err
	}
	defer output.Close()
	_, err = io.Copy(output, input)
	return err
}

func mustFileInfo(path string) os.FileInfo {
	info, _ := os.Stat(path)
	if info == nil {
		return zeroFileInfo{}
	}
	return info
}

type zeroFileInfo struct{}

func (zeroFileInfo) Name() string       { return "" }
func (zeroFileInfo) Size() int64        { return 0 }
func (zeroFileInfo) Mode() os.FileMode  { return 0o644 }
func (zeroFileInfo) ModTime() time.Time { return time.Now() }
func (zeroFileInfo) IsDir() bool        { return false }
func (zeroFileInfo) Sys() any           { return nil }
