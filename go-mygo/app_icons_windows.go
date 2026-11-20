//go:build windows

package main

import (
	"image"
	"image/png"
	"os"
	"path/filepath"
	"syscall"
	"unsafe"
)

const (
	shgfiIcon      = 0x100
	shgfiLargeIcon = 0x0
	dibRGBColors   = 0
)

type shFileInfo struct {
	Icon        uintptr
	IconIndex   int32
	Attributes  uint32
	DisplayName [260]uint16
	TypeName    [80]uint16
}

type iconInfo struct {
	IsIcon   int32
	XHotspot uint32
	YHotspot uint32
	Mask     uintptr
	Color    uintptr
}

type bitmapHeader struct {
	Type       int32
	Width      int32
	Height     int32
	WidthBytes int32
	Planes     uint16
	BitCount   uint16
	Bits       uintptr
}

type bitmapInfoHeader struct {
	Size          uint32
	Width         int32
	Height        int32
	Planes        uint16
	BitCount      uint16
	Compression   uint32
	SizeImage     uint32
	XPelsPerMeter int32
	YPelsPerMeter int32
	ClrUsed       uint32
	ClrImportant  uint32
}

type bitmapInfo struct {
	Header bitmapInfoHeader
	Colors [1]uint32
}

var (
	shell32            = syscall.NewLazyDLL("shell32.dll")
	user32Icon         = syscall.NewLazyDLL("user32.dll")
	gdi32Icon          = syscall.NewLazyDLL("gdi32.dll")
	procSHGetFileInfoW = shell32.NewProc("SHGetFileInfoW")
	procDestroyIcon    = user32Icon.NewProc("DestroyIcon")
	procGetIconInfo    = user32Icon.NewProc("GetIconInfo")
	procGetDC          = user32Icon.NewProc("GetDC")
	procReleaseDC      = user32Icon.NewProc("ReleaseDC")
	procGetObjectW     = gdi32Icon.NewProc("GetObjectW")
	procGetDIBits      = gdi32Icon.NewProc("GetDIBits")
	procDeleteObject   = gdi32Icon.NewProc("DeleteObject")
)

func ensureIconAt(target, iconPath string) bool {
	if info, err := os.Stat(iconPath); err == nil && info.Size() > 0 {
		return true
	}
	wide, err := syscall.UTF16PtrFromString(target)
	if err != nil {
		return false
	}
	var info shFileInfo
	result, _, _ := procSHGetFileInfoW.Call(
		uintptr(unsafe.Pointer(wide)), 0, uintptr(unsafe.Pointer(&info)),
		unsafe.Sizeof(info), shgfiIcon|shgfiLargeIcon,
	)
	if result == 0 || info.Icon == 0 {
		return false
	}
	defer procDestroyIcon.Call(info.Icon)

	var icon iconInfo
	if ok, _, _ := procGetIconInfo.Call(info.Icon, uintptr(unsafe.Pointer(&icon))); ok == 0 {
		return false
	}
	if icon.Mask != 0 {
		defer procDeleteObject.Call(icon.Mask)
	}
	if icon.Color != 0 {
		defer procDeleteObject.Call(icon.Color)
	}
	if icon.Color == 0 {
		return false
	}
	var bmp bitmapHeader
	got, _, _ := procGetObjectW.Call(icon.Color, unsafe.Sizeof(bmp), uintptr(unsafe.Pointer(&bmp)))
	if got == 0 || bmp.Width <= 0 || bmp.Height <= 0 || bmp.Width > 256 || bmp.Height > 256 {
		return false
	}
	dc, _, _ := procGetDC.Call(0)
	if dc == 0 {
		return false
	}
	defer procReleaseDC.Call(0, dc)
	pixels := readBitmapPixels(dc, icon.Color, bmp.Width, bmp.Height)
	if pixels == nil {
		return false
	}
	hasAlpha := false
	for i := 3; i < len(pixels); i += 4 {
		if pixels[i] != 0 {
			hasAlpha = true
			break
		}
	}
	if !hasAlpha && icon.Mask != 0 {
		if mask := readBitmapPixels(dc, icon.Mask, bmp.Width, bmp.Height); mask != nil {
			for i := int32(0); i < bmp.Width*bmp.Height; i++ {
				if mask[i*4] != 0 {
					pixels[i*4+3] = 0
				} else {
					pixels[i*4+3] = 255
				}
			}
		}
	}
	for i := 0; i < len(pixels); i += 4 {
		if !hasAlpha {
			pixels[i+3] = 255
		}
		pixels[i], pixels[i+2] = pixels[i+2], pixels[i]
	}
	img := &image.NRGBA{Pix: pixels, Stride: int(bmp.Width) * 4, Rect: image.Rect(0, 0, int(bmp.Width), int(bmp.Height))}
	if err := os.MkdirAll(filepath.Dir(iconPath), 0o755); err != nil {
		return false
	}
	file, err := os.Create(iconPath)
	if err != nil {
		return false
	}
	defer file.Close()
	return png.Encode(file, img) == nil
}

func readBitmapPixels(dc uintptr, bitmap uintptr, width, height int32) []byte {
	header := bitmapInfo{
		Header: bitmapInfoHeader{
			Size: uint32(unsafe.Sizeof(bitmapInfoHeader{})), Width: width, Height: -height,
			Planes: 1, BitCount: 32, Compression: 0,
		},
	}
	pixels := make([]byte, int(width*height*4))
	rows, _, _ := procGetDIBits.Call(
		dc, bitmap, 0, uintptr(height), uintptr(unsafe.Pointer(&pixels[0])),
		uintptr(unsafe.Pointer(&header)), dibRGBColors,
	)
	if int32(rows) != height {
		return nil
	}
	return pixels
}
