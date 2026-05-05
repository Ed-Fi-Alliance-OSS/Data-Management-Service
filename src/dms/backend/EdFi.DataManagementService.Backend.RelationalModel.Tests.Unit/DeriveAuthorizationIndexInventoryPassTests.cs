// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture asserting all five PrimaryAssociation authorization indexes are emitted
/// when their resources are present in the model set.
/// </summary>
[TestFixture]
public class Given_All_Five_PrimaryAssociations_Are_Present
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
            {
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentContactAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStaffEducationOrganizationAssignmentAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStaffEducationOrganizationEmploymentAssociation()
                );
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentEducationOrganizationResponsibilityAssociation()
                );
            })
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_exactly_five_authorization_indexes()
    {
        _authIndexes.Should().HaveCount(5);
    }

    [Test]
    public void It_should_emit_StudentSchoolAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentSchoolAssociation");
        index.Name.Value.Should().Be("IX_StudentSchoolAssociation_SchoolId_Unified_Auth");
        index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Unified");
        index.IncludeColumns.Should().NotBeNull();
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IsUnique.Should().BeFalse();
    }

    [Test]
    public void It_should_emit_StudentContactAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentContactAssociation");
        index.Name.Value.Should().Be("IX_StudentContactAssociation_Student_DocumentId_Auth");
        index.KeyColumns.Select(c => c.Value).Should().Equal("Student_DocumentId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Contact_DocumentId");
    }

    [Test]
    public void It_should_emit_StaffEducationOrganizationAssignmentAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StaffEducationOrganizationAssignmentAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StaffEducationOrganizationAssignmentAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Staff_DocumentId");
    }

    [Test]
    public void It_should_emit_StaffEducationOrganizationEmploymentAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StaffEducationOrganizationEmploymentAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StaffEducationOrganizationEmploymentAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Staff_DocumentId");
    }

    [Test]
    public void It_should_emit_StudentEducationOrganizationResponsibilityAssociation_index_with_INCLUDE()
    {
        var index = SingleByTable("StudentEducationOrganizationResponsibilityAssociation");
        index
            .Name.Value.Should()
            .Be(
                "IX_StudentEducationOrganizationResponsibilityAssociation_EducationOrganization_EducationOrganizationId_Auth"
            );
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
    }

    [Test]
    public void It_should_classify_all_indexes_as_Authorization()
    {
        _authIndexes.Should().AllSatisfy(i => i.Kind.Should().Be(DbIndexKind.Authorization));
    }

    private DbIndexInfo SingleByTable(string tableName) =>
        _authIndexes.Single(i => i.Table.Name == tableName);
}

/// <summary>
/// Test fixture asserting PrimaryAssociation indexes are silently skipped when the resource
/// is not present in the model set.
/// </summary>
[TestFixture]
public class Given_No_PrimaryAssociation_Resources_Are_Present
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildPlainResource("Course"))
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_any_authorization_indexes()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting PrimaryAssociation key resolves through a UnifiedAlias to the
/// canonical storage column.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_With_UnifiedAlias_Key
{
    private DbIndexInfo _index = default!;

    [SetUp]
    public void Setup()
    {
        var result = AuthorizationIndexTestRunner.Build(ctx =>
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildStudentSchoolAssociationWithAliasedSchoolId(
                    canonicalColumn: new DbColumnName("SchoolId_Canonical")
                )
            )
        );
        _index = result.IndexesInCreateOrder.Single(i => i.Kind == DbIndexKind.Authorization);
    }

    [Test]
    public void It_should_use_the_canonical_column_in_the_index_key()
    {
        _index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Canonical");
    }

    [Test]
    public void It_should_use_the_canonical_column_in_the_index_name()
    {
        _index.Name.Value.Should().Be("IX_StudentSchoolAssociation_SchoolId_Canonical_Auth");
    }
}

