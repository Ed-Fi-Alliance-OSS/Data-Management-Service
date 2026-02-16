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
/// Test fixture for reference-site key-unification behavior.
/// </summary>
[TestFixture]
public class Given_Key_Unification_For_Reference_Sites
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildReferenceUnificationProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _rootTable = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should create one canonical stored column per applied reference-site class.
    /// </summary>
    [Test]
    public void It_should_create_one_canonical_stored_column_per_applied_class()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        keyUnificationClass
            .MemberPathColumns.Select(column => column.Value)
            .Should()
            .Equal("School_SchoolId", "SecondarySchool_SchoolId");

        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );
        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should gate unified reference-site aliases by each site's DocumentId presence column.
    /// </summary>
    [Test]
    public void It_should_gate_reference_aliases_by_reference_DocumentId_presence()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Single();

        var schoolIdAlias = _rootTable.Columns.Single(column => column.ColumnName.Value == "School_SchoolId");
        var secondarySchoolIdAlias = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "SecondarySchool_SchoolId"
        );
        var schoolStorage = schoolIdAlias.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject;
        var secondaryStorage = secondarySchoolIdAlias
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;

        schoolStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        schoolStorage.PresenceColumn.Should().Be(new DbColumnName("School_DocumentId"));
        secondaryStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        secondaryStorage.PresenceColumn.Should().Be(new DbColumnName("SecondarySchool_DocumentId"));
        schoolIdAlias.SourceJsonPath!.Value.Canonical.Should().Be("$.schoolReference.schoolId");
        secondarySchoolIdAlias
            .SourceJsonPath!.Value.Canonical.Should()
            .Be("$.secondarySchoolReference.schoolId");
    }

    /// <summary>
    /// It should rewrite foreign keys to stored columns (never unified aliases or synthetic presence columns).
    /// </summary>
    [Test]
    public void It_should_rewrite_foreign_keys_to_stored_columns_only()
    {
        var columnsByName = _rootTable.Columns.ToDictionary(column => column.ColumnName, column => column);

        foreach (var foreignKey in _rootTable.Constraints.OfType<TableConstraint.ForeignKey>())
        {
            foreach (var localColumn in foreignKey.Columns)
            {
                columnsByName.Should().ContainKey(localColumn);
                var column = columnsByName[localColumn];
                column.Storage.Should().BeOfType<ColumnStorage.Stored>();
                localColumn.Value.EndsWith("_Present", StringComparison.Ordinal).Should().BeFalse();
            }
        }
    }
}

/// <summary>
/// Test fixture for optional non-reference scalar key-unification behavior.
/// </summary>
[TestFixture]
public class Given_Key_Unification_For_Optional_NonReference_Scalars
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildOptionalScalarUnificationProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _rootTable = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Example"
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should create synthetic presence flags for optional non-reference unified members.
    /// </summary>
    [Test]
    public void It_should_create_synthetic_presence_flags_for_optional_non_reference_members()
    {
        var fiscalYearPresence = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "FiscalYear_Present"
        );
        var localFiscalYearPresence = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "LocalFiscalYear_Present"
        );

        fiscalYearPresence.Kind.Should().Be(ColumnKind.Scalar);
        fiscalYearPresence.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Boolean));
        fiscalYearPresence.IsNullable.Should().BeTrue();
        fiscalYearPresence.SourceJsonPath.Should().BeNull();
        fiscalYearPresence.Storage.Should().BeOfType<ColumnStorage.Stored>();

        localFiscalYearPresence.Kind.Should().Be(ColumnKind.Scalar);
        localFiscalYearPresence.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Boolean));
        localFiscalYearPresence.IsNullable.Should().BeTrue();
        localFiscalYearPresence.SourceJsonPath.Should().BeNull();
        localFiscalYearPresence.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should convert optional scalar members to presence-gated aliases of one canonical column.
    /// </summary>
    [Test]
    public void It_should_convert_optional_scalar_members_to_presence_gated_aliases()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );
        var fiscalYear = _rootTable.Columns.Single(column => column.ColumnName.Value == "FiscalYear");
        var localFiscalYear = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "LocalFiscalYear"
        );
        var fiscalYearStorage = fiscalYear.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject;
        var localFiscalYearStorage = localFiscalYear
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;

        keyUnificationClass
            .MemberPathColumns.Select(column => column.Value)
            .Should()
            .Equal("FiscalYear", "LocalFiscalYear");
        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
        fiscalYearStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        fiscalYearStorage.PresenceColumn.Should().Be(new DbColumnName("FiscalYear_Present"));
        localFiscalYearStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        localFiscalYearStorage.PresenceColumn.Should().Be(new DbColumnName("LocalFiscalYear_Present"));
        fiscalYear.SourceJsonPath!.Value.Canonical.Should().Be("$.fiscalYear");
        localFiscalYear.SourceJsonPath!.Value.Canonical.Should().Be("$.localFiscalYear");
    }

    /// <summary>
    /// It should append one null-or-true hardening check per synthetic presence column.
    /// </summary>
    [Test]
    public void It_should_add_null_or_true_hardening_for_each_synthetic_presence_column()
    {
        var nullOrTrueConstraints = _rootTable
            .Constraints.OfType<TableConstraint.NullOrTrue>()
            .Select(constraint => constraint.Column.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        nullOrTrueConstraints.Should().Equal("FiscalYear_Present", "LocalFiscalYear_Present");
    }
}

