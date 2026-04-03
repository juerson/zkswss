use ahash::AHashMap;
use arc_swap::ArcSwap;
use ipnetwork::IpNetwork;
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    net::IpAddr,
    path::{Path, PathBuf},
    sync::{Arc, RwLock},
    time::{Duration, Instant},
};
use thiserror::Error;
use tokio::net::TcpStream;
use tracing::{debug, info, warn};

// ── 错误类型 ──────────────────────────────────────────────────────────────────

#[derive(Error, Debug)]
pub enum RouterError {
    #[error("Invalid rule format: {0}")]
    InvalidRule(String),
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),
    #[error("Parse error: {0}")]
    Parse(String),
    #[error("Network error: {0}")]
    Network(String),
    #[error("Rule set '{0}' not found")]
    RuleSetNotFound(String),
    #[error("YAML error: {0}")]
    Yaml(String),
}

// ── 正则缓存：RwLock + AHashMap（读多写少）──────────────────────────────────

static REGEX_CACHE: Lazy<RwLock<AHashMap<String, regex::Regex>>> =
    Lazy::new(|| RwLock::new(AHashMap::new()));

fn regex_matches(pattern: &str, domain: &str) -> bool {
    // 热路径：读锁，多线程并行，无独占
    if let Ok(cache) = REGEX_CACHE.read() {
        if let Some(re) = cache.get(pattern) {
            return re.is_match(domain);
        }
    }
    // 缓存未命中：升级为写锁插入
    let mut cache = REGEX_CACHE.write().unwrap();
    let re = cache.entry(pattern.to_string()).or_insert_with(|| {
        regex::Regex::new(pattern).unwrap_or_else(|_| regex::Regex::new("^.+$").unwrap())
    });
    re.is_match(domain)
}

// ── RuleAction ─────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum RuleAction {
    #[serde(rename = "proxy")]
    Proxy,
    #[serde(rename = "direct")]
    Direct,
    #[serde(rename = "reject")]
    Reject,
    Named(Arc<str>),
}

impl RuleAction {
    fn parse(s: &str) -> Self {
        match s.trim().to_uppercase().as_str() {
            "PROXY" => Self::Proxy,
            "DIRECT" => Self::Direct,
            "REJECT" | "BLOCK" | "DENY" => Self::Reject,
            other => Self::Named(Arc::from(other)),
        }
    }
}

// ── 协议 / 域名模式 / 端口匹配器 ─────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NetworkProtocol {
    Tcp,
    Udp,
    Any,
}

#[derive(Debug, Clone)]
pub enum DomainPattern {
    Exact(String),
    Suffix(String),
    Keyword(String),
    Regex(String),
    Wildcard,
}

#[derive(Debug, Clone)]
pub enum PortMatcher {
    Single(u16),
    Range(u16, u16),
    List(Vec<u16>),
}

impl PortMatcher {
    pub fn matches(&self, port: u16) -> bool {
        match self {
            Self::Single(p) => port == *p,
            Self::Range(lo, hi) => port >= *lo && port <= *hi,
            Self::List(ports) => ports.contains(&port),
        }
    }

    fn parse(s: &str) -> Result<Self, RouterError> {
        if s.contains(',') {
            let ports = s
                .split(',')
                .map(|p| {
                    p.trim()
                        .parse::<u16>()
                        .map_err(|_| RouterError::Parse(format!("Invalid port: {}", p)))
                })
                .collect::<Result<Vec<_>, _>>()?;
            return Ok(Self::List(ports));
        }
        if let Some((lo, hi)) = s.split_once('-') {
            let lo = lo
                .trim()
                .parse::<u16>()
                .map_err(|_| RouterError::Parse(format!("Invalid port range: {}", s)))?;
            let hi = hi
                .trim()
                .parse::<u16>()
                .map_err(|_| RouterError::Parse(format!("Invalid port range: {}", s)))?;
            return Ok(Self::Range(lo, hi));
        }
        let p = s
            .trim()
            .parse::<u16>()
            .map_err(|_| RouterError::Parse(format!("Invalid port: {}", s)))?;
        Ok(Self::Single(p))
    }
}

// ── RuleType / RuleCondition / Rule ──────────────────────────────────────────

#[derive(Debug, Clone)]
pub enum RuleType {
    Domain(DomainPattern),
    Network(IpNetwork),
    Ip(IpAddr),
    SrcNetwork(IpNetwork),
    GeoLocation(String),
    DestPort(PortMatcher),
    SrcPort(PortMatcher),
    ProcessName(String),
    ProcessPath(String),
    Network2(NetworkProtocol),
    And(Vec<RuleCondition>),
    Or(Vec<RuleCondition>),
    Not(Box<RuleCondition>),
    RuleSet(String),
    Final,
}

#[derive(Debug, Clone)]
pub struct RuleCondition {
    pub rule_type: RuleType,
}

#[derive(Debug, Clone)]
pub struct Rule {
    pub rule_type: RuleType,
    pub action: RuleAction,
    pub description: Option<String>,
    pub no_resolve: bool,
}

impl Rule {
    pub fn from_str(line: &str) -> Result<Self, RouterError> {
        let parts: Vec<&str> = line.splitn(4, ',').collect();
        if parts.len() < 2 {
            return Err(RouterError::InvalidRule(line.to_string()));
        }

        let type_str = parts[0].trim().to_uppercase();
        let pattern = parts[1].trim();

        let (action_str, extra) = if type_str == "FINAL" || type_str == "MATCH" {
            (pattern.to_string(), "")
        } else if parts.len() >= 3 {
            (
                parts[2].trim().to_string(),
                parts.get(3).copied().unwrap_or("").trim(),
            )
        } else {
            return Err(RouterError::InvalidRule(format!(
                "Too few fields: {}",
                line
            )));
        };

        let action = RuleAction::parse(&action_str);
        let no_resolve = extra.eq_ignore_ascii_case("no-resolve");
        let rule_type = Self::parse_type(&type_str, pattern)?;

        Ok(Rule {
            rule_type,
            action,
            description: None,
            no_resolve,
        })
    }