/// <summary>
/// Test fixture asserting the pass silently skips a PrimaryAssociation in default mode when
/// the resource is present but its root table is missing the expected literal key column.
/// Synthetic test fixtures (e.g. <c>small/referential-identity</c>) reuse PA names without
/// carrying the post-key-unification PA columns; in default mode the pass tolerates that and
/// relies on the strict-mode pipeline to catch real schema drift.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Missing_Required_Column_In_Default_Mode
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithoutKeyColumn()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_silently_skip_the_PA_emission()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting the pass throws in strict mode when a PrimaryAssociation resource
/// is present but its root table is missing the expected literal key column. Strict mode is
/// the production-runtime configuration; throwing here surfaces real schema drift loudly
/// instead of silently emitting zero authorization indexes.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Missing_Required_Column_In_Strict_Mode
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.BuildStrict(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithoutKeyColumn()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_name_the_resource_and_missing_column()
    {
        _exception!.Message.Should().Contain("StudentSchoolAssociation");
        _exception.Message.Should().Contain("SchoolId_Unified");
    }
}

/// <summary>
/// Test fixture asserting an EducationOrganization securable element on a root reference
/// emits a single-column authorization index.
/// </summary>
[TestFixture]
public class Given_Resource_With_EdOrg_Securable_On_Root_Reference
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index()
    {
        _authIndexes.Should().ContainSingle();
    }

    [Test]
    public void It_should_index_the_resolved_identity_column()
    {
        var index = _authIndexes.Single();
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_EducationOrganizationId");
    }

    [Test]
    public void It_should_have_no_INCLUDE_columns()
    {
        var index = _authIndexes.Single();
        index.IncludeColumns.Should().BeNull();
    }

    [Test]
    public void It_should_use_the_Auth_suffix()
    {
        _authIndexes.Single().Name.Value.Should().EndWith("_Auth");
    }
}

/// <summary>
/// Test fixture asserting a Namespace securable element on a root scalar emits an
/// authorization index on the matching root column.
/// </summary>
[TestFixture]
public class Given_Resource_With_Namespace_Securable_On_Root_Scalar
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_Namespace_column()
    {
        _authIndexes.Should().ContainSingle();
        _authIndexes.Single().KeyColumns.Select(c => c.Value).Should().Equal("Namespace");
    }

    [Test]
    public void It_should_have_no_INCLUDE_columns()
    {
        _authIndexes.Single().IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting an array-nested Namespace securable element resolves to a column on
/// the child collection table (e.g. <c>$.requiredAssessments[*].assessmentReference.namespace</c>
/// on a GraduationPlan-like resource resolves to the child table's namespace identity scalar)
/// and emits an authorization index there.
/// </summary>
[TestFixture]
public class Given_Resource_With_Nested_Namespace_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNestedNamespaceSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_child_collection_table()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("GraduationPlanLikeRequiredAssessment");
        index.KeyColumns.Select(c => c.Value).Should().Equal("RequiredAssessmentAssessment_Namespace");
        index.IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting an array-nested EducationOrganization securable element resolves to
/// a column on the child collection table and emits an authorization index there.
/// </summary>
[TestFixture]
public class Given_Resource_With_Nested_EdOrg_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithNestedEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_emit_a_single_index_on_the_child_collection_table()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.Table.Name.Should().Be("AssessmentAdministrationParticipationLikeAdministrationPoint");
        index
            .KeyColumns.Select(c => c.Value)
            .Should()
            .Equal("AdministeringOrganization_EducationOrganizationId");
        index.IncludeColumns.Should().BeNull();
    }
}

/// <summary>
/// Test fixture asserting Student/Contact/Staff securable elements are ignored by this pass
/// (DMS-1094 scope).
/// </summary>
[TestFixture]
public class Given_Resource_With_Person_Securable_Elements_Only
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithPersonSecurables()
                )
            )
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_any_authorization_indexes()
    {
        _authIndexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting that when a PrimaryAssociation resource also exposes its own EdOrg
/// securable that resolves to the same key column, only the PA index is emitted (the EdOrg
/// emission is suppressed by the PA-coverage dedup).
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Has_Own_EdOrg_Securable
{
    private IReadOnlyList<DbIndexInfo> _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        _authIndexes = AuthorizationIndexTestRunner
            .Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildStudentSchoolAssociationWithSchoolEdOrgSecurable()
                )
            )
            .IndexesInCreateOrder.Where(i =>
                i.Kind == DbIndexKind.Authorization && i.Table.Name == "StudentSchoolAssociation"
            )
            .ToArray();
    }

    [Test]
    public void It_should_emit_only_the_PA_index_on_SchoolId()
    {
        _authIndexes.Should().ContainSingle();
        var index = _authIndexes.Single();
        index.KeyColumns.Select(c => c.Value).Should().Equal("SchoolId_Unified");
        index.IncludeColumns!.Select(c => c.Value).Should().Equal("Student_DocumentId");
    }
}

