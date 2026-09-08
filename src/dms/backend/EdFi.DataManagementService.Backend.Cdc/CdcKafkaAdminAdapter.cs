// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Live deployment authority supplements DescribeAcls, which cannot enumerate superusers,
/// default-allow settings or grants inherited through an external authorizer/group mapping.
/// Must cover exactly the supplied live broker inventory and expand inherited grants to effective
/// Kafka principals. InventoryComplete means no unobserved authorization mechanism remains.
/// This is observed deployment state, never the requested authorization profile or an operator assertion.
/// </summary>
public interface ICdcKafkaAuthorizationInspection
{
    Task<CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>> InspectAsync(
        IReadOnlyList<int> brokerIds,
        CancellationToken cancellationToken
    );
}

public sealed record CdcKafkaAuthorizationDeploymentEvidence(
    bool InventoryComplete,
    [property: JsonIgnore] IReadOnlyList<CdcKafkaBrokerAuthorization> Brokers,
    [property: JsonIgnore] IReadOnlyList<CdcKafkaAclGrant> InheritedGrants
)
{
    public override string ToString() => nameof(CdcKafkaAuthorizationDeploymentEvidence);
}

public sealed record CdcKafkaBrokerAuthorization(
    [property: JsonIgnore] int BrokerId,
    bool AuthorizationEnabled,
    bool AllowEveryoneIfNoAclFound,
    [property: JsonIgnore] IReadOnlyList<string> SuperuserPrincipals
)
{
    public override string ToString() => nameof(CdcKafkaBrokerAuthorization);
}

