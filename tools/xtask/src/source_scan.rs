use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

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
    "std::thread::Builder",
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
    let source = normalize_raw_identifiers(&rust_code_only(&text));
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
    let source_root = match std::fs::canonicalize(path) {
        Ok(path) => path,
        Err(error) => {
            report
                .violations
                .push(format!("cannot resolve {}: {error}", path.display()));
            return;
        }
    };
    let mut roots = Vec::new();
    collect_rust_roots(path, &mut roots, report);
    let mut visited = BTreeSet::new();
    for root in roots {
        scan_rust_file(&root, module, &source_root, &mut visited, report);
    }
}

fn collect_rust_roots(path: &Path, roots: &mut Vec<PathBuf>, report: &mut ScanReport) {
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
            collect_rust_roots(&entry_path, roots, report);
        } else if entry_path
            .extension()
            .is_some_and(|extension| extension == "rs")
        {
            roots.push(entry_path);
        }
    }
}

fn scan_rust_file(
    path: &Path,
    module: &str,
    source_root: &Path,
    visited: &mut BTreeSet<PathBuf>,
    report: &mut ScanReport,
) {
    let canonical = match std::fs::canonicalize(path) {
        Ok(path) => path,
        Err(error) => {
            report
                .violations
                .push(format!("cannot resolve {}: {error}", path.display()));
            return;
        }
    };
    if !canonical.starts_with(source_root) {
        report.violations.push(format!(
            "{}: included Rust source is outside module src `{}`",
            canonical.display(),
            source_root.display()
        ));
        return;
    }
    if !visited.insert(canonical.clone()) {
        return;
    }
    report.files += 1;
    let text = match std::fs::read_to_string(&canonical) {
        Ok(text) => text,
        Err(error) => {
            report
                .violations
                .push(format!("cannot read {}: {error}", canonical.display()));
            return;
        }
    };
    inspect_rust_redlines(&canonical, report);
    let source = normalize_raw_identifiers(&rust_code_only(&text));
    let code = compact_code(&source);
    let aliases = use_bindings(&source);
    for api in UNBOUNDED_APIS {
        if contains_api(&code, api, &aliases) {
            report.violations.push(format!(
                "{}: unbounded channel API `{api}` is forbidden",
                canonical.display()
            ));
        }
    }
    if let Some(api) = macro_expanded_api(&source, UNBOUNDED_APIS, &aliases) {
        report.violations.push(format!(
            "{}: macro-expanded unbounded channel API `{api}` is forbidden",
            canonical.display()
        ));
    }
    if module != "host-runtime" {
        for api in SPAWN_APIS {
            if contains_api(&code, api, &aliases) {
                report.violations.push(format!(
                    "{}: direct spawn API `{api}` must go through host-runtime",
                    canonical.display()
                ));
            }
        }
        if let Some(api) = macro_expanded_api(&source, SPAWN_APIS, &aliases) {
            report.violations.push(format!(
                "{}: macro-expanded spawn API `{api}` must go through host-runtime",
                canonical.display()
            ));
        }
        for api in SLEEP_APIS {
            if contains_api(&code, api, &aliases) {
                report.violations.push(format!(
                    "{}: direct sleep API `{api}` must go through host-runtime timers",
                    canonical.display()
                ));
            }
        }
        if let Some(api) = macro_expanded_api(&source, SLEEP_APIS, &aliases) {
            report.violations.push(format!(
                "{}: macro-expanded sleep API `{api}` must go through host-runtime timers",
                canonical.display()
            ));
        }
    }

    let (references, unresolved) = rust_source_references(&text);
    for kind in unresolved {
        report.violations.push(format!(
            "{}: {kind} Rust source path must be a static string literal",
            canonical.display()
        ));
    }
    let parent = canonical.parent().unwrap_or(source_root);
    for reference in references {
        scan_rust_file(
            &parent.join(reference),
            module,
            source_root,
            visited,
            report,
        );
    }
}

