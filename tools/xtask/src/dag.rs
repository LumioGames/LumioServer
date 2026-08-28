use super::{
    has_markdown_heading, markdown_fence_marker, markdown_indented_code, markdown_section,
    value_array, value_string, Document, Value,
};
use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;

const BASELINE: &str = "LGE-V1.4-2026-08-27";
const DEV_ONLY_TESTKIT: &str = "lumio-host-testkit";

#[derive(Clone, Debug, Default)]
pub(super) struct DagGuard {
    pub(super) modules: BTreeMap<String, ModuleRule>,
    pub(super) compile_edges: BTreeSet<(String, String)>,
    pub(super) command_edges: usize,
    pub(super) event_edges: usize,
    command_trace_edges: BTreeSet<(String, String)>,
    event_trace_edges: BTreeSet<(String, String)>,
    composition_root: String,
    sources: Vec<String>,
}

#[derive(Clone, Debug, Default)]
pub(super) struct ModuleRule {
    pub(super) package: String,
    pub(super) role: String,
}

pub(super) fn parse_guard(document: &Document) -> Result<DagGuard, Vec<String>> {
    let mut errors = Vec::new();
    let baseline = required_string(&document.values, "baseline", &mut errors);
    if baseline.as_deref().is_some_and(|value| value != BASELINE) {
        errors.push(format!(
            "module DAG baseline must be `{BASELINE}`, got `{}`",
            baseline.unwrap()
        ));
    }
    let composition_root =
        required_string(&document.values, "composition_root", &mut errors).unwrap_or_default();
    let sources = [
        "baseline_source",
        "compile_source",
        "command_source",
        "event_source",
    ]
    .iter()
    .filter_map(|key| required_string(&document.values, key, &mut errors))
    .collect::<Vec<_>>();

    let mut modules = BTreeMap::new();
    let mut package_names = BTreeSet::new();
    let module_tables = document.tables.get("module").cloned().unwrap_or_default();
    if module_tables.is_empty() {
        errors.push("module DAG must declare at least one `[[module]]`".to_owned());
    }
    for (index, table) in module_tables.iter().enumerate() {
        let label = format!("module[{index}]");
        let name = table_string(table, "name", &label, &mut errors);
        let package = table_string_allow_empty(table, "package", &label, &mut errors);
        let role = table_string(table, "role", &label, &mut errors);
        let (Some(name), Some(package), Some(role)) = (name, package, role) else {
            continue;
        };
        if !matches!(role.as_str(), "module" | "composition-root" | "blocked") {
            errors.push(format!("{label}: unsupported role `{role}`"));
        }
        if role == "blocked" && !package.is_empty() {
            errors.push(format!(
                "{label}: blocked module `{name}` must not define a package"
            ));
        }
        if role != "blocked" && package.is_empty() {
            errors.push(format!(
                "{label}: implementable module `{name}` needs a package"
            ));
        }
        if !package.is_empty() && !package_names.insert(package.clone()) {
            errors.push(format!("duplicate module package `{package}`"));
        }
        if modules
            .insert(name.clone(), ModuleRule { package, role })
            .is_some()
        {
            errors.push(format!("duplicate module `{name}`"));
        }
    }

    let compile_values = required_array(&document.values, "compile_edges", &mut errors);
    let mut compile_edges = BTreeSet::new();
    for edge in compile_values {
        match parse_edge(&edge) {
            Ok((from, to)) => {
                if !compile_edges.insert((from, to)) {
                    errors.push(format!("duplicate compile edge `{edge}`"));
                }
            }
            Err(error) => errors.push(format!("compile edge `{edge}`: {error}")),
        }
    }
    let (command_edges, command_trace_edges) = validate_flow_edges(
        &required_array(&document.values, "command_edges", &mut errors),
        "command",
        &modules,
        &mut errors,
    );
    let (event_edges, event_trace_edges) = validate_flow_edges(
        &required_array(&document.values, "event_edges", &mut errors),
        "event/ack",
        &modules,
        &mut errors,
    );

    validate_declared_graph(&modules, &compile_edges, &composition_root, &mut errors);
    if let Some(cycle) = find_cycle(&compile_edges) {
        errors.push(format!("compile dependency cycle: {}", cycle.join(" -> ")));
    }

    if errors.is_empty() {
        Ok(DagGuard {
            modules,
            compile_edges,
            command_edges,
            event_edges,
            command_trace_edges,
            event_trace_edges,
            composition_root,
            sources,
        })
    } else {
        errors.sort();
        errors.dedup();
        Err(errors)
    }
}

fn required_string(
    values: &BTreeMap<String, Value>,
    key: &str,
    errors: &mut Vec<String>,
) -> Option<String> {
    match value_string(values, key) {
        Ok(value) => Some(value),
        Err(error) => {
            errors.push(error);
            None
        }
    }
}

fn required_array(
    values: &BTreeMap<String, Value>,
    key: &str,
    errors: &mut Vec<String>,
) -> Vec<String> {
    match value_array(values, key) {
        Ok(value) => value,
        Err(error) => {
            errors.push(error);
            Vec::new()
        }
    }
}

fn table_string(
    table: &BTreeMap<String, Value>,
    key: &str,
    label: &str,
    errors: &mut Vec<String>,
) -> Option<String> {
    match value_string(table, key) {
        Ok(value) => Some(value),
        Err(error) => {
            errors.push(format!("{label}: {error}"));
            None
        }
    }
}

fn table_string_allow_empty(
    table: &BTreeMap<String, Value>,
    key: &str,
    label: &str,
    errors: &mut Vec<String>,
) -> Option<String> {
    match table.get(key) {
        Some(Value::String(value)) => Some(value.clone()),
        Some(Value::Array(_)) => {
            errors.push(format!("{label}: `{key}` must be a string"));
            None
        }
        None => {
            errors.push(format!("{label}: missing `{key}`"));
            None
        }
    }
}

fn parse_edge(value: &str) -> Result<(String, String), String> {
    let Some((from, to)) = value.split_once("->") else {
        return Err("expected `from -> to`".to_owned());
    };
    let from = from.trim();
    let to = to.trim();
    if from.is_empty() || to.is_empty() {
        return Err("edge endpoints must not be empty".to_owned());
    }
    Ok((from.to_owned(), to.to_owned()))
}