/// <summary>
/// Test fixture for optional non-reference descriptor key-unification behavior.
/// </summary>
[TestFixture]
public class Given_Key_Unification_For_Optional_NonReference_Descriptors
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema =
            KeyUnificationPassTestSchemaBuilder.BuildOptionalDescriptorUnificationProjectSchema();
        var result = KeyUnificationPassTestSchemaBuilder.BuildDerivedSet(projectSchema);
        _rootTable = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "DescriptorExample"
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should convert optional descriptor members to presence-gated unified aliases.
    /// </summary>
    [Test]
    public void It_should_convert_optional_descriptor_members_to_presence_gated_aliases()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );
        var primaryDescriptorColumn = _rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.primarySchoolTypeDescriptor"
        );
        var secondaryDescriptorColumn = _rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.secondarySchoolTypeDescriptor"
        );
        var primaryStorage = primaryDescriptorColumn
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;
        var secondaryStorage = secondaryDescriptorColumn
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;

        canonicalColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn
            .TargetResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
        primaryDescriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        secondaryDescriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        primaryStorage.PresenceColumn.Should().NotBeNull();
        secondaryStorage.PresenceColumn.Should().NotBeNull();
        primaryStorage.CanonicalColumn.Should().Be(canonicalColumn.ColumnName);
        secondaryStorage.CanonicalColumn.Should().Be(canonicalColumn.ColumnName);
    }

    /// <summary>
    /// It should append one null-or-true hardening check per optional descriptor presence flag.
    /// </summary>
    [Test]
    public void It_should_add_null_or_true_hardening_for_optional_descriptor_presence_flags()
    {
        var primaryDescriptorColumn = _rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.primarySchoolTypeDescriptor"
        );
        var secondaryDescriptorColumn = _rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.secondarySchoolTypeDescriptor"
        );
        var primaryPresenceColumn = primaryDescriptorColumn
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject.PresenceColumn;
        var secondaryPresenceColumn = secondaryDescriptorColumn
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject.PresenceColumn;
        var nullOrTrueColumns = _rootTable
            .Constraints.OfType<TableConstraint.NullOrTrue>()
            .Select(constraint => constraint.Column.Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        primaryPresenceColumn.Should().NotBeNull();
        secondaryPresenceColumn.Should().NotBeNull();
        var primaryPresence = primaryPresenceColumn!.Value;
        var secondaryPresence = secondaryPresenceColumn!.Value;

        var expectedPresenceColumns = new[] { primaryPresence.Value, secondaryPresence.Value }
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        expectedPresenceColumns.Should().HaveCount(2);
        nullOrTrueColumns.Should().Equal(expectedPresenceColumns);
    }
}

