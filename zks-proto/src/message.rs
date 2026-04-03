//! ZKS Protocol Messages
//!
//! Binary protocol for efficient tunneling:
//! - CONNECT: Request to open a TCP connection to a target (with auth & backup)
//! - DATA: Tunneled data (ZKS-encrypted payload)
//! - CLOSE: Close a stream
//! - ERROR: Error response

use bytes::{Buf, BufMut, Bytes, BytesMut};
use std::io::Cursor;

/// Maximum size of a single frame (1MB)
pub const MAX_FRAME_SIZE: usize = 1024 * 1024;

/// Maximum number of backup addresses
pub const MAX_BACKUP_ADDRS: usize = 8;

/// Backup address for failover
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct BackupAddr {
    /// Backup host
    pub host: String,
    /// Backup port
    pub port: u16,
}

/// NAT signaling message for NAT traversal coordination
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub enum NatSignalingMessage {
    /// NAT type information exchange
    NatInfo {
        delta_type: String,
        avg_delta: f64,
        last_port: u16,
    },
    /// Port prediction coordination
    PortPrediction {
        ports: Vec<u16>,
        nat_type: String,
        timeout_ms: u64,
    },
    /// Birthday attack coordination
    BirthdayAttack {
        start_port: u16,
        end_port: u16,
        listen_count: u16,
    },
}

/// Command types for the tunnel protocol
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommandType {
    /// Request to connect to a target address (TCP)
    Connect = 0x01,
    /// Data frame (bidirectional, for TCP streams)
    Data = 0x02,
    /// Close a stream
    Close = 0x03,
    /// Error response
    ErrorReply = 0x04,
    /// Ping/keepalive
    Ping = 0x05,
    /// Pong response
    Pong = 0x06,
    /// UDP datagram (stateless)
    UdpDatagram = 0x07,
    /// DNS query (special handling via DoH)
    DnsQuery = 0x08,
    /// DNS response
    DnsResponse = 0x09,
    /// Connection established successfully
    ConnectSuccess = 0x0A,
    /// HTTP Request (for fetch-based forwarding)
    HttpRequest = 0x0B,
    /// HTTP Response (from fetch)
    HttpResponse = 0x0C,
    /// Forward encrypted payload to next hop (multi-hop chaining)
    ChainForward = 0x10,
    /// Acknowledgement from next hop
    ChainAck = 0x11,
    /// Raw IP packet for VPN mode (layer 3) - encrypted with ZKS keys
    IpPacket = 0x20,
    /// Batch of IP packets for high-throughput VPN mode (reduces WebSocket overhead)
    BatchIpPacket = 0x21,
    /// Control message (raw JSON/Text) - internal use, not sent on wire as binary
    Control = 0x30,
    /// NAT signaling message for NAT traversal coordination
    NatSignaling = 0x31,
}

impl TryFrom<u8> for CommandType {
    type Error = crate::ProtoError;

    fn try_from(value: u8) -> Result<Self, Self::Error> {
        match value {
            0x01 => Ok(Self::Connect),
            0x02 => Ok(Self::Data),
            0x03 => Ok(Self::Close),
            0x04 => Ok(Self::ErrorReply),
            0x05 => Ok(Self::Ping),
            0x06 => Ok(Self::Pong),
            0x07 => Ok(Self::UdpDatagram),
            0x08 => Ok(Self::DnsQuery),
            0x09 => Ok(Self::DnsResponse),
            0x0A => Ok(Self::ConnectSuccess),
            0x0B => Ok(Self::HttpRequest),
            0x0C => Ok(Self::HttpResponse),
            0x10 => Ok(Self::ChainForward),
            0x11 => Ok(Self::ChainAck),
            0x20 => Ok(Self::IpPacket),
            0x21 => Ok(Self::BatchIpPacket),
            0x30 => Ok(Self::Control),
            _ => Err(crate::ProtoError::InvalidCommand(value)),
        }
    }
}

/// Stream identifier for multiplexing connections
pub type StreamId = u32;

