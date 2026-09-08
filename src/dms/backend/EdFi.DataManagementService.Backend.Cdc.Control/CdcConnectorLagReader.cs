// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Outcome of one attempt to read the connector's source-lag metrics. Every outcome other than
/// <see cref="Succeeded"/> is absent evidence rather than a lag verdict, and each keeps readiness
/// false instead of passing.
/// </summary>
public enum CdcConnectorLagReadOutcome
{
    Succeeded,

    /// <summary>
    /// The bridge answered, but the connector's streaming metrics — or one of the four attributes the
    /// observation requires — are not reported.
    /// </summary>
    MetricsAbsent,

    /// <summary>The bridge could not be reached, timed out, or answered a failure.</summary>
    Unavailable,

    /// <summary>The bridge answered successfully with a body that is not the documented shape.</summary>
    MalformedResponse,
}

/// <summary>
/// One complete source-lag reading. The shared observation admits a lag verdict only when the current
/// value and all three quantiles are present, so a reading is only ever composed from all four.
/// </summary>
public sealed record CdcConnectorLagReading(
    long CurrentMilliseconds,
    long P50Milliseconds,
    long P95Milliseconds,
    long P99Milliseconds
);

/// <summary>
/// Result of one lag read. <see cref="Summary"/> is composed from the request that was issued and the
/// status it received; a bridge response body never reaches it.
/// </summary>
public sealed record CdcConnectorLagReadResult(
    CdcConnectorLagReadOutcome Outcome,
    CdcConnectorLagReading? Reading,
    string? Summary
)
{
    public bool Succeeded => Outcome == CdcConnectorLagReadOutcome.Succeeded && Reading is not null;
}

/// <summary>
/// Reads Debezium's <c>MilliSecondsBehindSource</c> current value and its P50/P95/P99 quantiles from
/// the Connect worker.
/// </summary>
/// <remarks>
/// The transport is a seam so the observation mapping stays independent of it and testable without
/// HTTP. The progress topic is never a substitute for these metrics: it is connector-internal state
/// carrying no quantiles, and the shared observation requires every quantile whenever the lag state
/// is anything other than unknown.
/// </remarks>
public interface ICdcConnectorLagReader
{
    /// <param name="topicPrefix">
    /// The connector's <c>topic.prefix</c>, which is the value Debezium publishes its metrics under.
    /// </param>
    Task<CdcConnectorLagReadResult> ReadAsync(
        CdcProvider provider,
        string topicPrefix,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Reads the Debezium streaming metrics over the Jolokia JMX-to-HTTP bridge the Connect image
/// inherits from its Debezium base, which <c>ENABLE_JOLOKIA=true</c> activates on port 8778.
/// </summary>
internal sealed class CdcConnectorJolokiaLagReader(
    IHttpClientFactory httpClientFactory,
    IOptions<CdcControlOptions> options,
    ILogger<CdcConnectorJolokiaLagReader> logger
) : ICdcConnectorLagReader
{
    /// <summary>Named client the deployment configures transport and handler policy on.</summary>
    internal const string HttpClientName = "dms-cdc-connect-metrics";

    /// <summary>
    /// The Debezium entrypoint hardcodes the Jolokia agent's port, so it is a property of the image
    /// rather than a deployment choice.
    /// </summary>
    internal const int JolokiaPort = 8778;

    internal const string CurrentLagAttributeName = "MilliSecondsBehindSource";
    internal const string P50LagAttributeName = "MilliSecondsBehindSourceP50";
    internal const string P95LagAttributeName = "MilliSecondsBehindSourceP95";
    internal const string P99LagAttributeName = "MilliSecondsBehindSourceP99";

    private const int JolokiaSuccessStatus = 200;
    private const int JolokiaNotFoundStatus = 404;

    private static readonly string[] LagAttributeNames =
    [
        CurrentLagAttributeName,
        P50LagAttributeName,
        P95LagAttributeName,
        P99LagAttributeName,
    ];

    public async Task<CdcConnectorLagReadResult> ReadAsync(
        CdcProvider provider,
        string topicPrefix,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicPrefix);

        CdcControlOptions controlOptions = options.Value;
        TimeSpan timeout = controlOptions.Timeouts.ConnectRequest;

        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        requestCancellation.CancelAfter(timeout);

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            RequestUri(controlOptions, provider, topicPrefix)
        );

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, requestCancellation.Token);

            // Only the status is logged. A bridge error body carries a JMX stack trace.
            logger.LogDebug(
                "Debezium source lag metrics read answered {StatusCode}.",
                (int)response.StatusCode
            );

