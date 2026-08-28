// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Outcome of one read of the running DMS's DocumentCache status endpoint. Every outcome other than
/// <see cref="Succeeded"/> is absent evidence rather than a correlation verdict, so each keeps write
/// admission closed. The three failures are reported apart from one another because they need
/// different things done about them: an unmapped route is a deployment setting, a rejected token is a
/// credential, and an unreachable endpoint is a running process.
/// </summary>
public enum CdcProjectionStatusReadOutcome
{
    Succeeded,

    /// <summary>
    /// The DMS answered 404. The route is mapped only when
    /// <c>DataManagement:DocumentCache:Status:RequiredRole</c> is a valid single role token, which it
    /// is not by default, so this is a configuration fault rather than an outage.
    /// </summary>
    EndpointNotMapped,

    /// <summary>The DMS answered 401 or 403: the supplied bearer token does not carry the required role.</summary>
    Unauthorized,

    /// <summary>The DMS could not be reached, timed out, or answered a failure of its own.</summary>
    Unavailable,

    /// <summary>The DMS answered successfully with a body that is not the documented status shape.</summary>
    MalformedResponse,
}

/// <summary>
/// The evidence one status read took from the DMS. Only what the correlation is built from is read:
/// the endpoint publishes a full projection report, and the rest of it is not this observation's to
/// carry.
/// </summary>
public sealed record CdcProjectionStatusReading(
    DateTimeOffset ObservedAt,
    IReadOnlyList<CdcProjectionTargetReading> Targets
);

/// <summary>One reported projection target, as the DocumentCache status contract publishes it.</summary>
public sealed record CdcProjectionTargetReading(
    DocumentCacheStatusTargetKey TargetKey,
    DateTimeOffset ProcessObservedAt,
    string? Provider,
    string? PhysicalSourceFingerprint,
    DocumentCacheOperationalHealthStatus OperationalHealthStatus,
    DocumentCacheStatusReason OperationalHealthReason,
    DocumentCacheCaughtUpStatus CaughtUpStatus,
    DocumentCacheStatusReason CaughtUpReason,
    DocumentCacheStatusQueuePresence QueuePresence,
    IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory> EnqueueFailureCategories
);

/// <summary>
/// Result of one status read. <see cref="Summary"/> is composed from the request that was issued and
/// the status it received; a response body never reaches it, and neither does the bearer token.
/// </summary>
public sealed record CdcProjectionStatusReadResult(
    CdcProjectionStatusReadOutcome Outcome,
    CdcProjectionStatusReading? Status,
    string? Summary
)
{
    public bool Succeeded => Outcome == CdcProjectionStatusReadOutcome.Succeeded && Status is not null;
}

/// <summary>
/// Correlates the running DMS projector's own reported status with the CDC binding.
/// </summary>
/// <remarks>
/// The evidence is read over HTTP from the process that actually runs the projector. It is never
/// obtained by resolving <c>IDocumentCacheStatusService</c> in this process: the CLI registers the
/// projection supervisor without its hosted service, so no projector runs here and there is no runtime
/// to observe. Standalone direct observation would force both operational health and caught-up to
/// unknown, and runtime-endpoint mode would classify the same absence as a process failure — either
/// way the enablement sequence could never observe caught-up.
/// </remarks>
public interface ICdcProjectionCorrelationCollector
{
    Task<CdcProjectionCorrelationObservation> CollectAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken = default
    );
}

