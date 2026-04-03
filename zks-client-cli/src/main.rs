use clap::Parser;
use std::{
    env,
    net::SocketAddr,
    path::{Path, PathBuf},
    sync::Arc,
};
use tokio::net::TcpListener;
use tracing::{error, info, Level};
use tracing_subscriber::{fmt::format::FmtSpan, EnvFilter, FmtSubscriber};
use zks_client_cli::{
    cli::{Args, Mode},
    proxy::{
        http::HttpProxyServer, hybrid::HybridProxyServer, router::ProxyRouter, socks5::Socks5Server,
    },
    tunnel::client::TunnelClient,
};

type BoxError = Box<dyn std::error::Error + Send + Sync>;

/// 初始化日志系统
fn init_tracing(verbose: bool) -> Result<(), BoxError> {
    let level = if verbose { Level::DEBUG } else { Level::INFO };

    let subscriber = FmtSubscriber::builder()
        .with_max_level(level)
        .with_target(false)
        .with_ansi(false)
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(level.as_str())),
        )
        .with_timer(tracing_subscriber::fmt::time::OffsetTime::new(
            time::UtcOffset::from_hms(8, 0, 0)?,
            time::macros::format_description!("[hour]:[minute]:[second]"),
        ))
        .with_span_events(FmtSpan::NONE)
        .finish();

    tracing::subscriber::set_global_default(subscriber)?;
    Ok(())
}

/// 路径解析：相对于可执行文件的路径
fn resolve_path(path: &str) -> PathBuf {
    let path_obj = Path::new(path);
    if path_obj.is_absolute() {
        return path_obj.to_path_buf();
    }

    env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|parent| parent.join(path_obj)))
        .unwrap_or_else(|| path_obj.to_path_buf())
}

/// 解析备份地址(PRXYIP)
fn parse_backup_addrs(addrs: &Option<String>) -> Vec<zks_proto::BackupAddr> {
    addrs
        .as_ref()
        .map(|s| {
            s.split(',')
                .filter_map(|part| {
                    let mut iter = part.trim().splitn(2, ':');
                    let host = iter.next()?;
                    let port = iter.next()?.parse().ok()?;
                    Some(zks_proto::BackupAddr {
                        host: host.to_string(),
                        port,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// 加载路由规则
async fn load_router(path: &Option<String>) -> Option<Arc<ProxyRouter>> {
    let file_path = path.as_ref()?;
    let resolved = resolve_path(file_path);

    info!("📋 Loading routing rules from: {:?}", resolved);

    match ProxyRouter::from_file(resolved.to_string_lossy().as_ref()).await {
        Ok(router) => {
            info!("✅ Rules loaded successfully");
            Some(Arc::new(router))
        }
        Err(e) => {
            error!("❌ Failed to load rules: {}", e);
            None
        }
    }
}

/// 核心代理运行逻辑
async fn run_proxy(args: Args, tunnel: TunnelClient) -> Result<(), BoxError> {
    let addr: SocketAddr = format!("{}:{}", args.bind, args.port).parse()?;
    let listener = TcpListener::bind(addr).await?;
    let router = load_router(&args.rules_file).await;

    match args.mode {
        Mode::Socks5 => {
            info!("🚀 SOCKS5 proxy listening on {}", addr);
            let server = match router {
                Some(r) => Socks5Server::with_router_and_mode(tunnel, r, args.mode),
                None => Socks5Server::with_mode(tunnel, args.mode),
            };
            server.run(listener).await?;
        }
        Mode::Http => {
            info!("🚀 HTTP proxy listening on {}", addr);
            let server = match router {
                Some(r) => HttpProxyServer::with_router_and_mode(tunnel, r, args.mode),
                None => HttpProxyServer::with_mode(tunnel, args.mode),
            };
            server.run(listener).await?;
        }
        Mode::Hybrid => {
            info!("🚀 Hybrid proxy (SOCKS5 + HTTP) listening on {}", addr);
            let server = match router {
                Some(r) => HybridProxyServer::with_router_and_mode(tunnel, r, args.mode),
                None => HybridProxyServer::with_mode(tunnel, args.mode),
            };
            server.run(listener).await?;
        }
    }
    Ok(())
}

fn print_banner(args: &Args) {
    println!(
        r#"
╔══════════════════════════════════════════════════════════════╗
║         ZKS-Worker Client - Proxy Mode                       ║
╠══════════════════════════════════════════════════════════════╣
║  Worker: {:<51} ║
║  Mode:   {:<51} ║
║  Listen: {:<51} ║
╚══════════════════════════════════════════════════════════════╝"#,
        args.worker,
        format!("{:?}", args.mode),
        format!("{}:{}", args.bind, args.port)
    );
}

#[tokio::main]
async fn main() -> Result<(), BoxError> {
    let args = Args::parse();

    init_tracing(args.verbose)?;
    print_banner(&args);

    info!("Connecting to ZKS-Worker: {}...", args.worker);

    let backup_addrs = parse_backup_addrs(&args.backup_addrs);

    // 建立隧道连接
    let tunnel = TunnelClient::connect_ws_with_options(
        &args.worker,
        args.auth_token.clone(),
        backup_addrs,
        args.cf_ip.clone(),
    )
    .await
    .map_err(|e| {
        error!("❌ Failed to connect: {}", e);
        e
    })?;

    info!("✅ Connected to Worker!");

    // 启动代理服务
    run_proxy(args, tunnel).await
}
