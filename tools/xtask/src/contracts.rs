use std::collections::BTreeMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};

const BASELINE_ID: &str = "LGE-V1.4-2026-08-27";
const LOCK_FILES: [&str; 3] = [
    "contracts/architecture-contracts.lock.toml",
    "contracts/managed-host-contracts.lock.toml",
    "contracts/core-engine-contracts.lock.toml",
];
const ARCHITECTURE_LOCKED_PATHS: [&str; 6] = [
    "packages",
    "schemas",
    "ids/index.json",
    "fixtures",
    "tools/lumio_contract.py",
    "tools/lumio_generate.py",
];

struct ArtifactSpec {
    slug: &'static str,
    package: &'static str,
}

const ARCHITECTURE_ARTIFACTS: [ArtifactSpec; 6] = [
    ArtifactSpec {
        slug: "protocol_permission_validator",
        package: "lumio-gen-protocol-permission-validator",
    },
    ArtifactSpec {
        slug: "mapping_table",
        package: "lumio-gen-mapping-table",
    },
    ArtifactSpec {
        slug: "canonical_serializer",
        package: "lumio-gen-canonical-serializer",
    },
    ArtifactSpec {
        slug: "language_binding",
        package: "lumio-gen-language-binding",
    },
    ArtifactSpec {
        slug: "contract_types",
        package: "lumio-gen-contract-types",
    },
    ArtifactSpec {
        slug: "contract_runtime",
        package: "lumio-gen-contract-runtime",
    },
];

pub(crate) fn verify() -> ExitCode {
    let repository_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    match verify_repository(&repository_root) {
        Ok(()) => {
            println!("contract verification passed");
            ExitCode::SUCCESS
        }
        Err(errors) => {
            for error in errors {
                eprintln!("contract verification: {error}");
            }
            ExitCode::from(4)
        }
    }
}

fn verify_repository(repository_root: &Path) -> Result<(), Vec<String>> {
    let mut errors = Vec::new();

    for relative_lock in LOCK_FILES {
        let lock_path = repository_root.join(relative_lock);
        let text = match fs::read_to_string(&lock_path) {
            Ok(text) => text,
            Err(error) => {
                errors.push(format!("{}: {error}", lock_path.display()));
                continue;
            }
        };
        let fields = match parse_lock_text(&text) {
            Ok(fields) => fields,
            Err(error) => {
                errors.push(format!("{}: {error}", lock_path.display()));
                continue;
            }
        };

        let field_errors = validate_lock_fields(&fields);
        let unavailable_only =
            field_errors.len() == 1 && field_errors[0].contains("artifact unavailable");
        errors.extend(
            field_errors
                .into_iter()
                .map(|error| format!("{relative_lock}: {error}")),
        );

        match fields.get("availability").map(String::as_str) {
            Some("available") if errors_for_lock_are_absent(&fields) => {
                if fields.get("contract").map(String::as_str) == Some("architecture") {
                    errors.extend(
                        verify_architecture_contract(repository_root, &fields)
                            .into_iter()
                            .map(|error| format!("{relative_lock}: {error}")),
                    );
                }
            }
            Some("missing") if unavailable_only => {
                errors.extend(
                    verify_missing_boundary(repository_root, &fields)
                        .into_iter()
                        .map(|error| format!("{relative_lock}: {error}")),
                );
            }
            _ => {}
        }
    }

    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors)
    }
}

fn errors_for_lock_are_absent(fields: &BTreeMap<String, String>) -> bool {
    validate_lock_fields(fields).is_empty()
}

fn parse_lock_text(text: &str) -> Result<BTreeMap<String, String>, String> {
    let mut fields = BTreeMap::new();
    for (index, raw_line) in text.lines().enumerate() {
        let line_number = index + 1;
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') {
            return Err(format!(
                "line {line_number}: tables and arrays are not supported in contract locks"
            ));
        }
        let Some((raw_key, raw_value)) = line.split_once('=') else {
            return Err(format!("line {line_number}: expected key = value"));
        };
        let key = raw_key.trim();
        if key.is_empty()
            || !key
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
        {
            return Err(format!("line {line_number}: invalid key {key:?}"));
        }
        let value = parse_lock_value(raw_value.trim(), line_number)?;
        if fields.insert(key.to_owned(), value).is_some() {
            return Err(format!("line {line_number}: duplicate key {key}"));
        }
    }
    Ok(fields)
}

fn parse_lock_value(raw: &str, line_number: usize) -> Result<String, String> {
    if let Some(inner) = raw
        .strip_prefix('"')
        .and_then(|value| value.strip_suffix('"'))
    {
        if inner.contains(['"', '\\']) {
            return Err(format!(
                "line {line_number}: escapes and embedded quotes are not supported"
            ));
        }
        return Ok(inner.to_owned());
    }
    if !raw.is_empty() && raw.bytes().all(|byte| byte.is_ascii_digit()) {
        return Ok(raw.to_owned());
    }
    Err(format!(
        "line {line_number}: expected an integer or a basic quoted string"
    ))
}

fn validate_lock_fields(fields: &BTreeMap<String, String>) -> Vec<String> {
    let mut errors = Vec::new();
    require_fields(
        fields,
        &[
            "lock_format",
            "contract",
            "availability",
            "source_repository",
            "source_path",
            "source_commit",
            "baseline_id",
        ],
        &mut errors,
    );
    if fields.get("lock_format").map(String::as_str) != Some("1") {
        errors.push("lock_format must be 1".to_owned());
    }
    if fields.get("baseline_id").map(String::as_str) != Some(BASELINE_ID) {
        errors.push(format!("baseline_id must be {BASELINE_ID}"));
    }
    if let Some(commit) = fields.get("source_commit") {
        validate_hex_field("source_commit", commit, 40, &mut errors);
    }
    for (key, value) in fields {
        let lower = value.to_ascii_lowercase();
        if lower.contains("v0.3") || value.contains("LGE-V1.2") {
            errors.push(format!(
                "{key} references a forbidden compatibility pointer"
            ));
        }
    }

    match fields.get("availability").map(String::as_str) {
        Some("available") => validate_available_fields(fields, &mut errors),
        Some("missing") => {
            require_fields(
                fields,
                &[
                    "expected_artifact",
                    "missing_reason",
                    "boundary_path",
                    "boundary_hash",
                ],
                &mut errors,
            );
            if let Some(hash) = fields.get("boundary_hash") {
                validate_hex_field("boundary_hash", hash, 64, &mut errors);
            }
            let contract = fields.get("contract").map_or("unknown", String::as_str);
            let reason = fields
                .get("missing_reason")
                .map_or("no reason recorded", String::as_str);
            errors.push(format!("{contract} artifact unavailable: {reason}"));
        }
        Some(other) => errors.push(format!("unsupported availability {other:?}")),
        None => {}
    }
    errors
}

