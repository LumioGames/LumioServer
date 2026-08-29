using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Lumio.Server.MvpHost.GeneratedContracts.Tests
{
    /// <summary>
    /// 自过期守卫：盯住「架构源的 C# 生成面到位了没有」，因为这决定本仓该不该自己写 DTO 与 gate。
    ///
    /// **本守卫已到期，并已按到期后的现实翻转。** 卡面写这三条时，架构源的 C# 面只有
    /// 字段名表；本轮实测（架构源 <c>origin/main</c>，2026-08-29）ADR-048 (D-3) 的
    /// 「closed contract type bodies」与可执行 gate 已经发布：
    ///
    /// - <c>Lumio.Gen.ContractTypes.ReplicationEnvelope</c> 是**已存在**的公开类型
    ///   （<c>ContractBodies.cs</c>，12 个字段按 schema 声明序，<c>Body</c> 为 <c>OpaqueJson</c>）；
    /// - <c>Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.Evaluate</c> 是**已存在**的公开校验方法。
    ///
    /// 因此守卫的结论已经从「等生成物」变成**给下游卡的硬指令**：
    /// <c>implement-mvp-envelope-wire-and-fixture-gate</c> **不得**再手写 Envelope DTO，
    /// auth 存根卡**不得**再手写 permission gate 执行体——直接用这里拷进来的生成类型。
    /// 三条一旦变红，说明架构源把已发布的面又撤了，属于反向漂移，必须停下上报而不是本地绕过。
    ///
    /// 反射范围限定在 <see cref="GeneratedContractReflection"/>（单个程序集 + <c>Lumio.Gen.*</c>
    /// 命名空间），不得改成遍历「引用到的全部 <c>Lumio.Gen.*</c> 程序集」——源码拷贝方案下
    /// 不存在独立的 <c>Lumio.Gen.*</c> 程序集，那样写集合恒空、断言静默失效。
    /// </summary>
    public sealed class ContractArtifactDebtTest
    {
        [Fact]
        public void 绑定表把复制信封SchemaId映射到CSharp类型名ReplicationEnvelope()
        {
            var bindings = GeneratedContractReflection
                .StaticValue<System.Collections.IEnumerable>("Lumio.Gen.LanguageBinding.Bindings", "All")
                .Cast<object>()
                .ToList();

            var match = bindings.SingleOrDefault(b =>
                (string)b.GetType().GetProperty("SchemaId")!.GetValue(b)! == "replication-envelope");

            Assert.NotNull(match);
            Assert.Equal("ReplicationEnvelope", (string)match.GetType().GetProperty("CsharpType")!.GetValue(match)!);
        }

        /// <summary>
        /// 守卫的翻转面之一。卡面原文断言「反射**不到**名为 ReplicationEnvelope 的公开类型」；
        /// 该类型现已随 ADR-048 发布，故断言改为正向的「在册」，并锁住 <c>Body</c> 的形状——
        /// <c>OpaqueJson</c> 表示 body 内层结构架构源**刻意未冻结**（D-009 未解冻，A1-β 仍 BLOCKED）。
        /// 谁把 <c>Body</c> 换成具体类型，谁就是在发明未裁的状态载荷。
        /// </summary>
        [Fact]
        public void 生成面已提供ReplicationEnvelope且body仍是未冻结的不透明JSON()
        {
            var envelope = GeneratedContractReflection.Require("Lumio.Gen.ContractTypes.ReplicationEnvelope");

            var body = envelope.GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(body);
            Assert.Equal("Lumio.Gen.ContractTypes.OpaqueJson", body.PropertyType.FullName);
        }

        [Fact]
        public void 权限字段名表里的具名字段在册()
        {
            var names = GeneratedContractReflection.StaticValue<string[]>(
                "Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields", "Names");

            foreach (var field in new[]
                     {
                         "sessionId", "productId", "gameReleaseId", "messageId", "role", "claims",
                         "connectionGeneration", "antiReplay", "verdict",
                     })
            {
                Assert.Contains(field, names);
            }
        }

        /// <summary>
        /// 守卫的翻转面之二。卡面原文断言「该命名空间下不存在任何公开的校验方法（只有字段名表）」；
        /// ADR-048 已发布可执行 gate，故断言改为正向的「存在且可调用」。
        /// 顺带锁住拒绝优先级表——多条同时失败时的判定顺序是公共规则，本仓不得自定。
        /// </summary>
        [Fact]
        public void 生成面已提供可执行的权限闸门及其拒绝优先级()
        {
            var gate = GeneratedContractReflection.Require("Lumio.Gen.ProtocolPermissionValidator.ProtocolGate");

            var evaluate = gate.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(evaluate);

            var precedence = GeneratedContractReflection.StaticValue<string[]>(
                "Lumio.Gen.ProtocolPermissionValidator.ProtocolGate", "RejectPrecedence");

            Assert.Equal("StaleConnectionGeneration", precedence[0]);
            Assert.Contains("MessagePermissionDenied", precedence);
        }

        /// <summary>
        /// 源码拷贝方案的健全性检查：反射范围一旦被写成恒空集合，上面几条会**全部**变成
        /// 「找不到类型」而不是静默通过——但这条把「集合非空」单独钉死，让失效原因一眼可读。
        /// </summary>
        [Fact]
        public void 反射范围非空()
        {
            Assert.NotEmpty(GeneratedContractReflection.PublicGenTypes);
        }
    }
}
