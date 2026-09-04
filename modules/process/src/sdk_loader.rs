//! Hand-written FFI loader for the Lumio Engine native SDK.
//!
//! Loads the SDK DLL via `LoadLibraryW`/`GetProcAddress` (no `libloading`),
//! verifies the `build-info.json` sidecar against the binary (SHA-256) and the
//! root table (`abiHash`, `buildId`), then probes `ping`. Any mismatch is a
//! startup failure (exit code 1) naming the failed check.
//!
//! Root table layout: `engine/abi/native-abi.json` in the architecture repo
//! (commit cef0b03), including the `CoreCLR` host slots (`create_clr_host`,
//! `clr_host_call`, `destroy_clr_host`). The real DLL is exercised in the
//! integration phase.
#![allow(unsafe_code)] // FFI boundary: unsafe is the point of this module.

use std::ffi::c_void;
use std::fmt::{Display, Formatter};
use std::fs::File;
use std::io::Read;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

use crate::wire::BUILD_INFO_SIDECAR;

/// ABI version requested from `lumio_engine_get_api_v1`.
pub const ABI_VERSION: u32 = 1;
/// The only exported SDK symbol; everything else hangs off the root table.
#[cfg_attr(not(windows), allow(dead_code))]
pub const ENTRY_SYMBOL: &str = "lumio_engine_get_api_v1";

/// SDK status codes (architecture repo `engine/abi/native-abi.json`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum SdkStatus {
    /// Call succeeded.
    Success = 0,
    /// Null pointer / bad argument / bad entry spec.
    InvalidArgument = 1,
    /// Root table version rejected.
    UnsupportedVersion = 2,
    /// `create_clr_host` failed (rolled back).
    ClrInitFailed = 3,
    /// Managed entry returned a failure.
    ClrEntryFailed = 4,
    /// Output buffer too small; `bytes_written` carries the required size.
    BufferTooSmall = 5,
}

impl SdkStatus {
    /// Interpret a raw status word.
    #[must_use]
    pub const fn from_i32(value: i32) -> Option<Self> {
        match value {
            0 => Some(Self::Success),
            1 => Some(Self::InvalidArgument),
            2 => Some(Self::UnsupportedVersion),
            3 => Some(Self::ClrInitFailed),
            4 => Some(Self::ClrEntryFailed),
            5 => Some(Self::BufferTooSmall),
            _ => None,
        }
    }
}

/// Root API table returned by `lumio_engine_get_api_v1`.
///
/// Layout mirrors the architecture repo's `engine/abi/native-abi.json`
/// (x64: size 88, ping at 56, the CLR chain at 64/72/80).
#[repr(C)]
#[derive(Clone, Copy)]
pub(crate) struct RootApiV1 {
    /// ABI version the DLL was built for.
    pub abi_version: u32,
    /// `size_of::<RootApiV1>()` as seen by the DLL.
    pub struct_size: u32,
    /// Lowercase-hex SHA-256 of the ABI definition.
    pub abi_hash: [u8; 32],
    /// Build identifier bytes.
    pub build_id: [u8; 16],
    /// Liveness probe: writes 1 into `*mut u32` marker, returns status.
    pub ping: Option<unsafe extern "C" fn(*mut c_void) -> i32>,
    /// Creates the `CoreCLR` host; resolves the managed entry fail-fast.
    pub create_clr_host: Option<
        unsafe extern "C" fn(
            *const u8,        // hostfxr_path (UTF-8 NUL-terminated)
            *const u8,        // runtime_config_path
            *const u8,        // assembly_path
            *const u8,        // entry_spec: '<assembly-qualified type>;<method>'
            *mut *mut c_void, // out opaque handle
        ) -> i32,
    >,
    /// One byte-protocol call into the managed entry.
    pub clr_host_call: Option<
        unsafe extern "C" fn(
            *mut c_void, // host
            *const u8,   // input (null allowed when input_len is 0)
            u32,         // input_len
            *mut u8,     // output (null allowed when capacity is 0)
            u32,         // output_capacity
            *mut u32,    // out bytes_written
        ) -> i32,
    >,
    /// Destroys the `CoreCLR` host.
    pub destroy_clr_host: Option<unsafe extern "C" fn(*mut c_void) -> i32>,
}

/// Result of one `clr_host_call` FFI invocation.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct ClrCall {
    /// Raw status word (see [`SdkStatus`]).
    pub status: i32,
    /// Bytes written (`Success`) or required size (`BufferTooSmall`).
    pub written: u32,
}

