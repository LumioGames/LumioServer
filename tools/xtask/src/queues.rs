use super::{value_array, value_string, Document, Value};
use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;

const BASELINE: &str = "LGE-V1.4-2026-08-27";
const DESIGN_SNAPSHOT: &str = "LGE-V1.2-2026-08-27";
const REGISTRY_FIELDS: &[&str] = &[
    "module",
    "queue",
    "element",
    "owner",
    "producers",
    "consumers",
    "ordering",
    "capacity_config",
    "full_action",
    "close_semantics",
];
const INLINE_FIELDS: &[&str] = &[
    "module",
    "name",
    "element",
    "owner",
    "producers",
    "consumers",
    "ordering",
    "capacity",
    "full",
    "close",
];
const TRACE_FIELDS: &[&str] = &[
    "queue",
    "owner",
    "producer/consumer",
    "ordering",
    "capacity",
    "full",
    "close",
];

#[derive(Clone, Debug, Default)]
pub(super) struct QueueGuard {
    expected: BTreeSet<(String, String)>,
    inline: bool,
    baseline_source: String,
    registry_source: String,
    registry_trace_rows: BTreeSet<String>,
    registry_trace_contracts: BTreeMap<String, String>,
    design_registry: Option<String>,
    required_fields: BTreeSet<String>,
}

pub(super) fn parse_guard(document: &Document) -> Result<QueueGuard, Vec<String>> {
    let mut errors = Vec::new();
    let baseline = capture(value_string(&document.values, "baseline"), &mut errors);
    if baseline.as_deref().is_some_and(|value| value != BASELINE) {
        errors.push(format!(
            "queue guard baseline must be `{BASELINE}`, got `{}`",
            baseline.unwrap()
        ));
    }
    let registry_source = capture(
        value_string(&document.values, "registry_source"),
        &mut errors,
    )
    .unwrap_or_default();
    let baseline_source = capture(
        value_string(&document.values, "baseline_source"),
        &mut errors,
    )
    .unwrap_or_default();

    let (inline, mut expected) = parse_inline_contracts(document, &mut errors);
    let design_registry = optional_string(&document.values, "design_registry", &mut errors);
    let registry_trace_rows = optional_array(&document.values, "registry_trace_rows", &mut errors)
        .into_iter()
        .collect::<BTreeSet<_>>();
    let registry_trace_contracts = parse_registry_trace_contracts(&document.values, &mut errors);
    validate_registry_snapshot(&document.values, design_registry.is_some(), &mut errors);
    let required_fields = optional_array(&document.values, "required_fields", &mut errors)
        .into_iter()
        .collect::<BTreeSet<_>>();
    let listed_contracts = optional_array(&document.values, "contracts", &mut errors);
    for contract in listed_contracts {
        let Some((module, name)) = contract.split_once("::") else {
            errors.push(format!(
                "queue contract id `{contract}` must be `module::queue`"
            ));
            continue;
        };
        if module.trim().is_empty() || name.trim().is_empty() {
            errors.push(format!(
                "queue contract id `{contract}` has an empty component"
            ));
        } else if !expected.insert((module.trim().to_owned(), name.trim().to_owned())) {
            errors.push(format!("duplicate queue contract id `{contract}`"));
        }
    }

    validate_external_guard(
        inline,
        design_registry.as_deref(),
        &expected,
        &registry_trace_rows,
        &registry_trace_contracts,
        &required_fields,
        &mut errors,
    );

    if errors.is_empty() {
        Ok(QueueGuard {
            expected,
            inline,
            baseline_source,
            registry_source,
            registry_trace_rows,
            registry_trace_contracts,
            design_registry,
            required_fields,
        })
    } else {
        errors.sort();
        errors.dedup();
        Err(errors)
    }
}

