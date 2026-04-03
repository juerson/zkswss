use super::router::{ProxyRouter, RuleAction};
use super::{log_action, parse_host_port, relay_direct, resolve_action};
use crate::cli::Mode;
use crate::tunnel::client::TunnelClient;
use bytes::{Bytes, BytesMut};
use std::sync::Arc;
use tokio::io::{self, AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::{TcpListener, TcpStream};
use tracing::debug;
use zks_proto::ZksMessage;

const RESP_200_CONNECT: &[u8] = b"HTTP/1.1 200 Connection Established\r\n\r\n";
const RESP_400: &[u8] = b"HTTP/1.1 400 Bad Request\r\n\r\n";
const RESP_403: &[u8] = b"HTTP/1.1 403 Forbidden\r\n\r\n";
const RESP_502: &[u8] = b"HTTP/1.1 502 Bad Gateway\r\n\r\n";
const RESP_504: &[u8] = b"HTTP/1.1 504 Gateway Timeout\r\n\r\n";

type BoxError = Box<dyn std::error::Error + Send + Sync>;

pub struct HttpProxyServer {
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
}

impl HttpProxyServer {
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

    pub async fn run(self, listener: TcpListener) -> Result<(), BoxError> {
        let server = Arc::new(self);
        loop {
            let (stream, addr) = listener.accept().await?;
            let s = Arc::clone(&server);
            tokio::spawn(async move {
                if let Err(e) = handle_http_connection(
                    stream,
                    s.tunnel.clone(),
                    s.router.clone(),
                    s.mode.clone(),
                )
                .await
                {
                    debug!("HTTP proxy connection error from {}: {}", addr, e);
                }
            });
        }
    }
}

pub(crate) async fn handle_http_connection(
    stream: TcpStream,
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
) -> Result<(), BoxError> {
    let mut reader = BufReader::new(stream);
    let mut first_line = String::with_capacity(128);

    if reader.read_line(&mut first_line).await? == 0 {
        return Ok(());
    }

    let parts: Vec<&str> = first_line.split_whitespace().collect();
    if parts.len() < 3 {
        let _ = reader.get_mut().write_all(RESP_400).await;
        return Err("Invalid HTTP request line".into());
    }

    let method = parts[0];
    let uri = parts[1];

    if method.eq_ignore_ascii_case("CONNECT") {
        drain_headers(&mut reader).await?;
        handle_connect(uri, reader.into_inner(), tunnel, router, mode).await
    } else {
        let (headers_bytes, host_from_header) = collect_headers_as_bytes(&mut reader).await?;
        handle_http_forward(
            method,
            uri,
            headers_bytes,
            host_from_header,
            reader.into_inner(),
            tunnel,
            router,
            mode,
        )
        .await
    }
}

async fn handle_connect(
    host_port: &str,
    mut stream: TcpStream,
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
) -> Result<(), BoxError> {
    let (host, port) = parse_host_port(host_port, 443)?;
    let action = resolve_action(&router, &host, port, None).await;
    log_action(&mode, &action, &host, port);

    match action {
        RuleAction::Reject => {
            stream.write_all(RESP_403).await?;
        }
        RuleAction::Proxy | RuleAction::Named(_) => match tunnel.open_stream(&host, port).await {
            Ok((id, rx)) => {
                stream.write_all(RESP_200_CONNECT).await?;
                tunnel.relay(id, stream, rx).await?;
            }
            Err(e) => {
                stream.write_all(RESP_502).await?;
                return Err(e.into());
            }
        },
        RuleAction::Direct => {
            if let Ok(target) = TcpStream::connect((host.as_str(), port)).await {
                stream.write_all(RESP_200_CONNECT).await?;
                relay_direct(stream, target).await;
            } else {
                stream.write_all(RESP_502).await?;
            }
        }
    }
    Ok(())
}

async fn handle_http_forward(
    method: &str,
    uri: &str,
    headers: Bytes,
    host_header: Option<String>,
    mut stream: TcpStream,
    tunnel: TunnelClient,
    router: Option<Arc<ProxyRouter>>,
    mode: Mode,
) -> Result<(), BoxError> {
    let (host, port, path) = parse_http_target_compat(uri, host_header)?;
    let action = resolve_action(&router, &host, port, None).await;
    log_action(&mode, &action, &host, port);

    match action {
        RuleAction::Reject => {
            stream.write_all(RESP_403).await?;
        }
        RuleAction::Proxy | RuleAction::Named(_) => {
            forward_via_tunnel(&mut stream, method, uri, &headers, tunnel).await?;
        }
        RuleAction::Direct => {
            forward_directly(&mut stream, method, &path, &headers, &host, port).await?;
        }
    }
    Ok(())
}

async fn forward_via_tunnel(
    stream: &mut TcpStream,
    method: &str,
    uri: &str,
    headers: &Bytes,
    tunnel: TunnelClient,
) -> Result<(), BoxError> {
    let stream_id = tunnel.get_next_stream_id();
    let mut rx = tunnel.register_http_request(stream_id)?;

    tunnel
        .send_message(ZksMessage::HttpRequest {
            stream_id,
            method: method.to_string(),
            url: uri.to_string(),
            headers: std::str::from_utf8(headers).unwrap_or("").to_string(),
            body: Bytes::new(),
        })
        .await?;

    if let Ok(Some(ZksMessage::HttpResponse {
        status,
        headers,
        body,
        ..
    })) = tokio::time::timeout(std::time::Duration::from_secs(30), rx.recv()).await
    {
        let mut resp = BytesMut::new();
        resp.extend_from_slice(format!("HTTP/1.1 {} OK\r\n{}\r\n", status, headers).as_bytes());
        resp.extend_from_slice(&body);
        stream.write_all(&resp).await?;
    } else {
        stream.write_all(RESP_504).await?;
    }
    Ok(())
}

async fn forward_directly(
    client: &mut TcpStream,
    method: &str,
    path: &str,
    headers: &Bytes,
    host: &str,
    port: u16,
) -> Result<(), BoxError> {
    let mut target = TcpStream::connect((host, port)).await?;
    let req = format!(
        "{} {} HTTP/1.1\r\n{}Connection: close\r\n\r\n",
        method,
        path,
        std::str::from_utf8(headers).unwrap_or("")
    );
    target.write_all(req.as_bytes()).await?;
    let _ = io::copy_bidirectional(client, &mut target).await;
    Ok(())
}

async fn collect_headers_as_bytes(
    reader: &mut BufReader<TcpStream>,
) -> Result<(Bytes, Option<String>), BoxError> {
    let mut headers = BytesMut::with_capacity(2048);
    let mut host = None;
    let mut line = String::new();

    loop {
        line.clear();
        let n = reader.read_line(&mut line).await?;
        if n == 0 || line == "\r\n" || line == "\n" {
            break;
        }

        if line.to_ascii_lowercase().starts_with("host:") {
            host = Some(line[5..].trim().to_string());
        }

        headers.extend_from_slice(line.as_bytes());
    }

    Ok((headers.freeze(), host))
}

fn parse_http_target_compat(
    uri: &str,
    host_header: Option<String>,
) -> Result<(String, u16, String), BoxError> {
    if uri.starts_with("http://") {
        let s = &uri[7..];
        let (hp, path) = s.split_once('/').unwrap_or((s, "/"));
        let (h, p) = parse_host_port(hp, 80)?;
        return Ok((h, p, path.to_string()));
    }
    let h_val = host_header.ok_or("No host")?;
    let (h, p) = parse_host_port(&h_val, 80)?;
    Ok((h, p, uri.to_string()))
}

async fn drain_headers(reader: &mut BufReader<TcpStream>) -> Result<(), BoxError> {
    let mut line = String::new();
    loop {
        line.clear();
        if reader.read_line(&mut line).await? == 0 || line == "\r\n" || line == "\n" {
            break;
        }
    }
    Ok(())
}
