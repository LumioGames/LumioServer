//! Loopback WebSocket Room wire. Bytes on the socket are Runtime/C-1 JSON.

use std::net::{TcpListener, TcpStream};
use std::thread;
use std::time::Duration;

use lumio_host_runtime::{bounded_channel, spawn_supervised, CancelToken, Sender, SupervisedTask};
use serde_json::Value;
use tokio_tungstenite::tungstenite::protocol::WebSocket;
use tokio_tungstenite::tungstenite::stream::MaybeTlsStream;
use tokio_tungstenite::tungstenite::{accept, client::connect as ws_connect, Message};

/// Egress to one accepted socket.
#[derive(Clone)]
pub struct WireSender {
    inner: Sender<WireOut>,
}

#[derive(Debug, Clone)]
pub enum WireOut {
    Text(String),
    Close,
}

impl WireSender {
    #[must_use]
    pub fn send_text(&self, text: String) -> bool {
        self.inner.send(WireOut::Text(text)).is_ok()
    }

    #[must_use]
    pub fn close(&self) -> bool {
        self.inner.send(WireOut::Close).is_ok()
    }
}

/// Owner-thread notice from a socket.
pub enum WireEvent {
    Attached {
        connection_id: String,
        egress: WireSender,
    },
    Input {
        connection_id: String,
        text: String,
    },
    Closed {
        connection_id: String,
    },
}

/// Loopback listener. Handshake is blocking; frames are polled.
pub struct RoomListener {
    pub port: u16,
    _accept: SupervisedTask,
}

impl RoomListener {
    /// Binds `127.0.0.1:0` and accepts connections on a supervised thread.
    pub fn bind(event_tx: Sender<WireEvent>) -> Result<Self, String> {
        let listener = TcpListener::bind("127.0.0.1:0")
            .map_err(|error| format!("BLOCKED: room wire bind: {error}"))?;
        let port = listener
            .local_addr()
            .map_err(|error| format!("BLOCKED: room wire addr: {error}"))?
            .port();
        listener
            .set_nonblocking(true)
            .map_err(|error| format!("BLOCKED: room wire nonblocking: {error}"))?;
        let accept = spawn_supervised("lumio-entity-chat-wire-accept", move |cancel| {
            let mut conns = Vec::new();
            loop {
                if cancel.is_cancelled() {
                    break;
                }
                match listener.accept() {
                    Ok((stream, _)) => {
                        let tx = event_tx.clone();
                        conns.push(spawn_supervised(
                            "lumio-entity-chat-wire-conn",
                            move |conn_cancel| {
                                handle_conn(stream, tx, conn_cancel);
                            },
                        ));
                    }
                    Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(10));
                    }
                    Err(_) => break,
                }
            }
        });
        Ok(Self {
            port,
            _accept: accept,
        })
    }

    #[must_use]
    pub fn uri(&self) -> String {
        format!("ws://127.0.0.1:{}", self.port)
    }
}

fn handle_conn(stream: TcpStream, event_tx: Sender<WireEvent>, cancel: CancelToken) {
    if stream.set_nonblocking(false).is_err() {
        return;
    }
    let mut ws = match accept(stream) {
        Ok(ws) => ws,
        Err(_) => return,
    };
    let first = match ws.read() {
        Ok(Message::Text(text)) => text,
        _ => return,
    };
    let connection_id = match parse_connection_id(&first) {
        Some(id) => id,
        None => return,
    };
    let (out_tx, out_rx) = bounded_channel(64);
    let egress = WireSender { inner: out_tx };
    if event_tx
        .send(WireEvent::Attached {
            connection_id: connection_id.clone(),
            egress,
        })
        .is_err()
    {
        return;
    }
    if ws.get_mut().set_nonblocking(true).is_err() {
        return;
    }
    loop {
        if cancel.is_cancelled() {
            break;
        }
        match out_rx.try_recv() {
            Ok(WireOut::Text(text)) => {
                if ws.send(Message::Text(text.into())).is_err() {
                    break;
                }
            }
            Ok(WireOut::Close) => {
                let _ = ws.close(None);
                break;
            }
            Err(_) => {}
        }
        match ws.read() {
            Ok(Message::Text(text)) => {
                if event_tx
                    .send(WireEvent::Input {
                        connection_id: connection_id.clone(),
                        text: text.to_string(),
                    })
                    .is_err()
                {
                    break;
                }
            }
            Ok(Message::Close(_)) => break,
            Ok(Message::Ping(payload)) => {
                let _ = ws.send(Message::Pong(payload));
            }
            Ok(_) => {}
            Err(error) if is_would_block(&error) => {}
            Err(_) => break,
        }
        thread::sleep(Duration::from_millis(5));
    }
    let _ = event_tx.send(WireEvent::Closed { connection_id });
}

fn is_would_block(error: &tokio_tungstenite::tungstenite::Error) -> bool {
    matches!(
        error,
        tokio_tungstenite::tungstenite::Error::Io(io) if io.kind() == std::io::ErrorKind::WouldBlock
            || io.kind() == std::io::ErrorKind::TimedOut
    )
}

fn parse_connection_id(text: &str) -> Option<String> {
    let value: Value = serde_json::from_str(text).ok()?;
    value
        .get("connectionId")
        .and_then(Value::as_str)
        .filter(|id| !id.is_empty())
        .map(str::to_owned)
}

/// Test/harness client that actually receives frames.
pub struct RoomClient {
    ws: WebSocket<MaybeTlsStream<TcpStream>>,
    pub received: Vec<String>,
}

impl RoomClient {
    /// Connects and attaches as `connection_id`.
    ///
    /// # Errors
    ///
    /// Returns a human-readable failure.
    pub fn connect(uri: &str, connection_id: &str) -> Result<Self, String> {
        let url = format!("{uri}/");
        let (mut ws, _) =
            ws_connect(url).map_err(|error| format!("room client connect: {error}"))?;
        match ws.get_mut() {
            MaybeTlsStream::Plain(stream) => {
                stream
                    .set_read_timeout(Some(Duration::from_secs(3)))
                    .map_err(|error| format!("room client timeout: {error}"))?;
            }
            _ => {}
        }
        let hello = serde_json::json!({ "connectionId": connection_id }).to_string();
        ws.send(Message::Text(hello.into()))
            .map_err(|error| format!("room client hello: {error}"))?;
        Ok(Self {
            ws,
            received: Vec::new(),
        })
    }

    /// Reads one text frame.
    ///
    /// # Errors
    ///
    /// Returns when the socket closes or times out.
    pub fn recv_text(&mut self) -> Result<String, String> {
        match self.ws.read() {
            Ok(Message::Text(text)) => {
                let owned = text.to_string();
                self.received.push(owned.clone());
                Ok(owned)
            }
            Ok(Message::Close(_)) => Err("closed".to_owned()),
            Ok(other) => Err(format!("unexpected {other}")),
            Err(error) => Err(error.to_string()),
        }
    }

    /// Sends a C-1 InputCommand (or other JSON) text frame.
    ///
    /// # Errors
    ///
    /// Returns a send failure.
    pub fn send_text(&mut self, text: &str) -> Result<(), String> {
        self.ws
            .send(Message::Text(text.to_owned().into()))
            .map_err(|error| error.to_string())
    }

    /// True when a later `recv_text` observes close.
    #[must_use]
    pub fn is_closed_after(&mut self) -> bool {
        matches!(self.ws.read(), Ok(Message::Close(_)) | Err(_))
    }
}
