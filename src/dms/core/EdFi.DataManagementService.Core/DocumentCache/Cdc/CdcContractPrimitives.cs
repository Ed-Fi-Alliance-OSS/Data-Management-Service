// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    MissingRequiredField,
    InvalidEnumValue,
    MalformedPayload,
    InvalidContractVersion,
    FutureUtcTimestamp,
    LocalStateUnavailable,
}

public sealed record CdcTargetIdentity(
    string DeploymentKey,
    string TenantKey,
    string DataStoreId,
    string InstanceKey,
    long Generation,
    CdcProvider Provider
);

public sealed record CdcBindingIdentity(
    string DeploymentKey,
    string TenantKey,
    string DataStoreId,
    string InstanceKey,
    long Generation
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

    public CdcComponentState State { get; }

    public CdcBlockingCategory Category { get; }

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
    private const int MaximumMessageLength = 512;

    [JsonConstructor]
    public CdcDiagnostic(CdcDiagnosticCategory category, string path, string message)
    {
        Category = category;
        Path = NormalizePath(path);
        Message = SanitizeMessage(message);
    }

    public CdcDiagnosticCategory Category { get; }

    public string Path { get; }

    public string Message { get; }

    private static string NormalizePath(string? path)
    {
        string normalized = LoggingSanitizer.SanitizeForConsole(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "$";
        }

        return normalized.Length <= MaximumPathLength ? normalized : normalized[..MaximumPathLength];
    }

    private static string SanitizeMessage(string? message)
    {
        string sanitized = LoggingSanitizer.SanitizeForLogging(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "CDC contract validation failed.";
        }

        return sanitized.Length <= MaximumMessageLength ? sanitized : sanitized[..MaximumMessageLength];
    }
}

public sealed record CdcContractValidationResult
{
    public CdcContractValidationResult(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Diagnostics = diagnostics;
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
        Diagnostics = diagnostics;
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

    public IReadOnlyList<CdcDiagnostic> Diagnostics => _diagnostics;

    public bool HasDiagnostics => _diagnostics.Count != 0;

    public void Add(CdcDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        _diagnostics.Add(diagnostic);
    }

    public void Add(CdcDiagnosticCategory category, string path, string message) =>
        _diagnostics.Add(new(category, path, message));

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
