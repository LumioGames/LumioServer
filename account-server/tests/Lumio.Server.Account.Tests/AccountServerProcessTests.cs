using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.Account;
using Lumio.Server.Account.App;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class AccountServerProcessTests
{
    [Fact]
    public void MissingArgumentsExitWithCode3()
    {
        Assert.Equal(3, Program.Main(["--unknown"]));
        Assert.Equal(AccountExitCodes.InvalidArguments, 3);
        Assert.Equal(AccountExitCodes.Success, 0);
        Assert.Equal(AccountExitCodes.InitializationFailed, 1);
        Assert.Equal(AccountExitCodes.Fatal, 2);
    }

    [Fact]
    public async Task ProcessLoginRestartReturnsPersistedBot01AccountId()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var store = Path.Combine(Path.GetTempPath(), "lumio-account-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        var admission = Ed25519Keys.Generate();
        var bot = Ed25519Keys.Generate();
        try
        {
            string accountId;
            await using (var process = await AccountServerProcess.StartAsync(store, admission.Seed, bot.PublicKey, timeout.Token))
            {
                var clock = new SystemAccountClock();
                var claim = BotToolCredential.Issue(bot.Seed, "bot-launcher", clock.UnixSeconds, clock.UnixSeconds + 3600);
                var first = await process.LoginAsync("Bot01", AccountTestProfile.Password, claim, timeout.Token);
                Assert.True(first.GetProperty("accepted").GetBoolean());
                Assert.True(first.GetProperty("accountNewlyCreated").GetBoolean());
                accountId = first.GetProperty("accountId").GetString()!;
                Assert.StartsWith("acct_", accountId, StringComparison.Ordinal);
                await process.StopAsync(timeout.Token);
            }

            await using (var process = await AccountServerProcess.StartAsync(store, admission.Seed, bot.PublicKey, timeout.Token))
            {
                var clock = new SystemAccountClock();
                var claim = BotToolCredential.Issue(bot.Seed, "bot-launcher", clock.UnixSeconds, clock.UnixSeconds + 3600);
                var second = await process.LoginAsync("Bot01", AccountTestProfile.Password, claim, timeout.Token);
                Assert.True(second.GetProperty("accepted").GetBoolean(), second.GetRawText());
                Assert.False(second.GetProperty("accountNewlyCreated").GetBoolean());
                Assert.Equal(accountId, second.GetProperty("accountId").GetString());
                await process.StopAsync(timeout.Token);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(store, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ProcessRejectsOrdinaryBotRegistrationOverWebsocket()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var store = Path.Combine(Path.GetTempPath(), "lumio-account-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(store);
        var admission = Ed25519Keys.Generate();
        var bot = Ed25519Keys.Generate();
        try
        {
            await using var process = await AccountServerProcess.StartAsync(store, admission.Seed, bot.PublicKey, timeout.Token);
            var error = await process.LoginAsync("Bot01", AccountTestProfile.Password, null, timeout.Token);
            Assert.Equal("Error", error.GetProperty("messageType").GetString());
            Assert.Equal(AccountErrorCode.BotNamespaceRegisterForbidden, error.GetProperty("code").GetString());
            await process.StopAsync(timeout.Token);
        }
        finally
        {
            try
            {
                Directory.Delete(store, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal sealed class AccountServerProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> standardError;
    private bool disposed;

    private AccountServerProcess(Process process, AccountReadyLine ready, Task<string> standardError)
    {
        this.process = process;
        Ready = ready;
        this.standardError = standardError;
    }

    public AccountReadyLine Ready { get; }

    public static async Task<AccountServerProcess> StartAsync(
        string storePath,
        byte[] admissionSeed,
        byte[] botPublicKey,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(Program).Assembly.Location;
        var directory = Path.GetDirectoryName(assembly) ?? throw new InvalidOperationException(assembly);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            process.StartInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        }

        process.StartInfo.Environment[AccountCommandLineParser.AdmissionPrivateKeyEnv] = Hex.EncodeLower(admissionSeed);
        process.StartInfo.Environment[AccountCommandLineParser.BotToolPublicKeyEnv] = Hex.EncodeLower(botPublicKey);
        process.StartInfo.ArgumentList.Add(assembly);
        process.StartInfo.ArgumentList.Add("--store-path");
        process.StartInfo.ArgumentList.Add(storePath);
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("127.0.0.1:0");

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("could not start account-server");
        }

        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 30);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException(
                        $"account-server exited before ready; exit={process.ExitCode}; stderr={await error.WaitAsync(cancellationToken).ConfigureAwait(false)}");
                }

                if (AccountReadyLine.TryParse(line, out var ready))
                {
                    Assert.Equal(AccountPort.ContractId, ready.ContractId);
                    Assert.Equal(process.Id, ready.Pid);
                    return new AccountServerProcess(process, ready, error);
                }
            }

            throw new TimeoutException("account-server ready line not observed");
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> LoginAsync(string loginName, string password, string? botToolCredential, CancellationToken cancellationToken)
    {
        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(AccountPort.Subprotocol);
        var uri = new Uri($"ws://127.0.0.1:{Ready.Port}/");
        await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(BuildRequest(loginName, password, botToolCredential));
        var json = document.RootElement.GetRawText();
        await client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[AccountPort.MaxFrameBytes];
        var received = await client.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
        using var response = JsonDocument.Parse(text);
        var clone = response.RootElement.Clone();
        try
        {
            if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
        }

        return clone;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
        _ = standardError;
    }

    private static string BuildRequest(string loginName, string password, string? botToolCredential)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("messageType", AccountPort.LoginOrRegisterMessageType);
            writer.WriteString("loginName", loginName);
            writer.WriteString("password", password);
            if (botToolCredential is not null)
            {
                writer.WriteString("botToolCredential", botToolCredential);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
