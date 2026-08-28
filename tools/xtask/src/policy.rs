#[path = "dag.rs"]
mod dag;
#[path = "queues.rs"]
mod queues;
#[path = "source_scan.rs"]
mod source_scan;

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

const DAG_GUARD: &str = ".spec/guards/module-dag.toml";
const QUEUE_GUARD: &str = ".spec/guards/queue-contracts.toml";
const INVALID_CYCLE: &str = "tests/policy/invalid_cycle.toml";
const INVALID_QUEUE: &str = "tests/policy/invalid_queue.toml";

#[derive(Clone, Debug, Eq, PartialEq)]
enum Value {
    String(String),
    Array(Vec<String>),
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct Document {
    values: BTreeMap<String, Value>,
    tables: BTreeMap<String, Vec<BTreeMap<String, Value>>>,
}

fn parse_document(text: &str) -> Result<Document, String> {
    let mut document = Document::default();
    let mut current_table: Option<(String, usize)> = None;
    let lines: Vec<&str> = text.lines().collect();
    let mut line_index = 0;

    while line_index < lines.len() {
        let line_number = line_index + 1;
        let line = strip_comment(lines[line_index]).trim().to_owned();
        line_index += 1;
        if line.is_empty() {
            continue;
        }

        if line.starts_with("[[") && line.ends_with("]]") {
            let name = line[2..line.len() - 2].trim();
            if name.is_empty() {
                return Err(format!("line {line_number}: empty array-table name"));
            }
            let entries = document.tables.entry(name.to_owned()).or_default();
            entries.push(BTreeMap::new());
            current_table = Some((name.to_owned(), entries.len() - 1));
            continue;
        }
        if line.starts_with('[') {
            return Err(format!(
                "line {line_number}: regular TOML tables are not supported by policy manifests"
            ));
        }

        let Some((key, raw_value)) = line.split_once('=') else {
            return Err(format!("line {line_number}: expected `key = value`"));
        };
        let key = key.trim();
        if key.is_empty() {
            return Err(format!("line {line_number}: empty key"));
        }

        let mut raw_value = raw_value.trim().to_owned();
        if raw_value.starts_with('[') {
            while !array_is_closed(&raw_value) {
                if line_index >= lines.len() {
                    return Err(format!("line {line_number}: unterminated array"));
                }
                let continuation = strip_comment(lines[line_index]);
                line_index += 1;
                raw_value.push('\n');
                raw_value.push_str(continuation.trim());
            }
        }
        let value = parse_value(&raw_value)
            .map_err(|error| format!("line {line_number}, key `{key}`: {error}"))?;

        let target = if let Some((table_name, table_index)) = &current_table {
            document
                .tables
                .get_mut(table_name)
                .and_then(|tables| tables.get_mut(*table_index))
                .expect("current array table exists")
        } else {
            &mut document.values
        };
        if target.insert(key.to_owned(), value).is_some() {
            return Err(format!("line {line_number}: duplicate key `{key}`"));
        }
    }

    Ok(document)
}

fn strip_comment(line: &str) -> &str {
    let mut in_string = false;
    let mut escaped = false;
    for (index, character) in line.char_indices() {
        if in_string {
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                in_string = false;
            }
        } else if character == '"' {
            in_string = true;
        } else if character == '#' {
            return &line[..index];
        }
    }
    line
}

