namespace Lumio.Server.MvpHost.Admission;

/// <summary>
/// Frozen field names and limits from architecture <c>lumio.entity-binding-query.v1</c>
/// at commit <c>fb3dca45</c>. This type is a consumer pin, not a second protocol.
/// Admission failure codes that originate in <c>lumio.account-port.v1</c> are
/// repeated here so Game Server can name them without a second semantic truth.
/// </summary>
public static class EntityBindingPort
{
    public const string ContractId = "lumio.entity-binding-query.v1";
    public const string FrozenArchitectureCommit = "fb3dca451aef5b392876e284ba871b05e58186bb";
    public const string FrozenContractSha256 = "0cff8d3d15ff94f3e80939f72aae58eee14456a263277d4f82652eb5a17b726a";

    public const string PlayerEntityType = "player";
    public const string BotEntityType = "bot";
    public const string TakeoverReasonCode = "connection_superseded";
    public const int MaxBindingsPerRoom = 4096;

    public const string AdmissionCredentialMalformed = "admission_credential_malformed";
    public const string AdmissionCredentialInvalidSignature = "admission_credential_invalid_signature";
    public const string AdmissionCredentialExpired = "admission_credential_expired";
    public const string BotNamespaceAdmissionForbidden = "bot_namespace_admission_forbidden";
    public const string InvalidRequest = "invalid_request";
    public const string CrossRoomReference = "cross_room_reference";
    public const string BindingNotFound = "binding_not_found";
    public const string StaleGeneration = "stale_generation";
    public const string NonExistent = "non_existent";
}
