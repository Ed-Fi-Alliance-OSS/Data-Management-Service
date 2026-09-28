// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Bounded unavailability or captured healthy replay affects only committed-offset evidence.
/// Other calls and reads outside the interval delegate to the real transport; offsets are never repaired.</summary>
internal sealed class CdcOffsetEvidenceTransport(ICdcConnectTransport inner) : ICdcConnectTransport
{
    private readonly object _sync = new();
    private bool _active;
    private CancellationToken _faultToken;
    private int _unavailableReads;
    private CdcConnectOffsetEvidence _healthyEvidence = null!;
    private int _replayedReads;
    private int _startCalls;
    public int ReplayedReads => Volatile.Read(ref _replayedReads);
    public int StartCalls => Volatile.Read(ref _startCalls);
    public int UnavailableReads => Volatile.Read(ref _unavailableReads);

    public async Task RunUnavailableAsync(
        Func<CancellationToken, Task> action,
        TimeSpan duration,
        CancellationToken token
    ) => await RunAsync(action, duration, token, null!);

    public Task RunWithHealthyEvidenceAsync(
        CdcConnectOffsetEvidence evidence,
        Func<CancellationToken, Task> action,
        TimeSpan duration,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.State != CdcConnectOffsetState.Streaming)
        {
            throw new ArgumentException("CDC_API_EXPECTED_HEALTHY_OFFSET", nameof(evidence));
        }
        return RunAsync(action, duration, token, evidence);
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> action,
        TimeSpan duration,
        CancellationToken token,
        CdcConnectOffsetEvidence healthyEvidence
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, TimeSpan.FromMinutes(5));
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(duration);
        lock (_sync)
        {
            if (_active)
            {
                throw new InvalidOperationException("CDC_API_OFFSET_FAULT_ALREADY_ACTIVE");
            }
            _healthyEvidence = healthyEvidence;
            _faultToken = timeout.Token;
            _active = true;
        }
        try
        {
            await action(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_sync)
            {
                _active = false;
                _healthyEvidence = null!;
            }
        }
    }

    public Task<CdcTransportResult<CdcConnectOffsetEvidence>> ReadOffsetEvidenceAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_active && !_faultToken.IsCancellationRequested)
            {
                if (_healthyEvidence is not null)
                {
                    Interlocked.Increment(ref _replayedReads);
                    return Task.FromResult<CdcTransportResult<CdcConnectOffsetEvidence>>(
                        new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(_healthyEvidence)
                    );
                }
                Interlocked.Increment(ref _unavailableReads);
                // Matches the existing native-recovery transport fault; production classifies it.
                throw new IOException("CDC_API_OFFSET_EVIDENCE_UNAVAILABLE");
            }
        }
        return inner.ReadOffsetEvidenceAsync(request, cancellationToken);
    }

    public Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ReadConfigurationAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.ReadConfigurationAsync(request, cancellationToken);

    public Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ValidateConfigurationAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) => inner.ValidateConfigurationAsync(request, payload, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> CreateAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) => inner.CreateAsync(request, payload, cancellationToken);

    public Task<CdcTransportResult<JsonElement>> ReadOffsetsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.ReadOffsetsAsync(request, cancellationToken);

    public Task<CdcTransportResult<CdcConnectStatus>> ReadStatusAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.ReadStatusAsync(request, cancellationToken);

    public Task<
        CdcTransportResult<CdcTransportAcknowledgement>
    > UpdateConfigurationForRecordSizeIncreaseAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    ) => inner.UpdateConfigurationForRecordSizeIncreaseAsync(request, payload, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> ResumeAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _startCalls);
        return inner.ResumeAsync(request, cancellationToken);
    }

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteOffsetsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.DeleteOffsetsAsync(request, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> RestartAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _startCalls);
        return inner.RestartAsync(request, cancellationToken);
    }

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> StopAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.StopAsync(request, cancellationToken);

    public Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) => inner.DeleteAsync(request, cancellationToken);
}
