# zkswss

基于zks协议修改，同时删除CLI核心中多余（用不到）的功能，zks + ws + tls（无ECH），超低请求数，长连接。

<img src="images\zks-gui.png" />


## zks-core命令行工具（内核）

> zks-core.exe -h

```
Usage: zks-core.exe [OPTIONS] --worker <WORKER>

Options:
  -w, --worker <WORKER>              worker地址，例如：wss://worker.username.workers.dev/session
  -m, --mode <MODE>                  本地代理模式: hybrid (http+socks5)、http、socks5 [default: hybrid] [possible values: socks5, http, hybrid]
  -p, --port <PORT>                  绑定的本机端口 [default: 1080]
  -b, --bind <BIND>                  绑定的本机地址，通常是回环地址 [default: 127.0.0.1]
  -v, --verbose                      是否开启打印详细日志模式
      --auth-token <AUTH_TOKEN>      Worker的身份验证秘密(与服务器的AUTH_TOKEN共享)
      --backup-addrs <BACKUP_ADDRS>  (可选)等同ProxyIP，CF不允许访问的网站，就靠它连接
      --rules-file <RULES_FILE>      (可选)路由规则文件(类clash规则，支持DOMAIN, DOMAIN-SUFFIX, IP-CIDR, IP-CIDR6, GEOIP, RULE-SET) [default: rules.yaml]
      --cf-ip <CF_IP>                (可选)CF优选地址，支持域名，IPv4, IPv4 CIDR，多个值用逗号隔开 [default: r2.dev]
  -h, --help                         Print help
  -V, --version                      Print version
```

## 关于ZKS

**ZKS (Zero-Knowledge Swarm，零知识群蜂)** 是一种下一代通信协议，旨在泛在监控和审查时代提供隐蔽且不可破解的隐私保护。

与容易被深度数据包检测 (DPI) 识别的传统 VPN 不同，ZKS 采用了以下技术：

- **Wasif-Vernam 密码**：一种新型、高性能的基于 XOR（异或）的流密码，并具有强制性的密钥轮换机制。
- **熵税 (Entropy Tax)**：一种用于生成稳健随机数的去中心化机制。
- **群蜂拓扑 (Swarm Topology)**：一种点对点 (P2P) 网状网络，其中每个节点都可以充当客户端、中继或出口。
- **协议拟态 (Protocol Mimicry)**：使流量特征与合法的 HTTPS/TLS 流量完全一致，难以区分。

> 这是原作者的介绍的，本项目不是完全体，有的东西没有用到，没有那么厉害，不要指望提供完好的隐蔽且不可破解的隐私保护。
>
> 作者主页：https://github.com/cswasif
>
> 协议主页：https://github.com/zks-protocol
>
> 代码摘取：https://github.com/zks-protocol/zks-vpn