/// Protocol message types
#[derive(Debug, Clone)]
pub enum ZksMessage {
    /// Connect to target: hostname:port (TCP)
    /// Extended to support authentication and backup addresses
    Connect {
        stream_id: StreamId,
        host: String,
        port: u16,
        /// Authentication token (encrypted in frame, optional)
        auth_token: Option<String>,
        /// Backup addresses for failover (encrypted in frame, optional)
        backup_addrs: Vec<BackupAddr>,
    },
    /// Data payload for a stream (TCP)
    Data { stream_id: StreamId, payload: Bytes },
    /// Close a stream
    Close { stream_id: StreamId },
    /// Error on a stream
    ErrorReply {
        stream_id: StreamId,
        code: u16,
        message: String,
    },
    /// Ping
    Ping,
    /// Pong
    Pong,
    /// UDP datagram (stateless, no connection tracking)
    /// Used for general UDP traffic
    UdpDatagram {
        /// Request ID for matching responses
        request_id: u32,
        /// Destination host
        host: String,
        /// Destination port
        port: u16,
        /// Payload data
        payload: Bytes,
    },
    /// DNS query - will be resolved via DoH
    DnsQuery {
        /// Request ID for matching responses
        request_id: u32,
        /// Raw DNS query packet
        query: Bytes,
    },
    /// DNS response
    DnsResponse {
        /// Request ID matching the query
        request_id: u32,
        /// Raw DNS response packet
        response: Bytes,
    },
    /// Connection established successfully
    ConnectSuccess { stream_id: StreamId },
    /// HTTP Request for fetch-based forwarding (bypasses Cloudflare connect() limitations)
    /// Worker will use fetch() API to make the request
    HttpRequest {
        /// Stream ID for response correlation
        stream_id: StreamId,
        /// HTTP method (GET, POST, etc.)
        method: String,
        /// Full URL (e.g., "https://google.com/path")
        url: String,
        /// Request headers as "Key: Value\r\n" concatenated
        headers: String,
        /// Optional request body
        body: Bytes,
    },
    /// HTTP Response from fetch
    HttpResponse {
        /// Stream ID matching the request
        stream_id: StreamId,
        /// HTTP status code
        status: u16,
        /// Response headers as "Key: Value\r\n" concatenated
        headers: String,
        /// Response body
        body: Bytes,
    },
    /// Forward encrypted payload to next hop (ZKS-over-ZKS chaining)
    /// The payload is already encrypted for the next hop
    ChainForward {
        /// Chain ID for tracking multi-hop sessions
        chain_id: u32,
        /// Room ID of the next hop's relay
        next_room: String,
        /// Already-encrypted payload for the next hop
        payload: Bytes,
    },
    /// Acknowledgement from a hop in the chain
    ChainAck {
        /// Chain ID matching the forward
        chain_id: u32,
        /// Success or error
        success: bool,
        /// Optional message
        message: String,
    },
    /// Raw IP packet for VPN mode (ZKS-encrypted layer 3 payload)
    /// Used by P2P VPN to tunnel all system traffic through Exit Peer
    IpPacket {
        /// Full IP packet payload (encrypted with ZKS keys before wire)
        payload: Bytes,
    },
    /// Batch of IP packets for high-throughput VPN mode
    /// Reduces WebSocket message overhead by sending multiple packets in one message
    BatchIpPacket {
        /// Multiple IP packet payloads
        packets: Vec<Bytes>,
    },
    /// Control message (raw JSON/Text)
    /// Used to pass non-tunnel messages (like Swarm Entropy events) up from the relay
    Control { message: String },
    /// NAT signaling message for NAT traversal coordination
    /// Used for port prediction and birthday attack coordination
    NatSignaling(NatSignalingMessage),
}

