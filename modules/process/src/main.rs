//! Thin binary entry: argument parsing and exit-code mapping only.

#[tokio::main]
async fn main() {
    let argv: Vec<String> = std::env::args().skip(1).collect();
    match lumio_server_process::cli::parse(&argv) {
        Ok(lumio_server_process::cli::Parsed::Help) => {
            println!("{}", lumio_server_process::cli::USAGE);
            std::process::exit(0);
        }
        Ok(lumio_server_process::cli::Parsed::Args(boxed)) => {
            std::process::exit(lumio_server_process::run(*boxed).await);
        }
        Err(message) => {
            eprintln!("error: {message}\n\n{}", lumio_server_process::cli::USAGE);
            std::process::exit(3);
        }
    }
}
