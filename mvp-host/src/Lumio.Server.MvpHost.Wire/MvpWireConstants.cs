namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 本仓出站信封的单点常量。每一项都必须能指回一条公共依据或一条 <c>absences.json</c> 登记，
    /// 不存在「实现者临场选的值」。
    /// </summary>
    public static class MvpWireConstants
    {
        /// <summary>
        /// 空映射集的 <c>mappingSetHash</c>。ADR-045 §2 把该字段定义为 ADR-041 的
        /// <c>ReplicationMappingSetV1</c> 域摘要，空映射集**有定义值**——
        /// canonicalBytes = <c>{"digestDomain":"ReplicationMappingSetV1","mappings":[]}</c>。
        ///
        /// **不是空串、不是全零、不是省略成员**：ADR-045 的 Alternatives 逐一否决了三种 sentinel，
        /// 理由是每种都要在每个实现里加特例，省略成员还会重开 ADR-028 已关闭的「缺失意味什么」歧义。
        /// 本仓早先的 provisional「64 个 0」已构成对公共规则的违反，已在 R-00270 更正。
        ///
        /// 该值不是抄来的字面量：<c>SemanticLayerTest.MappingSetHash等于镜像golden且可自算复核</c>
        /// 从镜像的 <c>contract-mirror/canonical/canonical-digest-profile.json</c> 取 golden 比对，
        /// 并对其 <c>canonicalBytes</c> 自算 sha256 复核——常量 / golden / 自算三方一致才算数。
        /// 语义见 <c>absences.json</c> 的 <c>ABS-REPLICATION-MAPPING-SET</c>：含义仅为
        /// 「本 MVP 宿主不声明任何映射集」，**不构成对该字段语义的公共主张**。
        /// </summary>
        public const string MappingSetHash = "a805f7c841f708981cc82a93047d7b0c8e6bf923f3dba18e179036741a6d2ea7";

        /// <summary>
        /// 出站 <c>reliability</c> 恒取值。<c>FullSnapshot</c> 另有公共硬约束必须为 <c>Reliable</c>
        /// （<c>tools/lumio_contract.py</c> 的 <c>FullSnapshot must use Reliable</c>，
        /// 公共契约里唯一一条 messageType × reliability 交叉约束）。
        /// </summary>
        public const string Reliability = "Reliable";

        /// <summary>出站 <c>integrity.algorithm</c> 恒取值：本仓不产出任何校验和。</summary>
        public const string IntegrityAlgorithmNone = "None";

        /// <summary>出站 <c>integrity.value</c> 恒取值，由 schema 的 <c>None → ^none$</c> 分支定死。</summary>
        public const string IntegrityValueNone = "none";

        /// <summary>
        /// 出站 <c>protocolVersion</c>。取镜像 fixture 实测值；该字段的取值域由 schema 判定，
        /// 本仓不声明任何版本协商语义。
        /// </summary>
        public const int ProtocolVersion = 1;
    }
}