fn rust_source_references(text: &str) -> (Vec<String>, Vec<&'static str>) {
    let chars = text.chars().collect::<Vec<_>>();
    let mut references = Vec::new();
    let mut unresolved = Vec::new();
    let mut index = 0;
    while index < chars.len() {
        match (chars[index], chars.get(index + 1).copied()) {
            ('/', Some('/' | '*')) => {
                index = skip_rust_trivia(&chars, index);
            }
            ('"', _) => index = skip_quoted(&chars, index + 1, '"'),
            ('\'', _) if looks_like_character(&chars, index) => {
                index = skip_quoted(&chars, index + 1, '\'');
            }
            ('r', _) if raw_string_start(&chars, index).is_some() => {
                let (hashes, consumed) = raw_string_start(&chars, index).unwrap_or_default();
                index = skip_raw_string(&chars, index + consumed, hashes);
            }
            ('#', _) => {
                let cursor = skip_rust_trivia(&chars, index + 1);
                if chars.get(cursor) == Some(&'[') {
                    let (paths, end) = rust_attribute_paths(&chars, cursor);
                    references.extend(paths);
                    index = end;
                } else {
                    index += 1;
                }
            }
            (character, _) if character.is_ascii_alphabetic() || character == '_' => {
                let (identifier, end) = rust_identifier(&chars, index);
                if identifier == "include" {
                    let mut cursor = skip_rust_trivia(&chars, end);
                    if chars.get(cursor) == Some(&'!') {
                        cursor = skip_rust_trivia(&chars, cursor + 1);
                        if chars
                            .get(cursor)
                            .is_some_and(|character| matches!(character, '(' | '[' | '{'))
                        {
                            cursor = skip_rust_trivia(&chars, cursor + 1);
                            if let Some((path, end)) = rust_string_literal(&chars, cursor) {
                                references.push(path);
                                index = end;
                                continue;
                            }
                            unresolved.push("include!");
                        }
                    }
                }
                index = end;
            }
            _ => index += 1,
        }
    }
    references.sort();
    references.dedup();
    unresolved.sort_unstable();
    unresolved.dedup();
    (references, unresolved)
}

fn rust_attribute_paths(chars: &[char], opener: usize) -> (Vec<String>, usize) {
    let mut paths = Vec::new();
    let mut depth = 1_u32;
    let mut index = opener + 1;
    while index < chars.len() && depth > 0 {
        index = skip_rust_trivia(chars, index);
        let Some(character) = chars.get(index).copied() else {
            break;
        };
        if character == '[' {
            depth += 1;
            index += 1;
        } else if character == ']' {
            depth -= 1;
            index += 1;
        } else if character.is_ascii_alphabetic() || character == '_' {
            let (identifier, end) = rust_identifier(chars, index);
            index = end;
            if identifier == "path" {
                let mut cursor = skip_rust_trivia(chars, index);
                if chars.get(cursor) == Some(&'=') {
                    cursor = skip_rust_trivia(chars, cursor + 1);
                    if let Some((path, end)) = rust_string_literal(chars, cursor) {
                        paths.push(path);
                        index = end;
                    }
                }
            }
        } else if character == '"' {
            index = skip_quoted(chars, index + 1, '"');
        } else if character == 'r' && raw_string_start(chars, index).is_some() {
            let (hashes, consumed) = raw_string_start(chars, index).unwrap_or_default();
            index = skip_raw_string(chars, index + consumed, hashes);
        } else {
            index += 1;
        }
    }
    (paths, index)
}

fn rust_identifier(chars: &[char], start: usize) -> (String, usize) {
    let mut end = start + 1;
    while chars
        .get(end)
        .is_some_and(|character| character.is_ascii_alphanumeric() || *character == '_')
    {
        end += 1;
    }
    (chars[start..end].iter().collect(), end)
}

fn skip_rust_trivia(chars: &[char], mut index: usize) -> usize {
    loop {
        while chars
            .get(index)
            .is_some_and(|character| character.is_whitespace())
        {
            index += 1;
        }
        match (chars.get(index), chars.get(index + 1)) {
            (Some('/'), Some('/')) => {
                index += 2;
                while chars.get(index).is_some_and(|character| *character != '\n') {
                    index += 1;
                }
            }
            (Some('/'), Some('*')) => index = skip_block_comment(chars, index + 2),
            _ => return index,
        }
    }
}

