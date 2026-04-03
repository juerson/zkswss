use std::net::Ipv4Addr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

pub mod client;
pub use client::TunnelClient;

/// 解析 WebSocket URL
pub fn parse_ws_url(url: &str) -> Option<(String, u16, String)> {
    let (is_tls, rest) = if let Some(r) = url.strip_prefix("wss://") {
        (true, r)
    } else if let Some(r) = url.strip_prefix("ws://") {
        (false, r)
    } else {
        return None;
    };

    let default_port = if is_tls { 443 } else { 80 };

    // 分离 authority (host:port) 和 path_query
    let (authority, path_query) = match rest.find('/') {
        Some(i) => (&rest[..i], &rest[i..]),
        None => (rest, "/"),
    };

    // 处理 IPv6 的端口提取逻辑
    let (host, port) = if authority.starts_with('[') {
        // IPv6 格式: [addr]:port
        let end_bracket = authority.find(']')?;
        let host = &authority[1..end_bracket];
        let port_part = &authority[end_bracket + 1..];
        let port = if port_part.starts_with(':') {
            port_part[1..].parse().ok()?
        } else {
            default_port
        };
        (host, port)
    } else {
        // IPv4 或域名格式
        match authority.rfind(':') {
            Some(i) => (&authority[..i], authority[i + 1..].parse().ok()?),
            None => (authority, default_port),
        }
    };

    Some((host.to_string(), port, path_query.to_string()))
}

/// 快速随机索引生成
/// 结合了系统时间戳和原子计数器，减少对系统调用的依赖且保证了一定的随机性
fn rand_index(len: usize) -> usize {
    static COUNTER: AtomicUsize = AtomicUsize::new(0);
    let count = COUNTER.fetch_add(1, Ordering::Relaxed);

    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);

    // 简单的混合哈希，避免引入 rand 库
    let hash = (now as usize)
        .wrapping_add(count)
        .wrapping_mul(0x9e3779b97f4a7c15);
    hash % len
}

/// 解析 Cloudflare IP 段并返回随机 IP
pub fn resolve_cf_ip(input: &str) -> Option<String> {
    let parts: Vec<&str> = input
        .split(',')
        .map(|s| s.trim())
        .filter(|s| !s.is_empty())
        .collect();
    if parts.is_empty() {
        return None;
    }

    let selected = parts[rand_index(parts.len())];

    if !selected.contains('/') {
        return Some(selected.to_string());
    }

    let (ip_str, prefix_str) = selected.split_once('/')?;
    let prefix: u32 = prefix_str.parse().ok()?;
    let base: u32 = ip_str.parse::<Ipv4Addr>().ok()?.into();

    if prefix >= 32 {
        return Some(ip_str.to_string());
    }

    let num_ips = 2u32.checked_pow(32 - prefix).unwrap_or(0);

    if num_ips <= 2 {
        return Some(ip_str.to_string());
    }

    // 避开网络地址(.0)和广播地址(.255)，在可用范围内随机选择
    // 假设是标准的 C 类及以上，偏移量从 1 到 num_ips - 2
    let mask = !((1u32.checked_shl(32 - prefix).unwrap_or(0)).wrapping_sub(1));
    let network = base & mask;
    let offset = (rand_index((num_ips - 2) as usize) as u32) + 1;

    Some(Ipv4Addr::from(network + offset).to_string())
}
