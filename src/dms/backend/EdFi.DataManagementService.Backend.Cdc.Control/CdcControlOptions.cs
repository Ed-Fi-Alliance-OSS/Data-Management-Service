// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Durability profile the deployment operates under. The profile selects the replication and
/// in-sync-replica expectations that governed Kafka topics are created with and validated against.
/// </summary>
public enum CdcDurabilityProfile
{
    Local,
    Production,
}

/// <summary>
/// Deployment and runtime policy inputs owned by the CDC control plane. These are the inputs the
/// connector-template story assigned to its caller rather than to template rendering.
/// </summary>
public sealed class CdcControlOptions
{
    public const string SectionName = $"{DocumentCacheOptions.SectionName}:Cdc";

    public const string LocalDurabilityProfile = "local";

    public const string ProductionDurabilityProfile = "production";

    /// <summary>Opaque deployment key contributing to governed artifact names.</summary>
    public string DeploymentKey { get; set; } = string.Empty;

    /// <summary>Opaque instance key contributing to governed artifact names.</summary>
    public string InstanceKey { get; set; } = string.Empty;

    /// <summary>Topic prefix contributing to governed topic names.</summary>
    public string TopicPrefix { get; set; } = string.Empty;

    /// <summary>Binding generation. A new generation never reuses a prior generation's artifacts.</summary>
    public long Generation { get; set; }

    /// <summary>Fixed partition count for the binding's public topic.</summary>
    public int PartitionCount { get; set; }

    public string KafkaBootstrapServers { get; set; } = string.Empty;

    public string ConnectBaseUri { get; set; } = string.Empty;

    /// <summary>Identifies the Connect worker group whose offset store is validated.</summary>
    public string ConnectWorkerKey { get; set; } = string.Empty;

    /// <summary>
    /// Cluster-scoped Connect offset storage topic. It is shared, is never deleted, and never
    /// appears in per-binding teardown.
    /// </summary>
    public string ConnectOffsetStorageTopic { get; set; } = string.Empty;

    /// <summary>Either <c>local</c> or <c>production</c>.</summary>
    public string DurabilityProfile { get; set; } = string.Empty;

    /// <summary>
    /// Largest record the pipeline must carry end to end. Required with no default: it drives topic
    /// configuration, producer overrides, and broker-limit verification, so an absent value fails closed.
    /// </summary>
    public int MaxRecordBytes { get; set; }

    /// <summary>
    /// Optional producer buffer override. When supplied it must be at least
    /// <c>max(<see cref="CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes"/>, <see cref="MaxRecordBytes"/>)</c>.
    /// </summary>
    public int? ProducerBufferBytes { get; set; }

    public TimeSpan? HeartbeatInterval { get; set; }

    public TimeSpan? SqlServerPollInterval { get; set; }

    /// <summary>Connector lag at or below this threshold is acceptable for write admission.</summary>
    public TimeSpan LagThreshold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// False for a local broker with no authorizer, in which case ACL evidence is reported as
    /// not applicable rather than satisfied.
    /// </summary>
    public bool AclsEnabled { get; set; }

    public string ConnectorPrincipal { get; set; } = string.Empty;

    /// <summary>
    /// Deployment-supplied consumer principals. An empty list is valid for local and no-consumer
    /// deployments.
    /// </summary>
    public IList<string> ConsumerPrincipals { get; set; } = new List<string>();

    public string ConnectWorkerPrincipal { get; set; } = string.Empty;