fn markdown_section(text: &str, anchor: &str) -> Option<String> {
    let mut section = Vec::new();
    let mut level = None;
    let mut fence = None;
    let mut html_block = None;
    let mut in_html_comment = false;
    let mut paragraph_open = false;
    for raw_line in text.lines() {
        if let Some((marker, width)) = fence {
            if level.is_some() {
                section.push(raw_line.to_owned());
            }
            if markdown_fence_marker(raw_line).is_some_and(
                |(candidate, candidate_width, suffix)| {
                    candidate == marker && candidate_width >= width && suffix.trim().is_empty()
                },
            ) {
                fence = None;
            }
            continue;
        }

        if let Some(block) = html_block {
            if markdown_html_block_ends(block, raw_line) {
                html_block = None;
            }
            paragraph_open = false;
            continue;
        }

        let was_in_html_comment = in_html_comment;
        let line = markdown_without_html_comments(raw_line, &mut in_html_comment);
        if let Some(block) = markdown_html_block_start(&line, !paragraph_open) {
            if !was_in_html_comment {
                in_html_comment = false;
            }
            if !markdown_html_block_ends(block, &line) {
                html_block = Some(block);
            }
            paragraph_open = false;
            continue;
        }
        if markdown_indented_code(&line) {
            if level.is_some() {
                section.push(String::new());
            }
            paragraph_open = false;
            continue;
        }
        if let Some((marker, width, _)) = markdown_fence_marker(&line) {
            fence = Some((marker, width));
            if level.is_some() {
                section.push(line);
            }
            paragraph_open = false;
            continue;
        }

        let heading_level = markdown_heading_level(&line);
        if level.is_none() {
            if heading_level.is_some_and(|heading_level| {
                let trimmed = line.trim_start_matches(' ');
                trimmed[heading_level..].split_whitespace().next() == Some(anchor)
            }) {
                level = heading_level;
            }
            paragraph_open = heading_level.is_none() && !line.trim().is_empty();
            continue;
        }
        if heading_level.is_some_and(|heading_level| heading_level <= level.unwrap_or(0)) {
            break;
        }
        paragraph_open = heading_level.is_none() && !line.trim().is_empty();
        section.push(line);
    }
    level.map(|_| section.join("\n"))
}

