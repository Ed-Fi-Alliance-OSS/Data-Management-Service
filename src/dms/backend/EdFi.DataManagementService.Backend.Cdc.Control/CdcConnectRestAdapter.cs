// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Outcome of one Kafka Connect REST operation. Every outcome other than
/// <see cref="Succeeded"/> is fail-closed evidence: the caller treats it as an absent observation and
/// never as an implicit pass. None of them is terminal — each names a condition the operator or a
/// later step can address before the operation is issued again.
/// </summary>
public enum CdcConnectOutcome
{
    Succeeded,

    /// <summary>The worker answered 404: the connector, or its committed offsets, does not exist.</summary>
    NotFound,

    /// <summary>
    /// The worker answered 409: a rebalance is in progress, or the connector is not in the state the
    /// operation requires.
    /// </summary>
    Conflict,

    /// <summary>The worker refused the request as made — any other client error.</summary>
    Rejected,

    /// <summary>The worker could not be reached, timed out, or answered a server error.</summary>
    Unavailable,

    /// <summary>The worker answered successfully with a body that is not the documented shape.</summary>
    MalformedResponse,
}

/// <summary>
/// Bounded failure evidence for one Connect operation. <see cref="Summary"/> is composed by the
/// adapter from the request it issued and the status it received; a Connect response body never
/// reaches it, because worker error bodies quote submitted connector configuration and task stack
/// traces.
/// </summary>
public sealed record CdcConnectFailure(int? StatusCode, string Summary, bool Retryable);

/// <summary>Result of a Connect operation that carries no payload of its own.</summary>
public sealed record CdcConnectResult(CdcConnectOutcome Outcome, CdcConnectFailure? Failure)
{
    public bool Succeeded => Outcome == CdcConnectOutcome.Succeeded;
}

/// <summary>Result of a Connect operation that reads evidence back from the worker.</summary>
public sealed record CdcConnectResult<TValue>(
    CdcConnectOutcome Outcome,
    TValue? Value,
    CdcConnectFailure? Failure
)
    where TValue : class
{
    public bool Succeeded => Outcome == CdcConnectOutcome.Succeeded && Value is not null;
}

/// <summary>
/// The plugin-side validation verdict for a rendered connector configuration. Only the error count
/// and the offending property names cross this boundary: Connect echoes the submitted value back in
/// each validation message, so the messages themselves stay inside the worker.
/// </summary>
public sealed record CdcConnectConfigValidation(int ErrorCount, IReadOnlyList<string> ErrorPropertyNames);

/// <summary>Connector and task state as the worker reports it.</summary>
public sealed record CdcConnectorStatus(string ConnectorState, IReadOnlyList<CdcConnectorTaskStatus> Tasks);

/// <summary>
/// One task's reported state. <see cref="ErrorCategory"/> is the leading exception type of the task
/// trace, never the trace itself.
/// </summary>
public sealed record CdcConnectorTaskStatus(int? Id, string State, string? ErrorCategory);

/// <summary>Committed source offsets as the worker reports them, in the order it returned them.</summary>
public sealed record CdcConnectorOffsets(IReadOnlyList<CdcConnectorOffsetEntry> Entries);

/// <summary>
/// One committed source-offset entry. Both elements are detached copies, so they outlive the parsed
/// response document. Interpreting them is the offset observation's work, not the adapter's.
/// </summary>
public sealed record CdcConnectorOffsetEntry(JsonElement Partition, JsonElement Offset);

/// <summary>
/// The Kafka Connect REST operations the CDC control plane issues. Every method reports a
/// fail-closed <see cref="CdcConnectOutcome"/> rather than throwing on a worker failure, and no
/// method carries a response body out of the adapter.
/// </summary>
public interface ICdcConnectClient
{
    /// <summary>
    /// Asks the worker to validate a rendered configuration against the connector plugin before the
    /// connector is registered.
    /// </summary>
    Task<CdcConnectResult<CdcConnectConfigValidation>> ValidateConnectorPluginConfigAsync(
        string connectorClass,
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken
    );

    /// <summary>Registers the connector, or updates the configuration of an existing one.</summary>
    Task<CdcConnectResult> PutConnectorConfigAsync(
        string connectorName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken
    );

    /// <summary>Reads the connector's live configuration back for read-back validation.</summary>
    Task<CdcConnectResult<IReadOnlyDictionary<string, string>>> GetConnectorConfigAsync(
        string connectorName,
        CancellationToken cancellationToken
    );

