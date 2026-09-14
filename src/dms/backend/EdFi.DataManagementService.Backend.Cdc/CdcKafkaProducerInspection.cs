// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Reads effective overrides from Connect and heap from deployment authority. Correlates worker
/// identity around the read; never substitutes requested values, worker defaults or template output.
/// Full connector/worker validation and recovery detection remain the lifecycle controller's gates.
/// </summary>
public sealed class CdcKafkaProducerInspection(
    ICdcConnectTransport connect,
    ICdcWorkerInspectionTransport worker
) : ICdcKafkaProducerInspection
{
    public async Task<CdcTransportResult<CdcKafkaProducerCapacityEvidence>> InspectAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timing.CallTimeout);
        var token = timeout.Token;
        var component = CdcDeploymentComponent.Worker;
        try
        {
            var before = await worker.InspectAsync(request, token).WaitAsync(token);
            if (
                before is not CdcTransportResult<CdcWorkerInspection>.Observed first
                || !Matches(first.Value, request)
            )
            {
                return Unavailable(component);
            }
            component = CdcDeploymentComponent.Connect;
            var configuration = await connect.ReadConfigurationAsync(request, token).WaitAsync(token);
            if (
                configuration is not CdcTransportResult<IReadOnlyDictionary<string, string>>.Observed current
                || !Number(current.Value, "producer.override.max.request.size", out long maxRequest)
                || !Number(current.Value, "producer.override.buffer.memory", out long buffer)
            )
            {
                return Unavailable(component);
            }
            component = CdcDeploymentComponent.Worker;
            var after = await worker.InspectAsync(request, token).WaitAsync(token);
            if (
                after is not CdcTransportResult<CdcWorkerInspection>.Observed last
                || !Matches(last.Value, request)
                || first.Value.ProcessIdentity != last.Value.ProcessIdentity
                || first.Value.ImageDigest != last.Value.ImageDigest
                || first.Value.HeapBytes != last.Value.HeapBytes
            )
            {
                return Unavailable(component);
            }
            token.ThrowIfCancellationRequested();
            return new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Observed(
                new(maxRequest, buffer, last.Value.HeapBytes)
            );
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Unavailable(
                new(component, CdcDeploymentFailure.Timeout)
            );
        }
        catch (Exception exception)
        {
            return new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Unavailable(
                CdcDeploymentDiagnostic.FromException(component, exception)
            );
        }
    }

    private static bool Matches(CdcWorkerInspection evidence, CdcDeploymentRequest request) =>
        !string.IsNullOrWhiteSpace(evidence.ProcessIdentity)
        && !string.IsNullOrWhiteSpace(evidence.ImageDigest)
        && evidence.MetricsEndpoint == request.WorkerMetricsEndpoint
        && evidence.HeapBytes > 0
        && evidence.EffectiveConfiguration.TryGetValue("offset.storage.topic", out var topic)
        && topic == request.WorkerPolicy.OffsetStorageTopic.Value;

    private static bool Number(IReadOnlyDictionary<string, string> configuration, string key, out long value)
    {
        value = 0;
        return configuration.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static CdcTransportResult<CdcKafkaProducerCapacityEvidence> Unavailable(
        CdcDeploymentComponent component
    ) =>
        new CdcTransportResult<CdcKafkaProducerCapacityEvidence>.Unavailable(
            new(component, CdcDeploymentFailure.Unavailable)
        );
}
