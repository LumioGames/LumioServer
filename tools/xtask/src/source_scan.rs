use std::path::Path;

const LEGACY_DIRS: &[&str] = &["network", "release-router", "maintenance", "headless"];
const LEGACY_IDENTIFIERS: &[&str] = &[
    "lumio_network",
    "lumio_release_router",
    "lumio_maintenance",
    "lumio_headless",
];
const BLOCKED_IDENTIFIER: &str = "lumio_protocol_dispatch";
const BLOCKED_PACKAGE: &str = "lumio-protocol-dispatch";
const UNBOUNDED_APIS: &[&str] = &[
    "std::sync::mpsc::channel",
    "tokio::sync::mpsc::unbounded_channel",
    "crossbeam_channel::unbounded",
    "async_channel::unbounded",
    "flume::unbounded",
    "futures::channel::mpsc::unbounded",
];
const SPAWN_APIS: &[&str] = &[
    "std::thread::spawn",
    "thread::spawn",
    "std::thread::Builder::new().spawn",
    "std::thread::Builder::new",
    "std::thread::Builder::default",
    "std::thread::Builder::spawn",
    "tokio::spawn",
    "tokio::task::spawn",
    "tokio::task::spawn_blocking",
    "async_std::task::spawn",
];
const SLEEP_APIS: &[&str] = &[
    "std::thread::sleep",
    "thread::sleep",
    "tokio::time::sleep",
    "async_std::task::sleep",
];

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(super) struct ScanReport {
    pub(super) files: usize,
    pub(super) violations: Vec<String>,
}

pub(super) fn scan(root: &Path) -> ScanReport {
    let mut report = ScanReport::default();
    scan_repository_redlines(root, &mut report);
    let modules = root.join("modules");
    for legacy in LEGACY_DIRS {
        let path = modules.join(legacy);
        if path.exists() {
            report.violations.push(format!(
                "legacy first-class module directory `{}`",
                path.display()
            ));
        }
    }

    let blocked = modules.join("protocol-dispatch");
    for path in [blocked.join("Cargo.toml"), blocked.join("src")] {
        if path.exists() {
            report.violations.push(format!(
                "protocol-dispatch is blocked by D-009 but `{}` exists",
                path.display()
            ));
        }
    }

    let entries = match std::fs::read_dir(&modules) {
        Ok(entries) => entries,
        Err(error) => {
            report
                .violations
                .push(format!("cannot scan {}: {error}", modules.display()));
            return report;
        }
    };
    for entry in entries.flatten() {
        if !entry.file_type().is_ok_and(|kind| kind.is_dir()) {
            continue;
        }
        let module = entry.file_name().to_string_lossy().into_owned();
        let manifest = entry.path().join("Cargo.toml");
        if manifest.exists() {
            scan_manifest(&manifest, &mut report);
        }
        let source = entry.path().join("src");
        if source.exists() {
            scan_rust_tree(&source, &module, &mut report);
        }
    }
    report.violations.sort();
    report.violations.dedup();
    report
}

fn scan_repository_redlines(path: &Path, report: &mut ScanReport) {
    let entries = match std::fs::read_dir(path) {
        Ok(entries) => entries,
        Err(error) => {
            report
                .violations
                .push(format!("cannot scan {}: {error}", path.display()));
            return;
        }
    };
    for entry in entries.flatten() {
        let entry_path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            let name = entry.file_name();
            if name != ".git" && name != "target" {
                scan_repository_redlines(&entry_path, report);
            }
        } else if file_type.is_file() && entry.file_name() == "Cargo.toml" {
            inspect_manifest(&entry_path, report);
        } else if file_type.is_file()
            && entry_path
                .extension()
                .is_some_and(|extension| extension == "rs")
        {
            inspect_rust_redlines(&entry_path, report);
        }
    }
}

fn scan_manifest(path: &Path, report: &mut ScanReport) {
    report.files += 1;
    inspect_manifest(path, report);
}

