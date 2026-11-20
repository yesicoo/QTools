use std::fs;
use std::path::Path;

/// 从 Windows Shell 提取程序大图标并编码为 PNG。
/// 失败一律返回 false，由前端回退字母头像；绝不影响启动器可用性。
#[cfg(windows)]
pub fn ensure_icon_at(target: &str, icon_path: &Path) -> bool {
    use windows_sys::Win32::Graphics::Gdi::{BITMAP, DeleteObject, GetDC, GetObjectW, ReleaseDC};
    use windows_sys::Win32::UI::Shell::{SHFILEINFOW, SHGFI_ICON, SHGFI_LARGEICON, SHGetFileInfoW};
    use windows_sys::Win32::UI::WindowsAndMessaging::{DestroyIcon, GetIconInfo, ICONINFO};

    if icon_path.is_file() {
        return true;
    }
    let wide: Vec<u16> = target.encode_utf16().chain(std::iter::once(0)).collect();
    let mut info = SHFILEINFOW::default();
    unsafe {
        let result = SHGetFileInfoW(
            wide.as_ptr(),
            0,
            &mut info,
            std::mem::size_of::<SHFILEINFOW>() as u32,
            SHGFI_ICON | SHGFI_LARGEICON,
        );
        if result == 0 || info.hIcon.is_null() {
            return false;
        }
        let icon = info.hIcon;
        let mut icon_info = ICONINFO::default();
        if GetIconInfo(icon, &mut icon_info) == 0 {
            DestroyIcon(icon);
            return false;
        }
        let color_bitmap = icon_info.hbmColor;
        let mask_bitmap = icon_info.hbmMask;
        let mut success = false;
        if !color_bitmap.is_null() {
            let mut bitmap = BITMAP::default();
            let got = GetObjectW(
                color_bitmap,
                std::mem::size_of::<BITMAP>() as i32,
                &mut bitmap as *mut BITMAP as *mut core::ffi::c_void,
            );
            let width = bitmap.bmWidth;
            let height = bitmap.bmHeight;
            if got != 0 && width > 0 && height > 0 && width <= 256 && height <= 256 {
                let dc = GetDC(std::ptr::null_mut());
                if !dc.is_null() {
                    if let Some(mut pixels) = read_bitmap(dc, color_bitmap, width, height) {
                        let has_alpha = pixels.chunks_exact(4).any(|pixel| pixel[3] != 0);
                        if !has_alpha && !mask_bitmap.is_null() {
                            if let Some(mask) = read_bitmap(dc, mask_bitmap, width, height) {
                                for index in 0..(width * height) as usize {
                                    pixels[index * 4 + 3] =
                                        if mask[index * 4] != 0 { 0 } else { 255 };
                                }
                            }
                        } else if !has_alpha {
                            for pixel in pixels.chunks_exact_mut(4) {
                                pixel[3] = 255;
                            }
                        }
                        for pixel in pixels.chunks_exact_mut(4) {
                            pixel.swap(0, 2);
                        }
                        if let Some(parent) = icon_path.parent() {
                            let _ = fs::create_dir_all(parent);
                        }
                        if let Ok(file) = fs::File::create(icon_path) {
                            let mut encoder = png::Encoder::new(file, width as u32, height as u32);
                            encoder.set_color(png::ColorType::Rgba);
                            encoder.set_depth(png::BitDepth::Eight);
                            if let Ok(mut writer) = encoder.write_header() {
                                success = writer.write_image_data(&pixels).is_ok();
                            }
                        }
                    }
                    ReleaseDC(std::ptr::null_mut(), dc);
                }
            }
        }
        if !mask_bitmap.is_null() {
            DeleteObject(mask_bitmap as *mut core::ffi::c_void);
        }
        if !color_bitmap.is_null() {
            DeleteObject(color_bitmap as *mut core::ffi::c_void);
        }
        DestroyIcon(icon);
        success
    }
}

#[cfg(windows)]
fn read_bitmap(
    dc: *mut core::ffi::c_void,
    bitmap: *mut core::ffi::c_void,
    width: i32,
    height: i32,
) -> Option<Vec<u8>> {
    use windows_sys::Win32::Graphics::Gdi::{
        BITMAPINFO, BITMAPINFOHEADER, DIB_RGB_COLORS, GetDIBits,
    };
    let mut info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: 0,
            ..Default::default()
        },
        ..Default::default()
    };
    let mut pixels = vec![0u8; (width * height * 4) as usize];
    let rows = unsafe {
        GetDIBits(
            dc,
            bitmap,
            0,
            height as u32,
            pixels.as_mut_ptr() as *mut core::ffi::c_void,
            &mut info,
            DIB_RGB_COLORS,
        )
    };
    (rows == height).then_some(pixels)
}

#[cfg(not(windows))]
pub fn ensure_icon_at(_target: &str, _icon_path: &Path) -> bool {
    false
}
