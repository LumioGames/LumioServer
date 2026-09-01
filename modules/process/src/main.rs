//! Thin binary entry: argument parsing and exit-code mapping only.
//!
//! Exit codes follow the hello-wire-v1 contract `process.server.exitCodes`:
//! 0 normal shutdown, 1 initialization failure, 2 runtime fatal error
//! (a panic escaping `run`), 3 usage error. Panics inside spawned tokio tasks
//! do not reach this handler and remain a hardening item (see modules/process
//! README).

#[tokio::main]
async fn main() {
    let argv: Vec<String> = std::env::args().skip(1).collect();
    match lumio_server_process::cli::parse(&argv) {
        Ok(lumio_server_process::cli::Parsed::Help) => {
            println!("{}", lumio_server_process::cli::USAGE);
            std::process::exit(0);
        }
        Ok(lumio_server_process::cli::Parsed::Args(boxed)) => {
            use futures_util::FutureExt;
            let future = std::panic::AssertUnwindSafe(lumio_server_process::run(*boxed));
            match future.catch_unwind().await {
                Ok(code) => std::process::exit(code),
                Err(payload) => {
                    let detail = payload
                        .downcast_ref::<&str>()
                        .map(|s| (*s).to_string())
                        .or_else(|| payload.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "unknown panic payload".to_string());
                    eprintln!("fatal: runtime panic: {detail}");
                    std::process::exit(2);
                }
            }
        }
        Err(message) => {
            eprintln!("error: {message}\n\n{}", lumio_server_process::cli::USAGE);
            std::process::exit(3);
        }
    }
}