            if (!response.IsSuccessStatusCode)
            {
                return Failed(
                    CdcConnectorLagReadOutcome.Unavailable,
                    "The Debezium metrics bridge answered "
                        + $"{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}."
                );
            }

            return ParseReading(await response.Content.ReadAsStringAsync(requestCancellation.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                CdcConnectorLagReadOutcome.Unavailable,
                "The Debezium metrics bridge did not answer within "
                    + $"{timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."
            );
        }
        catch (HttpRequestException exception)
        {
            return Failed(
                CdcConnectorLagReadOutcome.Unavailable,
                $"The Debezium metrics bridge could not be reached: {exception.HttpRequestError}."
            );
        }
    }

    /// <summary>
    /// Resolves the bridge the metrics are read from. The Jolokia port is fixed by the image
    /// entrypoint, so the bridge is the Connect worker's own host on that port unless the deployment
    /// published it somewhere else.
    /// </summary>
    internal static Uri MetricsBaseUri(CdcControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConnectMetricsBaseUri))
        {
            Uri configured = new(options.ConnectMetricsBaseUri, UriKind.Absolute);
            return configured.AbsoluteUri.EndsWith('/') ? configured : new($"{configured.AbsoluteUri}/");
        }

        Uri connectBaseUri = new(options.ConnectBaseUri, UriKind.Absolute);
        return new UriBuilder(connectBaseUri.Scheme, connectBaseUri.Host, JolokiaPort) { Path = "/" }.Uri;
    }

    /// <summary>
    /// The object-name pattern the connector's streaming metrics are registered under. Debezium names
    /// them <c>debezium.&lt;connector&gt;:type=connector-metrics,context=streaming,server=&lt;topic.prefix&gt;</c>,
    /// and individual providers add further key properties of their own — SQL Server reports a task
    /// key. Reading through a property-list pattern therefore matches the connector's MBean without
    /// asserting an exact object name that varies by provider.
    /// </summary>
    internal static string MetricsObjectNamePattern(CdcProvider provider, string topicPrefix) =>
        $"debezium.{ProviderDomainToken(provider)}:type=connector-metrics,context=streaming,"
        + $"server={topicPrefix},*";

    private static string ProviderDomainToken(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "postgres",
            CdcProvider.SqlServer => "sql_server",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static Uri RequestUri(CdcControlOptions options, CdcProvider provider, string topicPrefix)
    {
        string objectName = Uri.EscapeDataString(MetricsObjectNamePattern(provider, topicPrefix));

        return new(
            MetricsBaseUri(options),
            $"jolokia/read/{objectName}/{string.Join(',', LagAttributeNames)}"
        );
    }

    private static CdcConnectorLagReadResult ParseReading(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Malformed();
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Malformed();
            }

            // The bridge answers 200 at the transport layer and reports the JMX outcome in the body,
            // so an absent MBean arrives as a successful response carrying a 404 status.
            if (ReadInt32(root, "status") is not { } status)
            {
                return Malformed();
            }

            if (status == JolokiaNotFoundStatus)
            {
                return Absent(
                    "The connector's Debezium streaming metrics are not registered on the Connect worker."
                );
            }

            if (status != JolokiaSuccessStatus)
            {
                return Failed(
                    CdcConnectorLagReadOutcome.Unavailable,
                    "The Debezium metrics bridge reported status "
                        + $"{status.ToString(CultureInfo.InvariantCulture)}."
                );
            }

            if (
                !root.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.Object
            )
            {
                return Malformed();
            }

            return SelectAttributes(value, out JsonElement attributes)
                ? ToReading(attributes)
                : AttributeSelectionFailure(value);
        }
    }

    /// <summary>
    /// A pattern read answers with one entry per matching MBean, and a read that resolved to a single
    /// MBean answers with the attributes themselves. Exactly one MBean may match: a topic prefix that
    /// matched several is ambiguous evidence rather than this connector's lag.
    /// </summary>
    private static bool SelectAttributes(JsonElement value, out JsonElement attributes)
    {
        attributes = value;

        int propertyCount = 0;
        JsonElement first = default;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (propertyCount == 0)
            {
                first = property.Value;
            }

            propertyCount++;
        }

        if (propertyCount == 0)
        {
            return false;
        }

        if (first.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (propertyCount > 1)
        {
            return false;
        }

        attributes = first;
        return true;
    }

    private static CdcConnectorLagReadResult AttributeSelectionFailure(JsonElement value) =>
        value.EnumerateObject().Any()
            ? Absent("More than one Debezium streaming metrics MBean matched the connector's topic prefix.")
            : Absent("No Debezium streaming metrics MBean matched the connector's topic prefix.");

    private static CdcConnectorLagReadResult ToReading(JsonElement attributes)
    {
        long[] values = new long[LagAttributeNames.Length];
        for (int index = 0; index < LagAttributeNames.Length; index++)
        {
            if (ReadReportedMilliseconds(attributes, LagAttributeNames[index]) is not { } value)
            {
                return Absent($"The Debezium streaming metric {LagAttributeNames[index]} was not reported.");
            }

            values[index] = value;
        }

        return new(
            CdcConnectorLagReadOutcome.Succeeded,
            new(values[0], values[1], values[2], values[3]),
            null
        );
    }

    /// <summary>
    /// Reads one reported metric. Debezium reports the current value as a whole number of
    /// milliseconds and the quantiles as fractional ones, and reports a metric it has no measurement
    /// for as a negative sentinel — which is absent evidence, not a lag of zero.
    /// </summary>
    private static long? ReadReportedMilliseconds(JsonElement attributes, string attributeName)
    {
        if (
            !attributes.TryGetProperty(attributeName, out JsonElement attribute)
            || ToMilliseconds(attribute) is not { } milliseconds
            || milliseconds < 0
        )
        {
            return null;
        }

        return milliseconds;
    }

    private static long? ToMilliseconds(JsonElement attribute)
    {
        if (attribute.ValueKind == JsonValueKind.Number)
        {
            return attribute.TryGetInt64(out long value) ? value : Round(attribute.GetDouble());
        }

        if (attribute.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return double.TryParse(
            attribute.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsedValue
        )
            ? Round(parsedValue)
            : null;
    }

    private static long? Round(double value) =>
        double.IsFinite(value) && value >= long.MinValue && value <= long.MaxValue
            ? (long)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out int value)
            ? value
            : null;

    private static CdcConnectorLagReadResult Failed(CdcConnectorLagReadOutcome outcome, string summary) =>
        new(outcome, null, summary);

    private static CdcConnectorLagReadResult Absent(string summary) =>
        Failed(CdcConnectorLagReadOutcome.MetricsAbsent, summary);

    private static CdcConnectorLagReadResult Malformed() =>
        Failed(
            CdcConnectorLagReadOutcome.MalformedResponse,
            "The Debezium metrics bridge answered with a body that is not the documented shape."
        );
}