/// <summary>
/// Test fixture for deterministic equality-constraint classification diagnostics.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Constraint_Classification
{
    private RelationalResourceModel _resourceModel = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildConstraintClassificationProjectSchema();
        var result = KeyUnificationPassTestSchemaBuilder.BuildDerivedSet(projectSchema);
        _resourceModel = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "ConstraintExample"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should classify constraints as applied, redundant, and cross-table ignored.
    /// </summary>
    [Test]
    public void It_should_classify_constraints_deterministically()
    {
        var diagnostics = _resourceModel.KeyUnificationEqualityConstraints;
        var rootTable = _resourceModel.Root;
        var rootFiscalYearColumn = rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.fiscalYear"
        );
        var rootLocalFiscalYearColumn = rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.localFiscalYear"
        );
        var sectionTable = _resourceModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.sections[*]"
        );
        var sectionFiscalYearColumn = sectionTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.sections[*].fiscalYear"
        );
        var keyUnificationClass = rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var applied = diagnostics.Applied.Should().ContainSingle().Subject;
        var redundant = diagnostics.Redundant.Should().ContainSingle().Subject;
        var ignored = diagnostics.Ignored.Should().ContainSingle().Subject;
        var ignoredByReason = diagnostics.IgnoredByReason.Should().ContainSingle().Subject;

        applied.EndpointAPath.Canonical.Should().Be("$.fiscalYear");
        applied.EndpointBPath.Canonical.Should().Be("$.localFiscalYear");
        applied.Table.Should().Be(rootTable.Table);
        applied.EndpointAColumn.Should().Be(rootFiscalYearColumn.ColumnName);
        applied.EndpointBColumn.Should().Be(rootLocalFiscalYearColumn.ColumnName);
        applied.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);

        redundant.EndpointAPath.Canonical.Should().Be("$.fiscalYear");
        redundant.EndpointBPath.Canonical.Should().Be("$.fiscalYear");
        redundant.Binding.Table.Should().Be(rootTable.Table);
        redundant.Binding.Column.Should().Be(rootFiscalYearColumn.ColumnName);

        ignored.EndpointAPath.Canonical.Should().Be("$.fiscalYear");
        ignored.EndpointBPath.Canonical.Should().Be("$.sections[*].fiscalYear");
        ignored.Reason.Should().Be(KeyUnificationIgnoredReason.CrossTable);
        ignored.EndpointABinding.Table.Should().Be(rootTable.Table);
        ignored.EndpointABinding.Column.Should().Be(rootFiscalYearColumn.ColumnName);
        ignored.EndpointBBinding.Table.Should().Be(sectionTable.Table);
        ignored.EndpointBBinding.Column.Should().Be(sectionFiscalYearColumn.ColumnName);

        ignoredByReason.Reason.Should().Be(KeyUnificationIgnoredReason.CrossTable);
        ignoredByReason.Count.Should().Be(1);
    }
}

/// <summary>
/// Test fixture for unresolved equality-constraint endpoint failures.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_An_Unresolved_Endpoint
{
    private Action _act = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildUnresolvedEndpointProjectSchema();
        _act = () => KeyUnificationPassTestSchemaBuilder.BuildDerivedSet(projectSchema);
    }

    /// <summary>
    /// It should fail fast when an endpoint does not bind to any source path.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_unresolved_endpoints()
    {
        _act.Should().Throw<InvalidOperationException>().WithMessage("*was not bound to any column*");
    }
}

/// <summary>
/// Test fixture for ambiguous equality-constraint endpoint failures.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_An_Ambiguous_Endpoint
{
    private Action _act = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildAmbiguousEndpointProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        IRelationalModelSetPass[] passes =
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new DuplicateSourcePathBindingPass(
                resourceName: "AmbiguousExample",
                sourcePath: "$.fiscalYear",
                aliasSuffix: "Alias"
            ),
            new KeyUnificationPass(),
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        _act = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when an endpoint resolves to multiple distinct bindings.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_ambiguous_endpoints()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*resolved to multiple distinct bindings*");
    }
}

/// <summary>
/// Test fixture for unsupported endpoint-kind failures.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_Unsupported_Endpoint_Kinds
{
    private Action _act = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildUnsupportedEndpointKindProjectSchema();
        _act = () => KeyUnificationPassTestSchemaBuilder.BuildDerivedSet(projectSchema);
    }

    /// <summary>
    /// It should fail fast when an endpoint resolves to a non-scalar, non-descriptor kind.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_unsupported_endpoint_kinds()
    {
        _act.Should().Throw<InvalidOperationException>().WithMessage("*unsupported column kind*");
    }
}

