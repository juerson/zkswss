use bytes::{Bytes, BytesMut};
use futures_util::{stream::SplitSink, stream::SplitStream, stream::StreamExt, SinkExt};
use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicU32, Ordering},
        Arc, Mutex as StdMutex, RwLock as StdRwLock,
    },
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpStream,
    sync::{mpsc, oneshot},
    time::timeout,
};
use tokio_native_tls::TlsConnector as TokioTlsConnector;
use tokio_tungstenite::{
    client_async,
    tungstenite::{client::IntoClientRequest, Message},
    WebSocketStream,
};
use tracing::{debug, error, info};
use zks_proto::{StreamId, ZksMessage};

// -----------------------------------------------------------------------------
// 类型：
// -----------------------------------------------------------------------------
pub type AnyError = Box<dyn std::error::Error + Send + Sync>;
pub type Result<T, E = AnyError> = std::result::Result<T, E>;

type TlsStream = tokio_native_tls::TlsStream<TcpStream>;
type WsSink = SplitSink<WebSocketStream<TlsStream>, Message>;
type WsSource = SplitStream<WebSocketStream<TlsStream>>;

type StreamMap = Arc<StdMutex<HashMap<StreamId, StreamState>>>;
type PendingMap = Arc<StdMutex<HashMap<StreamId, oneshot::Sender<Result<(), String>>>>>;
type HttpResponseMap = Arc<StdMutex<HashMap<StreamId, mpsc::Sender<ZksMessage>>>>;

struct StreamState {
    tx: mpsc::Sender<Bytes>,
}

// -----------------------------------------------------------------------------
// 认证模块：
// -----------------------------------------------------------------------------
mod auth_bridge {
    use std::collections::hash_map::DefaultHasher;
    use std::hash::{Hash, Hasher};
    use std::time::{SystemTime, UNIX_EPOCH};

    pub fn generate_nonce() -> String {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        format!("{:016x}", now)
    }

    pub fn generate_token(secret: &str) -> String {
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        let nonce = generate_nonce();
        let signature = compute_hmac(secret, timestamp, &nonce);
        format!("{}:{}:{}", timestamp, nonce, signature)
    }

    fn compute_hmac(secret: &str, timestamp: u64, nonce: &str) -> String {
        let combined = format!("{}:{}:{}", timestamp, nonce, secret);
        let mut h1 = DefaultHasher::new();
        secret.hash(&mut h1);
        let key_hash = h1.finish();
        let mut h2 = DefaultHasher::new();
        combined.hash(&mut h2);
        key_hash.hash(&mut h2);
        format!("{:016x}{:016x}", key_hash, h2.finish())
    }
}

