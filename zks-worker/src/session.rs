use bytes::Bytes;
use futures::channel::mpsc;
use futures::{FutureExt, StreamExt};
use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::rc::Rc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use wasm_bindgen_futures::spawn_local;
use worker::*;
use worker::{Method, Url};
use zks_proto::{StreamId, ZksMessage};

/// Stream state - holds a sender for write requests to the socket task
struct StreamInfo {
    write_tx: mpsc::Sender<Bytes>,
}

/// Cached DNS response with expiry timestamp (ms since epoch).
struct DnsCacheEntry {
    response: Vec<u8>,
    expires_at: f64,
}

/// How long (ms) to keep a cached DNS response.
/// 30 s is conservative; most A records have TTL >= 60 s in practice.
const DNS_CACHE_TTL_MS: f64 = 30_000.0;

#[durable_object]
pub struct WorkerSession {
    state: State,
    #[allow(dead_code)]
    env: Env,
    /// Shared with `spawn_local` socket tasks — must stay `Rc<RefCell<...>>`.
    active_streams: Rc<RefCell<HashMap<StreamId, StreamInfo>>>,
    /// Only ever accessed from DO handler methods; `Cell` has zero borrow overhead.
    connection_count: Cell<u32>,
    /// Only accessed from DO handler methods; no `Rc` needed.
    nonce_cache: RefCell<HashMap<String, u64>>,
    /// Only accessed from DO handler methods; `Cell<bool>` is cheapest option.
    authenticated: Cell<bool>,
    /// Lazily-parsed backup addresses from the BACKUP_ADDRS env var.
    /// Parsed once on first Connect, reused for every subsequent one.
    backup_addrs_cache: RefCell<Option<Vec<zks_proto::BackupAddr>>>,
    /// DNS-over-HTTPS response cache keyed by the DNS *question* section
    /// (bytes 12+, skipping the 2-byte transaction ID that changes per query).
    dns_cache: RefCell<HashMap<Vec<u8>, DnsCacheEntry>>,
}

impl DurableObject for WorkerSession {
    fn new(state: State, env: Env) -> Self {
        console_log!("[WorkerSession] Initializing new session");
        Self {
            state,
            env,
            // Pre-size to avoid early rehashes for typical stream counts.
            active_streams: Rc::new(RefCell::new(HashMap::with_capacity(8))),
            connection_count: Cell::new(0),
            nonce_cache: RefCell::new(HashMap::with_capacity(16)),
            authenticated: Cell::new(false),
            backup_addrs_cache: RefCell::new(None),
            dns_cache: RefCell::new(HashMap::with_capacity(32)),
        }
    }

    async fn fetch(&self, req: Request) -> Result<Response> {
        let upgrade = req.headers().get("Upgrade")?;

        if upgrade.as_deref() != Some("websocket") {
            return Response::error("Expected WebSocket upgrade", 426);
        }

        let pair = WebSocketPair::new()?;
        let server = pair.server;
        let client = pair.client;

        self.state.accept_web_socket(&server);

        let count = self.connection_count.get() + 1;
        self.connection_count.set(count);
        console_log!("[WorkerSession] Connection #{} established", count);

        Response::from_websocket(client)
    }

    async fn websocket_message(
        &self,
        ws: WebSocket,
        message: WebSocketIncomingMessage,
    ) -> Result<()> {
        match message {
            WebSocketIncomingMessage::Binary(data) => {
                self.handle_binary_message(&ws, &data).await?;
            }
            WebSocketIncomingMessage::String(text) => {
                console_log!(
                    "[WorkerSession] Text message received: {} bytes",
                    text.len()
                );
            }
        }
        Ok(())
    }

    async fn websocket_close(
        &self,
        _ws: WebSocket,
        code: usize,
        reason: String,
        was_clean: bool,
    ) -> Result<()> {
        console_log!(
            "[WorkerSession] Connection closed: code={}, reason={}, clean={}",
            code,
            reason,
            was_clean
        );
        // Dropping all senders signals every socket task to stop cleanly
        self.active_streams.borrow_mut().clear();
        Ok(())
    }
}