fn parse_inline_contracts(
    document: &Document,
    errors: &mut Vec<String>,
) -> (bool, BTreeSet<(String, String)>) {
    let tables = document.tables.get("queue");
    let inline = tables.is_some_and(|tables| !tables.is_empty());
    let mut expected = BTreeSet::new();
    for (index, table) in tables.into_iter().flatten().enumerate() {
        let label = format!("queue[{index}]");
        for field in INLINE_FIELDS {
            match table.get(*field) {
                Some(Value::String(value)) if !value.trim().is_empty() => {}
                Some(Value::String(_)) => {
                    errors.push(format!("{label}: `{field}` must not be empty"));
                }
                Some(Value::Array(_)) => {
                    errors.push(format!("{label}: `{field}` must be a string"));
                }
                None => errors.push(format!("{label}: missing `{field}`")),
            }
        }
        let module = table_string(table, "module");
        let name = table_string(table, "name");
        if let (Some(module), Some(name)) = (module, name) {
            if !expected.insert((module.clone(), name.clone())) {
                errors.push(format!("duplicate queue contract `{module}::{name}`"));
            }
        }
    }
    (inline, expected)
}

fn validate_external_guard(
    inline: bool,
    design_registry: Option<&str>,
    expected: &BTreeSet<(String, String)>,
    registry_trace_rows: &BTreeSet<String>,
    registry_trace_contracts: &BTreeMap<String, String>,
    required_fields: &BTreeSet<String>,
    errors: &mut Vec<String>,
) {
    if inline {
        return;
    }
    if design_registry.is_none() {
        errors.push("queue guard must define `design_registry` or inline queues".to_owned());
    }
    if expected.is_empty() {
        errors.push("queue guard must list expected `contracts`".to_owned());
    }
    if registry_trace_rows.is_empty() {
        errors.push("queue guard must list expected `registry_trace_rows`".to_owned());
    }
    if registry_trace_contracts.is_empty() {
        errors.push("queue guard must list expected `registry_trace_contracts`".to_owned());
    }
    let contract_rows = registry_trace_contracts
        .keys()
        .cloned()
        .collect::<BTreeSet<_>>();
    for missing in registry_trace_rows.difference(&contract_rows) {
        errors.push(format!(
            "queue guard trace contract is missing row `{missing}`"
        ));
    }
    for extra in contract_rows.difference(registry_trace_rows) {
        errors.push(format!(
            "queue guard trace contract has unregistered row `{extra}`"
        ));
    }
    for field in REGISTRY_FIELDS {
        if !required_fields.contains(*field) {
            errors.push(format!("queue guard required_fields is missing `{field}`"));
        }
    }
}

fn validate_registry_snapshot(
    values: &BTreeMap<String, Value>,
    has_registry: bool,
    errors: &mut Vec<String>,
) {
    if !has_registry {
        return;
    }
    match value_string(values, "registry_snapshot") {
        Ok(snapshot) if snapshot == DESIGN_SNAPSHOT => {}
        Ok(snapshot) => errors.push(format!(
            "queue design registry snapshot must be `{DESIGN_SNAPSHOT}`, got `{snapshot}`"
        )),
        Err(error) => errors.push(error),
    }
}

fn capture<T>(result: Result<T, String>, errors: &mut Vec<String>) -> Option<T> {
    match result {
        Ok(value) => Some(value),
        Err(error) => {
            errors.push(error);
            None
        }
    }
}

fn optional_string(
    values: &BTreeMap<String, Value>,
    key: &str,
    errors: &mut Vec<String>,
) -> Option<String> {
    if !values.contains_key(key) {
        return None;
    }
    capture(value_string(values, key), errors)
}

fn optional_array(
    values: &BTreeMap<String, Value>,
    key: &str,
    errors: &mut Vec<String>,
) -> Vec<String> {
    if !values.contains_key(key) {
        return Vec::new();
    }
    capture(value_array(values, key), errors).unwrap_or_default()
}

