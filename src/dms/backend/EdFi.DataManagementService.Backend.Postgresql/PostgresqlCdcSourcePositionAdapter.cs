// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

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

    public async Task<CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        CdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);

        if (diagnostics.HasDiagnostics)
        {
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

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
                ? CdcProviderBarrierCaptureResult.PostgresqlSuccess(lsn, UtcNow())
                : CdcProviderBarrierCaptureResult.Failure(
                    CdcProvider.Postgresql,
                    UtcNow(),
                    diagnostics.Diagnostics
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture was cancelled."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-barrier-timeout");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture timed out."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-barrier-failed");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture failed."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
    }

    public CdcProviderBarrierObservation ObserveProviderBarrier(CdcProviderBarrierObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.CapturedBarrier);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);
        AddDiagnostics(diagnostics, request.CapturedBarrier.Diagnostics);
        ValidatePostgresqlCaptureResult(request.CapturedBarrier, "$.capturedBarrier.provider", diagnostics);

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

        CdcProviderBarrierState barrierState = CdcProviderBarrierState.Unknown;
        string? committedPosition = null;

        if (!diagnostics.HasDiagnostics && request.CapturedBarrier.Succeeded)
        {
            CdcPostgresqlWalPositionResult barrierResult = CdcPostgresqlProviderPosition.ParseWalLsn(
                request.CapturedBarrier.PostgresqlBarrierLsn,
                CurrentWalLsnPath
            );
            AddDiagnostics(diagnostics, barrierResult.Diagnostics);

            if (barrierResult.Position is not null && !diagnostics.HasDiagnostics)
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
                else if (IsKnownNotReached(comparison))
                {
                    barrierState = CdcProviderBarrierState.NotReached;
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

    private static void ValidatePostgresqlCaptureResult(
        CdcProviderBarrierCaptureResult captureResult,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (captureResult.Provider != CdcProvider.Postgresql)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC provider barrier capture result provider did not match PostgreSQL."
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
            artifactNameResult.Inventory.ConnectorName
        );
        AddDiagnostics(diagnostics, sourcePartitionHash.Diagnostics);

        return sourcePartitionHash.Hash;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static bool IsKnownNotReached(CdcProviderPositionComparisonResult comparison) =>
        comparison.Diagnostics.Count > 0
        && comparison.Diagnostics.All(diagnostic =>
            diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering
        );

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