impl WorkerSession {
    async fn handle_binary_message(&self, ws: &WebSocket, data: &[u8]) -> Result<()> {
        let msg = match ZksMessage::decode(data) {
            Ok(m) => m,
            Err(e) => {
                console_error!("[WorkerSession] Failed to decode message: {:?}", e);
                return Ok(());
            }
        };

        match msg {
            ZksMessage::Connect {
                stream_id,
                host,
                port,
                auth_token,
                backup_addrs,
            } => {
                let is_authenticated = self.authenticated.get();

                if !is_authenticated {
                    if let Some(ref token) = auth_token {
                        if !self.validate_auth_token(token) {
                            console_warn!(
                                "[WorkerSession] Invalid auth token for stream {}",
                                stream_id
                            );
                            Self::send_error(ws, stream_id, 401, "Unauthorized: invalid token");
                            return Ok(());
                        }
                        self.authenticated.set(true);
                        console_log!("[WorkerSession] First stream authenticated, token validated");
                    } else {
                        console_warn!("[WorkerSession] No auth token provided");
                        Self::send_error(ws, stream_id, 401, "Unauthorized: no token");
                        return Ok(());
                    }
                } else {
                    console_log!(
                        "[WorkerSession] Already authenticated, skipping token validation"
                    );
                }

                let mut final_backup_addrs = backup_addrs;
                if final_backup_addrs.is_empty() {
                    final_backup_addrs = self.get_default_backup_addrs();
                }

                if !Self::is_valid_host(&host) {
                    console_warn!("[WorkerSession] Blocked connection to: {}", host);
                    Self::send_error(ws, stream_id, 403, &format!("Blocked host: {}", host));
                    return Ok(());
                }

                self.handle_connect_with_fallback(ws, stream_id, &host, port, &final_backup_addrs)
                    .await?;
            }
            ZksMessage::Data { stream_id, payload } => {
                self.handle_data(stream_id, &payload).await?;
            }
            ZksMessage::Close { stream_id } => {
                self.active_streams.borrow_mut().remove(&stream_id);
                console_log!("[WorkerSession] Stream {} closed by client", stream_id);
            }
            ZksMessage::DnsQuery { request_id, query } => {
                self.handle_dns(ws, request_id as u16, &query).await;
            }
            ZksMessage::HttpRequest {
                stream_id,
                method,
                url,
                headers,
                body,
            } => {
                self.handle_http_request(ws, stream_id, &method, &url, &headers, &body)
                    .await?;
            }
            _ => {}
        }

        Ok(())
    }

    /// Check whether `host` is a routable public address.
    ///
    /// Avoids heap allocation: all comparisons are byte-level on the raw
    /// `&str` slice, so no `to_lowercase()` String is ever created.
    fn is_valid_host(host: &str) -> bool {
        if host.is_empty() || host.len() > 253 {
            return false;
        }

        // Fast case-insensitive ASCII prefix check without allocation.
        let ascii_starts_with_ci = |s: &str, prefix: &[u8]| -> bool {
            let sb = s.as_bytes();
            sb.len() >= prefix.len()
                && sb[..prefix.len()]
                    .iter()
                    .zip(prefix)
                    .all(|(&a, &p)| a | 0x20 == p)
        };

        // Exact-match blocked names
        if ascii_starts_with_ci(host, b"localhost") || ascii_starts_with_ci(host, b"::1") {
            return false;
        }

        let b = host.as_bytes();

        // 0.x.x.x / loopback-ish
        if b.first() == Some(&b'0') && b.get(1) == Some(&b'.') {
            return false;
        }

        // 10.x.x.x
        if b.starts_with(b"10.") {
            return false;
        }

        // 127.x.x.x
        if b.starts_with(b"127.") {
            return false;
        }

        // 169.254.x.x  (link-local)
        if b.starts_with(b"169.254.") {
            return false;
        }

        // 192.168.x.x
        if b.starts_with(b"192.168.") {
            return false;
        }

        // 172.16.x.x – 172.31.x.x
        // Parse the second octet numerically instead of 16 separate prefix checks.
        if b.starts_with(b"172.") {
            // Find the second dot
            if let Some(dot2) = b[4..].iter().position(|&c| c == b'.') {
                // Parse digits between "172." and the second dot
                let octet2_bytes = &b[4..4 + dot2];
                let octet2: u8 = octet2_bytes
                    .iter()
                    .fold(0u16, |acc, &c| acc * 10 + (c - b'0') as u16)
                    as u8;
                if (16..=31).contains(&octet2) {
                    return false;
                }
            }
        }

        true
    }