fn inspect_manifest(path: &Path, report: &mut ScanReport) {
    match std::fs::read_to_string(path) {
        Ok(text) => {
            let active = text
                .lines()
                .map(super::strip_comment)
                .collect::<Vec<_>>()
                .join("\n");
            for identifier in LEGACY_IDENTIFIERS {
                let package = identifier.replace('_', "-");
                if contains_token(&active, identifier) || contains_package_token(&active, &package)
                {
                    report.violations.push(format!(
                        "{}: legacy first-class module name `{package}`",
                        path.display()
                    ));
                }
            }
            if contains_token(&active, BLOCKED_IDENTIFIER)
                || contains_package_token(&active, BLOCKED_PACKAGE)
            {
                report.violations.push(format!(
                    "{}: blocked D-009 package `{BLOCKED_PACKAGE}`",
                    path.display()
                ));
            }
        }
        Err(error) => report
            .violations
            .push(format!("cannot read {}: {error}", path.display())),
    }
}

fn inspect_rust_redlines(path: &Path, report: &mut ScanReport) {
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) => {
            report
                .violations
                .push(format!("cannot read {}: {error}", path.display()));
            return;
        }
    };
    let source = rust_code_only(&text);
    for identifier in LEGACY_IDENTIFIERS {
        if contains_token(&source, identifier) {
            report.violations.push(format!(
                "{}: legacy first-class module identifier `{identifier}`",
                path.display()
            ));
        }
    }
    if contains_token(&source, BLOCKED_IDENTIFIER) {
        report.violations.push(format!(
            "{}: blocked D-009 identifier `{BLOCKED_IDENTIFIER}`",
            path.display()
        ));
    }
}

fn scan_rust_tree(path: &Path, module: &str, report: &mut ScanReport) {
    let entries = match std::fs::read_dir(path) {
        Ok(entries) => entries,
        Err(error) => {
            report
                .violations
                .push(format!("cannot scan {}: {error}", path.display()));
            return;
        }
    };
    for entry in entries.flatten() {
        let entry_path = entry.path();
        if entry.file_type().is_ok_and(|kind| kind.is_dir()) {
            scan_rust_tree(&entry_path, module, report);
        } else if entry_path
            .extension()
            .is_some_and(|extension| extension == "rs")
        {
            scan_rust_file(&entry_path, module, report);
        }
    }
}

fn scan_rust_file(path: &Path, module: &str, report: &mut ScanReport) {
    report.files += 1;
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) => {
            report
                .violations
                .push(format!("cannot read {}: {error}", path.display()));
            return;
        }
    };
    let source = rust_code_only(&text);
    let code = compact_code(&source);
    let aliases = use_bindings(&source);
    for api in UNBOUNDED_APIS {
        if contains_api(&code, api, &aliases) {
            report.violations.push(format!(
                "{}: unbounded channel API `{api}` is forbidden",
                path.display()
            ));
        }
    }
    if module != "host-runtime" {
        for api in SPAWN_APIS {
            if contains_api(&code, api, &aliases) {
                report.violations.push(format!(
                    "{}: direct spawn API `{api}` must go through host-runtime",
                    path.display()
                ));
            }
        }
        for api in SLEEP_APIS {
            if contains_api(&code, api, &aliases) {
                report.violations.push(format!(
                    "{}: direct sleep API `{api}` must go through host-runtime timers",
                    path.display()
                ));
            }
        }
    }
}

fn contains_api(code: &str, api: &str, aliases: &[(String, String)]) -> bool {
    contains_token(code, api)
        || aliases.iter().any(|(alias, path)| {
            if alias == "*" {
                return api
                    .strip_prefix(path)
                    .and_then(|suffix| suffix.strip_prefix("::"))
                    .is_some_and(|suffix| !suffix.is_empty() && contains_token(code, suffix));
            }
            api.strip_prefix(path).is_some_and(|suffix| {
                (suffix.is_empty() || suffix.starts_with("::"))
                    && contains_token(code, &format!("{alias}{suffix}"))
            })
        })
}

fn use_bindings(source: &str) -> Vec<(String, String)> {
    let tokens = rust_tokens(source);
    let mut bindings = Vec::new();
    let mut index = 0;
    while index < tokens.len() {
        if tokens[index] == "use" {
            let end = tokens[index + 1..]
                .iter()
                .position(|token| token == ";")
                .map_or(tokens.len(), |offset| index + offset + 1);
            let mut cursor = index + 1;
            parse_use_tree(&tokens[..end], &mut cursor, &[], &mut bindings);
            index = end;
        } else if tokens[index] == "extern"
            && tokens.get(index + 1).is_some_and(|token| token == "crate")
        {
            let Some(package) = tokens.get(index + 2) else {
                break;
            };
            let alias = if tokens.get(index + 3).is_some_and(|token| token == "as") {
                tokens.get(index + 4).cloned()
            } else {
                Some(package.clone())
            };
            if let Some(alias) = alias.filter(|alias| alias != "_") {
                add_binding(alias, package, &mut bindings);
            }
        }
        index += 1;
    }
    bindings.sort();
    bindings.dedup();
    bindings
}