fn validate_available_fields(fields: &BTreeMap<String, String>, errors: &mut Vec<String>) {
    require_fields(
        fields,
        &[
            "version",
            "schema_epoch",
            "content_hash",
            "input_hash",
            "generator_identity",
            "generator_command",
            "baseline_source_commit",
            "baseline_document",
            "baseline_document_hash",
            "consumer_path",
            "consumer_hash",
            "positive_fixture",
            "negative_fixture",
            "abi_positive_fixture",
            "abi_negative_fixture",
            "json_naming_fixture",
            "type_naming_source",
            "c_abi_naming_fixture",
        ],
        errors,
    );
    for key in [
        "content_hash",
        "input_hash",
        "generator_identity",
        "baseline_document_hash",
        "consumer_hash",
    ] {
        if let Some(value) = fields.get(key) {
            validate_hex_field(key, value, 64, errors);
        }
    }
    if let Some(commit) = fields.get("baseline_source_commit") {
        validate_hex_field("baseline_source_commit", commit, 40, errors);
    }
    if fields.get("generator_command").map(String::as_str)
        != Some("python tools/lumio_contract.py generate --out packages")
    {
        errors.push("generator_command is not the published upstream command".to_owned());
    }
    if fields.get("contract").map(String::as_str) != Some("architecture") {
        errors.push("only the architecture contract is currently available".to_owned());
    }
    for artifact in &ARCHITECTURE_ARTIFACTS {
        let path_key = format!("artifact_{}_path", artifact.slug);
        let hash_key = format!("artifact_{}_hash", artifact.slug);
        require_fields(fields, &[&path_key, &hash_key], errors);
        if let Some(hash) = fields.get(&hash_key) {
            validate_hex_field(&hash_key, hash, 64, errors);
        }
    }
}

fn require_fields(fields: &BTreeMap<String, String>, required: &[&str], errors: &mut Vec<String>) {
    for key in required {
        if fields.get(*key).is_none_or(String::is_empty) {
            errors.push(format!("missing required field {key}"));
        }
    }
}

fn validate_hex_field(key: &str, value: &str, length: usize, errors: &mut Vec<String>) {
    if value.len() != length
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        errors.push(format!(
            "{key} must be {length} lowercase hexadecimal characters"
        ));
    }
}

fn verify_missing_boundary(
    repository_root: &Path,
    fields: &BTreeMap<String, String>,
) -> Vec<String> {
    let mut errors = verify_source_commit(fields);
    let Some(relative_path) = fields.get("boundary_path") else {
        return errors;
    };
    let Some(expected_hash) = fields.get("boundary_hash") else {
        return errors;
    };
    match repository_path(repository_root, relative_path) {
        Ok(path) => match directory_hash(&path, false) {
            Ok(actual_hash) if actual_hash == *expected_hash => {}
            Ok(actual_hash) => errors.push(format!(
                "controlled refusal boundary hash drift at {}: expected {expected_hash}, got {actual_hash}",
                path.display()
            )),
            Err(error) => errors.push(error),
        },
        Err(error) => errors.push(error),
    }
    errors
}

fn verify_architecture_contract(
    repository_root: &Path,
    fields: &BTreeMap<String, String>,
) -> Vec<String> {
    let mut errors = verify_source_commit(fields);
    let source_root = PathBuf::from(&fields["source_path"]);
    let source_commit = &fields["source_commit"];
    if let Err(error) =
        verify_git_paths_unchanged(&source_root, source_commit, &ARCHITECTURE_LOCKED_PATHS)
    {
        errors.push(error);
    }

    verify_baseline_document(&source_root, fields, &mut errors);
    verify_generator_identity(&source_root, fields, &mut errors);
    verify_input_hash(&source_root, fields, &mut errors);
    verify_architecture_artifacts(&source_root, fields, &mut errors);
    verify_architecture_wrapper(repository_root, fields, &mut errors);
    verify_consumer_hash(repository_root, fields, &mut errors);
    verify_naming_evidence(&source_root, fields, &mut errors);
    verify_upstream_commands(&source_root, fields, &mut errors);
    errors
}

fn verify_source_commit(fields: &BTreeMap<String, String>) -> Vec<String> {
    let mut errors = Vec::new();
    let (Some(source_path), Some(source_commit)) =
        (fields.get("source_path"), fields.get("source_commit"))
    else {
        return errors;
    };
    let root = Path::new(source_path);
    if !root.is_dir() {
        errors.push(format!(
            "source repository does not exist: {}",
            root.display()
        ));
        return errors;
    }
    let object = format!("{source_commit}^{{commit}}");
    match Command::new("git")
        .args(["-C", source_path, "cat-file", "-e", &object])
        .output()
    {
        Ok(output) if output.status.success() => {}
        Ok(output) => errors.push(format!(
            "source commit {source_commit} is unavailable in {}: {}",
            root.display(),
            command_error(&output)
        )),
        Err(error) => errors.push(format!("could not execute git: {error}")),
    }
    errors
}

fn verify_git_paths_unchanged(
    source_root: &Path,
    source_commit: &str,
    locked_paths: &[&str],
) -> Result<(), String> {
    let mut command = Command::new("git");
    command
        .arg("-C")
        .arg(source_root)
        .args(["diff", "--quiet", source_commit, "--"])
        .args(locked_paths);
    let output = command
        .output()
        .map_err(|error| format!("could not execute git diff: {error}"))?;
    if output.status.success() {
        Ok(())
    } else if output.status.code() == Some(1) {
        Err(format!(
            "upstream contract inputs differ from locked artifact commit {source_commit}"
        ))
    } else {
        Err(format!("git diff failed: {}", command_error(&output)))
    }
}

fn verify_baseline_document(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let object = format!(
        "{}:{}",
        fields["baseline_source_commit"], fields["baseline_document"]
    );
    match Command::new("git")
        .arg("-C")
        .arg(source_root)
        .args(["show", &object])
        .output()
    {
        Ok(output) if output.status.success() => check_hash(
            "baseline document",
            &fields["baseline_document_hash"],
            &sha256_hex(&output.stdout),
            errors,
        ),
        Ok(output) => errors.push(format!(
            "could not read locked baseline document: {}",
            command_error(&output)
        )),
        Err(error) => errors.push(format!("could not execute git show: {error}")),
    }
}

