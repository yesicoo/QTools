//go:build windows

package main

import "golang.org/x/sys/windows/registry"

func windowsWebViewRuntimeVersion() string {
	const clientID = `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}`
	for _, item := range []struct {
		root registry.Key
		path string
	}{
		{registry.CURRENT_USER, `Software\Microsoft\EdgeUpdate\Clients\` + clientID},
		{registry.LOCAL_MACHINE, `SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\` + clientID},
		{registry.LOCAL_MACHINE, `SOFTWARE\Microsoft\EdgeUpdate\Clients\` + clientID},
	} {
		key, err := registry.OpenKey(item.root, item.path, registry.QUERY_VALUE)
		if err != nil {
			continue
		}
		value, _, err := key.GetStringValue("pv")
		key.Close()
		if err == nil && value != "" {
			return value
		}
	}
	return ""
}
