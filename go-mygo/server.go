package main

import (
	"fmt"
	"io"
	"io/fs"
	"mime"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const (
	hostUIPrefix   = "/__host__"
	toolsPrefix    = "/tools/"
	maxToolFile    = 8 * 1024 * 1024
	hostCSP        = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'self'; base-uri 'none'; object-src 'none'; form-action 'none'"
	defaultToolCSP = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; script-src 'self' 'unsafe-inline' https:; style-src 'self' 'unsafe-inline' https:; img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'"
	allowEvalCSP   = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; style-src 'self' 'unsafe-inline' https:; img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'"
)

type localServer struct {
	listener net.Listener
	server   *http.Server
	state    *AppState
}

func startLocalServer(state *AppState, fixedPort int) (*localServer, error) {
	address := fmt.Sprintf("127.0.0.1:%d", fixedPort)
	listener, err := net.Listen("tcp", address)
	if err != nil {
		return nil, err
	}
	port := listener.Addr().(*net.TCPAddr).Port
	state.SetPort(port)
	semaphore := make(chan struct{}, 64)
	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case semaphore <- struct{}{}:
			defer func() { <-semaphore }()
		default:
			http.Error(w, "Too Many Requests", http.StatusTooManyRequests)
			return
		}
		state.handleHTTP(w, r)
	})
	server := &http.Server{
		Handler: handler, ReadHeaderTimeout: 15 * time.Second,
		WriteTimeout: 15 * time.Second, IdleTimeout: 15 * time.Second,
		MaxHeaderBytes: 16 * 1024,
	}
	local := &localServer{listener: listener, server: server, state: state}
	go func() {
		_ = server.Serve(listener)
	}()
	logInfo("本地服务已启动：http://127.0.0.1:%d", port)
	return local, nil
}

func (s *AppState) handleHTTP(w http.ResponseWriter, r *http.Request) {
	start := time.Now()
	status := 200
	defer func() { logInfo("%s %s -> %d (%dms)", r.Method, r.URL.Path, status, time.Since(start).Milliseconds()) }()

	port := s.Port()
	host := strings.ToLower(r.Host)
	if host != fmt.Sprintf("127.0.0.1:%d", port) && host != fmt.Sprintf("localhost:%d", port) {
		status = http.StatusForbidden
		http.Error(w, "Forbidden: invalid Host header", status)
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		status = http.StatusMethodNotAllowed
		w.Header().Set("Allow", "GET, HEAD")
		http.Error(w, "Method Not Allowed", status)
		return
	}
	if strings.ContainsAny(r.URL.Path, "\\\x00") {
		status = http.StatusBadRequest
		http.Error(w, "Bad Request", status)
		return
	}
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Security-Policy", s.cspFor(r.URL.Path))
	status = s.route(w, r)
}

func (s *AppState) route(w http.ResponseWriter, r *http.Request) int {
	requestPath := r.URL.Path
	if requestPath == "/health" {
		return writeResponse(w, r, 200, "application/json; charset=utf-8",
			[]byte(fmt.Sprintf(`{"status":"ok","version":"%s","port":%d}`, appVersion, s.Port())))
	}
	if requestPath == hostUIPrefix+"/go-shim.js" {
		return writeResponse(w, r, 200, "text/javascript; charset=utf-8", []byte(goShimJS))
	}
	if requestPath == hostUIPrefix || strings.HasPrefix(requestPath, hostUIPrefix+"/") {
		return s.serveHostAsset(w, r, requestPath)
	}
	if strings.HasPrefix(requestPath, toolsPrefix) {
		return s.serveToolAsset(w, r, requestPath)
	}
	return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
}

