//! Architecture bans for R-00374: consume-only Rust host.

use std::fs;
use std::path::{Path, PathBuf};

fn process_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn collect_text_files(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.filter_map(Result::ok) {
        let path = entry.path();
        if path.is_dir() {
            collect_text_files(&path, out);
            continue;
        }
        if path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| matches!(ext, "rs" | "cs" | "mjs" | "js" | "yml" | "md"))
        {
            out.push(path);
        }
    }
}

fn read_owned_sources() -> Vec<(PathBuf, String)> {
    let mut files = Vec::new();
    collect_text_files(&process_root().join("src/entity_chat"), &mut files);
    collect_text_files(&process_root().join("tests"), &mut files);
    collect_text_files(
        &process_root()
            .parent()
            .expect("modules")
            .parent()
            .expect("repo")
            .join("entity-chat-host/src"),
        &mut files,
    );
    files
        .into_iter()
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_none_or(|name| name != "entity_chat_architecture.rs")
        })
        .filter_map(|path| {
            fs::read_to_string(&path)
                .ok()
                .map(|text| (path, text.replace('\\', "/")))
        })
        .collect()
}

#[test]
fn host_src_has_no_private_binding_issue_query_or_expire_due() {
    let host = fs::read_to_string(process_root().join("src/entity_chat/host.rs")).expect("host.rs");
    for banned in [
        "by_account",
        "read_attribute",
        "expire_due",
        "next_net_entity_id",
        "instance_key",
        "tombstones",
    ] {
        assert!(
            !host.contains(banned),
            "host.rs must not contain `{banned}`"
        );
    }
}

#[test]
fn process_src_grep_bans_are_empty() {
    let mut files = Vec::new();
    collect_text_files(&process_root().join("src"), &mut files);
    let mut hits = Vec::new();
    for path in files {
        let text = fs::read_to_string(&path).expect("read");
        for banned in [
            "by_account",
            "read_attribute",
            "expire_due",
            "next_net_entity_id",
        ] {
            if text.contains(banned) {
                hits.push(format!("{}:{banned}", path.display()));
            }
        }
    }
    assert!(
        hits.is_empty(),
        "banned host-owned symbols still present: {hits:?}"
    );
}

#[test]
fn owned_sources_have_no_hardcoded_dev_machine_paths() {
    let mut hits = Vec::new();
    for (path, text) in read_owned_sources() {
        if text.contains("C:/Work") || text.contains("C:/Users") {
            hits.push(path.display().to_string());
        }
    }
    assert!(
        hits.is_empty(),
        "hardcoded C:/Work or C:/Users paths remain in {hits:?}"
    );
}
