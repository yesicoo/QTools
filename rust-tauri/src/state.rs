use parking_lot::{Mutex, RwLock};
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU16, Ordering};

use crate::backup::BackupService;
use crate::config::{AppConfig, ConfigStore};
use crate::launcher::{
    AppRegistry, AppStore, HiddenProgramStore, PinnedItemStore, ProgramIndex, UsageStore,
    WebToolRegistry, WebToolStore,
};
use crate::paths::AppPaths;
use crate::secret_store::SecretStore;
use crate::storage::ToolStorage;
use crate::tools::ToolRegistry;

/// 内嵌工具凭证：主窗口 iframe 通过 `?bt=` 携带，分发器据此把身份解析为对应 toolId。
#[derive(Clone)]
pub struct EmbedToken {
    pub tool_id: String,
    pub expires: std::time::Instant,
}

pub struct AppState {
    pub paths: AppPaths,
    pub config: ConfigStore,
    pub backup: BackupService,
    pub tools: Arc<ToolRegistry>,
    pub storage: Arc<ToolStorage>,
    pub secrets: Arc<SecretStore>,
    pub apps: Arc<AppRegistry>,
    pub app_store: Arc<AppStore>,
    pub web_tools: Arc<WebToolRegistry>,
    pub web_store: Arc<WebToolStore>,
    pub usage: Arc<UsageStore>,
    pub hidden: Arc<HiddenProgramStore>,
    pub pinned: Arc<PinnedItemStore>,
    pub program_index: RwLock<ProgramIndex>,
    pub server_port: AtomicU16,
    pub embed_tokens: Mutex<HashMap<String, EmbedToken>>,
    pub pending_cancel: Mutex<HashMap<String, tokio::sync::watch::Sender<bool>>>,
    pub exit_requested: AtomicBool,
    pub tray_menu_pending: AtomicBool,
    pub tray_menu_cursor: Mutex<Option<(f64, f64)>>,
    pub tray_menu_shown_at: Mutex<Option<std::time::Instant>>,
}

impl AppState {
    pub fn new(paths: AppPaths) -> Arc<Self> {
        let tools = Arc::new(ToolRegistry::new(paths.tools_dir()));
        let _ = tools.ensure_samples();
        tools.rescan();
        let apps = Arc::new(AppRegistry::new(paths.apps_dir()));
        apps.rescan();
        let web_tools = Arc::new(WebToolRegistry::new(paths.web_tools_dir()));
        web_tools.rescan();
        let usage = Arc::new(UsageStore::new(paths.usage_file()));
        usage.ensure_seeded(
            tools
                .all()
                .into_iter()
                .map(|tool| tool.manifest.id)
                .chain(apps.all().into_iter().map(|app| app.entry.id))
                .chain(web_tools.all().into_iter().map(|web| web.entry.id)),
        );
        let program_index = ProgramIndex::load(&paths.program_index_file());
        let pinned = Arc::new(PinnedItemStore::new(paths.pinned_items_file()));
        let hidden = Arc::new(HiddenProgramStore::new(paths.hidden_programs_file()));
        let app_store = Arc::new(AppStore::new(apps.clone()));
        let web_store = Arc::new(WebToolStore::new(web_tools.clone()));
        let config = ConfigStore::new(&paths);
        let storage = Arc::new(ToolStorage::new(paths.storage_dir()));
        let secrets = Arc::new(SecretStore::new(paths.secrets_dir()));
        let backup = BackupService::new(paths.root.clone());

        Arc::new(Self {
            config,
            backup,
            tools,
            storage,
            secrets,
            apps,
            app_store,
            web_tools,
            web_store,
            usage,
            hidden,
            pinned,
            program_index: RwLock::new(program_index),
            server_port: AtomicU16::new(0),
            embed_tokens: Mutex::new(HashMap::new()),
            pending_cancel: Mutex::new(HashMap::new()),
            exit_requested: AtomicBool::new(false),
            tray_menu_pending: AtomicBool::new(false),
            tray_menu_cursor: Mutex::new(None),
            tray_menu_shown_at: Mutex::new(None),
            paths,
        })
    }

    pub fn config_snapshot(&self) -> AppConfig {
        self.config.get()
    }

    pub fn set_port(&self, port: u16) {
        self.server_port.store(port, Ordering::SeqCst);
    }

    pub fn port(&self) -> u16 {
        self.server_port.load(Ordering::SeqCst)
    }

    pub fn register_embed_token(&self, tool_id: &str) -> String {
        let token = format!("{:032x}", uuid::Uuid::new_v4().as_u128());
        let mut tokens = self.embed_tokens.lock();
        // 同一工具只保留最新一张凭证，旧凭证立即作废。
        tokens.retain(|_, entry| entry.tool_id != tool_id);
        if tokens.len() >= 64 {
            let now = std::time::Instant::now();
            tokens.retain(|_, entry| entry.expires > now);
            if tokens.len() >= 64 {
                if let Some(oldest) = tokens
                    .iter()
                    .min_by_key(|(_, entry)| entry.expires)
                    .map(|(token, _)| token.clone())
                {
                    tokens.remove(&oldest);
                }
            }
        }
        tokens.insert(
            token.clone(),
            EmbedToken {
                tool_id: tool_id.to_string(),
                // 有效期只是过期兜底；换页 / 关闭 / 删除走 Revoke。
                expires: std::time::Instant::now() + std::time::Duration::from_secs(24 * 60 * 60),
            },
        );
        token
    }

    pub fn resolve_embed_token(&self, token: &str) -> Option<String> {
        if token.len() != 32 {
            return None;
        }
        let mut tokens = self.embed_tokens.lock();
        match tokens.get(token) {
            Some(entry) if entry.expires > std::time::Instant::now() => Some(entry.tool_id.clone()),
            Some(_) => {
                tokens.remove(token);
                None
            }
            None => None,
        }
    }

    pub fn revoke_embed_tokens(&self, tool_id: &str) {
        self.embed_tokens
            .lock()
            .retain(|_, value| value.tool_id != tool_id);
    }
}
