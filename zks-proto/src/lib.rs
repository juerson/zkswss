//! ZKS Protocol Definitions
//!
//! This crate defines the binary protocol for communication between
//! the ZKS client and worker.

mod error;
mod message;

pub use error::*;
pub use message::*;
