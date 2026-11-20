package main

import (
	"embed"
	"io/fs"
)

//go:embed all:assets
var embeddedAssets embed.FS

// assetFS 返回内嵌的宿主页面、品牌图标与示例工具。
func assetFS() fs.FS {
	sub, err := fs.Sub(embeddedAssets, "assets")
	if err != nil {
		panic(err)
	}
	return sub
}