fn rust_string_literal(chars: &[char], start: usize) -> Option<(String, usize)> {
    if chars.get(start) == Some(&'r') {
        let (hashes, consumed) = raw_string_start(chars, start)?;
        let content_start = start + consumed;
        let mut end = content_start;
        while end < chars.len() && !raw_string_end(chars, end, hashes) {
            end += 1;
        }
        if end == chars.len() {
            return None;
        }
        return Some((chars[content_start..end].iter().collect(), end + hashes + 1));
    }
    if chars.get(start) != Some(&'"') {
        return None;
    }
    let mut value = String::new();
    let mut index = start + 1;
    while let Some(character) = chars.get(index).copied() {
        index += 1;
        match character {
            '"' => return Some((value, index)),
            '\\' => {
                let escaped = chars.get(index).copied()?;
                index += 1;
                match escaped {
                    '"' | '\\' => value.push(escaped),
                    'n' => value.push('\n'),
                    'r' => value.push('\r'),
                    't' => value.push('\t'),
                    '0' => value.push('\0'),
                    _ => return None,
                }
            }
            other => value.push(other),
        }
    }
    None
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

#[derive(Debug)]
struct MacroRule {
    name: String,
    matcher: Vec<MacroMatcherToken>,
    expansion: Vec<String>,
}

#[derive(Debug)]
enum MacroMatcherToken {
    Literal(String),
    Binding { name: String, fragment: String },
}

fn macro_expanded_api<'a>(
    source: &str,
    apis: &'a [&str],
    aliases: &[(String, String)],
) -> Option<&'a str> {
    let tokens = rust_tokens(source);
    for rule in parse_macro_rules(&tokens) {
        let mut index = 0;
        while index + 2 < tokens.len() {
            if tokens[index] != rule.name
                || tokens.get(index + 1).is_none_or(|token| token != "!")
                || tokens
                    .get(index + 2)
                    .is_none_or(|token| !is_group_opener(token))
            {
                index += 1;
                continue;
            }
            let opener = index + 2;
            let Some(closer) = matching_group_end(&tokens, opener) else {
                break;
            };
            if let Some(bindings) = match_macro_invocation(&rule, &tokens[opener + 1..closer]) {
                let expanded = expand_macro_rule(&rule, &bindings);
                let code = expanded.join("");
                if let Some(api) = apis
                    .iter()
                    .copied()
                    .find(|api| contains_api(&code, api, aliases))
                {
                    return Some(api);
                }
            }
            index = closer + 1;
        }
    }
    None
}

fn parse_macro_rules(tokens: &[String]) -> Vec<MacroRule> {
    let mut rules = Vec::new();
    let mut index = 0;
    while index + 3 < tokens.len() {
        if tokens[index] != "macro_rules"
            || tokens[index + 1] != "!"
            || !is_group_opener(&tokens[index + 3])
        {
            index += 1;
            continue;
        }
        let name = tokens[index + 2].clone();
        let outer_opener = index + 3;
        let Some(outer_closer) = matching_group_end(tokens, outer_opener) else {
            break;
        };
        let mut cursor = outer_opener + 1;
        while cursor < outer_closer {
            if !is_group_opener(&tokens[cursor]) {
                cursor += 1;
                continue;
            }
            let matcher_opener = cursor;
            let Some(matcher_closer) = matching_group_end(tokens, matcher_opener) else {
                break;
            };
            if tokens
                .get(matcher_closer + 1)
                .is_none_or(|token| token != "=>")
                || tokens
                    .get(matcher_closer + 2)
                    .is_none_or(|token| !is_group_opener(token))
            {
                cursor = matcher_closer + 1;
                continue;
            }
            let expansion_opener = matcher_closer + 2;
            let Some(expansion_closer) = matching_group_end(tokens, expansion_opener) else {
                break;
            };
            let Some(matcher) = parse_macro_matcher(&tokens[matcher_opener + 1..matcher_closer])
            else {
                cursor = expansion_closer + 1;
                continue;
            };
            rules.push(MacroRule {
                name: name.clone(),
                matcher,
                expansion: tokens[expansion_opener + 1..expansion_closer].to_vec(),
            });
            cursor = expansion_closer + 1;
        }
        index = outer_closer + 1;
    }
    rules
}