fn parse_use_tree(
    tokens: &[String],
    cursor: &mut usize,
    prefix: &[String],
    bindings: &mut Vec<(String, String)>,
) {
    while tokens.get(*cursor).is_some_and(|token| token == "::") {
        *cursor += 1;
    }
    if tokens.get(*cursor).is_some_and(|token| token == "{") {
        *cursor += 1;
        while *cursor < tokens.len() && tokens[*cursor] != "}" {
            parse_use_tree(tokens, cursor, prefix, bindings);
            if tokens.get(*cursor).is_some_and(|token| token != "}") {
                *cursor += 1;
            }
        }
        if tokens.get(*cursor).is_some_and(|token| token == "}") {
            *cursor += 1;
        }
        return;
    }

    let Some(segment) = tokens.get(*cursor).cloned() else {
        return;
    };
    if segment == "*" {
        *cursor += 1;
        if !prefix.is_empty() {
            add_binding("*".to_owned(), &prefix.join("::"), bindings);
        }
        return;
    }
    if matches!(segment.as_str(), "," | "}" | ";") {
        *cursor += 1;
        return;
    }
    *cursor += 1;
    let mut path = prefix.to_vec();
    if segment != "self" {
        path.push(segment.clone());
    }

    if tokens.get(*cursor).is_some_and(|token| token == "::") {
        *cursor += 1;
        parse_use_tree(tokens, cursor, &path, bindings);
        return;
    }

    let alias = if tokens.get(*cursor).is_some_and(|token| token == "as") {
        *cursor += 1;
        let alias = tokens.get(*cursor).cloned();
        if alias.is_some() {
            *cursor += 1;
        }
        alias
    } else if segment == "self" {
        path.last().cloned()
    } else {
        Some(segment)
    };
    if let (Some(alias), false) = (alias, path.is_empty()) {
        if alias != "_" {
            let path = path.join("::");
            add_binding(alias, &path, bindings);
        }
    }
}

fn add_binding(alias: String, path: &str, bindings: &mut Vec<(String, String)>) {
    let (head, tail) = path.split_once("::").unwrap_or((path, ""));
    let resolved = bindings
        .iter()
        .rev()
        .find(|(candidate, _)| candidate == head)
        .map_or_else(
            || path.to_owned(),
            |(_, target)| {
                if tail.is_empty() {
                    target.clone()
                } else {
                    format!("{target}::{tail}")
                }
            },
        );
    bindings.push((alias, resolved));
}

fn rust_tokens(source: &str) -> Vec<String> {
    let chars = source.chars().collect::<Vec<_>>();
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < chars.len() {
        let character = chars[index];
        if character.is_ascii_alphabetic() || character == '_' {
            let start = index;
            index += 1;
            while chars
                .get(index)
                .is_some_and(|next| next.is_ascii_alphanumeric() || *next == '_')
            {
                index += 1;
            }
            tokens.push(chars[start..index].iter().collect());
        } else if character == ':' && chars.get(index + 1) == Some(&':') {
            tokens.push("::".to_owned());
            index += 2;
        } else {
            if matches!(character, '{' | '}' | ',' | ';' | '*') {
                tokens.push(character.to_string());
            }
            index += 1;
        }
    }
    tokens
}

fn contains_token(text: &str, token: &str) -> bool {
    text.match_indices(token).any(|(index, _)| {
        let before = text[..index].chars().next_back();
        let after = text[index + token.len()..].chars().next();
        !before.is_some_and(is_identifier_character) && !after.is_some_and(is_identifier_character)
    })
}

fn contains_package_token(text: &str, token: &str) -> bool {
    text.match_indices(token).any(|(index, _)| {
        let before = text[..index].chars().next_back();
        let after = text[index + token.len()..].chars().next();
        !before.is_some_and(is_package_character) && !after.is_some_and(is_package_character)
    })
}

fn is_identifier_character(character: char) -> bool {
    character.is_alphanumeric() || character == '_'
}