#[cfg_attr(not(windows), allow(dead_code))]
type GetApiV1 = unsafe extern "C" fn(u32, *mut *const RootApiV1) -> i32;

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn LoadLibraryW(filename: *const u16) -> isize;
    fn GetProcAddress(module: isize, name: *const u8) -> *const c_void;
    fn FreeLibrary(module: isize) -> i32;
}

/// Sidecar contents (`build-info.json`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuildInfo {
    /// Hex build id (matches root table `build_id`).
    pub build_id: String,
    /// Hex ABI hash (matches root table `abi_hash`).
    pub abi_hash: String,
    /// Hex SHA-256 of the DLL file itself.
    pub binary_sha256: String,
}

/// Every way SDK loading can fail at startup.
#[derive(Debug)]
pub enum LoadError {
    /// DLL file missing.
    MissingFile(PathBuf),
    /// Sidecar file missing.
    MissingSidecar(PathBuf),
    /// Sidecar unparseable or missing a string field (field named inside).
    BadSidecar(String),
    /// DLL bytes do not hash to the sidecar `binarySha256`.
    BinaryHashMismatch {
        /// Computed hash of the loaded file.
        actual: String,
        /// Sidecar value.
        expected: String,
    },
    /// Root table `abi_hash` differs from the sidecar.
    AbiHashMismatch {
        /// Value read from the root table.
        actual: String,
        /// Sidecar value.
        expected: String,
    },
    /// Root table `build_id` differs from the sidecar.
    BuildIdMismatch {
        /// Value read from the root table.
        actual: String,
        /// Sidecar value.
        expected: String,
    },
    /// Root table shape/version/slots invalid (detail names the field).
    InvalidRootTable(String),
    /// Entry symbol rejected the requested ABI version.
    UnsupportedVersion(i32),
    /// `lumio_engine_get_api_v1` is not exported.
    EntryMissing,
    /// `LoadLibraryW` failed (bad image, missing deps, ...).
    LibraryLoadFailed(PathBuf),
    /// `ping` probe failed (status or marker mismatch).
    PingFailed(String),
    /// Native SDK loader is Windows-only (kernel32).
    UnsupportedPlatform,
}

impl Display for LoadError {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::MissingFile(path) => write!(f, "native SDK file missing: {}", path.display()),
            Self::MissingSidecar(path) => {
                write!(f, "build-info sidecar missing: {}", path.display())
            }
            Self::BadSidecar(detail) => write!(f, "build-info sidecar invalid: {detail}"),
            Self::BinaryHashMismatch { actual, expected } => write!(
                f,
                "binarySha256 mismatch: file hashes to {actual}, sidecar says {expected}"
            ),
            Self::AbiHashMismatch { actual, expected } => write!(
                f,
                "abiHash mismatch: root table reports {actual}, sidecar says {expected}"
            ),
            Self::BuildIdMismatch { actual, expected } => write!(
                f,
                "buildId mismatch: root table reports {actual}, sidecar says {expected}"
            ),
            Self::InvalidRootTable(detail) => write!(f, "root API table invalid: {detail}"),
            Self::UnsupportedVersion(status) => {
                write!(
                    f,
                    "entry symbol rejected ABI version {ABI_VERSION} (status {status})"
                )
            }
            Self::EntryMissing => write!(f, "export `{ENTRY_SYMBOL}` not found in DLL"),
            Self::LibraryLoadFailed(path) => write!(f, "LoadLibraryW failed: {}", path.display()),
            Self::PingFailed(detail) => write!(f, "SDK ping failed: {detail}"),
            Self::UnsupportedPlatform => {
                write!(f, "BLOCKED: native SDK loader is Windows-only")
            }
        }
    }
}

impl std::error::Error for LoadError {}

fn hex(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        use std::fmt::Write as _;
        let _ = write!(out, "{byte:02x}");
    }
    out
}

/// Compute the lowercase-hex SHA-256 of a file, streaming.
fn file_sha256(path: &Path) -> std::io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex(&hasher.finalize()))
}

fn sidecar_path(native_path: &Path) -> PathBuf {
    native_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(BUILD_INFO_SIDECAR)
}

