# zkswss

基于zks协议修改，同时删除CLI核心中多余（用不到）的功能，zks + ws + tls（无ECH），超低请求数，长连接。

协议来源：https://github.com/zks-protocol/zks-vpn

<img src="images\zks-gui.png" />

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