fn validate_flow_edges(
    values: &[String],
    kind: &str,
    modules: &BTreeMap<String, ModuleRule>,
    errors: &mut Vec<String>,
) -> (usize, BTreeSet<(String, String)>) {
    let mut unique = BTreeSet::new();
    let mut trace_edges = BTreeSet::new();
    for value in values {
        let Some((edge, contract)) = value.split_once('|') else {
            errors.push(format!("{kind} edge `{value}` must include `| contract`"));
            continue;
        };
        let Ok((from, to)) = parse_edge(edge) else {
            errors.push(format!("invalid {kind} edge `{value}`"));
            continue;
        };
        if contract.trim().is_empty() {
            errors.push(format!("invalid {kind} edge `{value}`"));
        }
        for endpoint in [&from, &to] {
            if !modules.contains_key(endpoint)
                && !matches!(endpoint.as_str(), "external-control-plane" | "timer-owner")
            {
                errors.push(format!(
                    "{kind} edge `{value}` references unknown endpoint `{endpoint}`"
                ));
            }
            if modules
                .get(endpoint)
                .is_some_and(|rule| rule.role == "blocked")
            {
                errors.push(format!(
                    "{kind} edge `{value}` references blocked module `{endpoint}`"
                ));
            }
        }
        if !unique.insert(value.trim().to_owned()) {
            errors.push(format!("duplicate {kind} edge `{value}`"));
        }
        trace_edges.insert((from, to));
    }
    (unique.len(), trace_edges)
}

fn validate_declared_graph(
    modules: &BTreeMap<String, ModuleRule>,
    edges: &BTreeSet<(String, String)>,
    composition_root: &str,
    errors: &mut Vec<String>,
) {
    let roots = modules
        .iter()
        .filter(|(_, rule)| rule.role == "composition-root")
        .map(|(name, _)| name.as_str())
        .collect::<Vec<_>>();
    if roots != [composition_root] {
        errors.push(format!(
            "composition root must be exactly `{composition_root}`, got {roots:?}"
        ));
    }
    if composition_root != "process" {
        errors.push("only `process` may be the composition root".to_owned());
    }
    for (from, to) in edges {
        for endpoint in [from, to] {
            if !modules.contains_key(endpoint) {
                errors.push(format!(
                    "compile edge references unknown module `{endpoint}`"
                ));
            }
        }
        if from == to {
            errors.push(format!("self compile edge `{from} -> {to}` is forbidden"));
        }
        if from == composition_root || to == composition_root {
            errors.push(format!(
                "composition root is omitted from the declared layer DAG: `{from} -> {to}`"
            ));
        }
        if modules.get(from).is_some_and(|rule| rule.role == "blocked")
            || modules.get(to).is_some_and(|rule| rule.role == "blocked")
        {
            errors.push(format!(
                "blocked module appears in compile edge `{from} -> {to}`"
            ));
        }
    }
    if edges.iter().any(|(from, _)| from == "host-profiles") {
        errors.push("host-profiles must have zero first-class module dependencies".to_owned());
    }
}

fn find_cycle(edges: &BTreeSet<(String, String)>) -> Option<Vec<String>> {
    let mut graph: BTreeMap<&str, Vec<&str>> = BTreeMap::new();
    for (from, to) in edges {
        graph.entry(from).or_default().push(to);
        graph.entry(to).or_default();
    }
    let mut visited = BTreeSet::new();
    let mut active = BTreeSet::new();
    let mut stack = Vec::new();
    for node in graph.keys().copied() {
        if let Some(cycle) = visit(node, &graph, &mut visited, &mut active, &mut stack) {
            return Some(cycle);
        }
    }
    None
}

fn visit<'a>(
    node: &'a str,
    graph: &BTreeMap<&'a str, Vec<&'a str>>,
    visited: &mut BTreeSet<&'a str>,
    active: &mut BTreeSet<&'a str>,
    stack: &mut Vec<&'a str>,
) -> Option<Vec<String>> {
    if active.contains(node) {
        let start = stack.iter().position(|entry| *entry == node).unwrap_or(0);
        let mut cycle = stack[start..]
            .iter()
            .map(ToString::to_string)
            .collect::<Vec<_>>();
        cycle.push(node.to_owned());
        return Some(cycle);
    }
    if !visited.insert(node) {
        return None;
    }
    active.insert(node);
    stack.push(node);
    for next in graph.get(node).into_iter().flatten() {
        if let Some(cycle) = visit(next, graph, visited, active, stack) {
            return Some(cycle);
        }
    }
    stack.pop();
    active.remove(node);
    None
}

