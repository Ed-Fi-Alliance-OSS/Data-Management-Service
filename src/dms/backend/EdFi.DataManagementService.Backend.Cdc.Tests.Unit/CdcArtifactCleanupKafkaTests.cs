// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Kind = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcGovernedArtifactKind;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcArtifactCleanupKafka(CdcProvider provider)
{
    private CdcArtifactCleanupScope _scope = null!;
    private IAdminClient _client = null!;
    private ICdcKafkaAuthorizationInspection _authorization = null!;
    private CdcKafkaAdminAdapter _adapter = null!;
    private CdcDeploymentKafkaPolicyPlan _plan = null!;
    private HashSet<string> _topics = null!;
    private List<AclBinding> _grants = null!;
    private List<string> _deleted = null!;
    private List<AclBindingFilter> _filters = null!;
    private bool _lostResponse;
    private bool _readbackFails;

    [SetUp]
    public void Setup()
    {
        _scope = CdcArtifactCleanupTestData.Scope(
            CdcArtifactCleanupTestData.WithTiming(
                CdcDeploymentRequestTestData.Request(
                    provider,
                    worker: CdcDeploymentRequestTestData.Worker(
                        authorization: CdcKafkaAuthorizationProfile.AuthorizationEnabled
                    )
                )
            )
        );
        _plan = CdcDeploymentKafkaPolicy.Build(_scope.Request);
        _client = A.Fake<IAdminClient>(options => options.Strict());
        _authorization = A.Fake<ICdcKafkaAuthorizationInspection>();
        _adapter = new(_client, _authorization)
        {
            DescribeCluster = _ =>
                Task.FromResult(new DescribeClusterResult { AuthorizedOperations = [AclOperation.Describe] }),
        };
        _topics = _plan
            .BindingTopics.Append(_plan.OffsetStore)
            .Select(topic => topic.Name)
            .Append("peer-topic")
            .ToHashSet();
        _grants = _plan.BindingGrants.Concat(_plan.OffsetStoreGrants).Select(Binding).ToList();
        _grants.Add(
            Binding(new("peer", CdcKafkaAclResourceType.Topic, "peer-topic", CdcKafkaAclOperation.Read))
        );
        _deleted = [];
        _filters = [];
        _lostResponse = false;
        _readbackFails = false;
        A.CallTo(() => _client.Dispose()).DoesNothing();
        A.CallTo(() => _client.GetMetadata(A<TimeSpan>._)).ReturnsLazily(() => Metadata("peer-topic"));
        A.CallTo(() => _client.GetMetadata(A<string>._, A<TimeSpan>._))
            .ReturnsLazily(
                (string topic, TimeSpan _) =>
                {
                    if (_readbackFails && _deleted.Count > 0)
                    {
                        throw new KafkaException(new Error(ErrorCode.TopicAuthorizationFailed, "secret"));
                    }
                    return Metadata(topic);
                }
            );
        A.CallTo(() =>
                _client.DescribeConfigsAsync(A<IEnumerable<ConfigResource>>._, A<DescribeConfigsOptions>._)
            )
            .ReturnsLazily(
                (IEnumerable<ConfigResource> resources, DescribeConfigsOptions _) =>
                    resources
                        .Select(resource => new DescribeConfigsResult
                        {
                            ConfigResource = resource,
                            Entries = _plan
                                .BindingTopics.Append(_plan.OffsetStore)
                                .Single(topic => topic.Name == resource.Name)
                                .Configuration.ToDictionary(
                                    pair => pair.Key,
                                    pair => new ConfigEntryResult
                                    {
                                        Name = pair.Key,
                                        Value = pair.Value,
                                        Source = ConfigSource.DynamicTopicConfig,
                                    }
                                ),
                        })
                        .ToList()
            );
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(
                    new(true, [new(0, true, false, [])], [])
                )
            );
        A.CallTo(() => _client.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .ReturnsLazily(() =>
            {
                if (_readbackFails && _filters.Count > 0)
                {
                    throw new KafkaException(new Error(ErrorCode.ClusterAuthorizationFailed, "secret"));
                }
                return new DescribeAclsResult { AclBindings = _grants.ToList() };
            });
        A.CallTo(() => _client.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .ReturnsLazily(
                (IEnumerable<string> topics, DeleteTopicsOptions _) =>
                {
                    foreach (string topic in topics)
                    {
                        _deleted.Add(topic);
                        _topics.Remove(topic);
                    }
                    return _lostResponse
                        ? Task.FromException(new TimeoutException("secret"))
                        : Task.CompletedTask;
                }
            );
        A.CallTo(() => _client.DeleteAclsAsync(A<IEnumerable<AclBindingFilter>>._, A<DeleteAclsOptions>._))
            .ReturnsLazily(
                (IEnumerable<AclBindingFilter> filters, DeleteAclsOptions _) =>
                {
                    foreach (var filter in filters)
                    {
                        _filters.Add(filter);
                        _grants.RemoveAll(grant =>
                            grant.Pattern.Type == filter.PatternFilter.Type
                            && grant.Pattern.Name == filter.PatternFilter.Name
                            && grant.Pattern.ResourcePatternType == filter.PatternFilter.ResourcePatternType
                            && grant.Entry.Principal == filter.EntryFilter.Principal
                            && grant.Entry.Host == filter.EntryFilter.Host
                            && grant.Entry.Operation == filter.EntryFilter.Operation
                            && grant.Entry.PermissionType == filter.EntryFilter.PermissionType
                        );
                    }
                    return _lostResponse
                        ? Task.FromException<List<DeleteAclsResult>>(new TimeoutException("secret"))
                        : Task.FromResult(new List<DeleteAclsResult>());
                }
            );
    }

    [TearDown]
    public void Teardown()
    {
        _adapter.Dispose();
        _client.Dispose();
    }

    private Metadata Metadata(string topic) =>
        new(
            [new(0, "private-broker", 9092)],
            [
                new(
                    topic,
                    [new(0, 0, [0], [0], new(ErrorCode.NoError))],
                    new(_topics.Contains(topic) ? ErrorCode.NoError : ErrorCode.UnknownTopicOrPart)
                ),
            ],
            0,
            "private-broker"
        );

    private static AclBinding Binding(CdcKafkaAclGrant grant) =>
        new()
        {
            Pattern = new()
            {
                Type = Enum.Parse<ResourceType>(grant.ResourceType.ToString()),
                Name = grant.ResourceName,
                ResourcePatternType = Enum.Parse<ResourcePatternType>(grant.Pattern.ToString()),
            },
            Entry = new()
            {
                Principal = grant.Principal,
                Host = grant.Host,
                Operation = Enum.Parse<AclOperation>(grant.Operation.ToString()),
                PermissionType = Enum.Parse<AclPermissionType>(grant.Permission.ToString()),
            },
        };

    private Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> Delete(Kind kind) =>
        _adapter.DeleteAsync(_scope, kind, CancellationToken.None);

    [Test]
    public async Task It_deletes_only_the_binding_topic_inventory()
    {
        foreach (
            var artifact in _scope.Inventory.Where(item =>
                item.Kind is Kind.PublicTopic or Kind.ProgressTopic or Kind.SchemaHistoryTopic
            )
        )
        {
            (await Delete(artifact.Kind)).State.Should().Be(CdcTransportEvidenceState.Observed);
        }
        _topics.Should().BeEquivalentTo(_scope.Request.WorkerPolicy.OffsetStorageTopic.Value, "peer-topic");
    }

    [TestCase(Kind.PublicTopic)]
    [TestCase(Kind.ProgressTopic)]
    [TestCase(Kind.PublicTopicAcls)]
    [TestCase(Kind.ProgressTopicAcls)]
    public async Task It_reconciles_a_lost_delete_response(Kind kind)
    {
        _lostResponse = true;
        (await Delete(kind)).State.Should().Be(CdcTransportEvidenceState.Observed);
    }

    [TestCase(Kind.PublicTopic)]
    [TestCase(Kind.PublicTopicAcls)]
    public async Task It_does_not_accept_acknowledgement_without_live_absence(Kind kind)
    {
        _readbackFails = true;
        var result = await Delete(kind);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        result.Diagnostics[0].Failure.Should().Be(CdcDeploymentFailure.AuthenticationFailed);
    }

    [Test]
    public async Task It_leaves_shared_peer_and_group_grants_untouched()
    {
        var retained = _grants
            .Where(grant =>
                grant.Pattern.Type == ResourceType.Group
                || grant.Pattern.Name == "peer-topic"
                || grant.Pattern.Name == _scope.Request.WorkerPolicy.OffsetStorageTopic.Value
            )
            .ToArray();
        foreach (
            var artifact in _scope.Inventory.Where(item =>
                item.Kind is Kind.PublicTopicAcls or Kind.ProgressTopicAcls or Kind.SchemaHistoryTopicAcls
            )
        )
        {
            (await Delete(artifact.Kind)).State.Should().Be(CdcTransportEvidenceState.Observed);
        }
        _grants.Should().BeEquivalentTo(retained);
        _filters
            .Should()
            .OnlyContain(filter =>
                filter.PatternFilter.Type == ResourceType.Topic
                && filter.PatternFilter.ResourcePatternType == ResourcePatternType.Literal
                && !string.IsNullOrEmpty(filter.EntryFilter.Principal)
                && !string.IsNullOrEmpty(filter.EntryFilter.Host)
                && filter.EntryFilter.Operation != AclOperation.Any
                && filter.EntryFilter.PermissionType == AclPermissionType.Allow
            );
    }

    [TestCase("wildcard")]
    [TestCase("prefix")]
    [TestCase("peer")]
    [TestCase("deny")]
    [TestCase("host")]
    public async Task It_rejects_ungoverned_grants_without_broad_deletion(string change)
    {
        var grant = Binding(
            _plan.BindingGrants.First(item => item.ResourceName == _scope.Request.Binding.TopicName)
        );
        switch (change)
        {
            case "wildcard":
                grant.Pattern.Name = "*";
                break;
            case "prefix":
                grant.Pattern.Name = _scope.Request.Binding.TopicName[..4];
                grant.Pattern.ResourcePatternType = ResourcePatternType.Prefixed;
                break;
            case "peer":
                grant.Entry.Principal = "peer-principal";
                break;
            case "deny":
                grant.Entry.PermissionType = AclPermissionType.Deny;
                break;
            case "host":
                grant.Entry.Host = "private-other-host";
                break;
        }
        _grants.Add(grant);
        (await Delete(Kind.PublicTopicAcls)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _filters.Should().BeEmpty();
    }

    [Test]
    public async Task It_keeps_inherited_grants_unverified_when_native_deletion_cannot_remove_them()
    {
        var inherited = _plan.BindingGrants.First(item =>
            item.ResourceName == _scope.Request.Binding.TopicName
        );
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(
                    new(true, [new(0, true, false, [])], [inherited])
                )
            );
        (await Delete(Kind.PublicTopicAcls)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_accepts_authoritative_absence_without_deleting()
    {
        _topics.Remove(_scope.Request.Binding.TopicName);
        (await Delete(Kind.PublicTopic)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _deleted.Should().BeEmpty();
    }

    [Test]
    public async Task It_rejects_unknown_authorization_without_effects()
    {
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Unavailable(
                    new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                )
            );
        (await Delete(Kind.PublicTopicAcls)).State.Should().Be(CdcTransportEvidenceState.Unavailable);
        _filters.Should().BeEmpty();
    }

    [Test]
    public async Task It_bounds_unconfirmed_topic_deletion_and_retains_the_topic()
    {
        A.CallTo(() => _client.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .Returns(Task.CompletedTask);
        (await Delete(Kind.PublicTopic))
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Failure.Should()
            .Be(CdcDeploymentFailure.Timeout);
        _topics.Should().Contain(_scope.Request.Binding.TopicName);
    }

    [Test]
    public async Task It_accepts_explicit_authorization_disabled_absence_without_deleting_grants()
    {
        _scope = CdcArtifactCleanupTestData.Scope(CdcDeploymentRequestTestData.Request(provider));
        A.CallTo(() => _authorization.InspectAsync(A<IReadOnlyList<int>>._, A<CancellationToken>._))
            .Returns(
                new CdcTransportResult<CdcKafkaAuthorizationDeploymentEvidence>.Observed(
                    new(true, [new(0, false, false, [])], [])
                )
            );
        (await Delete(Kind.PublicTopicAcls)).State.Should().Be(CdcTransportEvidenceState.Observed);
        _filters.Should().BeEmpty();
        A.CallTo(() => _client.DescribeAclsAsync(A<AclBindingFilter>._, A<DescribeAclsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_preserves_cancellation_before_effects()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () => _adapter.DeleteAsync(_scope, Kind.PublicTopic, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        _deleted.Should().BeEmpty();
    }
}