fn parse_registry_trace_contracts(
    values: &BTreeMap<String, Value>,
    errors: &mut Vec<String>,
) -> BTreeMap<String, String> {
    let mut contracts = BTreeMap::new();
    for (index, contract) in optional_array(values, "registry_trace_contracts", errors)
        .into_iter()
        .enumerate()
    {
        let cells = contract.split('|').map(str::trim).collect::<Vec<_>>();
        if cells.len() != TRACE_FIELDS.len() {
            errors.push(format!(
                "registry_trace_contracts[{index}] must have {} columns, got {}",
                TRACE_FIELDS.len(),
                cells.len()
            ));
            continue;
        }
        for (field, value) in TRACE_FIELDS.iter().zip(&cells) {
            if value.is_empty() {
                errors.push(format!(
                    "registry_trace_contracts[{index}] is missing `{field}`"
                ));
            }
        }
        let name = cells[0];
        if name.is_empty() {
            continue;
        }
        if contracts
            .insert(name.to_owned(), cells.join(" | "))
            .is_some()
        {
            errors.push(format!("duplicate registry trace contract row `{name}`"));
        }
    }
    contracts
}

fn table_string(table: &BTreeMap<String, Value>, key: &str) -> Option<String> {
    match table.get(key) {
        Some(Value::String(value)) if !value.trim().is_empty() => Some(value.clone()),
        _ => None,
    }
}

impl QueueGuard {
    pub(super) fn count(&self) -> usize {
        self.expected.len()
    }

    pub(super) fn modules(&self) -> impl Iterator<Item = &str> {
        self.expected.iter().map(|(module, _)| module.as_str())
    }
}

pub(super) fn check_registry(root: &Path, guard: &QueueGuard) -> Vec<String> {
    let mut errors = validate_trace_source(root, &guard.baseline_source);
    errors.extend(validate_registry_trace(
        root,
        &guard.registry_source,
        &guard.registry_trace_rows,
        &guard.registry_trace_contracts,
    ));
    if guard.inline {
        return errors;
    }
    let Some(relative) = &guard.design_registry else {
        return errors;
    };
    let path = root.join(relative);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) => {
            errors.push(format!("{}: {error}", path.display()));
            return errors;
        }
    };
    let rows = match parse_flat_json_array(&text) {
        Ok(rows) => rows,
        Err(error) => {
            errors.push(format!("{}: {error}", path.display()));
            return errors;
        }
    };
    let mut actual = BTreeSet::new();
    for (index, row) in rows.iter().enumerate() {
        for field in &guard.required_fields {
            if row.get(field).is_none_or(|value| value.trim().is_empty()) {
                errors.push(format!("queue registry row {index} is missing `{field}`"));
            }
        }
        if let (Some(module), Some(name)) = (row.get("module"), row.get("queue")) {
            if !actual.insert((module.clone(), name.clone())) {
                errors.push(format!("duplicate queue registry row `{module}::{name}`"));
            }
        }
    }
    for missing in guard.expected.difference(&actual) {
        errors.push(format!(
            "queue registry is missing `{}::{}`",
            missing.0, missing.1
        ));
    }
    for extra in actual.difference(&guard.expected) {
        errors.push(format!(
            "queue registry has unguarded `{}::{}`",
            extra.0, extra.1
        ));
    }
    errors
}

fn validate_registry_trace(
    root: &Path,
    source: &str,
    expected: &BTreeSet<String>,
    expected_contracts: &BTreeMap<String, String>,
) -> Vec<String> {
    let (relative, anchor) = source.split_once('#').unwrap_or((source, ""));
    let path = root.join(relative);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) => {
            return vec![format!(
                "queue trace source `{source}` cannot be read: {error}"
            )];
        }
    };
    let Some(section) = markdown_section(&text, anchor) else {
        return vec![format!(
            "queue trace source `{source}` is missing its anchor"
        )];
    };
    let (actual, mut errors) = markdown_queue_rows(&section, source);
    let actual_rows = actual.keys().cloned().collect::<BTreeSet<_>>();
    for missing in expected.difference(&actual_rows) {
        errors.push(format!(
            "queue trace is missing documented queue row `{missing}` in `{source}`"
        ));
    }
    for extra in actual_rows.difference(expected) {
        errors.push(format!(
            "queue trace has unguarded documented queue row `{extra}` in `{source}`"
        ));
    }
    for (name, contract) in expected_contracts {
        if let Some(actual_contract) = actual.get(name) {
            if actual_contract != contract {
                errors.push(format!(
                    "documented queue row `{name}` content drift in `{source}`: expected `{contract}`, got `{actual_contract}`"
                ));
            }
        }
    }
    errors
}