/// Read the sidecar and verify the DLL bytes against `binarySha256`.
///
/// Pure filesystem work — unit tested with constructed temp files; loading a
/// real DLL is exercised in the integration phase.
///
/// # Errors
///
/// See [`LoadError`]; the message always names the failed check.
pub fn verify_sidecar(native_path: &Path) -> Result<BuildInfo, LoadError> {
    if !native_path.is_file() {
        return Err(LoadError::MissingFile(native_path.to_path_buf()));
    }
    let sidecar = sidecar_path(native_path);
    if !sidecar.is_file() {
        return Err(LoadError::MissingSidecar(sidecar));
    }
    let text = std::fs::read_to_string(&sidecar)
        .map_err(|error| LoadError::BadSidecar(format!("{}: {error}", sidecar.display())))?;
    let value: serde_json::Value = serde_json::from_str(&text)
        .map_err(|error| LoadError::BadSidecar(format!("{}: {error}", sidecar.display())))?;
    let required = |field: &str| -> Result<String, LoadError> {
        value
            .get(field)
            .and_then(|v| v.as_str())
            .filter(|v| !v.is_empty())
            .map(str::to_owned)
            .ok_or_else(|| {
                LoadError::BadSidecar(format!("{}: missing string `{field}`", sidecar.display()))
            })
    };
    let info = BuildInfo {
        build_id: required("buildId")?,
        abi_hash: required("abiHash")?,
        binary_sha256: required("binarySha256")?,
    };
    let actual = file_sha256(native_path)
        .map_err(|error| LoadError::BadSidecar(format!("{}: {error}", native_path.display())))?;
    if !actual.eq_ignore_ascii_case(&info.binary_sha256) {
        return Err(LoadError::BinaryHashMismatch {
            actual,
            expected: info.binary_sha256,
        });
    }
    Ok(info)
}

/// Validate a loaded root table against the sidecar and the expected shape.
///
/// # Errors
///
/// See [`LoadError`]; the message names the mismatching field.
pub(crate) fn verify_root_table(root: &RootApiV1, info: &BuildInfo) -> Result<(), LoadError> {
    if root.abi_version != ABI_VERSION {
        return Err(LoadError::InvalidRootTable(format!(
            "abi_version {} != {ABI_VERSION}",
            root.abi_version
        )));
    }
    let min_size = u32::try_from(std::mem::size_of::<RootApiV1>()).unwrap_or(u32::MAX);
    if root.struct_size < min_size {
        return Err(LoadError::InvalidRootTable(format!(
            "struct_size {} < {min_size} (extended layout required)",
            root.struct_size
        )));
    }
    for (name, slot) in [
        ("ping", root.ping.is_none()),
        ("create_clr_host", root.create_clr_host.is_none()),
        ("clr_host_call", root.clr_host_call.is_none()),
        ("destroy_clr_host", root.destroy_clr_host.is_none()),
    ] {
        if slot {
            return Err(LoadError::InvalidRootTable(format!(
                "`{name}` slot is null"
            )));
        }
    }
    let abi_hash = hex(&root.abi_hash);
    if !abi_hash.eq_ignore_ascii_case(&info.abi_hash) {
        return Err(LoadError::AbiHashMismatch {
            actual: abi_hash,
            expected: info.abi_hash.clone(),
        });
    }
    let build_id = hex(&root.build_id);
    if !build_id.eq_ignore_ascii_case(&info.build_id) {
        return Err(LoadError::BuildIdMismatch {
            actual: build_id,
            expected: info.build_id.clone(),
        });
    }
    Ok(())
}

impl std::fmt::Debug for SdkLease {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        // The root table holds raw fn pointers; report the module handle only.
        f.debug_struct("SdkLease")
            .field("module", &self.module)
            .finish_non_exhaustive()
    }
}

/// Opaque `CoreCLR` host handle.
#[derive(Debug, Clone, Copy)]
pub(crate) struct ClrHostHandle(*mut c_void);

// SAFETY: the handle is opaque and only dereferenced through the SDK root
// table by the single owning bridge; moving it into the world task at startup
// is the only cross-thread transfer.
unsafe impl Send for ClrHostHandle {}

impl ClrHostHandle {
    /// Whether the handle is the null sentinel.
    #[must_use]
    pub const fn is_null(self) -> bool {
        self.0.is_null()
    }
}

/// Loaded SDK lease: keeps the module handle until dropped.
pub struct SdkLease {
    module: isize,
    root: RootApiV1,
}