impl ZksMessage {
    /// Encode message to binary format
    ///
    /// Format:
    /// - CONNECT:      [cmd:1][stream_id:4][port:2][host_len:2][host:N][auth_len:2][auth:N][backup_count:1][backup_addrs...]
    /// - DATA:         [cmd:1][stream_id:4][payload_len:4][payload:N]
    /// - CLOSE:        [cmd:1][stream_id:4]
    /// - ERROR:        [cmd:1][stream_id:4][code:2][msg_len:2][msg:N]
    /// - PING:         [cmd:1]
    /// - PONG:         [cmd:1]
    /// - UDP_DATAGRAM: [cmd:1][request_id:4][port:2][host_len:2][host:N][payload_len:4][payload:N]
    /// - DNS_QUERY:    [cmd:1][request_id:4][query_len:4][query:N]
    /// - DNS_RESPONSE: [cmd:1][request_id:4][response_len:4][response:N]
    /// - CONNECT_SUCCESS: [cmd:1][stream_id:4]
    pub fn encode(&self) -> Bytes {
        let mut buf = BytesMut::with_capacity(256);

        match self {
            ZksMessage::Connect {
                stream_id,
                host,
                port,
                auth_token,
                backup_addrs,
            } => {
                buf.put_u8(CommandType::Connect as u8);
                buf.put_u32(*stream_id);
                buf.put_u16(*port);
                buf.put_u16(host.len() as u16);
                buf.put_slice(host.as_bytes());

                // Encode auth token (optional)
                let auth_bytes = auth_token.as_ref().map(|s| s.as_bytes()).unwrap_or(b"");
                buf.put_u16(auth_bytes.len() as u16);
                if !auth_bytes.is_empty() {
                    buf.put_slice(auth_bytes);
                }

                // Encode backup addresses (optional, max 8)
                let backup_count = backup_addrs.len().min(MAX_BACKUP_ADDRS) as u8;
                buf.put_u8(backup_count);
                for addr in backup_addrs.iter().take(MAX_BACKUP_ADDRS) {
                    buf.put_u16(addr.host.len() as u16);
                    buf.put_slice(addr.host.as_bytes());
                    buf.put_u16(addr.port);
                }
            }
            ZksMessage::Data { stream_id, payload } => {
                buf.put_u8(CommandType::Data as u8);
                buf.put_u32(*stream_id);
                buf.put_u32(payload.len() as u32);
                buf.put_slice(payload);
            }
            ZksMessage::Close { stream_id } => {
                buf.put_u8(CommandType::Close as u8);
                buf.put_u32(*stream_id);
            }
            ZksMessage::ErrorReply {
                stream_id,
                code,
                message,
            } => {
                buf.put_u8(CommandType::ErrorReply as u8);
                buf.put_u32(*stream_id);
                buf.put_u16(*code);
                buf.put_u16(message.len() as u16);
                buf.put_slice(message.as_bytes());
            }
            ZksMessage::Ping => {
                buf.put_u8(CommandType::Ping as u8);
            }
            ZksMessage::Pong => {
                buf.put_u8(CommandType::Pong as u8);
            }
            ZksMessage::UdpDatagram {
                request_id,
                host,
                port,
                payload,
            } => {
                buf.put_u8(CommandType::UdpDatagram as u8);
                buf.put_u32(*request_id);
                buf.put_u16(*port);
                buf.put_u16(host.len() as u16);
                buf.put_slice(host.as_bytes());
                buf.put_u32(payload.len() as u32);
                buf.put_slice(payload);
            }
            ZksMessage::DnsQuery { request_id, query } => {
                buf.put_u8(CommandType::DnsQuery as u8);
                buf.put_u32(*request_id);
                buf.put_u32(query.len() as u32);
                buf.put_slice(query);
            }
            ZksMessage::DnsResponse {
                request_id,
                response,
            } => {
                buf.put_u8(CommandType::DnsResponse as u8);
                buf.put_u32(*request_id);
                buf.put_u32(response.len() as u32);
                buf.put_slice(response);
            }
            ZksMessage::ConnectSuccess { stream_id } => {
                buf.put_u8(CommandType::ConnectSuccess as u8);
                buf.put_u32(*stream_id);
            }
            ZksMessage::HttpRequest {
                stream_id,
                method,
                url,
                headers,
                body,
            } => {
                buf.put_u8(CommandType::HttpRequest as u8);
                buf.put_u32(*stream_id);
                buf.put_u16(method.len() as u16);
                buf.put_slice(method.as_bytes());
                buf.put_u16(url.len() as u16);
                buf.put_slice(url.as_bytes());
                buf.put_u16(headers.len() as u16);
                buf.put_slice(headers.as_bytes());
                buf.put_u32(body.len() as u32);
                buf.put_slice(body);
            }
            ZksMessage::HttpResponse {
                stream_id,
                status,
                headers,
                body,
            } => {
                buf.put_u8(CommandType::HttpResponse as u8);
                buf.put_u32(*stream_id);
                buf.put_u16(*status);
                buf.put_u16(headers.len() as u16);
                buf.put_slice(headers.as_bytes());
                buf.put_u32(body.len() as u32);
                buf.put_slice(body);
            }
            ZksMessage::ChainForward {
                chain_id,
                next_room,
                payload,
            } => {
                buf.put_u8(CommandType::ChainForward as u8);
                buf.put_u32(*chain_id);
                buf.put_u16(next_room.len() as u16);
                buf.put_slice(next_room.as_bytes());
                buf.put_u32(payload.len() as u32);
                buf.put_slice(payload);
            }
            ZksMessage::ChainAck {
                chain_id,
                success,
                message,
            } => {
                buf.put_u8(CommandType::ChainAck as u8);
                buf.put_u32(*chain_id);
                buf.put_u8(if *success { 1 } else { 0 });
                buf.put_u16(message.len() as u16);
                buf.put_slice(message.as_bytes());
            }
            ZksMessage::IpPacket { payload } => {
                buf.put_u8(CommandType::IpPacket as u8);
                buf.put_u32(payload.len() as u32);
                buf.put_slice(payload);
            }

            ZksMessage::BatchIpPacket { packets } => {
                buf.put_u8(CommandType::BatchIpPacket as u8);
                buf.put_u16(packets.len() as u16);
                for p in packets {
                    buf.put_u32(p.len() as u32);
                    buf.put_slice(p);
                }
            }
            ZksMessage::NatSignaling(nat_msg) => {
                // NAT signaling messages are JSON-encoded
                match serde_json::to_string(nat_msg) {
                    Ok(json) => {
                        buf.put_u8(CommandType::NatSignaling as u8);
                        buf.put_u32(json.len() as u32);
                        buf.put_slice(json.as_bytes());
                    }
                    Err(e) => {
                        // Log error and skip this message
                        eprintln!("Failed to serialize NAT signaling message: {}", e);
                        return Bytes::new();
                    }
                }
            }
            ZksMessage::Control { message } => {
                // Control messages are internal and shouldn't be encoded as binary frames
                // But for completeness, we define a format
                buf.put_u8(CommandType::Control as u8);
                buf.put_u32(message.len() as u32);
                buf.put_slice(message.as_bytes());
            }
        }

        buf.freeze()
    }

