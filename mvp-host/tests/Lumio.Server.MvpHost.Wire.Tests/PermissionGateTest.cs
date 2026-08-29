using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// gate 的判定**不在本仓**——它在生成物 <c>ProtocolGate.Evaluate</c>。
    /// 这组测试因此测两件事：转发没转错，以及本仓没有偷偷写第二份判定。
    /// </summary>
    public sealed class PermissionGateTest
    {
        private static MvpPermissionGateRequest Admitted() => new(
            SessionId: "session-001",
            ProductId: "A",
            GameReleaseId: "A-1.1.0",
            MessageId: "Delta",
            Role: "Client",
            Claims: ImmutableArray.Create("replication.read"),
            ConnectionGeneration: 7,
            AdmittedSessionId: "session-001",
            AdmittedProductId: "A",
            AdmittedGameReleaseId: "A-1.1.0",
            AdmittedRole: "Client",
            AdmittedClaims: ImmutableArray.Create("replication.read"),
            AdmittedConnectionGeneration: 7);

        [Fact]
        public void 六项全等时判Accept且无拒绝理由()
        {
            var verdict = MvpProtocolPermissionGate.Evaluate(Admitted());

            Assert.True(verdict.Accepted);
            Assert.Null(verdict.RejectReason);
        }

        /// <summary>
        /// 代次不等时拒绝理由必须是 <c>StaleConnectionGeneration</c>——
        /// 它在生成物的 <c>RejectPrecedence</c> 里排第一，多项同时失败时先报它。
        /// 优先级是公共规则，本仓自定就会与其他实现分歧。
        /// </summary>
        [Fact]
        public void 代次不等时拒绝理由是StaleConnectionGeneration()
        {
            var request = Admitted() with { ConnectionGeneration = 6 };

            var verdict = MvpProtocolPermissionGate.Evaluate(request);

            Assert.False(verdict.Accepted);
            Assert.Equal("StaleConnectionGeneration", verdict.RejectReason);
        }

        [Fact]
        public void 代次与会话同时不等时仍先报代次()
        {
            var request = Admitted() with { ConnectionGeneration = 6, SessionId = "session-999" };

            var verdict = MvpProtocolPermissionGate.Evaluate(request);

            Assert.Equal("StaleConnectionGeneration", verdict.RejectReason);
        }

        [Theory]
        [InlineData("SessionMismatch")]
        [InlineData("ReleaseMismatch")]
        [InlineData("RoleMismatch")]
        [InlineData("ClaimNotGranted")]
        [InlineData("MessagePermissionDenied")]
        public void 拒绝理由取值在gate的schema登记的取值域内(string reason)
        {
            var schema = JsonNode.Parse(MirrorFixtures.ReadText("schemas", "protocol-permission-gate.schema.json"))!;
            var allowed = CollectEnumValues(schema, "rejectReason");

            Assert.Contains(reason, allowed);
        }

        [Fact]
        public void 未注册的messageId被拒且理由是MessagePermissionDenied()
        {
            var request = Admitted() with { MessageId = "UnknownType" };

            var verdict = MvpProtocolPermissionGate.Evaluate(request);

            Assert.False(verdict.Accepted);
            Assert.Equal("MessagePermissionDenied", verdict.RejectReason);
        }

        [Fact]
        public void claim不是admittedClaims子集时被拒()
        {
            var request = Admitted() with { Claims = ImmutableArray.Create("replication.read", "world.write") };

            var verdict = MvpProtocolPermissionGate.Evaluate(request);

            Assert.False(verdict.Accepted);
            Assert.Equal("ClaimNotGranted", verdict.RejectReason);
        }

        /// <summary>镜像 gate fixture 的回归：accept 与 stale-generation 各一条。</summary>
        [Fact]
        public void 镜像的两条gate用例判定与生成物一致()
        {
            var accept = JsonNode.Parse(
                MirrorFixtures.ReadText("fixtures", "valid", "protocol-permission-gate-accept.json"))!;
            Assert.Equal("Accept", accept["verdict"]!.GetValue<string>());
            Assert.True(MvpProtocolPermissionGate.Evaluate(FromFixture(accept)).Accepted);

            var stale = JsonNode.Parse(
                MirrorFixtures.ReadText("fixtures", "invalid", "protocol-permission-gate-stale-generation.json"))!;
            var staleVerdict = MvpProtocolPermissionGate.Evaluate(FromFixture(stale));
            Assert.False(staleVerdict.Accepted);
            Assert.Equal("StaleConnectionGeneration", staleVerdict.RejectReason);
        }

        /// <summary>
        /// 本仓没有第二份判定逻辑：<c>MvpProtocolPermissionGate</c> 的源码里
        /// 不出现任何拒绝理由字面量——它们全部来自生成物。
        /// 复制一份字面量就等于给了本仓一个独立漂移的入口。
        /// </summary>
        [Fact]
        public void 本仓源码不含任何拒绝理由字面量()
        {
            var source = File.ReadAllText(Path.Combine(
                MirrorFixtures.MvpHostRoot, "src", "Lumio.Server.MvpHost.Wire", "MvpProtocolPermissionGate.cs"));

            foreach (var reason in Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RejectPrecedence)
            {
                Assert.DoesNotContain($"\"{reason}\"", source, StringComparison.Ordinal);
            }
        }

        private static MvpPermissionGateRequest FromFixture(JsonNode fixture)
        {
            var claims = ToArray(fixture["claims"]);
            var admittedClaims = ToArray(fixture["admittedClaims"]);

            return new MvpPermissionGateRequest(
                SessionId: fixture["sessionId"]!.GetValue<string>(),
                ProductId: fixture["productId"]!.GetValue<string>(),
                GameReleaseId: fixture["gameReleaseId"]!.GetValue<string>(),
                MessageId: fixture["messageId"]!.GetValue<string>(),
                Role: fixture["role"]!.GetValue<string>(),
                Claims: claims,
                ConnectionGeneration: fixture["connectionGeneration"]!.GetValue<ulong>(),
                AdmittedSessionId: fixture["admittedSessionId"]!.GetValue<string>(),
                AdmittedProductId: fixture["admittedProductId"]!.GetValue<string>(),
                AdmittedGameReleaseId: fixture["admittedGameReleaseId"]!.GetValue<string>(),
                AdmittedRole: fixture["admittedRole"]!.GetValue<string>(),
                AdmittedClaims: admittedClaims,
                AdmittedConnectionGeneration: fixture["admittedConnectionGeneration"]!.GetValue<ulong>());
        }

        private static ImmutableArray<string> ToArray(JsonNode? node)
            => node is JsonArray array
                ? array.Select(x => x!.GetValue<string>()).ToImmutableArray()
                : ImmutableArray<string>.Empty;

        private static System.Collections.Generic.List<string> CollectEnumValues(JsonNode node, string propertyName)
        {
            var found = new System.Collections.Generic.List<string>();

            void Walk(JsonNode? current, string? key)
            {
                switch (current)
                {
                    case JsonObject obj:
                        if (key == propertyName && obj["enum"] is JsonArray values)
                        {
                            found.AddRange(values.Select(v => v!.GetValue<string>()));
                        }

                        foreach (var (childKey, child) in obj)
                        {
                            Walk(child, childKey);
                        }

                        break;
                    case JsonArray array:
                        foreach (var child in array)
                        {
                            Walk(child, key);
                        }

                        break;
                }
            }

            Walk(node, null);
            return found;
        }
    }
}
