package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

const maxProgramEntries = 400

type IndexEntry struct {
	ID       string  `json:"id"`
	Name     string  `json:"name"`
	Target   string  `json:"target"`
	Source   string  `json:"source"`
	Category *string `json:"category,omitempty"`
}

type programIndexFile struct {
	SchemaVersion int          `json:"schemaVersion"`
	IndexedAtUTC  string       `json:"indexedAtUtc"`
	Entries       []IndexEntry `json:"entries"`
}

type ProgramIndexSnapshot struct {
	Entries        []IndexEntry
	LastIndexedUTC *string
	Indexing       bool
}

type ProgramIndex struct {
	mu             sync.RWMutex
	path           string
	entries        []IndexEntry
	lastIndexedUTC *string
	indexing       bool
}

func NewProgramIndex(path string) *ProgramIndex {
	index := &ProgramIndex{path: path}
	if data, err := os.ReadFile(path); err == nil {
		var file programIndexFile
		if json.Unmarshal(data, &file) == nil {
			index.entries = file.Entries
			if file.IndexedAtUTC != "" {
				value := file.IndexedAtUTC
				index.lastIndexedUTC = &value
			}
		}
	}
	return index
}

func (p *ProgramIndex) Snapshot() ProgramIndexSnapshot {
	p.mu.RLock()
	defer p.mu.RUnlock()
	entries := append([]IndexEntry(nil), p.entries...)
	var timestamp *string
	if p.lastIndexedUTC != nil {
		value := *p.lastIndexedUTC
		timestamp = &value
	}
	return ProgramIndexSnapshot{Entries: entries, LastIndexedUTC: timestamp, Indexing: p.indexing}
}

func (p *ProgramIndex) startIndexing() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.indexing {
		return false
	}
	p.indexing = true
	return true
}

func (p *ProgramIndex) finishIndexing(entries []IndexEntry) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.indexing = false
	if len(entries) == 0 {
		return
	}
	timestamp := time.Now().UTC().Format(time.RFC3339)
	p.entries = entries
	p.lastIndexedUTC = &timestamp
	file := programIndexFile{SchemaVersion: 1, IndexedAtUTC: timestamp, Entries: entries}
	if data, err := json.Marshal(file); err == nil {
		_ = os.MkdirAll(filepath.Dir(p.path), 0o755)
		temp := p.path + ".tmp"
		if os.WriteFile(temp, data, 0o644) == nil {
			_ = os.Rename(temp, p.path)
		}
	}
}

type discoveredProgram struct {
	Name     string
	Target   string
	Source   string
	Category *string
}

func scanPrograms() []IndexEntry {
	programs := collectPrograms()
	deduped := dedupePrograms(programs)
	entries := []IndexEntry{}
	seen := map[string]bool{}
	for _, program := range deduped {
		target := strings.TrimSpace(program.Target)
		if strings.TrimSpace(program.Name) == "" || isJunkProgramName(program.Name) {
			continue
		}
		key := strings.ToLower(target)
		if target == "" || seen[key] {
			continue
		}
		seen[key] = true
		entries = append(entries, IndexEntry{
			ID: buildIDFromTarget(target), Name: strings.TrimSpace(program.Name),
			Target: target, Source: program.Source, Category: program.Category,
		})
	}
	sort.Slice(entries, func(i, j int) bool {
		return strings.ToLower(entries[i].Name) < strings.ToLower(entries[j].Name)
	})
	if len(entries) > maxProgramEntries {
		entries = entries[:maxProgramEntries]
	}
	return entries
}

func dedupePrograms(programs []discoveredProgram) []discoveredProgram {
	result := []discoveredProgram{}
	seen := map[string]bool{}
	for _, program := range programs {
		target := strings.TrimSpace(program.Target)
		if target == "" {
			continue
		}
		key := "t:" + strings.ToLower(target)
		if strings.HasSuffix(strings.ToLower(target), ".lnk") {
			if resolved := resolveLnkTarget(target); resolved != "" {
				if !strings.HasSuffix(strings.ToLower(resolved), ".exe") {
					continue
				}
				key = "t:" + strings.ToLower(resolved)
			} else {
				key = "n:" + strings.ToLower(strings.TrimSuffix(filepath.Base(target), filepath.Ext(target)))
			}
		}
		if !seen[key] {
			seen[key] = true
			result = append(result, program)
		}
	}
	return result
}

