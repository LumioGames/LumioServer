using System;
using System.Text.Json;

namespace Lumio.Server.Account.App;

public readonly record struct AccountReadyLine(int Port, int Pid, string ContractId, string StorePath)
{
    public const string Prefix = "ACCOUNT_SERVER_READY ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public override string ToString()
    {
        return Prefix + JsonSerializer.Serialize(new Payload(Port, Pid, ContractId, StorePath), JsonOptions);
    }

    public static bool TryParse(string? line, out AccountReadyLine ready)
    {
        ready = default;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(line.AsSpan(Prefix.Length), JsonOptions);
            if (payload is null
                || payload.Port <= 0
                || payload.Pid <= 0
                || string.IsNullOrEmpty(payload.ContractId)
                || string.IsNullOrEmpty(payload.StorePath))
            {
                return false;
            }

            ready = new AccountReadyLine(payload.Port, payload.Pid, payload.ContractId, payload.StorePath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(int Port, int Pid, string ContractId, string StorePath);
}