/// <summary>
/// Test fixture asserting the pass throws on an unresolvable EdOrg securable JSON path.
/// </summary>
[TestFixture]
public class Given_Unresolvable_EdOrg_Securable_Path
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        _exception = TestExceptions.CaptureException(() =>
            AuthorizationIndexTestRunner.Build(ctx =>
                ctx.ConcreteResourcesInNameOrder.Add(
                    AuthIndexFixtureResources.BuildResourceWithUnresolvableEdOrgSecurable()
                )
            )
        );
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_should_name_the_resource_and_offending_path()
    {
        _exception!.Message.Should().Contain("UnresolvableResource");
        _exception.Message.Should().Contain("$.nonexistentReference.id");
    }
}

/// <summary>
/// Test fixture asserting two builds with the same input produce identical authorization
/// index entries (determinism).
/// </summary>
[TestFixture]
public class Given_Two_Builds_With_The_Same_Input
{
    private IReadOnlyList<DbIndexInfo> _firstBuild = default!;
    private IReadOnlyList<DbIndexInfo> _secondBuild = default!;

    [SetUp]
    public void Setup()
    {
        IReadOnlyList<DbIndexInfo> Run() =>
            AuthorizationIndexTestRunner
                .Build(ctx =>
                {
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildStudentSchoolAssociation()
                    );
                    ctx.ConcreteResourcesInNameOrder.Add(
                        AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
                    );
                })
                .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization)
                .ToArray();

        _firstBuild = Run();
        _secondBuild = Run();
    }

    [Test]
    public void It_should_produce_identical_index_entries()
    {
        _secondBuild.Should().BeEquivalentTo(_firstBuild, options => options.WithStrictOrdering());
    }
}

/// <summary>
/// Test fixture asserting that an FK-support index and an Authorization index can target the
/// same root-table column without tripping <c>ValidateIndexNameUniqueness</c> in
/// <see cref="RelationalModelSetBuilderContext.BuildResult"/>. The <c>_Auth</c> suffix on
/// authorization index names is the load-bearing differentiator; this test guards against a
/// future change to <see cref="ConstraintNaming.BuildAuthorizationIndexName"/> dropping or
/// shifting that suffix in a way that would collide with FK-support index names.
/// </summary>
[TestFixture]
public class Given_FkSupport_And_Authorization_Index_On_Same_Column
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = AuthorizationIndexTestRunner.Build(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable());

            // Pre-seed an FK-support index targeting the same root column the auth pass will
            // emit on (the identity scalar). FK-support indexes normally go on FK DocumentId
            // columns; this synthetic seeding deliberately collides on the identity column to
            // exercise the name-uniqueness invariant. If the auth pass dropped its `_Auth`
            // suffix, the names would collide and BuildResult would throw via
            // ValidateIndexNameUniqueness.
            var courseTable = new DbTableName(new DbSchemaName("edfi"), "Course");
            var sharedColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");
            ctx.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName(
                        ConstraintNaming.BuildForeignKeySupportIndexName(courseTable, [sharedColumn])
                    ),
                    courseTable,
                    KeyColumns: [sharedColumn],
                    IsUnique: false,
                    Kind: DbIndexKind.ForeignKeySupport
                )
            );
        });
    }

    [Test]
    public void It_should_emit_both_indexes_with_distinct_names()
    {
        var indexNames = _result
            .IndexesInCreateOrder.Where(i => i.Table.Name == "Course")
            .Select(i => i.Name.Value)
            .ToArray();

        indexNames.Should().HaveCount(2);
        indexNames.Should().Contain("IX_Course_EducationOrganization_EducationOrganizationId");
        indexNames.Should().Contain("IX_Course_EducationOrganization_EducationOrganizationId_Auth");
    }
}

/// <summary>
/// Test fixture asserting a securable-element authorization index is suppressed when an existing
/// PrimaryKey or UniqueConstraint index already leads on the same <c>(table, column)</c>. The
/// PK/UK already supports the same equality lookup, so an extra non-unique covering index would
/// be dead weight on writes and storage.
/// </summary>
[TestFixture]
public class Given_PkUk_Already_Covers_The_Auth_Column
{
    private IReadOnlyList<DbIndexInfo> _authIndexesOnCourse = default!;

