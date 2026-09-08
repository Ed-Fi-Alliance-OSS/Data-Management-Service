// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using Microsoft.Extensions.Configuration;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// In-memory controller input. This is neither ownership evidence nor a persisted workflow journal.
/// Hosts convert provider configuration tokens using the existing Core configuration contracts.
/// Runtime target resolution must independently verify actual DocumentCache:Targets membership.
/// </summary>
public sealed class CdcDeploymentRequest
{
    public CdcDeploymentRequest(
        CoreCdc.CdcBinding binding,
        IConfiguration dmsSettings,
        CdcProviderSetupRequest providerSetup,
        Uri connectEndpoint,
        Uri workerMetricsEndpoint,
        CdcConnectorTemplateDeploymentPolicy connectorPolicy,
        CdcWorkerDeploymentPolicy workerPolicy,
        CdcProviderConnectionProperties providerConnectionProperties,
        CdcKafkaClientSecurityProperties kafkaClientSecurityProperties,
        CdcDeploymentTiming timing
    )
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(dmsSettings);
        ArgumentNullException.ThrowIfNull(providerSetup);
        ArgumentNullException.ThrowIfNull(connectorPolicy);
        ArgumentNullException.ThrowIfNull(workerPolicy);
        ArgumentNullException.ThrowIfNull(providerConnectionProperties);
        ArgumentNullException.ThrowIfNull(kafkaClientSecurityProperties);
        ArgumentNullException.ThrowIfNull(timing);

        // Do not copy validation messages containing operator-supplied text into workflow diagnostics.
        if (!CoreCdc.CdcBindingValidator.Validate(binding).Succeeded)
        {
            throw new ArgumentException("CDC deployment binding is invalid.", nameof(binding));
        }

        CdcConnectorTemplateBindingArtifacts artifacts = CdcConnectorTemplateBindingArtifacts.From(
            binding,
            nameof(binding)
        );
        if (
            providerSetup.Provider != artifacts.Provider
            || providerConnectionProperties.Provider != artifacts.Provider
            || providerSetup.BoundPhysicalSourceFingerprint != artifacts.BoundPhysicalSourceFingerprint
            || !Enum.IsDefined(providerSetup.Mode)
        )
        {
            throw new ArgumentException(
                "CDC deployment provider or source identity does not match the binding.",
                nameof(providerSetup)
            );
        }

        if (!ArtifactNamesMatch(providerSetup.ArtifactNames, artifacts.ProviderArtifactNames))
        {
            throw new ArgumentException(
                "CDC provider artifact names do not match the binding.",
                nameof(providerSetup)
            );
        }

        if (
            providerConnectionProperties
                .Properties.Concat(kafkaClientSecurityProperties.Properties)
                .Any(property =>
                    !CdcConnectorTemplateInputValidator.HasExternalizedSecretReferenceIfRequired(
                        property.Key,
                        property.Value
                    )
                )
        )
        {
            throw new ArgumentException("CDC connector credentials require externalized secret references.");
        }

        int producerBufferBytes =
            connectorPolicy.ProducerBufferBytes
            ?? Math.Max(
                CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes,
                connectorPolicy.MaxRecordBytes
            );
        if (workerPolicy.HeapBytes <= producerBufferBytes)
        {
            throw new ArgumentException(
                "CDC worker heap must exceed the producer buffer budget.",
                nameof(workerPolicy)
            );
        }

        if (
            artifacts.ArtifactInventory.GovernedArtifacts.Any(artifact =>
                artifact.Name == workerPolicy.OffsetStorageTopic.Value
            )
        )
        {
            throw new ArgumentException(
                "CDC shared offset storage must be separate from binding artifacts.",
                nameof(workerPolicy)
            );
        }