    /// Decode message from binary format
    pub fn decode(data: &[u8]) -> Result<Self, crate::ProtoError> {
        if data.is_empty() {
            return Err(crate::ProtoError::EmptyMessage);
        }

        let mut cursor = Cursor::new(data);
        let cmd = CommandType::try_from(cursor.get_u8())?;

        match cmd {
            CommandType::Connect => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                let port = cursor.get_u16();
                let host_len = cursor.get_u16() as usize;

                if cursor.remaining() < host_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut host_bytes = vec![0u8; host_len];
                cursor.copy_to_slice(&mut host_bytes);
                let host =
                    String::from_utf8(host_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                // Decode auth token (optional)
                let auth_token = if cursor.remaining() >= 2 {
                    let auth_len = cursor.get_u16() as usize;
                    if cursor.remaining() >= auth_len {
                        let mut auth_bytes = vec![0u8; auth_len];
                        cursor.copy_to_slice(&mut auth_bytes);
                        Some(
                            String::from_utf8(auth_bytes)
                                .map_err(|_| crate::ProtoError::InvalidUtf8)?,
                        )
                    } else {
                        None
                    }
                } else {
                    None
                };

                // Decode backup addresses (optional)
                let backup_addrs = if cursor.remaining() >= 1 {
                    let backup_count = cursor.get_u8() as usize;
                    let mut addrs = Vec::with_capacity(backup_count.min(MAX_BACKUP_ADDRS));
                    for _ in 0..backup_count.min(MAX_BACKUP_ADDRS) {
                        if cursor.remaining() < 4 {
                            break;
                        }
                        let backup_host_len = cursor.get_u16() as usize;
                        if cursor.remaining() < backup_host_len + 2 {
                            break;
                        }
                        let mut backup_host_bytes = vec![0u8; backup_host_len];
                        cursor.copy_to_slice(&mut backup_host_bytes);
                        let backup_host = String::from_utf8(backup_host_bytes)
                            .map_err(|_| crate::ProtoError::InvalidUtf8)?;
                        let backup_port = cursor.get_u16();
                        addrs.push(BackupAddr {
                            host: backup_host,
                            port: backup_port,
                        });
                    }
                    addrs
                } else {
                    Vec::new()
                };

                Ok(ZksMessage::Connect {
                    stream_id,
                    host,
                    port,
                    auth_token,
                    backup_addrs,
                })
            }
            CommandType::Data => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                let payload_len = cursor.get_u32() as usize;

                if cursor.remaining() < payload_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..payload_len]);