    [SetUp]
    public void Setup()
    {
        var courseTable = new DbTableName(new DbSchemaName("edfi"), "Course");
        var authColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");

        var result = AuthorizationIndexTestRunner.Build(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildCourseWithEdOrgSecurable());

            // Pre-seed a unique index whose leading column matches the auth pass's target.
            // The auth pass should detect this and suppress its own emission.
            ctx.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("UX_Course_NK"),
                    courseTable,
                    KeyColumns: [authColumn],
                    IsUnique: true,
                    Kind: DbIndexKind.UniqueConstraint
                )
            );
        });

        _authIndexesOnCourse = result
            .IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization && i.Table == courseTable)
            .ToArray();
    }

    [Test]
    public void It_should_not_emit_a_redundant_authorization_index()
    {
        _authIndexesOnCourse.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture asserting that authorization indexes appear in canonical
/// <c>(schema, table, name)</c> order in <c>IndexesInCreateOrder</c> after the
/// <see cref="CanonicalizeOrderingPass"/> runs. Guards against a future refactor to the new
/// pass that introduces a non-deterministic data structure (e.g. iterating a <c>HashSet</c>):
/// the canonicalize pass is the safety net, this test asserts it.
/// </summary>
[TestFixture]
public class Given_Multiple_Authorization_Indexes_After_Canonicalize
{
    private DbIndexInfo[] _authIndexes = default!;

    [SetUp]
    public void Setup()
    {
        // Add resources in non-canonical order so canonicalization actually has work to do.
        var result = AuthorizationIndexTestRunner.BuildWithCanonicalize(ctx =>
        {
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildStudentSchoolAssociation());
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildResourceWithNamespaceSecurable()
            );
            ctx.ConcreteResourcesInNameOrder.Add(AuthIndexFixtureResources.BuildStudentContactAssociation());
        });

        _authIndexes = result.IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization).ToArray();
    }

    [Test]
    public void It_should_order_authorization_indexes_by_schema_table_then_name()
    {
        var sortKeys = _authIndexes
            .Select(i => $"{i.Table.Schema.Value}|{i.Table.Name}|{i.Name.Value}")
            .ToArray();
        sortKeys.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Test]
    public void It_should_emit_at_least_two_authorization_indexes()
    {
        // Sanity check the test actually exercises ordering across multiple entries.
        _authIndexes.Length.Should().BeGreaterOrEqualTo(2);
    }
}

/// <summary>
/// Test fixture asserting the canonical pass list contains
/// <see cref="DeriveAuthorizationIndexInventoryPass"/> immediately after
/// <see cref="DeriveAuthHierarchyPass"/> and before the dialect-shortening / canonicalize passes.
/// </summary>
[TestFixture]
public class Given_The_Canonical_Pass_List
{
    [Test]
    public void It_should_register_DeriveAuthorizationIndexInventoryPass_after_DeriveAuthHierarchyPass()
    {
        var passNames = RelationalModelSetPasses.CreateDefault().Select(p => p.GetType().Name).ToArray();

        var hierarchyIndex = Array.IndexOf(passNames, nameof(DeriveAuthHierarchyPass));
        var authIndexInventoryIndex = Array.IndexOf(passNames, nameof(DeriveAuthorizationIndexInventoryPass));

        hierarchyIndex.Should().BeGreaterThan(-1);
        authIndexInventoryIndex.Should().Be(hierarchyIndex + 1);
    }

    [Test]
    public void It_should_run_before_the_dialect_shortening_pass()
    {
        var passNames = RelationalModelSetPasses.CreateDefault().Select(p => p.GetType().Name).ToArray();

        var authIndexInventoryIndex = Array.IndexOf(passNames, nameof(DeriveAuthorizationIndexInventoryPass));
        var dialectShorteningIndex = Array.IndexOf(passNames, "ApplyDialectIdentifierShorteningPass");

        authIndexInventoryIndex.Should().BeLessThan(dialectShorteningIndex);
    }

    [Test]
    public void It_should_be_present_in_the_strict_pass_list()
    {
        RelationalModelSetPasses
            .CreateStrict()
            .Select(p => p.GetType().Name)
            .Should()
            .Contain(nameof(DeriveAuthorizationIndexInventoryPass));
    }
}