        Binding = binding;
        DmsSettings = dmsSettings;
        ProviderSetup = providerSetup;
        ConnectEndpoint = ValidateEndpoint(connectEndpoint, nameof(connectEndpoint));
        WorkerMetricsEndpoint = ValidateEndpoint(workerMetricsEndpoint, nameof(workerMetricsEndpoint));
        ConnectorPolicy = connectorPolicy;
        WorkerPolicy = workerPolicy;
        ProviderConnectionProperties = providerConnectionProperties;
        KafkaClientSecurityProperties = kafkaClientSecurityProperties;
        Timing = timing;
    }

    public CoreCdc.CdcBinding Binding { get; }
    public CoreCdc.CdcTargetIdentity TargetIdentity => Binding.ToTargetIdentity();

    [JsonIgnore]
    public IConfiguration DmsSettings { get; }

    [JsonIgnore]
    public CdcProviderSetupRequest ProviderSetup { get; }

    [JsonIgnore]
    public Uri ConnectEndpoint { get; }

    [JsonIgnore]
    public Uri WorkerMetricsEndpoint { get; }

    [JsonIgnore]
    public CdcConnectorTemplateDeploymentPolicy ConnectorPolicy { get; }

    [JsonIgnore]
    public CdcWorkerDeploymentPolicy WorkerPolicy { get; }

    [JsonIgnore]
    public CdcProviderConnectionProperties ProviderConnectionProperties { get; }

    [JsonIgnore]
    public CdcKafkaClientSecurityProperties KafkaClientSecurityProperties { get; }

    public CdcDeploymentTiming Timing { get; }

    /// <summary>Fresh provider evidence is required; the template service owns render/live validation.</summary>
    public CdcConnectorTemplateRequest CreateTemplateRequest(CdcConnectorProviderSetupEvidence evidence) =>
        new(Binding, evidence, ConnectorPolicy, ProviderConnectionProperties, KafkaClientSecurityProperties);

    public override string ToString() => nameof(CdcDeploymentRequest);

    private static Uri ValidateEndpoint(Uri endpoint, string parameterName)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(parameterName);
        }
        if (
            !endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https")
            || endpoint.UserInfo.Length > 0
            || endpoint.Query.Length > 0
            || endpoint.Fragment.Length > 0
        )
        {
            throw new ArgumentException(
                "CDC endpoint must be an absolute HTTP(S) URI without credentials, query, or fragment.",
                parameterName
            );
        }
        return endpoint;
    }

    private static bool ArtifactNamesMatch(
        CdcProviderArtifactNames actual,
        CdcProviderArtifactNames expected
    ) =>
        actual.Postgresql == expected.Postgresql
        && (
            actual.SqlServer is null && expected.SqlServer is null
            || actual.SqlServer is not null
                && expected.SqlServer is not null
                && actual.SqlServer.GatingRoleName == expected.SqlServer.GatingRoleName
                && actual
                    .SqlServer.CaptureInstanceNames.OrderBy(pair => pair.Key)
                    .SequenceEqual(expected.SqlServer.CaptureInstanceNames.OrderBy(pair => pair.Key))
        );
}

/// <summary>Finite operational bounds, independent of provider heartbeat/template policy.</summary>
public sealed class CdcDeploymentTiming
{
    public static readonly TimeSpan MaximumCallTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumWaitTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumTelemetryAge = TimeSpan.FromMinutes(1);

    public CdcDeploymentTiming(
        TimeSpan callTimeout,
        TimeSpan waitTimeout,
        TimeSpan pollInterval,
        TimeSpan? maximumObservationAge = null
    )
    {
        CallTimeout = Bounded(callTimeout, MaximumCallTimeout, nameof(callTimeout));
        WaitTimeout = Bounded(waitTimeout, MaximumWaitTimeout, nameof(waitTimeout));
        PollInterval = Bounded(pollInterval, MaximumPollInterval, nameof(pollInterval));
        MaximumObservationAge = Bounded(
            maximumObservationAge ?? TimeSpan.FromSeconds(10),
            MaximumTelemetryAge,
            nameof(maximumObservationAge)
        );
        if (CallTimeout > WaitTimeout || PollInterval > WaitTimeout)
        {
            throw new ArgumentException(
                "CDC call timeout and poll interval must fit within the complete wait timeout."
            );
        }
    }

