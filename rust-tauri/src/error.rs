use std::fmt;

/// 桥接错误：code 是全大写短横线常量，页面按 code 判断；内部异常不得泄露堆栈。
#[derive(Debug, Clone)]
pub struct BridgeError {
    pub code: String,
    pub message: String,
}

impl BridgeError {
    pub fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }

    pub fn bad_request(message: impl Into<String>) -> Self {
        Self::new("BRIDGE_BAD_REQUEST", message)
    }

    pub fn permission_denied(message: impl Into<String>) -> Self {
        Self::new("PERMISSION_DENIED", message)
    }

    pub fn internal(message: impl Into<String>) -> Self {
        Self::new("INTERNAL_ERROR", message)
    }

    pub fn unknown_method(method: &str) -> Self {
        Self::new("UNKNOWN_METHOD", format!("未知方法：{method}"))
    }
}

impl fmt::Display for BridgeError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for BridgeError {}

impl From<std::io::Error> for BridgeError {
    fn from(value: std::io::Error) -> Self {
        Self::new("IO_ERROR", value.to_string())
    }
}

impl From<serde_json::Error> for BridgeError {
    fn from(value: serde_json::Error) -> Self {
        Self::bad_request(value.to_string())
    }
}

pub type BridgeResult<T> = Result<T, BridgeError>;