fn verify_generator_identity(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let mut bytes = Vec::new();
    for relative in ["tools/lumio_contract.py", "tools/lumio_generate.py"] {
        match fs::read(source_root.join(relative)) {
            Ok(mut file) => bytes.append(&mut file),
            Err(error) => {
                errors.push(format!(
                    "cannot read upstream generator {relative}: {error}"
                ));
                return;
            }
        }
    }
    check_hash(
        "generator identity",
        &fields["generator_identity"],
        &sha256_hex(&bytes),
        errors,
    );
}

fn verify_input_hash(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    match architecture_input_hash(source_root) {
        Ok(actual) => check_hash("generator input", &fields["input_hash"], &actual, errors),
        Err(error) => errors.push(error),
    }
}

fn verify_architecture_artifacts(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let index = source_root.join("packages/index.json");
    match file_hash(&index) {
        Ok(actual) => check_hash("artifact index", &fields["content_hash"], &actual, errors),
        Err(error) => errors.push(error),
    }

    for artifact in &ARCHITECTURE_ARTIFACTS {
        let path_key = format!("artifact_{}_path", artifact.slug);
        let hash_key = format!("artifact_{}_hash", artifact.slug);
        let path = source_root.join(&fields[&path_key]);
        match directory_hash(&path, true) {
            Ok(actual) => check_hash(artifact.package, &fields[&hash_key], &actual, errors),
            Err(error) => errors.push(error),
        }
    }
}

fn verify_consumer_hash(
    repository_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    match repository_path(repository_root, &fields["consumer_path"])
        .and_then(|path| directory_hash(&path, false))
    {
        Ok(actual) => check_hash(
            "consumer wrapper",
            &fields["consumer_hash"],
            &actual,
            errors,
        ),
        Err(error) => errors.push(error),
    }
}

fn verify_architecture_wrapper(
    repository_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let Some(consumer_path) = fields.get("consumer_path") else {
        return;
    };
    let manifest = match repository_path(repository_root, consumer_path) {
        Ok(path) => path.join("Cargo.toml"),
        Err(error) => {
            errors.push(error);
            return;
        }
    };
    match fs::read_to_string(&manifest) {
        Ok(text) => errors.extend(validate_architecture_wrapper_text(&text, fields)),
        Err(error) => errors.push(format!("cannot read {}: {error}", manifest.display())),
    }
}

fn validate_architecture_wrapper_text(
    text: &str,
    fields: &BTreeMap<String, String>,
) -> Vec<String> {
    let dependencies = match parse_wrapper_dependencies(text) {
        Ok(dependencies) => dependencies,
        Err(error) => return vec![format!("architecture wrapper manifest: {error}")],
    };
    let expected_packages = ARCHITECTURE_ARTIFACTS
        .iter()
        .map(|artifact| artifact.package)
        .collect::<std::collections::BTreeSet<_>>();
    let actual_packages = dependencies
        .keys()
        .map(String::as_str)
        .collect::<std::collections::BTreeSet<_>>();
    let mut errors = Vec::new();
    for missing in expected_packages.difference(&actual_packages) {
        errors.push(format!(
            "architecture wrapper dependency `{missing}` is missing"
        ));
    }
    for extra in actual_packages.difference(&expected_packages) {
        errors.push(format!(
            "architecture wrapper has unguarded dependency `{extra}`"
        ));
    }

    let expected_coordinates = [
        ("git", fields.get("source_repository").cloned()),
        ("rev", fields.get("source_commit").cloned()),
        (
            "version",
            fields.get("version").map(|version| format!("={version}")),
        ),
    ];
    for artifact in &ARCHITECTURE_ARTIFACTS {
        let Some(coordinates) = dependencies.get(artifact.package) else {
            continue;
        };
        for (key, expected) in &expected_coordinates {
            let Some(expected) = expected else {
                errors.push(format!(
                    "architecture lock is missing `{key}` for wrapper validation"
                ));
                continue;
            };
            match coordinates.get(*key) {
                Some(actual) if actual == expected => {}
                Some(actual) => errors.push(format!(
                    "architecture wrapper dependency `{}` {key} drift: expected `{expected}`, got `{actual}`",
                    artifact.package
                )),
                None => errors.push(format!(
                    "architecture wrapper dependency `{}` is missing {key}",
                    artifact.package
                )),
            }
        }
        for extra in coordinates.keys().filter(|key| {
            !expected_coordinates
                .iter()
                .any(|(expected, _)| *expected == key.as_str())
        }) {
            errors.push(format!(
                "architecture wrapper dependency `{}` has unguarded coordinate `{extra}`",
                artifact.package
            ));
        }
    }
    errors
}

fn parse_wrapper_dependencies(
    text: &str,
) -> Result<BTreeMap<String, BTreeMap<String, String>>, String> {
    let mut dependencies = BTreeMap::new();
    let mut in_dependencies = false;
    let mut found_dependencies = false;
    for (index, raw_line) in text.lines().enumerate() {
        let line_number = index + 1;
        let line = raw_line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') {
            in_dependencies = line == "[dependencies]";
            found_dependencies |= in_dependencies;
            continue;
        }
        if !in_dependencies {
            continue;
        }
        let Some((raw_package, raw_coordinates)) = line.split_once('=') else {
            return Err(format!(
                "line {line_number}: expected dependency = inline table"
            ));
        };
        let package = raw_package
            .trim()
            .trim_matches(|character| matches!(character, '\'' | '"'));
        if package.is_empty() {
            return Err(format!("line {line_number}: empty dependency name"));
        }
        let coordinates = parse_wrapper_coordinates(raw_coordinates, line_number)?;
        if dependencies
            .insert(package.to_owned(), coordinates)
            .is_some()
        {
            return Err(format!(
                "line {line_number}: duplicate dependency `{package}`"
            ));
        }
    }
    if !found_dependencies {
        return Err("missing [dependencies] table".to_owned());
    }
    Ok(dependencies)
}