    const TOKEN_MAX_AGE_SECS: u64 = 300;

    fn validate_auth_token(&self, token: &str) -> bool {
        let secret = match self.env.var("AUTH_TOKEN") {
            Ok(s) => s.to_string(),
            Err(_) => {
                console_warn!("[WorkerSession] No AUTH_TOKEN set");
                return false;
            }
        };

        // Use splitn(3) + manual next() instead of collect() to avoid Vec allocation.
        let mut parts = token.splitn(3, ':');
        let (ts_str, nonce, signature) = match (parts.next(), parts.next(), parts.next()) {
            (Some(a), Some(b), Some(c)) => (a, b, c),
            _ => {
                console_warn!(
                    "[WorkerSession] Invalid token format - expected timestamp:nonce:signature"
                );
                return false;
            }
        };

        let timestamp = match ts_str.parse::<u64>() {
            Ok(t) => t,
            Err(_) => {
                console_warn!("[WorkerSession] Invalid timestamp");
                return false;
            }
        };

        let current_time = (js_sys::Date::now() / 1000.0) as u64;

        if current_time.saturating_sub(timestamp) > Self::TOKEN_MAX_AGE_SECS {
            console_warn!("[WorkerSession] Token expired");
            return false;
        }

        let expected_sig = Self::compute_hmac(&secret, timestamp, nonce);
        if signature != expected_sig {
            console_warn!("[WorkerSession] Invalid signature");
            return false;
        }

        let mut cache = self.nonce_cache.borrow_mut();
        if cache.contains_key(nonce) {
            console_warn!("[WorkerSession] Replay detected: nonce reused");
            return false;
        }
        cache.insert(nonce.to_string(), timestamp);

        // Only sweep expired entries when the cache is actually large.
        if cache.len() > 1000 {
            cache.retain(|_, v| current_time.saturating_sub(*v) < Self::TOKEN_MAX_AGE_SECS);
        }

        console_log!("[WorkerSession] Authenticated with dynamic token");
        true
    }

    /// Compute the token signature.
    ///
    /// Uses `DefaultHasher::new()` which is deterministic (fixed seed = 0,
    /// independent of `HashMap`'s `RandomState`).  The client must use the
    /// identical algorithm, so **do not change the hash implementation here**
    /// without a matching change on the client side.
    fn compute_hmac(secret: &str, timestamp: u64, nonce: &str) -> String {
        use std::collections::hash_map::DefaultHasher;
        use std::hash::{Hash, Hasher};

        let combined = format!("{}:{}:{}", timestamp, nonce, secret);

        let mut hasher1 = DefaultHasher::new();
        secret.hash(&mut hasher1);
        let key_hash = hasher1.finish();

        let mut hasher2 = DefaultHasher::new();
        combined.hash(&mut hasher2);
        key_hash.hash(&mut hasher2);

        format!("{:016x}{:016x}", key_hash, hasher2.finish())
    }

