use std::error::Error;
use std::fmt;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

/// Loads raw fixtures from one explicitly declared upstream root.
#[derive(Clone, Debug)]
pub struct FixtureLoader {
    root: PathBuf,
}

impl FixtureLoader {
    /// Opens and canonicalizes an upstream fixture directory.
    ///
    /// # Errors
    ///
    /// Returns an error when the root is unavailable or is not a directory.
    pub fn new(root: impl AsRef<Path>) -> Result<Self, FixtureError> {
        let requested = root.as_ref().to_path_buf();
        let canonical =
            fs::canonicalize(&requested).map_err(|source| FixtureError::RootUnavailable {
                path: requested.clone(),
                source,
            })?;
        if !canonical.is_dir() {
            return Err(FixtureError::RootNotDirectory { path: canonical });
        }
        Ok(Self { root: canonical })
    }

    /// Loads raw bytes without parsing or redefining the upstream schema.
    ///
    /// # Errors
    ///
    /// Rejects non-relative paths, traversal, symbolic-link escapes,
    /// directories, unavailable fixtures, and read failures.
    pub fn load(&self, relative_path: impl AsRef<Path>) -> Result<LoadedFixture, FixtureError> {
        let relative_path = relative_path.as_ref();
        if relative_path.as_os_str().is_empty()
            || relative_path.is_absolute()
            || !relative_path
                .components()
                .all(|component| matches!(component, Component::Normal(_)))
        {
            return Err(FixtureError::InvalidRelativePath {
                path: relative_path.to_path_buf(),
            });
        }

        let candidate = self.root.join(relative_path);
        let source_path =
            fs::canonicalize(&candidate).map_err(|source| FixtureError::FixtureUnavailable {
                path: relative_path.to_path_buf(),
                source,
            })?;
        if !source_path.starts_with(&self.root) {
            return Err(FixtureError::EscapesRoot {
                path: relative_path.to_path_buf(),
            });
        }
        if !source_path.is_file() {
            return Err(FixtureError::NotAFile {
                path: relative_path.to_path_buf(),
            });
        }

        let bytes = fs::read(&source_path).map_err(|source| FixtureError::ReadFailed {
            path: relative_path.to_path_buf(),
            source,
        })?;
        Ok(LoadedFixture {
            relative_path: relative_path.to_path_buf(),
            source_path,
            bytes,
        })
    }
}

/// Raw fixture content with enough provenance for test failure messages.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LoadedFixture {
    relative_path: PathBuf,
    source_path: PathBuf,
    bytes: Vec<u8>,
}

impl LoadedFixture {
    /// Returns the fixture path relative to the declared upstream root.
    #[must_use]
    pub fn relative_path(&self) -> &Path {
        &self.relative_path
    }

    /// Returns the canonical source path used for this load.
    #[must_use]
    pub fn source_path(&self) -> &Path {
        &self.source_path
    }

    /// Borrows the raw upstream bytes.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    /// Transfers the raw bytes to the caller's generated contract parser.
    #[must_use]
    pub fn into_bytes(self) -> Vec<u8> {
        self.bytes
    }
}

/// A fixture-root or fixture-load failure.
#[derive(Debug)]
pub enum FixtureError {
    /// The declared fixture root could not be canonicalized.
    RootUnavailable { path: PathBuf, source: io::Error },
    /// The declared fixture root is not a directory.
    RootNotDirectory { path: PathBuf },
    /// The requested path is absolute, empty, or contains traversal components.
    InvalidRelativePath { path: PathBuf },
    /// The requested fixture does not exist or cannot be canonicalized.
    FixtureUnavailable { path: PathBuf, source: io::Error },
    /// A symbolic link resolved outside the declared fixture root.
    EscapesRoot { path: PathBuf },
    /// The requested fixture resolves to something other than a file.
    NotAFile { path: PathBuf },
    /// The fixture was located but its raw bytes could not be read.
    ReadFailed { path: PathBuf, source: io::Error },
}

impl fmt::Display for FixtureError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::RootUnavailable { path, .. } => {
                write!(formatter, "fixture root is unavailable: {}", path.display())
            }
            Self::RootNotDirectory { path } => {
                write!(
                    formatter,
                    "fixture root is not a directory: {}",
                    path.display()
                )
            }
            Self::InvalidRelativePath { path } => {
                write!(
                    formatter,
                    "fixture path must remain relative: {}",
                    path.display()
                )
            }
            Self::FixtureUnavailable { path, .. } => {
                write!(formatter, "fixture is unavailable: {}", path.display())
            }
            Self::EscapesRoot { path } => {
                write!(
                    formatter,
                    "fixture escapes declared root: {}",
                    path.display()
                )
            }
            Self::NotAFile { path } => {
                write!(formatter, "fixture is not a file: {}", path.display())
            }
            Self::ReadFailed { path, .. } => {
                write!(formatter, "fixture could not be read: {}", path.display())
            }
        }
    }
}

impl Error for FixtureError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::RootUnavailable { source, .. }
            | Self::FixtureUnavailable { source, .. }
            | Self::ReadFailed { source, .. } => Some(source),
            Self::RootNotDirectory { .. }
            | Self::InvalidRelativePath { .. }
            | Self::EscapesRoot { .. }
            | Self::NotAFile { .. } => None,
        }
    }
}