internal sealed class CdcProjectionCorrelationCollector(
    IHttpClientFactory httpClientFactory,
    IOptions<CdcControlOptions> options,
    TimeProvider timeProvider,
    ILogger<CdcProjectionCorrelationCollector> logger
) : ICdcProjectionCorrelationCollector
{
    /// <summary>Named client the deployment configures transport and handler policy on.</summary>
    internal const string HttpClientName = "dms-cdc-projection-status";

    internal const string StatusEndpointPath = "health/document-cache";

    /// <summary>
    /// The setting that decides whether the DMS maps the status endpoint at all. It is named in the
    /// unmapped-route diagnostic because a bare unavailability would leave an operator with nothing to
    /// act on.
    /// </summary>
    internal const string RequiredRoleSettingName = "DataManagement:DocumentCache:Status:RequiredRole";

    public async Task<CdcProjectionCorrelationObservation> CollectAsync(
        CdcObservationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        CdcProjectionStatusReadResult read = await ReadAsync(cancellationToken).ConfigureAwait(false);

        return CdcProjectionCorrelationObservationMapper.Map(context, read, timeProvider.GetUtcNow());
    }

    internal async Task<CdcProjectionStatusReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        CdcControlOptions controlOptions = options.Value;
        TimeSpan timeout = controlOptions.Timeouts.StatusEndpoint;

        using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        requestCancellation.CancelAfter(timeout);

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Get, RequestUri(controlOptions));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            controlOptions.DmsBearerToken
        );

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, requestCancellation.Token);

            // Only the status is logged. A DocumentCache status body reports every configured target.
            logger.LogDebug("DMS DocumentCache status read answered {StatusCode}.", (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                return FailedStatus(response.StatusCode);
            }

            return ParseStatus(await response.Content.ReadAsStringAsync(requestCancellation.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(
                CdcProjectionStatusReadOutcome.Unavailable,
                "The DMS DocumentCache status endpoint did not answer within "
                    + $"{timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."
            );
        }
        catch (HttpRequestException exception)
        {
            return Failed(
                CdcProjectionStatusReadOutcome.Unavailable,
                $"The DMS DocumentCache status endpoint could not be reached: {exception.HttpRequestError}."
            );
        }
    }

    private static CdcProjectionStatusReadResult FailedStatus(HttpStatusCode statusCode)
    {
        string reportedStatus = ((int)statusCode).ToString(CultureInfo.InvariantCulture);

        return statusCode switch
        {
            HttpStatusCode.NotFound => Failed(
                CdcProjectionStatusReadOutcome.EndpointNotMapped,
                "The running DMS did not map the DocumentCache status endpoint. Set "
                    + $"{RequiredRoleSettingName} to a single role token and restart the DMS."
            ),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failed(
                CdcProjectionStatusReadOutcome.Unauthorized,
                $"The DMS DocumentCache status endpoint answered {reportedStatus}: the supplied token "
                    + $"does not carry the role {RequiredRoleSettingName} requires."
            ),
            _ => Failed(
                CdcProjectionStatusReadOutcome.Unavailable,
                $"The DMS DocumentCache status endpoint answered {reportedStatus}."
            ),
        };
    }

    /// <summary>
    /// Reads the published status. Only the evidence the correlation is built from is taken; the
    /// endpoint publishes a full projection report, and the rest of it is not this observation's to
    /// carry. A target the documented shape cannot be read out of makes the whole answer malformed
    /// rather than one skipped entry: a status the control plane cannot read is not evidence that the
    /// target it is looking for is absent.
    /// </summary>
    private static CdcProjectionStatusReadResult ParseStatus(string body)
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
            if (
                root.ValueKind != JsonValueKind.Object
                || ReadTimestamp(root, "observedAt") is not { } observedAt
                || !root.TryGetProperty("targets", out JsonElement targets)
                || targets.ValueKind != JsonValueKind.Array
            )
            {
                return Malformed();
            }

            List<CdcProjectionTargetReading> readings = [];
            foreach (JsonElement target in targets.EnumerateArray())
            {
                if (ReadTarget(target) is not { } reading)
                {
                    return Malformed();
                }

                readings.Add(reading);
            }

            return new(CdcProjectionStatusReadOutcome.Succeeded, new(observedAt, readings), null);
        }
    }

    private static CdcProjectionTargetReading? ReadTarget(JsonElement target)
    {
        if (
            target.ValueKind != JsonValueKind.Object
            || ReadTargetKey(target) is not { } targetKey
            || ReadTimestamp(target, "processObservedAt") is not { } processObservedAt
            || ReadComponentState<DocumentCacheOperationalHealthStatus>(target, "operationalHealth", "status")
                is not { } operationalHealthStatus
            || ReadComponentState<DocumentCacheStatusReason>(target, "operationalHealth", "reason")
                is not { } operationalHealthReason
            || ReadComponentState<DocumentCacheCaughtUpStatus>(target, "caughtUp", "status")
                is not { } caughtUpStatus
            || ReadComponentState<DocumentCacheStatusReason>(target, "caughtUp", "reason")
                is not { } caughtUpReason
            || ReadComponentState<DocumentCacheStatusQueuePresence>(target, "queueSummary", "presence")
                is not { } queuePresence
            || ReadEnqueueFailureCategories(target) is not { } enqueueFailureCategories
        )
        {
            return null;
        }

        return new(
            targetKey,
            processObservedAt,
            ReadString(target, "provider"),
            ReadString(target, "physicalSourceFingerprint"),
            operationalHealthStatus,
            operationalHealthReason,
            caughtUpStatus,
            caughtUpReason,
            queuePresence,
            enqueueFailureCategories
        );
    }

    private static DocumentCacheStatusTargetKey? ReadTargetKey(JsonElement target)
    {
        if (
            !target.TryGetProperty("targetKey", out JsonElement targetKey)
            || targetKey.ValueKind != JsonValueKind.Object
            || ReadString(targetKey, "tenantKey") is not { } tenantKey
            || !targetKey.TryGetProperty("dataStoreId", out JsonElement dataStoreIdElement)
            || dataStoreIdElement.ValueKind != JsonValueKind.Number
            || !dataStoreIdElement.TryGetInt64(out long dataStoreId)
            || !DocumentCacheTargetKey.TryCreate(
                tenantKey,
                dataStoreId,
                out DocumentCacheTargetKey? key,
                out _
            )
        )
        {
            return null;
        }

        return DocumentCacheStatusTargetKey.FromTargetKey(key);
    }

    private static IReadOnlyList<DocumentCacheStatusEnqueueFailureCategory>? ReadEnqueueFailureCategories(
        JsonElement target
    )
    {
        if (
            !target.TryGetProperty("enqueueFailures", out JsonElement enqueueFailures)
            || enqueueFailures.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }

        if (
            !enqueueFailures.TryGetProperty("byCategory", out JsonElement byCategory)
            || byCategory.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }

        List<DocumentCacheStatusEnqueueFailureCategory> categories = [];
        foreach (JsonElement categoryCount in byCategory.EnumerateArray())
        {
            if (
                categoryCount.ValueKind != JsonValueKind.Object
                || ReadEnum<DocumentCacheStatusEnqueueFailureCategory>(categoryCount, "category")
                    is not { } category
            )
            {
                return null;
            }

            categories.Add(category);
        }

        return categories;
    }

    private static TEnum? ReadComponentState<TEnum>(
        JsonElement target,
        string componentName,
        string propertyName
    )
        where TEnum : struct, Enum =>
        target.TryGetProperty(componentName, out JsonElement component)
        && component.ValueKind == JsonValueKind.Object
            ? ReadEnum<TEnum>(component, propertyName)
            : null;

    /// <summary>
    /// Reads one published enum. The contract writes its members in lower camel case, which differs
    /// from the declared name only in the leading character; a value written any other way — a member
    /// this build does not define, or an ordinal — is not read as a member at all.
    /// </summary>
    private static TEnum? ReadEnum<TEnum>(JsonElement element, string propertyName)
        where TEnum : struct, Enum =>
        ReadString(element, propertyName) is { Length: > 0 } text
        && char.IsLetter(text[0])
        && Enum.TryParse(text, ignoreCase: true, out TEnum value)
        && Enum.IsDefined(value)
            ? value
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName) =>
        ReadString(element, propertyName) is { } text
        && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value
        )
            ? value
            : null;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Uri RequestUri(CdcControlOptions options)
    {
        string baseUrl = options.DmsBaseUrl;
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new(new Uri(baseUrl, UriKind.Absolute), StatusEndpointPath);
    }

    private static CdcProjectionStatusReadResult Failed(
        CdcProjectionStatusReadOutcome outcome,
        string summary
    ) => new(outcome, null, summary);

    private static CdcProjectionStatusReadResult Malformed() =>
        Failed(
            CdcProjectionStatusReadOutcome.MalformedResponse,
            "The DMS DocumentCache status endpoint answered with a body that is not the documented shape."
        );
}