    fn parse_type(type_str: &str, pattern: &str) -> Result<RuleType, RouterError> {
        Ok(match type_str {
            "DOMAIN" => RuleType::Domain(DomainPattern::Exact(pattern.to_lowercase())),
            "DOMAIN-SUFFIX" => RuleType::Domain(DomainPattern::Suffix(pattern.to_lowercase())),
            "DOMAIN-KEYWORD" => RuleType::Domain(DomainPattern::Keyword(pattern.to_lowercase())),
            "DOMAIN-REGEX" => RuleType::Domain(DomainPattern::Regex(pattern.to_string())),

            "IP-CIDR" | "IP-CIDR6" => {
                let net = pattern.parse::<IpNetwork>().map_err(|e| {
                    RouterError::Parse(format!("Invalid CIDR '{}': {}", pattern, e))
                })?;
                RuleType::Network(net)
            }
            "SRC-IP-CIDR" => {
                let net = pattern.parse::<IpNetwork>().map_err(|e| {
                    RouterError::Parse(format!("Invalid SRC CIDR '{}': {}", pattern, e))
                })?;
                RuleType::SrcNetwork(net)
            }
            "GEOIP" => {
                let cc = pattern.to_uppercase();
                if cc != "LAN" && cc.len() != 2 {
                    return Err(RouterError::Parse(format!(
                        "Invalid country code: {}",
                        pattern
                    )));
                }
                RuleType::GeoLocation(cc)
            }

            "DEST-PORT" | "DST-PORT" => RuleType::DestPort(PortMatcher::parse(pattern)?),
            "SRC-PORT" => RuleType::SrcPort(PortMatcher::parse(pattern)?),

            "PROCESS-NAME" => RuleType::ProcessName(pattern.to_string()),
            "PROCESS-PATH" => RuleType::ProcessPath(pattern.to_string()),

            "NETWORK" => {
                let proto = match pattern.to_uppercase().as_str() {
                    "TCP" => NetworkProtocol::Tcp,
                    "UDP" => NetworkProtocol::Udp,
                    _ => return Err(RouterError::Parse(format!("Unknown protocol: {}", pattern))),
                };
                RuleType::Network2(proto)
            }

            "RULE-SET" => RuleType::RuleSet(pattern.to_string()),

            "AND" => RuleType::And(Self::parse_composite(pattern)?),
            "OR" => RuleType::Or(Self::parse_composite(pattern)?),
            "NOT" => {
                let conds = Self::parse_composite(pattern)?;
                let first = conds
                    .into_iter()
                    .next()
                    .ok_or_else(|| RouterError::Parse("NOT requires one condition".into()))?;
                RuleType::Not(Box::new(first))
            }

            "FINAL" | "MATCH" => RuleType::Final,

            _ => {
                return Err(RouterError::InvalidRule(format!(
                    "Unknown rule type: {}",
                    type_str
                )))
            }
        })
    }

    fn parse_composite(s: &str) -> Result<Vec<RuleCondition>, RouterError> {
        let s = s.trim().trim_start_matches('(').trim_end_matches(')');
        let mut conditions = Vec::new();
        let mut depth = 0i32;
        let mut start = 0usize;

        for (i, &b) in s.as_bytes().iter().enumerate() {
            match b {
                b'(' => depth += 1,
                b')' => {
                    depth -= 1;
                    if depth == 0 {
                        let seg = &s[start..=i];
                        let inner = seg.trim().trim_start_matches('(').trim_end_matches(')');
                        if !inner.is_empty() {
                            if let Some((t, p)) = inner.split_once(',') {
                                if let Ok(rule_type) =
                                    Rule::parse_type(&t.trim().to_uppercase(), p.trim())
                                {
                                    conditions.push(RuleCondition { rule_type });
                                }
                            }
                        }
                        start = i + 1;
                    }
                }
                _ => {}
            }
        }
        Ok(conditions)
    }
}

// ── 后缀检查辅助函数 ────────────────────────────────────────────────────

/// 检查 `domain` 是否等于 `suffix` 或以 `.<suffix>` 结尾
#[inline]
fn domain_has_suffix(domain: &str, suffix: &str) -> bool {
    domain == suffix
        || (domain.len() > suffix.len()
            && domain.ends_with(suffix)
            && domain.as_bytes()[domain.len() - suffix.len() - 1] == b'.')
}

// ── DomainIndex：AHashMap 精确匹配 + TLD 分桶后缀 ────────────────────────────

#[derive(Debug, Default)]
struct DomainIndex {
    /// 精确匹配
    exact: AHashMap<String, RuleAction>,
    /// 后缀按 TLD 分桶
    suffix_buckets: AHashMap<String, Vec<(String, RuleAction)>>,
    /// 关键字 / 正则 / 通配符等慢路径规则
    slow: Vec<(DomainPattern, RuleAction)>,
}

impl DomainIndex {
    fn insert(&mut self, pattern: DomainPattern, action: RuleAction) {
        match pattern {
            DomainPattern::Exact(s) => {
                self.exact.entry(s).or_insert(action);
            }
            DomainPattern::Suffix(s) => {
                let tld = s.rsplit('.').next().unwrap_or(&s).to_string();
                self.suffix_buckets
                    .entry(tld)
                    .or_default()
                    .push((s, action));
            }
            other => {
                self.slow.push((other, action));
            }
        }
    }