                Ok(ZksMessage::Data { stream_id, payload })
            }
            CommandType::Close => {
                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                Ok(ZksMessage::Close { stream_id })
            }
            CommandType::ErrorReply => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                let code = cursor.get_u16();
                let msg_len = cursor.get_u16() as usize;

                if cursor.remaining() < msg_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut msg_bytes = vec![0u8; msg_len];
                cursor.copy_to_slice(&mut msg_bytes);
                let message =
                    String::from_utf8(msg_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                Ok(ZksMessage::ErrorReply {
                    stream_id,
                    code,
                    message,
                })
            }
            CommandType::Ping => Ok(ZksMessage::Ping),
            CommandType::Pong => Ok(ZksMessage::Pong),
            CommandType::UdpDatagram => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let request_id = cursor.get_u32();
                let port = cursor.get_u16();
                let host_len = cursor.get_u16() as usize;

                if cursor.remaining() < host_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut host_bytes = vec![0u8; host_len];
                cursor.copy_to_slice(&mut host_bytes);
                let host =
                    String::from_utf8(host_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload_len = cursor.get_u32() as usize;

                if cursor.remaining() < payload_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..payload_len]);

                Ok(ZksMessage::UdpDatagram {
                    request_id,
                    host,
                    port,
                    payload,
                })
            }
            CommandType::DnsQuery => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let request_id = cursor.get_u32();
                let query_len = cursor.get_u32() as usize;

                if cursor.remaining() < query_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let query =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..query_len]);

                Ok(ZksMessage::DnsQuery { request_id, query })
            }
            CommandType::DnsResponse => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let request_id = cursor.get_u32();
                let response_len = cursor.get_u32() as usize;

                if cursor.remaining() < response_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let response =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..response_len]);

                Ok(ZksMessage::DnsResponse {
                    request_id,
                    response,
                })
            }
            CommandType::ConnectSuccess => {
                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                Ok(ZksMessage::ConnectSuccess { stream_id })
            }
            CommandType::HttpRequest => {
                if cursor.remaining() < 6 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();

                let method_len = cursor.get_u16() as usize;
                if cursor.remaining() < method_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut method_bytes = vec![0u8; method_len];
                cursor.copy_to_slice(&mut method_bytes);
                let method =
                    String::from_utf8(method_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 2 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let url_len = cursor.get_u16() as usize;
                if cursor.remaining() < url_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut url_bytes = vec![0u8; url_len];
                cursor.copy_to_slice(&mut url_bytes);
                let url =
                    String::from_utf8(url_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 2 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let headers_len = cursor.get_u16() as usize;
                if cursor.remaining() < headers_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut headers_bytes = vec![0u8; headers_len];
                cursor.copy_to_slice(&mut headers_bytes);
                let headers =
                    String::from_utf8(headers_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let body_len = cursor.get_u32() as usize;
                if cursor.remaining() < body_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let body = Bytes::copy_from_slice(&data[cursor.position() as usize..][..body_len]);

                Ok(ZksMessage::HttpRequest {
                    stream_id,
                    method,
                    url,
                    headers,
                    body,
                })
            }
            CommandType::HttpResponse => {
                if cursor.remaining() < 8 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let stream_id = cursor.get_u32();
                let status = cursor.get_u16();

                let headers_len = cursor.get_u16() as usize;
                if cursor.remaining() < headers_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut headers_bytes = vec![0u8; headers_len];
                cursor.copy_to_slice(&mut headers_bytes);
                let headers =
                    String::from_utf8(headers_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let body_len = cursor.get_u32() as usize;
                if cursor.remaining() < body_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let body = Bytes::copy_from_slice(&data[cursor.position() as usize..][..body_len]);

                Ok(ZksMessage::HttpResponse {
                    stream_id,
                    status,
                    headers,
                    body,
                })
            }
            CommandType::ChainForward => {
                if cursor.remaining() < 6 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let chain_id = cursor.get_u32();
                let room_len = cursor.get_u16() as usize;

                if cursor.remaining() < room_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut room_bytes = vec![0u8; room_len];
                cursor.copy_to_slice(&mut room_bytes);
                let next_room =
                    String::from_utf8(room_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload_len = cursor.get_u32() as usize;
                if cursor.remaining() < payload_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..payload_len]);

                Ok(ZksMessage::ChainForward {
                    chain_id,
                    next_room,
                    payload,
                })
            }
            CommandType::ChainAck => {
                if cursor.remaining() < 7 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let chain_id = cursor.get_u32();
                let success = cursor.get_u8() != 0;
                let msg_len = cursor.get_u16() as usize;

                if cursor.remaining() < msg_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut msg_bytes = vec![0u8; msg_len];
                cursor.copy_to_slice(&mut msg_bytes);
                let message =
                    String::from_utf8(msg_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                Ok(ZksMessage::ChainAck {
                    chain_id,
                    success,
                    message,
                })
            }
            CommandType::IpPacket => {
                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload_len = cursor.get_u32() as usize;

                if cursor.remaining() < payload_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let payload =
                    Bytes::copy_from_slice(&data[cursor.position() as usize..][..payload_len]);

                Ok(ZksMessage::IpPacket { payload })
            }
            CommandType::BatchIpPacket => {
                if cursor.remaining() < 2 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let count = cursor.get_u16() as usize;
                let mut packets = Vec::with_capacity(count);

                for _ in 0..count {
                    if cursor.remaining() < 4 {
                        return Err(crate::ProtoError::InsufficientData);
                    }
                    let payload_len = cursor.get_u32() as usize;

                    if cursor.remaining() < payload_len {
                        return Err(crate::ProtoError::InsufficientData);
                    }
                    let payload =
                        Bytes::copy_from_slice(&data[cursor.position() as usize..][..payload_len]);
                    cursor.advance(payload_len);
                    packets.push(payload);
                }

                Ok(ZksMessage::BatchIpPacket { packets })
            }
            CommandType::NatSignaling => {
                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let msg_len = cursor.get_u32() as usize;
                if cursor.remaining() < msg_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut msg_bytes = vec![0u8; msg_len];
                cursor.copy_to_slice(&mut msg_bytes);
                let json_str =
                    String::from_utf8(msg_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;
                let nat_msg = serde_json::from_str::<NatSignalingMessage>(&json_str)
                    .map_err(|_| crate::ProtoError::InvalidCommand(0x31))?;

                Ok(ZksMessage::NatSignaling(nat_msg))
            }
            CommandType::Control => {
                if cursor.remaining() < 4 {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let msg_len = cursor.get_u32() as usize;
                if cursor.remaining() < msg_len {
                    return Err(crate::ProtoError::InsufficientData);
                }
                let mut msg_bytes = vec![0u8; msg_len];
                cursor.copy_to_slice(&mut msg_bytes);
                let message =
                    String::from_utf8(msg_bytes).map_err(|_| crate::ProtoError::InvalidUtf8)?;

                Ok(ZksMessage::Control { message })
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_connect_roundtrip() {
        let msg = ZksMessage::Connect {
            stream_id: 42,
            host: "google.com".to_string(),
            port: 443,
            auth_token: Some("jHLtV7ZAm8Vt8rZZX8Ey".to_string()),
            backup_addrs: vec![],
        };
        let encoded = msg.encode();
        let decoded = ZksMessage::decode(&encoded).unwrap();

        match decoded {
            ZksMessage::Connect {
                stream_id,
                host,
                port,
                auth_token,
                backup_addrs,
            } => {
                assert_eq!(stream_id, 42);
                assert_eq!(host, "google.com");
                assert_eq!(port, 443);
                assert_eq!(auth_token, Some("jHLtV7ZAm8Vt8rZZX8Ey".to_string()));
                assert_eq!(backup_addrs, vec![]);
            }
            _ => panic!("Wrong message type"),
        }
    }

    #[test]
    fn test_data_roundtrip() {
        let payload = Bytes::from("Hello, World!");
        let msg = ZksMessage::Data {
            stream_id: 1,
            payload: payload.clone(),
        };
        let encoded = msg.encode();
        let decoded = ZksMessage::decode(&encoded).unwrap();

        match decoded {
            ZksMessage::Data {
                stream_id,
                payload: p,
            } => {
                assert_eq!(stream_id, 1);
                assert_eq!(p, payload);
            }
            _ => panic!("Wrong message type"),
        }
    }
}
