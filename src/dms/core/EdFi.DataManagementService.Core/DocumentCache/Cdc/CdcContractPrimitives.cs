// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcProvider>))]
public enum CdcProvider
{
    Postgresql,
    SqlServer,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcLifecycleState>))]
public enum CdcLifecycleState
{
    Disabled,
    Resetting,
    Rebuilding,
    Tracking,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcReadiness>))]
public enum CdcReadiness
{
    Ready,
    NotReady,
    Unknown,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcComponentState>))]
public enum CdcComponentState
{
    Satisfied,
    NotSatisfied,
    Unknown,
    NotApplicable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcBlockingCategory>))]
public enum CdcBlockingCategory
{
    None,
    BindingMissing,
    BindingMismatch,
    SourceMismatch,
    SourceHistoryLost,
    ProjectionNonOperational,
    ProviderSetupInvalid,
    KafkaPolicyInvalid,
    ConnectOffsetStoreInvalid,
    ConnectorConfigInvalid,
    ConnectorNotRunning,
    SnapshotIncomplete,
    ProjectionBacklog,
    ProviderHistoryUnknown,
    ProviderBarrierNotReached,
    LagExceeded,
    StatusObservationUnavailable,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcDiagnosticCategory>))]
public enum CdcDiagnosticCategory
{
    None,
    BindingMissing,
    BindingMismatch,
    SourceMismatch,
    SourceHistoryLost,
    ProjectionNonOperational,
    ProviderSetupInvalid,
    KafkaPolicyInvalid,
    ConnectOffsetStoreInvalid,
    ConnectorConfigInvalid,
    ConnectorNotRunning,
    SnapshotIncomplete,
    ProjectionBacklog,
    ProviderHistoryUnknown,
    ProviderBarrierNotReached,
    LagExceeded,
    StatusObservationUnavailable,
    MissingRequiredField,
    InvalidEnumValue,
    MalformedPayload,
    InvalidContractVersion,
    FutureUtcTimestamp,
    LocalStateUnavailable,
    MalformedProof,
    InvalidOperationId,
    InvalidTimestamp,
    BindingIdentityMismatch,
    VerificationIncomplete,
    InventoryIncomplete,
    UnexpectedArtifact,
    DuplicateArtifact,
    ArtifactNameMismatch,
    ArtifactNotRemoved,
    UnsafeEvidence,
    OperationMismatch,
    UnsupportedOperation,
    TargetMismatch,
    ProviderMismatch,
    InvalidOrdering,
    InvalidObservation,
    MalformedObservation,
    StaleObservation,
    FutureObservedAt,
    DiagnosticsTruncated,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcDiagnosticSeverity>))]
public enum CdcDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcDiagnosticComponent>))]
public enum CdcDiagnosticComponent
{
    Binding,
    Projection,
    ProviderSetup,
    ProviderBarrier,
    SourceHistory,
    KafkaPolicy,
    ConnectOffsetStore,
    ConnectorConfig,
    ConnectorRuntime,
    Lag,
    StateStore,
    ProofValidation,
    ObservationValidation,
    Admission,
    Retry,
}

public sealed record CdcTargetIdentity(
    [property: JsonRequired] string DeploymentKey,
    [property: JsonRequired] string TenantKey,
    [property: JsonRequired] string DataStoreId,
    [property: JsonRequired] string InstanceKey,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] CdcProvider Provider
);

public sealed record CdcBindingIdentity(
    [property: JsonRequired] string DeploymentKey,
    [property: JsonRequired] string TenantKey,
    [property: JsonRequired] string DataStoreId,
    [property: JsonRequired] string InstanceKey,
    [property: JsonRequired] long Generation
)
{
    public static CdcBindingIdentity FromTargetIdentity(CdcTargetIdentity targetIdentity)
    {
        ArgumentNullException.ThrowIfNull(targetIdentity);

        return new(
            targetIdentity.DeploymentKey,
            targetIdentity.TenantKey,
            targetIdentity.DataStoreId,
            targetIdentity.InstanceKey,
            targetIdentity.Generation
        );
    }
}

internal static class CdcSensitiveText
{
    private static readonly string[] DirectSensitiveFragments =
    [
        "password",
        "pwd",
        "secret",
        "connection string",
        "connectionstring",
        "credential",
        "tenantdisplay",
        "source identity",
        "sourceidentity",
        "source uuid",
        "sourceuuid",
        "database=",
        "database:",
        "database.",
        "database/",
        "database\\",
        "server=",
        "server:",
        "server.",
        "server/",
        "server\\",
        "catalog=",
        "catalog:",
        "catalog.",
        "catalog/",
        "catalog\\",
        "host=",
        "host:",
        "host.",
        "data source=",
        "initial catalog=",
        "user id=",
        "uid=",
        "username=",
        "security.protocol",
        "sasl.",
        "sasl_",
        "ssl.",
        "ssl_",
        "kafka security",
        "privatekey",
        "private key",
        "accesskey",
        "access key",
        "apikey",
        "api key",
        "bearer ",
    ];

