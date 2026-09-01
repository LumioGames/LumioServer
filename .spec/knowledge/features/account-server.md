---
name: account-server
description: 独立账号进程 login-or-register、AccountEntity 与准入凭证签发；改账号服或消费 lumio.account-port.v1 时查
metadata:
  type: doc
  status: 已交付
---

# Account Server（独立进程）

`account-server/` 是 RM-00011 的独立 C# 账号进程：幂等 login-or-register、低频 ECS `AccountEntity`、Argon2id 凭证库、签名过期不透明准入凭证。字段真值是架构仓 `engine/wire/account-port-v1.json`（`lumio.account-port.v1`），本仓不另写协议。

## 背景 / 目标

浏览器与 Bot 进入 Room 前必须经真实 Account Server 认证。测试不得旁路账号系统。本切片每部署一个中心实例，只绑环回地址。

## 设计

- **进程**：`lumio-account-server` 监听 `127.0.0.1:0`，子协议 `lumio-account-v1`。就绪行 `ACCOUNT_SERVER_READY ` + JSON（`port`/`pid`/`contractId`/`storePath`）。Ed25519 私钥与 Bot 工具公钥经环境变量注入，不入库。
- **login-or-register**：用户名不存在则创建；存在且口令正确返回同一 AccountId；口令错误 `wrong_password` 且零覆写。并发首次登录收敛到一个 AccountEntity。
- **AccountEntity**：`AccountIdentityComponent`（accountId + loginName + createdAt）在账号服自有 World。凭证哈希只在独立凭证库，不进普通组件、不回响应、不进审计。
- **Bot 命名空间**：`^Bot[0-9]+$` 必须携带有效 Bot 工具凭证；普通客户端 register / login 分别 `bot_namespace_register_forbidden` / `bot_namespace_login_forbidden`。
- **准入凭证**：`LumioBinV1(payload) || raw-64 签名`，digest 的 64 hex 拼入 ADR-042 preimage。`nonce` 按 ABI `bytes16` 定宽裸 16 字节。`keyId` 是 payload 内 u8 轮换位，不从公钥派生。

## 待解决

- Game Server 的 `verify_admission` 端口与顶号（R-00346 / R-00350）不在本进程。
- 生产口令策略、在线吊销、非环回暴露的访问控制面。

## 相关

- 契约：架构仓 `engine/wire/account-port-v1.json`、ADR-054
- 实现：[`account-server/`](../../../account-server/README.md)
