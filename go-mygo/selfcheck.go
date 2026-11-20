package main

import (
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

type selfTestResult struct {
	passed int
	failed int
}

func runSelfTest(state *AppState, fixedPort int) int {
	server, err := startLocalServer(state, fixedPort)
	if err != nil {
		fmt.Printf("FAIL 无法启动本地服务：%v\n", err)
		return 1
	}
	defer server.server.Close()
	port := state.Port()
	client := &http.Client{Timeout: 5 * time.Second}
	result := &selfTestResult{}
	check := func(name string, ok bool, detail string) {
		if ok {
			result.passed++
			fmt.Printf("PASS %s\n", name)
		} else {
			result.failed++
			fmt.Printf("FAIL %s %s\n", name, detail)
		}
	}
	request := func(method, url string, host string) (*http.Response, string, error) {
		req, err := http.NewRequest(method, url, nil)
		if err != nil {
			return nil, "", err
		}
		if host != "" {
			req.Host = host
		}
		response, err := client.Do(req)
		if err != nil {
			return nil, "", err
		}
		defer response.Body.Close()
		data, _ := io.ReadAll(io.LimitReader(response.Body, 4*1024*1024))
		return response, string(data), nil
	}
	base := fmt.Sprintf("http://127.0.0.1:%d", port)

	response, body, err := request(http.MethodGet, base+"/health", "")
	check("GET /health 返回 200", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))
	check("健康检查包含服务状态", strings.Contains(body, `"status":"ok"`), body)
	check("健康检查版本一致", strings.Contains(body, appVersion), body)

	response, _, err = request(http.MethodGet, base+"/health", "evil.example")
	check("错误 Host 被拒绝", err == nil && response.StatusCode == 403, fmt.Sprintf("%v", err))

	response, body, err = request(http.MethodGet, base+"/__host__/index.html", "")
	check("宿主首页可访问", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))
	check("宿主首页注入桥接", strings.Contains(body, "go-shim.js") && strings.Contains(body, "bridge.js"), "")
	check("宿主 CSP 禁止外部连接", response.Header.Get("Content-Security-Policy") != "" &&
		strings.Contains(response.Header.Get("Content-Security-Policy"), "connect-src 'none'"), "")

	response, body, err = request(http.MethodGet, base+"/__host__/go-shim.js", "")
	check("mygo 桥接 shim 可访问", err == nil && response.StatusCode == 200 && strings.Contains(body, "window.external"), "")

	response, _, err = request(http.MethodGet, base+"/__host__/settings.html", "")
	check("设置页可访问", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))

	response, body, err = request(http.MethodGet, base+"/__host__/icon.png", "")
	check("品牌图标可访问", err == nil && response.StatusCode == 200 && len(body) > 100, "")

	response, _, err = request(http.MethodGet, base+"/__host__/bridge.js", "")
	check("bridge.js 可访问", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))

	response, _, err = request(http.MethodGet, base+"/__host__/app.css", "")
	check("宿主样式可访问", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))

	response, _, err = request(http.MethodPost, base+"/__host__/index.html", "")
	check("POST 方法被拒绝", err == nil && response.StatusCode == 405, fmt.Sprintf("%v", err))

	response, body, err = request(http.MethodHead, base+"/__host__/index.html", "")
	check("HEAD 请求不返回正文", err == nil && response.StatusCode == 200 && body == "", "")

	response, body, err = request(http.MethodHead, base+"/health", "")
	check("HEAD 健康检查不返回正文", err == nil && response.StatusCode == 200 && body == "", "")

	tools := state.Tools.All()
	if len(tools) > 0 {
		tool := tools[0]
		url := fmt.Sprintf("%s/tools/%s/%s", base, tool.Manifest.ID, tool.Entry())
		response, body, err = request(http.MethodGet, url, "")
		check("工具页面可访问", err == nil && response.StatusCode == 200, fmt.Sprintf("%v", err))
		check("工具页面注入桥接", strings.Contains(body, "go-shim.js"), "")
		check("工具页面注入主题", strings.Contains(body, "tool-theme.js"), "")
		check("工具 CSP 允许联网", response.Header.Get("Content-Security-Policy") != "" &&
			strings.Contains(response.Header.Get("Content-Security-Policy"), "https:"), "")

		response, _, err = request(http.MethodGet, fmt.Sprintf("%s/tools/%s/manifest.json", base, tool.Manifest.ID), "")
		check("工具清单静态可访问", err == nil && response.StatusCode == 200, "")

		response, _, err = request(http.MethodGet, fmt.Sprintf("%s/tools/%s/.versions/1.0.0/index.html", base, tool.Manifest.ID), "")
		check("历史版本目录被拒绝", err == nil && response.StatusCode == 404, fmt.Sprintf("%v", err))

		response, _, err = request(http.MethodGet, fmt.Sprintf("%s/tools/%s/../manifest.json", base, tool.Manifest.ID), "")
		check("路径穿越被拒绝", err == nil && response.StatusCode != 200, fmt.Sprintf("%v", err))
	} else {
		check("工具页面可访问", false, "没有示例工具")
	}

	response, _, err = request(http.MethodGet, base+"/tools/json-formatter/", "")
	check("目录列表被拒绝", err == nil && response.StatusCode == 404, fmt.Sprintf("%v", err))

	response, _, err = request(http.MethodGet, base+"/__host__/app-icon/not-exist", "")
	check("不存在的应用图标返回 404", err == nil && response.StatusCode == 404, "")

	response, _, err = request(http.MethodGet, base+"/not-found", "")
	check("未知路径返回 404", err == nil && response.StatusCode == 404, "")

	response, body, err = request(http.MethodGet, base+"/tools/json-formatter/manifest.json", "")
	check("manifest 返回 JSON", err == nil && response.StatusCode == 200 && strings.Contains(body, `"json-formatter"`), "")

	if result.failed == 0 {
		fmt.Printf("\n%d PASS / 0 FAIL\n", result.passed)
		return 0
	}
	fmt.Printf("\n%d PASS / %d FAIL\n", result.passed, result.failed)
	return 1
}