/// <summary>
/// Maps one DocumentCache status read onto the shared projection correlation observation. The mapping
/// is transport-independent: it consumes a read result and never reaches for evidence of its own.
/// </summary>
public static class CdcProjectionCorrelationObservationMapper
{
    /// <summary>
    /// Composes the observation. The correlation state reports what the running DMS agreed with, not a
    /// verdict on it: only identity agreement is decided here, and the reported health, caught-up,
    /// queue, and enqueue-failure evidence is passed through as observed. Absent evidence reports
    /// <see cref="CdcProjectionCorrelationState.Unavailable"/>, which keeps admission closed.
    /// </summary>
    /// <remarks>
    /// The observation is deliberately not run through
    /// <see cref="CdcProjectionCorrelationObservationValidator"/> here. That validator treats every
    /// mismatch state as a contract failure, which is exactly what a mismatch is meant to report, so
    /// validating and degrading would erase the classification an operator needs. The status evaluator
    /// validates the observation where the failure is interpreted.
    /// </remarks>
    public static CdcProjectionCorrelationObservation Map(
        CdcObservationContext context,
        CdcProjectionStatusReadResult read,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);

        if (!read.Succeeded || read.Status is not { } status)
        {
            return Unobserved(
                context,
                observedAt,
                CdcProjectionCorrelationState.Unavailable,
                DocumentCacheStatusReason.None,
                EvidenceUnavailable(read.Outcome, observedAt)
            );
        }