func (s *AppState) serveHostAsset(w http.ResponseWriter, r *http.Request, requestPath string) int {
	relative := strings.TrimPrefix(requestPath, hostUIPrefix+"/")
	if relative == requestPath || relative == "" {
		relative = "index.html"
	}
	for _, segment := range strings.Split(relative, "/") {
		if segment == "" {
			continue
		}
		if strings.HasPrefix(segment, ".") || strings.ContainsRune(segment, 0) {
			return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
		}
	}
	if strings.HasPrefix(relative, "app-icon/") {
		return s.serveAppIcon(w, r, strings.TrimPrefix(relative, "app-icon/"))
	}
	assetPath := "host-ui/" + relative
	servedName := relative
	switch strings.ToLower(relative) {
	case "icon.png":
		assetPath, servedName = "brand/icon.png", "icon.png"
	case "icon.ico":
		assetPath, servedName = "brand/icon.ico", "icon.ico"
	}
	data, err := fs.ReadFile(assetFS(), assetPath)
	if err != nil {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	if isHTML(servedName) {
		data = []byte(s.transformHTML(string(data), "__host__"))
	}
	return writeResponse(w, r, 200, contentTypeFor(servedName), data)
}

func (s *AppState) serveAppIcon(w http.ResponseWriter, r *http.Request, id string) int {
	if id == "" || strings.ContainsAny(id, "/\\") || strings.Contains(id, "..") {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	candidates := []string{
		filepath.Join(s.Paths.AppDir(id), "icon.png"),
		filepath.Join(s.Paths.IconCacheDir(), id+".png"),
	}
	for _, candidate := range candidates {
		if data, err := readFileLimited(candidate, maxToolFile); err == nil {
			return writeResponse(w, r, 200, "image/png", data)
		}
	}
	return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
}

func (s *AppState) serveToolAsset(w http.ResponseWriter, r *http.Request, requestPath string) int {
	relative := strings.TrimPrefix(requestPath, toolsPrefix)
	parts := strings.SplitN(relative, "/", 2)
	toolID := parts[0]
	if !validToolID.MatchString(toolID) {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	if len(parts) < 2 || parts[1] == "" {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	segments := strings.Split(parts[1], "/")
	cleaned := []string{}
	for _, segment := range segments {
		if segment == "" {
			continue
		}
		if segment == "." || segment == ".." || strings.HasPrefix(segment, ".") || strings.ContainsRune(segment, 0) {
			return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
		}
		cleaned = append(cleaned, segment)
	}
	if len(cleaned) == 0 {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	toolDir := s.Paths.ToolDir(toolID)
	full := filepath.Join(append([]string{toolDir}, cleaned...)...)
	info, err := statFile(full)
	if err != nil || info.IsDir() || info.Size() > maxToolFile {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	canonicalTool, err := filepath.EvalSymlinks(toolDir)
	if err != nil {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	canonicalFile, err := filepath.EvalSymlinks(full)
	if err != nil {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	rel, err := filepath.Rel(canonicalTool, canonicalFile)
	if err != nil || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		logWarn("静态文件请求被拒绝：符号链接逃逸")
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	data, err := readFileLimited(full, maxToolFile)
	if err != nil {
		return writeResponse(w, r, 404, "text/plain; charset=utf-8", []byte("Not Found"))
	}
	name := cleaned[len(cleaned)-1]
	if isHTML(name) {
		data = []byte(s.transformHTML(string(data), toolID))
	}
	return writeResponse(w, r, 200, contentTypeFor(name), data)
}

func (s *AppState) transformHTML(html, toolID string) string {
	theme := s.Config.Snapshot().Shell.Theme
	bridge := fmt.Sprintf(
		`<script src="%s/go-shim.js"></script><script src="%s/bridge.js?tool=%s&v=%s&port=%d"></script>`,
		hostUIPrefix, hostUIPrefix, urlQueryEscape(toolID), appVersion, s.Port(),
	)
	transformed := insertAfterHeadOpen(html, bridge)
	if toolID == "__host__" {
		if explicit := explicitTheme(theme); explicit != "" {
			transformed = setHTMLAttribute(transformed, "data-theme", explicit)
		}
		return transformed
	}
	themeAssets := `<link rel="stylesheet" href="` + hostUIPrefix + `/tool-theme.css">` +
		`<script src="` + hostUIPrefix + `/tool-theme.js" defer></script>`
	transformed = insertAfterHeadOpen(transformed, themeAssets)
	themed := containsCI(transformed, "data-theme") ||
		containsCI(transformed, "prefers-color-scheme") || containsCI(transformed, "color-scheme")
	transformed = setHTMLAttribute(transformed, "data-theme", explicitTheme(theme))
	transformed = setHTMLAttribute(transformed, "data-qtools-theme", theme)
	themedValue := "0"
	if themed {
		themedValue = "1"
	}
	transformed = setHTMLAttribute(transformed, "data-qtools-themed", themedValue)
	autoDark := "0"
	if !themed && themeIsDark(theme) {
		autoDark = "1"
	}
	transformed = setHTMLAttribute(transformed, "data-qtools-auto-dark", autoDark)
	controls := `<script src="` + hostUIPrefix + `/window-controls.js"></script>`
	if index := strings.LastIndex(strings.ToLower(transformed), "</body>"); index >= 0 {
		transformed = transformed[:index] + controls + transformed[index:]
	} else {
		transformed = controls + transformed
	}
	return transformed
}

func (s *AppState) cspFor(requestPath string) string {
	if requestPath == hostUIPrefix || strings.HasPrefix(requestPath, hostUIPrefix+"/") {
		return hostCSP
	}
	if strings.HasPrefix(requestPath, toolsPrefix) {
		relative := strings.TrimPrefix(requestPath, toolsPrefix)
		id := strings.SplitN(relative, "/", 2)[0]
		if tool := s.Tools.Find(id); tool != nil {
			if tool.Manifest.CSP != nil && strings.TrimSpace(*tool.Manifest.CSP) != "" {
				return *tool.Manifest.CSP
			}
			if tool.Manifest.AllowEval {
				return allowEvalCSP
			}
		}
	}
	return defaultToolCSP
}

func writeResponse(w http.ResponseWriter, r *http.Request, status int, contentType string, data []byte) int {
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Length", strconv.Itoa(len(data)))
	w.WriteHeader(status)
	if r.Method != http.MethodHead {
		_, _ = w.Write(data)
	}
	return status
}

func isHTML(name string) bool {
	extension := strings.ToLower(filepath.Ext(name))
	return extension == ".html" || extension == ".htm"
}

func contentTypeFor(name string) string {
	extension := strings.ToLower(filepath.Ext(name))
	switch extension {
	case ".html", ".htm":
		return "text/html; charset=utf-8"
	case ".js", ".mjs":
		return "text/javascript; charset=utf-8"
	case ".css":
		return "text/css; charset=utf-8"
	case ".json", ".map":
		return "application/json; charset=utf-8"
	case ".svg":
		return "image/svg+xml"
	case ".png":
		return "image/png"
	case ".jpg", ".jpeg":
		return "image/jpeg"
	case ".gif":
		return "image/gif"
	case ".webp":
		return "image/webp"
	case ".ico":
		return "image/x-icon"
	case ".woff2":
		return "font/woff2"
	case ".woff":
		return "font/woff"
	case ".ttf":
		return "font/ttf"
	case ".txt", ".md":
		return "text/plain; charset=utf-8"
	}
	if value := mime.TypeByExtension(extension); value != "" {
		return value
	}
	return "application/octet-stream"
}

func insertAfterHeadOpen(html, snippet string) string {
	lower := strings.ToLower(html)
	if open := strings.Index(lower, "<head"); open >= 0 {
		if closeOffset := strings.IndexByte(lower[open:], '>'); closeOffset >= 0 {
			index := open + closeOffset + 1
			return html[:index] + snippet + html[index:]
		}
	}
	return snippet + html
}

func setHTMLAttribute(html, name, value string) string {
	lower := strings.ToLower(html)
	open := strings.Index(lower, "<html")
	if open < 0 {
		return html
	}
	closeOffset := strings.IndexByte(lower[open:], '>')
	if closeOffset < 0 {
		return html
	}
	closeIndex := open + closeOffset
	nameLower := strings.ToLower(name)
	index := -1
	scan := open + 5
	for scan < closeIndex {
		hitOffset := strings.Index(lower[scan:closeIndex], nameLower)
		if hitOffset < 0 {
			break
		}
		hit := scan + hitOffset
		beforeOK := hit > open && isHTMLSpace(lower[hit-1])
		after := hit + len(nameLower)
		afterOK := after < closeIndex && (isHTMLSpace(lower[after]) || lower[after] == '=')
		if beforeOK && afterOK {
			index = hit
			break
		}
		scan = hit + len(nameLower)
	}
	if index < 0 {
		if value == "" {
			return html
		}
		return html[:closeIndex] + " " + name + `="` + htmlEscape(value) + `"` + html[closeIndex:]
	}
	if value == "" {
		start := index
		for start > open && isHTMLSpace(lower[start-1]) {
			start--
		}
		end := index + len(nameLower)
		for end < closeIndex && isHTMLSpace(lower[end]) {
			end++
		}
		if end < closeIndex && lower[end] == '=' {
			end++
			for end < closeIndex && isHTMLSpace(lower[end]) {
				end++
			}
			if end < closeIndex && (lower[end] == '"' || lower[end] == '\'') {
				quote := lower[end]
				if valueEnd := strings.IndexByte(lower[end+1:closeIndex], quote); valueEnd >= 0 {
					end += 1 + valueEnd + 1
				}
			} else {
				for end < closeIndex && !isHTMLSpace(lower[end]) {
					end++
				}
			}
		}
		return html[:start] + html[end:]
	}
	equalsOffset := strings.IndexByte(lower[index+len(nameLower):closeIndex], '=')
	if equalsOffset < 0 {
		return html
	}
	equals := index + len(nameLower) + equalsOffset
	quoteIndex := equals + 1
	for quoteIndex < closeIndex && isHTMLSpace(lower[quoteIndex]) {
		quoteIndex++
	}
	if quoteIndex >= closeIndex || (lower[quoteIndex] != '"' && lower[quoteIndex] != '\'') {
		return html
	}
	quote := lower[quoteIndex]
	closingOffset := strings.IndexByte(lower[quoteIndex+1:closeIndex], quote)
	if closingOffset < 0 {
		return html
	}
	closing := quoteIndex + 1 + closingOffset
	return html[:quoteIndex+1] + htmlEscape(value) + html[closing:]
}

func isHTMLSpace(ch byte) bool { return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' }
func containsCI(haystack, needle string) bool {
	return strings.Contains(strings.ToLower(haystack), strings.ToLower(needle))
}
func htmlEscape(value string) string {
	value = strings.ReplaceAll(value, "&", "&amp;")
	value = strings.ReplaceAll(value, `"`, "&quot;")
	value = strings.ReplaceAll(value, "<", "&lt;")
	return strings.ReplaceAll(value, ">", "&gt;")
}
func urlQueryEscape(value string) string { return strings.ReplaceAll(value, " ", "%20") }

func toolURL(port int, toolID, entry string) string {
	return fmt.Sprintf("http://127.0.0.1:%d/tools/%s/%s", port, urlQueryEscape(toolID), strings.TrimPrefix(entry, "/"))
}

func hostURL(port int, page string) string {
	return fmt.Sprintf("http://127.0.0.1:%d%s/%s", port, hostUIPrefix, strings.TrimPrefix(page, "/"))
}

func readFileLimited(path string, limit int64) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || info.Size() > limit {
		return nil, fmt.Errorf("文件过大或不可读")
	}
	return io.ReadAll(io.LimitReader(file, limit))
}

func statFile(path string) (fs.FileInfo, error) { return os.Stat(path) }

const goShimJS = `(function () {
  "use strict";
  var pending = [];
  function hasRuntime() {
    return window.mygo && typeof window.mygo.call === "function";
  }
  function deliver(text) {
    try {
      if (window.__qtoolsReceive) { window.__qtoolsReceive(String(text)); }
    } catch (error) { }
  }
  function sendNow(message) {
    try {
      var channel = null;
      try {
        channel = window.mygo.channel(function (line) { deliver(line); });
      } catch (error) { channel = null; }
      var args = [String(message)];
      if (channel) { args.push(channel); }
      var pendingCall = window.mygo.call.apply(window.mygo, ["Bridge.Call"].concat(args));
      if (pendingCall && typeof pendingCall.then === "function") {
        pendingCall.then(function (final) {
          if (typeof final === "string" && final) { deliver(final); }
        }).catch(function (error) {
          var id = "unknown";
          try { id = JSON.parse(String(message)).id || id; } catch (e) { }
          deliver(JSON.stringify({
            id: id,
            error: { code: "BRIDGE_UNAVAILABLE", message: "桥接调用失败：" + (error && (error.message || error)) }
          }));
        });
      }
    } catch (error) { }
  }
  function flush() {
    while (pending.length && hasRuntime()) { sendNow(pending.shift()); }
    if (pending.length) { setTimeout(flush, 50); }
  }
  window.external = {
    sendMessage: function (message) {
      if (hasRuntime()) { sendNow(message); }
      else { pending.push(message); setTimeout(flush, 50); }
    },
    receiveMessage: function (callback) { window.__qtoolsReceive = callback; }
  };
})();`