    /// Return the default backup addresses, parsing the env var at most once
    /// per Durable Object lifetime and caching the result.
    fn get_default_backup_addrs(&self) -> Vec<zks_proto::BackupAddr> {
        // Fast path: already parsed.
        if let Some(ref cached) = *self.backup_addrs_cache.borrow() {
            return cached.clone();
        }

        // Slow path: parse once and store.
        let parsed = match self.env.var("BACKUP_ADDRS") {
            Ok(val) => val
                .to_string()
                .split(',')
                .filter_map(|s| {
                    let s = s.trim();
                    let colon = s.rfind(':')?;
                    let host = s[..colon].to_string();
                    let port: u16 = s[colon + 1..].parse().ok()?;
                    Some(zks_proto::BackupAddr { host, port })
                })
                .collect::<Vec<_>>(),
            Err(_) => vec![],
        };

        let result = parsed.clone();
        *self.backup_addrs_cache.borrow_mut() = Some(parsed);
        result
    }

    #[inline]
    fn send_error(ws: &WebSocket, stream_id: StreamId, code: u16, message: &str) {
        let error_msg = ZksMessage::ErrorReply {
            stream_id,
            code,
            message: message.to_string(),
        };
        let _ = ws.send_with_bytes(error_msg.encode());
    }

    #[allow(dead_code)]
    async fn handle_connect(
        &self,
        ws: &WebSocket,
        stream_id: StreamId,
        host: &str,
        port: u16,
    ) -> Result<()> {
        if self.active_streams.borrow().contains_key(&stream_id) {
            Self::send_error(ws, stream_id, 409, "Stream ID already in use");
            return Ok(());
        }

        match Socket::builder().connect(host, port) {
            Ok(socket) => {
                if let Err(e) = socket.opened().await {
                    Self::send_error(ws, stream_id, 502, &format!("Connection failed: {:?}", e));
                    return Ok(());
                }

                let success_msg = ZksMessage::ConnectSuccess { stream_id };
                if let Err(_e) = ws.send_with_bytes(success_msg.encode()) {
                    return Ok(());
                }

                let (write_tx, write_rx) = mpsc::channel::<Bytes>(256);
                self.active_streams
                    .borrow_mut()
                    .insert(stream_id, StreamInfo { write_tx });

                let ws_clone = ws.clone();
                let active_streams = self.active_streams.clone();
                spawn_local(async move {
                    Self::run_socket_loop(socket, write_rx, ws_clone, stream_id, active_streams)
                        .await;
                });
            }
            Err(e) => {
                Self::send_error(ws, stream_id, 502, &format!("Connection failed: {:?}", e));
            }
        }

        Ok(())
    }

    /// Handle connect with fallback to backup addresses
    async fn handle_connect_with_fallback(
        &self,
        ws: &WebSocket,
        stream_id: StreamId,
        primary_host: &str,
        primary_port: u16,
        backup_addrs: &[zks_proto::BackupAddr],
    ) -> Result<()> {
        #[allow(unused_assignments)]
        let mut last_error: Option<String> = None;

        let primary_result = self
            .try_connect(ws, stream_id, primary_host, primary_port)
            .await;
        if let Err(e) = primary_result {
            console_warn!("[WorkerSession] Primary address failed: {}", e);
            last_error = Some(e.to_string());
        } else {
            return Ok(());
        }

        for (i, addr) in backup_addrs.iter().enumerate() {
            console_log!(
                "[WorkerSession] Trying backup address {}: {}:{}",
                i + 1,
                addr.host,
                addr.port
            );
            match self.try_connect(ws, stream_id, &addr.host, addr.port).await {
                Ok(()) => {
                    console_log!("[WorkerSession] Backup address {} succeeded", i + 1);
                    return Ok(());
                }
                Err(e) => {
                    console_warn!("[WorkerSession] Backup address {} failed: {}", i + 1, e);
                    last_error = Some(e.to_string());
                }
            }
        }

        let error_msg = last_error.unwrap_or_else(|| "Unknown error".to_string());
        Self::send_error(
            ws,
            stream_id,
            502,
            &format!("All connections failed: {}", error_msg),
        );
        Ok(())
    }