fn is_package_character(character: char) -> bool {
    character.is_alphanumeric() || matches!(character, '_' | '-')
}

fn compact_code(text: &str) -> String {
    text.chars()
        .filter(|character| !character.is_whitespace())
        .collect()
}

fn rust_code_only(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let mut output = String::with_capacity(text.len());
    let mut index = 0;
    while index < chars.len() {
        let current = chars[index];
        let next = chars.get(index + 1).copied();
        if current == '/' && next == Some('/') {
            index = skip_line_comment(&chars, index + 2, &mut output);
        } else if current == '/' && next == Some('*') {
            index = skip_block_comment(&chars, index + 2);
            output.push(' ');
        } else if current == '"' {
            index = skip_quoted(&chars, index + 1, '"');
            output.push(' ');
        } else if current == '\'' && looks_like_character(&chars, index) {
            index = skip_quoted(&chars, index + 1, '\'');
            output.push(' ');
        } else if current == 'r' {
            if let Some((hashes, consumed)) = raw_string_start(&chars, index) {
                index = skip_raw_string(&chars, index + consumed, hashes);
                output.push(' ');
            } else {
                output.push(current);
                index += 1;
            }
        } else {
            output.push(current);
            index += 1;
        }
    }
    output
}

fn skip_line_comment(chars: &[char], mut index: usize, output: &mut String) -> usize {
    while chars.get(index).is_some_and(|character| *character != '\n') {
        index += 1;
    }
    if chars.get(index) == Some(&'\n') {
        output.push('\n');
        index += 1;
    } else {
        output.push(' ');
    }
    index
}

fn skip_block_comment(chars: &[char], mut index: usize) -> usize {
    let mut depth = 1_u32;
    while index < chars.len() && depth > 0 {
        match (chars[index], chars.get(index + 1).copied()) {
            ('/', Some('*')) => {
                depth += 1;
                index += 2;
            }
            ('*', Some('/')) => {
                depth -= 1;
                index += 2;
            }
            _ => index += 1,
        }
    }
    index
}

fn skip_quoted(chars: &[char], mut index: usize, quote: char) -> usize {
    let mut escaped = false;
    while let Some(character) = chars.get(index) {
        index += 1;
        if escaped {
            escaped = false;
        } else if *character == '\\' {
            escaped = true;
        } else if *character == quote {
            break;
        }
    }
    index
}

fn skip_raw_string(chars: &[char], mut index: usize, hashes: usize) -> usize {
    while index < chars.len() {
        if raw_string_end(chars, index, hashes) {
            return index + hashes + 1;
        }
        index += 1;
    }
    index
}

fn looks_like_character(chars: &[char], index: usize) -> bool {
    matches!(chars.get(index + 2), Some('\''))
        || (chars.get(index + 1) == Some(&'\\') && chars.get(index + 3) == Some(&'\''))
}

fn raw_string_start(chars: &[char], index: usize) -> Option<(usize, usize)> {
    let mut cursor = index + 1;
    let mut hashes = 0;
    while chars.get(cursor) == Some(&'#') {
        hashes += 1;
        cursor += 1;
    }
    (chars.get(cursor) == Some(&'"')).then_some((hashes, cursor - index + 1))
}

