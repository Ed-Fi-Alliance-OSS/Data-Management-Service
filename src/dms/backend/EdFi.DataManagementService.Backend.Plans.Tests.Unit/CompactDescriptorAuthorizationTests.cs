// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_CompactDescriptor_CustomViewAuthorization_And_NamespaceAuthorization(SqlDialect dialect)
{
    private CompactDescriptorAuthorizationFixture _fixture = null!;
    private IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> _customChecks = null!;
    private string _customSql = null!;

    private string Quote(string name) => dialect is SqlDialect.Pgsql ? $"\"{name}\"" : $"[{name}]";

    private NamespacePrefixParameterization Prefixes =>
        NamespacePrefixParameterizationFactory.Create(
            dialect,
            ["uri://Example.org/Kind "],
            "namespacePrefixes"
        );

    [SetUp]
    public void Setup()
    {
        _fixture = new(dialect);
        _customChecks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Subject,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.EdFiKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        _customSql = new SingleRecordCustomViewAuthorizationSqlCompiler(dialect)
            .Compile(new(_customChecks, "documentId"))
            .AuthorizationSql;
    }

    [Test]
    public void It_bridges_the_stored_compact_key_to_the_auth_view_document_key()
    {
        _customChecks[0]
            .PathToBasisResource.Should()
            .Equal(
                new ColumnPathStep(
                    _fixture.Subject.RelationalModel.Root.Table,
                    new("Kind_DescriptorId"),
                    CompactDescriptorAuthorizationFixture.DescriptorTable,
                    new("DescriptorId")
                ),
                new ColumnPathStep(
                    CompactDescriptorAuthorizationFixture.DescriptorTable,
                    new("DocumentId"),
                    null,
                    null
                )
            );
        _customSql.Should().Contain($"t1.{Quote("DescriptorId")} = r.{Quote("Kind_DescriptorId")}");
        _customSql.Should().Contain($"t1.{Quote("DocumentId")} IN (SELECT cv.{Quote("DocumentId")}");
        _customSql.Should().NotContain($"r.{Quote("Kind_DescriptorId")} IN (SELECT cv.{Quote("DocumentId")}");
    }

    [Test]
    public void It_looks_up_a_proposed_int_key_before_testing_document_membership()
    {
        var binding = ((CustomViewAuthorizationCheckTarget.Proposed)_customChecks[1].CheckTarget).Binding;
        binding.KeyScalarKind.Should().Be(ScalarKind.Int32);
        _customSql
            .Should()
            .Contain(
                $"WHERE t1.{Quote("DescriptorId")} = @customViewAuthorization1 AND t1.{Quote("DocumentId")} IN (SELECT cv.{Quote("DocumentId")}"
            );
        _customSql.Should().NotContain("WHEN @customViewAuthorization1 IN");
    }

    [Test]
    public void It_preserves_stored_before_proposed_checks_and_null_and_denial_branches()
    {
        _customChecks
            .Select(c => c.ValueSource)
            .Should()
            .Equal(
                CustomViewAuthorizationCheckValueSource.Stored,
                CustomViewAuthorizationCheckValueSource.Proposed
            );
        _customSql
            .IndexOf("cv1|0|", StringComparison.Ordinal)
            .Should()
            .BeLessThan(_customSql.IndexOf("cv1|1|", StringComparison.Ordinal));
        _customSql.Should().Contain("THEN 1").And.Contain("cv1|0|n").And.Contain("cv1|1|n");
    }

    [Test]
    public void It_applies_the_custom_view_descriptor_bridge_to_page_and_total_count_sql()
    {
        var custom = (
            (CustomViewAuthorizationPlanOutcome.Plan)
                CustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Subject,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.EdFiKind
                        ),
                    ]
                )
        ).Checks;
        var plan = new PageDocumentIdSqlCompiler(dialect).Compile(
            new(
                _fixture.Subject.RelationalModel.Root.Table,
                [],
                new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                Mode: new PageCandidateMode.Traditional(IncludeTotalCountSql: true),
                Authorization: new(
                    [],
                    CustomViewChecks: PageDocumentIdCustomViewAdapter.AdaptFromChecks(custom)
                )
            )
        );
        foreach (var sql in new[] { plan.PageDocumentIdSql, plan.TotalCountSql })
        {
            sql.Should().Contain($"{Quote("DescriptorId")} = t0.{Quote("Kind_DescriptorId")}");
            sql.Should().Contain($"{Quote("DocumentId")} IN (SELECT");
        }
    }

    [Test]
    public void It_preserves_a_wide_first_hop_for_an_indirect_descriptor_basis()
    {
        var checks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Intermediate,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.EdFiKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        checks[0].PathToBasisResource.Should().HaveCount(3);
        ((CustomViewAuthorizationCheckTarget.Proposed)checks[1].CheckTarget)
            .Binding.KeyScalarKind.Should()
            .Be(ScalarKind.Int64);
        var sql = new SingleRecordCustomViewAuthorizationSqlCompiler(dialect)
            .Compile(new(checks, "documentId"))
            .AuthorizationSql;
        sql.Should().Contain($"t2.{Quote("DescriptorId")} = t1.{Quote("Kind_DescriptorId")}");
        sql.Should().Contain($"t2.{Quote("DocumentId")} IN (SELECT cv.{Quote("DocumentId")}");
    }

    [Test]
    public void It_selects_same_named_descriptor_types_by_qualified_resource_metadata()
    {
        _fixture
            .MappingSet.ResourceKeyIdByResource[CompactDescriptorAuthorizationFixture.EdFiKind]
            .Should()
            .Be(101);
        _fixture
            .MappingSet.ResourceKeyIdByResource[CompactDescriptorAuthorizationFixture.SampleKind]
            .Should()
            .Be(202);
        var checks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Subject,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.SampleKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        checks[0].BasisResource.Should().Be(CompactDescriptorAuthorizationFixture.SampleKind);
        checks[0]
            .PathToBasisResource[0]
            .SourceColumnName.Should()
            .Be(new DbColumnName("SampleKind_DescriptorId"));
    }

    [Test]
    public void It_fails_closed_for_descriptor_reference_namespace_bases(
        [Values(
            NamespaceAuthorizationOperation.ReadSingle,
            NamespaceAuthorizationOperation.ReadMany,
            NamespaceAuthorizationOperation.Update,
            NamespaceAuthorizationOperation.Delete
        )]
            NamespaceAuthorizationOperation operation,
        [Values(false, true)] bool hasPrefixes
    )
    {
        var outcome = NamespaceAuthorizationPlanner.Plan(
            _fixture.Subject,
            operation,
            new([], hasPrefixes ? ["uri://Example.org/"] : [])
        );

        outcome
            .Should()
            .Be(
                new NamespaceAuthorizationPlanOutcome.NoUsableRootColumn(
                    _fixture.Subject.ResourceKey.Resource
                )
            );
    }

    [Test]
    public void It_keeps_descriptor_self_basis_and_namespace_checks_document_keyed()
    {
        var descriptor = _fixture.MappingSet.Model.ConcreteResourcesInNameOrder.Single(r =>
            r.ResourceKey.Resource == CompactDescriptorAuthorizationFixture.SampleKind
        );
        var checks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    descriptor,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.SampleKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        checks[0]
            .PathToBasisResource.Should()
            .Equal(
                new ColumnPathStep(
                    CompactDescriptorAuthorizationFixture.DescriptorTable,
                    new("DocumentId"),
                    null,
                    null
                )
            );
        checks[1]
            .CheckTarget.Should()
            .BeOfType<CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable>();
        var ns = (
            (NamespaceAuthorizationPlanOutcome.Plan)
                NamespaceAuthorizationPlanner.Plan(
                    descriptor,
                    NamespaceAuthorizationOperation.Update,
                    new([], ["uri://Example.org/"])
                )
        ).Checks;
        ns.Should().OnlyContain(c => c.NamespaceColumn == new DbColumnName("Namespace"));
        var sql = new NamespaceAuthorizationSqlCompiler(dialect)
            .Compile(new(ns, Prefixes, "documentId", "proposedNamespace"))
            .AuthorizationSql;
        sql.Should().Contain($"r.{Quote("DocumentId")} = @documentId").And.NotContain("DescriptorId");
    }
}
