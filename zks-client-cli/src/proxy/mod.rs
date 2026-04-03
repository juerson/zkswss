pub mod http;
pub mod hybrid;
pub mod router;
pub mod socks5;

use crate::cli::Mode;
use router::{ProxyRouter, RuleAction};
use std::{
    collections::HashMap,
    net::IpAddr,
    sync::Arc,
    time::{Duration, Instant},
};
use tokio::{net::TcpStream, sync::RwLock};
use tracing::{error, info};

const DNS_CACHE_TTL: Duration = Duration::from_secs(300);
const DNS_CACHE_MAX: usize = 4096;

#[derive(Debug)]
struct DnsCacheEntry {
    addrs: Vec<IpAddr>,
    inserted: Instant,
}

impl DnsCacheEntry {
    fn is_expired(&self) -> bool {
        self.inserted.elapsed() > DNS_CACHE_TTL
    }
}

#[derive(Debug, Default)]
pub struct DnsCache {
    inner: RwLock<HashMap<String, DnsCacheEntry>>,
}

impl DnsCache {
    pub fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    pub async fn lookup(&self, host: &str, port: u16) -> Vec<IpAddr> {
        {
            let map = self.inner.read().await;
            if let Some(entry) = map.get(host) {
                if !entry.is_expired() {
                    return entry.addrs.clone();
                }
            }
        }

        let addrs: Vec<IpAddr> = tokio::net::lookup_host(format!("{}:{}", host, port))
            .await
            .map(|iter| iter.map(|s| s.ip()).collect())
            .unwrap_or_default();

        if !addrs.is_empty() {
            let mut map = self.inner.write().await;
            if map.len() >= DNS_CACHE_MAX {
                map.retain(|_, v| !v.is_expired());
            }
            map.insert(
                host.to_string(),
                DnsCacheEntry {
                    addrs: addrs.clone(),
                    inserted: Instant::now(),
                },
            );
        }

        addrs
    }
}

pub async fn resolve_action(
    router: &Option<Arc<ProxyRouter>>,
    host: &str,
    port: u16,
    dns_cache: Option<&Arc<DnsCache>>,
) -> RuleAction {
    let Some(ref r) = router else {
        return RuleAction::Proxy;
    };

    let domain_action = r.route_domain(host);
    if domain_action != r.default_action {
        return domain_action;
    }

    let addrs = if let Some(cache) = dns_cache {
        cache.lookup(host, port).await
    } else {
        tokio::net::lookup_host(format!("{}:{}", host, port))
            .await
            .map(|iter| iter.map(|s| s.ip()).collect())
            .unwrap_or_default()
    };

    for ip in &addrs {
        let ip_action = r.route_ip(ip);
        if ip_action != r.default_action {
            return ip_action;
        }
    }

    domain_action
}

pub fn log_action(mode: &Mode, action: &RuleAction, host: &str, port: u16) {
    match action {
        RuleAction::Proxy => info!("{} 🌐 PROXY  {}:{}", mode.display_name(), host, port),
        RuleAction::Direct => info!("{} 🚀 DIRECT {}:{}", mode.display_name(), host, port),
        RuleAction::Reject => error!("{} ❌ REJECT {}:{}", mode.display_name(), host, port),
        RuleAction::Named(n) => info!("{} 🏷️  {} {}:{}", mode.display_name(), n, host, port),
    }
}

pub fn parse_host_port(
    host_port: &str,
    default_port: u16,
) -> Result<(String, u16), Box<dyn std::error::Error + Send + Sync>> {
    if let Some(inner) = host_port.strip_prefix('[') {
        let end = inner.find(']').ok_or("Invalid IPv6 address: missing ']'")?;
        let host = inner[..end].to_string();
        let port = inner
            .get(end + 2..)
            .and_then(|p| p.parse().ok())
            .unwrap_or(default_port);
        return Ok((host, port));
    }
    let mut parts = host_port.rsplitn(2, ':');
    match (parts.next(), parts.next()) {
        (Some(port_str), Some(host)) => {
            Ok((host.to_string(), port_str.parse().unwrap_or(default_port)))
        }
        _ => Ok((host_port.to_string(), default_port)),
    }
}

pub async fn relay_direct(client: TcpStream, target: TcpStream) {
    let (mut cr, mut cw) = client.into_split();
    let (mut tr, mut tw) = target.into_split();
    tokio::select! {
        _ = tokio::io::copy(&mut cr, &mut tw) => {}
        _ = tokio::io::copy(&mut tr, &mut cw) => {}
    }
}