    public TimeSpan CallTimeout { get; }
    public TimeSpan WaitTimeout { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan MaximumObservationAge { get; }

    private static TimeSpan Bounded(TimeSpan value, TimeSpan maximum, string parameterName) =>
        value >= TimeSpan.FromMilliseconds(1) && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                "CDC intervals must be at least one millisecond and within the supported maximum."
            );
}

public enum CdcKafkaDurabilityProfile
{
    LocalSingleBroker,
    Production,
}

public enum CdcKafkaAuthorizationProfile
{
    AuthorizationDisabledLocal,
    AuthorizationEnabled,
}

/// <summary>Operator policy, never a substitute for live worker, broker, or ACL evidence.</summary>
public sealed class CdcWorkerDeploymentPolicy
{
    public CdcWorkerDeploymentPolicy(
        CdcSafeName workerKey,
        CdcSafeName offsetStorageTopic,
        string qualifiedImageDigest,
        long heapBytes,
        string clientConfigurationOverridePolicy,
        CdcKafkaDurabilityProfile durabilityProfile,
        CdcKafkaAuthorizationProfile authorizationProfile,
        CdcSafeName workerPrincipal,
        IReadOnlyList<CdcConsumerAccess> consumers
    )
    {
        ArgumentNullException.ThrowIfNull(consumers);
        if (
            !Enum.IsDefined(durabilityProfile)
            || !Enum.IsDefined(authorizationProfile)
            || durabilityProfile == CdcKafkaDurabilityProfile.Production
                && authorizationProfile != CdcKafkaAuthorizationProfile.AuthorizationEnabled
        )
        {
            throw new ArgumentException(
                "CDC deployment requires an explicit compatible durability and authorization profile."
            );
        }
        if (
            qualifiedImageDigest is null
            || !qualifiedImageDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || qualifiedImageDigest.Length != 71
            || qualifiedImageDigest[7..]
                .Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
        )
        {
            throw new ArgumentException(
                "CDC worker requires a qualified immutable SHA-256 image digest.",
                nameof(qualifiedImageDigest)
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(clientConfigurationOverridePolicy);
        if (heapBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heapBytes), "CDC worker heap must be positive.");
        }
        // Safe-name value types can be default-initialized; revalidate at the request boundary.
        WorkerKey = new CdcSafeName(workerKey.Value);
        OffsetStorageTopic = new CdcSafeName(offsetStorageTopic.Value);
        WorkerPrincipal = new CdcSafeName(workerPrincipal.Value);
        QualifiedImageDigest = qualifiedImageDigest;
        HeapBytes = heapBytes;
        ClientConfigurationOverridePolicy = clientConfigurationOverridePolicy;
        DurabilityProfile = durabilityProfile;
        AuthorizationProfile = authorizationProfile;
        Consumers = Array.AsReadOnly(
            consumers
                .Select(consumer =>
                {
                    ArgumentNullException.ThrowIfNull(consumer);
                    return new CdcConsumerAccess(
                        new CdcSafeName(consumer.Principal.Value),
                        new CdcSafeName(consumer.Group.Value)
                    );
                })
                .Distinct()
                .ToArray()
        );
    }

    public CdcSafeName WorkerKey { get; }
    public CdcSafeName OffsetStorageTopic { get; }
    public string QualifiedImageDigest { get; }
    public long HeapBytes { get; }
    public string ClientConfigurationOverridePolicy { get; }
    public CdcKafkaDurabilityProfile DurabilityProfile { get; }
    public CdcKafkaAuthorizationProfile AuthorizationProfile { get; }
    public CdcSafeName WorkerPrincipal { get; }
    public IReadOnlyList<CdcConsumerAccess> Consumers { get; }

    public override string ToString() => nameof(CdcWorkerDeploymentPolicy);
}

public sealed record CdcConsumerAccess(CdcSafeName Principal, CdcSafeName Group);
