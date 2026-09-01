using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.Account;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumio.Server.Account.App;

internal sealed class AccountProtocolServer : IAsyncDisposable
{
    private readonly AccountRuntime runtime;
    private readonly string listenHost;
    private readonly int listenPort;
    private WebApplication? application;
    private bool disposed;

    public AccountProtocolServer(AccountRuntime runtime, string listenHost, int listenPort)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.listenHost = listenHost ?? throw new ArgumentNullException(nameof(listenHost));
        this.listenPort = listenPort;
    }

    public int BoundPort { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(listenHost, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("account-server binds 127.0.0.1 only");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(AccountProtocolServer).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, listenPort));

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleRequestAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        application = app;
        BoundPort = ResolveBoundPort(app.Urls);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (application is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await application.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(AccountPort.Subprotocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(AccountPort.Subprotocol)
            .ConfigureAwait(false);
        var cancellation = context.RequestAborted;
        while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
        {
            byte[]? payload;
            WebSocketMessageType type;
            try
            {
                (payload, type) = await ReceiveMessageAsync(socket, cancellation).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                await WriteErrorAndCloseAsync(
                    socket,
                    AccountErrorCode.InvalidRequest,
                    "frame exceeds limits",
                    cancellation).ConfigureAwait(false);
                return;
            }

            if (payload is null || type == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        socket.CloseStatusDescription ?? "closed",
                        cancellation).ConfigureAwait(false);
                }

                return;
            }

            if (type != WebSocketMessageType.Text)
            {
                await WriteErrorAndCloseAsync(
                    socket,
                    AccountErrorCode.InvalidRequest,
                    "expected a text frame",
                    cancellation).ConfigureAwait(false);
                return;
            }

            if (payload.Length > AccountPort.MaxRequestJsonBytes)
            {
                await WriteErrorAndCloseAsync(
                    socket,
                    AccountErrorCode.InvalidRequest,
                    "request JSON exceeds maxRequestJsonBytes",
                    cancellation).ConfigureAwait(false);
                return;
            }

            if (!TryReadLogin(payload, out var loginName, out var password, out var botTool, out var close))
            {
                await WriteErrorAsync(socket, AccountErrorCode.InvalidRequest, "malformed LoginOrRegister", cancellation)
                    .ConfigureAwait(false);
                if (close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid_request", cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                continue;
            }

            var outcome = runtime.LoginOrRegister(loginName, password, botTool);
            if (outcome.Accepted)
            {
                await WriteAckAsync(socket, outcome, cancellation).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorAsync(
                    socket,
                    outcome.Code ?? AccountErrorCode.InvalidRequest,
                    outcome.Detail ?? string.Empty,
                    cancellation).ConfigureAwait(false);
                if (string.Equals(outcome.Code, AccountErrorCode.InvalidRequest, StringComparison.Ordinal))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid_request", cancellation)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private static bool TryReadLogin(
        byte[] json,
        out string loginName,
        out string password,
        out string? botToolCredential,
        out bool close)
    {
        loginName = string.Empty;
        password = string.Empty;
        botToolCredential = null;
        close = true;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("messageType", out var messageType)
                || messageType.GetString() != AccountPort.LoginOrRegisterMessageType
                || !root.TryGetProperty("loginName", out var loginElement)
                || loginElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("password", out var passwordElement)
                || passwordElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            loginName = loginElement.GetString() ?? string.Empty;
            password = passwordElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("botToolCredential", out var botElement)
                && botElement.ValueKind != JsonValueKind.Null)
            {
                if (botElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                botToolCredential = botElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<(byte[]? Payload, WebSocketMessageType Type)> ReceiveMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>(1024);
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (null, WebSocketMessageType.Close);
            }

            if (writer.WrittenCount + result.Count > AccountPort.MaxFrameBytes)
            {
                throw new InvalidDataException("maxFrameBytes");
            }

            writer.Write(buffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return (writer.WrittenSpan.ToArray(), result.MessageType);
            }
        }
    }

    private static async Task WriteAckAsync(
        WebSocket socket,
        LoginOrRegisterOutcome outcome,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("messageType", AccountPort.LoginOrRegisterAckMessageType);
            writer.WriteBoolean("accepted", true);
            writer.WriteBoolean("accountNewlyCreated", outcome.AccountNewlyCreated);
            writer.WriteString("accountId", outcome.AccountId);
            writer.WriteString("loginName", outcome.LoginName);
            writer.WriteString("admissionCredential", outcome.AdmissionCredential);
            writer.WriteNumber("admissionExpiresAt", outcome.AdmissionExpiresAt);
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        WebSocket socket,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("messageType", AccountPort.ErrorMessageType);
            writer.WriteString("code", code);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }

        await socket.SendAsync(stream.ToArray(), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteErrorAndCloseAsync(
        WebSocket socket,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        await WriteErrorAsync(socket, code, detail, cancellationToken).ConfigureAwait(false);
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static int ResolveBoundPort(ICollection<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("account-server did not bind a port");
    }
}
