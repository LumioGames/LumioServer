namespace Lumio.Server.Account;

/// <summary>
/// Frozen field names and limits from architecture <c>lumio.account-port.v1</c>
/// at commit <c>2b7e321</c>. This type is a consumer pin, not a second protocol.
/// </summary>
public static class AccountPort
{
    public const string ContractId = "lumio.account-port.v1";
    public const string Subprotocol = "lumio-account-v1";
    public const string FrozenArchitectureCommit = "2b7e3219a980946d2296f1fbf780a4cb854759f9";
    public const string FrozenContractSha256 = "29466228ee40b2a22b9cd924235acc7da9c3398b0d77777e655e32ab550bee9b";

    public const string LoginOrRegisterMessageType = "LoginOrRegister";
    public const string LoginOrRegisterAckMessageType = "LoginOrRegisterAck";
    public const string ErrorMessageType = "Error";

    public const int MaxFrameBytes = 65536;
    public const int MaxRequestJsonBytes = 16384;
    public const int LoginNameMinLength = 3;
    public const int LoginNameMaxLength = 32;
    public const int PasswordMinLength = 6;
    public const int PasswordMaxLength = 128;
    public const int AdmissionCredentialTtlSeconds = 300;
    public const int BotToolCredentialMaxTtlSeconds = 86400;

    public const ushort AdmissionPayloadVersion = 1;
    public const ushort BotToolPayloadVersion = 1;
    public const string AdmissionTrustDomain = "account-admission";
    public const string AdmissionPayloadType = "admission-credential-v1";
    public const string BotToolTrustDomain = "bot-tool";
    public const string BotToolPayloadType = "bot-tool-credential-v1";
    public const string BotToolScope = "bot-namespace";

    public const string AccountIdPattern = "^acct_[0-9a-f]{32}$";
    public const string LoginNamePattern = "^[A-Za-z][A-Za-z0-9_-]{2,31}$";
    public const string BotNamespacePattern = "^Bot[0-9]+$";

    public const int Argon2MemoryKib = 19456;
    public const int Argon2Iterations = 2;
    public const int Argon2Parallelism = 1;
    public const int Argon2HashLength = 32;
    public const int Argon2SaltLength = 16;
}
