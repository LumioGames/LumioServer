//! Refusal boundary for the unpublished `LumioGameRuntime` Managed Host ABI Rust package.

#![forbid(unsafe_code)]

compile_error!(
    "LumioGameRuntime has not published a Managed Host ABI Rust artifact; refusing to define a local ABI"
);