    fn build(&mut self) {
        // 每个桶内按后缀长度降序，更具体的规则优先
        for bucket in self.suffix_buckets.values_mut() {
            bucket.sort_by(|a, b| b.0.len().cmp(&a.0.len()));
        }
    }

    fn lookup(&self, domain: &str) -> Option<&RuleAction> {
        // 条件 lowercase：域名已小写时零分配（实践中占绝大多数）
        let lower_buf;
        let lower = if domain.bytes().any(|b| b.is_ascii_uppercase()) {
            lower_buf = domain.to_lowercase();
            lower_buf.as_str()
        } else {
            domain
        };

        // 1. 精确匹配 O(1)
        if let Some(action) = self.exact.get(lower) {
            return Some(action);
        }

        // 2. 只扫描同 TLD 桶
        let tld = lower.rsplit('.').next().unwrap_or(lower);
        if let Some(bucket) = self.suffix_buckets.get(tld) {
            for (suffix, action) in bucket {
                if domain_has_suffix(lower, suffix) {
                    return Some(action);
                }
            }
        }

        // 3. 慢路径：关键字 / 正则 / 通配符
        for (pattern, action) in &self.slow {
            let matched = match pattern {
                DomainPattern::Wildcard => true,
                DomainPattern::Keyword(kw) => lower.contains(kw.as_str()),
                DomainPattern::Regex(pat) => regex_matches(pat, lower),
                _ => false,
            };
            if matched {
                return Some(action);
            }
        }

        None
    }
}

// ── IpIndex：/32、/128 走 HashMap O(1)，其余 CIDR 线性扫描 ───────────────────

#[derive(Debug, Default)]
struct IpIndex {
    exact_v4: AHashMap<u32, RuleAction>,    // /32  → O(1)
    exact_v6: AHashMap<u128, RuleAction>,   // /128 → O(1)
    networks: Vec<(IpNetwork, RuleAction)>, // 其余 CIDR，按前缀长度降序
}

impl IpIndex {
    fn insert(&mut self, net: IpNetwork, action: RuleAction) {
        match net {
            IpNetwork::V4(n) if n.prefix() == 32 => {
                self.exact_v4.entry(u32::from(n.ip())).or_insert(action);
            }
            IpNetwork::V6(n) if n.prefix() == 128 => {
                self.exact_v6.entry(u128::from(n.ip())).or_insert(action);
            }
            _ => {
                self.networks.push((net, action));
            }
        }
    }

    fn build(&mut self) {
        self.networks
            .sort_by(|a, b| b.0.prefix().cmp(&a.0.prefix()));
    }

    fn lookup(&self, ip: &IpAddr) -> Option<&RuleAction> {
        // 精确命中 O(1)
        match ip {
            IpAddr::V4(v4) => {
                if let Some(a) = self.exact_v4.get(&u32::from(*v4)) {
                    return Some(a);
                }
            }
            IpAddr::V6(v6) => {
                if let Some(a) = self.exact_v6.get(&u128::from(*v6)) {
                    return Some(a);
                }
            }
        }
        // CIDR 线性扫描（已按前缀长度排序，首次命中即最具体规则）
        self.networks
            .iter()
            .find(|(net, _)| net.contains(*ip))
            .map(|(_, a)| a)
    }
}

// ── RuleProvider 配置 ─────────────────────────────────────────────────────────

#[derive(Debug, Clone, Deserialize)]
pub struct RuleProviderConfig {
    #[serde(rename = "type")]
    pub source_type: ProviderSourceType,
    pub behavior: ProviderBehavior,
    pub url: Option<String>,
    pub path: Option<String>,
    pub interval: Option<u64>,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ProviderSourceType {
    Http,
    File,
    Inline,
}

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ProviderBehavior {
    Domain,
    IpCidr,
    Classical,
}

#[derive(Debug, Deserialize, Default)]
struct ProxyConfig {
    #[serde(rename = "rule-providers", default)]
    rule_providers: HashMap<String, RuleProviderConfig>,
    #[serde(default)]
    rules: Vec<String>,
}

// ── RuleSet ───────────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct RuleSet {
    pub name: String,
    pub behavior: ProviderBehavior,
    /// Classical 行为下，端口/进程/复合等无法索引的规则
    // classical_rules: Vec<Rule>,
    /// 所有行为均填充这两个索引（Classical 也走索引，消除线性扫描）
    domain_index: Arc<DomainIndex>,
    ip_index: Arc<IpIndex>,
    pub source: RuleSetSource,
    pub loaded_at: Instant,
    pub ttl: Option<Duration>,
    pub rule_count: usize,
}

#[derive(Debug, Clone)]
pub enum RuleSetSource {
    File(PathBuf),
    Url(String),
    Inline,
}

impl RuleSet {
    pub fn is_stale(&self) -> bool {
        self.ttl
            .map(|t| self.loaded_at.elapsed() > t)
            .unwrap_or(false)
    }

