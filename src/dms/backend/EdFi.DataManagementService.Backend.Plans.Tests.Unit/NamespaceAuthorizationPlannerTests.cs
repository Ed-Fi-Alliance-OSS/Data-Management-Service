// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespaceAuthorizationPlanner
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string resource) =>
        new(id, new QualifiedResourceName("Ed-Fi", resource), "1.0", false);

    private static DbTableModel RootTable(string name, IReadOnlyList<DbColumnModel> columns) =>
        new(
            Table(name),
            Path("$"),
            new TableKey("PK_" + name, [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            columns,
            []
        );

    private static DbTableModel ChildTable(
        string name,
        string scopePath,
        IReadOnlyList<DbColumnModel> columns
    ) =>
        new(
            Table(name),
            Path(scopePath),
            new TableKey("PK_" + name, [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.ParentKeyPart)]),
            columns,
            []
        );

    private static ConcreteResourceModel RootNamespaceResource()
    {
        // AcademicWeek-shape: root-table scalar Namespace at $.namespace.
        var root = RootTable(
            "AcademicWeek",
            [new DbColumnModel(Col("Namespace"), ColumnKind.Scalar, null, false, Path("$.namespace"), null)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "AcademicWeek"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(1, "AcademicWeek"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace"], [], [], []),
        };
    }

    private static ConcreteResourceModel ChildCollectionOnlyNamespaceResource()
    {
        // GraduationPlan-shape: only securable element is array-nested at
        // $.requiredAssessments[*].assessmentReference.namespace. The resolver returns a
        // child-table source, which the planner must reject.
        var root = RootTable("GraduationPlanLike", []);
        var childName = Table("GraduationPlanLikeRequiredAssessment");
        var nestedCol = Col("RequiredAssessmentAssessment_Namespace");
        const string nestedPath = "$.requiredAssessments[*].assessmentReference.namespace";
        var child = ChildTable(
            "GraduationPlanLikeRequiredAssessment",
            "$.requiredAssessments[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("RequiredAssessmentAssessment_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Assessment")
                ),
                new DbColumnModel(nestedCol, ColumnKind.Scalar, null, false, null, null),
            ]
        );
        var binding = new DocumentReferenceBinding(
            true,
            Path("$.requiredAssessments[*].assessmentReference"),
            childName,
            Col("RequiredAssessmentAssessment_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Assessment"),
            [new ReferenceIdentityBinding(Path("$.namespace"), Path(nestedPath), nestedCol)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "GraduationPlanLike"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root, child],
            [binding],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(2, "GraduationPlanLike"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], [nestedPath], [], [], []),
        };
    }

    private static ConcreteResourceModel MixedNamespaceResource()
    {
        // Resource declares two Namespace securable element paths: one root-level scalar and one
        // array-nested. The planner must keep the root-table column and discard the child one.
        var rootNamespaceCol = Col("Namespace");
        var nestedNamespaceCol = Col("RequiredAssessmentAssessment_Namespace");
        const string nestedPath = "$.requiredAssessments[*].assessmentReference.namespace";

        var root = RootTable(
            "MixedNamespace",
            [new DbColumnModel(rootNamespaceCol, ColumnKind.Scalar, null, false, Path("$.namespace"), null)]
        );
        var childName = Table("MixedNamespaceRequiredAssessment");
        var child = ChildTable(
            "MixedNamespaceRequiredAssessment",
            "$.requiredAssessments[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("RequiredAssessmentAssessment_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Assessment")
                ),
                new DbColumnModel(nestedNamespaceCol, ColumnKind.Scalar, null, false, null, null),
            ]
        );
        var binding = new DocumentReferenceBinding(
            true,
            Path("$.requiredAssessments[*].assessmentReference"),
            childName,
            Col("RequiredAssessmentAssessment_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Assessment"),
            [new ReferenceIdentityBinding(Path("$.namespace"), Path(nestedPath), nestedNamespaceCol)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "MixedNamespace"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root, child],
            [binding],
            []
        );
        return new ConcreteResourceModel(
            ResourceKey(3, "MixedNamespace"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements([], ["$.namespace", nestedPath], [], [], []),
        };
    }

    private static RelationalAuthorizationContext TwoPrefixContext() =>
        new([], ["uri://ed-fi.org/", "uri://gbisd.edu/"]);

    private static RelationalAuthorizationContext EmptyPrefixContext() => new([], []);

    [Test]
    public void It_returns_no_usable_root_column_when_only_child_collection_namespace_paths_resolve()
    {
        var resource = ChildCollectionOnlyNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoUsableRootColumn>();
        ((NamespaceAuthorizationPlanOutcome.NoUsableRootColumn)outcome)
            .Resource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "GraduationPlanLike"));
    }

    [Test]
    public void It_keeps_only_the_root_table_namespace_when_both_root_and_child_collection_paths_resolve()
    {
        var resource = MixedNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>();
        var plan = (NamespaceAuthorizationPlanOutcome.Plan)outcome;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].RootTable.Should().Be(Table("MixedNamespace"));
        plan.Checks[0].NamespaceColumn.Should().Be(Col("Namespace"));
    }

    [Test]
    public void It_returns_no_usable_root_column_before_no_prefixes_when_metadata_is_broken_and_prefixes_are_empty()
    {
        var resource = ChildCollectionOnlyNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadMany,
            EmptyPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoUsableRootColumn>();
    }

    [Test]
    public void It_returns_no_prefixes_configured_when_metadata_is_valid_but_client_prefixes_are_empty()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            EmptyPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured>();
        ((NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured)outcome)
            .StrategyName.Should()
            .Be("NamespaceBased");
    }

    [Test]
    public void It_emits_a_single_stored_check_for_read_single()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].Index.Should().Be(0);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
        plan.Checks[0].RootTable.Should().Be(Table("AcademicWeek"));
        plan.Checks[0].NamespaceColumn.Should().Be(Col("Namespace"));
    }

    [Test]
    public void It_emits_a_single_stored_check_for_read_many()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadMany,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
    }

    [Test]
    public void It_emits_a_single_stored_check_for_delete()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.Delete,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
    }

    [Test]
    public void It_emits_a_single_proposed_check_for_create()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.Create,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].Index.Should().Be(0);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Proposed);
    }

    [Test]
    public void It_emits_ordered_stored_then_proposed_checks_for_update()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.Update,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(2);
        plan.Checks[0].Index.Should().Be(0);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
        plan.Checks[1].Index.Should().Be(1);
        plan.Checks[1].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Proposed);
    }

    [Test]
    public void It_carries_the_strategy_name_on_every_emitted_check()
    {
        var resource = RootNamespaceResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.Update,
            TwoPrefixContext()
        );

        var plan = (NamespaceAuthorizationPlanOutcome.Plan)outcome;
        plan.Checks.Select(static c => c.StrategyName).Should().AllBe("NamespaceBased");
    }

    [Test]
    public void It_emits_a_plan_when_unrelated_securable_metadata_is_unresolvable_alongside_a_valid_root_namespace()
    {
        // The resource declares a valid root Namespace path plus unrelated Student / Contact /
        // Staff / EducationOrganization paths that cannot be resolved. Namespace planning must
        // ignore the unrelated metadata and produce a normal Plan outcome.
        var rootNamespaceCol = Col("Namespace");
        var root = RootTable(
            "ResourceWithUnrelatedBrokenMetadata",
            [new DbColumnModel(rootNamespaceCol, ColumnKind.Scalar, null, false, Path("$.namespace"), null)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "ResourceWithUnrelatedBrokenMetadata"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        var resource = new ConcreteResourceModel(
            ResourceKey(4, "ResourceWithUnrelatedBrokenMetadata"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                Namespace: ["$.namespace"],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: ["$.contactReference.contactUniqueId"],
                Staff: ["$.staffReference.staffUniqueId"]
            ),
        };

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].RootTable.Should().Be(Table("ResourceWithUnrelatedBrokenMetadata"));
        plan.Checks[0].NamespaceColumn.Should().Be(rootNamespaceCol);
    }

    [Test]
    public void It_returns_no_usable_root_column_when_a_namespace_path_cannot_be_resolved_at_all()
    {
        // Resource declares a Namespace securable element whose JSON path does not match any
        // column on the root or child tables. The planner returns NoUsableRootColumn rather
        // than letting the unresolved path bubble up as an exception.
        var root = RootTable("ResourceWithUnresolvableNamespace", []);
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "ResourceWithUnresolvableNamespace"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root],
            [],
            []
        );
        var resource = new ConcreteResourceModel(
            ResourceKey(5, "ResourceWithUnresolvableNamespace"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: ["$.assessmentAdministrationReference.namespace"],
                Student: [],
                Contact: [],
                Staff: []
            ),
        };

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoUsableRootColumn>();
        ((NamespaceAuthorizationPlanOutcome.NoUsableRootColumn)outcome)
            .Resource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "ResourceWithUnresolvableNamespace"));
    }

    private static ConcreteResourceModel DescriptorResource(bool includeDescriptorMetadata = true)
    {
        // Descriptor resources store their Namespace on the shared dms.Descriptor root table and carry
        // empty SecurableElements; the planner must resolve the column from DescriptorMetadata rather
        // than from securable-element paths.
        var descriptorSchema = new DbSchemaName("dms");
        var root = new DbTableModel(
            new DbTableName(descriptorSchema, "Descriptor"),
            Path("$"),
            new TableKey("PK_Descriptor", [new DbKeyColumn(_documentId, ColumnKind.Scalar)]),
            [new DbColumnModel(Col("Namespace"), ColumnKind.Scalar, null, false, Path("$.namespace"), null)],
            []
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
            descriptorSchema,
            ResourceStorageKind.SharedDescriptorTable,
            root,
            [root],
            [],
            []
        );
        var descriptorMetadata = includeDescriptorMetadata
            ? new DescriptorMetadata(
                new DescriptorColumnContract(
                    Namespace: Col("Namespace"),
                    CodeValue: Col("CodeValue"),
                    ShortDescription: Col("ShortDescription"),
                    Description: Col("Description"),
                    EffectiveBeginDate: Col("EffectiveBeginDate"),
                    EffectiveEndDate: Col("EffectiveEndDate"),
                    Discriminator: null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
            : null;

        return new ConcreteResourceModel(
            new ResourceKeyEntry(7, new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"), "1.0", true),
            ResourceStorageKind.SharedDescriptorTable,
            model,
            descriptorMetadata
        );
    }

    [Test]
    public void It_resolves_the_descriptor_namespace_column_from_descriptor_metadata_when_securable_elements_are_empty()
    {
        var resource = DescriptorResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(1);
        plan.Checks[0].RootTable.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        plan.Checks[0].NamespaceColumn.Should().Be(Col("Namespace"));
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
    }

    [Test]
    public void It_emits_ordered_stored_then_proposed_descriptor_checks_for_update()
    {
        var resource = DescriptorResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.Update,
            TwoPrefixContext()
        );

        var plan = outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.Plan>().Subject;
        plan.Checks.Should().HaveCount(2);
        plan.Checks[0].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Stored);
        plan.Checks[1].ValueSource.Should().Be(NamespaceAuthorizationCheckValueSource.Proposed);
    }

    [Test]
    public void It_returns_no_prefixes_configured_for_a_descriptor_when_client_prefixes_are_empty()
    {
        var resource = DescriptorResource();

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            EmptyPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoPrefixesConfigured>();
    }

    [Test]
    public void It_fails_closed_with_no_usable_root_column_for_a_shared_descriptor_resource_missing_descriptor_metadata()
    {
        // Defensive: a SharedDescriptorTable resource with no DescriptorMetadata is impossible metadata.
        // The planner must fail closed (500) rather than authorize against an unknown column.
        var resource = DescriptorResource(includeDescriptorMetadata: false);

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoUsableRootColumn>();
    }

    [Test]
    public void It_returns_no_usable_root_column_when_namespace_path_resolves_only_to_a_child_collection_table_and_other_securable_metadata_is_unresolvable()
    {
        // Combines two namespace-planning guards: child-collection-only Namespace path plus
        // unrelated unresolved Student metadata. Namespace planning must not let the unrelated
        // metadata throw, and must still surface NoUsableRootColumn for the child-only path.
        var childName = Table("GraduationPlanLikeWithBrokenStudentRequiredAssessment");
        var nestedCol = Col("RequiredAssessmentAssessment_Namespace");
        const string nestedPath = "$.requiredAssessments[*].assessmentReference.namespace";

        var root = RootTable("GraduationPlanLikeWithBrokenStudent", []);
        var child = ChildTable(
            "GraduationPlanLikeWithBrokenStudentRequiredAssessment",
            "$.requiredAssessments[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("RequiredAssessmentAssessment_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Assessment")
                ),
                new DbColumnModel(nestedCol, ColumnKind.Scalar, null, false, null, null),
            ]
        );
        var binding = new DocumentReferenceBinding(
            true,
            Path("$.requiredAssessments[*].assessmentReference"),
            childName,
            Col("RequiredAssessmentAssessment_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "Assessment"),
            [new ReferenceIdentityBinding(Path("$.namespace"), Path(nestedPath), nestedCol)]
        );
        var model = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "GraduationPlanLikeWithBrokenStudent"),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root, child],
            [binding],
            []
        );
        var resource = new ConcreteResourceModel(
            ResourceKey(6, "GraduationPlanLikeWithBrokenStudent"),
            ResourceStorageKind.RelationalTables,
            model
        )
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [nestedPath],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: [],
                Staff: []
            ),
        };

        var outcome = NamespaceAuthorizationPlanner.Plan(
            resource,
            NamespaceAuthorizationOperation.ReadSingle,
            TwoPrefixContext()
        );

        outcome.Should().BeOfType<NamespaceAuthorizationPlanOutcome.NoUsableRootColumn>();
    }
}
