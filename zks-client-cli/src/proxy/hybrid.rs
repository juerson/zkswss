use super::http::handle_http_connection;
use super::router::ProxyRouter;
use super::socks5::handle_socks5_connection;
use crate::cli::Mode;
use crate::tunnel::client::TunnelClient;
use std::{sync::Arc, time::Duration};
use tokio::net::{TcpListener, TcpStream};
use tracing::{debug, error};

const SOCKS5_VERSION: u8 = 0x05;
const CONNECT_TIMEOUT_SECS: u64 = 30;

pub struct HybridProxyServer {
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
}

impl HybridProxyServer {
    pub fn new(tunnel: TunnelClient) -> Self {
        Self {
            tunnel,
            router: None,
            mode: Mode::Http,
        }
    }

    pub fn with_mode(tunnel: TunnelClient, mode: Mode) -> Self {
        Self {
            tunnel,
            router: None,
            mode,
        }
    }

    pub fn with_router(tunnel: TunnelClient, router: Arc<ProxyRouter>) -> Self {
        Self {
            tunnel,
            router: Some(router),
            mode: Mode::Http,
        }
    }

    pub fn with_router_and_mode(
        tunnel: TunnelClient,
        router: Arc<ProxyRouter>,
        mode: Mode,
    ) -> Self {
        Self {
            tunnel,
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
            debug!("New hybrid proxy connection from {}", addr);

            let tunnel = self.tunnel.clone();
            let router = self.router.clone();
            let mode = self.mode.clone();

            tokio::spawn(async move {
                match tokio::time::timeout(
                    Duration::from_secs(CONNECT_TIMEOUT_SECS),
                    dispatch(stream, tunnel, router, mode),
                )
                .await
                {
                    Ok(Err(e)) => error!("Hybrid proxy error from {}: {}", addr, e),
                    Err(_) => debug!("Connection timed out from {}", addr),
                    Ok(Ok(())) => {}
                }
            });
        }
    }
}

async fn dispatch(
    stream: TcpStream,
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let mut first_byte = [0u8; 1];
    stream
        .peek(&mut first_byte)
        .await
        .map_err(|_| "Failed to peek first byte")?;

    if first_byte[0] == SOCKS5_VERSION {
        debug!("Detected SOCKS5 protocol");
        handle_socks5_connection(stream, Arc::new(tunnel), router, mode).await
    } else {
        debug!("Detected HTTP protocol");
        handle_http_connection(stream, tunnel, router, mode).await
    }
}
