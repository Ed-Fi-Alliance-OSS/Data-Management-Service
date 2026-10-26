// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Pure-function tests for the canonical stored-column resolution shared by the trigger and
/// tracked-change derivation passes via <see cref="IdentityProjectionResolver"/>.
/// </summary>
[TestFixture]
public class Given_Canonical_Stored_Column_Resolution
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "School");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a single-table model with a stored canonical column and a unified-alias column over it.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        var canonical = new DbColumnModel(
            new DbColumnName("SchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var alias = new DbColumnModel(
            new DbColumnName("EducationOrganizationId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId"), PresenceColumn: null)
        );

        return new DbTableModel(
            new DbTableName(_schema, "School"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_School", [new DbKeyColumn(new DbColumnName("Id"), ColumnKind.Scalar)]),
            [canonical, alias],
            []
        );
    }

    /// <summary>
    /// It should return a stored column unchanged.
    /// </summary>
    [Test]
    public void It_should_return_a_stored_column_unchanged()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("SchoolId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should unwrap a unified-alias column to its canonical storage column.
    /// </summary>
    [Test]
    public void It_should_unwrap_a_unified_alias_to_its_canonical_column()
    {
        var resolved = IdentityProjectionResolver.ResolveToStoredColumn(
            new DbColumnName("EducationOrganizationId"),
            BuildTable(),
            _resource
        );

        resolved.Value.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should throw when the requested column is not present on the table.
    /// </summary>
    [Test]
    public void It_should_throw_for_a_missing_column()
    {
        var act = () =>
            IdentityProjectionResolver.ResolveToStoredColumn(
                new DbColumnName("DoesNotExist"),
                BuildTable(),
                _resource
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*DoesNotExist*");
    }
}

/// <summary>
/// Pure-function tests for selecting the single hash-element column out of a same-site logical
/// reference field group, where key unification can fan one identity JSON path out to multiple
/// physical member columns that all share one canonical storage column.
/// </summary>
[TestFixture]
public class Given_Key_Unified_Identity_Element_Column_Selection
{
    private static readonly QualifiedResourceName _resource = new("Ed-Fi", "Registration");
    private static readonly DbSchemaName _schema = new("edfi");

    /// <summary>
    /// Builds a root table with one stored canonical column, two unified-alias members over it, and a
    /// stored column that does not participate in key unification.
    /// </summary>
    private static DbTableModel BuildTable()
    {
        var canonical = new DbColumnModel(
            new DbColumnName("SchoolId_Unified"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var primaryAlias = new DbColumnModel(
            new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Unified"), PresenceColumn: null)
        );

        var secondaryAlias = new DbColumnModel(
            new DbColumnName("Offering_SecondarySchoolReferenceSchoolId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null,
            new ColumnStorage.UnifiedAlias(new DbColumnName("SchoolId_Unified"), PresenceColumn: null)
        );

        var unrelated = new DbColumnModel(
            new DbColumnName("RegistrationId"),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.String, 20),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        return new DbTableModel(
            new DbTableName(_schema, "Registration"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_Registration", [new DbKeyColumn(new DbColumnName("Id"), ColumnKind.Scalar)]),
            [canonical, primaryAlias, secondaryAlias, unrelated],
            []
        );
    }

    /// <summary>
    /// It should throw for an empty member column list, because an identity hash element must be
    /// represented by at least one column.
    /// </summary>
    [Test]
    public void It_should_throw_for_an_empty_member_column_list()
    {
        var act = () =>
            IdentityProjectionResolver.SelectIdentityElementColumn(
                [],
                BuildTable(),
                "$.offeringReference.schoolId",
                _resource
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$.offeringReference.schoolId*")
            .WithMessage("*empty logical reference field group*");
    }

    /// <summary>
    /// It should return a single member column unchanged.
    /// </summary>
    [Test]
    public void It_should_return_a_single_member_column_unchanged()
    {
        var selected = IdentityProjectionResolver.SelectIdentityElementColumn(
            [new DbColumnName("RegistrationId")],
            BuildTable(),
            "$.registrationId",
            _resource
        );

        selected.Value.Should().Be("RegistrationId");
    }

    /// <summary>
    /// It should select the first member column when all members share one canonical storage column.
    /// </summary>
    [Test]
    public void It_should_select_the_first_member_when_all_members_share_canonical_storage()
    {
        var selected = IdentityProjectionResolver.SelectIdentityElementColumn(
            [
                new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
                new DbColumnName("Offering_SecondarySchoolReferenceSchoolId"),
            ],
            BuildTable(),
            "$.offeringReference.schoolId",
            _resource
        );

        selected.Value.Should().Be("Offering_PrimarySchoolReferenceSchoolId");
    }

    /// <summary>
    /// It should throw when members resolve to different canonical storage columns, because multiple
    /// stored values for one identity JSON path cannot be collapsed into a single hash element.
    /// </summary>
    [Test]
    public void It_should_throw_when_members_resolve_to_different_canonical_columns()
    {
        var act = () =>
            IdentityProjectionResolver.SelectIdentityElementColumn(
                [
                    new DbColumnName("Offering_PrimarySchoolReferenceSchoolId"),
                    new DbColumnName("RegistrationId"),
                ],
                BuildTable(),
                "$.offeringReference.schoolId",
                _resource
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$.offeringReference.schoolId*")
            .WithMessage("*SchoolId_Unified*")
            .WithMessage("*RegistrationId*");
    }
}

/// <summary>
/// Test fixture proving reference-bearing identity elements pair each identity path with its locally stored
/// identity-part column (e.g. School_SchoolId), never the FK DocumentId column.
/// </summary>
[TestFixture]
public class Given_Reference_Bearing_Identity_Element_Mappings
{
    private IReadOnlyList<IdentityElementMapping> _enrollment = default!;

    [SetUp]
    public void Setup()
    {
        var mappings = IdentityElementMappingCapturePass.Build(
            ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema(),
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );
        _enrollment = mappings[new QualifiedResourceName("Ed-Fi", "Enrollment")];
    }

    [Test]
    public void It_should_pair_identity_part_columns_with_their_identity_paths()
    {
        _enrollment
            .Select(element => (element.Column.Value, element.IdentityJsonPath))
            .Should()
            .Contain(("School_SchoolId", "$.schoolReference.schoolId"))
            .And.Contain(("School_EducationOrganizationId", "$.schoolReference.educationOrganizationId"))
            .And.Contain(("Student_StudentUniqueId", "$.studentReference.studentUniqueId"));
        _enrollment
            .Select(element => element.Column.Value)
            .Should()
            .NotContain(["School_DocumentId", "Student_DocumentId"]);
    }
}

/// <summary>
/// Test fixture proving identity elements stay one per identity path, in path order, when key unification fans
/// one reference-site logical field out to multiple unified alias columns, and when distinct identity paths share
/// canonical storage.
/// </summary>
[TestFixture]
public class Given_Key_Unified_Reference_Identity_Element_Mappings
{
    private DerivedRelationalModelSet _result = default!;
    private IReadOnlyDictionary<QualifiedResourceName, IReadOnlyList<IdentityElementMapping>> _mappings =
        default!;

    [SetUp]
    public void Setup()
    {
        var passes = RelationalModelSetPasses.CreateDefault();
        _mappings = IdentityElementMappingCapturePass.Build(
            TriggerInventoryTestSchemaBuilder.BuildKeyUnifiedReferenceIdentityProjectSchema(),
            passes,
            result => _result = result
        );
    }

    [Test]
    public void It_should_emit_one_identity_element_per_identity_path_in_order()
    {
        _mappings[new QualifiedResourceName("Ed-Fi", "Registration")]
            .Select(element => element.IdentityJsonPath)
            .Should()
            .Equal("$.offeringReference.offeringName", "$.offeringReference.schoolId", "$.registrationId");
    }

    [Test]
    public void It_should_represent_a_key_unified_logical_field_by_its_first_member_column()
    {
        var offeringBinding = _result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Registration"
            )
            .RelationalModel.DocumentReferenceBindings.Single(binding =>
                binding.ReferenceObjectPath.Canonical == "$.offeringReference"
            );
        var schoolIdGroup = offeringBinding
            .GetLogicalFieldGroups()
            .Single(group => group.ReferenceJsonPath.Canonical == "$.offeringReference.schoolId");
        schoolIdGroup
            .MemberColumns.Should()
            .HaveCountGreaterThan(
                1,
                "the fixture must fan one logical reference field out to multiple columns"
            );

        _mappings[new QualifiedResourceName("Ed-Fi", "Registration")]
            .Single(element => element.IdentityJsonPath == "$.offeringReference.schoolId")
            .Column.Should()
            .Be(schoolIdGroup.MemberColumns[0]);
    }

    [Test]
    public void It_should_keep_distinct_identity_paths_that_share_canonical_storage()
    {
        _mappings[new QualifiedResourceName("Ed-Fi", "Offering")]
            .Select(element => element.IdentityJsonPath)
            .Should()
            .Equal(
                "$.offeringName",
                "$.primarySchoolReference.schoolId",
                "$.secondarySchoolReference.schoolId"
            );
    }
}

/// <summary>
/// Appended after the given passes, captures <see cref="IdentityProjectionResolver.BuildIdentityElementMappings"/>
/// for every relational resource, exactly as the trigger and tracked-change passes call it.
/// </summary>
internal sealed class IdentityElementMappingCapturePass(
    Dictionary<QualifiedResourceName, IReadOnlyList<IdentityElementMapping>> captured
) : IRelationalModelSetPass
{
    public static IReadOnlyDictionary<QualifiedResourceName, IReadOnlyList<IdentityElementMapping>> Build(
        JsonObject projectSchema,
        IReadOnlyList<IRelationalModelSetPass> passes,
        Action<DerivedRelationalModelSet>? onResult = null
    )
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                projectSchema,
                isExtensionProject: false
            ),
        ]);
        Dictionary<QualifiedResourceName, IReadOnlyList<IdentityElementMapping>> captured = [];
        var result = new DerivedRelationalModelSetBuilder([
            .. passes,
            new IdentityElementMappingCapturePass(captured),
        ]).Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        onResult?.Invoke(result);

        return captured;
    }

    public void Execute(RelationalModelSetBuilderContext context)
    {
        var resourcesByKey = context.ConcreteResourcesInNameOrder.ToDictionary(model =>
            model.ResourceKey.Resource
        );

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (
                RelationalModelSetSchemaHelpers.IsResourceExtension(resourceContext)
                || !resourcesByKey.TryGetValue(resource, out var model)
                || model.StorageKind != ResourceStorageKind.RelationalTables
            )
            {
                continue;
            }

            captured[resource] = IdentityProjectionResolver.BuildIdentityElementMappings(
                model.RelationalModel,
                context.GetOrCreateResourceBuilderContext(resourceContext),
                resource
            );
        }
    }
}
