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
/// Test fixture asserting the pass silently skips a PrimaryAssociation when the resource is
/// present but its root table is missing the expected literal key column. Synthetic test
/// fixtures (e.g. <c>small/referential-identity</c>) reuse PA names without carrying the
/// post-key-unification PA columns; throwing here would break those fixtures, and the
/// authoritative golden manifests are the safety net for real schema drift.
/// </summary>
[TestFixture]
public class Given_PrimaryAssociation_Missing_Required_Column
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
    public void It_should_index_the_resolved_root_FK_column()
    {
        var index = _authIndexes.Single();
        index.KeyColumns.Select(c => c.Value).Should().Equal("EducationOrganization_DocumentId");
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
/// Test fixture asserting array-nested securable element paths are silently skipped without
/// raising an exception.
/// </summary>
[TestFixture]
public class Given_Securable_Element_With_Array_Nested_Path
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = AuthorizationIndexTestRunner.Build(ctx =>
            ctx.ConcreteResourcesInNameOrder.Add(
                AuthIndexFixtureResources.BuildResourceWithArrayNestedSecurable()
            )
        );
    }

    [Test]
    public void It_should_not_emit_any_authorization_indexes()
    {
        _result.IndexesInCreateOrder.Where(i => i.Kind == DbIndexKind.Authorization).Should().BeEmpty();
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
    public static DerivedRelationalModelSet Build(Action<RelationalModelSetBuilderContext> setup)
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new SetupFixturePass(setup),
            new DeriveAuthorizationIndexInventoryPass(),
        ]);
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
        var rootTable = new DbTableName(_edfiSchema, resourceName);

        var columns = new[]
        {
            BuildScalarColumn(new DbColumnName("DocumentId")),
            BuildScalarColumn(fkColumn),
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
                    fkColumn
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

    public static ConcreteResourceModel BuildResourceWithArrayNestedSecurable()
    {
        var columns = new[] { BuildScalarColumn(new DbColumnName("DocumentId")) };

        return BuildResource(
            "ArrayNestedCarrier",
            columns,
            [],
            new ResourceSecurableElements(
                EducationOrganization: [new EdOrgSecurableElement("$.foo[*].bar", "Bar")],
                Namespace: ["$.collection[*].namespace"],
                Student: [],
                Contact: [],
                Staff: []
            )
        );
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