impl SdkLease {
    /// Probe the SDK root table's `ping`.
    ///
    /// # Errors
    ///
    /// [`LoadError::PingFailed`] when the status is non-zero or the marker is
    /// not written.
    pub fn ping(&self) -> Result<(), LoadError> {
        let ping = self
            .root
            .ping
            .ok_or_else(|| LoadError::InvalidRootTable("`ping` slot vanished".to_owned()))?;
        let mut marker: u32 = 0;
        // SAFETY: ping writes a single u32 into our stack marker when the
        // pointer is non-null, which is exactly what we pass.
        let status = unsafe { ping(std::ptr::from_mut(&mut marker).cast::<c_void>()) };
        if status != 0 {
            return Err(LoadError::PingFailed(format!("status {status}")));
        }
        if marker != 1 {
            return Err(LoadError::PingFailed(format!("marker {marker} != 1")));
        }
        Ok(())
    }

    /// Create the `CoreCLR` host (safe wrapper over the root table slot).
    ///
    /// `entry_spec` is `'<assembly-qualified type name>;<entry method name>'`
    /// (split at the last `;`), exactly as the ABI expects.
    ///
    /// The `CoreCLR` runtime cannot be unloaded and re-initialized within one
    /// process: this must succeed at most once per process, balanced by one
    /// [`Self::destroy_clr_host`] at shutdown.
    ///
    /// # Errors
    ///
    /// Human-readable failure when the native call reports an error.
    pub(crate) fn create_clr_host(
        &self,
        hostfxr_path: &str,
        runtime_config_path: &str,
        assembly_path: &str,
        entry_spec: &str,
    ) -> Result<ClrHostHandle, String> {
        let create = self
            .root
            .create_clr_host
            .ok_or_else(|| "create_clr_host slot is null".to_owned())?;
        let hostfxr = utf8_null(hostfxr_path);
        let runtime_config = utf8_null(runtime_config_path);
        let assembly = utf8_null(assembly_path);
        let entry = utf8_null(entry_spec);
        let mut handle: *mut c_void = std::ptr::null_mut();
        // SAFETY: all four cstrings are valid NUL-terminated UTF-8 buffers
        // owned by this stack frame; `handle` is writable storage for one
        // opaque pointer and the callee promises null on failure.
        let status = unsafe {
            create(
                hostfxr.as_ptr(),
                runtime_config.as_ptr(),
                assembly.as_ptr(),
                entry.as_ptr(),
                std::ptr::from_mut(&mut handle),
            )
        };
        if status != SdkStatus::Success as i32 || handle.is_null() {
            return Err(format!(
                "create_clr_host failed with status {status} ({})",
                status_name(status)
            ));
        }
        Ok(ClrHostHandle(handle))
    }

    /// One byte-protocol call into the managed entry.
    ///
    /// `input` may be empty (a null pointer is passed); `output` may be empty
    /// for capacity probing (`BufferTooSmall` then reports the required size).
    ///
    /// # Errors
    ///
    /// Human-readable failure when the slot is missing or lengths do not fit.
    pub(crate) fn clr_host_call(
        &self,
        handle: ClrHostHandle,
        input: &[u8],
        output: &mut [u8],
    ) -> Result<ClrCall, String> {
        let call = self
            .root
            .clr_host_call
            .ok_or_else(|| "clr_host_call slot is null".to_owned())?;
        let input_len = u32::try_from(input.len()).map_err(|_| "input too large".to_owned())?;
        let capacity = u32::try_from(output.len()).map_err(|_| "output too large".to_owned())?;
        let input_ptr = if input.is_empty() {
            std::ptr::null()
        } else {
            input.as_ptr()
        };
        let output_ptr = if output.is_empty() {
            std::ptr::null_mut()
        } else {
            output.as_mut_ptr()
        };
        let mut written: u32 = 0;
        // SAFETY: `handle` is owned by this process's bridge; pointers and
        // lengths are consistent with the buffers above (null only when the
        // matching length is zero) and `written` is valid storage.
        let status = unsafe {
            call(
                handle.0,
                input_ptr,
                input_len,
                output_ptr,
                capacity,
                std::ptr::from_mut(&mut written),
            )
        };
        Ok(ClrCall { status, written })
    }