#[derive(Clone, Copy)]
enum MarkdownHtmlBlock {
    ClosingTag(&'static str),
    Terminator(&'static str),
    BlankLine,
}

const MARKDOWN_HTML_BLOCK_TAGS: &[&str] = &[
    "address",
    "article",
    "aside",
    "base",
    "basefont",
    "blockquote",
    "body",
    "caption",
    "center",
    "col",
    "colgroup",
    "dd",
    "details",
    "dialog",
    "dir",
    "div",
    "dl",
    "dt",
    "fieldset",
    "figcaption",
    "figure",
    "footer",
    "form",
    "frame",
    "frameset",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
    "head",
    "header",
    "hr",
    "html",
    "iframe",
    "legend",
    "li",
    "link",
    "main",
    "menu",
    "menuitem",
    "nav",
    "noframes",
    "ol",
    "optgroup",
    "option",
    "p",
    "param",
    "search",
    "section",
    "summary",
    "table",
    "tbody",
    "td",
    "tfoot",
    "th",
    "thead",
    "title",
    "tr",
    "track",
    "ul",
];

fn markdown_html_block_start(line: &str, allow_type_seven: bool) -> Option<MarkdownHtmlBlock> {
    if markdown_indented_code(line) {
        return None;
    }
    let trimmed = line.trim_start_matches(' ');
    let lower = trimmed.to_ascii_lowercase();
    for tag in ["pre", "script", "style", "textarea"] {
        if html_tag_at_start(&lower, tag) {
            return Some(MarkdownHtmlBlock::ClosingTag(tag));
        }
    }
    if lower.starts_with("<?") {
        return Some(MarkdownHtmlBlock::Terminator("?>"));
    }
    if lower.starts_with("<![cdata[") {
        return Some(MarkdownHtmlBlock::Terminator("]]>"));
    }
    if trimmed
        .strip_prefix("<!")
        .and_then(|suffix| suffix.chars().next())
        .is_some_and(|character| character.is_ascii_uppercase())
    {
        return Some(MarkdownHtmlBlock::Terminator(">"));
    }
    let standard_block = MARKDOWN_HTML_BLOCK_TAGS
        .iter()
        .any(|tag| html_tag_at_start(&lower, tag))
        .then_some(MarkdownHtmlBlock::BlankLine);
    standard_block.or_else(|| {
        (allow_type_seven && markdown_type_seven_html_tag(trimmed))
            .then_some(MarkdownHtmlBlock::BlankLine)
    })
}

fn markdown_type_seven_html_tag(line: &str) -> bool {
    let line = line.trim_end();
    let Some(mut inner) = line
        .strip_prefix('<')
        .and_then(|line| line.strip_suffix('>'))
    else {
        return false;
    };
    let closing = inner.starts_with('/');
    if closing {
        inner = &inner[1..];
    }
    let tag_length = inner
        .char_indices()
        .take_while(|(index, character)| {
            if *index == 0 {
                character.is_ascii_alphabetic()
            } else {
                character.is_ascii_alphanumeric() || *character == '-'
            }
        })
        .map(|(index, character)| index + character.len_utf8())
        .last()
        .unwrap_or(0);
    if tag_length == 0 {
        return false;
    }
    let suffix = &inner[tag_length..];
    if closing {
        return suffix.trim().is_empty();
    }
    if suffix.is_empty() {
        return true;
    }
    if !suffix
        .chars()
        .next()
        .is_some_and(|character| character.is_whitespace() || character == '/')
    {
        return false;
    }

    let mut quote = None;
    for character in suffix.chars() {
        if let Some(active_quote) = quote {
            if character == active_quote {
                quote = None;
            }
        } else if matches!(character, '\'' | '"') {
            quote = Some(character);
        } else if matches!(character, '<' | '>' | '`') {
            return false;
        }
    }
    quote.is_none()
}

fn markdown_html_block_ends(block: MarkdownHtmlBlock, line: &str) -> bool {
    let lower = line.to_ascii_lowercase();
    match block {
        MarkdownHtmlBlock::ClosingTag(tag) => lower.contains(&format!("</{tag}>")),
        MarkdownHtmlBlock::Terminator(terminator) => lower.contains(terminator),
        MarkdownHtmlBlock::BlankLine => line.trim().is_empty(),
    }
}

fn html_tag_at_start(line: &str, tag: &str) -> bool {
    for prefix in [format!("<{tag}"), format!("</{tag}")] {
        if let Some(suffix) = line.strip_prefix(&prefix) {
            if suffix
                .chars()
                .next()
                .is_none_or(|character| character.is_whitespace() || matches!(character, '>' | '/'))
            {
                return true;
            }
        }
    }
    false
}

fn has_markdown_heading(text: &str, anchor: &str) -> bool {
    markdown_section(text, anchor).is_some()
}

fn markdown_heading_level(line: &str) -> Option<usize> {
    if markdown_indented_code(line) {
        return None;
    }
    let trimmed = line.trim_start_matches(' ');
    let level = trimmed
        .chars()
        .take_while(|character| *character == '#')
        .count();
    if !(1..=6).contains(&level) {
        return None;
    }
    trimmed[level..]
        .chars()
        .next()
        .is_none_or(char::is_whitespace)
        .then_some(level)
}

fn markdown_without_html_comments(line: &str, in_comment: &mut bool) -> String {
    let mut visible = String::new();
    let mut remaining = line;
    loop {
        if *in_comment {
            let Some(end) = remaining.find("-->") else {
                return visible;
            };
            remaining = &remaining[end + 3..];
            *in_comment = false;
        } else if let Some(start) = remaining.find("<!--") {
            visible.push_str(&remaining[..start]);
            remaining = &remaining[start + 4..];
            *in_comment = true;
        } else {
            visible.push_str(remaining);
            return visible;
        }
    }
}

fn markdown_indented_code(line: &str) -> bool {
    let mut spaces = 0;
    for character in line.chars() {
        match character {
            ' ' => spaces += 1,
            '\t' => return true,
            _ => break,
        }
    }
    spaces >= 4
}

fn markdown_fence_marker(line: &str) -> Option<(u8, usize, &str)> {
    if markdown_indented_code(line) {
        return None;
    }
    let trimmed = line.trim_start_matches(' ');
    let marker = *trimmed.as_bytes().first()?;
    if !matches!(marker, b'`' | b'~') {
        return None;
    }
    let width = trimmed
        .bytes()
        .take_while(|candidate| *candidate == marker)
        .count();
    if width < 3 {
        return None;
    }
    let suffix = &trimmed[width..];
    if marker == b'`' && suffix.contains('`') {
        return None;
    }
    Some((marker, width, suffix))
}

fn array_is_closed(raw: &str) -> bool {
    let mut in_string = false;
    let mut escaped = false;
    let mut depth = 0_u32;
    for character in raw.chars() {
        if in_string {
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                in_string = false;
            }
            continue;
        }
        match character {
            '"' => in_string = true,
            '[' => depth += 1,
            ']' => depth = depth.saturating_sub(1),
            _ => {}
        }
    }
    depth == 0 && !in_string
}

fn parse_value(raw: &str) -> Result<Value, String> {
    let raw = raw.trim();
    if raw.starts_with('"') {
        return parse_basic_string(raw).map(Value::String);
    }
    if raw.starts_with('[') {
        return parse_string_array(raw).map(Value::Array);
    }
    Err("only basic strings and arrays of basic strings are supported".to_owned())
}

fn parse_basic_string(raw: &str) -> Result<String, String> {
    let chars: Vec<char> = raw.chars().collect();
    if chars.first() != Some(&'"') {
        return Err("expected a basic string".to_owned());
    }
    let mut output = String::new();
    let mut index = 1;
    while index < chars.len() {
        match chars[index] {
            '"' => {
                if chars[index + 1..]
                    .iter()
                    .any(|character| !character.is_whitespace())
                {
                    return Err("unexpected text after closing quote".to_owned());
                }
                return Ok(output);
            }
            '\\' => {
                index += 1;
                let Some(escaped) = chars.get(index) else {
                    return Err("unterminated escape".to_owned());
                };
                output.push(match escaped {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    other => return Err(format!("unsupported escape `\\{other}`")),
                });
            }
            character => output.push(character),
        }
        index += 1;
    }
    Err("unterminated basic string".to_owned())
}

fn parse_string_array(raw: &str) -> Result<Vec<String>, String> {
    let chars: Vec<char> = raw.chars().collect();
    if chars.first() != Some(&'[') {
        return Err("expected an array".to_owned());
    }
    let mut values = Vec::new();
    let mut index = 1;
    loop {
        while chars
            .get(index)
            .is_some_and(|character| character.is_whitespace())
        {
            index += 1;
        }
        if chars.get(index) == Some(&']') {
            index += 1;
            break;
        }
        if chars.get(index) != Some(&'"') {
            return Err("array values must be basic strings".to_owned());
        }

        let start = index;
        index += 1;
        let mut escaped = false;
        while let Some(character) = chars.get(index) {
            if escaped {
                escaped = false;
            } else if *character == '\\' {
                escaped = true;
            } else if *character == '"' {
                index += 1;
                break;
            }
            index += 1;
        }
        if index > chars.len() || chars.get(index.saturating_sub(1)) != Some(&'"') {
            return Err("unterminated string in array".to_owned());
        }
        let encoded: String = chars[start..index].iter().collect();
        values.push(parse_basic_string(&encoded)?);

        while chars
            .get(index)
            .is_some_and(|character| character.is_whitespace())
        {
            index += 1;
        }
        match chars.get(index) {
            Some(',') => index += 1,
            Some(']') => {
                index += 1;
                break;
            }
            _ => return Err("expected `,` or `]` after array value".to_owned()),
        }
    }
    if chars[index..]
        .iter()
        .any(|character| !character.is_whitespace())
    {
        return Err("unexpected text after array".to_owned());
    }
    Ok(values)
}

fn value_string(map: &BTreeMap<String, Value>, key: &str) -> Result<String, String> {
    match map.get(key) {
        Some(Value::String(value)) if !value.trim().is_empty() => Ok(value.clone()),
        Some(Value::String(_)) => Err(format!("`{key}` must not be empty")),
        Some(Value::Array(_)) => Err(format!("`{key}` must be a string")),
        None => Err(format!("missing `{key}`")),
    }
}

fn value_array(map: &BTreeMap<String, Value>, key: &str) -> Result<Vec<String>, String> {
    match map.get(key) {
        Some(Value::Array(values)) => Ok(values.clone()),
        Some(Value::String(_)) => Err(format!("`{key}` must be an array")),
        None => Err(format!("missing `{key}`")),
    }
}

fn read_document(root: &Path, relative: &str) -> Result<Document, String> {
    let path = root.join(relative);
    let text =
        std::fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
    parse_document(&text).map_err(|error| format!("{}: {error}", path.display()))
}

pub(crate) fn check(root: &Path) -> Result<PolicyReport, Vec<String>> {
    let mut errors = Vec::new();

    let dag_guard = match read_document(root, DAG_GUARD) {
        Ok(document) => match dag::parse_guard(&document) {
            Ok(guard) => Some(guard),
            Err(mut guard_errors) => {
                errors.append(&mut guard_errors);
                None
            }
        },
        Err(error) => {
            errors.push(error);
            None
        }
    };
    let queue_guard = match read_document(root, QUEUE_GUARD) {
        Ok(document) => match queues::parse_guard(&document) {
            Ok(guard) => Some(guard),
            Err(mut guard_errors) => {
                errors.append(&mut guard_errors);
                None
            }
        },
        Err(error) => {
            errors.push(error);
            None
        }
    };

    if let Some(guard) = &dag_guard {
        errors.extend(dag::check_live(root, guard));
    }
    if let Some(guard) = &queue_guard {
        errors.extend(queues::check_registry(root, guard));
    }
    if let (Some(dag), Some(queues)) = (&dag_guard, &queue_guard) {
        for module in queues.modules() {
            if !dag.modules.contains_key(module) {
                errors.push(format!(
                    "queue contract references unknown module `{module}`"
                ));
            }
        }
    }

    let scan = source_scan::scan(root);
    errors.extend(scan.violations.clone());
    verify_mutation_fixtures(root, &mut errors);
    errors.sort();
    errors.dedup();
    if !errors.is_empty() {
        return Err(errors);
    }

    let dag_guard = dag_guard.expect("validated DAG guard exists");
    let queue_guard = queue_guard.expect("validated queue guard exists");
    Ok(PolicyReport {
        modules: dag_guard.modules.len(),
        compile_edges: dag_guard.compile_edges.len(),
        command_edges: dag_guard.command_edges,
        event_edges: dag_guard.event_edges,
        queues: queue_guard.count(),
        source_files: scan.files,
    })
}

fn verify_mutation_fixtures(root: &Path, errors: &mut Vec<String>) {
    match read_document(root, INVALID_CYCLE) {
        Ok(document) => match dag::parse_guard(&document) {
            Err(fixture_errors) if fixture_errors.iter().any(|error| error.contains("cycle")) => {}
            Err(fixture_errors) => errors.push(format!(
                "{INVALID_CYCLE}: did not produce the expected cycle error: {fixture_errors:?}"
            )),
            Ok(_) => errors.push(format!("{INVALID_CYCLE}: invalid cycle was accepted")),
        },
        Err(error) => errors.push(error),
    }
    match read_document(root, INVALID_QUEUE) {
        Ok(document) => match queues::parse_guard(&document) {
            Err(fixture_errors)
                if ["owner", "full", "close"]
                    .iter()
                    .all(|field| fixture_errors.iter().any(|error| error.contains(field))) => {}
            Err(fixture_errors) => errors.push(format!(
                "{INVALID_QUEUE}: missing contract fields were not all rejected: {fixture_errors:?}"
            )),
            Ok(_) => errors.push(format!("{INVALID_QUEUE}: incomplete queue was accepted")),
        },
        Err(error) => errors.push(error),
    }
}

fn workspace_root() -> PathBuf {
    if let Some(manifest_dir) = option_env!("CARGO_MANIFEST_DIR") {
        let manifest_dir = Path::new(manifest_dir);
        if let Some(root) = manifest_dir.parent().and_then(Path::parent) {
            return root.to_path_buf();
        }
    }
    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

pub(crate) fn command() -> ExitCode {
    match check(&workspace_root()) {
        Ok(report) => {
            println!(
                "policy check OK: {} modules, {} compile edges, {} command edges, {} event/ack edges, {} queues, {} production files scanned",
                report.modules,
                report.compile_edges,
                report.command_edges,
                report.event_edges,
                report.queues,
                report.source_files
            );
            ExitCode::SUCCESS
        }
        Err(errors) => {
            for error in errors {
                eprintln!("FAIL {error}");
            }
            ExitCode::FAILURE
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub(crate) struct PolicyReport {
    pub(crate) modules: usize,
    pub(crate) compile_edges: usize,
    pub(crate) command_edges: usize,
    pub(crate) event_edges: usize,
    pub(crate) queues: usize,
    pub(crate) source_files: usize,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_scalar_array_and_array_table() {
        let document = parse_document(
            r#"
baseline = "LGE-V1.4-2026-08-27"
edges = ["a -> b", "b -> c"]
[[module]]
name = "a"
"#,
        )
        .expect("parse fixture");

        assert_eq!(
            value_string(&document.values, "baseline").unwrap(),
            "LGE-V1.4-2026-08-27"
        );
        assert_eq!(value_array(&document.values, "edges").unwrap().len(), 2);
        assert_eq!(document.tables["module"].len(), 1);
    }

    #[test]
    fn current_tree_satisfies_policy() {
        let report = check(&workspace_root()).unwrap_or_else(|errors| {
            panic!("policy errors:\n{}", errors.join("\n"));
        });
        assert_eq!(report.modules, 15);
        assert_eq!(report.queues, 37);
        assert_eq!(command(), ExitCode::SUCCESS);
    }
}