fn raw_string_end(chars: &[char], index: usize, hashes: usize) -> bool {
    chars.get(index) == Some(&'"')
        && (0..hashes).all(|offset| chars.get(index + offset + 1) == Some(&'#'))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn fixture() -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("lumio-policy-{}-{unique}", std::process::id()));
        fs::create_dir_all(root.join("modules/session/src")).unwrap();
        root
    }

    #[test]
    fn rejects_forbidden_source_apis_but_ignores_markdown() {
        for forbidden in [
            "std::sync::mpsc::channel()",
            "std::thread::spawn(|| {})",
            "tokio::spawn(async {})",
            "std::thread::sleep(duration)",
            "use lumio_network::Connection;",
        ] {
            let root = fixture();
            fs::write(root.join("modules/session/src/lib.rs"), forbidden).unwrap();
            fs::write(
                root.join("modules/session/README.md"),
                "std::thread::spawn and unbounded_channel are explanatory text",
            )
            .unwrap();
            let report = scan(&root);
            assert!(!report.violations.is_empty(), "not rejected: {forbidden}");
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn import_aliases_cannot_hide_forbidden_source_apis() {
        for forbidden in [
            "use std::thread as threads; threads::spawn(|| {});",
            "use tokio::time as clock; clock::sleep(duration).await;",
            "use std::sync::mpsc as mailbox; mailbox::channel::<u8>();",
            "use std::thread::*; spawn(|| {});",
            "use std::sync::mpsc::*; channel::<u8>();",
            "let builder = std::thread::Builder::new(); builder.spawn(|| {});",
            "use std::thread::Builder as ThreadBuilder; let builder = ThreadBuilder::new(); builder.spawn(|| {});",
            "let builder = std::thread::Builder::new(); std::thread::Builder::spawn(builder, || {});",
        ] {
            let root = fixture();
            fs::write(root.join("modules/session/src/lib.rs"), forbidden).unwrap();
            let report = scan(&root);
            assert!(!report.violations.is_empty(), "not rejected: {forbidden}");
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn source_comments_and_strings_do_not_trigger_redlines() {
        let root = fixture();
        fs::write(
            root.join("modules/session/src/lib.rs"),
            r#"
// std::thread::spawn is forbidden here.
const EXPLANATION: &str = "tokio::sync::mpsc::unbounded_channel";
pub fn legal() {}
"#,
        )
        .unwrap();
        assert!(scan(&root).violations.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn markdown_is_outside_the_source_scan_scope() {
        let root = fixture();
        fs::write(root.join("modules/session/src/lib.rs"), "pub fn legal() {}").unwrap();
        fs::write(
            root.join("modules/session/README.md"),
            "std::thread::spawn tokio::time::sleep lumio_network",
        )
        .unwrap();
        assert!(scan(&root).violations.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cargo_comments_do_not_trigger_redlines() {
        let root = fixture();
        fs::write(
            root.join("modules/session/Cargo.toml"),
            "# lumio-network and lumio-protocol-dispatch are forbidden\n[package]\nname = \"lumio-session\"",
        )
        .unwrap();
        assert!(scan(&root).violations.is_empty());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rejects_protocol_dispatch_cargo_or_source() {
        for blocked_path in ["Cargo.toml", "src"] {
            let root = fixture();
            let path = root.join("modules/protocol-dispatch").join(blocked_path);
            if blocked_path == "src" {
                fs::create_dir_all(&path).unwrap();
            } else {
                fs::create_dir_all(path.parent().unwrap()).unwrap();
                fs::write(&path, "[package]").unwrap();
            }
            let report = scan(&root);
            assert!(
                report
                    .violations
                    .iter()
                    .any(|error| error.contains("protocol-dispatch")),
                "not rejected: {}",
                path.display()
            );
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn rejects_the_blocked_dispatch_package_outside_modules() {
        let root = fixture();
        let package = root.join("crates/dispatch");
        fs::create_dir_all(package.join("src")).unwrap();
        fs::write(
            package.join("Cargo.toml"),
            "[package]\nname = 'lumio-protocol-dispatch'",
        )
        .unwrap();
        fs::write(package.join("src/lib.rs"), "pub struct Dispatch;").unwrap();

        let report = scan(&root);
        assert!(
            report
                .violations
                .iter()
                .any(|error| error.contains("D-009")),
            "blocked package outside modules was accepted: {:?}",
            report.violations
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rejects_blocked_dispatch_api_outside_modules() {
        let root = fixture();
        let package = root.join("crates/router");
        fs::create_dir_all(package.join("src")).unwrap();
        fs::write(
            package.join("Cargo.toml"),
            "[package]\nname = 'lumio-safe-router'",
        )
        .unwrap();
        fs::write(
            package.join("src/lib.rs"),
            "pub struct lumio_protocol_dispatch;",
        )
        .unwrap();

        let report = scan(&root);
        assert!(
            report
                .violations
                .iter()
                .any(|error| error.contains("D-009")),
            "blocked API outside modules was accepted: {:?}",
            report.violations
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn rejects_a_dependency_on_the_blocked_dispatch_package() {
        let root = fixture();
        fs::write(
            root.join("modules/session/Cargo.toml"),
            "[dependencies]\ndispatch = { package = \"lumio-protocol-dispatch\", version = \"1\" }",
        )
        .unwrap();
        assert!(scan(&root)
            .violations
            .iter()
            .any(|error| error.contains("D-009")));
        fs::remove_dir_all(root).unwrap();
    }
}
