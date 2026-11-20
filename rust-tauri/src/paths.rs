use std::fs;
use std::path::PathBuf;

/// 运行期路径唯一真源：与 .NET 版保持同样的数据目录布局，
/// 便于两个版本共用 `%APPDATA%\QTools` 下的工具、存储与配置。
#[derive(Clone)]
pub struct AppPaths {
    pub root: PathBuf,
}

impl AppPaths {
    pub fn initialize(explicit_root: Option<&str>) -> Self {
        let root = match explicit_root {
            Some(value) if !value.trim().is_empty() => {
                fs::canonicalize(value).unwrap_or_else(|_| PathBuf::from(value))
            }
            _ => recommended_root(),
        };
        let paths = Self { root };
        for directory in [
            paths.tools_dir(),
            paths.apps_dir(),
            paths.web_tools_dir(),
            paths.data_dir(),
            paths.storage_dir(),
            paths.logs_dir(),
        ] {
            let _ = fs::create_dir_all(directory);
        }
        paths
    }

    pub fn tools_dir(&self) -> PathBuf {
        self.root.join("tools")
    }

    pub fn apps_dir(&self) -> PathBuf {
        self.root.join("apps")
    }

    pub fn web_tools_dir(&self) -> PathBuf {
        self.root.join("webtools")
    }

    pub fn data_dir(&self) -> PathBuf {
        self.root.join("data")
    }

    pub fn storage_dir(&self) -> PathBuf {
        self.data_dir().join("storage")
    }

    pub fn logs_dir(&self) -> PathBuf {
        self.data_dir().join("logs")
    }

    pub fn config_file(&self) -> PathBuf {
        self.data_dir().join("config.json")
    }

    pub fn secrets_dir(&self) -> PathBuf {
        self.data_dir().join("secrets")
    }

    pub fn program_index_file(&self) -> PathBuf {
        self.data_dir().join("program-index.json")
    }

    pub fn usage_file(&self) -> PathBuf {
        self.data_dir().join("usage.json")
    }

    pub fn hidden_programs_file(&self) -> PathBuf {
        self.data_dir().join("hidden-programs.json")
    }

    pub fn pinned_items_file(&self) -> PathBuf {
        self.data_dir().join("pinned-items.json")
    }

    pub fn icon_cache_dir(&self) -> PathBuf {
        self.data_dir().join("icon-cache")
    }

    pub fn log_file(&self) -> PathBuf {
        self.logs_dir().join("qtools.log")
    }
}

fn recommended_root() -> PathBuf {
    if cfg!(any(target_os = "windows", target_os = "macos")) {
        let app_data = dirs::data_dir().or_else(dirs::home_dir).unwrap_or_default();
        app_data.join("QTools")
    } else {
        let base = std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .or_else(dirs::data_local_dir)
            .or_else(dirs::home_dir)
            .unwrap_or_default();
        base.join("QTools")
    }
}
