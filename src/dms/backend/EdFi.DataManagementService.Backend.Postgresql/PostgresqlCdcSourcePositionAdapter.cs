// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed record PostgresqlCdcProviderBarrierCaptureRequest(string ConnectionString, CdcBinding Binding)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed record PostgresqlCdcProviderBarrierCaptureResult
{
    private PostgresqlCdcProviderBarrierCaptureResult(
        string? postgresqlBarrierLsn,
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        PostgresqlBarrierLsn = postgresqlBarrierLsn;
        BarrierCapturedAt = barrierCapturedAt;
        Diagnostics = diagnostics;
    }

    public string? PostgresqlBarrierLsn { get; }

    public DateTimeOffset BarrierCapturedAt { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => PostgresqlBarrierLsn is not null && Diagnostics.Count == 0;

    public static PostgresqlCdcProviderBarrierCaptureResult Success(
        string postgresqlBarrierLsn,
        DateTimeOffset barrierCapturedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresqlBarrierLsn);

        return new(postgresqlBarrierLsn, barrierCapturedAt, []);
    }

    public static PostgresqlCdcProviderBarrierCaptureResult Failure(
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, barrierCapturedAt, diagnostics);
    }
}

internal sealed record PostgresqlCdcProviderBarrierObservationRequest(
    string OperationId,
    CdcBinding Binding,
    DateTimeOffset ProjectionCaughtUpObservedAt,
    PostgresqlCdcProviderBarrierCaptureResult CapturedBarrier,
    CdcConnectorOffsetObservation ConnectorOffset,
    string? ExpectedConnectSourcePartitionHash = null
);