    /// Try to connect to a single address
    async fn try_connect(
        &self,
        ws: &WebSocket,
        stream_id: StreamId,
        host: &str,
        port: u16,
    ) -> Result<()> {
        if self.active_streams.borrow().contains_key(&stream_id) {
            Self::send_error(ws, stream_id, 409, "Stream ID already in use");
            return Ok(());
        }

        match Socket::builder().connect(host, port) {
            Ok(socket) => {
                if let Err(e) = socket.opened().await {
                    return Err(format!("Socket open failed: {:?}", e).into());
                }

                let success_msg = ZksMessage::ConnectSuccess { stream_id };
                if let Err(e) = ws.send_with_bytes(success_msg.encode()) {
                    return Err(format!("Failed to send ConnectSuccess: {:?}", e).into());
                }

                let (write_tx, write_rx) = mpsc::channel::<Bytes>(256);
                self.active_streams
                    .borrow_mut()
                    .insert(stream_id, StreamInfo { write_tx });

                let ws_clone = ws.clone();
                let active_streams = self.active_streams.clone();
                spawn_local(async move {
                    Self::run_socket_loop(socket, write_rx, ws_clone, stream_id, active_streams)
                        .await;
                });

                Ok(())
            }
            Err(e) => Err(format!("Connection failed: {:?}", e).into()),
        }
    }

    /// Handle HTTP Request via fetch() API
    async fn handle_http_request(
        &self,
        ws: &WebSocket,
        stream_id: StreamId,
        method: &str,
        url: &str,
        headers: &str,
        body: &Bytes,
    ) -> Result<()> {
        if !Self::validate_url(url) {
            console_error!("[WorkerSession] Invalid URL received: {:?}", url);
            Self::send_error(ws, stream_id, 400, "Invalid URL format");
            return Ok(());
        }

        let mut init = RequestInit::new();
        init.method = match method {
            "GET" => Method::Get,
            "POST" => Method::Post,
            "HEAD" => Method::Head,
            "PUT" => Method::Put,
            "DELETE" => Method::Delete,
            "PATCH" => Method::Patch,
            "OPTIONS" => Method::Options,
            _ => Method::Get,
        };

        // Parse headers — avoid lowercase allocation for the "host" skip check
        // by comparing bytes directly.
        let req_headers = Headers::new();
        for line in headers.lines() {
            if let Some((key, value)) = line.split_once(':') {
                let key = key.trim();
                let value = value.trim();
                if !key.eq_ignore_ascii_case("host") {
                    let _ = req_headers.set(key, value);
                }
            }
        }
        init.headers = req_headers;

        if !body.is_empty() && matches!(method, "POST" | "PUT" | "PATCH") {
            init.body = Some(wasm_bindgen::JsValue::from_str(&String::from_utf8_lossy(
                body,
            )));
        }

        let request = match Request::new_with_init(url, &init) {
            Ok(r) => r,
            Err(e) => {
                Self::send_error(
                    ws,
                    stream_id,
                    500,
                    &format!("Request creation failed: {:?}", e),
                );
                return Ok(());
            }
        };

        match Fetch::Request(request).send().await {
            Ok(mut response) => {
                let status = response.status_code();

                // Pre-allocate a reasonable header buffer
                let mut resp_headers = String::with_capacity(512);
                for (k, v) in response.headers().entries() {
                    resp_headers.push_str(&k);
                    resp_headers.push_str(": ");
                    resp_headers.push_str(&v);
                    resp_headers.push_str("\r\n");
                }

                let body_bytes = match response.bytes().await {
                    Ok(b) => Bytes::from(b),
                    Err(_) => Bytes::new(),
                };

                let resp_msg = ZksMessage::HttpResponse {
                    stream_id,
                    status,
                    headers: resp_headers,
                    body: body_bytes,
                };

                if let Err(e) = ws.send_with_bytes(resp_msg.encode()) {
                    console_error!("[WorkerSession] Failed to send HttpResponse: {:?}", e);
                }
            }
            Err(e) => {
                Self::send_error(ws, stream_id, 502, &format!("Fetch failed: {:?}", e));
            }
        }

        Ok(())
    }

