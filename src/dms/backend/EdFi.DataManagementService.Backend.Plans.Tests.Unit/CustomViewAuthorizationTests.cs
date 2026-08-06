// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorization
{
    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                "PK_Test",
                new[] { new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.Scalar) }
            ),
            columns ?? System.Array.Empty<DbColumnModel>(),
            System.Array.Empty<TableConstraint>()
        );

    private static RelationalResourceModel CreateModel(
        string project,
        string resource,
        DbTableModel root,
        IReadOnlyList<DocumentReferenceBinding>? bindings = null
    ) =>
        new(
            new QualifiedResourceName(project, resource),
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            root,
            new[] { root },
            bindings ?? System.Array.Empty<DocumentReferenceBinding>(),
            System.Array.Empty<DescriptorEdgeSource>()
        );

    private static ConcreteResourceModel CreateConcrete(
        short keyId,
        string project,
        string resource,
        RelationalResourceModel model
    ) =>
        new(
            new ResourceKeyEntry(keyId, new QualifiedResourceName(project, resource), "1.0", false),
            ResourceStorageKind.RelationalTables,
            model
        );

    private static DerivedRelationalModelSet CreateModelSet(IReadOnlyList<ConcreteResourceModel> resources) =>
        new(
            new EffectiveSchemaInfo(
                "1.0",
                "1.0",
                "test",
                0,
                System.Array.Empty<byte>(),
                System.Array.Empty<SchemaComponentInfo>(),
                System.Array.Empty<ResourceKeyEntry>()
            ),
            SqlDialect.Pgsql,
            System.Array.Empty<ProjectSchemaInfo>(),
            resources,
            System.Array.Empty<AbstractIdentityTableInfo>(),
            System.Array.Empty<AbstractUnionViewInfo>(),
            System.Array.Empty<DbIndexInfo>(),
            System.Array.Empty<DbTriggerInfo>()
        );

    [Test]
    public void It_should_report_missing_join_path_as_security_configuration()
    {
        // Subject resource present in model set
        var subjectRoot = CreateRootTable(new DbTableName(new DbSchemaName("edfi"), "Student"));
        var subjectModel = CreateModel("Ed-Fi", "Student", subjectRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "Student", subjectModel);

        var modelSet = CreateModelSet(new[] { subject });

        var mappingSet = new MappingSet(
            new MappingSetKey("hash", SqlDialect.Pgsql, "v1"),
            modelSet,
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>(),
            new Dictionary<short, ResourceKeyEntry>(),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        // Strategy basis resource not present in the model set -> no join path
        var configured = new ConfiguredAuthorizationStrategy("StudentWithCustomAuth", 0);
        var supported = new SupportedCustomViewAuthorizationStrategy(
            configured,
            0,
            new QualifiedResourceName("Ed-Fi", "MissingBasis")
        );

        var outcome = CustomViewAuthorizationPlanner.Plan(mappingSet, subject, new[] { supported });

        outcome.Should().BeOfType<CustomViewAuthorizationPlanOutcome.SecurityConfiguration>();

        var sec = (CustomViewAuthorizationPlanOutcome.SecurityConfiguration)outcome;
        sec.Failures.Should().ContainSingle();
        sec.Failures[0].FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoCustomViewJoinPath);
        sec.PlannedChecks.Should().BeEmpty();
    }

    [Test]
    public void It_should_keep_successfully_planned_checks_when_a_later_strategy_has_no_join_path()
    {
        // Custom views are AND filters executing in CMS-configured order, so a later strategy that cannot
        // be planned must not discard the earlier ones: the caller still has to validate the earlier auth
        // views before reporting this failure, otherwise an earlier missing view is masked.
        var subjectRoot = CreateRootTable(new DbTableName(new DbSchemaName("edfi"), "Student"));
        var subjectModel = CreateModel("Ed-Fi", "Student", subjectRoot);
        var subject = CreateConcrete(1, "Ed-Fi", "Student", subjectModel);

        var mappingSet = new MappingSet(
            new MappingSetKey("hash", SqlDialect.Pgsql, "v1"),
            CreateModelSet(new[] { subject }),
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>(),
            new Dictionary<short, ResourceKeyEntry>(),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        // Index 0 resolves against the subject itself; index 1 has an unresolvable basis resource.
        var resolvable = new SupportedCustomViewAuthorizationStrategy(
            new ConfiguredAuthorizationStrategy("StudentWithResolvableCustomView", 0),
            0,
            new QualifiedResourceName("Ed-Fi", "Student")
        );
        var unresolvable = new SupportedCustomViewAuthorizationStrategy(
            new ConfiguredAuthorizationStrategy("MissingBasisWithCustomView", 1),
            1,
            new QualifiedResourceName("Ed-Fi", "MissingBasis")
        );

        var outcome = CustomViewAuthorizationPlanner.Plan(
            mappingSet,
            subject,
            new[] { resolvable, unresolvable }
        );

        var sec = outcome
            .Should()
            .BeOfType<CustomViewAuthorizationPlanOutcome.SecurityConfiguration>()
            .Subject;
        sec.Failures.Should().ContainSingle();
        sec.Failures[0].FailureKind.Should().Be(RelationshipAuthorizationFailureKind.NoCustomViewJoinPath);
        sec.Failures[0].ConfiguredStrategy!.RawConfiguredIndex.Should().Be(1);
        sec.PlannedChecks.Should().ContainSingle();
        sec.PlannedChecks[0].ConfiguredStrategy.StrategyName.Should().Be("StudentWithResolvableCustomView");
    }

    [Test]
    public void Adapter_should_create_page_custom_view_checks()
    {
        var configured = new ConfiguredAuthorizationStrategy("MyCustomView", 3);
        var pathStep = new ColumnPathStep(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            new DbColumnName("Student_DocumentId"),
            null,
            null
        );

        var check = new CustomViewAuthorizationCheckSpec(
            configured,
            new DbTableName(new DbSchemaName("edfi"), "CourseTranscript"),
            new DbColumnName("DocumentId"),
            new DbTableName(new DbSchemaName("auth"), "MyCustomView"),
            new DbColumnName("DocumentId"),
            new[] { pathStep }
        );

        var adapted = PageDocumentIdCustomViewAdapter.AdaptFromChecks(new[] { check });

        adapted.Should().HaveCount(1);
        var first = adapted[0];
        first.StrategyName.Should().Be("MyCustomView");
        first.AuthView.Should().Be(new DbTableName(new DbSchemaName("auth"), "MyCustomView"));
        first.AuthViewDocumentIdColumn.Should().Be(new DbColumnName("DocumentId"));
        first.PathToBasisResource.Should().HaveCount(1);
        first.RootTable.Should().Be(new DbTableName(new DbSchemaName("edfi"), "CourseTranscript"));
        first.RootDocumentIdColumn.Should().Be(new DbColumnName("DocumentId"));
    }
}