internal sealed class PostgresqlCdcSourcePositionAdapter(
    NpgsqlDataSourceCache dataSourceCache,
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    TimeProvider timeProvider,
    ILogger<PostgresqlCdcSourcePositionAdapter> logger
) : ICdcProviderSourcePositionAdapter
{
    private const string CurrentWalLsnPath = "$.postgresqlBarrierLsn";

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<PostgresqlCdcSourcePositionAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    CdcProvider ICdcProviderSourcePositionAdapter.Provider => CdcProvider.Postgresql;

    async Task<CdcProviderBarrierCaptureResult> ICdcProviderSourcePositionAdapter.CaptureBarrierAsync(
        CdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        PostgresqlCdcProviderBarrierCaptureResult result = await CaptureBarrierAsync(
                new(request.ConnectionString, request.Binding) { CommandTimeout = request.CommandTimeout },
                cancellationToken
            )
            .ConfigureAwait(false);

        return result.Succeeded
            ? CdcProviderBarrierCaptureResult.PostgresqlSuccess(
                result.PostgresqlBarrierLsn!,
                result.BarrierCapturedAt
            )
            : CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                result.BarrierCapturedAt,
                result.Diagnostics
            );
    }

    CdcProviderBarrierObservation ICdcProviderSourcePositionAdapter.ObserveProviderBarrier(
        CdcProviderBarrierObservationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return ObserveProviderBarrier(
            new(
                request.OperationId,
                request.Binding,
                request.ProjectionCaughtUpObservedAt,
                ToPostgresqlCaptureResult(request.CapturedBarrier),
                request.ConnectorOffset,
                request.ExpectedConnectSourcePartitionHash
            )
        );
    }

    Task<CdcSourceHistoryClassificationResult> ICdcProviderSourcePositionAdapter.ObserveSourceHistoryAsync(
        CdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return ObserveSourceHistoryAsync(request, cancellationToken);
    }

    public async Task<PostgresqlCdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        PostgresqlCdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);

        if (diagnostics.HasDiagnostics)
        {
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }

        try
        {
            NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(request.ConnectionString);
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText = """SELECT pg_catalog.pg_current_wal_lsn()::text;""";
            command.CommandTimeout = GetCommandTimeoutSeconds(request.CommandTimeout);

            string? lsn = (
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            )?.ToString();
            CdcPostgresqlWalPositionResult parseResult = CdcPostgresqlProviderPosition.ParseWalLsn(
                lsn,
                CurrentWalLsnPath
            );
            AddDiagnostics(diagnostics, parseResult.Diagnostics);

            return parseResult.Succeeded && lsn is not null
                ? PostgresqlCdcProviderBarrierCaptureResult.Success(lsn, UtcNow())
                : PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture was cancelled."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-barrier-timeout");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture timed out."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-barrier-failed");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture failed."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
    }

    public CdcProviderBarrierObservation ObserveProviderBarrier(
        PostgresqlCdcProviderBarrierObservationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.CapturedBarrier);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);
        AddDiagnostics(diagnostics, request.CapturedBarrier.Diagnostics);

        string? expectedSourcePartitionHash = ResolveExpectedPostgresqlSourcePartitionHash(
            request.Binding,
            request.ExpectedConnectSourcePartitionHash,
            diagnostics
        );
        CdcContractValidationResult connectorOffsetValidation =
            CdcConnectorOffsetObservationValidator.ValidateForBinding(
                request.ConnectorOffset,
                request.Binding,
                new(
                    request.OperationId,
                    request.Binding.ToTargetIdentity(),
                    request.Binding.PhysicalSourceFingerprint,
                    UtcNow()
                ),
                expectedSourcePartitionHash
            );
        AddDiagnostics(diagnostics, connectorOffsetValidation.Diagnostics);

        CdcProviderBarrierState barrierState = request.CapturedBarrier.Succeeded
            ? CdcProviderBarrierState.NotReached
            : CdcProviderBarrierState.Unknown;
        string? committedPosition = null;

        if (request.CapturedBarrier.PostgresqlBarrierLsn is not null)
        {
            CdcPostgresqlWalPositionResult barrierResult = CdcPostgresqlProviderPosition.ParseWalLsn(
                request.CapturedBarrier.PostgresqlBarrierLsn,
                CurrentWalLsnPath
            );
            AddDiagnostics(diagnostics, barrierResult.Diagnostics);

            if (barrierResult.Position is not null && connectorOffsetValidation.Succeeded)
            {
                CdcProviderPositionComparisonResult comparison =
                    CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                        barrierResult.Position.Value,
                        new(
                            request.ConnectorOffset.SourcePartitionMatchResult,
                            request.ConnectorOffset.IsSnapshot,
                            request.ConnectorOffset.IsNull,
                            request.ConnectorOffset.LsnProc
                        )
                    );
                AddDiagnostics(diagnostics, comparison.Diagnostics);

                if (comparison.Succeeded)
                {
                    barrierState = CdcProviderBarrierState.Reached;
                    committedPosition = comparison.CommittedPosition;
                }
            }
        }

        return new(
            CdcJsonContract.CurrentContractVersion,
            request.OperationId,
            UtcNow(),
            request.Binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            request.Binding.PhysicalSourceFingerprint,
            request.ProjectionCaughtUpObservedAt.ToUniversalTime(),
            request.CapturedBarrier.BarrierCapturedAt.ToUniversalTime(),
            request.ConnectorOffset.ObservedAt.ToUniversalTime(),
            barrierState,
            request.CapturedBarrier.PostgresqlBarrierLsn,
            null,
            null,
            null,
            committedPosition,
            [.. diagnostics.Diagnostics]
        );
    }

    public Task<CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        CdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);
        cancellationToken.ThrowIfCancellationRequested();

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);

        return Task.FromResult(
            CdcSourceHistoryContinuityClassifier.Evaluate(
                new(request.OperationId, UtcNow(), UtcNow(), request.Binding)
                {
                    ProviderSetup = request.ProviderSetup,
                    ConnectorOffset = request.ConnectorOffset,
                    ProviderHistory = request.ProviderHistory,
                    LatchedIncident = request.LatchedIncident,
                    ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
                    Diagnostics = [.. diagnostics.Diagnostics],
                }
            )
        );
    }

    private static PostgresqlCdcProviderBarrierCaptureResult ToPostgresqlCaptureResult(
        CdcProviderBarrierCaptureResult captureResult
    )
    {
        ArgumentNullException.ThrowIfNull(captureResult);

        if (
            captureResult.Provider == CdcProvider.Postgresql
            && captureResult.PostgresqlBarrierLsn is not null
            && captureResult.Diagnostics.Count == 0
        )
        {
            return PostgresqlCdcProviderBarrierCaptureResult.Success(
                captureResult.PostgresqlBarrierLsn,
                captureResult.BarrierCapturedAt
            );
        }

        return PostgresqlCdcProviderBarrierCaptureResult.Failure(
            captureResult.BarrierCapturedAt,
            captureResult.Provider == CdcProvider.Postgresql
                ? captureResult.Diagnostics
                :
                [
                    .. captureResult.Diagnostics,
                    new(
                        CdcDiagnosticCategory.ProviderMismatch,
                        "$.capturedBarrier.provider",
                        "CDC provider barrier capture result provider did not match PostgreSQL."
                    ),
                ]
        );
    }

    private static void ValidatePostgresqlBinding(
        CdcBinding binding,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (binding.Provider != CdcProvider.Postgresql)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC PostgreSQL source-position adapter requires a PostgreSQL binding."
            );
        }
    }

    private static string? ResolveExpectedPostgresqlSourcePartitionHash(
        CdcBinding binding,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (expectedConnectSourcePartitionHash is not null)
        {
            return expectedConnectSourcePartitionHash;
        }

        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        AddDiagnostics(diagnostics, artifactNameResult.Diagnostics);
        if (artifactNameResult.Inventory is null)
        {
            return null;
        }

        CdcSourcePartitionHashResult sourcePartitionHash = CdcSourcePartitionHashCalculator.ComputePostgresql(
            artifactNameResult.Inventory.TopicPrefix
        );
        AddDiagnostics(diagnostics, sourcePartitionHash.Diagnostics);

        return sourcePartitionHash.Hash;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static int GetCommandTimeoutSeconds(TimeSpan timeout)
    {
        double timeoutSeconds = Math.Ceiling(timeout.TotalSeconds);
        if (timeoutSeconds < 1)
        {
            return 1;
        }

        return timeoutSeconds > int.MaxValue ? int.MaxValue : (int)timeoutSeconds;
    }

    private void LogProviderObservationFailure(Exception exception, string outcome)
    {
        _logger.LogDebug(
            "PostgreSQL CDC source-position observation failed with outcome {Outcome}; exception type {ExceptionType}",
            outcome,
            exception.GetType().Name
        );
    }

    private static void AddDiagnostics(
        CdcDiagnosticCollector collector,
        IReadOnlyList<CdcDiagnostic>? diagnostics
    )
    {
        if (diagnostics is null)
        {
            return;
        }

        foreach (CdcDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic is not null))
        {
            collector.Add(diagnostic);
        }
    }
}
