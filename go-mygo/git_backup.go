package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

const (
	gitTimeout      = 120 * time.Second
	gitTransferTime = 300 * time.Second
)

func validateGitRemote(remote string) error {
	remote = strings.TrimSpace(remote)
	if remote == "" {
		return bridgeErr("GIT_URL_REQUIRED", "请先填写 Git 仓库地址")
	}
	if len([]rune(remote)) > 500 || strings.HasPrefix(remote, "-") {
		return bridgeErr("GIT_URL_INVALID", "Git 仓库地址不合法（不超过 500 字符，不能以 - 开头或含控制字符）")
	}
	for _, ch := range remote {
		if ch < 0x20 {
			return bridgeErr("GIT_URL_INVALID", "Git 仓库地址不合法")
		}
	}
	return nil
}

type gitRun struct {
	stdout string
	stderr string
	code   int
}

func runGit(ctx context.Context, directory string, timeout time.Duration, args ...string) (gitRun, error) {
	childCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	command := exec.CommandContext(childCtx, "git", args...)
	command.Dir = directory
	command.Env = append(os.Environ(), "GIT_TERMINAL_PROMPT=0")
	var stdout, stderr strings.Builder
	command.Stdout = &stdout
	command.Stderr = &stderr
	err := command.Run()
	result := gitRun{stdout: stdout.String(), stderr: stderr.String(), code: 0}
	if err != nil {
		if exitError, ok := err.(*exec.ExitError); ok {
			result.code = exitError.ExitCode()
		} else if childCtx.Err() == context.DeadlineExceeded || ctx.Err() == context.DeadlineExceeded {
			return result, bridgeErr("GIT_TIMEOUT", "Git 操作超时，请检查网络或仓库地址")
		} else if strings.Contains(strings.ToLower(err.Error()), "executable file not found") {
			return result, bridgeErr("GIT_NOT_FOUND", "未检测到 git 命令：请先安装 Git 并确保其在 PATH 中")
		} else {
			return result, bridgeErr("GIT_COMMAND_FAILED", err.Error())
		}
	}
	if ctx.Err() != nil {
		return result, bridgeErr("REQUEST_CANCELLED", "Git 操作已取消")
	}
	return result, nil
}

func (c *bridgeCall) gitPush(ctx context.Context) (any, error) {
	remote, err := readString(c.payload, "gitRemoteUrl")
	if err != nil {
		return nil, err
	}
	if err := validateGitRemote(remote); err != nil {
		return nil, err
	}
	pushed, committed, message, err := gitPushData(ctx, c.state, strings.TrimSpace(remote), c.stream)
	status := message
	if err != nil {
		status = errorMessage(err)
	}
	c.state.Config.Update(func(cfg *AppConfig) {
		value := strings.TrimSpace(remote)
		cfg.Backup.GitRemoteURL = &value
		if err == nil {
			now := time.Now().UTC().Format(time.RFC3339)
			cfg.Backup.LastPushUTC = &now
		}
		cfg.Backup.LastPushStatus = &status
	})
	if err != nil {
		return nil, err
	}
	return map[string]any{"pushed": pushed, "committed": committed, "message": message}, nil
}

func (c *bridgeCall) gitRestore(ctx context.Context) (any, error) {
	remote, err := readString(c.payload, "gitRemoteUrl")
	if err != nil {
		return nil, err
	}
	if err := validateGitRemote(remote); err != nil {
		return nil, err
	}
	safety, err := gitRestoreData(ctx, c.state, strings.TrimSpace(remote), c.stream)
	if err != nil {
		return nil, err
	}
	c.state.refreshAll()
	return map[string]any{"ok": true, "safetyBackupName": safety}, nil
}