    pub fn parse_content(
        name: &str,
        content: &str,
        behavior: &ProviderBehavior,
        source: RuleSetSource,
        ttl: Option<Duration>,
    ) -> Self {
        let lines = Self::extract_lines(content);

        let mut domain_index = DomainIndex::default();
        let mut ip_index = IpIndex::default();
        let mut classical_rules = Vec::new();

        match behavior {
            ProviderBehavior::Domain => {
                for line in &lines {
                    let (pattern, action) = if let Some(rest) = line.strip_prefix("+.") {
                        (
                            DomainPattern::Suffix(rest.to_lowercase()),
                            RuleAction::Direct,
                        )
                    } else {
                        (
                            DomainPattern::Exact(line.to_lowercase()),
                            RuleAction::Direct,
                        )
                    };
                    domain_index.insert(pattern, action);
                }
                domain_index.build();
            }
            ProviderBehavior::IpCidr => {
                for line in &lines {
                    match line.parse::<IpNetwork>() {
                        Ok(net) => ip_index.insert(net, RuleAction::Direct),
                        Err(_) => warn!("Invalid CIDR in rule set '{}': {}", name, line),
                    }
                }
                ip_index.build();
            }
            ProviderBehavior::Classical => {
                // 域名和 IP 规则走索引；其余（端口/进程/复合）保留在 classical_rules
                for line in &lines {
                    let with_action = format!("{},DIRECT", line);
                    match Rule::from_str(&with_action) {
                        Ok(r) => {
                            let Rule {
                                rule_type,
                                action,
                                description,
                                no_resolve,
                            } = r;
                            match rule_type {
                                RuleType::Domain(p) => {
                                    domain_index.insert(p, action);
                                }
                                RuleType::Network(net) => {
                                    ip_index.insert(net, action);
                                }
                                RuleType::Ip(ip_addr) => {
                                    let prefix = if ip_addr.is_ipv4() { 32 } else { 128 };
                                    if let Ok(net) = IpNetwork::new(ip_addr, prefix) {
                                        ip_index.insert(net, action);
                                    }
                                }
                                rule_type => classical_rules.push(Rule {
                                    rule_type,
                                    action,
                                    description,
                                    no_resolve,
                                }),
                            }
                        }
                        Err(e) => warn!("Classical rule error in '{}' '{}': {}", name, line, e),
                    }
                }
                domain_index.build();
                ip_index.build();
            }
        }

        let suffix_count: usize = domain_index.suffix_buckets.values().map(|b| b.len()).sum();
        let rule_count = domain_index.exact.len()
            + suffix_count
            + domain_index.slow.len()
            + ip_index.exact_v4.len()
            + ip_index.exact_v6.len()
            + ip_index.networks.len()
            + classical_rules.len();

        info!(
            "Rule set '{}' loaded {} rules ({:?})",
            name, rule_count, behavior
        );

        RuleSet {
            name: name.to_string(),
            behavior: behavior.clone(),
            // classical_rules,
            domain_index: Arc::new(domain_index),
            ip_index: Arc::new(ip_index),
            source,
            loaded_at: Instant::now(),
            ttl,
            rule_count,
        }
    }

    fn extract_lines(content: &str) -> Vec<String> {
        let has_payload = content.lines().any(|l| l.trim() == "payload:");

        if has_payload {
            let mut in_payload = false;
            let mut lines = Vec::new();
            for raw in content.lines() {
                let trimmed = raw.trim();
                if trimmed == "payload:" {
                    in_payload = true;
                    continue;
                }
                if in_payload {
                    let entry_raw = if let Some(rest) = trimmed.strip_prefix("- ") {
                        rest
                    } else if let Some(rest) = trimmed.strip_prefix('-') {
                        rest
                    } else {
                        if !trimmed.is_empty()
                            && !trimmed.starts_with('#')
                            && !trimmed.starts_with(' ')
                        {
                            break;
                        }
                        continue;
                    };
                    let entry = entry_raw
                        .trim()
                        .trim_matches(|c: char| c == '\'' || c == '"');
                    if !entry.is_empty() {
                        lines.push(entry.to_string());
                    }
                }
            }
            lines
        } else {
            content
                .lines()
                .map(|l| l.trim())
                .filter(|l| !l.is_empty() && !l.starts_with('#') && !l.starts_with("//"))
                .map(|l| l.to_string())
                .collect()
        }
    }

    pub fn matches_domain(&self, domain: &str) -> bool {
        match self.behavior {
            // Classical 行为的域名规则已进索引，与 Domain 行为走同一路径
            ProviderBehavior::Domain | ProviderBehavior::Classical => {
                self.domain_index.lookup(domain).is_some()
            }
            ProviderBehavior::IpCidr => false,
        }
    }

    pub fn matches_ip(&self, ip: &IpAddr) -> bool {
        match self.behavior {
            // Classical 行为的 IP 规则已进索引，与 IpCidr 行为走同一路径
            ProviderBehavior::IpCidr | ProviderBehavior::Classical => {
                self.ip_index.lookup(ip).is_some()
            }
            ProviderBehavior::Domain => false,
        }
    }
}

// ── MatchContext ──────────────────────────────────────────────────────────────

#[derive(Debug, Default, Clone)]
pub struct MatchContext {
    pub src_ip: Option<IpAddr>,
    pub src_port: Option<u16>,
    pub dst_port: Option<u16>,
    pub network: Option<NetworkProtocol>,
    pub process_name: Option<String>,
    pub process_path: Option<String>,
}

/// 静态空 ctx，route_domain / route_ip 不再每次在栈上初始化
static EMPTY_CTX: Lazy<MatchContext> = Lazy::new(MatchContext::default);

// ── ProxyRouter ───────────────────────────────────────────────────────────────

pub struct ProxyRouter {
    domain_index: DomainIndex,
    ip_index: IpIndex,
    slow_rules: Vec<Rule>,

    pub default_action: RuleAction,
    rule_sets: Arc<ArcSwap<HashMap<String, RuleSet>>>,
    pub geoip_reader: Option<maxminddb::Reader<Vec<u8>>>,
}

impl std::fmt::Debug for ProxyRouter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let suffix_count: usize = self
            .domain_index
            .suffix_buckets
            .values()
            .map(|b| b.len())
            .sum();
        let ip_total = self.ip_index.networks.len()
            + self.ip_index.exact_v4.len()
            + self.ip_index.exact_v6.len();
        f.debug_struct("ProxyRouter")
            .field("domain_exact", &self.domain_index.exact.len())
            .field("domain_suffix", &suffix_count)
            .field("ip_total", &ip_total)
            .field("slow_rules", &self.slow_rules.len())
            .field("default_action", &self.default_action)
            .finish()
    }
}