    /// Destroy the `CoreCLR` host.
    ///
    /// # Errors
    ///
    /// Human-readable failure when the native call reports an error.
    pub(crate) fn destroy_clr_host(&self, handle: ClrHostHandle) -> Result<(), String> {
        let destroy = self
            .root
            .destroy_clr_host
            .ok_or_else(|| "destroy_clr_host slot is null".to_owned())?;
        if handle.is_null() {
            return Ok(());
        }
        // SAFETY: the handle came from create_clr_host on this lease and each
        // handle is destroyed exactly once by the owning bridge's Drop.
        let status = unsafe { destroy(handle.0) };
        if status != 0 {
            return Err(format!("destroy_clr_host failed with status {status}"));
        }
        Ok(())
    }
}

impl Drop for SdkLease {
    fn drop(&mut self) {
        if self.module != 0 {
            #[cfg(windows)]
            {
                // SAFETY: the module handle came from LoadLibraryW and each
                // successful load is balanced by exactly one FreeLibrary; all CLR
                // hosts are destroyed before the lease drops.
                unsafe { FreeLibrary(self.module) };
            }
        }
    }
}

#[cfg(windows)]
fn wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn utf8_null(value: &str) -> Vec<u8> {
    let mut bytes = value.as_bytes().to_vec();
    bytes.push(0);
    bytes
}

/// Load the SDK DLL: sidecar verification, `LoadLibraryW`, root table fetch,
/// root-vs-sidecar verification and ping probe.
///
/// # Errors
///
/// See [`LoadError`]; every failure is fatal at startup (exit code 1).
pub fn load(native_path: &Path) -> Result<SdkLease, LoadError> {
    let info = verify_sidecar(native_path)?;
    #[cfg(not(windows))]
    {
        let _ = info;
        let _ = native_path;
        return Err(LoadError::UnsupportedPlatform);
    }
    #[cfg(windows)]
    {
        let wide = wide_null(&native_path.to_string_lossy());
        // SAFETY: `wide` is a valid null-terminated UTF-16 path; LoadLibraryW only
        // reads it. The returned handle is owned by the SdkLease below.
        let module = unsafe { LoadLibraryW(wide.as_ptr()) };
        if module == 0 {
            return Err(LoadError::LibraryLoadFailed(native_path.to_path_buf()));
        }

        // SAFETY: the module handle is valid and non-zero; `load_entry` only uses
        // it for GetProcAddress and the root-table fetch, and hands it back inside
        // the returned lease (or it is freed by the caller below on error).
        let lease = unsafe { load_entry(module, &info) };
        match lease {
            Ok(lease) => Ok(lease),
            Err(error) => {
                // SAFETY: module handle is valid (checked non-zero above) and this
                // is the balancing FreeLibrary for our load.
                unsafe { FreeLibrary(module) };
                Err(error)
            }
        }
    }
}

#[cfg(windows)]
unsafe fn load_entry(module: isize, info: &BuildInfo) -> Result<SdkLease, LoadError> {
    let symbol = utf8_null(ENTRY_SYMBOL);
    // SAFETY: `symbol` is a valid null-terminated name and the module handle
    // is valid for the lifetime of this call.
    let address = unsafe { GetProcAddress(module, symbol.as_ptr()) };
    if address.is_null() {
        return Err(LoadError::EntryMissing);
    }
    // SAFETY: the SDK contract guarantees the entry symbol has the
    // `GetApiV1` signature (C calling convention).
    let entry: GetApiV1 = unsafe { std::mem::transmute(address) };

    let mut root_ptr: *const RootApiV1 = std::ptr::null();
    // SAFETY: root_ptr is writable storage for one pointer.
    let status = unsafe { entry(ABI_VERSION, std::ptr::from_mut(&mut root_ptr)) };
    if status != 0 {
        return Err(LoadError::UnsupportedVersion(status));
    }
    if root_ptr.is_null() {
        return Err(LoadError::InvalidRootTable(
            "entry returned a null API table".to_owned(),
        ));
    }
    // SAFETY: the SDK contract keeps the root table at static storage for the
    // lifetime of the module; we copy the plain-old-data table out.
    let root = unsafe { *root_ptr };
    verify_root_table(&root, info)?;

    let lease = SdkLease { module, root };
    lease.ping()?;
    Ok(lease)
}