    /// <summary>Reads the connector's runtime state and the state of each of its tasks.</summary>
    Task<CdcConnectResult<CdcConnectorStatus>> GetConnectorStatusAsync(
        string connectorName,
        CancellationToken cancellationToken
    );

    /// <summary>Restarts the connector together with its tasks, whether or not they have failed.</summary>
    /// <remarks>
    /// This does not clear a <c>STOPPED</c> or <c>PAUSED</c> target state. Those are set by the worker
    /// rather than by a task failure, and a restart re-creates connector and task instances without
    /// changing them — a stopped connector has no tasks to restart at all. Use
    /// <see cref="ResumeConnectorAsync"/> for a connector the worker is holding fenced.
    /// </remarks>
    Task<CdcConnectResult> RestartConnectorAsync(string connectorName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a connector the worker is holding <c>STOPPED</c> or <c>PAUSED</c> to its running target
    /// state. This is the only operation that clears either one.
    /// </summary>
    /// <remarks>
    /// The worker applies the resume asynchronously and this does not wait for it, unlike
    /// <see cref="StopConnectorAsync"/>. Nothing here depends on the connector having reached a state
    /// first — the stop wait exists because an offsets deletion is accepted only for an already stopped
    /// connector — and the caller re-reads the runtime afterwards, so what it reports is what the worker
    /// had actually reached rather than what the resume asked for.
    /// </remarks>
    Task<CdcConnectResult> ResumeConnectorAsync(string connectorName, CancellationToken cancellationToken);

    /// <summary>
    /// Fences the connector so it commits no further offsets. This is a precondition of deleting its
    /// committed offsets, and it is how a source replacement stops the outgoing generation.
    /// </summary>
    Task<CdcConnectResult> StopConnectorAsync(string connectorName, CancellationToken cancellationToken);

    /// <summary>Reads the connector's committed source offsets from the shared offset store.</summary>
    Task<CdcConnectResult<CdcConnectorOffsets>> GetConnectorOffsetsAsync(
        string connectorName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes the connector's committed offsets. The worker accepts this only for a connector that
    /// exists and is stopped, and deleting the connector configuration does not remove them, so this
    /// runs between <see cref="StopConnectorAsync"/> and <see cref="DeleteConnectorAsync"/>.
    /// </summary>
    Task<CdcConnectResult> DeleteConnectorOffsetsAsync(
        string connectorName,
        CancellationToken cancellationToken
    );

    /// <summary>Deletes the connector configuration.</summary>
    Task<CdcConnectResult> DeleteConnectorAsync(string connectorName, CancellationToken cancellationToken);
}

internal sealed class CdcConnectRestAdapter(
    IHttpClientFactory httpClientFactory,
    IOptions<CdcControlOptions> options,
    TimeProvider timeProvider,
    ILogger<CdcConnectRestAdapter> logger
) : ICdcConnectClient
{
    /// <summary>Named client the deployment configures transport and handler policy on.</summary>
    internal const string HttpClientName = "dms-cdc-connect";

    /// <summary>
    /// Reported when a task trace does not open with a recognizable exception type. The trace itself
    /// is never substituted for the category.
    /// </summary>
    internal const string UnclassifiedErrorCategory = "unclassified";

    private const int MaximumErrorCategoryLength = 128;

    /// <summary>The connector state the worker reports once a stop has been applied.</summary>
    private const string StoppedConnectorState = "STOPPED";

    public async Task<CdcConnectResult<CdcConnectConfigValidation>> ValidateConnectorPluginConfigAsync(
        string connectorClass,
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorClass);
        ArgumentNullException.ThrowIfNull(config);

        // The worker validates a configuration it is given, so this is a PUT with the rendered
        // configuration as its body rather than a plain read.
        CdcConnectResponse response = await SendAsync(
            HttpMethod.Put,
            $"connector-plugins/{Uri.EscapeDataString(connectorClass)}/config/validate",
            JsonSerializer.Serialize(config),
            "connector plugin config validation",
            cancellationToken
        );

        return ToResult(response, ParseConfigValidation, "connector plugin config validation");
    }

    public async Task<CdcConnectResult> PutConnectorConfigAsync(
        string connectorName,
        IReadOnlyDictionary<string, string> config,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        ArgumentNullException.ThrowIfNull(config);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Put,
            $"{ConnectorPath(connectorName)}/config",
            JsonSerializer.Serialize(config),
            "connector registration",
            cancellationToken
        );

        return ToResult(response);
    }