pub(super) fn check_live(root: &Path, guard: &DagGuard) -> Vec<String> {
    let mut errors = validate_sources(root, guard);
    let workspace_members = match workspace_members(&root.join("Cargo.toml")) {
        Ok(members) => members,
        Err(error) => {
            errors.push(error);
            BTreeSet::new()
        }
    };
    let workspace_packages = match workspace_dependency_packages(&root.join("Cargo.toml")) {
        Ok(packages) => packages,
        Err(error) => {
            errors.push(error);
            BTreeMap::new()
        }
    };
    let package_to_module = guard
        .modules
        .iter()
        .filter(|(_, rule)| !rule.package.is_empty())
        .map(|(module, rule)| (rule.package.as_str(), module.as_str()))
        .collect::<BTreeMap<_, _>>();
    let mut live_edges = BTreeSet::new();

    for (module, rule) in &guard.modules {
        let module_dir = root.join("modules").join(module);
        let manifest = module_dir.join("Cargo.toml");
        if rule.role == "blocked" {
            if manifest.exists() {
                errors.push(format!("blocked module `{module}` has Cargo.toml"));
            }
            if module_dir.join("src").exists() {
                errors.push(format!("blocked module `{module}` has a src tree"));
            }
            continue;
        }
        if !manifest.exists() {
            continue;
        }
        let relative_member = format!("modules/{module}");
        if !workspace_members.contains(&relative_member) {
            errors.push(format!(
                "module `{module}` has Cargo.toml but is not an explicit workspace member"
            ));
        }
        match parse_manifest(&manifest) {
            Ok(parsed) => {
                if parsed.package != rule.package {
                    errors.push(format!(
                        "module `{module}` package mismatch: expected `{}`, got `{}`",
                        rule.package, parsed.package
                    ));
                }
                for dependency in parsed.dependencies {
                    let package = workspace_packages
                        .get(&dependency)
                        .map_or(dependency.as_str(), String::as_str);
                    if package == DEV_ONLY_TESTKIT {
                        errors.push(format!(
                            "module `{module}` production dependency `{dependency}` resolves to dev-only `{DEV_ONLY_TESTKIT}`"
                        ));
                        continue;
                    }
                    if let Some(target) = package_to_module.get(package) {
                        live_edges.insert((module.clone(), (*target).to_owned()));
                    }
                }
            }
            Err(error) => errors.push(error),
        }
    }

    let modules_dir = root.join("modules");
    if let Ok(entries) = std::fs::read_dir(&modules_dir) {
        for entry in entries.flatten() {
            if !entry.file_type().is_ok_and(|kind| kind.is_dir()) {
                continue;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            if !guard.modules.contains_key(&name)
                && (entry.path().join("Cargo.toml").exists() || entry.path().join("src").exists())
            {
                errors.push(format!(
                    "unregistered first-class module directory `{name}`"
                ));
            }
        }
    }
    errors.extend(validate_live_edges(&live_edges, guard));
    if let Some(cycle) = find_cycle(&live_edges) {
        errors.push(format!(
            "live Cargo dependency cycle: {}",
            cycle.join(" -> ")
        ));
    }
    errors
}

fn validate_sources(root: &Path, guard: &DagGuard) -> Vec<String> {
    let mut errors = Vec::new();
    for source in &guard.sources {
        let (relative, anchor) = source.split_once('#').unwrap_or((source, ""));
        let path = root.join(relative);
        match std::fs::read_to_string(&path) {
            Ok(text) if anchor.is_empty() || has_markdown_heading(&text, anchor) => {}
            Ok(_) => errors.push(format!("trace source `{source}` is missing its anchor")),
            Err(error) => errors.push(format!("trace source `{source}` cannot be read: {error}")),
        }
    }
    for (index, kind, expected) in [
        (1, "compile", &guard.compile_edges),
        (2, "command", &guard.command_trace_edges),
        (3, "event/ack", &guard.event_trace_edges),
    ] {
        if let Some(source) = guard.sources.get(index) {
            errors.extend(validate_graph_trace(
                root,
                source,
                kind,
                expected,
                &guard.modules,
            ));
        }
    }
    errors
}

fn validate_graph_trace(
    root: &Path,
    source: &str,
    kind: &str,
    expected: &BTreeSet<(String, String)>,
    modules: &BTreeMap<String, ModuleRule>,
) -> Vec<String> {
    let (relative, anchor) = source.split_once('#').unwrap_or((source, ""));
    let path = root.join(relative);
    let Ok(text) = std::fs::read_to_string(&path) else {
        return Vec::new();
    };
    let Some(section) = markdown_section(&text, anchor) else {
        return Vec::new();
    };
    let (actual, mut errors) = mermaid_edges(&section, modules);
    for edge in expected.difference(&actual) {
        errors.push(format!(
            "{kind} trace is missing documented edge `{} -> {}` in `{source}`",
            edge.0, edge.1
        ));
    }
    for edge in actual.difference(expected) {
        errors.push(format!(
            "{kind} trace has unguarded documented edge `{} -> {}` in `{source}`",
            edge.0, edge.1
        ));
    }
    errors
}

fn mermaid_edges(
    section: &str,
    modules: &BTreeMap<String, ModuleRule>,
) -> (BTreeSet<(String, String)>, Vec<String>) {
    let mermaid = mermaid_fenced_source(section);
    let mut aliases = BTreeMap::from([
        (
            "controlPlaneExt".to_owned(),
            "external-control-plane".to_owned(),
        ),
        ("anyOwner".to_owned(), "timer-owner".to_owned()),
    ]);
    let mut groups: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
    let mut current_group = None;

    for line in mermaid.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with("%%") {
            continue;
        }
        if let Some(group) = trimmed.strip_prefix("subgraph ") {
            let group = group
                .split(|character: char| character.is_whitespace() || character == '[')
                .next()
                .unwrap_or_default();
            if !group.is_empty() {
                groups.entry(group.to_owned()).or_default();
                current_group = Some(group.to_owned());
            }
            continue;
        }
        if trimmed == "end" {
            current_group = None;
            continue;
        }
        for (alias, label) in mermaid_nodes(trimmed) {
            let endpoint = modules
                .keys()
                .find(|module| contains_endpoint_token(&label, module))
                .cloned();
            if let Some(endpoint) = endpoint {
                aliases.insert(alias.clone(), endpoint);
                if let Some(group) = &current_group {
                    groups.entry(group.clone()).or_default().insert(alias);
                }
            }
        }
    }

    let blocked = modules
        .iter()
        .filter(|(_, rule)| rule.role == "blocked")
        .map(|(module, _)| module.as_str())
        .collect::<BTreeSet<_>>();
    let mut edges = BTreeSet::new();
    let mut errors = Vec::new();
    for line in mermaid.lines() {
        if line.trim_start().starts_with("%%") {
            continue;
        }
        let Some((left, right)) = line.split_once("-->") else {
            continue;
        };
        let Some(from_alias) = mermaid_left_alias(left) else {
            continue;
        };
        let Some(to_alias) = mermaid_right_alias(right) else {
            continue;
        };
        let from_group = groups.contains_key(from_alias);
        let to_group = groups.contains_key(to_alias);
        let from = resolve_mermaid_endpoint(from_alias, &aliases, &groups);
        let to = resolve_mermaid_endpoint(to_alias, &aliases, &groups);
        let (Some(from), Some(to)) = (from, to) else {
            errors.push(format!(
                "documented Mermaid edge has unknown endpoint `{from_alias} -> {to_alias}`"
            ));
            continue;
        };
        for source in &from {
            for target in &to {
                if (from_group && blocked.contains(source.as_str()))
                    || (to_group && blocked.contains(target.as_str()))
                {
                    continue;
                }
                edges.insert((source.clone(), target.clone()));
            }
        }
    }
    (edges, errors)
}

fn mermaid_fenced_source(section: &str) -> String {
    let mut source = Vec::new();
    let mut fence = None;
    for raw_line in section.lines() {
        if let Some((marker, width, capture)) = fence {
            if markdown_fence_marker(raw_line).is_some_and(
                |(candidate, candidate_width, suffix)| {
                    candidate == marker && candidate_width >= width && suffix.trim().is_empty()
                },
            ) {
                fence = None;
            } else if capture {
                source.push(raw_line);
            }
            continue;
        }
        if markdown_indented_code(raw_line) {
            continue;
        }
        if let Some((marker, width, suffix)) = markdown_fence_marker(raw_line) {
            fence = Some((marker, width, suffix.trim().eq_ignore_ascii_case("mermaid")));
        }
    }
    source.join("\n")
}

fn contains_endpoint_token(text: &str, token: &str) -> bool {
    text.match_indices(token).any(|(index, _)| {
        let before = text[..index].chars().next_back();
        let after = text[index + token.len()..].chars().next();
        !before
            .is_some_and(|character| character.is_alphanumeric() || matches!(character, '_' | '-'))
            && !after.is_some_and(|character| {
                character.is_alphanumeric() || matches!(character, '_' | '-')
            })
    })
}

fn mermaid_nodes(line: &str) -> Vec<(String, String)> {
    let bytes = line.as_bytes();
    let mut nodes = Vec::new();
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] != b'[' {
            index += 1;
            continue;
        }
        let mut alias_end = index;
        while alias_end > 0 && bytes[alias_end - 1].is_ascii_whitespace() {
            alias_end -= 1;
        }
        let mut alias_start = alias_end;
        while alias_start > 0
            && (bytes[alias_start - 1].is_ascii_alphanumeric()
                || matches!(bytes[alias_start - 1], b'_' | b'-'))
        {
            alias_start -= 1;
        }
        let Some(label_end) = line[index + 1..].find(']').map(|offset| index + offset + 1) else {
            break;
        };
        if alias_start < alias_end {
            nodes.push((
                line[alias_start..alias_end].to_owned(),
                line[index + 1..label_end].to_owned(),
            ));
        }
        index = label_end + 1;
    }
    nodes
}