// -----------------------------------------------------------------------------
// 辅助函数
// -----------------------------------------------------------------------------
fn is_transient(e: &(dyn std::error::Error + 'static)) -> bool {
    let s = e.to_string();
    s.contains("closed")
        || s.contains("Connection")
        || s.contains("timeout")
        || s.contains("channel closed")
        || s.contains("reset by peer")
        || s.contains("broken pipe")
        || s.contains("unexpected EOF")
}

async fn with_retry<F, Fut, T>(max_retries: u32, mut f: F) -> Result<T>
where
    F: FnMut() -> Fut,
    Fut: std::future::Future<Output = Result<T>>,
{
    let mut last_error: Option<AnyError> = None;
    for attempt in 1..=max_retries {
        match f().await {
            Ok(v) => return Ok(v),
            Err(e) if is_transient(e.as_ref()) && attempt < max_retries => {
                let delay = Duration::from_millis(1000 * (1 << (attempt - 1)));
                info!(
                    "Attempt {} failed (transient), retrying in {:?}...",
                    attempt, delay
                );
                tokio::time::sleep(delay).await;
                last_error = Some(e);
            }
            Err(e) => return Err(e),
        }
    }
    Err(last_error.unwrap_or_else(|| "All retry attempts failed".into()))
}

fn make_native_tls_connector() -> Result<TokioTlsConnector> {
    use native_tls::TlsConnector as NativeTlsConnector;
    let connector = NativeTlsConnector::new()?;
    Ok(TokioTlsConnector::from(connector))
}

async fn build_ws_connection(url: &str, cf_ip: Option<&str>) -> Result<(WsSink, WsSource)> {
    let (host, port, path_query) = super::parse_ws_url(url).unwrap();

    let resolved_ip = cf_ip.and_then(super::resolve_cf_ip);
    let connect_addr = format!("{}:{}", resolved_ip.as_deref().unwrap_or(&host), port);

    if resolved_ip.is_some() {
        info!("Connecting via CF IP: {} -> {}", connect_addr, host);
    }

    let tcp = TcpStream::connect(&connect_addr).await?;
    let tls = make_native_tls_connector()?.connect(&host, tcp).await?;

    let req = format!("wss://{}{}", host, path_query).into_client_request()?;
    let (ws, _) = client_async(req, tls).await?;

    Ok(ws.split())
}

// -----------------------------------------------------------------------------
// IO 任务管理
// -----------------------------------------------------------------------------
fn spawn_io_tasks(
    mut write: WsSink,
    mut read: WsSource,
    mut receiver: mpsc::Receiver<ZksMessage>,
    streams: StreamMap,
    pending_connections: PendingMap,
    pending_http: HttpResponseMap,
) -> (tokio::task::AbortHandle, tokio::task::AbortHandle) {
    let writer = tokio::spawn(async move {
        while let Some(msg) = receiver.recv().await {
            let encoded = msg.encode().to_vec();
            if let Err(e) = write.send(Message::Binary(encoded.into())).await {
                error!("WebSocket write error: {}", e);
                break;
            }
        }
        debug!("Writer task exiting");
    });

    let reader = tokio::spawn(async move {
        while let Some(Ok(Message::Binary(data))) = read.next().await {
            let Ok(tunnel_msg) = ZksMessage::decode(&data) else {
                continue;
            };

            match tunnel_msg {
                ZksMessage::Data { stream_id, payload } => {
                    let tx = streams
                        .lock()
                        .unwrap()
                        .get(&stream_id)
                        .map(|s| s.tx.clone());
                    if let Some(tx) = tx {
                        if tx.send(payload).await.is_err() {
                            debug!("Stream {} receiver dropped", stream_id);
                        }
                    }
                }
                ZksMessage::Close { stream_id } => {
                    streams.lock().unwrap().remove(&stream_id);
                    debug!("Stream {} closed by server", stream_id);
                }
                ZksMessage::ErrorReply {
                    stream_id,
                    code,
                    message,
                } => {
                    error!("Stream {} error: {} (code {})", stream_id, message, code);
                    if let Some(tx) = pending_connections.lock().unwrap().remove(&stream_id) {
                        let _ = tx.send(Err(message));
                    }
                    streams.lock().unwrap().remove(&stream_id);
                }
                ZksMessage::ConnectSuccess { stream_id } => {
                    debug!("Stream {} connected successfully", stream_id);
                    if let Some(tx) = pending_connections.lock().unwrap().remove(&stream_id) {
                        let _ = tx.send(Ok(()));
                    }
                }
                ZksMessage::HttpResponse {
                    stream_id, status, ..
                } => {
                    debug!("HTTP response for stream {}: {}", stream_id, status);
                    if let Some(tx) = pending_http.lock().unwrap().remove(&stream_id) {
                        let _ = tx.send(tunnel_msg);
                    }
                }
                _ => {}
            }
        }
        info!("WebSocket reader task closed/exited");
    });

    (writer.abort_handle(), reader.abort_handle())
}

// -----------------------------------------------------------------------------
// 隧道客户端实现
// -----------------------------------------------------------------------------
#[derive(Clone)]
pub struct TunnelClient {
    sender: Arc<StdRwLock<mpsc::Sender<ZksMessage>>>,
    next_stream_id: Arc<AtomicU32>,
    streams: StreamMap,
    pending_connections: PendingMap,
    pending_http_requests: HttpResponseMap,
    auth_token: Option<String>,
    backup_addrs: Vec<zks_proto::BackupAddr>,
    url: String,
    cf_ip: Option<String>,
    io_handles: Arc<StdMutex<(tokio::task::AbortHandle, tokio::task::AbortHandle)>>,
}

impl TunnelClient {
    pub async fn connect_ws(url: &str) -> Result<Self> {
        Self::connect_ws_with_options(url, None, vec![], None).await
    }

    pub async fn connect_ws_with_options(
        url: &str,
        auth_token: Option<String>,
        backup_addrs: Vec<zks_proto::BackupAddr>,
        cf_ip: Option<String>,
    ) -> Result<Self> {
        info!("Connecting to ZKS-Tunnel Worker at {}", url);
        let (write, read) = build_ws_connection(url, cf_ip.as_deref()).await?;
        info!("WebSocket connected");

        let (sender_tx, receiver) = mpsc::channel(256);
        let streams = Arc::new(StdMutex::new(HashMap::new()));
        let pending_connections = Arc::new(StdMutex::new(HashMap::new()));
        let pending_http_requests = Arc::new(StdMutex::new(HashMap::new()));

        let handles = spawn_io_tasks(
            write,
            read,
            receiver,
            streams.clone(),
            pending_connections.clone(),
            pending_http_requests.clone(),
        );

        Ok(Self {
            sender: Arc::new(StdRwLock::new(sender_tx)),
            next_stream_id: Arc::new(AtomicU32::new(1)),
            streams,
            pending_connections,
            pending_http_requests,
            auth_token,
            backup_addrs,
            url: url.to_string(),
            cf_ip,
            io_handles: Arc::new(StdMutex::new(handles)),
        })
    }

    pub async fn is_connected(&self) -> bool {
        !self.get_sender().is_closed()
    }

    fn get_sender(&self) -> mpsc::Sender<ZksMessage> {
        self.sender.read().unwrap().clone()
    }

    async fn ensure_connected(&self) -> Result<()> {
        if self.is_connected().await {
            return Ok(());
        }
        info!("Connection closed, attempting to reconnect...");
        with_retry(3, || self.try_reconnect()).await?;
        info!("Reconnected successfully");
        Ok(())
    }

    async fn try_reconnect(&self) -> Result<()> {
        {
            let lock = self.io_handles.lock().unwrap();
            lock.0.abort();
            lock.1.abort();
        }

        let (write, read) = build_ws_connection(&self.url, self.cf_ip.as_deref()).await?;
        let (new_sender_tx, receiver) = mpsc::channel(256);

        let handles = spawn_io_tasks(
            write,
            read,
            receiver,
            self.streams.clone(),
            self.pending_connections.clone(),
            self.pending_http_requests.clone(),
        );

        *self.sender.write().unwrap() = new_sender_tx;
        *self.io_handles.lock().unwrap() = handles;
        Ok(())
    }

    pub async fn open_stream(
        &self,
        host: &str,
        port: u16,
    ) -> Result<(StreamId, mpsc::Receiver<Bytes>)> {
        self.open_stream_with_options(
            host,
            port,
            self.auth_token.clone(),
            self.backup_addrs.clone(),
        )
        .await
    }

    pub async fn open_stream_with_options(
        &self,
        host: &str,
        port: u16,
        auth_secret: Option<String>,
        backup_addrs: Vec<zks_proto::BackupAddr>,
    ) -> Result<(StreamId, mpsc::Receiver<Bytes>)> {
        with_retry(3, || async {
            self.ensure_connected().await?;
            self.try_open_stream(host, port, auth_secret.clone(), backup_addrs.clone())
                .await
        })
        .await
    }

    async fn try_open_stream(
        &self,
        host: &str,
        port: u16,
        auth_secret: Option<String>,
        backup_addrs: Vec<zks_proto::BackupAddr>,
    ) -> Result<(StreamId, mpsc::Receiver<Bytes>)> {
        let auth = auth_secret
            .or_else(|| self.auth_token.clone())
            .map(|s| auth_bridge::generate_token(&s));

        let backups = if backup_addrs.is_empty() {
            self.backup_addrs.clone()
        } else {
            backup_addrs
        };

        let stream_id = self.get_next_stream_id();
        let (tx, rx) = mpsc::channel(256);

        self.streams
            .lock()
            .unwrap()
            .insert(stream_id, StreamState { tx });

        let (resp_tx, resp_rx) = oneshot::channel();
        self.pending_connections
            .lock()
            .unwrap()
            .insert(stream_id, resp_tx);

        self.get_sender()
            .send(ZksMessage::Connect {
                stream_id,
                host: host.to_string(),
                port,
                auth_token: auth,
                backup_addrs: backups,
            })
            .await?;

        match timeout(Duration::from_secs(10), resp_rx).await {
            Ok(Ok(Ok(()))) => {
                debug!("Opened stream {} to {}:{}", stream_id, host, port);
                Ok((stream_id, rx))
            }
            Ok(Ok(Err(e))) => {
                self.cleanup_stream(stream_id);
                Err(format!("Connection failed: {}", e).into())
            }
            Ok(Err(_)) => {
                self.cleanup_stream(stream_id);
                Err("Connection aborted".into())
            }
            Err(_) => {
                self.cleanup_stream(stream_id);
                Err("Connection timed out".into())
            }
        }
    }

    pub async fn relay(
        &self,
        stream_id: StreamId,
        local: TcpStream,
        mut rx: mpsc::Receiver<Bytes>,
    ) -> Result<()> {
        let (mut read_half, mut write_half) = local.into_split();
        let sender = self.get_sender();
        let sender_for_close = sender.clone();
        let streams = self.streams.clone();

        let local_to_tunnel = async move {
            let mut buf = BytesMut::with_capacity(8192);
            loop {
                match read_half.read_buf(&mut buf).await {
                    Ok(0) => {
                        debug!("Local EOF for stream {}", stream_id);
                        break;
                    }
                    Ok(_) => {
                        let payload = buf.split().freeze();
                        if sender
                            .send(ZksMessage::Data { stream_id, payload })
                            .await
                            .is_err()
                        {
                            debug!("Tunnel sender closed for stream {}", stream_id);
                            break;
                        }
                    }
                    Err(e) => {
                        debug!("Local read error for stream {}: {}", stream_id, e);
                        break;
                    }
                }
            }
        };

        let tunnel_to_local = async move {
            while let Some(data) = rx.recv().await {
                if let Err(e) = write_half.write_all(&data).await {
                    debug!("Local write error for stream {}: {}", stream_id, e);
                    break;
                }
            }
            debug!("Tunnel receiver closed for stream {}", stream_id);
        };

        tokio::select! {
            _ = local_to_tunnel => {}
            _ = tunnel_to_local => {}
        }

        let _ = sender_for_close.send(ZksMessage::Close { stream_id }).await;
        streams.lock().unwrap().remove(&stream_id);
        debug!("Stream {} relay completed", stream_id);
        Ok(())
    }

    pub async fn ping(&self) -> Result<()> {
        self.get_sender().send(ZksMessage::Ping).await?;
        Ok(())
    }

    pub async fn active_stream_count(&self) -> usize {
        self.streams.lock().unwrap().len()
    }

    pub async fn sender(&self) -> mpsc::Sender<ZksMessage> {
        self.get_sender()
    }

    fn cleanup_stream(&self, stream_id: StreamId) {
        self.streams.lock().unwrap().remove(&stream_id);
        self.pending_connections.lock().unwrap().remove(&stream_id);
    }

    pub fn get_next_stream_id(&self) -> StreamId {
        self.next_stream_id.fetch_add(1, Ordering::SeqCst)
    }

    pub fn register_http_request(&self, stream_id: StreamId) -> Result<mpsc::Receiver<ZksMessage>> {
        let (tx, rx) = mpsc::channel(1);
        self.pending_http_requests
            .lock()
            .unwrap()
            .insert(stream_id, tx);
        Ok(rx)
    }

    pub async fn send_message(&self, msg: ZksMessage) -> Result<()> {
        self.get_sender()
            .send(msg)
            .await
            .map_err(|e| format!("Failed to send message: {}", e).into())
    }

    pub async fn send_data(&self, stream_id: StreamId, payload: Bytes) -> Result<()> {
        self.send_message(ZksMessage::Data { stream_id, payload })
            .await
    }

    pub async fn close_stream(&self, stream_id: StreamId) -> Result<()> {
        self.send_message(ZksMessage::Close { stream_id }).await
    }
}
