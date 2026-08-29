using System.Collections.Generic;

namespace Lumio.Server.MvpHost.GeneratedContracts
{
    /// <summary>
    /// 拷进 <c>Generated/</c> 的架构源生成物的来源与指纹。
    ///
    /// **本文件由 <c>bash eng/generate-contracts.sh</c> 生成，不得手改。**
    /// 手改它等于伪造镜像的来源声明——而来源声明正是
    /// <c>bash eng/verify-generated-contracts.sh</c> 在架构源不可达时唯一能比对的东西。
    /// </summary>
    public static class GeneratedContractManifest
    {
        /// <summary>架构源声明的基线号，取自 <c>packages/csharp/Lumio.Gen.ContractTypes/artifact.descriptor.json</c>。</summary>
        public static string ArchitectureBaselineId => "LGE-V1.4-2026-08-27";

        /// <summary>拷贝所依据的架构源提交。跨仓引用只认已推送对象，工作区状态一律不采信。</summary>
        public static string ArchitectureCommit => "664ccd6cf77751190942439b9a4ac08184becdb6";

        /// <summary>架构源声明的 schema 世代。</summary>
        public static int SchemaEpoch => 1;

        /// <summary>
        /// <c>Generated/</c> 下每个 <c>.cs</c> 的 <c>sha256  相对路径</c>，格式与
        /// <c>shasum -a 256</c> 一致（两空格分隔），路径相对 <c>Generated/</c>。
        /// </summary>
        public static IReadOnlyList<string> ArtifactHashes { get; } = new[]
        {
            "a3088b84e7edf60a40436967a4f718170c8289d4fbe8223503c4d79b1acb3d3e  Lumio.Gen.CanonicalSerializer/CanonicalProfile.cs",
            "8e9edec3e509b866f22122324c03db43449bde590cbaa99f3973e4ad17a2563f  Lumio.Gen.CanonicalSerializer/CanonicalSerializer.cs",
            "a6491b1319daa26c9bf71f7a4e0070fcfab8ee0d7e6866ab70ed87dcca4bc383  Lumio.Gen.CanonicalSerializer/LumioBinProfile.cs",
            "ca82575f4293a77f6c4b22bb249c852330021adcb3c85b00d4ddc9190428f294  Lumio.Gen.ContractRuntime/ContractRuntime.cs",
            "be26683d6e96954cce71284d5742e1d1c87ee3bda095eb2b7813fd19a17db2ca  Lumio.Gen.ContractTypes/ContractBodies.cs",
            "ae326d6a407eb30c35b76b9a73e13c348ba8768494b01e61e1a5faf058220d4f  Lumio.Gen.ContractTypes/ContractTypes.cs",
            "3c429801118f92a9ced26e68d4dec869368e857963392e9ed60a8c8f72b5df99  Lumio.Gen.LanguageBinding/Bindings.cs",
            "08c80b5c7d9001dc6e6c4cce176a5bc26026277a937dc8e8614ead7706ca2110  Lumio.Gen.LanguageBinding/RootAbi.cs",
            "b3b009e0c13098cbddd63bc0bca030dfcf61366eacd1352e8b436cef2772e999  Lumio.Gen.MappingTable/MappingTable.cs",
            "7e953ad617f80ef13c90acecee43f758c18922a1cb2eacd0f5d9cfc8fcc7f855  Lumio.Gen.ProtocolPermissionValidator/ActivePermissionFields.cs",
            "8d081d7f732ed024f19cb08996641795625c1137af0854ee19aaf2215fe12b6c  Lumio.Gen.ProtocolPermissionValidator/ProtocolGate.cs",        };
    }
}