fn parse_macro_matcher(tokens: &[String]) -> Option<Vec<MacroMatcherToken>> {
    let mut matcher = Vec::new();
    let mut index = 0;
    while index < tokens.len() {
        if tokens[index] != "$" {
            matcher.push(MacroMatcherToken::Literal(tokens[index].clone()));
            index += 1;
            continue;
        }
        let name = tokens.get(index + 1)?.clone();
        let fragment = tokens.get(index + 2)?.clone();
        if !is_identifier_token(&name) || !is_identifier_token(&fragment) {
            return None;
        }
        matcher.push(MacroMatcherToken::Binding { name, fragment });
        index += 3;
    }
    Some(matcher)
}

fn match_macro_invocation(
    rule: &MacroRule,
    invocation: &[String],
) -> Option<BTreeMap<String, Vec<String>>> {
    match_macro_tokens(&rule.matcher, invocation, 0, 0, BTreeMap::new())
}

fn match_macro_tokens(
    matcher: &[MacroMatcherToken],
    invocation: &[String],
    matcher_index: usize,
    invocation_index: usize,
    bindings: BTreeMap<String, Vec<String>>,
) -> Option<BTreeMap<String, Vec<String>>> {
    let Some(token) = matcher.get(matcher_index) else {
        return (invocation_index == invocation.len()).then_some(bindings);
    };
    match token {
        MacroMatcherToken::Literal(expected) => {
            (invocation.get(invocation_index) == Some(expected)).then_some(())?;
            match_macro_tokens(
                matcher,
                invocation,
                matcher_index + 1,
                invocation_index + 1,
                bindings,
            )
        }
        MacroMatcherToken::Binding { name, fragment } => {
            for end in macro_fragment_ends(fragment, invocation, invocation_index) {
                let value = invocation[invocation_index..end].to_vec();
                if bindings.get(name).is_some_and(|bound| bound != &value) {
                    continue;
                }
                let mut next_bindings = bindings.clone();
                next_bindings.insert(name.clone(), value);
                if let Some(matched) =
                    match_macro_tokens(matcher, invocation, matcher_index + 1, end, next_bindings)
                {
                    return Some(matched);
                }
            }
            None
        }
    }
}

fn macro_fragment_ends(fragment: &str, tokens: &[String], start: usize) -> Vec<usize> {
    let Some(first) = tokens.get(start) else {
        return Vec::new();
    };
    if fragment == "ident" {
        return is_identifier_token(first)
            .then_some(vec![start + 1])
            .unwrap_or_default();
    }
    if fragment == "tt" {
        let end = if is_group_opener(first) {
            matching_group_end(tokens, start).map(|end| end + 1)
        } else {
            Some(start + 1)
        };
        return end.into_iter().collect();
    }
    if fragment == "block" {
        return (first == "{")
            .then(|| matching_group_end(tokens, start).map(|end| end + 1))
            .flatten()
            .into_iter()
            .collect();
    }

    let mut ends = Vec::new();
    let mut depth = 0_u32;
    for (index, token) in tokens.iter().enumerate().skip(start) {
        if is_group_opener(token) {
            depth += 1;
        } else if is_group_closer(token) {
            depth = depth.saturating_sub(1);
        }
        if depth == 0 {
            ends.push(index + 1);
        }
    }
    ends
}

fn expand_macro_rule(rule: &MacroRule, bindings: &BTreeMap<String, Vec<String>>) -> Vec<String> {
    let mut expanded = Vec::new();
    let mut index = 0;
    while index < rule.expansion.len() {
        if rule.expansion[index] == "$" {
            if let Some(parameter) = rule.expansion.get(index + 1) {
                if let Some(value) = bindings.get(parameter) {
                    expanded.extend(value.iter().cloned());
                    index += 2;
                    continue;
                }
            }
        }
        expanded.push(rule.expansion[index].clone());
        index += 1;
    }
    expanded
}

fn is_group_opener(token: &str) -> bool {
    matches!(token, "(" | "[" | "{")
}

fn is_group_closer(token: &str) -> bool {
    matches!(token, ")" | "]" | "}")
}

fn is_identifier_token(token: &str) -> bool {
    token
        .chars()
        .next()
        .is_some_and(|character| character.is_ascii_alphabetic() || character == '_')
        && token
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || character == '_')
}