fn mermaid_left_alias(side: &str) -> Option<&str> {
    let side = side.trim();
    let end = side.find('[').unwrap_or(side.len());
    side[..end].split_whitespace().last()
}

fn mermaid_right_alias(side: &str) -> Option<&str> {
    let mut side = side.trim_start();
    if let Some(label) = side.strip_prefix('|') {
        side = label.split_once('|')?.1.trim_start();
    }
    let end = side
        .find(|character: char| character == '[' || character.is_whitespace())
        .unwrap_or(side.len());
    (!side[..end].is_empty()).then_some(&side[..end])
}

fn resolve_mermaid_endpoint(
    alias: &str,
    aliases: &BTreeMap<String, String>,
    groups: &BTreeMap<String, BTreeSet<String>>,
) -> Option<BTreeSet<String>> {
    if let Some(endpoint) = aliases.get(alias) {
        return Some(BTreeSet::from([endpoint.clone()]));
    }
    groups.get(alias).and_then(|members| {
        members
            .iter()
            .map(|member| aliases.get(member).cloned())
            .collect()
    })
}

#[derive(Debug)]
struct ParsedManifest {
    package: String,
    dependencies: BTreeSet<String>,
}

fn parse_manifest(path: &Path) -> Result<ParsedManifest, String> {
    let text =
        std::fs::read_to_string(path).map_err(|error| format!("{}: {error}", path.display()))?;
    let mut section = Vec::new();
    let mut package = None;
    let mut dependencies = BTreeMap::new();
    for statement in toml_statements(&text) {
        let line = statement.as_str();
        if line.starts_with('[') && line.ends_with(']') {
            section = toml_key_path(&line[1..line.len() - 1]);
            continue;
        }
        let Some((raw_key, value)) = split_toml_assignment(line) else {
            continue;
        };
        let mut key_path = section.clone();
        key_path.extend(toml_key_path(raw_key));
        if key_path == ["package", "name"] {
            package = quoted_value(value);
        } else if is_production_dependency_container(&key_path) {
            record_inline_dependency_table(&mut dependencies, value);
        } else if let Some((alias, property)) = production_dependency_path(&key_path) {
            record_dependency(&mut dependencies, alias, property, value);
        } else if key_path.first().is_some_and(|segment| segment == "target") {
            record_inline_target_dependencies(&mut dependencies, &key_path, value);
        }
    }
    package
        .filter(|name| !name.is_empty())
        .map(|package| ParsedManifest {
            package,
            dependencies: dependencies.into_values().collect(),
        })
        .ok_or_else(|| format!("{}: missing [package] name", path.display()))
}

fn toml_key(raw: &str) -> String {
    let raw = raw.trim();
    if raw.starts_with(['"', '\'']) {
        quoted_value(raw).unwrap_or_else(|| raw.to_owned())
    } else {
        raw.to_owned()
    }
}

fn production_dependency_path(path: &[String]) -> Option<(String, Option<&str>)> {
    let dependency_index = if path
        .first()
        .is_some_and(|segment| is_production_dependency_kind(segment))
    {
        0
    } else if path.first().is_some_and(|segment| segment == "target") {
        path.iter()
            .enumerate()
            .skip(2)
            .find_map(|(index, segment)| is_production_dependency_kind(segment).then_some(index))?
    } else {
        return None;
    };
    let alias = path.get(dependency_index + 1)?.clone();
    let property = path
        .get(dependency_index + 2)
        .map(String::as_str)
        .filter(|property| is_dependency_property(property));
    Some((alias, property))
}

fn is_production_dependency_container(path: &[String]) -> bool {
    path.len() == 1 && is_production_dependency_kind(&path[0])
        || (path.len() == 3 && path[0] == "target" && is_production_dependency_kind(&path[2]))
}

fn workspace_dependency_path(path: &[String]) -> Option<(String, Option<&str>)> {
    if path.first().is_none_or(|segment| segment != "workspace")
        || path.get(1).is_none_or(|segment| segment != "dependencies")
    {
        return None;
    }
    let alias = path.get(2)?.clone();
    let property = path
        .get(3)
        .map(String::as_str)
        .filter(|property| is_dependency_property(property));
    Some((alias, property))
}

fn is_workspace_dependency_container(path: &[String]) -> bool {
    path == ["workspace", "dependencies"]
}

fn is_production_dependency_kind(segment: &str) -> bool {
    matches!(segment, "dependencies" | "build-dependencies")
}

fn is_dependency_property(property: &str) -> bool {
    matches!(
        property,
        "branch"
            | "default-features"
            | "features"
            | "git"
            | "optional"
            | "package"
            | "path"
            | "registry"
            | "rev"
            | "tag"
            | "version"
            | "workspace"
    )
}

fn record_dependency(
    dependencies: &mut BTreeMap<String, String>,
    alias: String,
    property: Option<&str>,
    value: &str,
) {
    if property == Some("package") {
        if let Some(package) = quoted_value(value) {
            dependencies.insert(alias, package);
        }
    } else if property.is_none() {
        let package = inline_package(value).unwrap_or_else(|| alias.clone());
        dependencies.insert(alias, package);
    } else {
        dependencies.entry(alias.clone()).or_insert(alias);
    }
}

fn record_inline_dependency_table(dependencies: &mut BTreeMap<String, String>, value: &str) {
    let Some(inner) = value
        .trim()
        .strip_prefix('{')
        .and_then(|value| value.strip_suffix('}'))
    else {
        return;
    };
    for entry in split_inline_entries(inner) {
        let Some((raw_key, nested_value)) = split_toml_assignment(entry) else {
            continue;
        };
        let path = toml_key_path(raw_key);
        let Some(alias) = path.first().cloned() else {
            continue;
        };
        let property = path
            .get(1)
            .map(String::as_str)
            .filter(|property| is_dependency_property(property));
        record_dependency(dependencies, alias, property, nested_value);
    }
}

fn record_inline_target_dependencies(
    dependencies: &mut BTreeMap<String, String>,
    path: &[String],
    value: &str,
) {
    let Some(inner) = value
        .trim()
        .strip_prefix('{')
        .and_then(|value| value.strip_suffix('}'))
    else {
        return;
    };
    for entry in split_inline_entries(inner) {
        let Some((raw_key, nested_value)) = split_toml_assignment(entry) else {
            continue;
        };
        let mut nested_path = path.to_vec();
        nested_path.extend(toml_key_path(raw_key));
        if is_production_dependency_container(&nested_path) {
            record_inline_dependency_table(dependencies, nested_value);
        } else if let Some((alias, property)) = production_dependency_path(&nested_path) {
            record_dependency(dependencies, alias, property, nested_value);
        } else if nested_path.len() == 2 {
            record_inline_target_dependencies(dependencies, &nested_path, nested_value);
        }
    }
}

