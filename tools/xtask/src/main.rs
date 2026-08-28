mod contracts;
mod policy;

use std::env;
use std::process::ExitCode;

fn usage() {
    eprintln!("usage: cargo xtask <contracts verify|policy check>");
}

fn main() -> ExitCode {
    let mut args = env::args().skip(1);
    let Some(domain) = args.next() else {
        usage();
        return ExitCode::from(2);
    };
    let Some(action) = args.next() else {
        usage();
        return ExitCode::from(2);
    };
    if args.next().is_some() {
        usage();
        return ExitCode::from(2);
    }

    match (domain.as_str(), action.as_str()) {
        ("contracts", "verify") => contracts::verify(),
        ("policy", "check") => policy::command(),
        _ => {
            usage();
            ExitCode::from(2)
        }
    }
}