// ── 内置规则：Lazy 静态缓存，多次构造 Router 只解析一次 ──────────────────────

static BUILTIN_RULES: Lazy<Vec<Rule>> = Lazy::new(make_builtin_direct_rules);

fn builtin_direct_rules() -> Vec<Rule> {
    BUILTIN_RULES.clone()
}

fn make_builtin_direct_rules() -> Vec<Rule> {
    const CIDRS: &[&str] = &[
        "127.0.0.0/8",
        "::1/128",
        "0.0.0.0/8",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "169.254.0.0/16",
        "fe80::/10",
        "224.0.0.0/4",
        "ff00::/8",
        "192.0.2.0/24",
        "198.51.100.0/24",
        "203.0.113.0/24",
        "240.0.0.0/4",
    ];

    const LOCAL_DOMAINS: &[(&str, bool)] = &[
        ("localhost", false),
        ("local", true),
        ("localdomain", true),
        ("internal", true),
        ("lan", true),
        ("home.arpa", true),
    ];

    let mut rules = Vec::with_capacity(CIDRS.len() + LOCAL_DOMAINS.len());

    rules.extend(CIDRS.iter().filter_map(|c| {
        c.parse::<IpNetwork>().ok().map(|net| Rule {
            rule_type: RuleType::Network(net),
            action: RuleAction::Direct,
            description: Some("builtin: private/reserved".into()),
            no_resolve: false,
        })
    }));

    for &(domain, is_suffix) in LOCAL_DOMAINS {
        let pat = if is_suffix {
            DomainPattern::Suffix(domain.to_string())
        } else {
            DomainPattern::Exact(domain.to_string())
        };
        rules.push(Rule {
            rule_type: RuleType::Domain(pat),
            action: RuleAction::Direct,
            description: Some("builtin: local domain".into()),
            no_resolve: true,
        });
    }

    rules
}

// ── Provider 加载辅助 ──────────────────────────────────────────────────────────────────────

async fn fetch_url(url: &str) -> Result<String, RouterError> {
    reqwest::get(url)
        .await
        .map_err(|e| RouterError::Network(e.to_string()))?
        .text()
        .await
        .map_err(|e| RouterError::Network(e.to_string()))
}

async fn load_provider_entry(
    name: &str,
    cfg: &RuleProviderConfig,
    base_dir: &Path,
) -> Option<(String, RuleSet)> {
    let ttl = cfg.interval.map(Duration::from_secs);

    match cfg.source_type {
        ProviderSourceType::Http => {
            let cache_path = cfg.path.as_deref().map(|p| base_dir.join(p));

            let cached = if let Some(ref cp) = cache_path {
                if cp.exists() {
                    tokio::fs::read_to_string(cp).await.ok()
                } else {
                    None
                }
            } else {
                None
            };

            let content: String = if let Some(c) = cached {
                c
            } else if let Some(url) = &cfg.url {
                match fetch_url(url).await {
                    Ok(c) => {
                        if let Some(ref cp) = cache_path {
                            if let Some(parent) = cp.parent() {
                                let _ = tokio::fs::create_dir_all(parent).await;
                            }
                            let _ = tokio::fs::write(cp, &c).await;
                        }
                        c
                    }
                    Err(e) => {
                        warn!("Failed to fetch rule set '{}': {}", name, e);
                        return None;
                    }
                }
            } else {
                warn!("Rule set '{}' has no url and no local cache", name);
                return None;
            };

            let source = RuleSetSource::Url(cfg.url.clone().unwrap_or_default());
            let set = RuleSet::parse_content(name, &content, &cfg.behavior, source, ttl);
            Some((name.to_string(), set))
        }

        ProviderSourceType::File => {
            let path = cfg.path.as_deref().map(|p| base_dir.join(p))?;
            match tokio::fs::read_to_string(&path).await {
                Ok(c) => {
                    let set = RuleSet::parse_content(
                        name,
                        &c,
                        &cfg.behavior,
                        RuleSetSource::File(path),
                        ttl,
                    );
                    Some((name.to_string(), set))
                }
                Err(e) => {
                    warn!("Failed to read rule set file '{}': {}", name, e);
                    None
                }
            }
        }

        ProviderSourceType::Inline => None,
    }
}

// ── ProxyRouter impl ──────────────────────────────────────────────────────────

impl ProxyRouter {
    pub async fn from_file<P: AsRef<Path>>(path: P) -> Result<Self, RouterError> {
        let content = tokio::fs::read_to_string(&path).await?;
        let base_dir = path.as_ref().parent().unwrap_or(Path::new("."));
        Self::from_yaml_str(&content, base_dir).await
    }