/// <summary>
/// Builds a derived relational model set by injecting a configurable fixture pass that
/// populates <c>ConcreteResourcesInNameOrder</c>, then runs the pass under test.
/// </summary>
internal static class AuthorizationIndexTestRunner
{
    public static DerivedRelationalModelSet Build(Action<RelationalModelSetBuilderContext> setup) =>
        BuildWith(setup, throwOnMissingPaLiteral: false, includeCanonicalize: false);

    public static DerivedRelationalModelSet BuildStrict(Action<RelationalModelSetBuilderContext> setup) =>
        BuildWith(setup, throwOnMissingPaLiteral: true, includeCanonicalize: false);

    public static DerivedRelationalModelSet BuildWithCanonicalize(
        Action<RelationalModelSetBuilderContext> setup
    ) => BuildWith(setup, throwOnMissingPaLiteral: false, includeCanonicalize: true);

    private static DerivedRelationalModelSet BuildWith(
        Action<RelationalModelSetBuilderContext> setup,
        bool throwOnMissingPaLiteral,
        bool includeCanonicalize
    )
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        IRelationalModelSetPass[] passes = includeCanonicalize
            ?
            [
                new SetupFixturePass(setup),
                new DeriveAuthorizationIndexInventoryPass(throwOnMissingPaLiteral),
                new CanonicalizeOrderingPass(),
            ]
            :
            [
                new SetupFixturePass(setup),
                new DeriveAuthorizationIndexInventoryPass(throwOnMissingPaLiteral),
            ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    private sealed class SetupFixturePass(Action<RelationalModelSetBuilderContext> setup)
        : IRelationalModelSetPass
    {
        public void Execute(RelationalModelSetBuilderContext context) => setup(context);
    }
}

internal static class TestExceptions
{
    public static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

/// <summary>
/// Hand-built <see cref="ConcreteResourceModel"/> fixtures for authorization-index pass tests.
/// Each builder produces a minimal but semantically valid resource with the columns,
/// reference bindings, and securable elements relevant to the scenario.
/// </summary>
internal static class AuthIndexFixtureResources
{
    private const string EdFi = "Ed-Fi";
    private static readonly DbSchemaName _edfiSchema = new("edfi");

