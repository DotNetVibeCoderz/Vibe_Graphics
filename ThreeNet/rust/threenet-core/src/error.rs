use thiserror::Error;

#[derive(Debug, Error)]
pub enum Error {
    #[error("no compatible GPU adapter was found")]
    NoAdapter,
    #[error("failed to create the GPU device: {0}")]
    Device(String),
    #[error("failed to create a rendering surface: {0}")]
    Surface(String),
    #[error("invalid handle or identifier: {0}")]
    InvalidHandle(&'static str),
    #[error("invalid argument: {0}")]
    InvalidArgument(String),
    #[error("asset error: {0}")]
    Asset(String),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
}

pub type Result<T> = std::result::Result<T, Error>;