func gitPushData(ctx context.Context, state *AppState, remote string, progress func(string, bool)) (bool, bool, string, error) {
	dataDir := state.Paths.DataDir()
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return false, false, "", bridgeErr("GIT_COMMAND_FAILED", err.Error())
	}
	progress("检查 git 环境…\n", false)
	version, err := runGit(ctx, dataDir, gitTimeout, "--version")
	if err != nil {
		return false, false, "", err
	}
	if version.code != 0 {
		return false, false, "", bridgeErr("GIT_NOT_FOUND", "git 命令执行失败，请确认已安装 Git")
	}
	repo := filepath.Join(dataDir, "backup-repo")
	if err := os.MkdirAll(repo, 0o755); err != nil {
		return false, false, "", bridgeErr("GIT_INIT_FAILED", err.Error())
	}
	if !fileExists(filepath.Join(repo, ".git")) {
		progress("初始化本地备份仓库…\n", false)
		init, err := runGit(ctx, repo, gitTimeout, "init", "-b", "main")
		if err != nil {
			return false, false, "", err
		}
		if init.code != 0 {
			fallback, err := runGit(ctx, repo, gitTimeout, "init")
			if err != nil {
				return false, false, "", err
			}
			if fallback.code != 0 {
				return false, false, "", bridgeErr("GIT_INIT_FAILED", "初始化备份仓库失败："+sanitizeGitOutput(fallback.stderr))
			}
		}
	}
	ensureGitIdentity(ctx, repo)
	if err := ensureGitRemote(ctx, repo, remote); err != nil {
		return false, false, "", err
	}
	progress("同步用户数据到备份仓库…\n", false)
	if err := syncGitMirror(state.Paths.Root, repo); err != nil {
		return false, false, "", err
	}
	if err := checkedGit(ctx, repo, "暂存备份内容", "add", "-A"); err != nil {
		return false, false, "", err
	}
	status, err := runGit(ctx, repo, gitTimeout, "status", "--porcelain")
	if err != nil {
		return false, false, "", err
	}
	if status.code == 0 && strings.TrimSpace(status.stdout) == "" {
		return false, false, "数据没有变化，无需推送", nil
	}
	progress("提交变更…\n", false)
	message := "QTools 备份 " + time.Now().Format("2006-01-02 15:04:05")
	if err := checkedGit(ctx, repo, "提交备份", "commit", "-m", message); err != nil {
		return false, false, "", err
	}
	_, _ = runGit(ctx, repo, gitTimeout, "branch", "-M", "main")
	progress("推送到远程仓库（凭证由 git 自身管理）…\n", false)
	push, err := runGit(ctx, repo, gitTransferTime, "push", "-u", "origin", "HEAD")
	if err != nil {
		return false, false, "", err
	}
	if push.code != 0 {
		return false, false, "", bridgeErr("GIT_PUSH_FAILED",
			fmt.Sprintf("推送到远程仓库失败（退出码 %d）：%s", push.code, sanitizeGitOutput(push.stderr)))
	}
	return true, true, "已提交并推送到远程仓库", nil
}

func gitRestoreData(ctx context.Context, state *AppState, remote string, progress func(string, bool)) (string, error) {
	dataDir := state.Paths.DataDir()
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return "", bridgeErr("GIT_COMMAND_FAILED", err.Error())
	}
	progress("检查 git 环境…\n", false)
	version, err := runGit(ctx, dataDir, gitTimeout, "--version")
	if err != nil {
		return "", err
	}
	if version.code != 0 {
		return "", bridgeErr("GIT_NOT_FOUND", "git 命令执行失败，请确认已安装 Git")
	}
	staging := filepath.Join(dataDir, fmt.Sprintf(".git-restore-%d", time.Now().UnixNano()))
	defer os.RemoveAll(staging)
	progress("读取远程仓库分支…\n", false)
	heads, err := runGit(ctx, dataDir, gitTransferTime, "ls-remote", "--heads", "--", remote)
	if err != nil {
		return "", err
	}
	if heads.code != 0 {
		return "", bridgeErr("GIT_CLONE_FAILED",
			fmt.Sprintf("读取远程仓库分支失败（退出码 %d）：%s", heads.code, sanitizeGitOutput(heads.stderr)))
	}
	branches := []string{}
	for _, line := range strings.Split(heads.stdout, "\n") {
		parts := strings.SplitN(strings.TrimSpace(line), "\t", 2)
		if len(parts) == 2 && strings.HasPrefix(parts[1], "refs/heads/") {
			branches = append(branches, strings.TrimPrefix(parts[1], "refs/heads/"))
		}
	}
	branch := ""
	for _, candidate := range []string{"main", "master"} {
		for _, value := range branches {
			if value == candidate {
				branch = value
				break
			}
		}
		if branch != "" {
			break
		}
	}
	if branch == "" && len(branches) > 0 {
		branch = branches[0]
	}
	if branch == "" {
		return "", bridgeErr("GIT_RESTORE_INVALID", "远程仓库是空的（需先用「推送到 Git」建立备份）")
	}
	progress("克隆远程仓库（浅克隆）…\n", false)
	clone, err := runGit(ctx, dataDir, gitTransferTime, "clone", "--depth", "1", "--branch", branch, "--", remote, staging)
	if err != nil {
		return "", err
	}
	if clone.code != 0 {
		return "", bridgeErr("GIT_CLONE_FAILED",
			fmt.Sprintf("克隆远程仓库失败（退出码 %d）：%s", clone.code, sanitizeGitOutput(clone.stderr)))
	}
	hasPayload := fileExists(filepath.Join(staging, "tools")) || fileExists(filepath.Join(staging, "apps")) ||
		fileExists(filepath.Join(staging, "data", "storage")) || fileExists(filepath.Join(staging, "data", "config.json"))
	if !hasPayload {
		return "", bridgeErr("GIT_RESTORE_INVALID", "远程仓库中没有 QTools 备份数据（需先用「推送到 Git」建立备份）")
	}
	progress("固化快照并恢复（恢复前自动备份当前状态）…\n", false)
	safety, _, _, err := createBackupWithNote(state, "Git 恢复快照")
	if err != nil {
		return "", err
	}
	if err := replaceLiveFromDirectory(state, staging); err != nil {
		return "", err
	}
	return safety, nil
}