        List<CdcProjectionTargetReading> matches = status
            .Targets.Where(target => IsBindingTarget(target.TargetKey, context.TargetIdentity))
            .ToList();

        if (matches.Count == 0)
        {
            return Unobserved(
                context,
                observedAt,
                CdcProjectionCorrelationState.TargetMismatch,
                DocumentCacheStatusReason.UnresolvedTarget,
                TargetMismatch(status.Targets.Count, observedAt)
            );
        }

        if (matches.Count > 1)
        {
            return Unobserved(
                context,
                observedAt,
                CdcProjectionCorrelationState.InvalidPayload,
                DocumentCacheStatusReason.None,
                DuplicateTarget(matches.Count, observedAt)
            );
        }

        return Correlate(context, matches[0], observedAt);
    }

    /// <summary>
    /// The same comparison the shared validator applies to the observation's own target key, so a
    /// target selected here is one the observation can be validated against.
    /// </summary>
    private static bool IsBindingTarget(
        DocumentCacheStatusTargetKey targetKey,
        CdcTargetIdentity targetIdentity
    ) =>
        string.Equals(
            CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(targetKey.TenantKey),
            targetIdentity.TenantKey,
            StringComparison.Ordinal
        )
        && string.Equals(
            targetKey.DataStoreId.ToString(CultureInfo.InvariantCulture),
            targetIdentity.DataStoreId,
            StringComparison.Ordinal
        );

    private static CdcProjectionCorrelationObservation Correlate(
        CdcObservationContext context,
        CdcProjectionTargetReading target,
        DateTimeOffset observedAt
    )
    {
        (CdcProjectionCorrelationState correlationState, CdcDiagnostic? diagnostic) = Classify(
            context,
            target,
            observedAt
        );

        // A DMS clock marginally ahead of the control plane must not make the observation claim it
        // observed the projection before the observation itself existed.
        DateTimeOffset effectiveObservedAt =
            target.ProcessObservedAt > observedAt ? target.ProcessObservedAt : observedAt;

        return new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            effectiveObservedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            target.ProcessObservedAt,
            target.TargetKey,
            correlationState,
            target.OperationalHealthStatus,
            target.OperationalHealthReason,
            target.CaughtUpStatus,
            target.CaughtUpReason,
            target.QueuePresence,
            target.EnqueueFailureCategories,
            CdcDiagnostic.NormalizeDiagnostics(diagnostic is null ? [] : [diagnostic])
        );
    }

    /// <summary>
    /// Decides identity agreement in the order the evidence is layered: the target the DMS is
    /// projecting, the provider it resolved for that target, and the physical source it fingerprinted.
    /// Evidence the payload does not carry is reported as an invalid payload; evidence it carries that
    /// disagrees with the binding is reported as the matching mismatch.
    /// </summary>
    private static (CdcProjectionCorrelationState State, CdcDiagnostic? Diagnostic) Classify(
        CdcObservationContext context,
        CdcProjectionTargetReading target,
        DateTimeOffset observedAt
    )
    {
        if (!RelationalProviderToken.TryNormalize(target.Provider, out RelationalProviderToken? reported))
        {
            return (
                CdcProjectionCorrelationState.InvalidPayload,
                ProviderUnreadable(target.Provider, observedAt)
            );
        }

        if (
            !CdcProviderToken.TryToRelationalProviderToken(
                context.TargetIdentity.Provider,
                out RelationalProviderToken? expected
            )
            || reported != expected
        )
        {
            return (
                CdcProjectionCorrelationState.ProviderMismatch,
                ProviderMismatch(reported, context.TargetIdentity.Provider, observedAt)
            );
        }

        // A binding with no recorded physical source has nothing for the reported one to disagree
        // with. The pre-binding preflight runs in exactly that state, and adopting the reported
        // fingerprint as agreement would be the control plane believing its own unverified evidence.
        if (context.PhysicalSourceFingerprint is not { } bindingFingerprint)
        {
            return (CdcProjectionCorrelationState.Matched, null);
        }

        if (target.PhysicalSourceFingerprint is not { } reportedFingerprint)
        {
            return (CdcProjectionCorrelationState.InvalidPayload, PhysicalSourceUnreported(observedAt));
        }

        return string.Equals(reportedFingerprint, bindingFingerprint, StringComparison.Ordinal)
            ? (CdcProjectionCorrelationState.Matched, null)
            : (CdcProjectionCorrelationState.SourceMismatch, SourceMismatch(reportedFingerprint, observedAt));
    }

    /// <summary>
    /// The observation composed when no target's evidence was observed. The reported projection state
    /// is unknown throughout rather than defaulted to anything an evaluator could read as healthy, and
    /// the E18 target key is the one the binding expects — derived, never taken from the payload.
    /// </summary>
    private static CdcProjectionCorrelationObservation Unobserved(
        CdcObservationContext context,
        DateTimeOffset observedAt,
        CdcProjectionCorrelationState correlationState,
        DocumentCacheStatusReason reason,
        CdcDiagnostic diagnostic
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            context.PhysicalSourceFingerprint,
            observedAt,
            // A target identity the shared contract cannot express as an E18 key leaves the field
            // absent, which the observation's validator reports as a missing required field. A
            // fabricated key would be worse: it would pass validation while naming nothing.
            ExpectedTargetKey(context.TargetIdentity)!,
            correlationState,
            DocumentCacheOperationalHealthStatus.Unknown,
            reason,
            DocumentCacheCaughtUpStatus.Unknown,
            reason,
            DocumentCacheStatusQueuePresence.Unavailable,
            [],
            CdcDiagnostic.NormalizeDiagnostics([diagnostic])
        );

    private static DocumentCacheStatusTargetKey? ExpectedTargetKey(CdcTargetIdentity targetIdentity)
    {
        string tenantKey = string.Equals(
            targetIdentity.TenantKey,
            CdcTargetValidator.DefaultBindingTenantKey,
            StringComparison.Ordinal
        )
            ? string.Empty
            : targetIdentity.TenantKey;

        if (
            !long.TryParse(
                targetIdentity.DataStoreId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long dataStoreId
            )
            || !DocumentCacheTargetKey.TryCreate(
                tenantKey,
                dataStoreId,
                out DocumentCacheTargetKey? key,
                out _
            )
        )
        {
            return null;
        }

        return DocumentCacheStatusTargetKey.FromTargetKey(key);
    }

    private static CdcDiagnostic EvidenceUnavailable(
        CdcProjectionStatusReadOutcome outcome,
        DateTimeOffset observedAt
    )
    {
        (string code, string message, bool retryable) = outcome switch
        {
            CdcProjectionStatusReadOutcome.EndpointNotMapped => (
                "projectionStatusEndpointNotMapped",
                "The running DMS does not map the DocumentCache status endpoint. Configure "
                    + $"{CdcProjectionCorrelationCollector.RequiredRoleSettingName} with a single role "
                    + "token and restart the DMS.",
                false
            ),
            CdcProjectionStatusReadOutcome.Unauthorized => (
                "projectionStatusUnauthorized",
                "The DMS DocumentCache status endpoint rejected the control plane's token. It must "
                    + $"carry the role {CdcProjectionCorrelationCollector.RequiredRoleSettingName} names.",
                false
            ),
            CdcProjectionStatusReadOutcome.MalformedResponse => (
                "projectionStatusMalformedResponse",
                "The DMS DocumentCache status endpoint answered in a shape that could not be read.",
                false
            ),
            _ => (
                "projectionStatusUnavailable",
                "CDC projection correlation evidence is unavailable from the running DMS.",
                true
            ),
        };

        return new CdcDiagnostic(
            code,
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            message,
            retryable,
            artifactKind: "documentCacheStatusEndpoint",
            artifactName: $"/{CdcProjectionCorrelationCollector.StatusEndpointPath}",
            expected: "the running DMS projector's reported status",
            observed: outcome.ToString()
        ).WithPath("$.correlationState");
    }

    private static CdcDiagnostic TargetMismatch(int reportedTargetCount, DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "projectionTargetMismatch",
            CdcDiagnosticCategory.TargetMismatch,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS does not project the CDC binding's target.",
            retryable: false,
            artifactKind: "documentCacheStatusTarget",
            expected: "one configured projection target matching the binding",
            observed: $"{reportedTargetCount.ToString(CultureInfo.InvariantCulture)} reported targets"
        ).WithPath("$.e18TargetKey");

    private static CdcDiagnostic DuplicateTarget(int matchCount, DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "projectionDuplicateTarget",
            CdcDiagnosticCategory.MalformedPayload,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS reported the CDC binding's target more than once.",
            retryable: false,
            artifactKind: "documentCacheStatusTarget",
            expected: "one reported target for the binding",
            observed: $"{matchCount.ToString(CultureInfo.InvariantCulture)} reported targets"
        ).WithPath("$.e18TargetKey");

    private static CdcDiagnostic ProviderUnreadable(string? reportedProvider, DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "projectionProviderUnreadable",
            CdcDiagnosticCategory.MalformedPayload,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS did not report a readable relational provider for the target.",
            retryable: true,
            artifactKind: "documentCacheStatusTarget",
            expected: $"{RelationalProviderToken.PostgresqlValue} or {RelationalProviderToken.SqlServerValue}",
            observed: reportedProvider is null ? "absent" : "unrecognized"
        ).WithPath("$.correlationState");

    private static CdcDiagnostic ProviderMismatch(
        RelationalProviderToken reportedProvider,
        CdcProvider bindingProvider,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            "projectionProviderMismatch",
            CdcDiagnosticCategory.ProviderMismatch,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS projects the target through a different relational provider than the "
                + "CDC binding was created for.",
            retryable: false,
            artifactKind: "documentCacheStatusTarget",
            expected: bindingProvider.ToString(),
            observed: reportedProvider.Value
        ).WithPath("$.correlationState");

    private static CdcDiagnostic PhysicalSourceUnreported(DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "projectionPhysicalSourceUnreported",
            CdcDiagnosticCategory.MalformedPayload,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS reported no physical source fingerprint for the target.",
            retryable: true,
            artifactKind: "documentCacheStatusTarget",
            expected: "the binding's physical source fingerprint",
            observed: "absent"
        ).WithPath("$.correlationState");

    private static CdcDiagnostic SourceMismatch(string reportedFingerprint, DateTimeOffset observedAt) =>
        new CdcDiagnostic(
            "projectionSourceMismatch",
            CdcDiagnosticCategory.SourceMismatch,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            "The running DMS projects the target from a different physical source than the CDC "
                + "binding captures.",
            retryable: false,
            artifactKind: "documentCacheStatusTarget",
            expected: "the binding's physical source fingerprint",
            observed: reportedFingerprint
        ).WithPath("$.correlationState");
}