fn parse_wrapper_coordinates(
    raw: &str,
    line_number: usize,
) -> Result<BTreeMap<String, String>, String> {
    let raw = raw.trim();
    let Some(inner) = raw
        .strip_prefix('{')
        .and_then(|value| value.strip_suffix('}'))
    else {
        return Err(format!(
            "line {line_number}: dependency must use an inline table"
        ));
    };
    let mut coordinates = BTreeMap::new();
    let mut start = 0;
    let mut in_string = false;
    let mut escaped = false;
    let mut entries = Vec::new();
    for (index, character) in inner.char_indices() {
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
        } else if character == ',' {
            entries.push(&inner[start..index]);
            start = index + 1;
        }
    }
    if in_string {
        return Err(format!(
            "line {line_number}: unterminated string in dependency coordinates"
        ));
    }
    entries.push(&inner[start..]);
    for entry in entries {
        let Some((raw_key, raw_value)) = entry.split_once('=') else {
            return Err(format!(
                "line {line_number}: expected coordinate key = value"
            ));
        };
        let key = raw_key.trim();
        if key.is_empty() {
            return Err(format!("line {line_number}: empty coordinate key"));
        }
        let value = parse_lock_value(raw_value.trim(), line_number)?;
        if coordinates.insert(key.to_owned(), value).is_some() {
            return Err(format!(
                "line {line_number}: duplicate dependency coordinate `{key}`"
            ));
        }
    }
    Ok(coordinates)
}

fn verify_naming_evidence(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let json_fixture = source_root.join(&fields["json_naming_fixture"]);
    match fs::read(&json_fixture) {
        Ok(bytes) => {
            if let Err(error) = verify_camel_case_json_keys(&bytes) {
                errors.push(format!("camelCase JSON evidence: {error}"));
            }
        }
        Err(error) => errors.push(format!("cannot read {}: {error}", json_fixture.display())),
    }

    let type_source = source_root.join(&fields["type_naming_source"]);
    match fs::read_to_string(&type_source) {
        Ok(source) => {
            if let Err(error) = verify_pascal_case_bindings(&source) {
                errors.push(format!("PascalCase type evidence: {error}"));
            }
        }
        Err(error) => errors.push(format!("cannot read {}: {error}", type_source.display())),
    }

    let abi_fixture = source_root.join(&fields["c_abi_naming_fixture"]);
    match fs::read(&abi_fixture) {
        Ok(bytes) => {
            if let Err(error) = verify_snake_case_abi_names(&bytes) {
                errors.push(format!("snake_case C ABI evidence: {error}"));
            }
        }
        Err(error) => errors.push(format!("cannot read {}: {error}", abi_fixture.display())),
    }
}

fn verify_upstream_commands(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    errors: &mut Vec<String>,
) {
    let python = match PythonCommand::discover() {
        Ok(python) => python,
        Err(error) => {
            errors.push(error);
            return;
        }
    };
    for fixture_key in [
        "positive_fixture",
        "negative_fixture",
        "abi_positive_fixture",
        "abi_negative_fixture",
    ] {
        let args = [
            "tools/lumio_contract.py",
            "validate",
            "--fixture",
            fields[fixture_key].as_str(),
            "--json",
        ];
        if let Err(error) = python.run(source_root, &args) {
            errors.push(format!("fixture {} failed: {error}", fields[fixture_key]));
        }
    }
    if let Err(error) = python.run(
        source_root,
        &["tools/lumio_contract.py", "validate", "--json"],
    ) {
        errors.push(format!("upstream fixture suite failed: {error}"));
    }

    if let Err(error) = verify_regenerated_artifacts(source_root, fields, &python) {
        errors.push(error);
    }
}

fn verify_regenerated_artifacts(
    source_root: &Path,
    fields: &BTreeMap<String, String>,
    python: &PythonCommand,
) -> Result<(), String> {
    let temporary_root = env::temp_dir().join(format!(
        "lumio-server-contract-verify-{}",
        std::process::id()
    ));
    fs::create_dir(&temporary_root).map_err(|error| {
        format!(
            "cannot create verification directory {}: {error}",
            temporary_root.display()
        )
    })?;

    let output_argument = temporary_root
        .to_str()
        .ok_or_else(|| "temporary path is not UTF-8".to_owned())?;
    let generate_result = python.run(
        source_root,
        &[
            "tools/lumio_contract.py",
            "generate",
            "--out",
            output_argument,
        ],
    );
    let verification_result = generate_result.and_then(|()| {
        for artifact in &ARCHITECTURE_ARTIFACTS {
            let hash_key = format!("artifact_{}_hash", artifact.slug);
            let path = temporary_root.join("rust").join(artifact.package);
            let actual = directory_hash(&path, true)?;
            if actual != fields[&hash_key] {
                return Err(format!(
                    "regenerated {} hash drift: expected {}, got {actual}",
                    artifact.package, fields[&hash_key]
                ));
            }
        }
        Ok(())
    });

    let cleanup_result = fs::remove_dir_all(&temporary_root).map_err(|error| {
        format!(
            "cannot remove verification directory {}: {error}",
            temporary_root.display()
        )
    });
    verification_result.and(cleanup_result)
}

struct PythonCommand {
    program: String,
    prefix: Vec<String>,
}

impl PythonCommand {
    fn discover() -> Result<Self, String> {
        let mut candidates = Vec::new();
        if let Ok(program) = env::var("PYTHON") {
            candidates.push((program, Vec::new()));
        }
        if cfg!(windows) {
            if let Ok(local_app_data) = env::var("LOCALAPPDATA") {
                let python_root = Path::new(&local_app_data).join("Programs/Python");
                if let Ok(entries) = sorted_directory_entries(&python_root) {
                    for directory in entries.into_iter().rev() {
                        let program = directory.join("python.exe");
                        if program.is_file() {
                            candidates.push((program.to_string_lossy().into_owned(), Vec::new()));
                        }
                    }
                }
            }
        }
        candidates.extend([
            ("python3".to_owned(), Vec::new()),
            ("python".to_owned(), Vec::new()),
            ("py".to_owned(), vec!["-3".to_owned()]),
        ]);
        for (program, prefix) in candidates {
            let output = Command::new(&program)
                .args(&prefix)
                .arg("--version")
                .output();
            if output.is_ok_and(|value| value.status.success()) {
                return Ok(Self { program, prefix });
            }
        }
        Err("no usable Python 3 interpreter was found".to_owned())
    }

    fn run(&self, current_dir: &Path, args: &[&str]) -> Result<(), String> {
        let output = Command::new(&self.program)
            .args(&self.prefix)
            .args(args)
            .current_dir(current_dir)
            .output()
            .map_err(|error| format!("could not run {}: {error}", self.program))?;
        if output.status.success() {
            Ok(())
        } else {
            Err(command_error(&output))
        }
    }
}

fn command_error(output: &std::process::Output) -> String {
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    format!(
        "exit {:?}; stdout={}; stderr={}",
        output.status.code(),
        truncate(stdout.trim(), 500),
        truncate(stderr.trim(), 500)
    )
}

fn truncate(value: &str, limit: usize) -> &str {
    value.get(..limit).unwrap_or(value)
}

