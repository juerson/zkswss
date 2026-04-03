use super::router::{ProxyRouter, RuleAction};
use super::{log_action, relay_direct, resolve_action};
use crate::cli::Mode;
use crate::tunnel::client::TunnelClient;
use std::{net::Ipv6Addr, sync::Arc};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::{TcpListener, TcpStream},
};
use tracing::{debug, error};

const SOCKS_VERSION: u8 = 0x05;
const AUTH_NO_AUTH: u8 = 0x00;
const CMD_CONNECT: u8 = 0x01;
const ATYP_IPV4: u8 = 0x01;
const ATYP_DOMAIN: u8 = 0x03;
const ATYP_IPV6: u8 = 0x04;
const REP_SUCCESS: u8 = 0x00;
const REP_HOST_UNREACHABLE: u8 = 0x04;
const REP_CMD_NOT_SUPPORTED: u8 = 0x07;
const REP_ATYP_NOT_SUPPORTED: u8 = 0x08;

pub struct Socks5Server {
    tunnel: Arc<TunnelClient>,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
}

impl Socks5Server {
    pub fn new(tunnel: TunnelClient) -> Self {
        Self {
            tunnel: Arc::new(tunnel),
            router: None,
            mode: Mode::Socks5,
        }
    }

    pub fn with_mode(tunnel: TunnelClient, mode: Mode) -> Self {
        Self {
            tunnel: Arc::new(tunnel),
            router: None,
            mode,
        }
    }

    pub fn with_router(tunnel: TunnelClient, router: Arc<ProxyRouter>) -> Self {
        Self {
            tunnel: Arc::new(tunnel),
            router: Some(router),
            mode: Mode::Socks5,
        }
    }

    pub fn with_router_and_mode(
        tunnel: TunnelClient,
        router: Arc<ProxyRouter>,
        mode: Mode,
    ) -> Self {
        Self {
            tunnel: Arc::new(tunnel),
            router: Some(router),
            mode,
        }
    }

    pub async fn run(
        &self,
        listener: TcpListener,
    ) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        loop {
            let (stream, addr) = listener.accept().await?;
            debug!("New SOCKS5 connection from {}", addr);

            let tunnel = self.tunnel.clone();
            let router = self.router.clone();
            let mode = self.mode.clone();

            tokio::spawn(async move {
                if let Err(e) = handle_socks5_connection(stream, tunnel, router, mode).await {
                    error!("SOCKS5 error from {}: {}", addr, e);
                }
            });
        }
    }
}

// 将地址解析提取出来
async fn read_addr(
    stream: &mut TcpStream,
) -> Result<(String, u16), Box<dyn std::error::Error + Send + Sync>> {
    let atyp = stream.read_u8().await?;
    match atyp {
        ATYP_IPV4 => {
            let mut ip = [0u8; 4];
            stream.read_exact(&mut ip).await?;
            let port = stream.read_u16().await?;
            Ok((format!("{}.{}.{}.{}", ip[0], ip[1], ip[2], ip[3]), port))
        }
        ATYP_DOMAIN => {
            let len = stream.read_u8().await? as usize;
            let mut domain = vec![0u8; len];
            stream.read_exact(&mut domain).await?;
            let port = stream.read_u16().await?;
            Ok((String::from_utf8_lossy(&domain).to_string(), port))
        }
        ATYP_IPV6 => {
            let mut ip = [0u8; 16];
            stream.read_exact(&mut ip).await?;
            let port = stream.read_u16().await?;
            Ok((format!("[{}]", Ipv6Addr::from(ip)), port))
        }
        _ => Err("Unsupported address type".into()),
    }
}

pub(crate) async fn handle_socks5_connection(
    mut stream: TcpStream,
    tunnel: Arc<TunnelClient>,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    // 握手
    let ver = stream.read_u8().await?;
    if ver != SOCKS_VERSION {
        return Err("Invalid version".into());
    }

    let nmethods = stream.read_u8().await?;
    let mut methods = vec![0u8; nmethods as usize];
    stream.read_exact(&mut methods).await?;

    stream.write_all(&[SOCKS_VERSION, AUTH_NO_AUTH]).await?;

    // 读请求头
    let mut header = [0u8; 3];
    stream.read_exact(&mut header).await?;
    if header[1] != CMD_CONNECT {
        stream
            .write_all(&socks5_reply(REP_CMD_NOT_SUPPORTED))
            .await?;
        return Err("Unsupported command".into());
    }

    // 解析目标地址
    let (host, port) = match read_addr(&mut stream).await {
        Ok(addr) => addr,
        Err(_) => {
            stream
                .write_all(&socks5_reply(REP_ATYP_NOT_SUPPORTED))
                .await?;
            return Err("Invalid address".into());
        }
    };

    // 路由逻辑
    let action = resolve_action(&router, &host, port, None).await;
    log_action(&mode, &action, &host, port);

    // 统一处理执行
    match action {
        RuleAction::Reject => {
            stream
                .write_all(&socks5_reply(REP_HOST_UNREACHABLE))
                .await?;
        }
        RuleAction::Proxy | RuleAction::Named(_) => match tunnel.open_stream(&host, port).await {
            Ok((id, rx)) => {
                stream.write_all(&socks5_reply(REP_SUCCESS)).await?;
                tunnel.relay(id, stream, rx).await?;
            }
            Err(_) => {
                stream
                    .write_all(&socks5_reply(REP_HOST_UNREACHABLE))
                    .await?;
            }
        },
        RuleAction::Direct => {
            let conn = tokio::time::timeout(
                std::time::Duration::from_secs(10),
                TcpStream::connect((host.as_str(), port)),
            )
            .await;

            if let Ok(Ok(target)) = conn {
                stream.write_all(&socks5_reply(REP_SUCCESS)).await?;
                relay_direct(stream, target).await;
            } else {
                stream
                    .write_all(&socks5_reply(REP_HOST_UNREACHABLE))
                    .await?;
            }
        }
    }
    Ok(())
}

fn socks5_reply(rep: u8) -> [u8; 10] {
    [SOCKS_VERSION, rep, 0x00, ATYP_IPV4, 0, 0, 0, 0, 0, 0]
}