fn markdown_section(text: &str, anchor: &str) -> Option<String> {
    let mut section = Vec::new();
    let mut level = None;
    for line in text.lines() {
        let trimmed = line.trim_start();
        let heading_level = trimmed
            .chars()
            .take_while(|character| *character == '#')
            .count();
        if level.is_none() {
            if heading_level > 0
                && trimmed[heading_level..].split_whitespace().next() == Some(anchor)
            {
                level = Some(heading_level);
            }
            continue;
        }
        if heading_level > 0 && heading_level <= level.unwrap_or(heading_level) {
            break;
        }
        section.push(line);
    }
    level.map(|_| section.join("\n"))
}

fn markdown_queue_rows(section: &str, source: &str) -> (BTreeMap<String, String>, Vec<String>) {
    let mut rows = BTreeMap::new();
    let mut errors = Vec::new();
    let mut fence = None;
    for line in section.lines().map(str::trim) {
        if let Some((marker, width)) = fence {
            if fence_marker(line).is_some_and(|(candidate, candidate_width)| {
                candidate == marker
                    && candidate_width >= width
                    && line[candidate_width..].trim().is_empty()
            }) {
                fence = None;
            }
            continue;
        }
        if let Some(marker) = fence_marker(line) {
            fence = Some(marker);
            continue;
        }
        if !line.starts_with('|') || !line.ends_with('|') {
            continue;
        }
        let cells = line
            .trim_matches('|')
            .split('|')
            .map(str::trim)
            .collect::<Vec<_>>();
        if cells.iter().all(|cell| {
            !cell.is_empty()
                && cell
                    .chars()
                    .all(|character| matches!(character, '-' | ':' | ' '))
        }) || cells
            .first()
            .is_some_and(|cell| cell.eq_ignore_ascii_case("queue") || *cell == "队列")
        {
            continue;
        }
        if cells.len() != TRACE_FIELDS.len() {
            errors.push(format!(
                "queue trace row in `{source}` must have {} columns, got {}: `{line}`",
                TRACE_FIELDS.len(),
                cells.len()
            ));
            continue;
        }
        let name = cells[0];
        for (field, value) in TRACE_FIELDS.iter().zip(&cells) {
            if value.is_empty() {
                errors.push(format!(
                    "queue trace row `{name}` in `{source}` is missing `{field}`"
                ));
            }
        }
        if !name.is_empty() && rows.insert(name.to_owned(), cells.join(" | ")).is_some() {
            errors.push(format!(
                "queue trace source `{source}` duplicates row `{name}`"
            ));
        }
    }
    (rows, errors)
}

fn fence_marker(line: &str) -> Option<(u8, usize)> {
    let marker = *line.as_bytes().first()?;
    if !matches!(marker, b'`' | b'~') {
        return None;
    }
    let width = line
        .bytes()
        .take_while(|candidate| *candidate == marker)
        .count();
    (width >= 3).then_some((marker, width))
}

fn validate_trace_source(root: &Path, source: &str) -> Vec<String> {
    let (relative, anchor) = source.split_once('#').unwrap_or((source, ""));
    match std::fs::read_to_string(root.join(relative)) {
        Ok(text) if anchor.is_empty() || has_markdown_heading(&text, anchor) => Vec::new(),
        Ok(_) => vec![format!(
            "queue trace source `{source}` is missing its anchor"
        )],
        Err(error) => vec![format!(
            "queue trace source `{source}` cannot be read: {error}"
        )],
    }
}

