// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

[JsonConverter(typeof(CdcLowerCamelJsonStringEnumConverter<CdcControlPlaneOperationStatus>))]
public enum CdcControlPlaneOperationStatus
{
    Succeeded,
    BindingMissing,
    BindingMismatch,
    StateStoreUnavailable,
    InvalidOperation,
}

public sealed record CdcBindingLifecycleResult(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcControlPlaneOperationStatus Status,
    CdcBindingStateContract? State,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcJsonContract;

public sealed record CdcBindingLifecycleListResult(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] CdcControlPlaneOperationStatus Status,
    [property: JsonRequired] IReadOnlyList<CdcBindingStateContract> States,
    [property: JsonRequired] IReadOnlyList<CdcDiagnostic> Diagnostics
) : ICdcJsonContract;

public interface ICdcBindingLifecycleService
{
    Task<CdcBindingLifecycleResult> CreateBindingIfAbsentAsync(
        CdcBinding binding,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleResult> ReadBindingAsync(
        CdcBindingIdentity identity,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleResult> ExactMatchBindingAsync(
        CdcBinding binding,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleListResult> ListBindingsAsync(
        string deploymentKey,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleResult> LatchSourceHistoryLossAsync(
        CdcIncident incident,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleResult> ImportVerifiedBindingAsync(
        CdcAdoptionProof verifiedAdoptionProof,
        CancellationToken cancellationToken = default
    );

    Task<CdcBindingLifecycleResult> DeleteStateAfterVerifiedCleanupAsync(
        CdcCleanupProof verifiedCleanupProof,
        CancellationToken cancellationToken = default
    );
}

public sealed record CdcProviderBarrierCaptureRequest(string ConnectionString, CdcBinding Binding)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CaptureWaitTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed record CdcProviderBarrierCaptureResult
{
    private CdcProviderBarrierCaptureResult(
        CdcProvider provider,
        string? postgresqlBarrierLsn,
        string? sqlServerCommitLsn,
        string? sqlServerChangeLsn,
        long? sqlServerEventSerialNo,
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Provider = provider;
        PostgresqlBarrierLsn = postgresqlBarrierLsn;
        SqlServerCommitLsn = sqlServerCommitLsn;
        SqlServerChangeLsn = sqlServerChangeLsn;
        SqlServerEventSerialNo = sqlServerEventSerialNo;
        BarrierCapturedAt = barrierCapturedAt.ToUniversalTime();
        Diagnostics = diagnostics;
    }

    [JsonRequired]
    public CdcProvider Provider { get; }

    public string? PostgresqlBarrierLsn { get; }

    public string? SqlServerCommitLsn { get; }

    public string? SqlServerChangeLsn { get; }

    public long? SqlServerEventSerialNo { get; }

    [JsonRequired]
    public DateTimeOffset BarrierCapturedAt { get; }

    [JsonRequired]
    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded =>
        Diagnostics.Count == 0
        && (
            Provider switch
            {
                CdcProvider.Postgresql => PostgresqlBarrierLsn is not null,
                CdcProvider.SqlServer => SqlServerCommitLsn is not null
                    && SqlServerChangeLsn is not null
                    && SqlServerEventSerialNo is not null,
                _ => false,
            }
        );

    public static CdcProviderBarrierCaptureResult PostgresqlSuccess(
        string postgresqlBarrierLsn,
        DateTimeOffset barrierCapturedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresqlBarrierLsn);

        return new(CdcProvider.Postgresql, postgresqlBarrierLsn, null, null, null, barrierCapturedAt, []);
    }

    public static CdcProviderBarrierCaptureResult SqlServerSuccess(
        string sqlServerCommitLsn,
        string sqlServerChangeLsn,
        long sqlServerEventSerialNo,
        DateTimeOffset barrierCapturedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlServerCommitLsn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlServerChangeLsn);

        return new(
            CdcProvider.SqlServer,
            null,
            sqlServerCommitLsn,
            sqlServerChangeLsn,
            sqlServerEventSerialNo,
            barrierCapturedAt,
            []
        );
    }

    public static CdcProviderBarrierCaptureResult Failure(
        CdcProvider provider,
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(provider, null, null, null, null, barrierCapturedAt, diagnostics);
    }
}

public sealed record CdcProviderBarrierObservationRequest(
    string OperationId,
    CdcBinding Binding,
    DateTimeOffset ProjectionCaughtUpObservedAt,
    CdcProviderBarrierCaptureResult CapturedBarrier,
    CdcConnectorOffsetObservation ConnectorOffset,
    string? ExpectedConnectSourcePartitionHash = null
);

public sealed record CdcSourceHistoryObservationRequest(
    string ConnectionString,
    string OperationId,
    CdcBinding Binding,
    CdcProviderSetupObservation? ProviderSetup,
    CdcConnectorOffsetObservation ConnectorOffset
)
{
    public CdcIncident? LatchedIncident { get; init; }

    public CdcSqlServerSchemaHistoryEvidence? SqlServerSchemaHistory { get; init; }

    public string? ExpectedConnectSourcePartitionHash { get; init; }

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public interface ICdcProviderSourcePositionAdapter
{
    CdcProvider Provider { get; }

    Task<CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        CdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken = default
    );

    CdcProviderBarrierObservation ObserveProviderBarrier(CdcProviderBarrierObservationRequest request);

    Task<CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        CdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken = default
    );
}
