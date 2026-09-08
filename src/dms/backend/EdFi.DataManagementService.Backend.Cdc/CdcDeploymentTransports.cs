// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Controller facade over broker transport. Setup authorizes and journals eligible effects; observe
/// never repairs. Both methods return fresh policy evidence, not readiness authorization. Shared
/// offset preparation precedes worker startup and has no binding cleanup operation. Callers evaluate
/// both offset and binding policy before registration/readiness. Calls and complete passes are bounded.
/// </summary>
public interface ICdcKafkaAdministrationTransport
{
    Task<CdcTransportResult<CoreCdc.CdcConnectOffsetStorePolicyObservation>> ObserveOffsetStoreAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CoreCdc.CdcConnectOffsetStorePolicyObservation>> ProvisionOffsetStoreAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CoreCdc.CdcKafkaPolicyObservation>> ObserveBindingAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CoreCdc.CdcKafkaPolicyObservation>> ProvisionBindingAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Connect REST transport; no unconditional upsert. Create conflicts/timeouts require read-back.
/// Offset JSON is passed to Core's provider parser and source-partition hash calculator.
/// HTTP success for a mutation is only acknowledgement, not authoritative completion evidence.
/// </summary>
public interface ICdcConnectTransport
{
    Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ReadConfigurationAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<IReadOnlyDictionary<string, string>>> ValidateConfigurationAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcTransportAcknowledgement>> CreateAsync(
        CdcDeploymentRequest request,
        CdcKafkaConnectRegistrationPayload payload,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CoreCdc.CdcConnectorRuntimeObservation>> ReadRuntimeAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<JsonElement>> ReadOffsetsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcTransportAcknowledgement>> RestartAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcTransportAcknowledgement>> StopAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcTransportAcknowledgement>> DeleteAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
}

public sealed record CdcTransportAcknowledgement;

/// <summary>Deployment authority supplies process identity and effective worker configuration.</summary>
public interface ICdcWorkerInspectionTransport
{
    Task<CdcTransportResult<CdcWorkerInspection>> InspectAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Raw worker evidence is deliberately excluded from workflow serialization and logging.</summary>
public sealed class CdcWorkerInspection(
    string processIdentity,
    Uri metricsEndpoint,
    IReadOnlyDictionary<string, string> effectiveConfiguration,
    string imageDigest,
    long heapBytes
)
{
    [JsonIgnore]
    public string ProcessIdentity { get; } = processIdentity;

    [JsonIgnore]
    public Uri MetricsEndpoint { get; } = metricsEndpoint;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> EffectiveConfiguration { get; } = effectiveConfiguration;

    [JsonIgnore]
    public string ImageDigest { get; } = imageDigest;

    [JsonIgnore]
    public long HeapBytes { get; } = heapBytes;

    public override string ToString() => nameof(CdcWorkerInspection);
}

/// <summary>
/// Collects fresh, worker/task-correlated lag evidence using worker inspection and Connect status
/// before and after the scrape. Implementations enforce maximum observation age and cancellation.
/// Core's existing lag evaluator determines readiness; telemetry does not replace a provider barrier.
/// </summary>
public interface ICdcWorkerMetricsTransport
{
    Task<CdcTransportResult<CoreCdc.CdcConnectorLagObservation>> ObserveAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
}
