use clap::{Parser, ValueEnum};

#[derive(ValueEnum, Clone, Debug, PartialEq, Eq)]
pub enum Mode {
    #[value(name = "socks5")]
    Socks5,
    #[value(name = "http")]
    Http,
    #[value(name = "hybrid")]
    Hybrid,
}

impl Mode {
    pub fn display_name(&self) -> &'static str {
        match self {
            Mode::Socks5 => "SOCKS5",
            Mode::Http => "HTTP",
            Mode::Hybrid => "Hybrid",
        }
    }
}

#[derive(Parser, Debug, Clone)]
#[command(name = "ZKS Worker客户端内核")]
#[command(author = "原GitHub作者@zks-protocol")]
#[command(version = "0.1.1")]
#[command(about = "连接到ZKS Worker的代理客户端内核", long_about = None)]
pub struct Args {
    /// worker地址，例如：wss://worker.username.workers.dev/session
    #[arg(short, long)]
    pub worker: String,

    /// 本地代理模式: hybrid (http+socks5)、http、socks5
    #[arg(short, long, value_enum, default_value_t = Mode::Hybrid)]
    pub mode: Mode,

    /// 绑定的本机端口
    #[arg(short, long, default_value_t = 1080)]
    pub port: u16,

    /// 绑定的本机地址，通常是回环地址
    #[arg(short, long, default_value = "127.0.0.1")]
    pub bind: String,

    /// 是否开启打印详细日志模式
    #[arg(short, long)]
    pub verbose: bool,

    /// Worker的身份验证秘密(与服务器的AUTH_TOKEN共享)
    #[arg(long)]
    pub auth_token: Option<String>,

    /// (可选)等同ProxyIP，CF不允许访问的网站，就靠它连接
    #[arg(long)]
    pub backup_addrs: Option<String>,

    /// (可选)路由规则文件(类clash规则，支持DOMAIN, DOMAIN-SUFFIX, IP-CIDR, IP-CIDR6, GEOIP, RULE-SET)
    #[arg(long, default_value = "rules.yaml")]
    pub rules_file: Option<String>,

    /// (可选)CF优选地址，支持域名，IPv4, IPv4 CIDR，多个值用逗号隔开
    #[arg(long, default_value = "r2.dev")]
    pub cf_ip: Option<String>,
}
