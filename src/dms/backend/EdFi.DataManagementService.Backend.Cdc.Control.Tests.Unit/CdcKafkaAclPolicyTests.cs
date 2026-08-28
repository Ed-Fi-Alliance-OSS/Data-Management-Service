// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Binding-scoped ACL provisioning and validation, and the composed Kafka policy observation. The fake
/// broker evaluates real filter semantics — including MATCH over wildcard and prefixed patterns — so an
/// over-broad grant cannot pass by escaping a literal-only query.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaAclPolicy")]
public class Given_CdcKafkaAclPolicy
{
    private const string ConnectorPrincipal = "User:connector";
    private const string ReaderPrincipal = "User:reader";
    private const string ReaderGroup = "reader-group";
    private const string ForeignPrincipal = "User:other-instance";
    private const int PartitionCount = 3;
    private const int MaxRecordBytes = 4_194_304;
    private const int GenerousLimit = 104_857_600;
    private const long SevenDaysMilliseconds = 604800000;

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_accepts_an_exact_acl_match_without_creating_a_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(inventory, ConformingAcls(inventory));

        CdcKafkaBindingAclPolicies first = await RunAclsAsync(adminClient, inventory);
        CdcKafkaBindingAclPolicies second = await RunAclsAsync(adminClient, inventory);

        foreach (CdcKafkaBindingAclPolicies policies in new[] { first, second })
        {
            policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.SchemaHistoryTopicAcls!.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
            policies.Diagnostics.Should().BeEmpty();
        }

        second.PublicTopicAcls.ResourceName.Should().Be(inventory.TopicName);
        second.ProgressTopicAcls.ResourceName.Should().Be(inventory.ProgressTopicName);
        second.SchemaHistoryTopicAcls!.ResourceName.Should().Be(inventory.SchemaHistoryTopicName);
        A.CallTo(() => adminClient.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_repairs_a_missing_public_topic_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.RemoveAll(binding =>
            binding.Entry.Principal == ReaderPrincipal && binding.Entry.Operation == AclOperation.Read
        );
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        CreatedGrants(adminClient)
            .Should()
            .ContainSingle(binding =>
                binding.Pattern.Name == inventory.TopicName
                && binding.Entry.Principal == ReaderPrincipal
                && binding.Entry.Operation == AclOperation.Read
                && binding.Entry.PermissionType == AclPermissionType.Allow
                && binding.Pattern.ResourcePatternType == ResourcePatternType.Literal
            );
    }