    /// HTTP relay via fetch() — reads raw HTTP from client and re-issues it
    /// (only useful for plain HTTP; TLS is opaque so HTTPS cannot be relayed)
    #[allow(dead_code)]
    async fn handle_http_fetch(
        &self,
        ws: &WebSocket,
        stream_id: StreamId,
        host: &str,
        _is_https: bool,
    ) -> Result<()> {
        let success_msg = ZksMessage::ConnectSuccess { stream_id };
        ws.send_with_bytes(success_msg.encode())?;

        let (write_tx, mut write_rx) = mpsc::channel::<Bytes>(64);
        self.active_streams
            .borrow_mut()
            .insert(stream_id, StreamInfo { write_tx });

        let ws_clone = ws.clone();
        let host_owned = host.to_string();
        let active_streams = self.active_streams.clone();

        spawn_local(async move {
            let mut buffer = Vec::new();
            let request_sent = false;

            while let Some(chunk) = write_rx.next().await {
                if request_sent {
                    continue;
                }

                buffer.extend_from_slice(&chunk);

                let req_str = String::from_utf8_lossy(&buffer);
                let mut lines = req_str.lines();
                let first_line = match lines.next() {
                    Some(l) => l,
                    None => continue,
                };

                let mut parts = first_line.splitn(3, ' ');
                let (method, path) = match (parts.next(), parts.next()) {
                    (Some(m), Some(p)) => (m, p),
                    _ => continue,
                };

                let url = format!("http://{}{}", host_owned, path);

                let mut init = RequestInit::new();
                init.method = match method {
                    "GET" => Method::Get,
                    "POST" => Method::Post,
                    "HEAD" => Method::Head,
                    "PUT" => Method::Put,
                    "DELETE" => Method::Delete,
                    _ => Method::Get,
                };

                let url_parsed = match Url::parse(&url) {
                    Ok(u) => u,
                    Err(e) => {
                        console_error!("[WorkerSession] Invalid URL {}: {:?}", url, e);
                        continue;
                    }
                };

                match Fetch::Url(url_parsed).send().await {
                    Ok(mut response) => {
                        let status_code = response.status_code();
                        let status_text = match status_code {
                            200 => "OK",
                            201 => "Created",
                            204 => "No Content",
                            400 => "Bad Request",
                            401 => "Unauthorized",
                            403 => "Forbidden",
                            404 => "Not Found",
                            500 => "Internal Server Error",
                            502 => "Bad Gateway",
                            503 => "Service Unavailable",
                            _ => "Unknown",
                        };
                        let status_line = format!("HTTP/1.1 {} {}\r\n", status_code, status_text);
                        let mut head = Vec::with_capacity(256);
                        head.extend_from_slice(status_line.as_bytes());

                        for (k, v) in response.headers().entries() {
                            if !k.eq_ignore_ascii_case("transfer-encoding") {
                                head.extend_from_slice(k.as_bytes());
                                head.extend_from_slice(b": ");
                                head.extend_from_slice(v.as_bytes());
                                head.extend_from_slice(b"\r\n");
                            }
                        }
                        head.extend_from_slice(b"\r\n");

                        let msg = ZksMessage::Data {
                            stream_id,
                            payload: Bytes::from(head),
                        };
                        let _ = ws_clone.send_with_bytes(msg.encode());

                        if let Ok(mut stream) = response.stream() {
                            while let Some(chunk_res) = stream.next().await {
                                match chunk_res {
                                    Ok(chunk) => {
                                        let msg = ZksMessage::Data {
                                            stream_id,
                                            payload: Bytes::from(chunk),
                                        };
                                        let _ = ws_clone.send_with_bytes(msg.encode());
                                    }
                                    Err(e) => {
                                        console_error!("[WorkerSession] Body read error: {:?}", e);
                                        break;
                                    }
                                }
                            }
                        }

                        let close_msg = ZksMessage::Close { stream_id };
                        let _ = ws_clone.send_with_bytes(close_msg.encode());
                        active_streams.borrow_mut().remove(&stream_id);
                        break;
                    }
                    Err(e) => {
                        console_error!("[WorkerSession] Fetch failed: {:?}", e);
                        let close_msg = ZksMessage::Close { stream_id };
                        let _ = ws_clone.send_with_bytes(close_msg.encode());
                        active_streams.borrow_mut().remove(&stream_id);
                        break;
                    }
                }
            }
        });

        Ok(())
    }