    pub async fn from_yaml_str(content: &str, base_dir: &Path) -> Result<Self, RouterError> {
        let config: ProxyConfig = serde_yaml::from_str(content)
            .map_err(|e: serde_yaml::Error| RouterError::Yaml(e.to_string()))?;

        let (rule_providers, config_rules) = (config.rule_providers, config.rules);

        // 并发加载所有 provider：从 O(n×RTT) 降至 O(RTT)
        let mut join_set = tokio::task::JoinSet::new();
        for (name, cfg) in rule_providers {
            let base_dir_buf = base_dir.to_path_buf();
            join_set.spawn(async move { load_provider_entry(&name, &cfg, &base_dir_buf).await });
        }

        let mut rule_sets_map: HashMap<String, RuleSet> = HashMap::new();
        while let Some(result) = join_set.join_next().await {
            if let Ok(Some((name, set))) = result {
                rule_sets_map.insert(name, set);
            }
        }
        let rule_sets = Arc::new(ArcSwap::from_pointee(rule_sets_map));

        let mut default_action = RuleAction::Proxy;
        let mut all_rules = builtin_direct_rules();

        for line in &config_rules {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            match Rule::from_str(line) {
                Ok(rule) => {
                    if matches!(rule.rule_type, RuleType::Final) {
                        default_action = rule.action.clone();
                    } else {
                        all_rules.push(rule);
                    }
                }
                Err(e) => warn!("Rule parse error '{}': {}", line, e),
            }
        }

        let geoip_reader = Self::load_geoip_database();
        let router = Self::build_indexes(all_rules, default_action, rule_sets, geoip_reader);

        let suffix_count: usize = router
            .domain_index
            .suffix_buckets
            .values()
            .map(|b| b.len())
            .sum();
        let ip_total = router.ip_index.networks.len()
            + router.ip_index.exact_v4.len()
            + router.ip_index.exact_v6.len();

        info!(
            "Router ready — domain exact={}, suffix={}, IPs={}, slow={}, rule_sets={}",
            router.domain_index.exact.len(),
            suffix_count,
            ip_total,
            router.slow_rules.len(),
            router.rule_sets.load().len(),
        );

        Ok(router)
    }

    pub fn from_plain_str(content: &str) -> Result<Self, RouterError> {
        let mut default_action = RuleAction::Proxy;
        let mut all_rules = builtin_direct_rules();

        for line in content.lines() {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') || line.starts_with("//") {
                continue;
            }
            match Rule::from_str(line) {
                Ok(rule) => {
                    if matches!(rule.rule_type, RuleType::Final) {
                        default_action = rule.action.clone();
                    } else {
                        all_rules.push(rule);
                    }
                }
                Err(e) => warn!("Rule parse error '{}': {}", line, e),
            }
        }

        let geoip_reader = Self::load_geoip_database();
        let router = Self::build_indexes(
            all_rules,
            default_action,
            Arc::new(ArcSwap::from_pointee(HashMap::new())),
            geoip_reader,
        );

        let suffix_count: usize = router
            .domain_index
            .suffix_buckets
            .values()
            .map(|b| b.len())
            .sum();
        let ip_total = router.ip_index.networks.len()
            + router.ip_index.exact_v4.len()
            + router.ip_index.exact_v6.len();

        info!(
            "Router ready — domain exact={}, suffix={}, IPs={}, slow={}",
            router.domain_index.exact.len(),
            suffix_count,
            ip_total,
            router.slow_rules.len(),
        );

