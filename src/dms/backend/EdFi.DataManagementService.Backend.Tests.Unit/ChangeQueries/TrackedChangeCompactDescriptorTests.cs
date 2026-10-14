// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_TrackedChange_Compact_Descriptor_Routing(SqlDialect dialect)
{
    private CompactDescriptorHistoryFixture _fixture = null!;
    private TrackedChangeQueryPlan _resourcePlan = null!;
    private IReadOnlyList<TrackedChangeQueryPlan> _descriptorPlans = [];
    private TrackedChangeAuthorizationSql _customViewSql = TrackedChangeAuthorizationSql.None;

    [SetUp]
    public void Setup()
    {
        _fixture = new(dialect);
        var customChecks = ReadChangesCustomViewPlanner.Plan(
            _fixture.MappingSet,
            _fixture.Authorization.Subject,
            _fixture.ResourceHistory,
            [
                Strategy(CompactDescriptorAuthorizationFixture.EdFiKind, 0),
                Strategy(CompactDescriptorAuthorizationFixture.SampleKind, 1),
            ]
        );
        customChecks.Failures.Should().BeEmpty();
        _customViewSql = TrackedChangeAuthorizationSqlEmitter.Emit(
            new([], null, null, null, customChecks.Checks),
            dialect,
            "c",
            A.Fake<IRelationalParameterConfigurator>()
        );
        var planner = new TrackedChangeQueryPlanner(dialect);
        _resourcePlan = planner.Plan(
            Request(_fixture.Authorization.Subject, _fixture.ResourceHistory),
            ResourceFields(),
            _customViewSql
        );
        _descriptorPlans =
        [
            .. new[]
            {
                CompactDescriptorAuthorizationFixture.EdFiKind,
                CompactDescriptorAuthorizationFixture.SampleKind,
            }.Select(resource =>
            {
                ConcreteResourceModel descriptor = _fixture.Descriptor(resource);
                var outcome = ReadChangesAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    descriptor,
                    _fixture.DescriptorHistory,
                    [new("NamespaceBased", 0)],
                    new([], ["uri://Example.org/Kind "])
                );
                ReadChangesAuthorizationPlan auth = outcome
                    .Should()
                    .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
                    .Subject.AuthorizationPlan;
                return planner.Plan(
                    Request(descriptor, _fixture.DescriptorHistory),
                    DescriptorFields(),
                    TrackedChangeAuthorizationSqlEmitter.Emit(
                        auth,
                        dialect,
                        "c",
                        A.Fake<IRelationalParameterConfigurator>()
                    )
                );
            }),
        ];
    }

    [Test]
    public void It_routes_shared_history_and_recreation_seeks_to_each_full_project_resource_key()
    {
        for (var index = 0; index < _descriptorPlans.Count; index++)
        {
            RelationalCommand command = _descriptorPlans[index].Command!;
            command
                .CommandText.Should()
                .Contain($"c.{Q("ResourceKeyId")} = @ResourceKeyId")
                .And.Contain($"live.{Q("ResourceKeyId")} = @ResourceKeyId")
                .And.Contain($"live.{Q("Namespace")} = c.{Q("OldNamespace")}")
                .And.Contain($"live.{Q("CodeValue")} = c.{Q("OldCodeValue")}")
                .And.NotContain("Discriminator")
                .And.NotContain("LOWER(")
                .And.NotContain(Q("Uri"))
                .And.NotContain("JOIN " + Table("dms", "Document"));
            command
                .Parameters.Single(parameter => parameter.Name == "@ResourceKeyId")
                .Value.Should()
                .BeOfType<short>()
                .Which.Should()
                .Be(index == 0 ? (short)101 : (short)202);
            command
                .Parameters.Select(parameter => parameter.Name)
                .Should()
                .Equal(
                    "@Limit",
                    "@Offset",
                    "@ResourceKeyId",
                    dialect is SqlDialect.Pgsql
                        ? "@ReadChangesNamespacePrefix"
                        : "@ReadChangesNamespacePrefix_0"
                );
        }
    }

    [Test]
    public void It_compares_both_compact_resource_foreign_keys_to_descriptor_ids()
    {
        string sql = _resourcePlan.Command!.CommandText;
        sql.Should()
            .Contain($"live.{Q("Kind_DescriptorId")} = descriptor_0.{Q("DescriptorId")}")
            .And.Contain($"live.{Q("SampleKind_DescriptorId")} = descriptor_1.{Q("DescriptorId")}")
            .And.Contain($"descriptor_0.{Q("ResourceKeyId")} = @DescriptorResourceKeyId0")
            .And.Contain($"descriptor_1.{Q("ResourceKeyId")} = @DescriptorResourceKeyId1")
            .And.NotContain($"descriptor_0.{Q("DocumentId")}")
            .And.NotContain($"descriptor_1.{Q("DocumentId")}");
        _resourcePlan
            .Command.Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(
                ("@Limit", 25L),
                ("@Offset", 0L),
                ("@DescriptorResourceKeyId0", (short)101),
                ("@DescriptorResourceKeyId1", (short)202),
                ("@CustomViewDescriptorResourceKeyId0", (short)101),
                ("@CustomViewDescriptorResourceKeyId1", (short)202)
            );
    }

    [Test]
    public void It_authorizes_deleted_bases_from_type_qualified_history_document_ids()
    {
        _customViewSql.Predicates.Should().HaveCount(2);
        for (var index = 0; index < _customViewSql.Predicates.Count; index++)
        {
            _customViewSql
                .Predicates[index]
                .Should()
                .Contain($"SELECT d.{Q("DocumentId")} AS {Q("DocumentId")}")
                .And.Contain(
                    $"UNION SELECT t.{Q("DocumentId")} FROM {Table("tracked_changes_edfi", "Descriptor")} t"
                )
                .And.Contain($"t.{Q("ResourceKeyId")} = @CustomViewDescriptorResourceKeyId{index}")
                .And.Contain(
                    $"t.{Q("OldNamespace")} = c.{Q(index == 0 ? "OldKind_Namespace" : "OldSampleKind_Namespace")}"
                )
                .And.Contain($"basis.{Q("DocumentId")} IN (SELECT {Q("DocumentId")} FROM")
                .And.NotContain(Q("DescriptorId"))
                .And.NotContain("JOIN " + Table("dms", "Document"));
        }
    }

    [Test]
    public void It_authorizes_shared_descriptor_history_from_its_retained_namespace()
    {
        foreach (var plan in _descriptorPlans)
        {
            string sql = plan.Command!.CommandText;
            sql.Should()
                .Contain($"c.{Q("OldNamespace")} IS NOT NULL")
                .And.Contain($"c.{Q("OldNamespace")} LIKE");
            sql.Should()
                .Contain($"c.{Q("OldNamespace")} AS {Q("namespace__old")}")
                .And.Contain($"c.{Q("OldCodeValue")} AS {Q("codeValue__old")}");
        }
    }

    [Test]
    public void It_fails_when_the_requested_project_has_no_resource_key_instead_of_using_a_same_named_type()
    {
        IRelationalTrackedChangeQueryRequest request = Request(
            _fixture.Descriptor(CompactDescriptorAuthorizationFixture.SampleKind),
            _fixture.DescriptorHistory
        );
        A.CallTo(() => request.MappingSet)
            .Returns(
                _fixture.MappingSet with
                {
                    ResourceKeyIdByResource = new Dictionary<QualifiedResourceName, short>
                    {
                        [CompactDescriptorAuthorizationFixture.EdFiKind] = 101,
                    },
                }
            );
        Action act = () =>
            new TrackedChangeQueryPlanner(dialect).Plan(
                request,
                DescriptorFields(),
                TrackedChangeAuthorizationSql.None
            );
        act.Should().Throw<KeyNotFoundException>().WithMessage("*Sample.KindDescriptor*");
    }

    private IRelationalTrackedChangeQueryRequest Request(
        ConcreteResourceModel resource,
        TrackedChangeTableInfo history
    )
    {
        var request = A.Fake<IRelationalTrackedChangeQueryRequest>();
        A.CallTo(() => request.Operation).Returns(ChangeQueryEndpointOperation.Deletes);
        A.CallTo(() => request.ResourceInfo)
            .Returns(
                new(
                    new(resource.ResourceKey.Resource.ProjectName),
                    new(resource.ResourceKey.Resource.ResourceName),
                    resource.StorageKind is ResourceStorageKind.SharedDescriptorTable,
                    new SemVer("5.2.0"),
                    AllowIdentityUpdates: false
                )
            );
        A.CallTo(() => request.MappingSet).Returns(_fixture.MappingSet);
        A.CallTo(() => request.ResourceModel).Returns(resource);
        A.CallTo(() => request.TrackedChangeTable).Returns(history);
        A.CallTo(() => request.PaginationParameters).Returns(new(25, 0, false, 500));
        A.CallTo(() => request.ChangeVersionRange).Returns(ChangeVersionRange.None);
        return request;
    }

    private ChangeQueryResponseField[] ResourceFields() =>
        [
            new(
                "kindDescriptor",
                ChangeQueryResponseFieldKind.Descriptor,
                _fixture.ResourceHistory.ValueColumnsInTableOrder[0],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[0],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[1],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[1]
            ),
            new(
                "sampleKindDescriptor",
                ChangeQueryResponseFieldKind.Descriptor,
                _fixture.ResourceHistory.ValueColumnsInTableOrder[2],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[2],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[3],
                _fixture.ResourceHistory.ValueColumnsInTableOrder[3]
            ),
        ];

    private ChangeQueryResponseField[] DescriptorFields() =>
        [
            new(
                "namespace",
                ChangeQueryResponseFieldKind.Scalar,
                _fixture.DescriptorHistory.ValueColumnsInTableOrder[0],
                _fixture.DescriptorHistory.ValueColumnsInTableOrder[0],
                null,
                null
            ),
            new(
                "codeValue",
                ChangeQueryResponseFieldKind.Scalar,
                _fixture.DescriptorHistory.ValueColumnsInTableOrder[1],
                _fixture.DescriptorHistory.ValueColumnsInTableOrder[1],
                null,
                null
            ),
        ];

    private static SupportedCustomViewAuthorizationStrategy Strategy(
        QualifiedResourceName resource,
        int index
    ) => new(new($"{resource.ResourceName}WithAllowedRowsIncludingDeletes", index), index, resource);

    private string Q(string name) => SqlIdentifierQuoter.QuoteIdentifier(dialect, name);

    private string Table(string schema, string name) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, new(new(schema), name));
}