    /// Socket handler loop — exclusively owns the Socket
    async fn run_socket_loop(
        mut socket: Socket,
        mut write_rx: mpsc::Receiver<Bytes>,
        ws: WebSocket,
        stream_id: StreamId,
        active_streams: Rc<RefCell<HashMap<StreamId, StreamInfo>>>,
    ) {
        // 16 KiB is a good fit: large enough for most TLS records, small enough
        // to stay inside WASM's linear memory without touching the GC heap.
        let mut read_buffer = vec![0u8; 16384];

        loop {
            futures::select! {
                write_data = write_rx.next() => {
                    match write_data {
                        Some(data) => {
                            if let Err(e) = socket.write_all(&data).await {
                                console_error!(
                                    "[WorkerSession] Write error on stream {}: {:?}",
                                    stream_id, e
                                );
                                break;
                            }
                        }
                        None => {
                            console_log!(
                                "[WorkerSession] Write channel closed for stream {}",
                                stream_id
                            );
                            break;
                        }
                    }
                }
                read_result = socket.read(&mut read_buffer).fuse() => {
                    match read_result {
                        Ok(0) => {
                            console_log!("[WorkerSession] Stream {} EOF", stream_id);
                            break;
                        }
                        Ok(n) => {
                            let msg = ZksMessage::Data {
                                stream_id,
                                payload: Bytes::copy_from_slice(&read_buffer[..n]),
                            };
                            if ws.send_with_bytes(msg.encode()).is_err() {
                                console_error!(
                                    "[WorkerSession] Failed to send data to client"
                                );
                                break;
                            }
                        }
                        Err(e) => {
                            console_error!(
                                "[WorkerSession] Read error on stream {}: {:?}",
                                stream_id, e
                            );
                            break;
                        }
                    }
                }
            }
        }

        let close_msg = ZksMessage::Close { stream_id };
        let _ = ws.send_with_bytes(close_msg.encode());
        active_streams.borrow_mut().remove(&stream_id);
        let _ = socket.close().await;

        console_log!(
            "[WorkerSession] Socket loop exiting for stream {}",
            stream_id
        );
    }

    async fn handle_data(&self, stream_id: StreamId, payload: &[u8]) -> Result<()> {
        let write_tx_opt = {
            let streams = self.active_streams.borrow();
            streams.get(&stream_id).map(|info| info.write_tx.clone())
        };

        if let Some(mut write_tx) = write_tx_opt {
            match write_tx.try_send(Bytes::copy_from_slice(payload)) {
                Ok(()) => {}
                Err(e) if e.is_full() => {
                    console_warn!(
                        "[WorkerSession] Dropping packet for stream {} (buffer full, {} bytes dropped)",
                        stream_id,
                        payload.len()
                    );
                }
                Err(e) if e.is_disconnected() => {
                    self.active_streams.borrow_mut().remove(&stream_id);
                    console_error!(
                        "[WorkerSession] Write channel closed for stream {}",
                        stream_id
                    );
                }
                Err(_) => {
                    self.active_streams.borrow_mut().remove(&stream_id);
                }
            }
        } else {
            console_warn!("[WorkerSession] Data for unknown stream {}", stream_id);
        }

        Ok(())
    }