fn check_hash(label: &str, expected: &str, actual: &str, errors: &mut Vec<String>) {
    if expected != actual {
        errors.push(format!(
            "{label} hash drift: expected {expected}, got {actual}"
        ));
    }
}

fn repository_path(repository_root: &Path, relative: &str) -> Result<PathBuf, String> {
    let path = Path::new(relative);
    if path.is_absolute()
        || path.components().any(|component| {
            matches!(
                component,
                std::path::Component::ParentDir
                    | std::path::Component::RootDir
                    | std::path::Component::Prefix(_)
            )
        })
    {
        return Err(format!("repository path must stay relative: {relative}"));
    }
    Ok(repository_root.join(path))
}

fn architecture_input_hash(source_root: &Path) -> Result<String, String> {
    let mut files = Vec::new();
    collect_json_files(source_root, &source_root.join("schemas"), &mut files)?;
    collect_json_files(source_root, &source_root.join("fixtures"), &mut files)?;
    files.push((
        "ids/index.json".to_owned(),
        source_root.join("ids/index.json"),
    ));
    files.sort_by(|left, right| left.0.cmp(&right.0));

    let mut material = Vec::new();
    for (index, (relative, path)) in files.into_iter().enumerate() {
        if index != 0 {
            material.push(b'\n');
        }
        material.extend_from_slice(relative.as_bytes());
        material.push(0);
        material.extend_from_slice(
            &fs::read(&path).map_err(|error| format!("cannot read {}: {error}", path.display()))?,
        );
    }
    Ok(sha256_hex(&material))
}

fn collect_json_files(
    source_root: &Path,
    directory: &Path,
    files: &mut Vec<(String, PathBuf)>,
) -> Result<(), String> {
    for path in sorted_directory_entries(directory)? {
        let file_type = fs::symlink_metadata(&path)
            .map_err(|error| format!("cannot inspect {}: {error}", path.display()))?
            .file_type();
        if file_type.is_symlink() {
            return Err(format!(
                "contract input may not be a symlink: {}",
                path.display()
            ));
        }
        if file_type.is_dir() {
            collect_json_files(source_root, &path, files)?;
        } else if file_type.is_file() && path.extension().is_some_and(|value| value == "json") {
            files.push((slash_relative(source_root, &path)?, path));
        }
    }
    Ok(())
}

fn directory_hash(directory: &Path, skip_descriptor: bool) -> Result<String, String> {
    if !directory.is_dir() {
        return Err(format!(
            "contract directory does not exist: {}",
            directory.display()
        ));
    }
    let mut paths = Vec::new();
    collect_regular_files(directory, directory, &mut paths)?;
    paths.sort_by(|left, right| left.0.cmp(&right.0));

    let mut lines = Vec::new();
    for (relative, path) in paths {
        if skip_descriptor && relative.ends_with(".descriptor.json") {
            continue;
        }
        lines.push(format!("{relative}={}", directory_file_hash(&path)?));
    }
    Ok(sha256_hex(lines.join("\n").as_bytes()))
}

fn directory_file_hash(path: &Path) -> Result<String, String> {
    const TEXT_EXTENSIONS: &[&str] = &[
        "cs", "csproj", "json", "md", "props", "rs", "targets", "toml",
    ];
    let bytes =
        fs::read(path).map_err(|error| format!("cannot hash {}: {error}", path.display()))?;
    let is_text = path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| TEXT_EXTENSIONS.contains(&extension));
    if is_text {
        if let Ok(text) = std::str::from_utf8(&bytes) {
            return Ok(sha256_hex(text.replace("\r\n", "\n").as_bytes()));
        }
    }
    Ok(sha256_hex(&bytes))
}

fn collect_regular_files(
    root: &Path,
    directory: &Path,
    paths: &mut Vec<(String, PathBuf)>,
) -> Result<(), String> {
    for path in sorted_directory_entries(directory)? {
        let file_type = fs::symlink_metadata(&path)
            .map_err(|error| format!("cannot inspect {}: {error}", path.display()))?
            .file_type();
        if file_type.is_symlink() {
            return Err(format!(
                "contract directory may not contain symlinks: {}",
                path.display()
            ));
        }
        if file_type.is_dir() {
            collect_regular_files(root, &path, paths)?;
        } else if file_type.is_file() {
            paths.push((slash_relative(root, &path)?, path));
        }
    }
    Ok(())
}

fn sorted_directory_entries(directory: &Path) -> Result<Vec<PathBuf>, String> {
    let mut paths = fs::read_dir(directory)
        .map_err(|error| format!("cannot read {}: {error}", directory.display()))?
        .map(|entry| {
            entry
                .map(|value| value.path())
                .map_err(|error| format!("cannot read {}: {error}", directory.display()))
        })
        .collect::<Result<Vec<_>, _>>()?;
    paths.sort();
    Ok(paths)
}

fn slash_relative(root: &Path, path: &Path) -> Result<String, String> {
    path.strip_prefix(root)
        .map_err(|error| format!("{} is outside {}: {error}", path.display(), root.display()))?
        .to_str()
        .map(|value| value.replace('\\', "/"))
        .ok_or_else(|| format!("path is not UTF-8: {}", path.display()))
}

fn file_hash(path: &Path) -> Result<String, String> {
    fs::read(path)
        .map(|bytes| sha256_hex(&bytes))
        .map_err(|error| format!("cannot hash {}: {error}", path.display()))
}

fn verify_camel_case_json_keys(bytes: &[u8]) -> Result<(), String> {
    let tokens = json_string_tokens(bytes)?;
    let keys: Vec<&str> = tokens
        .iter()
        .filter(|token| token.is_key)
        .map(|token| token.value.as_str())
        .collect();
    if keys.is_empty() {
        return Err("no JSON object keys were found".to_owned());
    }
    for key in keys {
        if !is_lower_camel_case(key) {
            return Err(format!("JSON key {key:?} is not camelCase"));
        }
    }
    Ok(())
}

fn verify_pascal_case_bindings(source: &str) -> Result<(), String> {
    let mut names = Vec::new();
    for marker in ["rust_type: \"", "csharp_type: \""] {
        let mut remaining = source;
        while let Some(start) = remaining.find(marker) {
            remaining = &remaining[start + marker.len()..];
            let Some(end) = remaining.find('"') else {
                return Err(format!("unterminated binding after {marker}"));
            };
            names.push(&remaining[..end]);
            remaining = &remaining[end + 1..];
        }
    }
    if names.is_empty() {
        return Err("no generated type bindings were found".to_owned());
    }
    for name in names {
        if !is_pascal_case(name) {
            return Err(format!("generated type {name:?} is not PascalCase"));
        }
    }
    Ok(())
}

