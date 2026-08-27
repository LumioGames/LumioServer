---
status: pending
---
# 实现 ReleaseCatalog/Manifest 一致性与 ExactRelease Predicate

## 涉及范围

- **Wave：** 3
- **归属：** `release-agent`
- **唯一目标：** 验证configured gameReleaseId、Catalog、Manifest和artifact hashes，提供session exact-match结果。
- **文件集：
  - `modules/release-agent/Cargo.toml`
  - `modules/release-agent/src/lib.rs`
  - `modules/release-agent/src/identity.rs`
  - `modules/release-agent/src/catalog.rs`
  - `modules/release-agent/src/manifest.rs`
  - `modules/release-agent/src/verifier.rs`
  - `modules/release-agent/src/matching.rs`
  - `modules/release-agent/src/commands.rs`
  - `modules/release-agent/src/events.rs`
  - `modules/release-agent/src/error.rs`
  - `modules/release-agent/tests/release_fixture_test.rs`
  - `modules/release-agent/tests/exact_match_test.rs`

## 验收标准

- [ ] 全部上游ReleaseCatalog/Manifest正反fixture通过。
- [ ] duplicate route、hash mismatch、configured id mismatch均在listener开放前失败。
- [ ] V1只接受ExactRelease；DeclaredNMinusOne输入明确拒绝/不启用。
- [ ] 公共九态/字段不在本crate重定义。
- [ ] 验证结果不可变且含source hashes/provenance。

## 依赖

- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)
- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)

## 接口

Consumes:
- generated ReleaseCatalog/Manifest、artifact evidence

Produces:
- `VerifiedReleaseBundle`、`ReleaseMatchResult`