    private static readonly string[] CompactSensitiveFragments =
    [
        "password",
        "pwd",
        "secret",
        "connectionstring",
        "credential",
        "tenantdisplay",
        "sourceidentity",
        "sourceuuid",
        "datasource",
        "initialcatalog",
        "userid",
        "username",
        "securityprotocol",
        "sasl",
        "kafkasecurity",
        "privatekey",
        "accesskey",
        "apikey",
        "bearer",
    ];

    private static readonly string[] CompactSensitivePrefixes = ["database", "server", "catalog", "host"];

    public static bool ContainsSensitiveFragment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        bool allowCompactPrefixMatch = !IsJsonPath(value);
        return ContainsDirectFragment(value)
            || ContainsDirectFragment(sanitized)
            || ContainsCompactFragment(value, allowCompactPrefixMatch)
            || ContainsCompactFragment(sanitized, allowCompactPrefixMatch);
    }

    private static bool ContainsDirectFragment(string value)
    {
        string lower = value.ToLowerInvariant();
        return Array.Exists(
            DirectSensitiveFragments,
            fragment => lower.Contains(fragment, StringComparison.Ordinal)
        );
    }

    private static bool ContainsCompactFragment(string value, bool allowPrefixMatch)
    {
        string compact = Compact(value);
        if (compact.Length == 0)
        {
            return false;
        }

        return Array.Exists(
                CompactSensitiveFragments,
                fragment => compact.Contains(fragment, StringComparison.Ordinal)
            )
            || (
                allowPrefixMatch
                && Array.Exists(
                    CompactSensitivePrefixes,
                    prefix => compact.StartsWith(prefix, StringComparison.Ordinal)
                )
            );
    }

    private static bool IsJsonPath(string value) =>
        value.TrimStart().StartsWith("$.", StringComparison.Ordinal);

    private static string Compact(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}

public static class CdcSha256ValueValidator
{
    private const string RequiredPrefix = "sha256:";
    private const int RequiredPrefixLength = 7;
    private const int HashHexDigitCount = 64;
    private const int RequiredLength = RequiredPrefixLength + HashHexDigitCount;