fn matching_group_end(tokens: &[String], opener: usize) -> Option<usize> {
    let mut stack = vec![tokens.get(opener)?.as_str()];
    for (index, token) in tokens.iter().enumerate().skip(opener + 1) {
        if is_group_opener(token) {
            stack.push(token);
            continue;
        }
        let closes = matches!(
            (stack.last().copied(), token.as_str()),
            (Some("("), ")") | (Some("["), "]") | (Some("{"), "}")
        );
        if closes {
            stack.pop();
            if stack.is_empty() {
                return Some(index);
            }
        }
    }
    None
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
        } else if character == '=' && chars.get(index + 1) == Some(&'>') {
            tokens.push("=>".to_owned());
            index += 2;
        } else {
            if matches!(
                character,
                '{' | '}' | '(' | ')' | '[' | ']' | ',' | ';' | '*' | '!' | '$'
            ) {
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

fn normalize_raw_identifiers(text: &str) -> String {
    let chars = text.chars().collect::<Vec<_>>();
    let mut output = String::with_capacity(text.len());
    let mut index = 0;
    while index < chars.len() {
        if chars[index] == 'r'
            && chars.get(index + 1) == Some(&'#')
            && chars
                .get(index + 2)
                .is_some_and(|character| character.is_alphabetic() || *character == '_')
        {
            index += 2;
        } else {
            output.push(chars[index]);
            index += 1;
        }
    }
    output
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
            "fn launch(builder: std::thread::Builder) { builder.spawn(|| {}); }",
            "use std::thread as threads; fn launch(builder: threads::Builder) { builder.spawn(|| {}); }",
            "use std::thread::*; fn launch(builder: Builder) { builder.spawn(|| {}); }",
            "fn launch(builder: std::thread::r#Builder) { builder.spawn(|| {}); }",
            "let _ = std::thread::r#spawn(|| {});",
            "std::thread::r#sleep(duration);",
            "let _ = std::sync::mpsc::r#channel::<u8>();",
        ] {
            let root = fixture();
            fs::write(root.join("modules/session/src/lib.rs"), forbidden).unwrap();
            let report = scan(&root);
            assert!(!report.violations.is_empty(), "not rejected: {forbidden}");
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn macro_expansion_cannot_hide_forbidden_source_apis() {
        for forbidden in [
            r"
macro_rules! call_thread {
    ($api:ident) => { let _ = std::thread::$api(|| {}); };
}
fn launch() { call_thread!(spawn); }
",
            r"
macro_rules! call_timer {
    ($root:ident, $module:ident, $api:ident) => { $root::$module::$api(duration); };
}
fn wait() { call_timer!(std, thread, sleep); }
",
            r"
macro_rules! make_channel {
    ($api:ident) => { std::sync::mpsc::$api::<u8>() };
}
fn mailbox() { let _ = make_channel!(channel); }
",
            r"
macro_rules! call_thread {
    (go $api:ident) => { let _ = std::thread::$api(|| {}); };
}
fn launch() { call_thread!(go spawn); }
",
        ] {
            let root = fixture();
            fs::write(root.join("modules/session/src/lib.rs"), forbidden).unwrap();
            let report = scan(&root);
            assert!(!report.violations.is_empty(), "not rejected: {forbidden}");
            fs::remove_dir_all(root).unwrap();
        }

        let root = fixture();
        fs::write(
            root.join("modules/session/src/lib.rs"),
            r"
fn legal() {
    let channel = 1;
    let spawn = true;
    let sleep = false;
    assert_eq!(channel, 1);
    assert!(spawn && !sleep);
}
",
        )
        .unwrap();
        assert!(
            scan(&root).violations.is_empty(),
            "ordinary macro arguments were rejected"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn included_rust_sources_are_scanned_regardless_of_extension() {
        for root_source in [
            "include!(\"thread.inc\");",
            "#[path = \"thread.inc\"] mod thread_impl;",
        ] {
            let root = fixture();
            fs::write(root.join("modules/session/src/lib.rs"), root_source).unwrap();
            fs::write(
                root.join("modules/session/src/thread.inc"),
                "pub fn launch() { std::thread::spawn(|| {}); }",
            )
            .unwrap();
            let report = scan(&root);
            assert!(
                report
                    .violations
                    .iter()
                    .any(|violation| violation.contains("spawn")),
                "included Rust source was not scanned: {root_source}"
            );
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
