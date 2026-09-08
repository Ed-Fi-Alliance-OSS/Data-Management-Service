// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// One target's evaluation, never a persisted authorization. Create a new instance for every pass,
/// even when retrying the same operation ID. Dispose on completion; invalidate immediately on any
/// observed recovery/reassignment. Only one collection is allowed, including after failed collection.
/// </summary>
public sealed class CdcTelemetryObservationPass : IDisposable
{
    private int _claimed;
    private int _invalidated;

    public CdcTelemetryObservationPass(
        CdcDeploymentRequest request,
        string operationId,
        long thresholdMilliseconds
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        // Reuse the Core envelope validator rather than defining another operation-token grammar.
        var probe = new CoreCdc.CdcConnectorLagObservation(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            operationId,
            DateTimeOffset.UtcNow,
            request.TargetIdentity,
            request.Binding.Provider,
            request.Binding.PhysicalSourceFingerprint,
            CoreCdc.CdcConnectorLagState.Unknown,
            null,
            thresholdMilliseconds,
            null,
            null,
            null,
            []
        );
        if (
            !CoreCdc
                .CdcConnectorLagObservationValidator.Validate(
                    probe,
                    new(
                        operationId,
                        request.TargetIdentity,
                        request.Binding.PhysicalSourceFingerprint,
                        probe.ObservedAt
                    )
                )
                .Succeeded
        )
        {
            throw new ArgumentException("CDC telemetry evaluation scope is invalid.");
        }
        Request = request;
        OperationId = operationId;
        ThresholdMilliseconds = thresholdMilliseconds;
    }

    internal CdcDeploymentRequest Request { get; }
    internal string OperationId { get; }
    internal long ThresholdMilliseconds { get; }
    internal bool IsValid => Volatile.Read(ref _invalidated) == 0;

    internal bool Claim(CdcDeploymentRequest request) =>
        ReferenceEquals(Request, request) && IsValid && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    public void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

    public void Dispose() => Invalidate();

    public override string ToString() => nameof(CdcTelemetryObservationPass);
}

/// <summary>
/// In-memory collection receipt. ReadForEvaluation must be called immediately at each readiness use,
/// including writer handoff after barrier/projection work. Do not retain the returned Core snapshot.
/// The owning controller must also invalidate its pass when it observes native or managed recovery.
/// </summary>
public sealed class CdcConnectorTelemetryObservation
{
    private readonly CdcTelemetryObservationPass _pass;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly CoreCdc.CdcConnectorLagObservation _observation;
    private readonly CdcConnectorTelemetryStatistics _statistics;

    internal CdcConnectorTelemetryObservation(
        CdcTelemetryObservationPass pass,
        TimeProvider clock,
        long started,
        DateTimeOffset collectionStartedAt,
        DateTimeOffset collectionCompletedAt,
        CoreCdc.CdcConnectorLagObservation observation,
        CdcConnectorTelemetryStatistics statistics
    )
    {
        _pass = pass;
        _clock = clock;
        _started = started;
        _observation = observation;
        _statistics = statistics;
        CollectionStartedAt = collectionStartedAt;
        CollectionCompletedAt = collectionCompletedAt;
    }

    [JsonIgnore]
    public DateTimeOffset CollectionStartedAt { get; }

    [JsonIgnore]
    public DateTimeOffset CollectionCompletedAt { get; }

    public CoreCdc.CdcConnectorLagObservation ReadForEvaluation(CdcTelemetryObservationPass pass)
    {
        return IsFresh(pass)
            ? _observation
            : _observation with
            {
                LagState = CoreCdc.CdcConnectorLagState.Unknown,
                CurrentLagMilliseconds = null,
                P50LagMilliseconds = null,
                P95LagMilliseconds = null,
                P99LagMilliseconds = null,
            };
    }

    public CdcConnectorTelemetryStatistics ReadStatisticsForEvaluation(CdcTelemetryObservationPass pass) =>
        IsFresh(pass) ? _statistics : new(null, null, null);

    private bool IsFresh(CdcTelemetryObservationPass pass)
    {
        var age = _clock.GetElapsedTime(_started);
        return ReferenceEquals(pass, _pass)
            && _pass.IsValid
            && age >= TimeSpan.Zero
            && age <= _pass.Request.Timing.MaximumObservationAge;
    }

    public override string ToString() => nameof(CdcConnectorTelemetryObservation);
}

/// <summary>Optional diagnostics only; no statistics are synthesized from successive scrapes.</summary>
public sealed record CdcConnectorTelemetryStatistics(
    double? MinimumMilliseconds,
    double? MaximumMilliseconds,
    double? AverageMilliseconds
);