/// <summary>
/// Test-only set pass that appends a duplicate source-path binding column to induce endpoint ambiguity.
/// </summary>
file sealed class DuplicateSourcePathBindingPass(string resourceName, string sourcePath, string aliasSuffix)
    : IRelationalModelSetPass
{
    /// <summary>
    /// Execute pass.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[index];

            if (
                !string.Equals(
                    concreteResource.ResourceKey.Resource.ResourceName,
                    resourceName,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            var updatedTables = concreteResource
                .RelationalModel.TablesInDependencyOrder.Select(DuplicateSourcePathColumn)
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.Table.Equals(concreteResource.RelationalModel.Root.Table)
            );
            var updatedModel = concreteResource.RelationalModel with
            {
                Root = updatedRoot,
                TablesInDependencyOrder = updatedTables,
            };

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Duplicate a source-path column for one table when present.
    /// </summary>
    private DbTableModel DuplicateSourcePathColumn(DbTableModel table)
    {
        var sourceColumn = table.Columns.SingleOrDefault(column =>
            column.SourceJsonPath?.Canonical == sourcePath
        );

        if (sourceColumn is null)
        {
            return table;
        }

        var duplicateName = AllocateDuplicateName(table.Columns, sourceColumn.ColumnName, aliasSuffix);
        var duplicateColumn = sourceColumn with { ColumnName = duplicateName };
        var updatedColumns = table.Columns.Concat([duplicateColumn]).ToArray();

        return table with
        {
            Columns = updatedColumns,
        };
    }

    /// <summary>
    /// Allocate deterministic duplicate column name.
    /// </summary>
    private static DbColumnName AllocateDuplicateName(
        IReadOnlyList<DbColumnModel> existingColumns,
        DbColumnName sourceColumnName,
        string aliasSuffix
    )
    {
        var existingNames = existingColumns
            .Select(column => column.ColumnName.Value)
            .ToHashSet(StringComparer.Ordinal);
        var initialName = $"{sourceColumnName.Value}_{aliasSuffix}";

        if (existingNames.Add(initialName))
        {
            return new DbColumnName(initialName);
        }

        var suffix = 2;

        while (true)
        {
            var candidate = $"{sourceColumnName.Value}_{aliasSuffix}_{suffix}";

            if (existingNames.Add(candidate))
            {
                return new DbColumnName(candidate);
            }

            suffix++;
        }
    }
}

/// <summary>
/// Schema builders for key-unification pass tests.
/// </summary>
file static class KeyUnificationPassTestSchemaBuilder
{
    /// <summary>
    /// Build a derived relational model set from one in-memory project schema.
    /// </summary>
    internal static DerivedRelationalModelSet BuildDerivedSet(JsonObject projectSchema)
    {
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// Build project schema for reference-site unification.
    /// </summary>
    internal static JsonObject BuildReferenceUnificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentReferenceUnificationSchema(),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema for optional non-reference scalar unification.
    /// </summary>
    internal static JsonObject BuildOptionalScalarUnificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["examples"] = BuildOptionalScalarResourceSchema() },
        };
    }

    /// <summary>
    /// Build project schema for optional non-reference descriptor unification.
    /// </summary>
    internal static JsonObject BuildOptionalDescriptorUnificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["descriptorExamples"] = BuildOptionalDescriptorResourceSchema(),
                ["schoolTypeDescriptors"] = BuildDescriptorSchema("SchoolTypeDescriptor"),
            },
        };
    }

    /// <summary>
    /// Build project schema for applied/redundant/ignored classification.
    /// </summary>
    internal static JsonObject BuildConstraintClassificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["constraintExamples"] = BuildConstraintClassificationResourceSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema with an unresolved equality endpoint.
    /// </summary>
    internal static JsonObject BuildUnresolvedEndpointProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["unresolvedExamples"] = BuildUnresolvedEndpointResourceSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema with an ambiguous equality endpoint.
    /// </summary>
    internal static JsonObject BuildAmbiguousEndpointProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["ambiguousExamples"] = BuildAmbiguousEndpointResourceSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema with unsupported endpoint kinds.
    /// </summary>
    internal static JsonObject BuildUnsupportedEndpointKindProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentUnsupportedEndpointKindSchema(),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build enrollment schema with two optional references that share one identity value.
    /// </summary>
    private static JsonObject BuildEnrollmentReferenceUnificationSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["secondarySchoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
                ["SecondarySchool"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.secondarySchoolReference.schoolId",
                        },
                    },
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.schoolReference.schoolId",
                    ["targetJsonPath"] = "$.secondarySchoolReference.schoolId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build enrollment schema with equality constraints over reference-object endpoints.
    /// </summary>
    private static JsonObject BuildEnrollmentUnsupportedEndpointKindSchema()
    {
        var schema = BuildEnrollmentReferenceUnificationSchema();
        schema["equalityConstraints"] = new JsonArray
        {
            new JsonObject
            {
                ["sourceJsonPath"] = "$.schoolReference",
                ["targetJsonPath"] = "$.secondarySchoolReference",
            },
        };

        return schema;
    }

    /// <summary>
    /// Build canonical target school schema.
    /// </summary>
    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("schoolId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["equalityConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build optional scalar schema with one equality constraint.
    /// </summary>
    private static JsonObject BuildOptionalScalarResourceSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["fiscalYear"] = new JsonObject { ["type"] = "integer" },
                ["localFiscalYear"] = new JsonObject { ["type"] = "integer" },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Example",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.fiscalYear",
                    ["targetJsonPath"] = "$.localFiscalYear",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build optional descriptor schema with one equality constraint.
    /// </summary>
    private static JsonObject BuildOptionalDescriptorResourceSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "DescriptorExample",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["PrimarySchoolTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["path"] = "$.primarySchoolTypeDescriptor",
                },
                ["SecondarySchoolTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["path"] = "$.secondarySchoolTypeDescriptor",
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.primarySchoolTypeDescriptor",
                    ["targetJsonPath"] = "$.secondarySchoolTypeDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["primarySchoolTypeDescriptor"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 50,
                    },
                    ["secondarySchoolTypeDescriptor"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 50,
                    },
                },
            },
        };
    }

    /// <summary>
    /// Build scalar schema with applied/redundant/cross-table equality constraints.
    /// </summary>
    private static JsonObject BuildConstraintClassificationResourceSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ConstraintExample",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["SectionFiscalYear"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.sections[*].fiscalYear",
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.localFiscalYear",
                    ["targetJsonPath"] = "$.fiscalYear",
                },
                new JsonObject { ["sourceJsonPath"] = "$.fiscalYear", ["targetJsonPath"] = "$.fiscalYear" },
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.sections[*].fiscalYear",
                    ["targetJsonPath"] = "$.fiscalYear",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["fiscalYear"] = new JsonObject { ["type"] = "integer" },
                    ["localFiscalYear"] = new JsonObject { ["type"] = "integer" },
                    ["sections"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["fiscalYear"] = new JsonObject { ["type"] = "integer" },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Build scalar schema with an unresolved equality endpoint.
    /// </summary>
    private static JsonObject BuildUnresolvedEndpointResourceSchema()
    {
        var schema = BuildOptionalScalarResourceSchema();
        schema["resourceName"] = "UnresolvedExample";
        schema["equalityConstraints"] = new JsonArray
        {
            new JsonObject
            {
                ["sourceJsonPath"] = "$.missingFiscalYear",
                ["targetJsonPath"] = "$.fiscalYear",
            },
        };

        return schema;
    }

    /// <summary>
    /// Build scalar schema with an endpoint that resolves to multiple bindings.
    /// </summary>
    private static JsonObject BuildAmbiguousEndpointResourceSchema()
    {
        var schema = BuildOptionalScalarResourceSchema();
        schema["resourceName"] = "AmbiguousExample";
        return schema;
    }

    /// <summary>
    /// Build descriptor schema with canonical descriptor insert shape.
    /// </summary>
    private static JsonObject BuildDescriptorSchema(string resourceName)
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = BuildDescriptorInsertSchema(),
        };
    }

    /// <summary>
    /// Build minimal descriptor insert schema.
    /// </summary>
    private static JsonObject BuildDescriptorInsertSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
            },
            ["required"] = new JsonArray("namespace", "codeValue"),
        };
    }
}