    async fn handle_dns(&self, ws: &WebSocket, query_id: u16, query: &[u8]) {
        console_log!("[WorkerSession] DNS query id={}", query_id);

        match self.resolve_dns_via_doh(query_id, query).await {
            Ok(response) => {
                let msg = ZksMessage::DnsResponse {
                    request_id: query_id as u32,
                    response: Bytes::from(response),
                };
                let _ = ws.send_with_bytes(msg.encode());
            }
            Err(e) => {
                console_error!("[WorkerSession] DNS failed: {:?}", e);
            }
        }
    }

    async fn resolve_dns_via_doh(&self, query_id: u16, query: &[u8]) -> Result<Vec<u8>> {
        // DNS wire format: bytes 0-1 = transaction ID (random per query),
        // bytes 2+ = flags + question section (stable for the same hostname/type).
        // Cache by the question section so repeated lookups hit the cache even
        // though the transaction ID changes every time.
        let now = js_sys::Date::now();
        let cache_key = if query.len() >= 12 {
            &query[12..]
        } else {
            query
        };

        // Check cache first (read-only borrow, dropped before any await).
        {
            let cache = self.dns_cache.borrow();
            if let Some(entry) = cache.get(cache_key) {
                if now < entry.expires_at {
                    // Patch the transaction ID in the cached response to match
                    // the current query so the client can correlate the reply.
                    let mut response = entry.response.clone();
                    if response.len() >= 2 {
                        response[0] = (query_id >> 8) as u8;
                        response[1] = (query_id & 0xFF) as u8;
                    }
                    console_log!("[WorkerSession] DNS cache hit for query_id={}", query_id);
                    return Ok(response);
                }
            }
        }

        // Cache miss — do the actual DoH request.
        let query_b64 = base64_url_encode(query);
        let url = format!("https://cloudflare-dns.com/dns-query?dns={}", query_b64);

        let mut init = RequestInit::new();
        init.method = Method::Get;
        let headers = Headers::new();
        headers.set("Accept", "application/dns-message")?;
        init.headers = headers;

        let request = Request::new_with_init(&url, &init)?;
        let mut response = Fetch::Request(request).send().await?;
        let bytes = response.bytes().await?;

        // Store in cache keyed by question section.
        // Sweep expired entries if the cache is getting large to bound memory use.
        {
            let mut cache = self.dns_cache.borrow_mut();
            if cache.len() >= 256 {
                cache.retain(|_, e| now < e.expires_at);
            }
            cache.insert(
                cache_key.to_vec(),
                DnsCacheEntry {
                    response: bytes.clone(),
                    expires_at: now + DNS_CACHE_TTL_MS,
                },
            );
        }

        Ok(bytes)
    }

    /// Validate that a URL string is safe to pass to fetch().
    /// Checks length and absence of control characters — no heap allocation.
    #[inline]
    fn validate_url(url: &str) -> bool {
        if url.is_empty() || url.len() > 2048 {
            return false;
        }
        // 0x00 is already caught by b < 0x20; no need for a separate check.
        !url.bytes().any(|b| b < 0x20 || b == 0x7F)
    }
}

fn base64_url_encode(data: &[u8]) -> String {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    // Pre-allocate exact output size: ceil(n / 3) * 4, but without padding
    let out_len = (data.len() * 4 + 2) / 3;
    let mut result = String::with_capacity(out_len);
    let mut i = 0;

    while i < data.len() {
        let b0 = data[i] as usize;
        let b1 = if i + 1 < data.len() {
            data[i + 1] as usize
        } else {
            0
        };
        let b2 = if i + 2 < data.len() {
            data[i + 2] as usize
        } else {
            0
        };

        result.push(ALPHABET[b0 >> 2] as char);
        result.push(ALPHABET[((b0 & 0x03) << 4) | (b1 >> 4)] as char);

        if i + 1 < data.len() {
            result.push(ALPHABET[((b1 & 0x0f) << 2) | (b2 >> 6)] as char);
        }

        if i + 2 < data.len() {
            result.push(ALPHABET[b2 & 0x3f] as char);
        }

        i += 3;
    }

    result
}