fn verify_snake_case_abi_names(bytes: &[u8]) -> Result<(), String> {
    let tokens = json_string_tokens(bytes)?;
    let mut names = Vec::new();
    for pair in tokens.windows(2) {
        if pair[0].is_key
            && matches!(
                pair[0].value.as_str(),
                "name" | "entrySymbol" | "symbolPrefix"
            )
            && !pair[1].is_key
        {
            names.push((pair[0].value.as_str(), pair[1].value.as_str()));
        }
    }
    if names.is_empty() {
        return Err("no C ABI names were found".to_owned());
    }
    for (key, name) in names {
        let allow_trailing_underscore = key == "symbolPrefix";
        if !is_snake_case(name, allow_trailing_underscore) {
            return Err(format!("C ABI name {name:?} is not snake_case"));
        }
    }
    Ok(())
}

struct JsonStringToken {
    value: String,
    is_key: bool,
}

fn json_string_tokens(bytes: &[u8]) -> Result<Vec<JsonStringToken>, String> {
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] != b'"' {
            index += 1;
            continue;
        }
        index += 1;
        let mut value = String::new();
        while index < bytes.len() && bytes[index] != b'"' {
            if bytes[index] == b'\\' {
                index += 1;
                let Some(escaped) = bytes.get(index).copied() else {
                    return Err("unterminated JSON escape".to_owned());
                };
                let decoded = match escaped {
                    b'"' => '"',
                    b'\\' => '\\',
                    b'/' => '/',
                    b'b' => '\u{0008}',
                    b'f' => '\u{000c}',
                    b'n' => '\n',
                    b'r' => '\r',
                    b't' => '\t',
                    b'u' => {
                        return Err(
                            "Unicode escapes are not supported in naming evidence".to_owned()
                        )
                    }
                    _ => return Err("invalid JSON escape".to_owned()),
                };
                value.push(decoded);
            } else if bytes[index].is_ascii() {
                value.push(char::from(bytes[index]));
            } else {
                return Err("non-ASCII naming evidence is not supported".to_owned());
            }
            index += 1;
        }
        if index == bytes.len() {
            return Err("unterminated JSON string".to_owned());
        }
        index += 1;
        let mut lookahead = index;
        while bytes.get(lookahead).is_some_and(u8::is_ascii_whitespace) {
            lookahead += 1;
        }
        tokens.push(JsonStringToken {
            value,
            is_key: bytes.get(lookahead) == Some(&b':'),
        });
    }
    Ok(tokens)
}

fn is_lower_camel_case(value: &str) -> bool {
    value.as_bytes().first().is_some_and(u8::is_ascii_lowercase)
        && value.bytes().all(|byte| byte.is_ascii_alphanumeric())
}

fn is_pascal_case(value: &str) -> bool {
    value.as_bytes().first().is_some_and(u8::is_ascii_uppercase)
        && value.bytes().all(|byte| byte.is_ascii_alphanumeric())
}

fn is_snake_case(value: &str, allow_trailing_underscore: bool) -> bool {
    let bytes = value.as_bytes();
    bytes.first().is_some_and(u8::is_ascii_lowercase)
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'_')
        && !value.contains("__")
        && (allow_trailing_underscore || !value.ends_with('_'))
}