        Ok(router)
    }

    fn build_indexes(
        rules: Vec<Rule>,
        default_action: RuleAction,
        rule_sets: Arc<ArcSwap<HashMap<String, RuleSet>>>,
        geoip_reader: Option<maxminddb::Reader<Vec<u8>>>,
    ) -> Self {
        let mut domain_index = DomainIndex::default();
        let mut ip_index = IpIndex::default();
        let mut slow_rules = Vec::new();

        for rule in rules {
            // 解构以便在 match 各分支中自由移动各字段
            let Rule {
                rule_type,
                action,
                description,
                no_resolve,
            } = rule;
            match rule_type {
                RuleType::Domain(p) => {
                    domain_index.insert(p, action);
                }
                RuleType::Network(net) => {
                    ip_index.insert(net, action);
                }
                // 单 IP 规则进 exact 哈希表，消除 slow_rules 线性扫描
                RuleType::Ip(ip_addr) => {
                    let prefix = if ip_addr.is_ipv4() { 32 } else { 128 };
                    if let Ok(net) = IpNetwork::new(ip_addr, prefix) {
                        ip_index.insert(net, action);
                    }
                }
                rule_type => {
                    slow_rules.push(Rule {
                        rule_type,
                        action,
                        description,
                        no_resolve,
                    });
                }
            }
        }

        domain_index.build();
        ip_index.build();

        Self {
            domain_index,
            ip_index,
            slow_rules,
            default_action,
            rule_sets,
            geoip_reader,
        }
    }

    pub async fn load_rule_set_file(
        &self,
        name: &str,
        path: &str,
        behavior: ProviderBehavior,
        ttl: Option<Duration>,
    ) -> Result<(), RouterError> {
        let content = tokio::fs::read_to_string(path).await?;
        let set = RuleSet::parse_content(
            name,
            &content,
            &behavior,
            RuleSetSource::File(path.into()),
            ttl,
        );
        // ArcSwap 写：clone map → 插入 → 原子替换指针
        let mut new_map = (**self.rule_sets.load()).clone();
        new_map.insert(name.to_string(), set);
        self.rule_sets.store(Arc::new(new_map));
        Ok(())
    }

    pub async fn load_rule_set_url(
        &self,
        name: &str,
        url: &str,
        behavior: ProviderBehavior,
        ttl: Option<Duration>,
    ) -> Result<(), RouterError> {
        let content = fetch_url(url).await?;
        let set = RuleSet::parse_content(
            name,
            &content,
            &behavior,
            RuleSetSource::Url(url.to_string()),
            ttl,
        );
        let mut new_map = (**self.rule_sets.load()).clone();
        new_map.insert(name.to_string(), set);
        self.rule_sets.store(Arc::new(new_map));
        Ok(())
    }

    pub async fn refresh_stale_rule_sets(&self) {
        // 只 clone 名称列表，减少持锁数据量
        let stale_names: Vec<String> = {
            let sets = self.rule_sets.load();
            sets.values()
                .filter(|s| s.is_stale())
                .map(|s| s.name.clone())
                .collect()
        };

        for name in stale_names {
            let (source, behavior, ttl) = {
                let sets = self.rule_sets.load();
                match sets.get(&name) {
                    Some(s) => (s.source.clone(), s.behavior.clone(), s.ttl),
                    None => continue,
                }
            };
            let result = match &source {
                RuleSetSource::File(p) => {
                    self.load_rule_set_file(&name, p.to_str().unwrap_or(""), behavior, ttl)
                        .await
                }
                RuleSetSource::Url(url) => self.load_rule_set_url(&name, url, behavior, ttl).await,
                RuleSetSource::Inline => continue,
            };
            match result {
                Ok(_) => info!("Refreshed rule set '{}'", name),
                Err(e) => warn!("Failed to refresh rule set '{}': {}", name, e),
            }
        }
    }

    pub fn route_domain(&self, domain: &str) -> RuleAction {
        if let Some(action) = self.domain_index.lookup(domain) {
            return action.clone();
        }
        for rule in &self.slow_rules {
            if let Some(action) = self.eval_slow_rule(rule, Some(domain), None, &EMPTY_CTX) {
                return action;
            }
        }
        self.default_action.clone()
    }

    pub fn route_ip(&self, ip: &IpAddr) -> RuleAction {
        if let Some(action) = self.ip_index.lookup(ip) {
            return action.clone();
        }
        for rule in &self.slow_rules {
            if let Some(action) = self.eval_slow_rule(rule, None, Some(ip), &EMPTY_CTX) {
                return action;
            }
        }
        self.default_action.clone()
    }

    pub fn route(
        &self,
        domain: Option<&str>,
        ip: Option<&IpAddr>,
        ctx: &MatchContext,
    ) -> RuleAction {
        if let Some(d) = domain {
            if let Some(action) = self.domain_index.lookup(d) {
                return action.clone();
            }
        }
        if let Some(i) = ip {
            if let Some(action) = self.ip_index.lookup(i) {
                return action.clone();
            }
        }
        for rule in &self.slow_rules {
            // 根据 ctx 字段是否有值，提前跳过必然不匹配的规则
            let skip = match &rule.rule_type {
                RuleType::GeoLocation(_) => ip.is_none(),
                RuleType::DestPort(_) => ctx.dst_port.is_none(),
                RuleType::SrcPort(_) => ctx.src_port.is_none(),
                RuleType::ProcessName(_) => ctx.process_name.is_none(),
                RuleType::ProcessPath(_) => ctx.process_path.is_none(),
                RuleType::Network2(_) => ctx.network.is_none(),
                RuleType::SrcNetwork(_) => ctx.src_ip.is_none(),
                _ => false,
            };
            if skip {
                continue;
            }
            if let Some(action) = self.eval_slow_rule(rule, domain, ip, ctx) {
                return action;
            }
        }
        self.default_action.clone()
    }

    fn eval_slow_rule(
        &self,
        rule: &Rule,
        domain: Option<&str>,
        ip: Option<&IpAddr>,
        ctx: &MatchContext,
    ) -> Option<RuleAction> {
        if self.matches_slow(&rule.rule_type, domain, ip, ctx) {
            Some(rule.action.clone())
        } else {
            None
        }
    }

    fn matches_slow(
        &self,
        rt: &RuleType,
        domain: Option<&str>,
        ip: Option<&IpAddr>,
        ctx: &MatchContext,
    ) -> bool {
        match rt {
            // 进入 slow_rules 的 Domain 规则只有 Keyword/Regex/Wildcard，
            // 不再重复查询 domain_index（已在 route() 快路径中处理）
            RuleType::Domain(p) => domain
                .map(|d| self.matches_domain_pattern(d, p))
                .unwrap_or(false),

            RuleType::Network(net) => ip.map(|a| net.contains(*a)).unwrap_or(false),

            RuleType::Ip(rule_ip) => ip.map(|a| a == rule_ip).unwrap_or(false),

            RuleType::SrcNetwork(net) => ctx.src_ip.map(|a| net.contains(a)).unwrap_or(false),

            RuleType::GeoLocation(cc) => {
                if cc == "LAN" {
                    ip.map(is_private_ip).unwrap_or(false)
                } else {
                    ip.and_then(|a| self.get_ip_country(a))
                        .map(|c| c.eq_ignore_ascii_case(cc))
                        .unwrap_or(false)
                }
            }

            RuleType::DestPort(m) => ctx.dst_port.map(|p| m.matches(p)).unwrap_or(false),
            RuleType::SrcPort(m) => ctx.src_port.map(|p| m.matches(p)).unwrap_or(false),

            RuleType::ProcessName(n) => ctx
                .process_name
                .as_deref()
                .map(|pn| pn.eq_ignore_ascii_case(n))
                .unwrap_or(false),

            RuleType::ProcessPath(p) => ctx
                .process_path
                .as_deref()
                .map(|pp| pp.eq_ignore_ascii_case(p))
                .unwrap_or(false),

            RuleType::Network2(proto) => ctx
                .network
                .as_ref()
                .map(|n| proto == &NetworkProtocol::Any || n == proto)
                .unwrap_or(false),

            RuleType::And(conds) => conds
                .iter()
                .all(|c| self.matches_slow(&c.rule_type, domain, ip, ctx)),

            RuleType::Or(conds) => conds
                .iter()
                .any(|c| self.matches_slow(&c.rule_type, domain, ip, ctx)),

            RuleType::Not(cond) => !self.matches_slow(&cond.rule_type, domain, ip, ctx),

            RuleType::RuleSet(name) => {
                let sets = self.rule_sets.load();
                if let Some(set) = sets.get(name.as_str()) {
                    let dm = domain.map(|d| set.matches_domain(d)).unwrap_or(false);
                    let im = ip.map(|i| set.matches_ip(i)).unwrap_or(false);
                    dm || im
                } else {
                    debug!("RULE-SET '{}' not loaded, skipping", name);
                    false
                }
            }

            RuleType::Final => true,
        }
    }

    fn matches_domain_pattern(&self, domain: &str, pattern: &DomainPattern) -> bool {
        match pattern {
            DomainPattern::Wildcard => true,
            DomainPattern::Exact(p) => domain.eq_ignore_ascii_case(p),
            DomainPattern::Suffix(s) => {
                let d = domain.to_lowercase();
                domain_has_suffix(&d, s)
            }
            DomainPattern::Keyword(kw) => domain.to_lowercase().contains(kw.as_str()),
            DomainPattern::Regex(pat) => regex_matches(pat, domain),
        }
    }

    pub fn get_ip_country(&self, ip: &IpAddr) -> Option<String> {
        let reader = self.geoip_reader.as_ref()?;
        let lookup = reader.lookup(*ip).ok()?;
        if let Some(country) = lookup.decode::<maxminddb::geoip2::Country>().ok()? {
            country.country.iso_code.map(|c: &str| c.to_string())
        } else {
            None
        }
    }

    /*
       GeoLite2-Country.mmdb
       https://github.com/P3TERX/GeoLite.mmdb
       https://github.com/8bitsaver/maxmind-geoip

       Country.mmdb
       https://github.com/runetfreedom/russia-blocked-geoip
       https://github.com/Dreamacro/maxmind-geoip

       Merged-IP.mmdb
       https://github.com/NetworkCats/Merged-IP-Data
    */
    fn load_geoip_database() -> Option<maxminddb::Reader<Vec<u8>>> {
        let db_paths = [
            "mmdb/Merged-IP.mmdb",
            "mmdb/Country.mmdb",
            "mmdb/GeoLite2-Country.mmdb",
            "mmdb/GeoIP2-Country.mmdb",
        ];
        for path in &db_paths {
            if let Ok(db) = std::fs::read(path) {
                if let Ok(reader) = maxminddb::Reader::from_source(db) {
                    info!("Loaded GeoIP database from: {}", path);
                    return Some(reader);
                }
            }
        }
        info!("GeoIP database not found — GEOIP rules will be skipped");
        None
    }

    pub async fn create_direct_connection(
        &self,
        host: &str,
        port: u16,
    ) -> Result<TcpStream, Box<dyn std::error::Error + Send + Sync>> {
        Ok(TcpStream::connect(format!("{}:{}", host, port)).await?)
    }

    pub fn stats(&self) -> RouterStats {
        let suffix_count: usize = self
            .domain_index
            .suffix_buckets
            .values()
            .map(|b| b.len())
            .sum();
        let domain = self.domain_index.exact.len() + suffix_count + self.domain_index.slow.len();
        let ip = self.ip_index.networks.len()
            + self.ip_index.exact_v4.len()
            + self.ip_index.exact_v6.len();

        let (mut port, mut process, mut geoip, mut ruleset, mut composite) = (0usize, 0, 0, 0, 0);
        let (mut proxy_r, mut direct_r, mut reject_r) = (0usize, 0, 0);

        for rule in &self.slow_rules {
            match &rule.action {
                RuleAction::Proxy | RuleAction::Named(_) => proxy_r += 1,
                RuleAction::Direct => direct_r += 1,
                RuleAction::Reject => reject_r += 1,
            }
            match &rule.rule_type {
                RuleType::GeoLocation(_) => geoip += 1,
                RuleType::DestPort(_) | RuleType::SrcPort(_) => port += 1,
                RuleType::ProcessName(_) | RuleType::ProcessPath(_) => process += 1,
                RuleType::RuleSet(_) => ruleset += 1,
                RuleType::And(_) | RuleType::Or(_) | RuleType::Not(_) => composite += 1,
                _ => {}
            }
        }

        let (loaded_sets, total_set_rules) = {
            let sets = self.rule_sets.load();
            (sets.len(), sets.values().map(|s| s.rule_count).sum())
        };

        RouterStats {
            total_rules: domain + ip + self.slow_rules.len(),
            domain_rules: domain,
            ip_rules: ip,
            port_rules: port,
            process_rules: process,
            geoip_rules: geoip,
            ruleset_refs: ruleset,
            composite_rules: composite,
            loaded_rule_sets: loaded_sets,
            total_rule_set_rules: total_set_rules,
            proxy_rules: proxy_r,
            direct_rules: direct_r,
            reject_rules: reject_r,
            default_action: self.default_action.clone(),
        }
    }
}

// ── 工具函数 ──────────────────────────────────────────────────────────────────

fn is_private_ip(ip: &IpAddr) -> bool {
    match ip {
        IpAddr::V4(v4) => {
            // 按照 RFC 规范，涵盖所有非公网段
            v4.is_loopback()
                || v4.is_private()
                || v4.is_link_local()
                || v4.is_broadcast()
                || v4.is_documentation()
                || v4.is_unspecified()
                || v4.is_multicast()
        }
        IpAddr::V6(v6) => {
            v6.is_loopback() || v6.is_unspecified() || v6.is_multicast() || v6.is_unique_local()
        }
    }
}

// ── RouterStats ───────────────────────────────────────────────────────────────

#[derive(Debug)]
pub struct RouterStats {
    pub total_rules: usize,
    pub domain_rules: usize,
    pub ip_rules: usize,
    pub port_rules: usize,
    pub process_rules: usize,
    pub geoip_rules: usize,
    pub ruleset_refs: usize,
    pub composite_rules: usize,
    pub loaded_rule_sets: usize,
    pub total_rule_set_rules: usize,
    pub proxy_rules: usize,
    pub direct_rules: usize,
    pub reject_rules: usize,
    pub default_action: RuleAction,
}
