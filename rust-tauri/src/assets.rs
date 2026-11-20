use rust_embed::RustEmbed;

/// 直接内嵌 .NET 版的宿主界面、示例工具与品牌资源，两个版本共享同一套前端。
#[derive(RustEmbed)]
#[folder = "../src/QTools.Host/Assets"]
pub struct Assets;