/// <summary>
/// Maps a source-lag reading onto the shared lag observation. The mapping is transport-independent:
/// it consumes a reading from any <see cref="ICdcConnectorLagReader"/> and never reaches for evidence
/// of its own.
/// </summary>
public static class CdcConnectorLagObservationMapper
{
    /// <summary>
    /// Composes the observation and validates it before returning. Absent, unusable, or internally
    /// inconsistent evidence reports <see cref="CdcConnectorLagState.Unknown"/> with null values —
    /// contract-legal, and the correct fail-closed degradation, because an unknown lag state keeps
    /// combined readiness false. A quantile is never synthesized to make readiness pass.
    /// </summary>
    public static CdcConnectorLagObservation Map(
        CdcObservationContext context,
        CdcConnectorLagReadResult reading,
        TimeSpan lagThreshold,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reading);

        if (!reading.Succeeded || reading.Reading is not { } observed)
        {
            return Complete(
                context,
                observedAt,
                CdcConnectorLagState.Unknown,
                null,
                null,
                [EvidenceUnavailable(reading.Outcome, observedAt)]
            );
        }

        if (
            observed.CurrentMilliseconds < 0
            || observed.P50Milliseconds < 0
            || observed.P95Milliseconds < 0
            || observed.P99Milliseconds < 0
        )
        {
            return Complete(
                context,
                observedAt,
                CdcConnectorLagState.Unknown,
                null,
                null,
                [UnusableReading(observedAt)]
            );
        }

        if (
            observed.P50Milliseconds > observed.P95Milliseconds
            || observed.P95Milliseconds > observed.P99Milliseconds
        )
        {
            return Complete(
                context,
                observedAt,
                CdcConnectorLagState.Unknown,
                null,
                null,
                [QuantilesOutOfOrder(observed, observedAt)]
            );
        }

        long thresholdMilliseconds = ThresholdMilliseconds(lagThreshold);
        bool exceeded = observed.CurrentMilliseconds > thresholdMilliseconds;