var junkEnglish = regexp.MustCompile(`(?i)\b(uninstalls?|unins\w*|setups?|installs?|installers?|readme|helps?|manuals?|changelogs?|licen[cs]es?|websites?|homepages?|documentation|quickstart|tutorial|eula|faq)\b`)

func isJunkProgramName(name string) bool {
	for _, word := range []string{
		"卸载", "安装", "帮助", "手册", "说明", "官网", "网站", "更新日志",
		"许可", "协议", "隐私", "反馈", "指南", "入门",
	} {
		if strings.Contains(name, word) {
			return true
		}
	}
	return junkEnglish.MatchString(name)
}

func resolveLnkTarget(path string) string {
	bytes, err := os.ReadFile(path)
	if err != nil || len(bytes) < 76 || !(bytes[0] == 'L' && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) {
		return ""
	}
	flags := readU32(bytes, 20)
	pos := 76
	if flags&0x1 != 0 {
		if pos+2 > len(bytes) {
			return ""
		}
		size := int(readU16(bytes, pos))
		if size == 0 || pos+2+size > len(bytes) {
			return ""
		}
		pos += 2 + size
	}
	if flags&0x2 == 0 || pos+28 > len(bytes) {
		return ""
	}
	linkInfoSize := int(int32(readU32(bytes, pos)))
	linkInfoFlags := readU32(bytes, pos+8)
	localOffset := int(int32(readU32(bytes, pos+16)))
	if linkInfoFlags&0x1 == 0 || linkInfoSize <= 0 || pos+linkInfoSize > len(bytes) ||
		localOffset < 28 || pos+localOffset >= len(bytes) {
		return ""
	}
	start := pos + localOffset
	if start+3 < len(bytes) && bytes[start+1] == 0 && bytes[start+3] == 0 {
		end := start
		for end+1 < len(bytes) && (bytes[end] != 0 || bytes[end+1] != 0) {
			end += 2
		}
		units := make([]uint16, 0, (end-start)/2)
		for i := start; i+1 < end; i += 2 {
			units = append(units, uint16(bytes[i])|uint16(bytes[i+1])<<8)
		}
		return cleanLnkPath(strings.TrimRight(string(utf16Decode(units)), "\x00"))
	}
	stop := start
	for stop < len(bytes) && bytes[stop] != 0 {
		stop++
	}
	return cleanLnkPath(string(bytes[start:stop]))
}

func cleanLnkPath(value string) string {
	value = strings.TrimSpace(value)
	if value == "" {
		return ""
	}
	for _, ch := range value {
		if ch < 0x20 {
			return ""
		}
	}
	return value
}

func readU16(data []byte, offset int) uint16 {
	if offset+2 > len(data) {
		return 0
	}
	return uint16(data[offset]) | uint16(data[offset+1])<<8
}

func readU32(data []byte, offset int) uint32 {
	if offset+4 > len(data) {
		return 0
	}
	return uint32(data[offset]) | uint32(data[offset+1])<<8 |
		uint32(data[offset+2])<<16 | uint32(data[offset+3])<<24
}

func utf16Decode(units []uint16) string {
	runes := make([]rune, 0, len(units))
	for i := 0; i < len(units); i++ {
		value := rune(units[i])
		if value >= 0xD800 && value <= 0xDBFF && i+1 < len(units) {
			next := rune(units[i+1])
			if next >= 0xDC00 && next <= 0xDFFF {
				runes = append(runes, 0x10000+(value-0xD800)*0x400+(next-0xDC00))
				i++
				continue
			}
		}
		runes = append(runes, value)
	}
	return string(runes)
}