fn toml_key_path(raw: &str) -> Vec<String> {
    split_unquoted_dots(raw).into_iter().map(toml_key).collect()
}

fn split_unquoted_dots(raw: &str) -> Vec<&str> {
    let mut quote = None;
    let mut escaped = false;
    let mut start = 0;
    let mut segments = Vec::new();
    for (index, character) in raw.char_indices() {
        if let Some(active_quote) = quote {
            if active_quote == '"' && escaped {
                escaped = false;
            } else if active_quote == '"' && character == '\\' {
                escaped = true;
            } else if character == active_quote {
                quote = None;
            }
        } else if matches!(character, '"' | '\'') {
            quote = Some(character);
        } else if character == '.' {
            segments.push(raw[start..index].trim());
            start = index + 1;
        }
    }
    segments.push(raw[start..].trim());
    segments
}

fn split_toml_assignment(raw: &str) -> Option<(&str, &str)> {
    let mut quote = None;
    let mut escaped = false;
    for (index, character) in raw.char_indices() {
        if let Some(active_quote) = quote {
            if active_quote == '"' && escaped {
                escaped = false;
            } else if active_quote == '"' && character == '\\' {
                escaped = true;
            } else if character == active_quote {
                quote = None;
            }
        } else if matches!(character, '"' | '\'') {
            quote = Some(character);
        } else if character == '=' {
            return Some((&raw[..index], &raw[index + 1..]));
        }
    }
    None
}