    [Test]
    public async Task It_repairs_a_missing_progress_topic_producer_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.RemoveAll(binding => binding.Pattern.Name == inventory.ProgressTopicName);
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        CreatedGrants(adminClient)
            .Where(binding => binding.Pattern.Name == inventory.ProgressTopicName)
            .Select(binding => binding.Entry.Operation)
            .Should()
            .BeEquivalentTo([AclOperation.Write, AclOperation.Describe]);
    }

    [Test]
    public async Task It_repairs_a_missing_consumer_group_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.RemoveAll(binding => binding.Pattern.Type == ResourceType.Group);
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        CreatedGrants(adminClient)
            .Should()
            .ContainSingle(binding =>
                binding.Pattern.Type == ResourceType.Group
                && binding.Pattern.Name == ReaderGroup
                && binding.Entry.Principal == ReaderPrincipal
                && binding.Entry.Operation == AclOperation.Read
            );
    }

    [Test]
    public async Task It_rejects_a_wildcard_topic_grant_reaching_the_public_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Topic, "*", ForeignPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);

        // The log sanitizer drops the bare wildcard, so the offending resource is identified by the
        // bucket it was found in rather than by name.
        policies
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.ArtifactKind == "publicTopicAcls"
                && diagnostic.Message.Contains("over-broad", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_rejects_a_prefixed_topic_grant_reaching_the_public_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(
            Acl(
                ResourceType.Topic,
                "edfi.dms",
                ForeignPrincipal,
                AclOperation.Read,
                ResourcePatternType.Prefixed
            )
        );
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_an_instance_consumer_grant_on_the_progress_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Topic, inventory.ProgressTopicName, ReaderPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.ArtifactName == inventory.ProgressTopicName);
    }

    [Test]
    public async Task It_rejects_an_instance_consumer_grant_on_the_schema_history_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(
            Acl(ResourceType.Topic, inventory.SchemaHistoryTopicName!, ReaderPrincipal, AclOperation.Read)
        );
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.SchemaHistoryTopicAcls!.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_an_instance_consumer_grant_on_another_instance_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(
            Acl(
                ResourceType.Topic,
                "edfi.dms.instance.other-store-g1.documents.v1",
                ReaderPrincipal,
                AclOperation.Read
            )
        );
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.ArtifactName == "edfi.dms.instance.other-store-g1.documents.v1"
            );
    }

    [Test]
    public async Task It_rejects_an_over_broad_consumer_group_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Group, ReaderGroup, ReaderPrincipal, AclOperation.All));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies.Diagnostics.Should().Contain(diagnostic => diagnostic.ArtifactName == ReaderGroup);
    }

    [Test]
    public async Task It_rejects_a_consumer_group_grant_held_by_another_principal()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Group, ReaderGroup, ForeignPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_a_consumer_holding_a_grant_on_another_consumer_group()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Group, "another-instance-group", ReaderPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        policies
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.ArtifactName == "another-instance-group");
    }

    [Test]
    public async Task It_rejects_a_wildcard_consumer_group_grant()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Group, "*", ReaderPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_rejects_a_deny_grant_on_a_governed_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(
            Acl(
                ResourceType.Topic,
                inventory.TopicName,
                ConnectorPrincipal,
                AclOperation.Write,
                permission: AclPermissionType.Deny
            )
        );
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
    }

    [Test]
    public async Task It_reports_not_applicable_when_the_deployment_has_no_authorizer()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(inventory, ConformingAcls(inventory));

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory, aclsEnabled: false);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.NotApplicable);
        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.NotApplicable);
        policies.SchemaHistoryTopicAcls!.State.Should().Be(CdcKafkaPolicyItemState.NotApplicable);
        policies.Diagnostics.Should().ContainSingle(diagnostic => diagnostic.Code == "kafkaAclsNotEnforced");
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reports_unknown_when_the_authorizer_cannot_be_queried()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, ConformingAcls(inventory));
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .Throws(new KafkaException(ErrorCode.ClusterAuthorizationFailed));

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        A.CallTo(() => adminClient.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reports_unknown_when_a_grant_repair_is_rejected()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.RemoveAll(binding => binding.Pattern.Name == inventory.ProgressTopicName);
        IAdminClient adminClient = Broker(inventory, acls);
        A.CallTo(() => adminClient.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .Throws(new KafkaException(ErrorCode.ClusterAuthorizationFailed));

        CdcKafkaBindingAclPolicies policies = await RunAclsAsync(adminClient, inventory);

        policies.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
    }

    [Test]
    public async Task It_composes_a_satisfied_kafka_policy_observation()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(inventory, ConformingAcls(inventory));

        CdcKafkaPolicyObservation observation = await RunPolicyAsync(adminClient, inventory);

        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Satisfied);
        observation.DurabilityProfile.Should().Be(CdcControlOptions.LocalDurabilityProfile);
        observation.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.SchemaHistoryTopic!.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.SchemaHistoryTopicAcls!.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.RecordSizePolicy.State.Should().Be(CdcKafkaPolicyItemState.Satisfied);
        observation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task It_composes_a_satisfied_observation_when_acls_are_not_applicable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, []);

        CdcKafkaPolicyObservation observation = await RunPolicyAsync(
            adminClient,
            inventory,
            aclsEnabled: false
        );

        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Satisfied);
        observation.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.NotApplicable);
        observation.SchemaHistoryTopic.Should().BeNull();
        observation.SchemaHistoryTopicAcls.Should().BeNull();
    }

    [Test]
    public async Task It_composes_an_invalid_observation_when_one_acl_bucket_is_invalid()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        List<AclBinding> acls = ConformingAcls(inventory);
        acls.Add(Acl(ResourceType.Topic, inventory.ProgressTopicName, ReaderPrincipal, AclOperation.Read));
        IAdminClient adminClient = Broker(inventory, acls);

        CdcKafkaPolicyObservation observation = await RunPolicyAsync(adminClient, inventory);

        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Invalid);
        observation.ProgressTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Invalid);
        observation.Diagnostics.Should().NotBeEmpty();
    }

    [Test]
    public async Task It_composes_an_unknown_observation_when_the_broker_is_unreachable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));
        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));

        CdcKafkaPolicyObservation observation = await RunPolicyAsync(adminClient, inventory);

        observation.PolicyState.Should().Be(CdcKafkaPolicyState.Unknown);
        observation.PublicTopic.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        observation.PublicTopicAcls.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
        observation.RecordSizePolicy.State.Should().Be(CdcKafkaPolicyItemState.Unknown);
    }

    private static async Task<CdcKafkaBindingAclPolicies> RunAclsAsync(
        IAdminClient adminClient,
        CdcArtifactInventory inventory,
        bool aclsEnabled = true
    ) =>
        await Adapter(adminClient, inventory, aclsEnabled)
            .EnsureBindingAclsAsync(inventory, CancellationToken.None);

    private static async Task<CdcKafkaPolicyObservation> RunPolicyAsync(
        IAdminClient adminClient,
        CdcArtifactInventory inventory,
        bool aclsEnabled = true
    )
    {
        CdcTargetIdentity targetIdentity = TargetIdentity(inventory);
        CdcKafkaPolicyObservation observation = await Adapter(adminClient, inventory, aclsEnabled)
            .EnsureBindingKafkaPolicyAsync(
                new("operation-1", targetIdentity, null),
                inventory,
                CancellationToken.None
            );

        CdcKafkaPolicyObservationValidator
            .Validate(observation, new("operation-1", targetIdentity, null, ObservedAt.AddMinutes(1)))
            .Succeeded.Should()
            .BeTrue("every composed Kafka policy observation must satisfy its own contract");

        return observation;
    }

    private static CdcKafkaAdminAdapter Adapter(
        IAdminClient adminClient,
        CdcArtifactInventory inventory,
        bool aclsEnabled
    ) =>
        new(
            adminClient,
            Options.Create(ControlOptions(inventory, aclsEnabled)),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance
        );

    private static IReadOnlyList<AclBinding> CreatedGrants(IAdminClient adminClient) =>
        [
            .. Fake.GetCalls(adminClient)
                .Where(call =>
                    string.Equals(
                        call.Method.Name,
                        nameof(IAdminClient.CreateAclsAsync),
                        StringComparison.Ordinal
                    )
                )
                .SelectMany(call => (IEnumerable<AclBinding>)call.Arguments[0]!),
        ];

    private static List<AclBinding> ConformingAcls(CdcArtifactInventory inventory)
    {
        List<AclBinding> acls =
        [
            Acl(ResourceType.Topic, inventory.TopicName, ConnectorPrincipal, AclOperation.Write),
            Acl(ResourceType.Topic, inventory.TopicName, ConnectorPrincipal, AclOperation.Describe),
            Acl(ResourceType.Topic, inventory.TopicName, ReaderPrincipal, AclOperation.Read),
            Acl(ResourceType.Topic, inventory.TopicName, ReaderPrincipal, AclOperation.Describe),
            Acl(ResourceType.Topic, inventory.ProgressTopicName, ConnectorPrincipal, AclOperation.Write),
            Acl(ResourceType.Topic, inventory.ProgressTopicName, ConnectorPrincipal, AclOperation.Describe),
            Acl(ResourceType.Group, ReaderGroup, ReaderPrincipal, AclOperation.Read),
        ];

        if (inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName)
        {
            acls.AddRange([
                Acl(ResourceType.Topic, schemaHistoryTopicName, ConnectorPrincipal, AclOperation.Read),
                Acl(ResourceType.Topic, schemaHistoryTopicName, ConnectorPrincipal, AclOperation.Write),
                Acl(ResourceType.Topic, schemaHistoryTopicName, ConnectorPrincipal, AclOperation.Describe),
                Acl(
                    ResourceType.Topic,
                    schemaHistoryTopicName,
                    ConnectorPrincipal,
                    AclOperation.DescribeConfigs
                ),
            ]);
        }

        return acls;
    }

    private static AclBinding Acl(
        ResourceType resourceType,
        string resourceName,
        string principal,
        AclOperation operation,
        ResourcePatternType patternType = ResourcePatternType.Literal,
        AclPermissionType permission = AclPermissionType.Allow
    ) =>
        new()
        {
            Pattern = new ResourcePattern
            {
                Type = resourceType,
                Name = resourceName,
                ResourcePatternType = patternType,
            },
            Entry = new AccessControlEntry
            {
                Principal = principal,
                Host = CdcKafkaAdminAdapter.AnyHost,
                Operation = operation,
                PermissionType = permission,
            },
        };

    /// <summary>
    /// A fake broker whose ACL store answers filters with Kafka's own matching semantics and accepts
    /// created grants, so repair is observable on a second pass.
    /// </summary>
    private static IAdminClient Broker(CdcArtifactInventory inventory, List<AclBinding> acls)
    {
        List<AclBinding> store = [.. acls];
        IAdminClient adminClient = A.Fake<IAdminClient>();

        A.CallTo(() => adminClient.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .ReturnsLazily(call =>
                Task.FromResult(
                    new DescribeAclsResult
                    {
                        AclBindings =
                        [
                            .. store.Where(binding => Matches((AclBindingFilter)call.Arguments[0]!, binding)),
                        ],
                    }
                )
            );
        A.CallTo(() => adminClient.CreateAclsAsync(A<IEnumerable<AclBinding>>._, A<CreateAclsOptions>._))
            .Invokes(call => store.AddRange((IEnumerable<AclBinding>)call.Arguments[0]!));

        StubTopics(adminClient, inventory);

        return adminClient;
    }

    private static bool Matches(AclBindingFilter filter, AclBinding binding)
    {
        if (
            filter.PatternFilter.Type != ResourceType.Any
            && filter.PatternFilter.Type != binding.Pattern.Type
        )
        {
            return false;
        }

        if (
            filter.EntryFilter.Principal is { } principal
            && !string.Equals(principal, binding.Entry.Principal, StringComparison.Ordinal)
        )
        {
            return false;
        }

        if (
            filter.EntryFilter.Operation != AclOperation.Any
            && filter.EntryFilter.Operation != binding.Entry.Operation
        )
        {
            return false;
        }

        if (
            filter.EntryFilter.PermissionType != AclPermissionType.Any
            && filter.EntryFilter.PermissionType != binding.Entry.PermissionType
        )
        {
            return false;
        }

        if (filter.PatternFilter.Name is not { } name)
        {
            return true;
        }

        return filter.PatternFilter.ResourcePatternType switch
        {
            // MATCH resolves every pattern that would authorize the named resource.
            ResourcePatternType.Match => binding.Pattern.ResourcePatternType switch
            {
                ResourcePatternType.Literal => binding.Pattern.Name == name || binding.Pattern.Name == "*",
                ResourcePatternType.Prefixed => name.StartsWith(
                    binding.Pattern.Name,
                    StringComparison.Ordinal
                ),
                _ => false,
            },
            ResourcePatternType.Any => binding.Pattern.Name == name,
            _ => binding.Pattern.ResourcePatternType == filter.PatternFilter.ResourcePatternType
                && binding.Pattern.Name == name,
        };
    }

    private static void StubTopics(IAdminClient adminClient, CdcArtifactInventory inventory)
    {
        List<TopicMetadata> topics =
        [
            TopicMetadataFor(inventory.TopicName, PartitionCount),
            TopicMetadataFor(inventory.ProgressTopicName, 1),
        ];

        StubTopicConfig(
            adminClient,
            inventory.TopicName,
            new(StringComparer.Ordinal)
            {
                [CdcKafkaAdminAdapter.CleanupPolicyConfigName] = "compact",
                [CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = "1",
                [CdcKafkaAdminAdapter.DeleteRetentionConfigName] = SevenDaysMilliseconds.ToString(
                    CultureInfo.InvariantCulture
                ),
                [CdcKafkaAdminAdapter.MaxMessageBytesConfigName] = MaxRecordBytes.ToString(
                    CultureInfo.InvariantCulture
                ),
            }
        );
        StubTopicConfig(
            adminClient,
            inventory.ProgressTopicName,
            new(StringComparer.Ordinal)
            {
                [CdcKafkaAdminAdapter.CleanupPolicyConfigName] = "compact",
                [CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = "1",
            }
        );

        if (inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName)
        {
            topics.Add(TopicMetadataFor(schemaHistoryTopicName, 1));
            StubTopicConfig(
                adminClient,
                schemaHistoryTopicName,
                new(StringComparer.Ordinal)
                {
                    [CdcKafkaAdminAdapter.CleanupPolicyConfigName] = "delete",
                    [CdcKafkaAdminAdapter.MinInSyncReplicasConfigName] = "1",
                    [CdcKafkaAdminAdapter.RetentionMillisecondsConfigName] = "-1",
                    [CdcKafkaAdminAdapter.RetentionBytesConfigName] = "-1",
                }
            );
        }

        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Returns(new Metadata([new BrokerMetadata(0, "broker", 9092)], topics, 0, "broker"));

        StubConfig(
            adminClient,
            ResourceType.Broker,
            "0",
            new(StringComparer.Ordinal)
            {
                [CdcKafkaAdminAdapter.SocketRequestMaxBytesConfigName] = GenerousLimit.ToString(
                    CultureInfo.InvariantCulture
                ),
                [CdcKafkaAdminAdapter.MessageMaxBytesConfigName] = GenerousLimit.ToString(
                    CultureInfo.InvariantCulture
                ),
                [CdcKafkaAdminAdapter.ReplicaFetchMaxBytesConfigName] = GenerousLimit.ToString(
                    CultureInfo.InvariantCulture
                ),
                [CdcKafkaAdminAdapter.ReplicaFetchResponseMaxBytesConfigName] = GenerousLimit.ToString(
                    CultureInfo.InvariantCulture
                ),
            },
            ConfigSource.StaticBrokerConfig
        );
    }

    private static void StubTopicConfig(
        IAdminClient adminClient,
        string topicName,
        Dictionary<string, string> configs
    ) => StubConfig(adminClient, ResourceType.Topic, topicName, configs, ConfigSource.DynamicTopicConfig);

    private static void StubConfig(
        IAdminClient adminClient,
        ResourceType resourceType,
        string resourceName,
        Dictionary<string, string> configs,
        ConfigSource source
    ) =>
        A.CallTo(() =>
                adminClient.DescribeConfigsAsync(
                    A<IEnumerable<ConfigResource>>.That.Matches(resources =>
                        resources.Single().Type == resourceType && resources.Single().Name == resourceName
                    ),
                    A<DescribeConfigsOptions>._
                )
            )
            .Returns(
                new List<DescribeConfigsResult>
                {
                    new()
                    {
                        Entries = configs.ToDictionary(
                            config => config.Key,
                            config => new ConfigEntryResult
                            {
                                Name = config.Key,
                                Value = config.Value,
                                Source = source,
                            },
                            StringComparer.Ordinal
                        ),
                    },
                }
            );

    private static TopicMetadata TopicMetadataFor(string topicName, int partitionCount) =>
        new(
            topicName,
            [
                .. Enumerable
                    .Range(0, partitionCount)
                    .Select(index => new PartitionMetadata(index, 0, [0], [0], new Error(ErrorCode.NoError))),
            ],
            new Error(ErrorCode.NoError)
        );

    private static CdcTargetIdentity TargetIdentity(CdcArtifactInventory inventory) =>
        new(
            inventory.DeploymentKey,
            "default",
            "1",
            inventory.InstanceKey,
            inventory.Generation,
            inventory.Provider
        );

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;

    private static CdcControlOptions ControlOptions(CdcArtifactInventory inventory, bool aclsEnabled) =>
        new()
        {
            DeploymentKey = inventory.DeploymentKey,
            InstanceKey = inventory.InstanceKey,
            TopicPrefix = inventory.TopicPrefix,
            Generation = inventory.Generation,
            PartitionCount = PartitionCount,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = MaxRecordBytes,
            AclsEnabled = aclsEnabled,
            ConnectorPrincipal = ConnectorPrincipal,
            ConnectWorkerPrincipal = "User:connect-worker",
            Consumers = [new() { Principal = ReaderPrincipal, ConsumerGroup = ReaderGroup }],
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