fn sha256_hex(data: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let bytes = sha256(data);
    let mut output = String::with_capacity(64);
    for byte in bytes {
        output.push(char::from(HEX[usize::from(byte >> 4)]));
        output.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    output
}

const SHA256_ROUND_CONSTANTS: [u32; 64] = [
    0x428a_2f98,
    0x7137_4491,
    0xb5c0_fbcf,
    0xe9b5_dba5,
    0x3956_c25b,
    0x59f1_11f1,
    0x923f_82a4,
    0xab1c_5ed5,
    0xd807_aa98,
    0x1283_5b01,
    0x2431_85be,
    0x550c_7dc3,
    0x72be_5d74,
    0x80de_b1fe,
    0x9bdc_06a7,
    0xc19b_f174,
    0xe49b_69c1,
    0xefbe_4786,
    0x0fc1_9dc6,
    0x240c_a1cc,
    0x2de9_2c6f,
    0x4a74_84aa,
    0x5cb0_a9dc,
    0x76f9_88da,
    0x983e_5152,
    0xa831_c66d,
    0xb003_27c8,
    0xbf59_7fc7,
    0xc6e0_0bf3,
    0xd5a7_9147,
    0x06ca_6351,
    0x1429_2967,
    0x27b7_0a85,
    0x2e1b_2138,
    0x4d2c_6dfc,
    0x5338_0d13,
    0x650a_7354,
    0x766a_0abb,
    0x81c2_c92e,
    0x9272_2c85,
    0xa2bf_e8a1,
    0xa81a_664b,
    0xc24b_8b70,
    0xc76c_51a3,
    0xd192_e819,
    0xd699_0624,
    0xf40e_3585,
    0x106a_a070,
    0x19a4_c116,
    0x1e37_6c08,
    0x2748_774c,
    0x34b0_bcb5,
    0x391c_0cb3,
    0x4ed8_aa4a,
    0x5b9c_ca4f,
    0x682e_6ff3,
    0x748f_82ee,
    0x78a5_636f,
    0x84c8_7814,
    0x8cc7_0208,
    0x90be_fffa,
    0xa450_6ceb,
    0xbef9_a3f7,
    0xc671_78f2,
];

// The verifier cannot depend on an artifact that it is responsible for authenticating.
#[allow(clippy::many_single_char_names, clippy::needless_range_loop)]
fn sha256(data: &[u8]) -> [u8; 32] {
    let mut hash: [u32; 8] = [
        0x6a09_e667,
        0xbb67_ae85,
        0x3c6e_f372,
        0xa54f_f53a,
        0x510e_527f,
        0x9b05_688c,
        0x1f83_d9ab,
        0x5be0_cd19,
    ];
    let bit_length = (data.len() as u64) * 8;
    let mut padded = data.to_vec();
    padded.push(0x80);
    while padded.len() % 64 != 56 {
        padded.push(0);
    }
    padded.extend_from_slice(&bit_length.to_be_bytes());

    let (chunks, remainder) = padded.as_chunks::<64>();
    debug_assert!(remainder.is_empty());
    for chunk in chunks {
        let mut words = [0_u32; 64];
        for index in 0..16 {
            words[index] = u32::from_be_bytes(
                chunk[index * 4..index * 4 + 4]
                    .try_into()
                    .expect("four-byte SHA-256 word"),
            );
        }
        for index in 16..64 {
            let s0 = words[index - 15].rotate_right(7)
                ^ words[index - 15].rotate_right(18)
                ^ (words[index - 15] >> 3);
            let s1 = words[index - 2].rotate_right(17)
                ^ words[index - 2].rotate_right(19)
                ^ (words[index - 2] >> 10);
            words[index] = words[index - 16]
                .wrapping_add(s0)
                .wrapping_add(words[index - 7])
                .wrapping_add(s1);
        }

        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h] = hash;
        for index in 0..64 {
            let sigma1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let choice = (e & f) ^ ((!e) & g);
            let first = h
                .wrapping_add(sigma1)
                .wrapping_add(choice)
                .wrapping_add(SHA256_ROUND_CONSTANTS[index])
                .wrapping_add(words[index]);
            let sigma0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let majority = (a & b) ^ (a & c) ^ (b & c);
            let second = sigma0.wrapping_add(majority);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(first);
            d = c;
            c = b;
            b = a;
            a = first.wrapping_add(second);
        }
        hash[0] = hash[0].wrapping_add(a);
        hash[1] = hash[1].wrapping_add(b);
        hash[2] = hash[2].wrapping_add(c);
        hash[3] = hash[3].wrapping_add(d);
        hash[4] = hash[4].wrapping_add(e);
        hash[5] = hash[5].wrapping_add(f);
        hash[6] = hash[6].wrapping_add(g);
        hash[7] = hash[7].wrapping_add(h);
    }

    let mut output = [0_u8; 32];
    for (index, word) in hash.iter().enumerate() {
        output[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn fixture_root(label: &str) -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system time after epoch")
            .as_nanos();
        let root = env::temp_dir().join(format!(
            "lumio-contract-{label}-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir_all(&root).expect("create contract fixture root");
        root
    }

    #[test]
    fn sha256_matches_standard_vectors() {
        assert_eq!(
            sha256_hex(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(
            sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn verifier_entrypoint_is_linkable() {
        let entrypoint: fn() -> ExitCode = verify;
        let _ = entrypoint;
    }

    #[test]
    fn parses_the_supported_lock_manifest_subset() {
        let fields = parse_lock_text(
            r#"
                lock_format = 1
                contract = "architecture"
                availability = "available"
                content_hash = "abc123"
            "#,
        )
        .expect("valid lock");

        assert_eq!(fields.get("lock_format").map(String::as_str), Some("1"));
        assert_eq!(
            fields.get("availability").map(String::as_str),
            Some("available")
        );
        assert!(parse_lock_text("contract = \"a\"\ncontract = \"b\"").is_err());
        assert!(parse_lock_text("contract = [\"a\"]").is_err());
    }

    #[test]
    fn enforces_contract_spelling_rules() {
        let json = br#"{
            "abiVersion": 1,
            "apiTable": [{"name": "lumio_core_api", "entrySymbol": "lumio_core_get_api_v1"}]
        }"#;
        assert!(verify_camel_case_json_keys(json).is_ok());
        assert!(verify_snake_case_abi_names(json).is_ok());
        assert!(verify_camel_case_json_keys(br#"{"abi_version": 1}"#).is_err());
        assert!(verify_snake_case_abi_names(
            br#"{"name": "LumioCoreApi", "entrySymbol": "lumio_core_get_api_v1"}"#
        )
        .is_err());

        let bindings = r#"
            Binding { rust_type: "NativeManagedAbi", csharp_type: "NativeManagedAbi" },
        "#;
        assert!(verify_pascal_case_bindings(bindings).is_ok());
        assert!(verify_pascal_case_bindings("rust_type: \"nativeManagedAbi\"").is_err());
    }

    #[test]
    fn reports_missing_artifacts_as_a_hard_failure() {
        let fields = parse_lock_text(
            r#"
                lock_format = 1
                contract = "managed-host"
                availability = "missing"
                source_repository = "LumioGameRuntime"
                source_path = "C:/Work/LumioGames/LumioGameRuntime"
                source_commit = "5e480dd4a6fc9989d05ae8cd257a80c3b5eac513"
                baseline_id = "LGE-V1.4-2026-08-27"
                expected_artifact = "Managed Host ABI Rust package"
                missing_reason = "No published Rust package exists"
                boundary_path = "generated/lumio-managed-host-contracts"
                boundary_hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            "#,
        )
        .expect("valid lock syntax");

        let errors = validate_lock_fields(&fields);
        assert!(errors.iter().any(|error| error.contains("unavailable")));
    }

    #[test]
    fn every_required_available_lock_field_is_enforced() {
        let fields = parse_lock_text(include_str!(
            "../../../contracts/architecture-contracts.lock.toml"
        ))
        .expect("architecture lock syntax");
        assert!(
            validate_lock_fields(&fields).is_empty(),
            "checked-in architecture lock fields are invalid"
        );

        let required = [
            "lock_format",
            "contract",
            "availability",
            "source_repository",
            "source_path",
            "source_commit",
            "baseline_id",
            "version",
            "schema_epoch",
            "content_hash",
            "input_hash",
            "generator_identity",
            "generator_command",
            "baseline_source_commit",
            "baseline_document",
            "baseline_document_hash",
            "consumer_path",
            "consumer_hash",
            "positive_fixture",
            "negative_fixture",
            "abi_positive_fixture",
            "abi_negative_fixture",
            "json_naming_fixture",
            "type_naming_source",
            "c_abi_naming_fixture",
        ];
        for key in required {
            let mut mutated = fields.clone();
            mutated.remove(key);
            assert!(
                validate_lock_fields(&mutated)
                    .iter()
                    .any(|error| error.contains(&format!("missing required field {key}"))),
                "deleting required lock field `{key}` was accepted"
            );
        }
        for artifact in &ARCHITECTURE_ARTIFACTS {
            for key in [
                format!("artifact_{}_path", artifact.slug),
                format!("artifact_{}_hash", artifact.slug),
            ] {
                let mut mutated = fields.clone();
                mutated.remove(&key);
                assert!(
                    validate_lock_fields(&mutated)
                        .iter()
                        .any(|error| error.contains(&format!("missing required field {key}"))),
                    "deleting required lock field `{key}` was accepted"
                );
            }
        }
    }

    #[test]
    fn compatibility_v03_pointers_are_rejected() {
        let mut fields = parse_lock_text(include_str!(
            "../../../contracts/architecture-contracts.lock.toml"
        ))
        .expect("architecture lock syntax");
        fields.insert(
            "compatibility_source".to_owned(),
            "compat/v0.3/legacy-contract.json".to_owned(),
        );
        assert!(validate_lock_fields(&fields)
            .iter()
            .any(|error| error.contains("forbidden compatibility pointer")));
    }

    #[test]
    fn architecture_input_hash_covers_invalid_and_index_fixtures() {
        let root = fixture_root("input-hash");
        for directory in ["schemas", "fixtures/valid", "fixtures/invalid", "ids"] {
            fs::create_dir_all(root.join(directory)).expect("create input directory");
        }
        fs::write(root.join("schemas/example.json"), b"{}").expect("write schema");
        fs::write(root.join("fixtures/valid/example.json"), b"{}").expect("write valid fixture");
        fs::write(root.join("fixtures/invalid/example.json"), b"{}")
            .expect("write invalid fixture");
        fs::write(root.join("fixtures/index.json"), b"{}").expect("write fixture index");
        fs::write(root.join("ids/index.json"), b"{}").expect("write id index");

        let baseline = architecture_input_hash(&root).expect("hash complete input tree");
        fs::write(
            root.join("fixtures/invalid/example.json"),
            b"{\"mutated\":true}",
        )
        .expect("mutate invalid fixture");
        let invalid_mutation = architecture_input_hash(&root).expect("rehash invalid fixture");
        assert_ne!(
            baseline, invalid_mutation,
            "invalid fixture drift was ignored"
        );

        fs::write(root.join("fixtures/index.json"), b"{\"mutated\":true}")
            .expect("mutate fixture index");
        let index_mutation = architecture_input_hash(&root).expect("rehash fixture index");
        assert_ne!(
            invalid_mutation, index_mutation,
            "fixture index drift was ignored"
        );
        fs::remove_dir_all(root).expect("remove input hash fixture");
    }

    #[test]
    fn consumer_and_refusal_boundary_hash_drift_are_rejected() {
        let root = fixture_root("local-hash-drift");
        let consumer = root.join("generated/architecture");
        let boundary = root.join("generated/missing");
        fs::create_dir_all(&consumer).expect("create consumer");
        fs::create_dir_all(&boundary).expect("create boundary");
        fs::write(consumer.join("Cargo.toml"), b"original").expect("write consumer");
        fs::write(boundary.join("Cargo.toml"), b"original").expect("write boundary");
        let consumer_hash = directory_hash(&consumer, false).expect("hash consumer");
        let boundary_hash = directory_hash(&boundary, false).expect("hash boundary");
        fs::write(consumer.join("Cargo.toml"), b"mutated").expect("mutate consumer");
        fs::write(boundary.join("Cargo.toml"), b"mutated").expect("mutate boundary");

        let consumer_fields = BTreeMap::from([
            (
                "consumer_path".to_owned(),
                "generated/architecture".to_owned(),
            ),
            ("consumer_hash".to_owned(), consumer_hash),
        ]);
        let boundary_fields = BTreeMap::from([
            ("boundary_path".to_owned(), "generated/missing".to_owned()),
            ("boundary_hash".to_owned(), boundary_hash),
        ]);
        let mut consumer_errors = Vec::new();
        verify_consumer_hash(&root, &consumer_fields, &mut consumer_errors);
        assert!(consumer_errors
            .iter()
            .any(|error| error.contains("consumer wrapper hash drift")));
        assert!(verify_missing_boundary(&root, &boundary_fields)
            .iter()
            .any(|error| error.contains("controlled refusal boundary hash drift")));
        fs::remove_dir_all(root).expect("remove local hash fixture");
    }

    #[test]
    fn directory_hash_is_stable_across_text_line_endings() {
        let root = fixture_root("line-endings");
        fs::write(root.join("Cargo.toml"), b"[package]\nname = \"fixture\"\n")
            .expect("write LF fixture");
        fs::write(root.join("lib.rs"), b"pub fn fixture() {}\n").expect("write LF source");
        let lf_hash = directory_hash(&root, false).expect("hash LF source");

        fs::write(
            root.join("Cargo.toml"),
            b"[package]\r\nname = \"fixture\"\r\n",
        )
        .expect("write CRLF fixture");
        fs::write(root.join("lib.rs"), b"pub fn fixture() {}\r\n").expect("write CRLF source");
        let crlf_hash = directory_hash(&root, false).expect("hash CRLF source");

        assert_eq!(
            lf_hash, crlf_hash,
            "text line endings changed the lock hash"
        );
        fs::remove_dir_all(root).expect("remove line ending fixture");
    }

    #[test]
    fn architecture_wrapper_dependency_coordinates_are_locked() {
        let fields = parse_lock_text(include_str!(
            "../../../contracts/architecture-contracts.lock.toml"
        ))
        .expect("architecture lock syntax");
        let wrapper = include_str!("../../../generated/lumio-architecture-contracts/Cargo.toml");
        assert!(
            validate_architecture_wrapper_text(wrapper, &fields).is_empty(),
            "checked-in wrapper must match the architecture lock"
        );

        for (from, to, label) in [
            (
                "https://github.com/LumioGames/LumioGameEngineArchitecture",
                "https://example.invalid/LumioGameEngineArchitecture",
                "git",
            ),
            (
                "3d5e29db72b70c88fb61e392832afe2a762b25cb",
                "0000000000000000000000000000000000000000",
                "rev",
            ),
            ("version = \"=0.0.0\"", "version = \"=9.9.9\"", "version"),
        ] {
            let mutated = wrapper.replacen(from, to, 1);
            let errors = validate_architecture_wrapper_text(&mutated, &fields);
            assert!(
                errors.iter().any(|error| error.contains(label)),
                "wrapper {label} drift was accepted: {errors:?}"
            );
        }
    }

    #[test]
    fn architecture_lock_covers_the_complete_fixture_tree() {
        assert!(ARCHITECTURE_LOCKED_PATHS.contains(&"fixtures"));
        assert!(!ARCHITECTURE_LOCKED_PATHS.contains(&"fixtures/valid"));
    }

    #[test]
    #[ignore = "requires the three sibling upstream repositories and Python 3"]
    fn repository_verifier_reports_only_the_two_unpublished_packages() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let errors = verify_repository(&root).expect_err("two upstream packages are unpublished");

        assert_eq!(
            errors.len(),
            2,
            "unexpected verification errors: {errors:#?}"
        );
        assert!(errors
            .iter()
            .all(|error| error.contains("artifact unavailable")));
    }
}