        return Complete(
            context,
            observedAt,
            exceeded ? CdcConnectorLagState.Exceeded : CdcConnectorLagState.WithinThreshold,
            observed,
            thresholdMilliseconds,
            exceeded ? [ThresholdExceeded(observed, thresholdMilliseconds, observedAt)] : []
        );
    }

    private static CdcConnectorLagObservation Complete(
        CdcObservationContext context,
        DateTimeOffset observedAt,
        CdcConnectorLagState lagState,
        CdcConnectorLagReading? reading,
        long? thresholdMilliseconds,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        CdcConnectorLagObservation observation = new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            lagState,
            reading?.CurrentMilliseconds,
            thresholdMilliseconds,
            reading?.P50Milliseconds,
            reading?.P95Milliseconds,
            reading?.P99Milliseconds,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );

        CdcContractValidationResult validationResult = CdcConnectorLagObservationValidator.Validate(
            observation,
            context.ToValidationContext(observedAt)
        );

        if (validationResult.Succeeded)
        {
            return observation;
        }

        // An observation that cannot pass its own contract never carries a lag verdict: an
        // inconsistent reading is absent evidence, not evidence that the pipeline is caught up.
        return observation with
        {
            LagState = CdcConnectorLagState.Unknown,
            CurrentLagMilliseconds = null,
            ThresholdMilliseconds = null,
            P50LagMilliseconds = null,
            P95LagMilliseconds = null,
            P99LagMilliseconds = null,
            Diagnostics = CdcDiagnostic.NormalizeDiagnostics([
                .. observation.Diagnostics,
                .. validationResult.Diagnostics,
            ]),
        };
    }

    private static long ThresholdMilliseconds(TimeSpan lagThreshold) =>
        lagThreshold <= TimeSpan.Zero ? 0 : (long)lagThreshold.TotalMilliseconds;

    private static CdcDiagnostic EvidenceUnavailable(
        CdcConnectorLagReadOutcome outcome,
        DateTimeOffset observedAt
    )
    {
        (string code, string message, bool retryable) = outcome switch
        {
            CdcConnectorLagReadOutcome.MetricsAbsent => (
                "connectorLagMetricsAbsent",
                "CDC connector source lag metrics are not reported by the Debezium metrics bridge.",
                true
            ),
            CdcConnectorLagReadOutcome.MalformedResponse => (
                "connectorLagMalformedResponse",
                "CDC connector source lag metrics were answered in a shape that could not be read.",
                false
            ),
            _ => (
                "connectorLagUnavailable",
                "CDC connector source lag evidence is unavailable from the Debezium metrics bridge.",
                true
            ),
        };

        return new CdcDiagnostic(
            code,
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Lag,
            observedAt,
            message,
            retryable,
            artifactKind: "connectorLag",
            expected: "debezium source lag current value and p50, p95, p99 quantiles",
            observed: outcome.ToString()
        ).WithPath("$.lagState");
    }

    private static CdcDiagnostic UnusableReading(DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "connectorLagUnusableReading",
            CdcDiagnosticCategory.InvalidObservation,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Lag,
            observedAt,
            "CDC connector source lag metrics reported a value the shared contract cannot carry.",
            retryable: false,
            artifactKind: "connectorLag",
            expected: "non-negative milliseconds",
            observed: "negative"
        ).WithPath("$.currentLagMilliseconds");

    private static CdcDiagnostic QuantilesOutOfOrder(
        CdcConnectorLagReading reading,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            "connectorLagQuantilesOutOfOrder",
            CdcDiagnosticCategory.InvalidOrdering,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Lag,
            observedAt,
            "CDC connector source lag quantiles must be ordered p50 <= p95 <= p99.",
            retryable: false,
            artifactKind: "connectorLag",
            expected: "p50 <= p95 <= p99",
            observed: Quantiles(reading)
        ).WithPath("$.p50LagMilliseconds");

    private static CdcDiagnostic ThresholdExceeded(
        CdcConnectorLagReading reading,
        long thresholdMilliseconds,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            "connectorLagExceeded",
            CdcDiagnosticCategory.LagExceeded,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Lag,
            observedAt,
            "CDC connector source lag exceeds the configured threshold.",
            retryable: true,
            artifactKind: "connectorLag",
            expected: Milliseconds(thresholdMilliseconds),
            observed: Milliseconds(reading.CurrentMilliseconds)
        ).WithPath("$.currentLagMilliseconds");

    private static string Quantiles(CdcConnectorLagReading reading) =>
        $"p50 {Milliseconds(reading.P50Milliseconds)}, p95 {Milliseconds(reading.P95Milliseconds)}, "
        + $"p99 {Milliseconds(reading.P99Milliseconds)}";

    private static string Milliseconds(long value) => $"{value.ToString(CultureInfo.InvariantCulture)} ms";
}
