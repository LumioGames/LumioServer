using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Loopback-only HTTP projection of ISessionAdminPort. It is created only when
/// the explicit test-control switch is present; it never carries envelopes.
/// </summary>
public sealed class TestControlServer : IAsyncDisposable
{
    private readonly WebApplication application;
    private bool disposed;

    private TestControlServer(WebApplication application, string boundUri)
    {
        this.application = application;
        BoundUri = boundUri;
    }

    public string BoundUri { get; }

    public static async ValueTask<TestControlServer> StartAsync(
        string listenUri,
        Func<ISessionAdminPort?> adminProvider,
        IMonotonicClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenUri);
        ArgumentNullException.ThrowIfNull(adminProvider);
        ArgumentNullException.ThrowIfNull(clock);
        if (!Uri.TryCreate(listenUri, UriKind.Absolute, out var requestedUri)
            || !requestedUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || !IsLoopbackHost(requestedUri.Host))
        {
            throw new ArgumentException("test control must bind to http://127.0.0.1 or http://[::1]", nameof(listenUri));
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(TestControlServer).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUri);

        var application = builder.Build();
        var server = new TestControlRouteState(adminProvider, clock);

        application.MapPost(
            "/test-control/begin-drain",
            context => HandleBeginDrainAsync(context, server));
        application.MapPost(
            "/test-control/kick",
            context => HandleKickAsync(context, server));
        application.MapPost(
            "/test-control/inject-world-mutation",
            context => HandleInjectMutationAsync(context, server));

        await application.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = application.Urls;
        var bound = FindBoundAddress(addresses, listenUri);
        return new TestControlServer(application, bound);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is bounded; disposal below still releases the listener.
        }

        await application.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task HandleBeginDrainAsync(HttpContext context, TestControlRouteState state)
    {
        var body = await ReadObjectAsync(context).ConfigureAwait(false);
        if (body is null || !TryReadInt(body, "graceSeconds", out var graceSeconds) || graceSeconds < 0)
        {
            await WriteResultAsync(context, new AckResult(false, "InvalidArgument")).ConfigureAwait(false);
            return;
        }

        var admin = state.AdminProvider();
        var result = admin is null
            ? new AckResult(false, "ContextClosing")
            : admin.BeginDrain(new MonotonicInstant(
                state.Clock.Now.Ticks + TimeSpan.FromSeconds(graceSeconds).Ticks));
        await WriteResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task HandleKickAsync(HttpContext context, TestControlRouteState state)
    {
        var body = await ReadObjectAsync(context).ConfigureAwait(false);
        if (body is null
            || !TryReadString(body, "sessionId", out var sessionId)
            || !TryReadString(body, "reasonCode", out var reasonCode))
        {
            await WriteResultAsync(context, new AckResult(false, "InvalidArgument")).ConfigureAwait(false);
            return;
        }

        var admin = state.AdminProvider();
        var result = admin is null
            ? new AckResult(false, "ContextClosing")
            : admin.Kick(new ServerSessionId(sessionId), reasonCode);
        await WriteResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task HandleInjectMutationAsync(HttpContext context, TestControlRouteState state)
    {
        var body = await ReadObjectAsync(context).ConfigureAwait(false);
        if (body is null
            || !TryReadString(body, "sessionId", out var sessionId)
            || !TryReadString(body, "opaqueCommandBase64", out var encoded))
        {
            await WriteResultAsync(context, new AckResult(false, "InvalidArgument")).ConfigureAwait(false);
            return;
        }

        byte[] command;
        try
        {
            command = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            await WriteResultAsync(context, new AckResult(false, "InvalidArgument")).ConfigureAwait(false);
            return;
        }

        var admin = state.AdminProvider();
        var result = admin is null
            ? new AckResult(false, "ContextClosing")
            : admin.InjectWorldMutation(new ServerSessionId(sessionId), command);
        await WriteResultAsync(context, result).ConfigureAwait(false);
    }

    private static async ValueTask<JsonObject?> ReadObjectAsync(HttpContext context)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask WriteResultAsync(HttpContext context, AckResult result)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status200OK;
        var body = new JsonObject
        {
            ["accepted"] = result.Accepted,
            ["stableErrorId"] = result.StableErrorId,
        };
        await context.Response.WriteAsync(body.ToJsonString()).ConfigureAwait(false);
    }

    private static bool TryReadString(JsonObject body, string name, out string value)
    {
        value = string.Empty;
        var node = body[name];
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var parsed)
            || string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadInt(JsonObject body, string name, out int value)
    {
        value = 0;
        var node = body[name];
        return node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value);
    }

    private static string FindBoundAddress(System.Collections.Generic.ICollection<string> addresses, string requested)
    {
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var parsed)
                && parsed.Port > 0)
            {
                return address.TrimEnd('/');
            }
        }

        return requested.TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalized = host.Trim('[', ']');
        return normalized.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestControlRouteState(Func<ISessionAdminPort?> AdminProvider, IMonotonicClock Clock);
}
