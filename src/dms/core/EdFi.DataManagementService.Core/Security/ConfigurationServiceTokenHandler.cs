// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Security;

public interface IConfigurationServiceTokenHandler
{
    /// <summary>Returns the current bearer, obtaining one when there is no reusable bearer.</summary>
    Task<ConfigurationServiceTokenLease> GetTokenAsync(
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// After a protected read rejected <paramref name="rejected" />, returns a newer reusable bearer
    /// if one exists, joins a pending grant or returns a failed grant's safe failure inside its window,
    /// and otherwise invalidates the rejected generation and obtains a replacement.
    /// </summary>
    Task<ConfigurationServiceTokenLease> RefreshRejectedTokenAsync(
        ConfigurationServiceTokenLease rejected,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// One published bearer and the local generation that published it. Every successful grant has a new
/// generation, even when the issuer returns the same bearer text, so a late rejection of an older
/// lease can never evict a newer one. An ordinary class, so default formatting never prints the
/// bearer.
/// </summary>
public sealed class ConfigurationServiceTokenLease(string accessToken, long generation)
{
    public string AccessToken { get; } = accessToken;

    public long Generation { get; } = generation;
}

/// <summary>
/// Obtains and reuses the DMS system bearer for Configuration Service API calls. One process-local
/// coordinator owns the current bearer, the one pending grant and the failure window.
/// </summary>
/// <remarks>
/// <para>
/// A bearer with a positive issuer lifetime L is reused for L - min(60 s, 0.1 L) measured from the
/// start of its grant, so a slow grant consumes its own window. A missing, null, zero or negative
/// lifetime is reused for 300 seconds from acquisition. The deprecated
/// <see cref="ConfigurationServiceTokenCacheOptions" /> cap, measured from acquisition, only lowers
/// either window. At or after the cutoff the next caller starts a replacement.
/// </para>
/// <para>
/// One grant is in flight at a time and every caller that arrives meanwhile joins it; a caller that
/// cancels its own wait leaves the grant running. After a failed grant, new attempts are suppressed
/// for 5 seconds from its completion, or 30 seconds after a 429, and callers in that window receive
/// the same safe failure. Grants remain available while requests drain during host shutdown. Once
/// the host has stopped, a pending grant is canceled without publishing anything or starting that
/// window.
/// </para>
/// <para>
/// Every successful grant publishes a new <see cref="ConfigurationServiceTokenLease.Generation" />.
/// A protected read's 401 invalidates only the generation that read sent, so a late rejection never
/// evicts a newer bearer, even one with the same text. An invalidated or expired generation is never
/// returned, including to a caller that joined its grant. If a joined bearer expires before admission
/// and no newer grant or failure has superseded it, it fails through the shared 5-second window
/// instead of starting another grant from that wait. A successful recovery never suppresses
/// recovery for a later call's first rejection; only a failed or unusable acquisition starts the
/// failure window.
/// </para>
/// <para>
/// Every grant failure surfaces as an <see cref="HttpRequestException" /> with fixed text and is
/// logged once here. A non-2xx grant carries its numeric status. Nothing the Configuration Service
/// returns, nothing the transport reports, and neither the credential nor a bearer reaches a log or
/// an exception.
/// </para>
/// </remarks>
public class ConfigurationServiceTokenHandler(
    ConfigurationServiceGrantClient grantClient,
    IOptions<ConfigurationServiceTokenCacheOptions> tokenCacheOptions,
    TimeProvider timeProvider,
    ILogger<ConfigurationServiceTokenHandler> logger,
    IHostApplicationLifetime? hostApplicationLifetime = null
) : IConfigurationServiceTokenHandler
{
    private const string TokenRoute = "connect/token";

    /// <summary>Reuse for a bearer whose grant reports no positive lifetime.</summary>
    internal static readonly TimeSpan FallbackReuse = TimeSpan.FromSeconds(300);

    /// <summary>The most a positive lifetime is shortened by before reuse ends.</summary>
    internal static readonly TimeSpan MaximumReuseMargin = TimeSpan.FromSeconds(60);

    internal static readonly TimeSpan FailedGrantCooldown = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan TooManyRequestsCooldown = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();

    // Guarded by _gate. A grant publishes its outcome to these before it releases its joiners.
    private CachedToken? _current;
    private Task<CachedToken>? _pending;
    private GrantFailure? _failure;
    private long _generation;

    /// <summary>A published lease and its reuse window.</summary>
    private sealed class CachedToken(ConfigurationServiceTokenLease lease, ReuseWindow window)
    {
        public ConfigurationServiceTokenLease Lease { get; } = lease;

        public ReuseWindow Window { get; } = window;
    }

    /// <param name="OriginTimestamp">The <see cref="TimeProvider" /> timestamp the window is measured from.</param>
    /// <param name="Reuse">How long from that timestamp the bearer is reused.</param>
    private readonly record struct ReuseWindow(long OriginTimestamp, TimeSpan Reuse);

    /// <summary>The safe parts of a failed grant, enough to answer callers inside its window.</summary>
    private sealed class GrantFailure(HttpRequestException failure, long completedTimestamp)
    {
        public HttpRequestError HttpRequestError { get; } = failure.HttpRequestError;

        public string Message { get; } = failure.Message;

        public HttpStatusCode? StatusCode { get; } = failure.StatusCode;

        public long CompletedTimestamp { get; } = completedTimestamp;

        public TimeSpan Cooldown { get; } =
            failure.StatusCode is HttpStatusCode.TooManyRequests
                ? TooManyRequestsCooldown
                : FailedGrantCooldown;

        public HttpRequestException ToException() => new(HttpRequestError, Message, inner: null, StatusCode);
    }

    /// <summary>
    /// The whole grant, including the body read after the headers, keeps the 100-second budget the
    /// HTTP client applied before the grant read its response headers-first.
    /// </summary>
    internal static readonly TimeSpan GrantBudget = TimeSpan.FromSeconds(100);

    /// <summary>The optional read of a failed grant's body for its problem type.</summary>
    internal static readonly TimeSpan DiagnosticReadBudget = TimeSpan.FromSeconds(1);

    internal const int DiagnosticBodyLimitBytes = 16 * 1024;

    /// <summary>
    /// The problem types a failed grant may report verbatim. Any other string type is reported as
    /// <see cref="UnrecognizedProblemType" />. Token-limit types come from the shared CMS contract
    /// constants, without depending on the public OAuth reader.
    /// </summary>
    internal const string AboutBlankProblemType = "about:blank";

    internal const string UnrecognizedProblemType = "unrecognized";

    public async Task<ConfigurationServiceTokenLease> GetTokenAsync(
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read before any grant. Hosts without eager options validation, such as the DocumentCache
        // runtime, first meet an invalid cap here: its value-free OptionsValidationException reaches
        // the caller, and no grant is sent or failure window started.
        TimeSpan? cap = tokenCacheOptions.Value.ExpirationCapSeconds is int capSeconds
            ? TimeSpan.FromSeconds(capSeconds)
            : null;

        while (true)
        {
            Task<CachedToken> pending;
            TaskCompletionSource<CachedToken>? startedGrant = null;

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_current is not null && IsReusable(_current))
                {
                    return _current.Lease;
                }

                if (_pending is null)
                {
                    if (
                        _failure is not null
                        && timeProvider.GetElapsedTime(_failure.CompletedTimestamp) < _failure.Cooldown
                    )
                    {
                        throw _failure.ToException();
                    }

                    // Published before any I/O starts, so every later caller joins this grant.
                    startedGrant = new TaskCompletionSource<CachedToken>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    _pending = startedGrant.Task;
                }

                pending = _pending;
            }

            if (startedGrant is not null)
            {
                _ = RunGrantAsync(startedGrant, clientId, clientSecret, scope, cap);
            }

            // The caller's token ends only this caller's wait; the shared grant keeps running.
            CachedToken token = await pending.WaitAsync(cancellationToken);
            HttpRequestException? exhausted = null;

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ReferenceEquals(_current, token) && IsReusable(token))
                {
                    return token.Lease;
                }

                if (ReferenceEquals(_current, token) && _pending is null && _failure is null)
                {
                    // This publication expired while its joiners were waiting to resume. Treat it
                    // like an unusable grant, rather than letting each wait acquire indefinitely.
                    // A newer pending grant or failure owns admission and must remain untouched.
                    exhausted = new HttpRequestException(
                        HttpRequestError.InvalidResponse,
                        "Configuration Service token reuse window ended before the token could be used."
                    );
                    _current = null;
                    _failure = new GrantFailure(exhausted, timeProvider.GetTimestamp());
                }
            }

            if (exhausted is not null)
            {
                try
                {
                    logger.LogError(
                        "Configuration Service token reuse window ended before the token could be used"
                    );
                }
                catch (Exception)
                {
                    // Diagnostics must not replace the safe failure already shared with joiners.
                }

                throw exhausted;
            }

            // A rejection or newer grant superseded the joined publication. Follow its outcome
            // rather than returning an old bearer or overwriting the newer coordinator state.
        }
    }

    public Task<ConfigurationServiceTokenLease> RefreshRejectedTokenAsync(
        ConfigurationServiceTokenLease rejected,
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken = default
    )
    {
        lock (_gate)
        {
            if (_current is not null && _current.Lease.Generation == rejected.Generation)
            {
                _current = null;
            }
        }

        // Invalidation touched only the rejected generation. Admission then follows the normal path:
        // a newer reusable bearer is returned, a pending grant is joined, a failure window answers
        // with its safe failure, and only otherwise does a replacement grant start.
        return GetTokenAsync(clientId, clientSecret, scope, cancellationToken);
    }

    private bool IsReusable(CachedToken token) =>
        timeProvider.GetElapsedTime(token.Window.OriginTimestamp) < token.Window.Reuse;

    /// <summary>
    /// Runs one grant outside the gate and publishes its outcome before completing the task its
    /// joiners wait on. The grant has its own HTTP budget and can run until host draining finishes.
    /// </summary>
    private async Task RunGrantAsync(
        TaskCompletionSource<CachedToken> grant,
        string clientId,
        string clientSecret,
        string scope,
        TimeSpan? cap
    )
    {
        // Everything that can throw runs inside the try, so the joiners' task always completes.
        CancellationToken stopped = CancellationToken.None;

        try
        {
            // ApplicationStopping fires before active requests drain. Those requests may still
            // need a grant or 401 recovery; cancel shared work only after the host has stopped.
            stopped = hostApplicationLifetime?.ApplicationStopped ?? CancellationToken.None;
            long startTimestamp = timeProvider.GetTimestamp();
            (string accessToken, double? expiresIn) = await FetchTokenAsync(
                clientId,
                clientSecret,
                scope,
                stopped
            );
            ReuseWindow window = Publishable(expiresIn, startTimestamp, cap);
            CachedToken token;

            lock (_gate)
            {
                // A new generation for every successful grant, whatever its bearer text.
                token = new CachedToken(
                    new ConfigurationServiceTokenLease(accessToken, ++_generation),
                    window
                );
                _current = token;
                _failure = null;
                _pending = null;
            }

            grant.SetResult(token);
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested)
        {
            lock (_gate)
            {
                _pending = null;
            }

            grant.SetCanceled(stopped);
        }
        catch (Exception exception)
        {
            // FetchTokenAsync reports its failures as safe HttpRequestExceptions; anything else is
            // replaced by a fixed one before it can reach a caller. Logging that replacement must not
            // be able to stop the joiners' task from completing.
            HttpRequestException failure;

            try
            {
                failure = exception as HttpRequestException ?? GrantNotAnswered(HttpRequestError.Unknown);
            }
            catch (Exception)
            {
                failure = new HttpRequestException(
                    HttpRequestError.Unknown,
                    "Configuration Service token request failed before a response was received."
                );
            }

            lock (_gate)
            {
                _failure = new GrantFailure(failure, timeProvider.GetTimestamp());
                _pending = null;
            }

            grant.SetException(failure);
        }
    }

    /// <summary>
    /// Computes the reuse window of a usable grant, or fails it when its lifetime cannot be
    /// represented as a duration or its issuer window closed before the grant completed. The issuer
    /// window runs from the start of the grant. The fallback window and the deprecated cap run from
    /// acquisition, so a slow grant never makes the cap reject a usable bearer.
    /// </summary>
    private ReuseWindow Publishable(double? expiresIn, long startTimestamp, TimeSpan? cap)
    {
        long completedTimestamp = timeProvider.GetTimestamp();

        if (expiresIn is not > 0)
        {
            TimeSpan fallback = Lower(FallbackReuse, cap);

            logger.LogWarning(
                "Configuration Service token response did not include a positive lifetime; the token is reused for at most {ReuseSeconds} seconds",
                fallback.TotalSeconds
            );

            return new ReuseWindow(completedTimestamp, fallback);
        }

        TimeSpan issuerReuse = IssuerReuse(expiresIn.Value) ?? throw GrantUnusable();
        TimeSpan grantDuration = timeProvider.GetElapsedTime(startTimestamp, completedTimestamp);

        if (grantDuration >= issuerReuse)
        {
            throw GrantExhausted();
        }

        // Both windows share the grant's start as their origin, so the cap is extended by the
        // grant's duration to keep it running from acquisition.
        return new ReuseWindow(
            startTimestamp,
            cap is TimeSpan limit ? Lower(issuerReuse, grantDuration + limit) : issuerReuse
        );
    }

    private static TimeSpan Lower(TimeSpan window, TimeSpan? cap) =>
        cap is TimeSpan limit && limit < window ? limit : window;

    /// <summary>
    /// L - min(60 s, 0.1 L) for a positive lifetime L in seconds, floored to whole ticks so a short
    /// lifetime is never rounded up. Null when the result is too large to be a duration.
    /// </summary>
    internal static TimeSpan? IssuerReuse(double lifetimeSeconds)
    {
        double lifetimeTicks = lifetimeSeconds * TimeSpan.TicksPerSecond;
        double marginTicks = Math.Min(MaximumReuseMargin.Ticks, lifetimeTicks / 10);
        double reuseTicks = Math.Floor(lifetimeTicks - marginTicks);

        return reuseTicks < TimeSpan.MaxValue.Ticks ? TimeSpan.FromTicks((long)reuseTicks) : null;
    }

    /// <summary>
    /// Sends one client-credentials grant and returns its bearer and its raw lifetime. The lifetime
    /// is null when the response omits it or sends null; any other lifetime that is not a finite
    /// number fails the grant.
    /// </summary>
    internal async Task<(string AccessToken, double? ExpiresIn)> FetchTokenAsync(
        string clientId,
        string clientSecret,
        string scope,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug("Cache miss - fetching new token from Configuration service");

        using var budget = new CancellationTokenSource(GrantBudget, timeProvider);
        using var grantCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            budget.Token
        );
        CancellationToken grantToken = grantCancellation.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenRoute)
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            ]),
        };

        HttpResponseMessage response;

        try
        {
            response = await grantClient.SendAsync(request, grantToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw GrantTimedOut();
        }
        catch (HttpRequestException transportFailure)
        {
            throw GrantNotAnswered(transportFailure.HttpRequestError);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw GrantNotAnswered(HttpRequestError.Unknown);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status is captured before any body is read, so nothing about the body can
                // lose it.
                HttpStatusCode statusCode = response.StatusCode;
                string? problemType = await ReadProblemTypeAsync(response, grantToken);
                cancellationToken.ThrowIfCancellationRequested();
                throw GrantRejected(statusCode, problemType);
            }

            (string AccessToken, double? ExpiresIn)? grant;

            try
            {
                grant = await ReadGrantAsync(response, grantToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                throw GrantTimedOut();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                grant = null;
            }

            return grant ?? throw GrantUnusable();
        }
    }

    /// <summary>
    /// Reads at most <see cref="DiagnosticBodyLimitBytes" /> of a failed grant's body within
    /// <see cref="DiagnosticReadBudget" /> and classifies its problem-details type. Returns null when
    /// the body is unreadable, too large, not JSON, or has no string type.
    /// </summary>
    private async Task<string?> ReadProblemTypeAsync(
        HttpResponseMessage response,
        CancellationToken grantToken
    )
    {
        using var deadline = new CancellationTokenSource(DiagnosticReadBudget, timeProvider);
        using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            grantToken,
            deadline.Token
        );

        try
        {
            byte[]? body = await ReadBoundedAsync(
                response.Content,
                DiagnosticBodyLimitBytes,
                diagnosticCancellation.Token
            );

            return body is null ? null : ClassifyProblemType(body);
        }
        catch (Exception)
        {
            // Diagnostics are optional and the status is already known, so no failure here - a
            // deadline, a broken stream or an unparsable body - may replace it. The caller checks
            // its own cancellation afterwards.
            return null;
        }
    }

    /// <summary>
    /// Reads the content up to <paramref name="limit" /> bytes, or returns null when it is larger.
    /// Each read is bounded by the token even if the stream ignores it.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken
    )
    {
        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .WaitAsync(cancellationToken);

        byte[] buffer = new byte[limit + 1];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total), cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > limit ? null : buffer[..total];
    }

    private static string? ClassifyProblemType(byte[] body)
    {
        using JsonDocument document = JsonDocument.Parse(body);

        if (
            document.RootElement.ValueKind is not JsonValueKind.Object
            || !document.RootElement.TryGetProperty("type", out JsonElement type)
            || type.ValueKind is not JsonValueKind.String
        )
        {
            return null;
        }

        return type.GetString() switch
        {
            AboutBlankProblemType => AboutBlankProblemType,
            ConfigurationServiceProblemTypes.LegacyTooManyTokens =>
                ConfigurationServiceProblemTypes.LegacyTooManyTokens,
            ConfigurationServiceProblemTypes.ManagementApiTooManyTokens =>
                ConfigurationServiceProblemTypes.ManagementApiTooManyTokens,
            ConfigurationServiceProblemTypes.ManagementApiInternalServerError =>
                ConfigurationServiceProblemTypes.ManagementApiInternalServerError,
            _ => UnrecognizedProblemType,
        };
    }

    /// <summary>
    /// Parses a 2xx grant body. Returns null when it does not carry a bearer that can be sent in an
    /// Authorization header, or carries a lifetime that is present but not a finite number.
    /// </summary>
    private static async Task<(string AccessToken, double? ExpiresIn)?> ReadGrantAsync(
        HttpResponseMessage response,
        CancellationToken grantToken
    )
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(grantToken).WaitAsync(grantToken);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (root.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (
            !TryGetPropertyIgnoringCase(root, "access_token", out JsonElement accessTokenElement)
            || accessTokenElement.ValueKind is not JsonValueKind.String
        )
        {
            return null;
        }

        string accessToken = accessTokenElement.GetString() ?? string.Empty;

        if (!IsUsableBearer(accessToken))
        {
            return null;
        }

        if (
            !TryGetPropertyIgnoringCase(root, "expires_in", out JsonElement expiresInElement)
            || expiresInElement.ValueKind is JsonValueKind.Null
        )
        {
            return (accessToken, null);
        }

        double? expiresIn = expiresInElement.ValueKind switch
        {
            JsonValueKind.Number when expiresInElement.TryGetDouble(out double number) => number,
            JsonValueKind.String
                when double.TryParse(
                    expiresInElement.GetString(),
                    NumberStyles.AllowLeadingSign
                        | NumberStyles.AllowDecimalPoint
                        | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out double quoted
                ) => quoted,
            _ => null,
        };

        return expiresIn is { } value && double.IsFinite(value) ? (accessToken, value) : null;
    }

    /// <summary>
    /// The existing reader matched the envelope's member names case-insensitively.
    /// </summary>
    private static bool TryGetPropertyIgnoringCase(JsonElement element, string name, out JsonElement value)
    {
        // Lazy, so the first match ends the enumeration and nothing is materialized.
        JsonElement? match = element
            .EnumerateObject()
            .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(property => (JsonElement?)property.Value)
            .FirstOrDefault();

        value = match ?? default;
        return match is not null;
    }

    /// <summary>
    /// A bearer is sent as one Authorization header credential, so it must be nonempty visible ASCII
    /// without whitespace or separators that could split or extend the header.
    /// </summary>
    private static bool IsUsableBearer(string accessToken) =>
        accessToken.Length > 0 && accessToken.All(character => character is > ' ' and <= '~');

    private HttpRequestException GrantRejected(HttpStatusCode statusCode, string? problemType)
    {
        int numericStatus = (int)statusCode;
        string message;

        // The problem type is an allowlisted value or the fixed unrecognized marker, never text taken
        // from the response, so the log and the exception may both carry it.
        if (problemType is null)
        {
            logger.LogError(
                "Configuration Service token request failed with status code {StatusCode}",
                numericStatus
            );
            message = $"Configuration Service token request failed with status code {numericStatus}.";
        }
        else
        {
            logger.LogError(
                "Configuration Service token request failed with status code {StatusCode} and problem type {ProblemType}",
                numericStatus,
                problemType
            );
            message =
                $"Configuration Service token request failed with status code {numericStatus} and problem type {problemType}.";
        }

        return new HttpRequestException(HttpRequestError.Unknown, message, inner: null, statusCode);
    }

    private HttpRequestException GrantNotAnswered(HttpRequestError httpRequestError)
    {
        logger.LogError(
            "Configuration Service token request failed before a response was received ({HttpRequestError})",
            httpRequestError
        );

        return new HttpRequestException(
            httpRequestError,
            "Configuration Service token request failed before a response was received."
        );
    }

    private HttpRequestException GrantTimedOut()
    {
        logger.LogError(
            "Configuration Service token request did not complete within {GrantBudgetSeconds} seconds",
            GrantBudget.TotalSeconds
        );

        return new HttpRequestException(
            HttpRequestError.Unknown,
            "Configuration Service token request timed out."
        );
    }

    private HttpRequestException GrantUnusable()
    {
        logger.LogError("Configuration Service token response was not usable");

        return new HttpRequestException(
            HttpRequestError.InvalidResponse,
            "Configuration Service token response was not usable."
        );
    }

    private HttpRequestException GrantExhausted()
    {
        logger.LogError("Configuration Service token reuse window ended before the token request completed");

        return new HttpRequestException(
            HttpRequestError.InvalidResponse,
            "Configuration Service token reuse window ended before the token request completed."
        );
    }
}