    public static bool IsValid(string? value)
    {
        if (
            value is null
            || value.Length != RequiredLength
            || !value.StartsWith(RequiredPrefix, StringComparison.Ordinal)
        )
        {
            return false;
        }

        foreach (char character in value.AsSpan(RequiredPrefix.Length))
        {
            if (!IsLowerHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public static void Validate(
        string? value,
        string path,
        string fieldName,
        bool required,
        CdcDiagnosticCollector diagnostics,
        CdcDiagnosticCategory malformedCategory,
        string malformedMessage,
        bool emptyIsMissing = false
    )
    {
        if (value is null || (emptyIsMissing && value.Length == 0))
        {
            if (required)
            {
                diagnostics.MissingRequiredField(path, fieldName);
            }

            return;
        }

        if (!IsValid(value))
        {
            diagnostics.Add(malformedCategory, path, malformedMessage);
        }
    }

    public static void ValidateMalformed(
        string? value,
        string path,
        CdcDiagnosticCollector diagnostics,
        CdcDiagnosticCategory malformedCategory,
        string malformedMessage
    )
    {
        if (!IsValid(value))
        {
            diagnostics.Add(malformedCategory, path, malformedMessage);
        }
    }

    private static bool IsLowerHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}

public sealed record CdcComponent
{
    private const int MaximumMessageLength = 512;

    [JsonConstructor]
    public CdcComponent(
        CdcComponentState state,
        CdcBlockingCategory category,
        DateTimeOffset? observedAt,
        string? message
    )
    {
        State = state;
        Category = category;
        ObservedAt = observedAt;
        Message = Sanitize(message);
    }

    [JsonRequired]
    public CdcComponentState State { get; init; }

    [JsonRequired]
    public CdcBlockingCategory Category { get; init; }

    public DateTimeOffset? ObservedAt { get; }

    public string? Message { get; }

    public static CdcComponent Satisfied(DateTimeOffset observedAt, string? message = null) =>
        new(CdcComponentState.Satisfied, CdcBlockingCategory.None, observedAt, message);

    public static CdcComponent NotSatisfied(
        CdcBlockingCategory category,
        DateTimeOffset? observedAt,
        string? message = null
    ) => new(CdcComponentState.NotSatisfied, category, observedAt, message);

    public static CdcComponent Unknown(
        CdcBlockingCategory category,
        DateTimeOffset? observedAt = null,
        string? message = null
    ) => new(CdcComponentState.Unknown, category, observedAt, message);

    public static CdcComponent NotApplicable(DateTimeOffset? observedAt = null, string? message = null) =>
        new(CdcComponentState.NotApplicable, CdcBlockingCategory.None, observedAt, message);

    private static string? Sanitize(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        return sanitized.Length <= MaximumMessageLength ? sanitized : sanitized[..MaximumMessageLength];
    }
}

public sealed record CdcDiagnostic
{
    private const int MaximumPathLength = 256;
    public static readonly DateTimeOffset DefaultObservedAt = DateTimeOffset.UnixEpoch;
    public const int MaximumDiagnostics = 16;
    public const int MaximumMessageLength = 512;
    public const int MaximumTextLength = 256;

    private static readonly IReadOnlyDictionary<CdcDiagnosticComponent, int> ComponentPrecedence =
        new Dictionary<CdcDiagnosticComponent, int>
        {
            [CdcDiagnosticComponent.Binding] = 0,
            [CdcDiagnosticComponent.Projection] = 1,
            [CdcDiagnosticComponent.ProviderSetup] = 2,
            [CdcDiagnosticComponent.ProviderBarrier] = 3,
            [CdcDiagnosticComponent.SourceHistory] = 4,
            [CdcDiagnosticComponent.KafkaPolicy] = 5,
            [CdcDiagnosticComponent.ConnectOffsetStore] = 6,
            [CdcDiagnosticComponent.ConnectorConfig] = 7,
            [CdcDiagnosticComponent.ConnectorRuntime] = 8,
            [CdcDiagnosticComponent.Lag] = 9,
            [CdcDiagnosticComponent.StateStore] = 10,
            [CdcDiagnosticComponent.ProofValidation] = 11,
            [CdcDiagnosticComponent.ObservationValidation] = 12,
            [CdcDiagnosticComponent.Admission] = 13,
            [CdcDiagnosticComponent.Retry] = 14,
        };

    [JsonConstructor]
    public CdcDiagnostic(
        string code,
        CdcDiagnosticCategory category,
        CdcDiagnosticSeverity severity,
        CdcDiagnosticComponent component,
        DateTimeOffset observedAt,
        string message,
        bool retryable,
        string? artifactKind = null,
        string? artifactName = null,
        string? expected = null,
        string? observed = null
    )
    {
        Code = SanitizeText(code, MaximumTextLength, ToLowerCamel(category), "diagnostic");
        Category = category;
        Severity = severity;
        Component = component;
        ObservedAt = observedAt.ToUniversalTime();
        Message = SanitizeText(message, MaximumMessageLength, "CDC diagnostic unavailable.", "diagnostic");
        Retryable = retryable;
        ArtifactKind = SanitizeOptionalText(artifactKind, MaximumTextLength, "artifactKind");
        ArtifactName = SanitizeOptionalText(artifactName, MaximumTextLength, "artifactName");
        Expected = SanitizeOptionalText(expected, MaximumTextLength, "expected");
        Observed = SanitizeOptionalText(observed, MaximumTextLength, "observed");
        Path = RecoverLegacyPath(Observed);
    }

    public CdcDiagnostic(CdcDiagnosticCategory category, string path, string message)
        : this(category, DefaultObservedAt, path, message) { }

    public CdcDiagnostic(
        CdcDiagnosticCategory category,
        DateTimeOffset observedAt,
        string path,
        string message
    )
        : this(
            ToLowerCamel(category),
            category,
            InferSeverity(category),
            InferComponent(category),
            observedAt,
            message,
            InferRetryable(category)
        )
    {
        Path = NormalizePath(path);
    }

    [JsonRequired]
    public string Code { get; init; }

    [JsonRequired]
    public CdcDiagnosticCategory Category { get; init; }

    [JsonRequired]
    public CdcDiagnosticSeverity Severity { get; init; }

    [JsonRequired]
    public CdcDiagnosticComponent Component { get; init; }

    [JsonRequired]
    public DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public string Message { get; init; }

    [JsonRequired]
    public bool Retryable { get; init; }

    public string? ArtifactKind { get; }

    public string? ArtifactName { get; }

    public string? Expected { get; }

    public string? Observed { get; }

    [JsonIgnore]
    public string Path { get; private init; }

    public CdcDiagnostic WithPath(string path) =>
        new(
            Code,
            Category,
            Severity,
            Component,
            ObservedAt,
            Message,
            Retryable,
            ArtifactKind,
            ArtifactName,
            Expected,
            Observed
        )
        {
            Path = NormalizePath(path),
        };

    public static IReadOnlyList<CdcDiagnostic> NormalizeDiagnostics(IReadOnlyList<CdcDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return [];
        }

        CdcDiagnostic[] ordered =
        [
            .. diagnostics
                .Where(diagnostic => diagnostic is not null)
                .OrderBy(diagnostic => GetComponentPrecedence(diagnostic.Component))
                .ThenBy(diagnostic => diagnostic.ObservedAt)
                .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.ArtifactKind ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.ArtifactName ?? string.Empty, StringComparer.Ordinal),
        ];

        if (ordered.Length <= MaximumDiagnostics)
        {
            return ordered;
        }

        int omittedCount = ordered.Length - MaximumDiagnostics + 1;
        CdcDiagnostic truncated = new(
            "diagnosticsTruncated",
            CdcDiagnosticCategory.DiagnosticsTruncated,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.ObservationValidation,
            ordered[MaximumDiagnostics - 2].ObservedAt,
            "CDC diagnostics were truncated.",
            false,
            observed: omittedCount.ToString(CultureInfo.InvariantCulture)
        );

        return [.. ordered.Take(MaximumDiagnostics - 1), truncated];
    }

    private static string NormalizePath(string? path)
    {
        string normalized = LoggingSanitizer.SanitizeForConsole(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "$";
        }

        return normalized.Length <= MaximumPathLength ? normalized : normalized[..MaximumPathLength];
    }

    private static string RecoverLegacyPath(string? observed) =>
        !string.IsNullOrWhiteSpace(observed) && observed[0] == '.' ? NormalizePath($"${observed}") : "$";

    private static string? SanitizeOptionalText(string? value, int maximumLength, string fieldName)
    {
        string sanitized = SanitizeText(value, maximumLength, string.Empty, fieldName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        return sanitized;
    }

    private static string SanitizeText(string? value, int maximumLength, string fallback, string fieldName)
    {
        if (CdcSensitiveText.ContainsSensitiveFragment(value))
        {
            return "redacted";
        }

        string sanitized = LoggingSanitizer.SanitizeForLogging(value);
        if (CdcSensitiveText.ContainsSensitiveFragment(sanitized))
        {
            return "redacted";
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback.Length == 0 ? string.Empty : fallback;
        }

        string bounded = sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
        return string.IsNullOrWhiteSpace(bounded) ? fieldName : bounded;
    }

    private static int GetComponentPrecedence(CdcDiagnosticComponent component) =>
        ComponentPrecedence.GetValueOrDefault(component, ComponentPrecedence.Count);

    private static string ToLowerCamel<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static CdcDiagnosticSeverity InferSeverity(CdcDiagnosticCategory category) =>
        category switch
        {
            CdcDiagnosticCategory.None => CdcDiagnosticSeverity.Info,
            CdcDiagnosticCategory.DiagnosticsTruncated or CdcDiagnosticCategory.StaleObservation =>
                CdcDiagnosticSeverity.Warning,
            _ => CdcDiagnosticSeverity.Error,
        };

    private static bool InferRetryable(CdcDiagnosticCategory category) =>
        category
            is CdcDiagnosticCategory.LocalStateUnavailable
                or CdcDiagnosticCategory.ProviderHistoryUnknown
                or CdcDiagnosticCategory.ProviderBarrierNotReached
                or CdcDiagnosticCategory.ProjectionBacklog
                or CdcDiagnosticCategory.ConnectorNotRunning
                or CdcDiagnosticCategory.LagExceeded
                or CdcDiagnosticCategory.StatusObservationUnavailable;

    private static CdcDiagnosticComponent InferComponent(CdcDiagnosticCategory category) =>
        category switch
        {
            CdcDiagnosticCategory.BindingMissing
            or CdcDiagnosticCategory.BindingMismatch
            or CdcDiagnosticCategory.BindingIdentityMismatch => CdcDiagnosticComponent.Binding,
            CdcDiagnosticCategory.ProjectionNonOperational or CdcDiagnosticCategory.ProjectionBacklog =>
                CdcDiagnosticComponent.Projection,
            CdcDiagnosticCategory.ProviderSetupInvalid => CdcDiagnosticComponent.ProviderSetup,
            CdcDiagnosticCategory.ProviderBarrierNotReached => CdcDiagnosticComponent.ProviderBarrier,
            CdcDiagnosticCategory.SourceHistoryLost or CdcDiagnosticCategory.ProviderHistoryUnknown =>
                CdcDiagnosticComponent.SourceHistory,
            CdcDiagnosticCategory.KafkaPolicyInvalid => CdcDiagnosticComponent.KafkaPolicy,
            CdcDiagnosticCategory.ConnectOffsetStoreInvalid => CdcDiagnosticComponent.ConnectOffsetStore,
            CdcDiagnosticCategory.ConnectorConfigInvalid => CdcDiagnosticComponent.ConnectorConfig,
            CdcDiagnosticCategory.ConnectorNotRunning or CdcDiagnosticCategory.SnapshotIncomplete =>
                CdcDiagnosticComponent.ConnectorRuntime,
            CdcDiagnosticCategory.LagExceeded => CdcDiagnosticComponent.Lag,
            CdcDiagnosticCategory.LocalStateUnavailable => CdcDiagnosticComponent.StateStore,
            CdcDiagnosticCategory.MalformedProof
            or CdcDiagnosticCategory.VerificationIncomplete
            or CdcDiagnosticCategory.InventoryIncomplete
            or CdcDiagnosticCategory.UnexpectedArtifact
            or CdcDiagnosticCategory.DuplicateArtifact
            or CdcDiagnosticCategory.ArtifactNameMismatch
            or CdcDiagnosticCategory.ArtifactNotRemoved
            or CdcDiagnosticCategory.UnsafeEvidence => CdcDiagnosticComponent.ProofValidation,
            _ => CdcDiagnosticComponent.ObservationValidation,
        };
}

public sealed record CdcContractValidationResult
{
    public CdcContractValidationResult(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Diagnostics = CdcDiagnostic.NormalizeDiagnostics(diagnostics);
    }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    [JsonIgnore]
    public bool Succeeded => Diagnostics.Count == 0;

    public static CdcContractValidationResult Success { get; } = new([]);

    public static CdcContractValidationResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics) =>
        new(diagnostics);
}

public sealed record CdcContractReadResult<TContract>
{
    private CdcContractReadResult(TContract? contract, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Contract = contract;
        Diagnostics = CdcDiagnostic.NormalizeDiagnostics(diagnostics);
    }