    public IDictionary<string, string> ProviderConnectionProperties { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> KafkaClientSecurityProperties { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Base URL of the running DMS whose projector supplies caught-up evidence.</summary>
    public string DmsBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bearer token whose role claim satisfies the DocumentCache status endpoint's authorization.
    /// Never emitted in diagnostics, telemetry, or serialized output.
    /// </summary>
    [JsonIgnore]
    public string DmsBearerToken { get; set; } = string.Empty;

    public CdcControlTimeoutOptions Timeouts { get; set; } = new();

    /// <summary>Parses <see cref="DurabilityProfile"/>, accepting only the two defined tokens.</summary>
    public static bool TryParseDurabilityProfile(
        string? value,
        [NotNullWhen(true)] out CdcDurabilityProfile? durabilityProfile
    )
    {
        if (string.Equals(value, LocalDurabilityProfile, StringComparison.OrdinalIgnoreCase))
        {
            durabilityProfile = CdcDurabilityProfile.Local;
            return true;
        }

        if (string.Equals(value, ProductionDurabilityProfile, StringComparison.OrdinalIgnoreCase))
        {
            durabilityProfile = CdcDurabilityProfile.Production;
            return true;
        }

        durabilityProfile = null;
        return false;
    }

    /// <summary>
    /// Projects the record-size and interval policy onto the connector-template contract that owns
    /// those rules, so the control plane never restates them.
    /// </summary>
    public CdcConnectorTemplateDeploymentPolicy ToDeploymentPolicy() =>
        new(
            KafkaBootstrapServers,
            MaxRecordBytes,
            ProducerBufferBytes,
            HeartbeatInterval,
            SqlServerPollInterval
        );

    public CdcProviderConnectionProperties ToProviderConnectionProperties(CdcProvider provider) =>
        new(provider, new Dictionary<string, string>(ProviderConnectionProperties, StringComparer.Ordinal));

    public CdcKafkaClientSecurityProperties ToKafkaClientSecurityProperties() =>
        new(new Dictionary<string, string>(KafkaClientSecurityProperties, StringComparer.Ordinal));
}

/// <summary>
/// Per-step timeouts. A step that times out returns a fail-closed result; elapsed time never
/// substitutes for evidence.
/// </summary>
public sealed class CdcControlTimeoutOptions
{
    public TimeSpan EligibilityProbe { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan KafkaAdmin { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectRequest { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StatusEndpoint { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ProviderSetup { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProjectionCaughtUp { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ProviderBarrier { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class CdcControlOptionsValidator : IValidateOptions<CdcControlOptions>
{
    public ValidateOptionsResult Validate(string? name, CdcControlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        ValidateArtifactIdentity(options, failures);
        ValidateEndpoints(options, failures);
        ValidateRecordSize(options, failures);
        ValidateIntervals(options, failures);
        ValidateAcls(options, failures);
        ValidateProjectionStatusAccess(options, failures);
        ValidateTimeouts(options.Timeouts, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateArtifactIdentity(CdcControlOptions options, List<string> failures)
    {
        RequireText(options.DeploymentKey, nameof(CdcControlOptions.DeploymentKey), failures);
        RequireText(options.InstanceKey, nameof(CdcControlOptions.InstanceKey), failures);
        RequireText(options.TopicPrefix, nameof(CdcControlOptions.TopicPrefix), failures);

        if (options.Generation <= 0)
        {
            failures.Add($"{nameof(CdcControlOptions.Generation)} must be positive.");
        }

        if (options.PartitionCount <= 0)
        {
            failures.Add($"{nameof(CdcControlOptions.PartitionCount)} must be positive.");
        }
    }

    private static void ValidateEndpoints(CdcControlOptions options, List<string> failures)
    {
        RequireText(options.KafkaBootstrapServers, nameof(CdcControlOptions.KafkaBootstrapServers), failures);
        RequireText(options.ConnectWorkerKey, nameof(CdcControlOptions.ConnectWorkerKey), failures);
        RequireText(
            options.ConnectOffsetStorageTopic,
            nameof(CdcControlOptions.ConnectOffsetStorageTopic),
            failures
        );
        RequireHttpUri(options.ConnectBaseUri, nameof(CdcControlOptions.ConnectBaseUri), failures);
    }

    private static void ValidateRecordSize(CdcControlOptions options, List<string> failures)
    {
        if (!CdcControlOptions.TryParseDurabilityProfile(options.DurabilityProfile, out _))
        {
            failures.Add(
                $"{nameof(CdcControlOptions.DurabilityProfile)} must be one of: "
                    + $"{CdcControlOptions.LocalDurabilityProfile}, {CdcControlOptions.ProductionDurabilityProfile}."
            );
        }

        if (options.MaxRecordBytes <= 0)
        {
            failures.Add(
                $"{nameof(CdcControlOptions.MaxRecordBytes)} must be specified and positive; it has no default."
            );
            return;
        }

        if (options.ProducerBufferBytes is not { } producerBufferBytes)
        {
            return;
        }

        int minimumProducerBufferBytes = Math.Max(
            CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes,
            options.MaxRecordBytes
        );

        if (producerBufferBytes < minimumProducerBufferBytes)
        {
            failures.Add(
                $"{nameof(CdcControlOptions.ProducerBufferBytes)} must be greater than or equal to "
                    + $"{minimumProducerBufferBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}."
            );
        }
    }

    private static void ValidateIntervals(CdcControlOptions options, List<string> failures)
    {
        RequirePositive(options.LagThreshold, nameof(CdcControlOptions.LagThreshold), failures);

        if (options.HeartbeatInterval is { } heartbeatInterval)
        {
            RequirePositive(heartbeatInterval, nameof(CdcControlOptions.HeartbeatInterval), failures);
        }

        if (options.SqlServerPollInterval is { } sqlServerPollInterval)
        {
            RequirePositive(sqlServerPollInterval, nameof(CdcControlOptions.SqlServerPollInterval), failures);
        }
    }

    private static void ValidateAcls(CdcControlOptions options, List<string> failures)
    {
        if (options.ConsumerPrincipals.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{nameof(CdcControlOptions.ConsumerPrincipals)} must not contain blank entries.");
        }

        if (
            options.ConsumerPrincipals.Distinct(StringComparer.Ordinal).Count()
            != options.ConsumerPrincipals.Count
        )
        {
            failures.Add($"{nameof(CdcControlOptions.ConsumerPrincipals)} must not contain duplicates.");
        }

        if (!options.AclsEnabled)
        {
            return;
        }

        RequireText(options.ConnectorPrincipal, nameof(CdcControlOptions.ConnectorPrincipal), failures);
        RequireText(
            options.ConnectWorkerPrincipal,
            nameof(CdcControlOptions.ConnectWorkerPrincipal),
            failures
        );
    }

    private static void ValidateProjectionStatusAccess(CdcControlOptions options, List<string> failures)
    {
        RequireHttpUri(options.DmsBaseUrl, nameof(CdcControlOptions.DmsBaseUrl), failures);
        RequireText(options.DmsBearerToken, nameof(CdcControlOptions.DmsBearerToken), failures);
    }

    private static void ValidateTimeouts(CdcControlTimeoutOptions timeouts, List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(timeouts);

        RequirePositive(
            timeouts.EligibilityProbe,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.EligibilityProbe)}",
            failures
        );
        RequirePositive(
            timeouts.KafkaAdmin,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.KafkaAdmin)}",
            failures
        );
        RequirePositive(
            timeouts.ConnectRequest,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.ConnectRequest)}",
            failures
        );
        RequirePositive(
            timeouts.StatusEndpoint,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.StatusEndpoint)}",
            failures
        );
        RequirePositive(
            timeouts.ProviderSetup,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.ProviderSetup)}",
            failures
        );
        RequirePositive(
            timeouts.ProjectionCaughtUp,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.ProjectionCaughtUp)}",
            failures
        );
        RequirePositive(
            timeouts.ProviderBarrier,
            $"{nameof(CdcControlOptions.Timeouts)}:{nameof(CdcControlTimeoutOptions.ProviderBarrier)}",
            failures
        );
    }

    private static void RequireText(string? value, string settingName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{settingName} must be supplied.");
        }
    }

    private static void RequirePositive(TimeSpan value, string settingName, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{settingName} must be positive.");
        }
    }

    private static void RequireHttpUri(string? value, string settingName, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{settingName} must be supplied.");
            return;
        }

        if (
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            failures.Add($"{settingName} must be an absolute http or https URL.");
        }
    }
}