fn has_markdown_heading(text: &str, anchor: &str) -> bool {
    text.lines().any(|line| {
        let line = line.trim_start();
        let heading = line.trim_start_matches('#');
        line.starts_with('#') && heading.split_whitespace().next() == Some(anchor)
    })
}

fn parse_flat_json_array(text: &str) -> Result<Vec<BTreeMap<String, String>>, String> {
    let mut cursor = JsonCursor::new(text);
    cursor.expect('[')?;
    let mut rows = Vec::new();
    loop {
        cursor.skip_whitespace();
        if cursor.consume(']') {
            break;
        }
        cursor.expect('{')?;
        let mut row = BTreeMap::new();
        loop {
            cursor.skip_whitespace();
            if cursor.consume('}') {
                break;
            }
            let key = cursor.string()?;
            cursor.expect(':')?;
            let value = cursor.string()?;
            if row.insert(key.clone(), value).is_some() {
                return Err(format!("duplicate JSON key `{key}`"));
            }
            cursor.skip_whitespace();
            if cursor.consume('}') {
                break;
            }
            cursor.expect(',')?;
        }
        rows.push(row);
        cursor.skip_whitespace();
        if cursor.consume(']') {
            break;
        }
        cursor.expect(',')?;
    }
    cursor.skip_whitespace();
    if cursor.peek().is_some() {
        return Err("unexpected text after JSON array".to_owned());
    }
    Ok(rows)
}

struct JsonCursor {
    chars: Vec<char>,
    index: usize,
}

impl JsonCursor {
    fn new(text: &str) -> Self {
        Self {
            chars: text.chars().collect(),
            index: 0,
        }
    }

    fn peek(&self) -> Option<char> {
        self.chars.get(self.index).copied()
    }

    fn skip_whitespace(&mut self) {
        while self.peek().is_some_and(char::is_whitespace) {
            self.index += 1;
        }
    }

    fn consume(&mut self, expected: char) -> bool {
        self.skip_whitespace();
        if self.peek() == Some(expected) {
            self.index += 1;
            true
        } else {
            false
        }
    }

    fn expect(&mut self, expected: char) -> Result<(), String> {
        if self.consume(expected) {
            Ok(())
        } else {
            Err(format!("expected `{expected}` at character {}", self.index))
        }
    }

    fn string(&mut self) -> Result<String, String> {
        self.expect('"')?;
        let mut value = String::new();
        loop {
            let Some(character) = self.peek() else {
                return Err("unterminated JSON string".to_owned());
            };
            self.index += 1;
            match character {
                '"' => return Ok(value),
                '\\' => value.push(self.escape()?),
                control if control.is_control() => {
                    return Err("control character in JSON string".to_owned());
                }
                other => value.push(other),
            }
        }
    }

