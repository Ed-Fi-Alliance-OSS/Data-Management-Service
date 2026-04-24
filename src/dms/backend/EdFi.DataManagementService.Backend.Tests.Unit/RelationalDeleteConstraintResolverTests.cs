// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Delete_Constraint_Resolver
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName CalendarResource = new("Ed-Fi", "Calendar");
    private static readonly QualifiedResourceName StudentAssociationResource = new(
        "Ed-Fi",
        "StudentEducationOrganizationAssociation"
    );
    private static readonly QualifiedResourceName EducationOrgCategoryDescriptorResource = new(
        "Ed-Fi",
        "EducationOrganizationCategoryDescriptor"
    );

    private RelationalDeleteConstraintResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new RelationalDeleteConstraintResolver();
    }

    [Test]
    public void It_resolves_a_foreign_key_on_a_root_table_to_the_owning_resource()
    {
        const string constraintName = "FK_Calendar_SchoolRef";
        var modelSet = BuildModelSet(
            BuildResource(SchoolResource, keyId: 1, BuildTable("School", Array.Empty<TableConstraint>())),
            BuildResource(CalendarResource, keyId: 2, BuildTable("Calendar", BuildForeignKey(constraintName)))
        );

        var result = _sut.TryResolveReferencingResource(modelSet, constraintName);

        result.Should().Be(CalendarResource);
    }

    [Test]
    public void It_resolves_a_foreign_key_on_a_child_collection_table_to_the_root_resource()
    {
        // The FK lives on a child/collection table of StudentEducationOrganizationAssociation
        // (e.g. StudentEducationOrganizationAssociationAddress). The resolver must return the
        // ROOT resource name — "StudentEducationOrganizationAssociation" — not the child table
        // name. This matches the ODS/API conflict-message contract exercised in the
        // DeleteReferenceValidation E2E scenarios.
        const string constraintName = "FK_StudentEdOrgAssocAddress_StudentRef";
        var modelSet = BuildModelSet(
            BuildResource(SchoolResource, keyId: 1, BuildTable("School", Array.Empty<TableConstraint>())),
            BuildResource(
                StudentAssociationResource,
                keyId: 2,
                BuildTable("StudentEducationOrganizationAssociation", Array.Empty<TableConstraint>()),
                BuildTable("StudentEducationOrganizationAssociationAddress", BuildForeignKey(constraintName))
            )
        );

        var result = _sut.TryResolveReferencingResource(modelSet, constraintName);

        result.Should().Be(StudentAssociationResource);
    }

    [Test]
    public void It_resolves_a_foreign_key_whose_target_is_the_shared_descriptor_table_to_the_owning_resource()
    {
        // The FK references dms.Descriptor, but the resolver cares only about the table that
        // OWNS the constraint (i.e., the referencing resource). Target-table shape does not
        // change the answer.
        const string constraintName = "FK_School_EdOrgCategoryDescriptor";
        var descriptorFk = new TableConstraint.ForeignKey(
            constraintName,
            [new DbColumnName("EducationOrganizationCategory_DescriptorId")],
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            [new DbColumnName("DocumentId")]
        );
        var modelSet = BuildModelSet(
            BuildResource(
                EducationOrgCategoryDescriptorResource,
                keyId: 1,
                BuildTable("EducationOrganizationCategoryDescriptor", Array.Empty<TableConstraint>())
            ),
            BuildResource(SchoolResource, keyId: 2, BuildTable("School", descriptorFk))
        );

        var result = _sut.TryResolveReferencingResource(modelSet, constraintName);

        result.Should().Be(SchoolResource);
    }

    [Test]
    public void It_returns_null_when_the_constraint_name_is_unknown()
    {
        var modelSet = BuildModelSet(
            BuildResource(
                CalendarResource,
                keyId: 1,
                BuildTable("Calendar", BuildForeignKey("FK_Calendar_SchoolRef"))
            )
        );

        var result = _sut.TryResolveReferencingResource(modelSet, "FK_DoesNotExist");

        result.Should().BeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void It_returns_null_for_null_empty_or_whitespace_constraint_names(string? constraintName)
    {
        var modelSet = BuildModelSet(
            BuildResource(
                CalendarResource,
                keyId: 1,
                BuildTable("Calendar", BuildForeignKey("FK_Calendar_SchoolRef"))
            )
        );

        var result = _sut.TryResolveReferencingResource(modelSet, constraintName!);

        result.Should().BeNull();
    }

    [Test]
    public void It_ignores_unique_and_primary_key_constraints_that_share_a_name_space_with_foreign_keys()
    {
        // A UNIQUE constraint named "UX_Calendar_NK" exists on the same table as a real FK.
        // Querying by the UNIQUE name must return null — the resolver only considers
        // TableConstraint.ForeignKey.
        const string uniqueConstraintName = "UX_Calendar_NK";
        const string foreignKeyConstraintName = "FK_Calendar_SchoolRef";
        var modelSet = BuildModelSet(
            BuildResource(
                CalendarResource,
                keyId: 1,
                BuildTable(
                    "Calendar",
                    new TableConstraint.Unique(uniqueConstraintName, [new DbColumnName("School_DocumentId")]),
                    BuildForeignKey(foreignKeyConstraintName)
                )
            )
        );

        _sut.TryResolveReferencingResource(modelSet, uniqueConstraintName).Should().BeNull();
        _sut.TryResolveReferencingResource(modelSet, foreignKeyConstraintName).Should().Be(CalendarResource);
    }

    [Test]
    public void It_scopes_its_cache_per_model_set_instance()
    {
        // The same constraint name can legitimately map to different resources in different
        // compiled models (e.g. development vs. production schema snapshots). Each
        // DerivedRelationalModelSet must get its own memoized index — proves the
        // ConditionalWeakTable keying works on instance identity.
        const string constraintName = "FK_Shared_Name";
        var calendarOwnedModelSet = BuildModelSet(
            BuildResource(CalendarResource, keyId: 1, BuildTable("Calendar", BuildForeignKey(constraintName)))
        );
        var schoolOwnedModelSet = BuildModelSet(
            BuildResource(SchoolResource, keyId: 1, BuildTable("School", BuildForeignKey(constraintName)))
        );

        _sut.TryResolveReferencingResource(calendarOwnedModelSet, constraintName)
            .Should()
            .Be(CalendarResource);
        _sut.TryResolveReferencingResource(schoolOwnedModelSet, constraintName).Should().Be(SchoolResource);
    }

    [Test]
    public void It_returns_consistent_results_across_repeated_queries_against_the_same_model_set()
    {
        // Smoke test for the memoized index: hitting the same name multiple times yields the
        // same answer, and a different name routed through the already-built index still
        // resolves correctly.
        var modelSet = BuildModelSet(
            BuildResource(
                CalendarResource,
                keyId: 1,
                BuildTable("Calendar", BuildForeignKey("FK_Calendar_SchoolRef"))
            ),
            BuildResource(
                StudentAssociationResource,
                keyId: 2,
                BuildTable(
                    "StudentEducationOrganizationAssociation",
                    BuildForeignKey("FK_StudentEdOrgAssoc_SchoolRef")
                )
            )
        );

        for (var i = 0; i < 3; i++)
        {
            _sut.TryResolveReferencingResource(modelSet, "FK_Calendar_SchoolRef")
                .Should()
                .Be(CalendarResource);
            _sut.TryResolveReferencingResource(modelSet, "FK_StudentEdOrgAssoc_SchoolRef")
                .Should()
                .Be(StudentAssociationResource);
            _sut.TryResolveReferencingResource(modelSet, "FK_Unknown").Should().BeNull();
        }
    }

    [Test]
    public void It_builds_the_per_model_set_index_exactly_once_across_repeated_lookups()
    {
        // The resolver memoizes its index per DerivedRelationalModelSet via
        // ConditionalWeakTable. A simple return-value-consistency check cannot distinguish
        // "memoized" from "rebuilt every call", so this test wraps the resource list in a
        // counter and asserts it is enumerated exactly once across a mix of hit and miss
        // lookups. If a future refactor dropped the cache, this assertion would fail.
        var resources = new[]
        {
            BuildResource(
                CalendarResource,
                keyId: 1,
                BuildTable("Calendar", BuildForeignKey("FK_Calendar_SchoolRef"))
            ),
            BuildResource(
                SchoolResource,
                keyId: 2,
                BuildTable("School", BuildForeignKey("FK_School_LocalEdAgencyRef"))
            ),
        };
        var counting = new EnumerationCountingList<ConcreteResourceModel>(resources);
        var modelSet = BuildModelSet(resources, concreteResourcesOverride: counting);

        _sut.TryResolveReferencingResource(modelSet, "FK_Calendar_SchoolRef").Should().Be(CalendarResource);
        _sut.TryResolveReferencingResource(modelSet, "FK_School_LocalEdAgencyRef")
            .Should()
            .Be(SchoolResource);
        _sut.TryResolveReferencingResource(modelSet, "FK_Unknown").Should().BeNull();
        _sut.TryResolveReferencingResource(modelSet, "FK_Calendar_SchoolRef").Should().Be(CalendarResource);

        counting
            .EnumerationCount.Should()
            .Be(
                1,
                "the resolver must build the index once per model set — repeated lookups hit the ConditionalWeakTable cache"
            );
    }

    [Test]
    public void It_keeps_the_first_owner_when_two_resources_share_the_same_foreign_key_constraint_name()
    {
        // Cross-resource FK-name duplication is legitimate when multiple resources share a
        // superclass table at the DDL layer — for example, every concrete descriptor resource
        // enumerates FK_Descriptor_Document pointing at the shared dms.Descriptor table. Because
        // at most one physical FK corresponds to each emitted name, the driver can only ever
        // surface one owning resource per violation; first-writer-wins therefore preserves correct
        // resolution for uniquely-owned FKs without crashing the whole model index on the shared-
        // superclass case. The iteration order here is ConcreteResourcesInNameOrder, so the
        // resource added first (CalendarResource) owns the entry.
        const string duplicateName = "FK_Duplicated_Name";
        var modelSet = BuildModelSet(
            BuildResource(CalendarResource, keyId: 1, BuildTable("Calendar", BuildForeignKey(duplicateName))),
            BuildResource(SchoolResource, keyId: 2, BuildTable("School", BuildForeignKey(duplicateName)))
        );

        var result = _sut.TryResolveReferencingResource(modelSet, duplicateName);

        result.Should().Be(CalendarResource);
    }

    private static DerivedRelationalModelSet BuildModelSet(params ConcreteResourceModel[] resources) =>
        BuildModelSet(resources, concreteResourcesOverride: null);

    private static DerivedRelationalModelSet BuildModelSet(
        ConcreteResourceModel[] resources,
        IReadOnlyList<ConcreteResourceModel>? concreteResourcesOverride
    )
    {
        var keys = resources.Select(r => r.ResourceKey).ToList();

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "schema-hash",
                ResourceKeyCount: (short)resources.Length,
                ResourceKeySeedHash: [],
                SchemaComponentsInEndpointOrder: [],
                ResourceKeysInIdOrder: keys
            ),
            SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder: [],
            ConcreteResourcesInNameOrder: concreteResourcesOverride ?? resources,
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );
    }

    /// <summary>
    /// Wraps an <see cref="IReadOnlyList{T}"/> so the test can observe how many times the
    /// resolver enumerates <see cref="DerivedRelationalModelSet.ConcreteResourcesInNameOrder"/>.
    /// The resolver's index build does a single <c>foreach</c> over the list; repeated lookups
    /// that go through the memoized index must not touch the enumerator again.
    /// </summary>
    private sealed class EnumerationCountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner = inner;

        public int EnumerationCount { get; private set; }

        public int Count => _inner.Count;

        public T this[int index] => _inner[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return _inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static ConcreteResourceModel BuildResource(
        QualifiedResourceName resource,
        short keyId,
        params DbTableModel[] tables
    )
    {
        var relationalModel = new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tables[0],
            TablesInDependencyOrder: tables,
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ConcreteResourceModel(
            new ResourceKeyEntry(keyId, resource, "1.0.0", false),
            ResourceStorageKind.RelationalTables,
            relationalModel
        );
    }

    private static DbTableModel BuildTable(string tableName, params TableConstraint[] constraints)
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_" + tableName,
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [],
            Constraints: constraints
        );
    }

    private static TableConstraint.ForeignKey BuildForeignKey(string name) =>
        new(
            Name: name,
            Columns: [new DbColumnName("SomeColumn")],
            TargetTable: new DbTableName(new DbSchemaName("edfi"), "SomeTarget"),
            TargetColumns: [new DbColumnName("DocumentId")]
        );
}