/// Human-readable status name for logs.
pub(crate) fn status_name(status: i32) -> &'static str {
    match SdkStatus::from_i32(status) {
        Some(SdkStatus::Success) => "Success",
        Some(SdkStatus::InvalidArgument) => "InvalidArgument",
        Some(SdkStatus::UnsupportedVersion) => "UnsupportedVersion",
        Some(SdkStatus::ClrInitFailed) => "ClrInitFailed",
        Some(SdkStatus::ClrEntryFailed) => "ClrEntryFailed",
        Some(SdkStatus::BufferTooSmall) => "BufferTooSmall",
        None => "Unknown",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fake_dll(dir: &Path, bytes: &[u8]) -> PathBuf {
        let path = dir.join("lumio_engine_native.dll");
        std::fs::write(&path, bytes).expect("write fake dll");
        path
    }

    fn write_sidecar(dir: &Path, body: &str) {
        std::fs::write(dir.join(BUILD_INFO_SIDECAR), body).expect("write sidecar");
    }

    fn sidecar_for(bytes: &[u8]) -> String {
        let digest = Sha256::digest(bytes);
        format!(
            "{{\"buildId\":\"{}\",\"abiHash\":\"{}\",\"binarySha256\":\"{}\"}}",
            "11".repeat(16),
            "ab".repeat(32),
            hex(&digest)
        )
    }

    unsafe extern "C" fn dummy_ping(_marker: *mut c_void) -> i32 {
        0
    }
    unsafe extern "C" fn dummy_create(
        _hostfxr: *const u8,
        _runtime_config: *const u8,
        _assembly: *const u8,
        _entry: *const u8,
        _out: *mut *mut c_void,
    ) -> i32 {
        0
    }
    unsafe extern "C" fn dummy_call(
        _host: *mut c_void,
        _input: *const u8,
        _input_len: u32,
        _output: *mut u8,
        _capacity: u32,
        _written: *mut u32,
    ) -> i32 {
        0
    }
    unsafe extern "C" fn dummy_destroy(_host: *mut c_void) -> i32 {
        0
    }

    fn sample_root() -> RootApiV1 {
        RootApiV1 {
            abi_version: ABI_VERSION,
            struct_size: u32::try_from(std::mem::size_of::<RootApiV1>()).unwrap_or(u32::MAX),
            abi_hash: [0xab; 32],
            build_id: [0x11; 16],
            ping: Some(dummy_ping),
            create_clr_host: Some(dummy_create),
            clr_host_call: Some(dummy_call),
            destroy_clr_host: Some(dummy_destroy),
        }
    }

    #[test]
    fn sidecar_matching_dll_bytes_is_accepted() {
        let dir = tempfile::tempdir().expect("tempdir");
        let bytes = b"fake native image";
        let dll = fake_dll(dir.path(), bytes);
        write_sidecar(dir.path(), &sidecar_for(bytes));
        let info = verify_sidecar(&dll).expect("sidecar verified");
        assert_eq!(info.build_id, "11".repeat(16));
        assert_eq!(info.binary_sha256, hex(&Sha256::digest(bytes)));
    }

    #[test]
    fn missing_dll_is_reported() {
        let dir = tempfile::tempdir().expect("tempdir");
        let error = verify_sidecar(&dir.path().join("nope.dll")).expect_err("missing dll");
        assert!(matches!(error, LoadError::MissingFile(_)), "{error}");
    }

    #[test]
    fn missing_sidecar_is_reported() {
        let dir = tempfile::tempdir().expect("tempdir");
        let dll = fake_dll(dir.path(), b"image");
        let error = verify_sidecar(&dll).expect_err("missing sidecar");
        assert!(matches!(error, LoadError::MissingSidecar(_)), "{error}");
    }

    #[test]
    fn binary_hash_mismatch_names_both_hashes() {
        let dir = tempfile::tempdir().expect("tempdir");
        let dll = fake_dll(dir.path(), b"image");
        write_sidecar(dir.path(), &sidecar_for(b"other"));
        let error = verify_sidecar(&dll).expect_err("hash mismatch");
        let LoadError::BinaryHashMismatch { actual, expected } = error else {
            panic!("expected BinaryHashMismatch");
        };
        assert_eq!(actual, hex(&Sha256::digest(b"image")));
        assert_ne!(actual, expected);
    }

    #[test]
    fn sidecar_missing_field_or_bad_json_is_rejected() {
        let dir = tempfile::tempdir().expect("tempdir");
        let dll = fake_dll(dir.path(), b"image");
        write_sidecar(dir.path(), "{\"buildId\":\"x\"}");
        let error = verify_sidecar(&dll).expect_err("missing abiHash");
        assert!(matches!(error, LoadError::BadSidecar(_)), "{error}");

        let dir2 = tempfile::tempdir().expect("tempdir");
        let dll2 = fake_dll(dir2.path(), b"image");
        write_sidecar(dir2.path(), "{ not json");
        assert!(matches!(
            verify_sidecar(&dll2),
            Err(LoadError::BadSidecar(_))
        ));
    }

    #[test]
    fn root_table_matching_sidecar_is_accepted() {
        let dir = tempfile::tempdir().expect("tempdir");
        let bytes = b"x";
        fake_dll(dir.path(), bytes);
        let info = BuildInfo {
            build_id: "11".repeat(16),
            abi_hash: "ab".repeat(32),
            binary_sha256: hex(bytes),
        };
        verify_root_table(&sample_root(), &info).expect("root verified");
    }

    #[test]
    fn abi_hash_mismatch_is_reported() {
        let dir = tempfile::tempdir().expect("tempdir");
        let bytes = b"x";
        fake_dll(dir.path(), bytes);
        let info = BuildInfo {
            build_id: "11".repeat(16),
            abi_hash: "cd".repeat(32),
            binary_sha256: hex(bytes),
        };
        assert!(matches!(
            verify_root_table(&sample_root(), &info),
            Err(LoadError::AbiHashMismatch { .. })
        ));
    }

    #[test]
    fn build_id_mismatch_is_reported() {
        let dir = tempfile::tempdir().expect("tempdir");
        let bytes = b"x";
        fake_dll(dir.path(), bytes);
        let info = BuildInfo {
            build_id: "22".repeat(16),
            abi_hash: "ab".repeat(32),
            binary_sha256: hex(bytes),
        };
        assert!(matches!(
            verify_root_table(&sample_root(), &info),
            Err(LoadError::BuildIdMismatch { .. })
        ));
    }

    #[test]
    fn root_table_shape_violations_are_reported() {
        let dir = tempfile::tempdir().expect("tempdir");
        fake_dll(dir.path(), b"x");
        let info = BuildInfo {
            build_id: "11".repeat(16),
            abi_hash: "ab".repeat(32),
            binary_sha256: "00".repeat(32),
        };
        let mut root = sample_root();
        root.abi_version = 9;
        assert!(matches!(
            verify_root_table(&root, &info),
            Err(LoadError::InvalidRootTable(_))
        ));
        let mut root = sample_root();
        root.struct_size = 4;
        assert!(matches!(
            verify_root_table(&root, &info),
            Err(LoadError::InvalidRootTable(_))
        ));
    }

    #[test]
    fn wide_and_utf8_buffers_are_null_terminated() {
        #[cfg(windows)]
        assert_eq!(wide_null("a"), vec![0x61, 0]);
        assert_eq!(utf8_null("a"), b"a\0".to_vec());
    }

    #[test]
    fn root_table_layout_matches_the_extended_abi() {
        // x64 golden layout from engine/abi/native-abi.json (commit cef0b03):
        // size 88; ping at 56; create_clr_host at 64; clr_host_call at 72;
        // destroy_clr_host at 80.
        assert_eq!(std::mem::size_of::<RootApiV1>(), 88);
        let root = sample_root();
        let base = std::ptr::from_ref(&root) as usize;
        assert_eq!(std::ptr::from_ref(&root.ping) as usize - base, 56);
        assert_eq!(
            std::ptr::from_ref(&root.create_clr_host) as usize - base,
            64
        );
        assert_eq!(std::ptr::from_ref(&root.clr_host_call) as usize - base, 72);
        assert_eq!(
            std::ptr::from_ref(&root.destroy_clr_host) as usize - base,
            80
        );
    }

    #[test]
    fn sdk_status_words_round_trip() {
        for value in 0..=5 {
            let status = SdkStatus::from_i32(value).expect("known status");
            assert_eq!(status as i32, value);
        }
        assert_eq!(SdkStatus::from_i32(6), None);
        assert_eq!(status_name(3), "ClrInitFailed");
    }

    #[test]
    fn load_of_missing_dll_fails_cleanly() {
        let dir = tempfile::tempdir().expect("tempdir");
        let error = load(&dir.path().join("nothing.dll")).expect_err("load must fail");
        assert!(matches!(error, LoadError::MissingFile(_)), "{error}");
    }
}
