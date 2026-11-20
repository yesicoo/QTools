use std::fs;
use std::path::{Path, PathBuf};

use aes_gcm::aead::{Aead, KeyInit};
use aes_gcm::{Aes256Gcm, Nonce};

use crate::error::{BridgeError, BridgeResult};

const SERVICE: &str = "QTools.AI";
const ACCOUNT: &str = "default";

/// 敏感信息存储：Windows DPAPI 优先，非 Windows 走 AES-GCM 加密文件。
/// 与 .NET 版共享文件布局：`dpapi.QTools.AI.default.bin` / `QTools.AI.default.bin` + `key.bin`。
pub struct SecretStore {
    dir: PathBuf,
}

impl SecretStore {
    pub fn new(dir: PathBuf) -> Self {
        let _ = fs::create_dir_all(&dir);
        Self { dir }
    }

    pub fn mode_name(&self) -> &'static str {
        if cfg!(windows) {
            "Windows DPAPI（当前用户）"
        } else {
            "本地加密文件（AES-GCM）"
        }
    }

    fn dpapi_path(&self) -> PathBuf {
        self.dir.join(format!("dpapi.{SERVICE}.{ACCOUNT}.bin"))
    }

    fn aes_path(&self) -> PathBuf {
        self.dir.join(format!("{SERVICE}.{ACCOUNT}.bin"))
    }

    pub fn is_configured(&self) -> bool {
        self.get().is_some()
    }

    pub fn get(&self) -> Option<String> {
        #[cfg(windows)]
        if let Some(secret) = read_dpapi(&self.dpapi_path()) {
            return Some(secret);
        }
        read_aes(&self.dir, &self.aes_path())
    }

    pub fn set(&self, secret: &str) -> BridgeResult<()> {
        #[cfg(windows)]
        if write_dpapi(&self.dpapi_path(), secret).is_ok() {
            return Ok(());
        }
        write_aes(&self.dir, &self.aes_path(), secret)
    }

    pub fn clear(&self) {
        let _ = fs::remove_file(self.dpapi_path());
        let _ = fs::remove_file(self.aes_path());
    }
}

#[cfg(windows)]
fn read_dpapi(path: &Path) -> Option<String> {
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{CRYPT_INTEGER_BLOB, CryptUnprotectData};
    let protected = fs::read(path).ok()?;
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: protected.len() as u32,
            pbData: protected.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        if CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            &mut output,
        ) == 0
        {
            return None;
        }
        let plain = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        LocalFree(output.pbData as *mut core::ffi::c_void);
        String::from_utf8(plain).ok()
    }
}

#[cfg(windows)]
fn write_dpapi(path: &Path, secret: &str) -> BridgeResult<()> {
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{CRYPT_INTEGER_BLOB, CryptProtectData};
    let plain = secret.as_bytes();
    let protected = unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: plain.len() as u32,
            pbData: plain.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        if CryptProtectData(
            &input,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            0,
            &mut output,
        ) == 0
        {
            return Err(BridgeError::new("SECRET_WRITE_FAILED", "DPAPI 加密失败"));
        }
        let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        LocalFree(output.pbData as *mut core::ffi::c_void);
        bytes
    };
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let temp = path.with_extension("bin.tmp");
    fs::write(&temp, protected).and_then(|_| fs::rename(&temp, path))?;
    Ok(())
}

fn aes_key(dir: &Path) -> BridgeResult<[u8; 32]> {
    let path = dir.join("key.bin");
    if let Ok(bytes) = fs::read(&path) {
        if bytes.len() == 32 {
            let mut key = [0u8; 32];
            key.copy_from_slice(&bytes);
            return Ok(key);
        }
    }
    let mut key = [0u8; 32];
    getrandom::fill(&mut key).map_err(|error| {
        BridgeError::new("SECRET_WRITE_FAILED", format!("生成密钥失败：{error}"))
    })?;
    fs::write(&path, key)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = fs::set_permissions(&path, fs::Permissions::from_mode(0o600));
    }
    Ok(key)
}

fn read_aes(dir: &Path, path: &Path) -> Option<String> {
    let buffer = fs::read(path).ok()?;
    if buffer.len() < 12 + 16 {
        return None;
    }
    let key = fs::read(dir.join("key.bin")).ok()?;
    if key.len() != 32 {
        return None;
    }
    let cipher = Aes256Gcm::new_from_slice(&key).ok()?;
    let nonce = Nonce::from_slice(&buffer[..12]);
    let tag = &buffer[12..28];
    let ciphertext = &buffer[28..];
    let mut sealed = ciphertext.to_vec();
    sealed.extend_from_slice(tag);
    let plain = cipher.decrypt(nonce, sealed.as_ref()).ok()?;
    String::from_utf8(plain).ok()
}

fn write_aes(dir: &Path, path: &Path, secret: &str) -> BridgeResult<()> {
    let key = aes_key(dir)?;
    let cipher = Aes256Gcm::new_from_slice(&key)
        .map_err(|error| BridgeError::new("SECRET_WRITE_FAILED", error.to_string()))?;
    let mut nonce_bytes = [0u8; 12];
    getrandom::fill(&mut nonce_bytes)
        .map_err(|error| BridgeError::new("SECRET_WRITE_FAILED", error.to_string()))?;
    let nonce = Nonce::from_slice(&nonce_bytes);
    let sealed = cipher
        .encrypt(nonce, secret.as_bytes())
        .map_err(|_| BridgeError::new("SECRET_WRITE_FAILED", "加密失败"))?;
    let split = sealed.len().saturating_sub(16);
    let (ciphertext, tag) = sealed.split_at(split);
    let mut file = Vec::with_capacity(12 + 16 + ciphertext.len());
    file.extend_from_slice(&nonce_bytes);
    file.extend_from_slice(tag);
    file.extend_from_slice(ciphertext);
    let temp = path.with_extension("bin.tmp");
    fs::write(&temp, file).and_then(|_| fs::rename(&temp, path))?;
    Ok(())
}