    public static ConcreteResourceModel BuildStudentSchoolAssociation() =>
        BuildPaResource(
            "StudentSchoolAssociation",
            new DbColumnName("SchoolId_Unified"),
            new DbColumnName("Student_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentContactAssociation() =>
        BuildPaResource(
            "StudentContactAssociation",
            new DbColumnName("Student_DocumentId"),
            new DbColumnName("Contact_DocumentId")
        );

    public static ConcreteResourceModel BuildStaffEducationOrganizationAssignmentAssociation() =>
        BuildPaResource(
            "StaffEducationOrganizationAssignmentAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        );

    public static ConcreteResourceModel BuildStaffEducationOrganizationEmploymentAssociation() =>
        BuildPaResource(
            "StaffEducationOrganizationEmploymentAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Staff_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentEducationOrganizationResponsibilityAssociation() =>
        BuildPaResource(
            "StudentEducationOrganizationResponsibilityAssociation",
            new DbColumnName("EducationOrganization_EducationOrganizationId"),
            new DbColumnName("Student_DocumentId")
        );

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithAliasedSchoolId(
        DbColumnName canonicalColumn
    )
    {
        var keyLiteral = new DbColumnName("SchoolId_Unified");
        var includeLiteral = new DbColumnName("Student_DocumentId");

        var columns = new[]
        {
            BuildScalarColumn(canonicalColumn),
            BuildAliasColumn(keyLiteral, canonicalColumn),
            BuildScalarColumn(includeLiteral),
        };

        return BuildResourceFromColumns("StudentSchoolAssociation", columns);
    }

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithoutKeyColumn()
    {
        // Root has Student_DocumentId but no SchoolId_Unified column — PA literal lookup must throw.
        var columns = new[] { BuildScalarColumn(new DbColumnName("Student_DocumentId")) };

        return BuildResourceFromColumns("StudentSchoolAssociation", columns);
    }

    public static ConcreteResourceModel BuildStudentSchoolAssociationWithSchoolEdOrgSecurable()
    {
        var resourceName = "StudentSchoolAssociation";
        var keyColumn = new DbColumnName("SchoolId_Unified");
        var includeColumn = new DbColumnName("Student_DocumentId");
        var rootTable = new DbTableName(_edfiSchema, resourceName);

        // The same SchoolId_Unified column resolves both the PA key and the EdOrg securable —
        // the pass must dedup the EdOrg emission against the PA-covered set.
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
            Table: rootTable,
            FkColumn: keyColumn,
            TargetResource: new QualifiedResourceName(EdFi, "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.schoolId"),
                    JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                    keyColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            [BuildScalarColumn(keyColumn), BuildScalarColumn(includeColumn)],
            [binding],
            new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId")],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildPlainResource(string resourceName) =>
        BuildResourceFromColumns(resourceName, [BuildScalarColumn(new DbColumnName("DocumentId"))]);

    public static ConcreteResourceModel BuildCourseWithEdOrgSecurable()
    {
        var resourceName = "Course";
        var fkColumn = new DbColumnName("EducationOrganization_DocumentId");
        var identityColumn = new DbColumnName("EducationOrganization_EducationOrganizationId");
        var rootTable = new DbTableName(_edfiSchema, resourceName);

        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumn(fkColumn),
            BuildScalarColumn(identityColumn),
        };

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.educationOrganizationReference"),
            Table: rootTable,
            FkColumn: fkColumn,
            TargetResource: new QualifiedResourceName(EdFi, "EducationOrganization"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.educationOrganizationId"),
                    JsonPathExpressionCompiler.Compile(
                        "$.educationOrganizationReference.educationOrganizationId"
                    ),
                    identityColumn
                ),
            ]
        );

        return BuildResource(
            resourceName,
            columns,
            [binding],
            new ResourceSecurableElements(
                EducationOrganization:
                [
                    new EdOrgSecurableElement(
                        "$.educationOrganizationReference.educationOrganizationId",
                        "EducationOrganizationId"
                    ),
                ],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildResourceWithNamespaceSecurable()
    {
        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumnWithJsonPath(new DbColumnName("Namespace"), "$.namespace"),
        };

        return BuildResource(
            "NamespaceCarrier",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: ["$.namespace"],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    public static ConcreteResourceModel BuildResourceWithNestedNamespaceSecurable()
    {
        var rootResourceName = "GraduationPlanLike";
        var childTableName = new DbTableName(_edfiSchema, "GraduationPlanLikeRequiredAssessment");
        var nestedNamespacePath = "$.requiredAssessments[*].assessmentReference.namespace";
        var nestedNamespaceColumn = new DbColumnName("RequiredAssessmentAssessment_Namespace");

        var rootTable = new DbTableModel(
            new DbTableName(_edfiSchema, rootResourceName),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{rootResourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            JsonPathExpressionCompiler.Compile("$.requiredAssessments[*]"),
            new TableKey(
                "PK_GraduationPlanLikeRequiredAssessment",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                BuildScalarColumn(new DbColumnName("CollectionItemId")),
                BuildScalarColumn(new DbColumnName("RequiredAssessmentAssessment_DocumentId")),
                BuildScalarColumn(nestedNamespaceColumn),
            ],
            []
        );

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile(
                "$.requiredAssessments[*].assessmentReference"
            ),
            Table: childTableName,
            FkColumn: new DbColumnName("RequiredAssessmentAssessment_DocumentId"),
            TargetResource: new QualifiedResourceName(EdFi, "Assessment"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.assessmentIdentifier"),
                    JsonPathExpressionCompiler.Compile(
                        "$.requiredAssessments[*].assessmentReference.assessmentIdentifier"
                    ),
                    new DbColumnName("RequiredAssessmentAssessment_AssessmentIdentifier")
                ),
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.namespace"),
                    JsonPathExpressionCompiler.Compile(nestedNamespacePath),
                    nestedNamespaceColumn
                ),
            ]
        );

        var qualifiedName = new QualifiedResourceName(EdFi, rootResourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [binding],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [nestedNamespacePath],
                Student: [],
                Contact: [],
                Staff: []
            ),
        };
    }

    public static ConcreteResourceModel BuildResourceWithNestedEdOrgSecurable()
    {
        var rootResourceName = "AssessmentAdministrationParticipationLike";
        var childTableName = new DbTableName(
            _edfiSchema,
            "AssessmentAdministrationParticipationLikeAdministrationPoint"
        );
        var nestedEdOrgPath =
            "$.assessmentAdministrationPoints[*].administeringOrganizationReference.educationOrganizationId";
        var nestedEdOrgColumn = new DbColumnName("AdministeringOrganization_EducationOrganizationId");

        var rootTable = new DbTableModel(
            new DbTableName(_edfiSchema, rootResourceName),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{rootResourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [BuildScalarColumn(new DbColumnName("DocumentId"))],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            JsonPathExpressionCompiler.Compile("$.assessmentAdministrationPoints[*]"),
            new TableKey(
                "PK_AssessmentAdministrationParticipationLikeAdministrationPoint",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                BuildScalarColumn(new DbColumnName("CollectionItemId")),
                BuildScalarColumn(new DbColumnName("AdministeringOrganization_DocumentId")),
                BuildScalarColumn(nestedEdOrgColumn),
            ],
            []
        );

        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile(
                "$.assessmentAdministrationPoints[*].administeringOrganizationReference"
            ),
            Table: childTableName,
            FkColumn: new DbColumnName("AdministeringOrganization_DocumentId"),
            TargetResource: new QualifiedResourceName(EdFi, "EducationOrganization"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    JsonPathExpressionCompiler.Compile("$.educationOrganizationId"),
                    JsonPathExpressionCompiler.Compile(nestedEdOrgPath),
                    nestedEdOrgColumn
                ),
            ]
        );

        var qualifiedName = new QualifiedResourceName(EdFi, rootResourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [binding],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = new ResourceSecurableElements(
                EducationOrganization:
                [
                    new EdOrgSecurableElement(nestedEdOrgPath, "EducationOrganizationId"),
                ],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            ),
        };
    }

    public static ConcreteResourceModel BuildResourceWithPersonSecurables()
    {
        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumn(new DbColumnName("Student_DocumentId")),
            BuildScalarColumn(new DbColumnName("Contact_DocumentId")),
            BuildScalarColumn(new DbColumnName("Staff_DocumentId")),
        };

        return BuildResource(
            "PersonCarrier",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [],
                Namespace: [],
                Student: ["$.studentReference.studentUniqueId"],
                Contact: ["$.contactReference.contactUniqueId"],
                Staff: ["$.staffReference.staffUniqueId"]
            )
        );
    }

