using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Lumio.Server.Account;

public sealed class AccountRuntime : IDisposable
{
    private readonly AccountServerOptions options;
    private readonly AccountWorld world = new();
    private readonly CredentialStore credentials = new();
    private readonly DurableAccountStore store;
    private readonly object gate = new();
    private readonly Dictionary<string, object> nameGates = new(StringComparer.Ordinal);
    private bool disposed;

    private AccountRuntime(AccountServerOptions options)
    {
        this.options = options;
        store = new DurableAccountStore(options.StorePath);
        store.Load(world, credentials);
        AdmissionPublicKey = Ed25519Keys.PublicKeyFromSeed(options.AdmissionPrivateSeed);
        StorePath = store.DirectoryPath;
    }

    public static AccountRuntime Open(AccountServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new AccountRuntime(options);
    }

    public string StorePath { get; }

    public byte[] AdmissionPublicKey { get; }

    public int EntityCount => world.Count;

    public LoginOrRegisterOutcome LoginOrRegister(string loginName, string password, string? botToolCredential)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (loginName is null || password is null)
        {
            return Reject(loginName ?? string.Empty, AccountErrorCode.InvalidRequest, "missing fields");
        }

        if (!LoginNameRules.IsValid(loginName))
        {
            return Reject(loginName, AccountErrorCode.InvalidUsername, "loginName does not match grammar");
        }

        if (password.Length < AccountPort.PasswordMinLength || password.Length > AccountPort.PasswordMaxLength)
        {
            return Reject(loginName, AccountErrorCode.InvalidPassword, "password length out of range");
        }

        var botName = LoginNameRules.IsBotNamespace(loginName);
        if (botName && !string.IsNullOrEmpty(botToolCredential)
            && !BotToolCredential.TryVerify(botToolCredential, options.BotToolPublicKey, options.Clock, out var botCode))
        {
            return Reject(loginName, botCode, "bot-tool credential rejected");
        }

        lock (NameGate(loginName))
        {
            var exists = world.TryGetByLoginName(loginName, out var identity);
            if (botName && string.IsNullOrEmpty(botToolCredential))
            {
                return Reject(
                    loginName,
                    exists ? AccountErrorCode.BotNamespaceLoginForbidden : AccountErrorCode.BotNamespaceRegisterForbidden,
                    "bot namespace requires a valid bot-tool credential");
            }

            if (exists)
            {
                if (!credentials.TryGet(identity!.AccountId, out var encoded)
                    || !Argon2idPasswordHasher.Verify(encoded, password))
                {
                    return Reject(loginName, AccountErrorCode.WrongPassword, "password does not match");
                }

                return Issue(identity, newlyCreated: false);
            }

            var accountId = NewAccountId();
            var created = world.Create(accountId, loginName, options.Clock.UnixSeconds);
            credentials.Put(accountId, Argon2idPasswordHasher.Hash(password));
            lock (gate)
            {
                store.Save(world, credentials);
            }

            Audit("account_created", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["accountId"] = created.AccountId,
                ["loginName"] = created.LoginName,
            });
            return Issue(created, newlyCreated: true);
        }
    }

    private LoginOrRegisterOutcome Issue(AccountIdentityComponent identity, bool newlyCreated)
    {
        var issuedAt = options.Clock.UnixSeconds;
        var expiresAt = issuedAt + (ulong)AccountPort.AdmissionCredentialTtlSeconds;
        var credential = AdmissionCredential.Issue(
            options.AdmissionPrivateSeed,
            options.AdmissionKeyId,
            identity.AccountId,
            identity.LoginName,
            LoginNameRules.IsBotNamespace(identity.LoginName),
            issuedAt,
            expiresAt);
        Audit("login_succeeded", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accountId"] = identity.AccountId,
            ["accountNewlyCreated"] = newlyCreated ? "true" : "false",
        });
        Audit("admission_credential_issued", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accountId"] = identity.AccountId,
            ["keyId"] = options.AdmissionKeyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["expiresAt"] = expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return LoginOrRegisterOutcome.Ok(newlyCreated, identity.AccountId, identity.LoginName, credential, expiresAt);
    }

    private LoginOrRegisterOutcome Reject(string loginName, string code, string detail)
    {
        Audit("login_rejected", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["loginName"] = loginName,
            ["code"] = code,
        });
        return LoginOrRegisterOutcome.Reject(code, detail);
    }

    private void Audit(string kind, Dictionary<string, string> fields)
    {
        options.Audit.Write(kind, fields);
    }

    private string NewAccountId()
    {
        Span<byte> raw = stackalloc byte[16];
        while (true)
        {
            RandomNumberGenerator.Fill(raw);
            var candidate = "acct_" + Hex.EncodeLower(raw);
            if (!world.ContainsAccountId(candidate))
            {
                return candidate;
            }
        }
    }

    public AccountIdentityComponent? FindByLoginName(string loginName)
    {
        return world.TryGetByLoginName(loginName, out var identity) ? identity : null;
    }

    public IReadOnlyList<AccountIdentityComponent> SnapshotIdentities() => world.Snapshot();

    public AdmissionVerifyOutcome VerifyAdmission(string admissionCredential)
    {
        return AdmissionCredential.Verify(
            admissionCredential,
            options.AdmissionKeyId,
            AdmissionPublicKey,
            options.Clock);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            store.Save(world, credentials);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (gate)
        {
            store.Save(world, credentials);
        }

        CryptographicOperations.ZeroMemory(options.AdmissionPrivateSeed);
        disposed = true;
    }

    internal object NameGate(string loginName)
    {
        lock (nameGates)
        {
            if (!nameGates.TryGetValue(loginName, out var gateObject))
            {
                gateObject = new object();
                nameGates[loginName] = gateObject;
            }

            return gateObject;
        }
    }

    internal AccountWorld World => world;

    internal CredentialStore Credentials => credentials;

    internal DurableAccountStore Store => store;

    internal AccountServerOptions Options => options;

    internal object Gate => gate;
}
