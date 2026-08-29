using System;
using System.Linq;
using Xunit;

namespace Lumio.Server.MvpHost.GeneratedContracts.Tests
{
    /// <summary>
    /// 拷进来的生成物确实是本仓声称的那条基线，而不是随手拿的另一份。
    ///
    /// **一律不断言元素个数**（总调度 2026-08-29 裁决）：`StableErrorIds` 在
    /// V1.4 基线内由 43 增至 53（ADR-046 的 native kernel status band），
    /// `SchemaIds` 同样会随 additive 增补变长——而 additive 增补正是被鼓励的。
    /// 计数断言必然腐烂，判据因此只取「存在性 + 身份」：具名 id 在册、BaselineId 相等。
    /// </summary>
    public sealed class BaselineSentinelTest
    {
        private const string CatalogType = "Lumio.Gen.ContractTypes.Catalog";

        [Fact]
        public void 基线号与本仓声称的一致()
        {
            var baselineId = GeneratedContractReflection.StaticValue<string>(CatalogType, "BaselineId");

            Assert.Equal("LGE-V1.4-2026-08-27", baselineId);
        }

        [Theory]
        [InlineData("replication-envelope")]
        [InlineData("protocol-permission-gate")]
        [InlineData("logging-event")]
        [InlineData("replication-mapping")]
        [InlineData("session-revision-vector")]
        public void 本仓镜像所依赖的具名SchemaId在册(string schemaId)
        {
            var schemaIds = GeneratedContractReflection.StaticValue<string[]>(CatalogType, "SchemaIds");

            Assert.Contains(schemaId, schemaIds);
        }

        /// <summary>
        /// 点名本仓实际会用到的错误码，不写总数。<c>BudgetExceeded</c> 在 A1 期是**多义码**
        /// （超长消息 / 队列背压预算共用），公共反例 <c>replication-length-exceeds-max</c>
        /// 只是其中一种成因：**可正向使用、不可反向断言**——收到该码推不出成因。
        /// </summary>
        [Theory]
        [InlineData("MessagePermissionDenied")]
        [InlineData("StaleConnectionGeneration")]
        [InlineData("SessionMismatch")]
        [InlineData("RoleMismatch")]
        [InlineData("ClaimNotGranted")]
        [InlineData("SessionAntiReplay")]
        [InlineData("BudgetExceeded")]
        [InlineData("ReleaseMismatch")]
        public void 本仓实际引用的具名ErrorCode在册(string errorId)
        {
            var errorIds = GeneratedContractReflection.StaticValue<string[]>(CatalogType, "StableErrorIds");

            Assert.Contains(errorId, errorIds);
        }

        [Fact]
        public void 状态迁移表里有WorldSlotHost这台状态机()
        {
            var transitions = GeneratedContractReflection
                .StaticValue<System.Collections.IEnumerable>("Lumio.Gen.ContractTypes.StateTransitionTable", "All")
                .Cast<object>()
                .ToList();

            var machines = transitions
                .Select(t => (string)t.GetType().GetProperty("Machine")!.GetValue(t)!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.Contains("WorldSlotHost", machines);
        }
    }
}