    public TContract? Contract { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    [JsonIgnore]
    public bool Succeeded => Diagnostics.Count == 0 && Contract is not null;

    public static CdcContractReadResult<TContract> Success(TContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        return new(contract, []);
    }

    public static CdcContractReadResult<TContract> Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(default, diagnostics);
    }
}

public sealed class CdcDiagnosticCollector
{
    private readonly List<CdcDiagnostic> _diagnostics = [];
    private readonly DateTimeOffset _observedAt;

    public CdcDiagnosticCollector()
        : this(CdcDiagnostic.DefaultObservedAt) { }

    public CdcDiagnosticCollector(DateTimeOffset observedAt)
    {
        _observedAt = observedAt.ToUniversalTime();
    }

    public IReadOnlyList<CdcDiagnostic> Diagnostics => CdcDiagnostic.NormalizeDiagnostics(_diagnostics);

    public bool HasDiagnostics => _diagnostics.Count != 0;

    public void Add(CdcDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        _diagnostics.Add(diagnostic);
    }

    public void Add(CdcDiagnosticCategory category, string path, string message) =>
        _diagnostics.Add(new(category, _observedAt, path, message));

    public void MissingRequiredField(string path, string fieldName) =>
        Add(CdcDiagnosticCategory.MissingRequiredField, path, $"Missing required field `{fieldName}`.");

    public void InvalidEnumValue(string path, string message) =>
        Add(CdcDiagnosticCategory.InvalidEnumValue, path, message);

    public void MalformedPayload(string path, string message) =>
        Add(CdcDiagnosticCategory.MalformedPayload, path, message);

    public void InvalidContractVersion(string path, string message) =>
        Add(CdcDiagnosticCategory.InvalidContractVersion, path, message);

    public void FutureUtcTimestamp(string path, DateTimeOffset value, DateTimeOffset now) =>
        Add(
            CdcDiagnosticCategory.FutureUtcTimestamp,
            path,
            $"UTC timestamp `{value:O}` must not be later than `{now:O}`."
        );

    public void LocalStateUnavailable(string path, string message) =>
        Add(CdcDiagnosticCategory.LocalStateUnavailable, path, message);

    public CdcContractValidationResult ToValidationResult() =>
        HasDiagnostics
            ? CdcContractValidationResult.Failure([.. _diagnostics])
            : CdcContractValidationResult.Success;
}
