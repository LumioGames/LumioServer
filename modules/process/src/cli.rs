//! Command-line surface of the `lumio-server` binary.
//!
//! The integration launcher owns the exact flag set; argument errors exit
//! with code 3. `--client` is accepted but the static HTTP root is not served
//! in this wave (the launcher passes it empty).

use std::path::PathBuf;

/// Flags that take one required value.
const VALUE_FLAGS: [&str; 10] = [
    "--engine-native",
    "--hostfxr",
    "--runtime-config",
    "--assembly",
    "--entry-type",
    "--entry-method",
    "--wire-contract",
    "--audit-file",
    "--ready-file",
    "--client",
];

/// Parsed command line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Args {
    /// Native SDK DLL path (`lumio_engine_get_api_v1` provider).
    pub engine_native: PathBuf,
    /// `hostfxr.dll` path for the `CoreCLR` host.
    pub hostfxr: PathBuf,
    /// `runtimeconfig.json` for the managed runtime.
    pub runtime_config: PathBuf,
    /// Managed gameplay assembly path.
    pub assembly: PathBuf,
    /// Entry type full name (`Lumio.GameRuntime.HelloEntry.HelloEntry, ...`).
    pub entry_type: String,
    /// Entry method name (`lumio_hello_entry`; the ABI joins it into
    /// `'<type>;<method>'` — see [`crate::runtime_bridge::ClrStart`]).
    pub entry_method: String,
    /// Wire contract JSON (`hello-wire-v1.json`).
    pub wire_contract: PathBuf,
    /// NDJSON audit sink (append).
    pub audit_file: PathBuf,
    /// Readiness JSON file written after `SERVER_READY`.
    pub ready_file: PathBuf,
    /// Optional static HTTP client root (unused in this wave, may be empty).
    pub client: Option<String>,
}

/// Result of parsing the process arguments.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Parsed {
    /// Runnable arguments (boxed: the variant dwarfs [`Parsed::Help`]).
    Args(Box<Args>),
    /// `--help`/`-h` was requested; print [`USAGE`] and exit 0.
    Help,
}

/// Usage text printed for `--help` and argument errors.
pub const USAGE: &str = "lumio-server — MS-00002 Hello World dedicated server

usage:
  lumio-server --engine-native <sdk.dll> --hostfxr <hostfxr.dll> \
    --runtime-config <runtimeconfig.json> --assembly <managed.dll> \
    --entry-type <type-full-name> --entry-method <method> \
    --wire-contract <hello-wire-v1.json> --audit-file <audit.ndjson> \
    --ready-file <ready.json> [--client <static-dir>]

exit codes: 0 normal shutdown, 1 initialization failure, 2 fatal runtime error, 3 argument error";

/// Parse `argv` (without the program name).
///
/// # Errors
///
/// Returns a message naming the missing/unknown/duplicated argument for the
/// caller to print before exiting with code 3.
pub fn parse(argv: &[String]) -> Result<Parsed, String> {
    if argv.iter().any(|a| a == "--help" || a == "-h") {
        return Ok(Parsed::Help);
    }

    let mut values: Vec<(&'static str, String)> = Vec::new();
    let mut index = 0;
    while index < argv.len() {
        let flag = argv[index].as_str();
        let Some(name) = VALUE_FLAGS.iter().find(|candidate| **candidate == flag) else {
            return Err(format!("unknown argument `{flag}`"));
        };
        let Some(value) = argv.get(index + 1) else {
            return Err(format!("argument `{flag}` requires a value"));
        };
        if values.iter().any(|(existing, _)| existing == name) {
            return Err(format!("argument `{flag}` given more than once"));
        }
        values.push((name, value.clone()));
        index += 2;
    }

    let get = |name: &str| -> Result<String, String> {
        values
            .iter()
            .find(|(existing, _)| *existing == name)
            .map(|(_, value)| value.clone())
            .ok_or_else(|| format!("missing required argument `{name}`"))
    };

    Ok(Parsed::Args(Box::new(Args {
        engine_native: PathBuf::from(get("--engine-native")?),
        hostfxr: PathBuf::from(get("--hostfxr")?),
        runtime_config: PathBuf::from(get("--runtime-config")?),
        assembly: PathBuf::from(get("--assembly")?),
        entry_type: get("--entry-type")?,
        entry_method: get("--entry-method")?,
        wire_contract: PathBuf::from(get("--wire-contract")?),
        audit_file: PathBuf::from(get("--audit-file")?),
        ready_file: PathBuf::from(get("--ready-file")?),
        client: values
            .iter()
            .find(|(existing, _)| *existing == "--client")
            .map(|(_, value)| value.clone()),
    })))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn argv(minus_program: &[&str]) -> Vec<String> {
        minus_program.iter().map(|s| (*s).to_owned()).collect()
    }

    fn full() -> Vec<String> {
        argv(&[
            "--engine-native",
            "sdk.dll",
            "--hostfxr",
            "hostfxr.dll",
            "--runtime-config",
            "rt.json",
            "--assembly",
            "game.dll",
            "--entry-type",
            "Lumio.GameRuntime.HelloEntry.HelloEntry, Lumio.GameRuntime.HelloEntry",
            "--entry-method",
            "lumio_hello_entry",
            "--wire-contract",
            "hello-wire-v1.json",
            "--audit-file",
            "audit.ndjson",
            "--ready-file",
            "ready.json",
        ])
    }

    #[test]
    fn parses_the_full_flag_set() {
        let Parsed::Args(args) = parse(&full()).expect("full flag set parses") else {
            panic!("expected runnable args");
        };
        assert_eq!(args.engine_native, PathBuf::from("sdk.dll"));
        assert_eq!(args.entry_method, "lumio_hello_entry");
        assert_eq!(args.client, None);
    }

    #[test]
    fn accepts_optional_client_flag() {
        let mut argument_list = full();
        argument_list.extend(["--client".to_owned(), "wwwroot".to_owned()]);
        let Parsed::Args(args) = parse(&argument_list).expect("client flag parses") else {
            panic!("expected runnable args");
        };
        assert_eq!(args.client.as_deref(), Some("wwwroot"));
    }

    #[test]
    fn help_short_circuits() {
        assert_eq!(parse(&argv(&["--help"])), Ok(Parsed::Help));
        assert_eq!(parse(&argv(&["-h"])), Ok(Parsed::Help));
    }

    #[test]
    fn missing_required_argument_is_named() {
        let argv = full();
        let error = parse(&argv[..argv.len() - 2]).expect_err("missing --ready-file");
        assert!(error.contains("--ready-file"), "error was: {error}");
    }

    #[test]
    fn valueless_flag_is_rejected() {
        let error = parse(&argv(&["--engine-native"])).expect_err("value required");
        assert!(error.contains("requires a value"), "error was: {error}");
    }

    #[test]
    fn unknown_flag_is_rejected() {
        let error = parse(&argv(&["--wat", "x"])).expect_err("unknown flag");
        assert!(error.contains("unknown argument"), "error was: {error}");
    }

    #[test]
    fn duplicated_flag_is_rejected() {
        let mut argv = full();
        argv.extend(["--audit-file".to_owned(), "again.ndjson".to_owned()]);
        let error = parse(&argv).expect_err("duplicate flag");
        assert!(error.contains("more than once"), "error was: {error}");
    }
}