    fn escape(&mut self) -> Result<char, String> {
        let Some(escaped) = self.peek() else {
            return Err("unterminated JSON escape".to_owned());
        };
        self.index += 1;
        match escaped {
            '"' => Ok('"'),
            '\\' => Ok('\\'),
            '/' => Ok('/'),
            'b' => Ok('\u{0008}'),
            'f' => Ok('\u{000c}'),
            'n' => Ok('\n'),
            'r' => Ok('\r'),
            't' => Ok('\t'),
            'u' => {
                let mut value = 0_u32;
                for _ in 0..4 {
                    let Some(digit) = self.peek().and_then(|character| character.to_digit(16))
                    else {
                        return Err("invalid JSON unicode escape".to_owned());
                    };
                    self.index += 1;
                    value = value * 16 + digit;
                }
                char::from_u32(value).ok_or_else(|| "invalid JSON unicode scalar".to_owned())
            }
            other => Err(format!("unsupported JSON escape `\\{other}`")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_queue_fixture_is_rejected() {
        let root = super::super::workspace_root();
        let text = std::fs::read_to_string(root.join("tests/policy/invalid_queue.toml")).unwrap();
        let document = super::super::parse_document(&text).unwrap();
        let errors = parse_guard(&document).expect_err("incomplete queue fixture must fail");
        for field in ["owner", "full", "close"] {
            assert!(
                errors.iter().any(|error| error.contains(field)),
                "{field}: {errors:?}"
            );
        }
    }

    #[test]
    fn parses_the_design_registry_as_structured_json() {
        let rows = parse_flat_json_array(
            r#"[{"module":"transport","queue":"Ingress","owner":"transport"}]"#,
        )
        .unwrap();
        assert_eq!(rows[0]["queue"], "Ingress");
    }

    #[test]
    fn documented_queue_rows_cannot_drift_from_the_guard() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("lumio-queue-trace-{}-{unique}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(
            root.join("README.md"),
            "# 1. Baseline\n\n## 4.2 Queues\n\n| queue | owner | producer | ordering | capacity | full | close |\n| --- | --- | --- | --- | --- | --- | --- |\n",
        )
        .unwrap();
        std::fs::write(
            root.join("queues.json"),
            r#"[{"module":"transport","queue":"Ingress","element":"Envelope","owner":"transport","producers":"reactor","consumers":"slot","ordering":"FIFO","capacity_config":"ingress.capacity","full_action":"reject","close_semantics":"drain"}]"#,
        )
        .unwrap();
        let document = super::super::parse_document(
            r#"
baseline = "LGE-V1.4-2026-08-27"
baseline_source = "README.md#1."
registry_snapshot = "LGE-V1.2-2026-08-27"
registry_source = "README.md#4.2"
design_registry = "queues.json"
required_fields = ["module", "queue", "element", "owner", "producers", "consumers", "ordering", "capacity_config", "full_action", "close_semantics"]
contracts = ["transport::Ingress"]
registry_trace_rows = ["Ingress"]
registry_trace_contracts = ["Ingress | transport | reactor -> slot | FIFO | ingress.capacity | reject | drain"]
"#,
        )
        .unwrap();
        let guard = parse_guard(&document).unwrap();
        let errors = check_registry(&root, &guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("missing documented queue row")),
            "queue matrix drift was accepted: {errors:?}"
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn documented_queue_semantics_cannot_drift_from_the_guard() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = std::env::temp_dir().join(format!(
            "lumio-queue-semantics-{}-{unique}",
            std::process::id()
        ));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(
            root.join("README.md"),
            "# 1. Baseline\n\n## 4.2 Queues\n\n| queue | owner | producer | ordering | capacity | full | close |\n| --- | --- | --- | --- | --- | --- | --- |\n| Ingress | transport | reactor -> slot | FIFO | ingress.capacity | silently drop | drain |\n\n```text\n| Ingress | transport | reactor -> slot | FIFO | ingress.capacity | reject | drain |\n```\n",
        )
        .unwrap();
        std::fs::write(
            root.join("queues.json"),
            r#"[{"module":"transport","queue":"Ingress","element":"Envelope","owner":"transport","producers":"reactor","consumers":"slot","ordering":"FIFO","capacity_config":"ingress.capacity","full_action":"reject","close_semantics":"drain"}]"#,
        )
        .unwrap();
        let document = super::super::parse_document(
            r#"
baseline = "LGE-V1.4-2026-08-27"
baseline_source = "README.md#1."
registry_snapshot = "LGE-V1.2-2026-08-27"
registry_source = "README.md#4.2"
design_registry = "queues.json"
required_fields = ["module", "queue", "element", "owner", "producers", "consumers", "ordering", "capacity_config", "full_action", "close_semantics"]
contracts = ["transport::Ingress"]
registry_trace_rows = ["Ingress"]
registry_trace_contracts = ["Ingress | transport | reactor -> slot | FIFO | ingress.capacity | reject | drain"]
"#,
        )
        .unwrap();
        let guard = parse_guard(&document).unwrap();
        let errors = check_registry(&root, &guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("documented queue row `Ingress` content drift")),
            "queue semantics drift was accepted: {errors:?}"
        );
        std::fs::remove_dir_all(root).unwrap();
    }
}