/// <summary>
/// Raw Kafka evidence/effects beneath the controller's policy observation facade. Callers authorize
/// and journal effects. No partition/configuration repair, deletes, producer, consumer or offset reads.
/// </summary>
public interface ICdcKafkaAdminAdapter
{
    Task<
        CdcTransportResult<EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcSqlServerSchemaHistoryState>
    > InspectSchemaHistoryAsync(CdcDeploymentRequest request, CancellationToken cancellationToken);
    Task<CdcTransportResult<CdcKafkaTopicEvidence>> InspectTopicAsync(
        CdcDeploymentRequest request,
        string topic,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcKafkaBrokerEvidence>> InspectBrokersAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcKafkaAclEvidence>> InspectAclsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcKafkaTopicEvidence>> CreateMissingTopicAsync(
        CdcDeploymentRequest request,
        CdcKafkaTopicIntent topic,
        CancellationToken cancellationToken
    );
    Task<CdcTransportResult<CdcKafkaAclEvidence>> ReconcileMissingGrantsAsync(
        CdcDeploymentRequest request,
        bool sharedOffsets,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Confluent admin transport. Every native request has a finite timeout; waiting additionally honors
/// caller cancellation. Native calls already in flight may finish after cancellation, so a later
/// invocation must reconcile live state. Only the safe factory owns the native client; test injection
/// does not. Native logs/error callbacks are suppressed because they can contain physical identifiers.
/// </summary>
public sealed partial class CdcKafkaAdminAdapter
    : ICdcKafkaAdminAdapter,
        ICdcArtifactCleanupAdapter,
        IDisposable
{
    private readonly IAdminClient _client;
    private readonly ICdcKafkaAuthorizationInspection _authorization;
    private readonly bool _ownsClient;
    private static readonly string[] TopicConfigurationKeys =
    [
        "cleanup.policy",
        "min.insync.replicas",
        "delete.retention.ms",
        "max.message.bytes",
        "retention.ms",
        "retention.bytes",
    ];

    internal CdcKafkaAdminAdapter(
        IAdminClient client,
        ICdcKafkaAuthorizationInspection authorization,
        bool ownsClient = false
    )
    {
        _client = client;
        _authorization = authorization;
        _ownsClient = ownsClient;
        ListOffsets = (specs, options) => client.ListOffsetsAsync(specs, options);
    }

    public static CdcTransportResult<CdcKafkaAdminAdapter> Create(
        AdminClientConfig configuration,
        ICdcKafkaAuthorizationInspection authorization
    )
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(authorization);
            AdminClientConfig safe = new(
                configuration
                    .Where(pair => pair.Key != "debug")
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            );
            // Topic-specific metadata requests must never enable broker auto-creation.
            safe.Set("allow.auto.create.topics", "false");
            safe.ClientId = "dms-cdc-admin";
            IAdminClient client = new AdminClientBuilder(safe)
                .SetLogHandler((_, _) => { })
                .SetErrorHandler((_, _) => { })
                .Build();
            return new CdcTransportResult<CdcKafkaAdminAdapter>.Observed(new(client, authorization, true));
        }
        catch (Exception exception)
        {
            return Unavailable<CdcKafkaAdminAdapter>(exception);
        }
    }

    public Task<CdcTransportResult<CdcKafkaTopicEvidence>> InspectTopicAsync(
        CdcDeploymentRequest request,
        string topic,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(topic))
                {
                    return Failure<CdcKafkaTopicEvidence>(CdcDeploymentFailure.InvalidInput);
                }
                Metadata metadata = await CallAsync(
                    () => Task.Run(() => _client.GetMetadata(topic, request.Timing.CallTimeout)),
                    request,
                    cancellationToken
                );
                TopicMetadata actual = metadata.Topics.Single(item => item.Topic == topic);
                if (actual.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    return new CdcTransportResult<CdcKafkaTopicEvidence>.Absent();
                }
                ThrowIfError(actual.Error);
                if (
                    actual.Partitions.Count == 0
                    || actual.Partitions.Exists(partition => partition.Error.IsError)
                )
                {
                    foreach (PartitionMetadata partition in actual.Partitions)
                    {
                        ThrowIfError(partition.Error);
                    }
                    return Failure<CdcKafkaTopicEvidence>(CdcDeploymentFailure.Unavailable);
                }
                Dictionary<int, IReadOnlyList<int>> replicas = actual.Partitions.ToDictionary(
                    partition => partition.PartitionId,
                    partition => (IReadOnlyList<int>)Array.AsReadOnly(partition.Replicas.ToArray())
                );
                if (
                    !Enumerable.Range(0, replicas.Count).All(replicas.ContainsKey)
                    || replicas.Values.Any(ids =>
                        ids.Count == 0 || ids.Any(id => id < 0) || ids.Distinct().Count() != ids.Count
                    )
                )
                {
                    return Failure<CdcKafkaTopicEvidence>(CdcDeploymentFailure.Unavailable);
                }
                ConfigResource resource = new() { Type = ResourceType.Topic, Name = topic };
                List<DescribeConfigsResult> configs = await DescribeConfigsAsync(
                    [resource],
                    request,
                    cancellationToken
                );
                DescribeConfigsResult config = ExactConfig(configs, resource);
                Dictionary<string, CdcKafkaConfigurationValue> values = [];
                foreach (string key in TopicConfigurationKeys)
                {
                    if (!config.Entries.TryGetValue(key, out var entry))
                    {
                        continue;
                    }
                    // Unknown origin and redacted values are missing evidence, never inferred overrides.
                    if (
                        entry.Name != key
                        || entry.IsSensitive
                        || entry.Value is null
                        || !Enum.IsDefined(entry.Source)
                        || entry.Source == ConfigSource.UnknownConfig
                    )
                    {
                        continue;
                    }
                    values.Add(key, new(entry.Value, entry.Source == ConfigSource.DynamicTopicConfig));
                }
                return new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(new(topic, replicas, values));
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcKafkaBrokerEvidence>> InspectBrokersAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                Metadata metadata = await ClusterAsync(request, cancellationToken);
                ConfigResource[] resources = metadata
                    .Brokers.Select(broker => new ConfigResource
                    {
                        Type = ResourceType.Broker,
                        Name = broker.BrokerId.ToString(CultureInfo.InvariantCulture),
                    })
                    .ToArray();
                List<DescribeConfigsResult> configs = await DescribeConfigsAsync(
                    resources,
                    request,
                    cancellationToken
                );
                if (configs.Count != resources.Length)
                {
                    return Failure<CdcKafkaBrokerEvidence>(CdcDeploymentFailure.Unavailable);
                }
                List<CdcKafkaBrokerCapacity> capacities = [];
                foreach (ConfigResource resource in resources)
                {
                    DescribeConfigsResult config = ExactConfig(configs, resource);
                    capacities.Add(
                        new(
                            int.Parse(resource.Name, CultureInfo.InvariantCulture),
                            PositiveNumber(config, "socket.request.max.bytes"),
                            PositiveNumber(config, "replica.fetch.max.bytes"),
                            PositiveNumber(config, "replica.fetch.response.max.bytes"),
                            PositiveNumber(config, "message.max.bytes")
                        )
                    );
                }
                Metadata after = await ClusterAsync(request, cancellationToken);
                if (!SameBrokers(metadata, after))
                {
                    return Failure<CdcKafkaBrokerEvidence>(CdcDeploymentFailure.Unavailable);
                }
                return new CdcTransportResult<CdcKafkaBrokerEvidence>.Observed(
                    new(true, capacities.AsReadOnly())
                );
            },
            cancellationToken
        );

    public Task<CdcTransportResult<CdcKafkaAclEvidence>> InspectAclsAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        GuardAsync(
            async () =>
            {
                Metadata metadata = await ClusterAsync(request, cancellationToken);
                int[] ids = metadata.Brokers.Select(broker => broker.BrokerId).Order().ToArray();
                using CancellationTokenSource inspectionTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                inspectionTimeout.CancelAfter(request.Timing.CallTimeout);
                CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence> deployment = await _authorization
                    .InspectAsync(ids, inspectionTimeout.Token)
                    .WaitAsync(inspectionTimeout.Token);
                if (
                    deployment
                    is not CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed observed
                )
                {
                    return Failure<CdcKafkaAclEvidence>(
                        deployment.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
                }
                CdcKafkaAuthorizationDeploymentEvidence authority = observed.Value;
                if (
                    !authority.InventoryComplete
                    || !authority.Brokers.Select(broker => broker.BrokerId).Order().SequenceEqual(ids)
                    || authority.Brokers.Select(broker => broker.AuthorizationEnabled).Distinct().Count() != 1
                    || authority.Brokers.Any(broker =>
                        broker.SuperuserPrincipals.Any(string.IsNullOrWhiteSpace)
                    )
                    || authority.InheritedGrants.Any(grant => !ValidGrant(grant))
                )
                {
                    return Failure<CdcKafkaAclEvidence>(CdcDeploymentFailure.Unavailable);
                }
                bool enabled = authority.Brokers[0].AuthorizationEnabled;
                List<CdcKafkaAclGrant> grants = [.. authority.InheritedGrants];
                if (enabled)
                {
                    // No principal/resource/allow-only filter: these hide wildcard/prefix/group/deny grants.
                    DescribeAclsResult acls = await CallAsync(
                        () =>
                            _client.DescribeAclsAsync(
                                new()
                                {
                                    PatternFilter = new()
                                    {
                                        Type = ResourceType.Any,
                                        ResourcePatternType = ResourcePatternType.Any,
                                    },
                                    EntryFilter = new()
                                    {
                                        Operation = AclOperation.Any,
                                        PermissionType = AclPermissionType.Any,
                                    },
                                },
                                new() { RequestTimeout = request.Timing.CallTimeout }
                            ),
                        request,
                        cancellationToken
                    );
                    grants.AddRange(acls.AclBindings.Select(MapGrant));
                }
                else if (
                    grants.Count > 0
                    || authority.Brokers.Any(broker =>
                        broker.AllowEveryoneIfNoAclFound || broker.SuperuserPrincipals.Count > 0
                    )
                )
                {
                    return Failure<CdcKafkaAclEvidence>(CdcDeploymentFailure.Unavailable);
                }
                Metadata after = await ClusterAsync(request, cancellationToken);
                if (!SameBrokers(metadata, after))
                {
                    return Failure<CdcKafkaAclEvidence>(CdcDeploymentFailure.Unavailable);
                }
                return new CdcTransportResult<CdcKafkaAclEvidence>.Observed(
                    new(
                        enabled,
                        true,
                        authority.Brokers.Any(broker => broker.AllowEveryoneIfNoAclFound),
                        authority
                            .Brokers.SelectMany(broker => broker.SuperuserPrincipals)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        grants.Distinct().ToArray()
                    )
                );
            },
            cancellationToken
        );

    public async Task<CdcTransportResult<CdcKafkaTopicEvidence>> CreateMissingTopicAsync(
        CdcDeploymentRequest request,
        CdcKafkaTopicIntent topic,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CdcDeploymentKafkaPolicyPlan plan = CdcDeploymentKafkaPolicy.Build(request);
        if (
            !plan
                .BindingTopics.Append(plan.OffsetStore)
                .Any(intent =>
                    intent.Role == topic.Role
                    && intent.Name == topic.Name
                    && intent.PartitionCount == topic.PartitionCount
                    && intent.ReplicationFactor == topic.ReplicationFactor
                    && intent
                        .Configuration.OrderBy(pair => pair.Key)
                        .SequenceEqual(topic.Configuration.OrderBy(pair => pair.Key))
                )
        )
        {
            return Failure<CdcKafkaTopicEvidence>(CdcDeploymentFailure.InvalidInput);
        }
        CdcTransportResult<CdcKafkaTopicEvidence> before = await InspectTopicAsync(
            request,
            topic.Name,
            cancellationToken
        );
        if (before is not CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
        {
            return before;
        }
        CdcTransportResult<CdcTransportAcknowledgement> attempt =
            await GuardAsync<CdcTransportAcknowledgement>(
                async () =>
                {
                    await CallAsync(
                        async () =>
                        {
                            await _client.CreateTopicsAsync(
                                [
                                    new()
                                    {
                                        Name = topic.Name,
                                        NumPartitions = topic.PartitionCount,
                                        ReplicationFactor = (short)topic.ReplicationFactor,
                                        Configs = topic.Configuration.ToDictionary(
                                            pair => pair.Key,
                                            pair => pair.Value
                                        ),
                                    },
                                ],
                                new()
                                {
                                    RequestTimeout = request.Timing.CallTimeout,
                                    OperationTimeout = request.Timing.CallTimeout,
                                }
                            );
                            return new CdcTransportAcknowledgement();
                        },
                        request,
                        cancellationToken
                    );
                    return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
                },
                cancellationToken
            );
        // Even successful acknowledgements are insufficient; conflicts and lost responses use the same lookup.
        CdcTransportResult<CdcKafkaTopicEvidence> after = await InspectTopicAsync(
            request,
            topic.Name,
            cancellationToken
        );
        return after is CdcTransportResult<CdcKafkaTopicEvidence>.Absent
            ? Failure<CdcKafkaTopicEvidence>(
                attempt.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.ValidationFailed
            )
            : after;
    }

    public async Task<CdcTransportResult<CdcKafkaAclEvidence>> ReconcileMissingGrantsAsync(
        CdcDeploymentRequest request,
        bool sharedOffsets,
        CancellationToken cancellationToken
    )
    {
        CdcTransportResult<CdcKafkaAclEvidence> before = await InspectAclsAsync(request, cancellationToken);
        CdcKafkaAclValidation validation = CdcDeploymentKafkaPolicy.ValidateAcls(
            request,
            before,
            sharedOffsets
        );
        if (!validation.CanAddMissingGrants)
        {
            return before;
        }
        CdcTransportResult<CdcTransportAcknowledgement> attempt =
            await GuardAsync<CdcTransportAcknowledgement>(
                async () =>
                {
                    await CallAsync(
                        async () =>
                        {
                            await _client.CreateAclsAsync(
                                validation.MissingGrants.Select(ToBinding),
                                new() { RequestTimeout = request.Timing.CallTimeout }
                            );
                            return new CdcTransportAcknowledgement();
                        },
                        request,
                        cancellationToken
                    );
                    return new CdcTransportResult<CdcTransportAcknowledgement>.Observed(new());
                },
                cancellationToken
            );
        CdcTransportResult<CdcKafkaAclEvidence> after = await InspectAclsAsync(request, cancellationToken);
        // Keep live unsafe/partial evidence available to the shared policy. Never retry the effect here.
        if (after is CdcTransportResult<CdcKafkaAclEvidence>.Unavailable && attempt.Diagnostics.Count > 0)
        {
            return Failure<CdcKafkaAclEvidence>(attempt.Diagnostics[0].Failure);
        }
        return after;
    }

    private async Task<Metadata> ClusterAsync(
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        Metadata metadata = await CallAsync(
            () => Task.Run(() => _client.GetMetadata(request.Timing.CallTimeout)),
            request,
            cancellationToken
        );
        if (
            metadata.Brokers.Count == 0
            || metadata.Brokers.Exists(broker =>
                broker.BrokerId < 0 || string.IsNullOrWhiteSpace(broker.Host) || broker.Port <= 0
            )
            || metadata.Brokers.Select(broker => broker.BrokerId).Distinct().Count() != metadata.Brokers.Count
        )
        {
            throw new InvalidOperationException();
        }
        return metadata;
    }

    private Task<List<DescribeConfigsResult>> DescribeConfigsAsync(
        ConfigResource[] resources,
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    ) =>
        CallAsync(
            () =>
                _client.DescribeConfigsAsync(
                    resources,
                    new() { RequestTimeout = request.Timing.CallTimeout }
                ),
            request,
            cancellationToken
        );

    private static DescribeConfigsResult ExactConfig(
        List<DescribeConfigsResult> results,
        ConfigResource resource
    ) =>
        results.Single(result =>
            result.ConfigResource.Type == resource.Type && result.ConfigResource.Name == resource.Name
        );

    private static long PositiveNumber(DescribeConfigsResult config, string key)
    {
        ConfigEntryResult entry = config.Entries[key];
        if (
            entry.Name != key
            || entry.IsSensitive
            || entry.Source is ConfigSource.UnknownConfig or ConfigSource.DynamicTopicConfig
            || !Enum.IsDefined(entry.Source)
            || !long.TryParse(entry.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value <= 0
        )
        {
            throw new InvalidOperationException();
        }
        return value;
    }

    private static bool SameBrokers(Metadata before, Metadata after) =>
        before
            .Brokers.Select(broker => (broker.BrokerId, broker.Host, broker.Port))
            .Order()
            .SequenceEqual(
                after.Brokers.Select(broker => (broker.BrokerId, broker.Host, broker.Port)).Order()
            );

    private static CdcKafkaAclGrant MapGrant(AclBinding binding)
    {
        CdcKafkaAclGrant grant = new(
            binding.Entry.Principal,
            ParseEnum<CdcKafkaAclResourceType>(binding.Pattern.Type.ToString()),
            binding.Pattern.Name,
            ParseEnum<CdcKafkaAclOperation>(binding.Entry.Operation.ToString()),
            ParseEnum<CdcKafkaAclPattern>(binding.Pattern.ResourcePatternType.ToString()),
            ParseEnum<CdcKafkaAclPermission>(binding.Entry.PermissionType.ToString()),
            binding.Entry.Host
        );
        return ValidGrant(grant) ? grant : throw new InvalidOperationException();
    }

    private static bool ValidGrant(CdcKafkaAclGrant grant) =>
        grant is not null
        && !string.IsNullOrWhiteSpace(grant.Principal)
        && !string.IsNullOrWhiteSpace(grant.ResourceName)
        && !string.IsNullOrWhiteSpace(grant.Host)
        && Enum.IsDefined(grant.ResourceType)
        && Enum.IsDefined(grant.Operation)
        && Enum.IsDefined(grant.Pattern)
        && Enum.IsDefined(grant.Permission);

    private static T ParseEnum<T>(string value)
        where T : struct, Enum =>
        Enum.TryParse(value, out T parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException();

    private static AclBinding ToBinding(CdcKafkaAclGrant grant) =>
        new()
        {
            Pattern = new()
            {
                Type = ParseEnum<ResourceType>(grant.ResourceType.ToString()),
                Name = grant.ResourceName,
                ResourcePatternType = ResourcePatternType.Literal,
            },
            Entry = new()
            {
                Principal = grant.Principal,
                Host = grant.Host,
                Operation = ParseEnum<AclOperation>(grant.Operation.ToString()),
                PermissionType = AclPermissionType.Allow,
            },
        };

    private static async Task<T> CallAsync<T>(
        Func<Task<T>> call,
        CdcDeploymentRequest request,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await call().WaitAsync(request.Timing.CallTimeout, cancellationToken);
    }

    private static async Task<CdcTransportResult<T>> GuardAsync<T>(
        Func<Task<CdcTransportResult<T>>> action,
        CancellationToken cancellationToken
    )
        where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await action();
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Unavailable<T>(exception);
        }
    }

    private static void ThrowIfError(Error error)
    {
        if (error.IsError)
        {
            throw new KafkaException(error);
        }
    }

    private static CdcTransportResult<T> Unavailable<T>(Exception exception)
        where T : notnull
    {
        // The client wraps per-resource failures in a generic partial-failure exception.
        // Inspect only typed error codes; never include aggregate exception messages or results.
        IEnumerable<ErrorCode> codes = exception switch
        {
            CreateTopicsException topics => topics.Results.Select(result => result.Error.Code),
            DescribeConfigsException configs => configs.Results.Select(result => result.Error.Code),
            CreateAclsException acls => acls.Results.Select(result => result.Error.Code),
            KafkaException kafka => [kafka.Error.Code],
            _ => [],
        };
        CdcDeploymentFailure[] failures = codes
            .Where(code => code != ErrorCode.NoError)
            .Select(Classify)
            .ToArray();
        CdcDeploymentFailure failure = exception switch
        {
            TimeoutException or OperationCanceledException => CdcDeploymentFailure.Timeout,
            UnauthorizedAccessException => CdcDeploymentFailure.AuthenticationFailed,
            _ when failures.Contains(CdcDeploymentFailure.AuthenticationFailed) =>
                CdcDeploymentFailure.AuthenticationFailed,
            _ when failures.Contains(CdcDeploymentFailure.Timeout) => CdcDeploymentFailure.Timeout,
            _ when failures.Length > 0
                    && Array.TrueForAll(failures, item => item == CdcDeploymentFailure.Conflict) =>
                CdcDeploymentFailure.Conflict,
            _ => CdcDeploymentFailure.Unavailable,
        };
        return Failure<T>(failure);
    }

    private static CdcDeploymentFailure Classify(ErrorCode code) =>
        code switch
        {
            ErrorCode.Local_TimedOut or ErrorCode.Local_MsgTimedOut or ErrorCode.RequestTimedOut =>
                CdcDeploymentFailure.Timeout,
            ErrorCode.SaslAuthenticationFailed
            or ErrorCode.Local_Authentication
            or ErrorCode.TopicAuthorizationFailed
            or ErrorCode.GroupAuthorizationFailed
            or ErrorCode.ClusterAuthorizationFailed => CdcDeploymentFailure.AuthenticationFailed,
            ErrorCode.TopicAlreadyExists => CdcDeploymentFailure.Conflict,
            _ => CdcDeploymentFailure.Unavailable,
        };

    private static CdcTransportResult<T> Failure<T>(CdcDeploymentFailure failure)
        where T : notnull =>
        new CdcTransportResult<T>.Unavailable(new(CdcDeploymentComponent.Kafka, failure));

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    public override string ToString() => nameof(CdcKafkaAdminAdapter);
}