    public async Task<CdcConnectResult<IReadOnlyDictionary<string, string>>> GetConnectorConfigAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Get,
            $"{ConnectorPath(connectorName)}/config",
            null,
            "connector config read-back",
            cancellationToken
        );

        return ToResult(response, ParseConfigMap, "connector config read-back");
    }

    public async Task<CdcConnectResult<CdcConnectorStatus>> GetConnectorStatusAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Get,
            $"{ConnectorPath(connectorName)}/status",
            null,
            "connector status",
            cancellationToken
        );

        return ToResult(response, ParseStatus, "connector status");
    }

    public async Task<CdcConnectResult> RestartConnectorAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Post,
            $"{ConnectorPath(connectorName)}/restart?includeTasks=true&onlyFailed=false",
            null,
            "connector restart",
            cancellationToken
        );

        return ToResult(response);
    }

    public async Task<CdcConnectResult> ResumeConnectorAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Put,
            $"{ConnectorPath(connectorName)}/resume",
            null,
            "connector resume",
            cancellationToken
        );

        return ToResult(response);
    }

    /// <summary>
    /// Stops the connector and waits until the worker reports it observably stopped.
    /// </summary>
    /// <remarks>
    /// Connect answers the stop before the herder has finished applying it, and it accepts an offsets
    /// deletion only for a connector already in <c>STOPPED</c>. Waiting is what turns the fence from a
    /// request into a fact: without it a retirement that races a rebalance is refused for a state it
    /// asked for and would have reached moments later. A connector that never settles is reported
    /// unavailable and retryable — never as stopped, because elapsed time is not the worker's answer.
    /// </remarks>
    public async Task<CdcConnectResult> StopConnectorAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Put,
            $"{ConnectorPath(connectorName)}/stop",
            null,
            "connector stop",
            cancellationToken
        );

        return response.Outcome == CdcConnectOutcome.Succeeded
            ? await AwaitStoppedAsync(connectorName, cancellationToken)
            : ToResult(response);
    }

    /// <summary>
    /// Reads the connector's state back until the worker reports it stopped, bounded by the elapsed
    /// Connect request timeout. A read the worker does not answer ends the wait on its own outcome
    /// rather than being retried into the budget.
    /// </summary>
    /// <remarks>
    /// The bound is a deadline rather than a read count: each state read carries its own request
    /// timeout, so a worker that accepts the stop and then answers slowly would spend that timeout on
    /// every one of a counted number of reads and take many multiples of the budget the exhaustion
    /// message names.
    /// </remarks>
    private async Task<CdcConnectResult> AwaitStoppedAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        CdcControlOptions controlOptions = options.Value;
        TimeSpan budget = controlOptions.Timeouts.ConnectRequest;
        TimeSpan pollInterval = controlOptions.Timeouts.PollInterval;
        DateTimeOffset deadline = timeProvider.GetUtcNow() + budget;

        while (true)
        {
            CdcConnectResult<CdcConnectorStatus> status = await GetConnectorStatusAsync(
                connectorName,
                cancellationToken
            );
            if (!status.Succeeded)
            {
                return new(status.Outcome, status.Failure);
            }

            if (
                string.Equals(
                    status.Value!.ConnectorState,
                    StoppedConnectorState,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return new(CdcConnectOutcome.Succeeded, null);
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                return new(
                    CdcConnectOutcome.Unavailable,
                    new(
                        null,
                        "Kafka Connect accepted the connector stop, but the connector did not reach "
                            + $"{StoppedConnectorState} within "
                            + $"{budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.",
                        Retryable: true
                    )
                );
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken);
        }
    }

    public async Task<CdcConnectResult<CdcConnectorOffsets>> GetConnectorOffsetsAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Get,
            $"{ConnectorPath(connectorName)}/offsets",
            null,
            "committed offset read",
            cancellationToken
        );

        return ToResult(response, ParseOffsets, "committed offset read");
    }

    public async Task<CdcConnectResult> DeleteConnectorOffsetsAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Delete,
            $"{ConnectorPath(connectorName)}/offsets",
            null,
            "committed offset deletion",
            cancellationToken
        );

        // The worker accepts this only for a connector that exists and is stopped, and reports a
        // connector that is still running as an ordinary client error. Both statuses therefore report
        // the same fail-closed condition, so the caller fences the connector rather than retrying the
        // deletion against a running one.
        if (
            response.Failure is { StatusCode: (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Conflict }
        )
        {
            return new(
                CdcConnectOutcome.Conflict,
                new(
                    response.Failure.StatusCode,
                    "Kafka Connect refused the committed offset deletion: the connector must exist and be "
                        + "STOPPED before its offsets are deleted.",
                    Retryable: true
                )
            );
        }

        return ToResult(response);
    }

    public async Task<CdcConnectResult> DeleteConnectorAsync(
        string connectorName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);

        CdcConnectResponse response = await SendAsync(
            HttpMethod.Delete,
            ConnectorPath(connectorName),
            null,
            "connector deletion",
            cancellationToken
        );

        return ToResult(response);
    }

    private async Task<CdcConnectResponse> SendAsync(
        HttpMethod method,
        string relativeUri,
        string? jsonBody,
        string operation,
        CancellationToken cancellationToken
    )
    {
        CdcControlOptions controlOptions = options.Value;
        TimeSpan timeout = controlOptions.Timeouts.ConnectRequest;

        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        requestCancellation.CancelAfter(timeout);

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(method, RequestUri(controlOptions, relativeUri));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, requestCancellation.Token);

            // Only the status is logged. A worker error body quotes the submitted configuration.
            logger.LogDebug(
                "Kafka Connect {ConnectOperation} answered {StatusCode}.",
                operation,
                (int)response.StatusCode
            );

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    ToFailedOutcome(response.StatusCode),
                    string.Empty,
                    StatusFailure(operation, response.StatusCode)
                );
            }

            string body = await response.Content.ReadAsStringAsync(requestCancellation.Token);
            return new(CdcConnectOutcome.Succeeded, body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                CdcConnectOutcome.Unavailable,
                string.Empty,
                new(
                    null,
                    $"Kafka Connect {operation} did not answer within "
                        + $"{timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.",
                    Retryable: true
                )
            );
        }
        catch (HttpRequestException exception)
        {
            return new(
                CdcConnectOutcome.Unavailable,
                string.Empty,
                new(
                    exception.StatusCode is { } statusCode ? (int)statusCode : null,
                    $"Kafka Connect {operation} could not reach the worker: {exception.HttpRequestError}.",
                    Retryable: true
                )
            );
        }
    }

    private static CdcConnectResult ToResult(CdcConnectResponse response) =>
        new(response.Outcome, response.Failure);

    private static CdcConnectResult<TValue> ToResult<TValue>(
        CdcConnectResponse response,
        Func<string, TValue?> parse,
        string operation
    )
        where TValue : class
    {
        if (response.Outcome != CdcConnectOutcome.Succeeded)
        {
            return new(response.Outcome, null, response.Failure);
        }

        TValue? value;
        try
        {
            value = parse(response.Body);
        }
        catch (JsonException)
        {
            value = null;
        }

        if (value is null)
        {
            return new(
                CdcConnectOutcome.MalformedResponse,
                null,
                new(
                    (int)HttpStatusCode.OK,
                    $"Kafka Connect {operation} answered with a body that is not the documented shape.",
                    Retryable: false
                )
            );
        }

        return new(CdcConnectOutcome.Succeeded, value, null);
    }

    private static CdcConnectOutcome ToFailedOutcome(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.NotFound => CdcConnectOutcome.NotFound,
            HttpStatusCode.Conflict => CdcConnectOutcome.Conflict,
            _ => (int)statusCode >= 500 ? CdcConnectOutcome.Unavailable : CdcConnectOutcome.Rejected,
        };

    private static CdcConnectFailure StatusFailure(string operation, HttpStatusCode statusCode) =>
        new(
            (int)statusCode,
            $"Kafka Connect {operation} answered "
                + $"{((int)statusCode).ToString(CultureInfo.InvariantCulture)}.",
            Retryable: statusCode == HttpStatusCode.Conflict || (int)statusCode >= 500
        );

    private static string ConnectorPath(string connectorName) =>
        $"connectors/{Uri.EscapeDataString(connectorName)}";

    private static Uri RequestUri(CdcControlOptions options, string relativeUri)
    {
        string baseUri = options.ConnectBaseUri;
        if (!baseUri.EndsWith('/'))
        {
            baseUri += "/";
        }

        return new(new Uri(baseUri, UriKind.Absolute), relativeUri);
    }

    private static IReadOnlyDictionary<string, string>? ParseConfigMap(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, string> config = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            config[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return config;
    }

    private static CdcConnectorStatus? ParseStatus(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("connector", out JsonElement connector)
            || connector.ValueKind != JsonValueKind.Object
            || !connector.TryGetProperty("state", out JsonElement connectorState)
            || connectorState.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        List<CdcConnectorTaskStatus> tasks = [];
        if (root.TryGetProperty("tasks", out JsonElement taskElements))
        {
            if (taskElements.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement task in taskElements.EnumerateArray())
            {
                if (ParseTaskStatus(task) is not { } taskStatus)
                {
                    return null;
                }

                tasks.Add(taskStatus);
            }
        }

        return new(connectorState.GetString() ?? string.Empty, tasks);
    }

    private static CdcConnectorTaskStatus? ParseTaskStatus(JsonElement task)
    {
        if (
            task.ValueKind != JsonValueKind.Object
            || !task.TryGetProperty("state", out JsonElement state)
            || state.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        int? id =
            task.TryGetProperty("id", out JsonElement idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt32(out int parsedId)
                ? parsedId
                : null;

        return new(id, state.GetString() ?? string.Empty, ToErrorCategory(task));
    }

    /// <summary>
    /// Reduces a task trace to the exception type that opens it. The remainder of a Connect trace is
    /// an unbounded message and stack that can quote connector configuration, so only this leading
    /// token crosses the adapter boundary; a trace shaped any other way reports
    /// <see cref="UnclassifiedErrorCategory"/> rather than a fragment of itself.
    /// </summary>
    private static string? ToErrorCategory(JsonElement task)
    {
        if (
            !task.TryGetProperty("trace", out JsonElement trace)
            || trace.ValueKind != JsonValueKind.String
            || trace.GetString() is not { } value
            || string.IsNullOrWhiteSpace(value)
        )
        {
            return null;
        }

        ReadOnlySpan<char> firstLine = value.AsSpan();
        int lineBreak = firstLine.IndexOfAny('\r', '\n');
        if (lineBreak >= 0)
        {
            firstLine = firstLine[..lineBreak];
        }

        firstLine = firstLine.Trim();
        int messageStart = firstLine.IndexOf(':');
        ReadOnlySpan<char> token = messageStart >= 0 ? firstLine[..messageStart] : firstLine;

        return IsQualifiedTypeName(token) ? token.ToString() : UnclassifiedErrorCategory;
    }

    /// <summary>
    /// A Java trace opens with a package-qualified exception type. Requiring the qualifying dot keeps
    /// a trace written as prose from passing its first word off as a category.
    /// </summary>
    private static bool IsQualifiedTypeName(ReadOnlySpan<char> token)
    {
        if (
            token.Length == 0
            || token.Length > MaximumErrorCategoryLength
            || !char.IsLetter(token[0])
            || !token.Contains('.')
        )
        {
            return false;
        }

        foreach (char character in token)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '$' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static CdcConnectorOffsets? ParseOffsets(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("offsets", out JsonElement offsets)
            || offsets.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }

        List<CdcConnectorOffsetEntry> entries = [];
        foreach (JsonElement entry in offsets.EnumerateArray())
        {
            if (
                entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("partition", out JsonElement partition)
                || !entry.TryGetProperty("offset", out JsonElement offset)
            )
            {
                return null;
            }

            entries.Add(new(partition.Clone(), offset.Clone()));
        }

        return new(entries);
    }

    private static CdcConnectConfigValidation? ParseConfigValidation(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("error_count", out JsonElement errorCountElement)
            || errorCountElement.ValueKind != JsonValueKind.Number
            || !errorCountElement.TryGetInt32(out int errorCount)
        )
        {
            return null;
        }

        List<string> errorPropertyNames = [];
        if (
            root.TryGetProperty("configs", out JsonElement configs)
            && configs.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement config in configs.EnumerateArray())
            {
                if (ErrorPropertyName(config) is { } propertyName)
                {
                    errorPropertyNames.Add(propertyName);
                }
            }
        }

        return new(errorCount, errorPropertyNames);
    }

    private static string? ErrorPropertyName(JsonElement config)
    {
        if (
            config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("errors", out JsonElement errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0
            || !value.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
        )
        {
            return null;
        }

        // Only the property name is reported: each validation message repeats the submitted value.
        return name.GetString() is { Length: > 0 } propertyName ? propertyName : null;
    }

    /// <summary>
    /// One completed exchange. <see cref="Body"/> is populated only for a successful response, so a
    /// failure body has no path out of <see cref="SendAsync"/>.
    /// </summary>
    private readonly record struct CdcConnectResponse(
        CdcConnectOutcome Outcome,
        string Body,
        CdcConnectFailure? Failure
    );
}