func replaceLiveFromDirectory(state *AppState, staging string) error {
	for _, directory := range []string{"tools", "apps", "data/storage"} {
		live := filepath.Join(state.Paths.Root, filepath.FromSlash(directory))
		restored := filepath.Join(staging, filepath.FromSlash(directory))
		_ = os.RemoveAll(live)
		if fileExists(restored) {
			if err := copyDirectory(restored, live); err != nil {
				return bridgeErr("GIT_RESTORE_FAILED", err.Error())
			}
		} else {
			_ = os.MkdirAll(live, 0o755)
		}
	}
	restoredConfig := filepath.Join(staging, "data", "config.json")
	if fileExists(restoredConfig) {
		liveConfig := filepath.Join(state.Paths.Root, "data", "config.json")
		_ = os.MkdirAll(filepath.Dir(liveConfig), 0o755)
		if err := copyFile(restoredConfig, liveConfig); err != nil {
			return bridgeErr("GIT_RESTORE_FAILED", err.Error())
		}
	}
	return nil
}

func syncGitMirror(root, repo string) error {
	entries, err := os.ReadDir(repo)
	if err != nil {
		return bridgeErr("GIT_COMMAND_FAILED", err.Error())
	}
	for _, entry := range entries {
		if entry.Name() == ".git" {
			continue
		}
		_ = os.RemoveAll(filepath.Join(repo, entry.Name()))
	}
	for _, relative := range backupPayloadFiles(root) {
		source := filepath.Join(root, filepath.FromSlash(relative))
		target := filepath.Join(repo, filepath.FromSlash(relative))
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return bridgeErr("GIT_COMMAND_FAILED", err.Error())
		}
		if err := copyFile(source, target); err != nil {
			return bridgeErr("GIT_COMMAND_FAILED", err.Error())
		}
	}
	return nil
}

func ensureGitIdentity(ctx context.Context, repo string) {
	name, _ := runGit(ctx, repo, gitTimeout, "config", "user.name")
	if name.code != 0 || strings.TrimSpace(name.stdout) == "" {
		_, _ = runGit(ctx, repo, gitTimeout, "config", "user.name", "QTools Backup")
	}
	email, _ := runGit(ctx, repo, gitTimeout, "config", "user.email")
	if email.code != 0 || strings.TrimSpace(email.stdout) == "" {
		_, _ = runGit(ctx, repo, gitTimeout, "config", "user.email", "qtools@localhost")
	}
}

func ensureGitRemote(ctx context.Context, repo, remote string) error {
	current, err := runGit(ctx, repo, gitTimeout, "remote", "get-url", "origin")
	if err != nil {
		return err
	}
	if current.code != 0 {
		return checkedGit(ctx, repo, "登记远程仓库", "remote", "add", "origin", remote)
	}
	if strings.TrimSpace(current.stdout) != remote {
		return checkedGit(ctx, repo, "更新远程仓库地址", "remote", "set-url", "origin", remote)
	}
	return nil
}

func checkedGit(ctx context.Context, repo, action string, args ...string) error {
	result, err := runGit(ctx, repo, gitTimeout, args...)
	if err != nil {
		return err
	}
	if result.code != 0 {
		return bridgeErr("GIT_COMMAND_FAILED",
			fmt.Sprintf("%s失败（退出码 %d）：%s", action, result.code, sanitizeGitOutput(result.stderr)))
	}
	return nil
}

func sanitizeGitOutput(value string) string {
	value = strings.TrimSpace(value)
	value = strings.ReplaceAll(value, "\r", " ")
	value = strings.ReplaceAll(value, "\n", " ")
	if len([]rune(value)) > 500 {
		value = string([]rune(value)[:500])
	}
	return value
}

func copyDirectory(source, target string) error {
	return filepath.WalkDir(source, func(path string, entry os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		destination := filepath.Join(target, relative)
		if entry.IsDir() {
			return os.MkdirAll(destination, 0o755)
		}
		if err := os.MkdirAll(filepath.Dir(destination), 0o755); err != nil {
			return err
		}
		input, err := os.Open(path)
		if err != nil {
			return err
		}
		defer input.Close()
		output, err := os.Create(destination)
		if err != nil {
			return err
		}
		_, copyErr := io.Copy(output, input)
		closeErr := output.Close()
		if copyErr != nil {
			return copyErr
		}
		return closeErr
	})
}