fn toml_statements(text: &str) -> Vec<String> {
    let mut statements = Vec::new();
    let mut current = String::new();
    let mut state = TomlStatementState::default();
    for raw_line in text.lines() {
        let line = toml_line_without_comment(raw_line, &mut state);
        if current.is_empty() {
            let line = line.trim();
            if line.is_empty() {
                continue;
            }
            current.push_str(line);
        } else {
            current.push('\n');
            current.push_str(&line);
        }
        if state.is_complete() {
            statements.push(std::mem::take(&mut current));
            state = TomlStatementState::default();
        }
    }
    if !current.is_empty() {
        statements.push(current);
    }
    statements
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum TomlStringKind {
    Basic,
    Literal,
    MultilineBasic,
    MultilineLiteral,
}

#[derive(Debug, Default)]
struct TomlStatementState {
    string: Option<TomlStringKind>,
    escaped: bool,
    depth: i32,
}

impl TomlStatementState {
    fn is_complete(&self) -> bool {
        self.string.is_none() && self.depth <= 0
    }
}

fn toml_line_without_comment(raw_line: &str, state: &mut TomlStatementState) -> String {
    let mut visible = String::new();
    let mut index = 0;
    while index < raw_line.len() {
        let remaining = &raw_line[index..];
        match state.string {
            Some(TomlStringKind::MultilineBasic) => {
                if !state.escaped && remaining.starts_with("\"\"\"") {
                    let width = remaining.bytes().take_while(|byte| *byte == b'"').count();
                    visible.push_str(&remaining[..width]);
                    index += width;
                    state.string = None;
                    continue;
                }
            }
            Some(TomlStringKind::MultilineLiteral) => {
                if remaining.starts_with("'''") {
                    let width = remaining.bytes().take_while(|byte| *byte == b'\'').count();
                    visible.push_str(&remaining[..width]);
                    index += width;
                    state.string = None;
                    continue;
                }
            }
            Some(TomlStringKind::Basic | TomlStringKind::Literal) | None => {}
        }

        if state.string.is_none() {
            if remaining.starts_with("\"\"\"") {
                visible.push_str("\"\"\"");
                index += 3;
                state.string = Some(TomlStringKind::MultilineBasic);
                state.escaped = false;
                continue;
            }
            if remaining.starts_with("'''") {
                visible.push_str("'''");
                index += 3;
                state.string = Some(TomlStringKind::MultilineLiteral);
                continue;
            }
        }

        let character = remaining.chars().next().expect("index is within the line");
        match state.string {
            Some(TomlStringKind::Basic) => {
                if state.escaped {
                    state.escaped = false;
                } else if character == '\\' {
                    state.escaped = true;
                } else if character == '"' {
                    state.string = None;
                }
            }
            Some(TomlStringKind::Literal) => {
                if character == '\'' {
                    state.string = None;
                }
            }
            Some(TomlStringKind::MultilineBasic) => {
                if state.escaped {
                    state.escaped = false;
                } else if character == '\\' {
                    state.escaped = true;
                }
            }
            Some(TomlStringKind::MultilineLiteral) => {}
            None => match character {
                '#' => break,
                '"' => state.string = Some(TomlStringKind::Basic),
                '\'' => state.string = Some(TomlStringKind::Literal),
                '[' | '{' => state.depth += 1,
                ']' | '}' => state.depth -= 1,
                _ => {}
            },
        }
        visible.push(character);
        index += character.len_utf8();
    }
    if state.string == Some(TomlStringKind::MultilineBasic) {
        state.escaped = false;
    }
    visible
}

fn quoted_value(raw: &str) -> Option<String> {
    let raw = raw.trim();
    if let Some(inner) = raw
        .strip_prefix("\"\"\"")
        .and_then(|value| value.strip_suffix("\"\"\""))
    {
        return decode_basic_toml_string(trim_multiline_initial_newline(inner));
    }
    if let Some(inner) = raw
        .strip_prefix("'''")
        .and_then(|value| value.strip_suffix("'''"))
    {
        return Some(trim_multiline_initial_newline(inner).to_owned());
    }
    let chars = raw.chars().collect::<Vec<_>>();
    let quote = *chars.first()?;
    if !matches!(quote, '"' | '\'') {
        return None;
    }
    let mut value = String::new();
    let mut index = 1;
    while let Some(character) = chars.get(index).copied() {
        index += 1;
        if character == quote {
            return Some(value);
        }
        if quote == '\'' || character != '\\' {
            value.push(character);
            continue;
        }
        let escaped = chars.get(index).copied()?;
        index += 1;
        match escaped {
            'b' => value.push('\u{0008}'),
            't' => value.push('\t'),
            'n' => value.push('\n'),
            'f' => value.push('\u{000c}'),
            'r' => value.push('\r'),
            '"' => value.push('"'),
            '\\' => value.push('\\'),
            'x' | 'u' | 'U' => {
                let digits = match escaped {
                    'x' => 2,
                    'u' => 4,
                    _ => 8,
                };
                let end = index.checked_add(digits)?;
                let scalar = chars
                    .get(index..end)?
                    .iter()
                    .try_fold(0_u32, |value, digit| {
                        digit.to_digit(16).map(|digit| value * 16 + digit)
                    })?;
                value.push(char::from_u32(scalar)?);
                index = end;
            }
            _ => return None,
        }
    }
    None
}

fn trim_multiline_initial_newline(raw: &str) -> &str {
    raw.strip_prefix('\n').unwrap_or(raw)
}

fn decode_basic_toml_string(raw: &str) -> Option<String> {
    let chars = raw.chars().collect::<Vec<_>>();
    let mut value = String::new();
    let mut index = 0;
    while let Some(character) = chars.get(index).copied() {
        index += 1;
        if character != '\\' {
            value.push(character);
            continue;
        }
        let mut continuation = index;
        while chars
            .get(continuation)
            .is_some_and(|character| matches!(character, ' ' | '\t' | '\r'))
        {
            continuation += 1;
        }
        if chars.get(continuation) == Some(&'\n') {
            index = continuation + 1;
            while chars
                .get(index)
                .is_some_and(|character| character.is_whitespace())
            {
                index += 1;
            }
            continue;
        }
        let escaped = chars.get(index).copied()?;
        index += 1;
        match escaped {
            'b' => value.push('\u{0008}'),
            't' => value.push('\t'),
            'n' => value.push('\n'),
            'f' => value.push('\u{000c}'),
            'r' => value.push('\r'),
            '"' => value.push('"'),
            '\\' => value.push('\\'),
            'x' | 'u' | 'U' => {
                let digits = match escaped {
                    'x' => 2,
                    'u' => 4,
                    _ => 8,
                };
                let end = index.checked_add(digits)?;
                let scalar = chars
                    .get(index..end)?
                    .iter()
                    .try_fold(0_u32, |value, digit| {
                        digit.to_digit(16).map(|digit| value * 16 + digit)
                    })?;
                value.push(char::from_u32(scalar)?);
                index = end;
            }
            _ => return None,
        }
    }
    Some(value)
}

fn inline_package(raw: &str) -> Option<String> {
    let inner = raw.trim().strip_prefix('{')?.strip_suffix('}')?;
    for entry in split_inline_entries(inner) {
        let (key, value) = entry.split_once('=')?;
        if toml_key(key) == "package" {
            return quoted_value(value);
        }
    }
    None
}

fn split_inline_entries(raw: &str) -> Vec<&str> {
    let mut entries = Vec::new();
    let mut start = 0;
    let mut quote = None;
    let mut escaped = false;
    let mut depth = 0_u32;
    for (index, character) in raw.char_indices() {
        if let Some(active_quote) = quote {
            if active_quote == '"' && escaped {
                escaped = false;
            } else if active_quote == '"' && character == '\\' {
                escaped = true;
            } else if character == active_quote {
                quote = None;
            }
            continue;
        }
        match character {
            '"' | '\'' => quote = Some(character),
            '[' | '{' => depth += 1,
            ']' | '}' => depth = depth.saturating_sub(1),
            ',' if depth == 0 => {
                entries.push(&raw[start..index]);
                start = index + 1;
            }
            _ => {}
        }
    }
    entries.push(&raw[start..]);
    entries
}

fn workspace_members(path: &Path) -> Result<BTreeSet<String>, String> {
    let text =
        std::fs::read_to_string(path).map_err(|error| format!("{}: {error}", path.display()))?;
    let mut section = Vec::new();
    let mut array = String::new();
    for statement in toml_statements(&text) {
        let line = statement.as_str();
        if line.starts_with('[') && line.ends_with(']') {
            section = toml_key_path(&line[1..line.len() - 1]);
            continue;
        }
        if let Some((key, value)) = split_toml_assignment(line) {
            let mut key_path = section.clone();
            key_path.extend(toml_key_path(key));
            if key_path == ["workspace", "members"] {
                value.trim().clone_into(&mut array);
                break;
            }
        }
    }
    let Value::Array(values) = super::parse_value(&array)
        .map_err(|error| format!("{}: invalid workspace members: {error}", path.display()))?
    else {
        return Err(format!(
            "{}: workspace members must be an array",
            path.display()
        ));
    };
    Ok(values
        .into_iter()
        .map(|value| value.replace('\\', "/").trim_end_matches('/').to_owned())
        .collect())
}

fn workspace_dependency_packages(path: &Path) -> Result<BTreeMap<String, String>, String> {
    let text =
        std::fs::read_to_string(path).map_err(|error| format!("{}: {error}", path.display()))?;
    let mut section = Vec::new();
    let mut packages = BTreeMap::new();
    for statement in toml_statements(&text) {
        let line = statement.as_str();
        if line.starts_with('[') && line.ends_with(']') {
            section = toml_key_path(&line[1..line.len() - 1]);
            continue;
        }
        let Some((raw_key, value)) = split_toml_assignment(line) else {
            continue;
        };
        let mut key_path = section.clone();
        key_path.extend(toml_key_path(raw_key));
        if is_workspace_dependency_container(&key_path) {
            record_inline_dependency_table(&mut packages, value);
        } else if let Some((alias, property)) = workspace_dependency_path(&key_path) {
            record_dependency(&mut packages, alias, property, value);
        }
    }
    Ok(packages)
}

fn validate_live_edges(graph: &BTreeSet<(String, String)>, guard: &DagGuard) -> Vec<String> {
    let mut errors = Vec::new();
    for (from, to) in graph {
        let target_blocked = guard
            .modules
            .get(to)
            .is_some_and(|rule| rule.role == "blocked");
        if target_blocked {
            errors.push(format!("blocked dependency edge `{from} -> {to}`"));
        } else if from != &guard.composition_root
            && !guard.compile_edges.contains(&(from.clone(), to.clone()))
        {
            errors.push(format!(
                "live edge outside architecture allowlist: `{from} -> {to}`"
            ));
        }
        if from == "host-profiles" {
            errors.push(format!(
                "host-profiles must not depend on first-class module `{to}`"
            ));
        }
    }
    errors
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_cycle_fixture_is_rejected() {
        let root = super::super::workspace_root();
        let text = std::fs::read_to_string(root.join("tests/policy/invalid_cycle.toml")).unwrap();
        let document = super::super::parse_document(&text).unwrap();
        let errors = parse_guard(&document).expect_err("cycle fixture must fail");
        assert!(
            errors.iter().any(|error| error.contains("cycle")),
            "{errors:?}"
        );
    }

    #[test]
    fn rejects_an_edge_outside_the_allowlist() {
        let guard = DagGuard {
            modules: BTreeMap::from([
                (
                    "host-runtime".to_owned(),
                    ModuleRule {
                        package: "lumio-host-runtime".to_owned(),
                        role: "module".to_owned(),
                    },
                ),
                (
                    "world-slot".to_owned(),
                    ModuleRule {
                        package: "lumio-world-slot".to_owned(),
                        role: "module".to_owned(),
                    },
                ),
            ]),
            compile_edges: BTreeSet::new(),
            command_edges: 0,
            event_edges: 0,
            command_trace_edges: BTreeSet::new(),
            event_trace_edges: BTreeSet::new(),
            composition_root: "process".to_owned(),
            sources: Vec::new(),
        };
        let graph = BTreeSet::from([("host-runtime".to_owned(), "world-slot".to_owned())]);
        let errors = validate_live_edges(&graph, &guard);
        assert!(
            errors.iter().any(|error| error.contains("outside")),
            "{errors:?}"
        );
    }

    #[test]
    fn parses_inline_and_table_dependency_forms() {
        let root = std::env::temp_dir().join(format!("lumio-dag-manifest-{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let manifest = root.join("Cargo.toml");
        std::fs::write(
            &manifest,
            r#"
[package]
name = "lumio-session"

[dependencies]
runtime = { package = "lumio-host-runtime", path = "../host-runtime" }

[build-dependencies.codegen]
package = "lumio-observability"

[target.'cfg(unix)'.dependencies.slot]
package = "lumio-world-slot"
"#,
        )
        .unwrap();
        let parsed = parse_manifest(&manifest).unwrap();
        assert_eq!(
            parsed.dependencies,
            BTreeSet::from([
                "lumio-host-runtime".to_owned(),
                "lumio-observability".to_owned(),
                "lumio-world-slot".to_owned(),
            ])
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn resolves_workspace_dependency_aliases_to_packages() {
        let root =
            std::env::temp_dir().join(format!("lumio-workspace-deps-{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let manifest = root.join("Cargo.toml");
        std::fs::write(
            &manifest,
            r#"
[workspace]
members = []

[workspace.dependencies]
runtime = { package = "lumio-host-runtime", path = "modules/host-runtime" }

[workspace.dependencies.slot]
package = "lumio-world-slot"
path = "modules/world-slot"
"#,
        )
        .unwrap();
        assert_eq!(
            workspace_dependency_packages(&manifest).unwrap(),
            BTreeMap::from([
                ("runtime".to_owned(), "lumio-host-runtime".to_owned()),
                ("slot".to_owned(), "lumio-world-slot".to_owned()),
            ])
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn documented_graph_edges_cannot_drift_from_the_guard() {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root =
            std::env::temp_dir().join(format!("lumio-graph-trace-{}-{unique}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("Cargo.toml"), "[workspace]\nmembers = []\n").unwrap();
        std::fs::write(
            root.join("README.md"),
            r"
# 1. Baseline

## 3.2.1 Compile
```mermaid
graph TD
    aNode[a]
    bNode[b]
    %% aNode --> bNode
```
```text
aNode --> bNode
```

## 3.2.2 Command
```mermaid
graph LR
    aNode[a]
    bNode[b]
    %% aNode --> bNode
```
```text
aNode --> bNode
```

## 3.2.3 Event
```mermaid
graph LR
    aNode[a]
    bNode[b]
    %% bNode --> aNode
```
```text
bNode --> aNode
```
",
        )
        .unwrap();
        let document = super::super::parse_document(
            r#"
baseline = "LGE-V1.4-2026-08-27"
baseline_source = "README.md#1."
composition_root = "process"
compile_source = "README.md#3.2.1"
command_source = "README.md#3.2.2"
event_source = "README.md#3.2.3"
compile_edges = ["a -> b"]
command_edges = ["a -> b | command"]
event_edges = ["b -> a | event"]

[[module]]
name = "process"
package = "process"
role = "composition-root"

[[module]]
name = "a"
package = "a"
role = "module"

[[module]]
name = "b"
package = "b"
role = "module"
"#,
        )
        .unwrap();
        let guard = parse_guard(&document).unwrap();
        let errors = check_live(&root, &guard);
        for kind in ["compile", "command", "event/ack"] {
            assert!(
                errors
                    .iter()
                    .any(|error| error.contains(kind) && error.contains("missing documented edge")),
                "{kind} graph drift was accepted: {errors:?}"
            );
        }
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn hidden_mermaid_graphs_are_not_trace_evidence() {
        let modules = BTreeMap::from([
            (
                "a".to_owned(),
                ModuleRule {
                    package: "a".to_owned(),
                    role: "module".to_owned(),
                },
            ),
            (
                "b".to_owned(),
                ModuleRule {
                    package: "b".to_owned(),
                    role: "module".to_owned(),
                },
            ),
        ]);

        for hidden_heading in [
            "<!--\n## 3.2.1 Compile\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n-->\n",
            "```markdown\n## 3.2.1 Compile\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n```\n",
            "    ## 3.2.1 Compile\n    ```mermaid\n    graph TD\n    aNode[a] --> bNode[b]\n    ```\n",
            "<pre>\n## 3.2.1 Compile\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n</pre>\n",
            "<span>\n## 3.2.1 Compile\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n</span>\n",
        ] {
            assert!(
                markdown_section(hidden_heading, "3.2.1").is_none(),
                "hidden graph heading was accepted"
            );
        }

        for hidden_graph in [
            "## 3.2.1 Compile\n<!--\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n-->\n",
            "## 3.2.1 Compile\n```markdown\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n```\n",
            "## 3.2.1 Compile\n    ```mermaid\n    graph TD\n    aNode[a] --> bNode[b]\n    ```\n",
            "## 3.2.1 Compile\n<pre>\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n</pre>\n",
            "## 3.2.1 Compile\n<span>\n```mermaid\ngraph TD\naNode[a] --> bNode[b]\n```\n</span>\n",
        ] {
            let section = markdown_section(hidden_graph, "3.2.1").expect("visible graph heading");
            let (edges, _) = mermaid_edges(&section, &modules);
            assert!(edges.is_empty(), "hidden Mermaid edge was accepted: {edges:?}");
        }
    }

    fn assert_production_dependency_rejected(
        root: &Path,
        module_dir: &Path,
        guard: &DagGuard,
        section: &str,
        dependency: &str,
    ) {
        std::fs::write(
            module_dir.join("Cargo.toml"),
            format!(
                r#"
[package]
name = "lumio-server"

{section}
{dependency}
"#
            ),
        )
        .unwrap();
        let errors = check_live(root, guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("dev-only") && error.contains("lumio-host-testkit")),
            "production dependency `{section}` / `{dependency}` was accepted: {errors:?}"
        );
    }

    fn assert_root_dotted_dependency_rejected(root: &Path, module_dir: &Path, guard: &DagGuard) {
        std::fs::write(
            module_dir.join("Cargo.toml"),
            r#"
dependencies.testkit.package = "lumio-host-testkit"
dependencies.testkit.path = "../../crates/lumio-host-testkit"

[package]
name = "lumio-server"
"#,
        )
        .unwrap();
        let errors = check_live(root, guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("dev-only") && error.contains("lumio-host-testkit")),
            "root dotted production dependency was accepted: {errors:?}"
        );
    }

    fn assert_inline_dependency_table_rejected(root: &Path, module_dir: &Path, guard: &DagGuard) {
        std::fs::write(
            module_dir.join("Cargo.toml"),
            r#"
dependencies = {
    testkit = { package = "lumio-host-testkit", path = "../../crates/lumio-host-testkit" }
}

[package]
name = "lumio-server"
"#,
        )
        .unwrap();
        let errors = check_live(root, guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("dev-only") && error.contains("lumio-host-testkit")),
            "inline production dependency table was accepted: {errors:?}"
        );
    }

    fn assert_multiline_benign_dependency_parsed(module_dir: &Path) {
        std::fs::write(
            module_dir.join("Cargo.toml"),
            r#"
[package]
name = "lumio-server"

[dependencies.lumio-host-testkit]
package = """benign-\
    package"""
"#,
        )
        .unwrap();
        let parsed = parse_manifest(&module_dir.join("Cargo.toml")).unwrap();
        assert_eq!(
            parsed.dependencies,
            BTreeSet::from(["benign-package".to_owned()]),
            "multiline continuation changed a renamed benign dependency"
        );
    }

    #[test]
    fn production_dependencies_cannot_use_dev_only_testkit() {
        let root =
            std::env::temp_dir().join(format!("lumio-testkit-policy-{}", std::process::id()));
        let module_dir = root.join("modules/process");
        std::fs::create_dir_all(&module_dir).unwrap();
        std::fs::write(
            root.join("Cargo.toml"),
            r#"
["workspace"]
members = ["modules/process"]

["workspace"."dependencies"]
testkit = { package = "lumio-host-testkit", path = "crates/lumio-host-testkit" }
literal-testkit = { package = 'lumio-host-testkit', path = 'crates/lumio-host-testkit' }
dotted-testkit.package = 'lumio-host-testkit'
dotted-testkit.path = 'crates/lumio-host-testkit'
escaped-testkit.package = "lumio\u002dhost\u002dtestkit"
escaped-testkit.path = "crates/lumio-host-testkit"
"#,
        )
        .unwrap();
        let guard = DagGuard {
            modules: BTreeMap::from([(
                "process".to_owned(),
                ModuleRule {
                    package: "lumio-server".to_owned(),
                    role: "composition-root".to_owned(),
                },
            )]),
            composition_root: "process".to_owned(),
            ..DagGuard::default()
        };

        let production_sections = [
            "[dependencies]",
            "[build-dependencies]",
            "[target.'cfg(unix)'.dependencies]",
            "[target.'cfg(unix)'.build-dependencies]",
            r#"["dependencies"]"#,
            r#"["build-dependencies"]"#,
            r#"[target.'cfg(unix)'."dependencies"]"#,
            r#"[target.'cfg(unix)'."build-dependencies"]"#,
        ];
        let dependency_forms = [
            "lumio-host-testkit = { path = '../../crates/lumio-host-testkit' }",
            "alias = { package = 'lumio-host-testkit', path = '../../crates/lumio-host-testkit' }",
            "lumio-host-testkit.path = '../../crates/lumio-host-testkit'",
            "'lumio-host-testkit' = { path = '../../crates/lumio-host-testkit' }",
            r#"escaped = { package = "lumio\u002dhost\u002dtestkit", path = "../../crates/lumio-host-testkit" }"#,
            r#""lumio\u002dhost\u002dtestkit" = { path = "../../crates/lumio-host-testkit" }"#,
            r#"alias.package = "lumio\x2dhost\x2dtestkit""#,
            r#"alias.package = """lumio-host-testkit""""#,
            r#"alias.package = """lumio\x2dhost\x2dtestkit""""#,
            "alias.package = '''lumio-host-testkit'''",
            r#"alias.package = """
lumio-host-testkit""""#,
            "alias.package = '''\nlumio-host-testkit'''",
            r#"alias.package = """lumio-host-\
    testkit""""#,
            "alias.package = \"\"\"lumio-host-\\   \n    testkit\"\"\"",
        ];
        for section in production_sections {
            for dependency in dependency_forms {
                assert_production_dependency_rejected(
                    &root,
                    &module_dir,
                    &guard,
                    section,
                    dependency,
                );
            }
        }

        assert_root_dotted_dependency_rejected(&root, &module_dir, &guard);

        for dependency in [
            "testkit.workspace = true",
            "literal-testkit.workspace = true",
            "dotted-testkit.workspace = true",
            "escaped-testkit.workspace = true",
        ] {
            assert_production_dependency_rejected(
                &root,
                &module_dir,
                &guard,
                "[dependencies]",
                dependency,
            );
        }

        assert_multiline_benign_dependency_parsed(&module_dir);

        std::fs::write(
            module_dir.join("Cargo.toml"),
            r#"
[package]
name = "lumio-server"

[dev-dependencies]
lumio-host-testkit = { path = "../../crates/lumio-host-testkit" }

[target.'cfg(unix)'.dev-dependencies]
target-testkit = { package = "lumio-host-testkit", path = "../../crates/lumio-host-testkit" }
"#,
        )
        .unwrap();
        assert_eq!(check_live(&root, &guard), Vec::<String>::new());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn whole_inline_dependency_tables_cannot_use_dev_only_testkit() {
        let root = std::env::temp_dir().join(format!(
            "lumio-inline-testkit-policy-{}",
            std::process::id()
        ));
        let module_dir = root.join("modules/process");
        std::fs::create_dir_all(&module_dir).unwrap();
        std::fs::write(
            root.join("Cargo.toml"),
            r#"
[workspace]
members = ["modules/process"]
dependencies = {
    testkit = { package = "lumio-host-testkit", path = "crates/lumio-host-testkit" }
}
"#,
        )
        .unwrap();
        let guard = DagGuard {
            modules: BTreeMap::from([(
                "process".to_owned(),
                ModuleRule {
                    package: "lumio-server".to_owned(),
                    role: "composition-root".to_owned(),
                },
            )]),
            composition_root: "process".to_owned(),
            ..DagGuard::default()
        };

        assert_inline_dependency_table_rejected(&root, &module_dir, &guard);
        assert_production_dependency_rejected(
            &root,
            &module_dir,
            &guard,
            "[dependencies]",
            "testkit.workspace = true",
        );

        std::fs::write(
            module_dir.join("Cargo.toml"),
            r#"
target = {
    'cfg(windows)' = {
        dependencies = {
            testkit = { package = "lumio-host-testkit", path = "../../crates/lumio-host-testkit" }
        }
    }
}

[package]
name = "lumio-server"
"#,
        )
        .unwrap();
        let errors = check_live(&root, &guard);
        assert!(
            errors
                .iter()
                .any(|error| error.contains("dev-only") && error.contains("lumio-host-testkit")),
            "whole inline target dependency tree was accepted: {errors:?}"
        );
        std::fs::remove_dir_all(root).unwrap();
    }
}
