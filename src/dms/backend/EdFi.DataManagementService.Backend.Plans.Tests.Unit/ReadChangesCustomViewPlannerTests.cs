// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

public abstract class ReadChangesCustomViewPlannerTestBase(SqlDialect dialect, bool includingDeletes)
{
    protected DerivedRelationalModelSet Model { get; private set; } = null!;
    protected bool IncludingDeletes { get; } = includingDeletes;
    private MappingSet _mapping = null!;

    [OneTimeSetUp]
    public void LoadModel()
    {
        (Model, _mapping) = Ds52FixtureHelper.BuildAndCompile(dialect);
    }

    protected ReadChangesCustomViewCheckSpec Plan(string subjectName, string basisName)
    {
        ConcreteResourceModel subject = Model.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", subjectName)
        );
        string strategyName = basisName + "WithCustomRule" + (IncludingDeletes ? "IncludingDeletes" : "");
        return ReadChangesAuthorizationPlanner
            .Plan(
                _mapping,
                subject,
                TrackedTable(subjectName),
                [new ConfiguredAuthorizationStrategy(strategyName, 0)],
                new RelationalAuthorizationContext([1L], [])
            )
            .Should()
            .BeOfType<ReadChangesAuthorizationPlanOutcome.Plan>()
            .Subject.AuthorizationPlan.CustomViewChecks.Should()
            .ContainSingle()
            .Subject;
    }

    protected TrackedChangeTableInfo TrackedTable(string resourceName) =>
        Model.TrackedChangeTablesInNameOrder.Single(table =>
            table.SourceTable == new DbTableName(new DbSchemaName("edfi"), resourceName)
        );

    protected DbColumnName OldDescriptorColumn(
        string resourceName,
        string sourcePath,
        TrackedChangeColumnRole role
    ) =>
        TrackedTable(resourceName)
            .ValueColumnsInTableOrder.Single(column =>
                column.SourceJsonPath == sourcePath && column.Role == role
            )
            .OldColumnName;
}

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_ReadChanges_Custom_View_With_Abstract_Self_Basis(SqlDialect dialect, bool includingDeletes)
    : ReadChangesCustomViewPlannerTestBase(dialect, includingDeletes)
{
    private readonly List<ReadChangesCustomViewCheckSpec> _checks = [];

    [SetUp]
    public void Setup()
    {
        _checks.Clear();
        foreach (AbstractUnionViewInfo view in Model.AbstractUnionViewsInNameOrder)
        {
            foreach (AbstractUnionViewArm member in view.UnionArmsInOrder)
            {
                _checks.Add(
                    Plan(
                        member.ConcreteMemberResourceKey.Resource.ResourceName,
                        view.AbstractResourceKey.Resource.ResourceName
                    )
                );
            }
        }
    }

    [Test]
    public void It_authorizes_each_concrete_member_through_its_own_stored_document_id()
    {
        _checks
            .Should()
            .NotBeEmpty()
            .And.AllSatisfy(check =>
                check
                    .Basis.Should()
                    .BeEquivalentTo(
                        new ReadChangesCustomViewBasis.StoredDocumentId(new DbColumnName("DocumentId"))
                    )
            );
    }
}

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_ReadChanges_Custom_View_With_Unified_Descriptor_Basis(
    SqlDialect dialect,
    bool includingDeletes
) : ReadChangesCustomViewPlannerTestBase(dialect, includingDeletes)
{
    private ReadChangesCustomViewBasis.DescriptorSeek _seek = null!;

    [SetUp]
    public void Setup()
    {
        _seek = Plan("ProgramEvaluationElement", "ProgramEvaluationPeriodDescriptor")
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.DescriptorSeek>()
            .Subject;
    }

    [Test]
    public void It_seeks_the_descriptor_using_the_stored_identifying_namespace_and_code_value()
    {
        const string sourcePath = "$.programEvaluationReference.programEvaluationPeriodDescriptor";
        _seek
            .Should()
            .BeEquivalentTo(
                new ReadChangesCustomViewBasis.DescriptorSeek(
                    new QualifiedResourceName("Ed-Fi", "ProgramEvaluationPeriodDescriptor"),
                    OldDescriptorColumn(
                        "ProgramEvaluationElement",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorNamespace
                    ),
                    OldDescriptorColumn(
                        "ProgramEvaluationElement",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorCodeValue
                    ),
                    _seek.ProbeArm
                )
            );
    }

    [Test]
    public void It_probes_descriptor_tombstones_only_when_the_suffix_is_configured()
    {
        (_seek.ProbeArm is not null).Should().Be(IncludingDeletes);
    }
}

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_ReadChanges_Custom_View_With_Unified_Descriptor_Identity(
    SqlDialect dialect,
    bool includingDeletes
) : ReadChangesCustomViewPlannerTestBase(dialect, includingDeletes)
{
    private ReadChangesCustomViewBasis.LiveSeek _seek = null!;
    private ReadChangesCustomViewDescriptorKeyPair _periodPair = null!;

    [SetUp]
    public void Setup()
    {
        _seek = Plan("EvaluationRubricDimension", "ProgramEvaluationElement")
            .Basis.Should()
            .BeOfType<ReadChangesCustomViewBasis.LiveSeek>()
            .Subject;
        _periodPair = _seek.DescriptorKeyPairs.Single(pair =>
            pair.DescriptorResource == new QualifiedResourceName("Ed-Fi", "ProgramEvaluationPeriodDescriptor")
        );
    }

    [Test]
    public void It_seeks_the_live_basis_through_the_canonical_descriptor_key_and_old_subject_values()
    {
        const string sourcePath = "$.programEvaluationElementReference.programEvaluationPeriodDescriptor";
        _periodPair
            .Should()
            .Be(
                new ReadChangesCustomViewDescriptorKeyPair(
                    new DbColumnName("ProgramEvaluationPeriodDescriptor_Unified_DescriptorId"),
                    OldDescriptorColumn(
                        "EvaluationRubricDimension",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorNamespace
                    ),
                    OldDescriptorColumn(
                        "EvaluationRubricDimension",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorCodeValue
                    ),
                    new QualifiedResourceName("Ed-Fi", "ProgramEvaluationPeriodDescriptor")
                )
            );
    }

    [Test]
    public void It_probes_the_basis_using_its_old_descriptor_values_only_when_the_suffix_is_configured()
    {
        if (!IncludingDeletes)
        {
            _seek.ProbeArms.Should().BeEmpty();
            return;
        }

        const string sourcePath = "$.programEvaluationReference.programEvaluationPeriodDescriptor";
        _seek
            .ProbeArms.Should()
            .ContainSingle()
            .Which.DescriptorKeyPairs.Should()
            .Contain(
                new ReadChangesCustomViewProbeDescriptorKeyPair(
                    OldDescriptorColumn(
                        "ProgramEvaluationElement",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorNamespace
                    ),
                    OldDescriptorColumn(
                        "ProgramEvaluationElement",
                        sourcePath,
                        TrackedChangeColumnRole.DescriptorCodeValue
                    ),
                    _periodPair.TrackedOldNamespaceColumn,
                    _periodPair.TrackedOldCodeValueColumn
                )
            );
    }
}