    public static ConcreteResourceModel BuildResourceWithUnresolvableEdOrgSecurable()
    {
        var columns = new[] { BuildScalarColumn(new DbColumnName("DocumentId")) };

        return BuildResource(
            "UnresolvableResource",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.nonexistentReference.id", "Id")],
                Namespace: [],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
    }

    private static ConcreteResourceModel BuildPaResource(
        string resourceName,
        DbColumnName keyColumn,
        DbColumnName includeColumn
    )
    {
        var columns = new[] { BuildScalarColumn(keyColumn), BuildScalarColumn(includeColumn) };
        return BuildResourceFromColumns(resourceName, columns);
    }

    private static ConcreteResourceModel BuildResourceFromColumns(
        string resourceName,
        IReadOnlyList<DbColumnModel> columns
    ) => BuildResource(resourceName, columns, [], ResourceSecurableElements.Empty);

    private static ConcreteResourceModel BuildResource(
        string resourceName,
        IReadOnlyList<DbColumnModel> columns,
        IReadOnlyList<DocumentReferenceBinding> bindings,
        ResourceSecurableElements securableElements
    )
    {
        var qualifiedName = new QualifiedResourceName(EdFi, resourceName);
        var resourceKey = new ResourceKeyEntry(1, qualifiedName, "1.0.0", false);
        var rootTableName = new DbTableName(_edfiSchema, resourceName);

        var rootTable = new DbTableModel(
            rootTableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                $"PK_{resourceName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        );

        var relationalModel = new RelationalResourceModel(
            qualifiedName,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            bindings,
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        {
            SecurableElements = securableElements,
        };
    }

    private static DbColumnModel BuildScalarColumn(DbColumnName name) =>
        new(
            name,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );

    private static DbColumnModel BuildScalarColumnWithJsonPath(DbColumnName name, string jsonPath) =>
        new(
            name,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String),
            IsNullable: true,
            SourceJsonPath: JsonPathExpressionCompiler.Compile(jsonPath),
            TargetResource: null
        );

    private static DbColumnModel BuildAliasColumn(DbColumnName aliasName, DbColumnName canonical) =>
        new(
            aliasName,
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(canonical, PresenceColumn: null)
        );
}
