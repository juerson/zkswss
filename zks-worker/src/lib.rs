//! [Client] --WebSocket--> [Worker] --TCP--> [Internet]
use futures::StreamExt;
use worker::*;

mod session;
pub use session::WorkerSession;

/// Entry point: Route requests to appropriate handlers
#[event(fetch)]
async fn fetch(req: Request, env: Env, _ctx: Context) -> Result<Response> {
    console_error_panic_hook::set_once();

    let url = req.url()?;
    let path = url.path();

    // WebSocket session endpoint
    if path.starts_with("/session") {
        return handle_session(req, env).await;
    }

    // Health check
    if path == "/health" || path == "/" {
        return Response::ok(
            serde_json::json!({
                "status": "ok",
                "service": "zks-worker",
                "version": "0.1.0",
                "capabilities": ["tcp", "websocket", "zks"]
            })
            .to_string(),
        );
    }

    // Entropy Tax endpoint
    if path.starts_with("/entropy") {
        return handle_entropy(req).await;
    }

    Response::error("Not Found. Use /session for connection.", 404)
}

/// Handle Entropy Tax requests
async fn handle_entropy(req: Request) -> Result<Response> {
    // 1. GET request: Fetch entropy (for Swarm Entropy)
    if req.method() == Method::Get && !req.headers().has("Upgrade")? {
        let mut entropy = [0u8; 32];
        fill_random(&mut entropy);
        let entropy_hex = hex::encode(&entropy);

        return Response::ok(
            serde_json::json!({
                "entropy": entropy_hex,
                "timestamp": Date::now().to_string()
            })
            .to_string(),
        );
    }

    // 2. WebSocket request: Contribute entropy (Entropy Tax)
    if req.headers().has("Upgrade")? {
        let pair = WebSocketPair::new()?;
        let server = pair.server;
        server.accept()?;

        wasm_bindgen_futures::spawn_local(async move {
            let mut event_stream = server.events().expect("could not open stream");
            while let Some(event) = event_stream.next().await {
                if let worker::WebsocketEvent::Message(msg) =
                    event.expect("received error in websocket")
                {
                    if let Some(text) = msg.text() {
                        let _ = server.send_with_str(format!("ACK: {}", text));
                    }
                }
            }
        });

        return Response::from_websocket(pair.client);
    }

    Response::error("Method not allowed", 405)
}

/// Handle WebSocket session upgrade
async fn handle_session(req: Request, env: Env) -> Result<Response> {
    let namespace = env.durable_object("WORKER_SESSION")?;
    let session_id = generate_session_id();
    let id = namespace.id_from_name(&session_id)?;
    let stub = id.get_stub()?;
    stub.fetch_with_request(req).await
}

/// Fill a buffer with random bytes via Web Crypto (no external crate needed).
/// In Cloudflare Workers, `globalThis.crypto` is always available and is
/// cryptographically strong — backed by the same CSPRNG as the platform.
#[inline]
fn fill_random(buf: &mut [u8]) {
    // Each Math.random() call yields ~53 bits of entropy; we pack 6 bytes per call
    // to stay safely below the 53-bit boundary and avoid f64 precision loss.
    let mut i = 0;
    while i < buf.len() {
        // Shift into integer range [0, 2^48) — 48 bits, 6 bytes, zero precision loss
        let r = (js_sys::Math::random() * 281_474_976_710_656.0_f64) as u64; // 2^48
        let bytes = r.to_le_bytes(); // 8 bytes, top 2 are always 0
        let take = (buf.len() - i).min(6);
        buf[i..i + take].copy_from_slice(&bytes[..take]);
        i += take;
    }
}

/// Generate a random 128-bit (32 hex char) session ID.
/// Uses fill_random so it is crypto-quality but has no external dependency.
fn generate_session_id() -> String {
    let mut buf = [0u8; 16];
    fill_random(&mut buf);
    hex::encode(&buf)
}

/// Minimal, allocation-free hex encoder (no external crate)
mod hex {
    const HEX: &[u8; 16] = b"0123456789abcdef";

    #[inline]
    pub fn encode(data: &[u8]) -> String {
        let mut out = String::with_capacity(data.len() * 2);
        for &b in data {
            out.push(HEX[(b >> 4) as usize] as char);
            out.push(HEX[(b & 0x0f) as usize] as char);
        }
        out
    }
}
