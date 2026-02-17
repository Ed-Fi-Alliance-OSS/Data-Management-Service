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
/// Test fixture for root unique constraint derivation.
/// </summary>
[TestFixture]
public class Given_Root_Unique_Constraint_Derivation
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _rootTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should create root unique using reference document ids.
    /// </summary>
    [Test]
    public void It_should_create_root_unique_using_reference_document_ids()
    {
        var uniqueConstraint = _rootTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "Student_DocumentId");
        uniqueConstraint.Name.Should().Be("UX_Enrollment_NK");
    }
}

/// <summary>
/// Test fixture for descriptor unique constraint derivation.
/// </summary>
[TestFixture]
public class Given_Descriptor_Unique_Constraint_Derivation
{
    private DbTableModel _descriptorTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildDescriptorProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var descriptorModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "GradeLevelDescriptor"
            )
            .RelationalModel;

        _descriptorTable = descriptorModel.Root;
    }

    /// <summary>
    /// It should add uri and discriminator columns.
    /// </summary>
    [Test]
    public void It_should_add_uri_and_discriminator_columns()
    {
        var uriColumn = _descriptorTable.Columns.Single(column => column.ColumnName.Value == "Uri");
        var discriminatorColumn = _descriptorTable.Columns.Single(column =>
            column.ColumnName.Value == "Discriminator"
        );

        uriColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 306));
        uriColumn.IsNullable.Should().BeFalse();
        uriColumn.SourceJsonPath.Should().BeNull();

        discriminatorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 128));
        discriminatorColumn.IsNullable.Should().BeFalse();
        discriminatorColumn.SourceJsonPath.Should().BeNull();
    }

    /// <summary>
    /// It should define unique on uri and discriminator.
    /// </summary>
    [Test]
    public void It_should_define_unique_on_uri_and_discriminator()
    {
        var uniqueConstraint = _descriptorTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint.Columns.Select(column => column.Value).Should().Equal("Uri", "Discriminator");
        uniqueConstraint.Name.Should().Be("UX_Descriptor_NK");
    }
}

/// <summary>
/// Test fixture for unmappable identity paths.
/// </summary>
[TestFixture]
public class Given_Unmappable_Identity_Paths
{
    /// <summary>
    /// It should fail fast when identity path cannot be mapped.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_identity_path_cannot_be_mapped()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action.Should().Throw<InvalidOperationException>().WithMessage("*addresses[*].streetNumberName*");
    }
}

/// <summary>
/// Test fixture for incomplete reference identity mapping.
/// </summary>
[TestFixture]
public class Given_Incomplete_Reference_Identity_Mapping
{
    /// <summary>
    /// It should fail fast when reference mapping is missing target identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_reference_mapping_is_missing_target_identity_paths()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintMissingIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Reference mapping 'School'*Ed-Fi:Enrollment*'$.educationOrganizationId'*Ed-Fi:School*"
            );
    }
}

/// <summary>
/// Test fixture for incomplete abstract reference identity mapping.
/// </summary>
[TestFixture]
public class Given_Incomplete_Abstract_Reference_Identity_Mapping
{
    /// <summary>
    /// It should fail fast when abstract reference mapping is missing identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_abstract_reference_mapping_is_missing_identity_paths()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceMissingIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Reference mapping 'EducationOrganization'*Ed-Fi:Enrollment*'$.organizationCode'*Ed-Fi:EducationOrganization*"
            );
    }
}

/// <summary>
/// Test fixture for duplicate source json path columns.
/// </summary>
[TestFixture]
public class Given_Duplicate_SourceJsonPath_Columns
{
    private IReadOnlyDictionary<string, DbColumnName> _lookup = default!;
    private string _sourcePath = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var tableName = new DbTableName(new DbSchemaName("edfi"), "Student");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var sourcePath = JsonPathExpressionCompiler.Compile("$.studentUniqueId");
        _sourcePath = sourcePath.Canonical;
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var columns = new[]
        {
            new DbColumnModel(
                new DbColumnName("StudentUniqueId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, 32),
                false,
                sourcePath,
                null
            ),
            new DbColumnModel(
                new DbColumnName("StudentUniqueId2"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, 32),
                false,
                sourcePath,
                null
            ),
        };
        var table = new DbTableModel(
            tableName,
            jsonScope,
            new TableKey($"PK_{tableName.Name}", Array.Empty<DbKeyColumn>()),
            columns,
            Array.Empty<TableConstraint>()
        );

        _lookup = ConstraintDerivationHelpers.BuildColumnNameLookupBySourceJsonPath(table, resource);
    }

    /// <summary>
    /// It should select the lexicographically first column name.
    /// </summary>
    [Test]
    public void It_should_select_the_lexicographically_first_column_name()
    {
        _lookup[_sourcePath].Value.Should().Be("StudentUniqueId");
    }
}

/// <summary>
/// Test fixture for duplicate source json path columns with mixed kinds.
/// </summary>
[TestFixture]
public class Given_Duplicate_SourceJsonPath_Columns_With_Mixed_Kinds
{
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var tableName = new DbTableName(new DbSchemaName("edfi"), "Student");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var sourcePath = JsonPathExpressionCompiler.Compile("$.studentUniqueId");
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var columns = new[]
        {
            new DbColumnModel(
                new DbColumnName("StudentUniqueId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, 32),
                false,
                sourcePath,
                null
            ),
            new DbColumnModel(
                new DbColumnName("Student_DocumentId"),
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                false,
                sourcePath,
                resource
            ),
        };
        var table = new DbTableModel(
            tableName,
            jsonScope,
            new TableKey($"PK_{tableName.Name}", Array.Empty<DbKeyColumn>()),
            columns,
            Array.Empty<TableConstraint>()
        );

        _action = () => ConstraintDerivationHelpers.BuildColumnNameLookupBySourceJsonPath(table, resource);
    }

    /// <summary>
    /// It should throw with diagnostic details.
    /// </summary>
    [Test]
    public void It_should_throw_with_diagnostic_details()
    {
        _action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*edfi.Student*Ed-Fi:Student*$.studentUniqueId*StudentUniqueId*Student_DocumentId*");
    }
}

/// <summary>
/// Test fixture for duplicate reference path bindings.
/// </summary>
[TestFixture]
public class Given_Duplicate_Reference_Path_Bindings
{
    private DbTableModel _enrollmentTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildDuplicateReferencePathProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should include each identity column when reference path is shared.
    /// </summary>
    [Test]
    public void It_should_include_each_identity_column_when_reference_path_is_shared()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId");
    }
}

/// <summary>
/// Test fixture for reference constraint derivation.
/// </summary>
[TestFixture]
public class Given_Reference_Constraint_Derivation
{
    private DbTableModel _enrollmentTable = default!;
    private DbTableModel _schoolTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        var schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
        _schoolTable = schoolModel.Root;
    }

    /// <summary>
    /// It should order reference fk columns by target identity order.
    /// </summary>
    [Test]
    public void It_should_order_reference_fk_columns_by_target_identity_order()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId");

        schoolFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }

    /// <summary>
    /// It should apply allow identity updates gating.
    /// </summary>
    [Test]
    public void It_should_apply_allow_identity_updates_gating()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");
        var studentFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "Student_DocumentId");

        schoolFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        studentFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    /// <summary>
    /// It should add target side unique for reference fks.
    /// </summary>
    [Test]
    public void It_should_add_target_side_unique_for_reference_fks()
    {
        var uniqueConstraint = _schoolTable
            .Constraints.OfType<TableConstraint.Unique>()
            .Single(constraint => constraint.Name == "UX_School_RefKey");

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }
}

/// <summary>
/// Test fixture for reference constraint derivation with key unification.
/// </summary>
[TestFixture]
public class Given_Reference_Constraint_Derivation_With_Key_Unification
{
    private DbTableModel _enrollmentTable = default!;
    private DbTableModel _schoolTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;
        var schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
        _schoolTable = schoolModel.Root;
    }

    /// <summary>
    /// It should emit composite reference FK columns using canonical storage columns only.
    /// </summary>
    [Test]
    public void It_should_emit_composite_reference_fk_columns_using_canonical_storage_columns_only()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        var localCanonicalColumn = ResolveCanonicalColumn(_enrollmentTable, "School_EducationOrganizationId");
        ResolveCanonicalColumn(_enrollmentTable, "School_SchoolId").Should().Be(localCanonicalColumn);

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", localCanonicalColumn.Value);

        var targetCanonicalColumn = ResolveCanonicalColumn(_schoolTable, "EducationOrganizationId");
        ResolveCanonicalColumn(_schoolTable, "SchoolId").Should().Be(targetCanonicalColumn);

        schoolFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", targetCanonicalColumn.Value);

        foreach (var columnName in schoolFk.Columns.Skip(1))
        {
            _enrollmentTable
                .Columns.Single(column => column.ColumnName.Equals(columnName))
                .Storage.Should()
                .BeOfType<ColumnStorage.Stored>();
        }

        foreach (var columnName in schoolFk.TargetColumns.Skip(1))
        {
            _schoolTable
                .Columns.Single(column => column.ColumnName.Equals(columnName))
                .Storage.Should()
                .BeOfType<ColumnStorage.Stored>();
        }
    }

    /// <summary>
    /// It should preserve all-or-none constraints on per-site binding columns.
    /// </summary>
    [Test]
    public void It_should_preserve_all_or_none_constraints_on_per_site_binding_columns()
    {
        var schoolAllOrNone = _enrollmentTable
            .Constraints.OfType<TableConstraint.AllOrNoneNullability>()
            .Single(constraint => constraint.FkColumn.Value == "School_DocumentId");

        schoolAllOrNone
            .DependentColumns.Select(column => column.Value)
            .Should()
            .Equal("School_EducationOrganizationId", "School_SchoolId");
    }

    /// <summary>
    /// It should emit target reference-key uniqueness over canonical storage columns.
    /// </summary>
    [Test]
    public void It_should_emit_target_reference_key_uniqueness_over_canonical_storage_columns()
    {
        var targetCanonicalColumn = ResolveCanonicalColumn(_schoolTable, "EducationOrganizationId");
        ResolveCanonicalColumn(_schoolTable, "SchoolId").Should().Be(targetCanonicalColumn);

        var uniqueConstraint = _schoolTable
            .Constraints.OfType<TableConstraint.Unique>()
            .Single(constraint => constraint.Name == "UX_School_RefKey");

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", targetCanonicalColumn.Value);
    }

    /// <summary>
    /// Resolves canonical storage column for a unified alias.
    /// </summary>
    private static DbColumnName ResolveCanonicalColumn(DbTableModel table, string aliasColumnName)
    {
        var alias = table.Columns.Single(column => column.ColumnName.Value == aliasColumnName);
        return alias.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject.CanonicalColumn;
    }
}

/// <summary>
/// Test fixture for foreign-key storage invariant validation.
/// </summary>
[TestFixture]
public class Given_Foreign_Key_Storage_Invariant_Validation
{
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new InjectTargetForeignKeyAliasPass(),
            new ValidateForeignKeyStorageInvariantPass(),
        ]);

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when target foreign-key columns include unified aliases.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_target_foreign_key_columns_include_unified_aliases()
    {
        var exception = _action.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("FK_Enrollment_School_Ref");
        exception.Message.Should().Contain("edfi.Enrollment");
        exception.Message.Should().Contain("edfi.School");
        exception.Message.Should().Contain("invalid target column(s)");
        exception.Message.Should().Contain("EducationOrganizationId");
    }

    /// <summary>
    /// Test pass that rewrites one target FK endpoint column to a unified-alias column.
    /// </summary>
    private sealed class InjectTargetForeignKeyAliasPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var resource = new QualifiedResourceName("Ed-Fi", "Enrollment");
            var resourceIndex = context.ConcreteResourcesInNameOrder.FindIndex(model =>
                model.ResourceKey.Resource == resource
            );

            if (resourceIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{resource.ProjectName}:{resource.ResourceName}' was not found."
                );
            }

            var concrete = context.ConcreteResourcesInNameOrder[resourceIndex];
            var root = concrete.RelationalModel.Root;
            var constraints = root.Constraints.ToArray();
            var fkIndex = Array.FindIndex(
                constraints,
                constraint =>
                    constraint is TableConstraint.ForeignKey foreignKey
                    && string.Equals(foreignKey.TargetTable.Name, "School", StringComparison.Ordinal)
                    && foreignKey.Columns.Count > 0
                    && string.Equals(
                        foreignKey.Columns[0].Value,
                        "School_DocumentId",
                        StringComparison.Ordinal
                    )
            );

            if (fkIndex < 0)
            {
                throw new InvalidOperationException(
                    $"School reference foreign key was not found on table '{root.Table}'."
                );
            }

            var schoolForeignKey = (TableConstraint.ForeignKey)constraints[fkIndex];

            if (schoolForeignKey.TargetColumns.Count < 2)
            {
                throw new InvalidOperationException(
                    $"Foreign key '{schoolForeignKey.Name}' does not contain identity target columns."
                );
            }

            var targetColumns = schoolForeignKey.TargetColumns.ToArray();
            targetColumns[1] = new DbColumnName("EducationOrganizationId");

            constraints[fkIndex] = schoolForeignKey with { TargetColumns = targetColumns };

            var updatedRoot = root with { Constraints = constraints };
            var updatedTables = concrete.RelationalModel.TablesInDependencyOrder.Select(table =>
                table.Table == root.Table ? updatedRoot : table
            );
            var updatedModel = concrete.RelationalModel with
            {
                Root = updatedRoot,
                TablesInDependencyOrder = updatedTables.ToArray(),
            };

            context.ConcreteResourcesInNameOrder[resourceIndex] = concrete with
            {
                RelationalModel = updatedModel,
            };
        }
    }
}

/// <summary>
/// Test fixture for local foreign-key alias invariant validation.
/// </summary>
[TestFixture]
public class Given_Foreign_Key_Storage_Invariant_Validation_With_Local_Alias
{
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithIdentityUnification();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new InjectLocalForeignKeyAliasPass(),
            new ValidateForeignKeyStorageInvariantPass(),
        ]);

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when local foreign-key columns include unified aliases.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_local_foreign_key_columns_include_unified_aliases()
    {
        var exception = _action.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("FK_Enrollment_School_Ref");
        exception.Message.Should().Contain("edfi.Enrollment");
        exception.Message.Should().Contain("edfi.School");
        exception.Message.Should().Contain("invalid local column(s)");
        exception.Message.Should().Contain("School_EducationOrganizationId");
    }

    /// <summary>
    /// Test pass that rewrites one local FK endpoint column to a unified-alias column.
    /// </summary>
    private sealed class InjectLocalForeignKeyAliasPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var resource = new QualifiedResourceName("Ed-Fi", "Enrollment");
            var resourceIndex = context.ConcreteResourcesInNameOrder.FindIndex(model =>
                model.ResourceKey.Resource == resource
            );

            if (resourceIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Concrete resource '{resource.ProjectName}:{resource.ResourceName}' was not found."
                );
            }

            var concrete = context.ConcreteResourcesInNameOrder[resourceIndex];
            var root = concrete.RelationalModel.Root;
            var constraints = root.Constraints.ToArray();
            var fkIndex = Array.FindIndex(
                constraints,
                constraint =>
                    constraint is TableConstraint.ForeignKey foreignKey
                    && string.Equals(foreignKey.TargetTable.Name, "School", StringComparison.Ordinal)
                    && foreignKey.Columns.Count > 0
                    && string.Equals(
                        foreignKey.Columns[0].Value,
                        "School_DocumentId",
                        StringComparison.Ordinal
                    )
            );

            if (fkIndex < 0)
            {
                throw new InvalidOperationException(
                    $"School reference foreign key was not found on table '{root.Table}'."
                );
            }

            var schoolForeignKey = (TableConstraint.ForeignKey)constraints[fkIndex];

            if (schoolForeignKey.Columns.Count < 2)
            {
                throw new InvalidOperationException(
                    $"Foreign key '{schoolForeignKey.Name}' does not contain identity local columns."
                );
            }

            var localColumns = schoolForeignKey.Columns.ToArray();
            localColumns[1] = new DbColumnName("School_EducationOrganizationId");

            constraints[fkIndex] = schoolForeignKey with { Columns = localColumns };

            var updatedRoot = root with { Constraints = constraints };
            var updatedTables = concrete.RelationalModel.TablesInDependencyOrder.Select(table =>
                table.Table == root.Table ? updatedRoot : table
            );
            var updatedModel = concrete.RelationalModel with
            {
                Root = updatedRoot,
                TablesInDependencyOrder = updatedTables.ToArray(),
            };

            context.ConcreteResourcesInNameOrder[resourceIndex] = concrete with
            {
                RelationalModel = updatedModel,
            };
        }
    }
}

/// <summary>
/// Test fixture for shuffled reference identity bindings.
/// </summary>
[TestFixture]
public class Given_Shuffled_Reference_Identity_Bindings
{
    private DbTableModel _enrollmentTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new ShuffleReferenceIdentityBindingsRelationalModelSetPass(
                    new QualifiedResourceName("Ed-Fi", "Enrollment"),
                    "$.schoolReference"
                ),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should resolve reference identity columns regardless of binding order.
    /// </summary>
    [Test]
    public void It_should_resolve_reference_identity_columns_regardless_of_binding_order()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");

        schoolFk
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId");

        schoolFk
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }

    /// <summary>
    /// Test type shuffle reference identity bindings relational model set pass.
    /// </summary>
    private sealed class ShuffleReferenceIdentityBindingsRelationalModelSetPass : IRelationalModelSetPass
    {
        private readonly QualifiedResourceName _resource;
        private readonly string _referenceObjectPath;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ShuffleReferenceIdentityBindingsRelationalModelSetPass(
            QualifiedResourceName resource,
            string referenceObjectPath
        )
        {
            _resource = resource;
            _referenceObjectPath = referenceObjectPath;
        }

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
            {
                var resourceEntry = context.ConcreteResourcesInNameOrder[index];

                if (resourceEntry.ResourceKey.Resource != _resource)
                {
                    continue;
                }

                var bindings = resourceEntry.RelationalModel.DocumentReferenceBindings.ToArray();
                var bindingIndex = Array.FindIndex(
                    bindings,
                    binding =>
                        string.Equals(
                            binding.ReferenceObjectPath.Canonical,
                            _referenceObjectPath,
                            StringComparison.Ordinal
                        )
                );

                if (bindingIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Reference object path '{_referenceObjectPath}' was not found for shuffle."
                    );
                }

                var binding = bindings[bindingIndex];

                if (binding.IdentityBindings.Count < 2)
                {
                    throw new InvalidOperationException(
                        $"Reference object path '{_referenceObjectPath}' does not have multiple identity bindings."
                    );
                }

                bindings[bindingIndex] = binding with
                {
                    IdentityBindings = binding.IdentityBindings.Reverse().ToArray(),
                };

                var updatedModel = resourceEntry.RelationalModel with
                {
                    DocumentReferenceBindings = bindings,
                };

                context.ConcreteResourcesInNameOrder[index] = resourceEntry with
                {
                    RelationalModel = updatedModel,
                };
                return;
            }

            throw new InvalidOperationException(
                $"Resource '{_resource.ProjectName}:{_resource.ResourceName}' was not found for shuffle."
            );
        }
    }
}

/// <summary>
/// Test fixture for target unique mutation from reference.
/// </summary>
[TestFixture]
public class Given_Target_Unique_Mutation_From_Reference
{
    private RelationalResourceModel _schoolModel = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildTargetUniqueMutationProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _schoolModel = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel;
    }

    /// <summary>
    /// It should add target unique constraint even when target has no references.
    /// </summary>
    [Test]
    public void It_should_add_target_unique_constraint_even_when_target_has_no_references()
    {
        _schoolModel.DocumentReferenceBindings.Should().BeEmpty();

        var uniqueConstraint = _schoolModel
            .Root.Constraints.OfType<TableConstraint.Unique>()
            .Single(constraint => constraint.Name == "UX_School_RefKey");

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }
}

/// <summary>
/// Test fixture for multiple references to same target.
/// </summary>
[TestFixture]
public class Given_Multiple_References_To_Same_Target
{
    private DbTableModel _schoolTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildMultipleTargetUniqueMutationProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _schoolTable = result
            .ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource.ResourceName == "School")
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should add the target unique constraint only once.
    /// </summary>
    [Test]
    public void It_should_add_the_target_unique_constraint_only_once()
    {
        var constraints = _schoolTable
            .Constraints.OfType<TableConstraint.Unique>()
            .Where(constraint => constraint.Name == "UX_School_RefKey");

        constraints.Should().ContainSingle();

        constraints
            .Single()
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "SchoolId");
    }
}

/// <summary>
/// Test fixture for abstract reference constraint derivation.
/// </summary>
[TestFixture]
public class Given_Abstract_Reference_Constraint_Derivation
{
    private DbTableModel _enrollmentTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should cascade updates for abstract targets.
    /// </summary>
    [Test]
    public void It_should_cascade_updates_for_abstract_targets()
    {
        var educationOrganizationFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "EducationOrganization_DocumentId");

        educationOrganizationFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        educationOrganizationFk.TargetTable.Name.Should().Be("EducationOrganizationIdentity");
    }
}

/// <summary>
/// Test fixture for reference constraint derivation on MSSQL dialect.
/// </summary>
[TestFixture]
public class Given_Reference_Constraint_Derivation_On_Mssql
{
    private DbTableModel _enrollmentTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should use NoAction for all reference FKs on MSSQL.
    /// </summary>
    [Test]
    public void It_should_use_NoAction_for_all_reference_FKs_on_Mssql()
    {
        var schoolFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "School_DocumentId");
        var studentFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "Student_DocumentId");

        schoolFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        studentFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }
}

/// <summary>
/// Test fixture for abstract reference constraint derivation on MSSQL dialect.
/// </summary>
[TestFixture]
public class Given_Abstract_Reference_Constraint_Derivation_On_Mssql
{
    private DbTableModel _enrollmentTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ReferenceBindingPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _enrollmentTable = enrollmentModel.Root;
    }

    /// <summary>
    /// It should use NoAction for abstract target on MSSQL.
    /// </summary>
    [Test]
    public void It_should_use_NoAction_for_abstract_target_on_Mssql()
    {
        var educationOrganizationFk = _enrollmentTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns[0].Value == "EducationOrganization_DocumentId");

        educationOrganizationFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        educationOrganizationFk.TargetTable.Name.Should().Be("EducationOrganizationIdentity");
    }
}

/// <summary>
/// Test fixture for array uniqueness constraint derivation.
/// </summary>
[TestFixture]
public class Given_Array_Uniqueness_Constraint_Derivation
{
    private DbTableModel _addressTable = default!;
    private DbTableModel _periodTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            )
            .RelationalModel;

        _addressTable = busRouteModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddress"
        );
        _periodTable = busRouteModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriod"
        );
    }

    /// <summary>
    /// It should map reference identity paths to document id.
    /// </summary>
    [Test]
    public void It_should_map_reference_identity_paths_to_document_id()
    {
        var uniqueConstraint = _addressTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "School_DocumentId");
        uniqueConstraint.Columns.Should().NotContain(column => column.Value == "Ordinal");
    }

    /// <summary>
    /// It should include parent key parts for nested arrays.
    /// </summary>
    [Test]
    public void It_should_include_parent_key_parts_for_nested_arrays()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "BeginDate");
        uniqueConstraint.Columns.Should().NotContain(column => column.Value == "Ordinal");
    }
}

/// <summary>
/// Test fixture for nested array uniqueness constraint derivation.
/// </summary>
[TestFixture]
public class Given_Nested_Array_Uniqueness_Constraint_Derivation
{
    private DbTableModel _periodTable = default!;
    private DbTableModel _sessionTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildNestedArrayUniquenessProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            )
            .RelationalModel;

        _periodTable = busRouteModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriod"
        );
        _sessionTable = busRouteModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddressPeriodSession"
        );
    }

    /// <summary>
    /// It should include parent key parts for nested constraints.
    /// </summary>
    [Test]
    public void It_should_include_parent_key_parts_for_nested_constraints()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "BeginDate");
    }

    /// <summary>
    /// It should include parent key parts for deeper nested constraints.
    /// </summary>
    [Test]
    public void It_should_include_parent_key_parts_for_deeper_nested_constraints()
    {
        var uniqueConstraint = _sessionTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "AddressOrdinal", "PeriodOrdinal", "SessionName");
    }
}

/// <summary>
/// Test fixture for unmappable array uniqueness path.
/// </summary>
[TestFixture]
public class Given_Unmappable_Array_Uniqueness_Path
{
    /// <summary>
    /// It should fail fast when path does not map to column.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_path_does_not_map_to_column()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessUnmappableProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action.Should().Throw<InvalidOperationException>().WithMessage("*schoolReference.link*");
    }
}

/// <summary>
/// Test fixture for array uniqueness constraint with multiple candidate tables.
/// </summary>
[TestFixture]
public class Given_Array_Uniqueness_Constraint_With_Multiple_Candidate_Tables
{
    private const string ProjectName = "Ed-Fi";
    private const string ResourceName = "Sample";
    private const string TableNameA = "SampleItems";
    private const string TableNameB = "SampleItemsAlt";
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessMultipleCandidateProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new DuplicateScopeResourceModelPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should include all candidate failures in the exception message.
    /// </summary>
    [Test]
    public void It_should_include_all_candidate_failures_in_the_exception_message()
    {
        var exception = _action.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain(TableNameA);
        exception.Message.Should().Contain(TableNameB);
        exception.Message.Should().Contain("did not map to a column");
    }

    /// <summary>
    /// Test type duplicate scope resource model pass.
    /// </summary>
    private sealed class DuplicateScopeResourceModelPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var resource = new QualifiedResourceName(ProjectName, ResourceName);
            var resourceKey = context.GetResourceKeyEntry(resource);
            var model = CreateResourceModel(resource);

            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, model)
            );
        }

        /// <summary>
        /// Create resource model.
        /// </summary>
        private static RelationalResourceModel CreateResourceModel(QualifiedResourceName resource)
        {
            var schema = new DbSchemaName("edfi");
            var rootScope = JsonPathExpressionCompiler.Compile("$");
            var itemsScope = JsonPathExpressionCompiler.Compile("$.items[*]");

            var rootKey = new TableKey(
                $"PK_{ResourceName}",
                new[]
                {
                    new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
                }
            );
            var rootColumns = new[]
            {
                new DbColumnModel(
                    RelationalNameConventions.DocumentIdColumnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            };
            var rootTable = new DbTableModel(
                new DbTableName(schema, ResourceName),
                rootScope,
                rootKey,
                rootColumns,
                Array.Empty<TableConstraint>()
            );

            var childKey = new TableKey(
                "PK_Items",
                new[]
                {
                    new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ItemOrdinal"), ColumnKind.Ordinal),
                }
            );

            var tableA = BuildItemsTable(
                new DbTableName(schema, TableNameA),
                itemsScope,
                childKey,
                "$.items[*].code",
                "ItemCode"
            );
            var tableB = BuildItemsTable(
                new DbTableName(schema, TableNameB),
                itemsScope,
                childKey,
                "$.items[*].altCode",
                "AltCode"
            );

            return new RelationalResourceModel(
                resource,
                schema,
                ResourceStorageKind.RelationalTables,
                rootTable,
                new[] { rootTable, tableA, tableB },
                Array.Empty<DocumentReferenceBinding>(),
                Array.Empty<DescriptorEdgeSource>()
            );
        }

        /// <summary>
        /// Build items table.
        /// </summary>
        private static DbTableModel BuildItemsTable(
            DbTableName tableName,
            JsonPathExpression scope,
            TableKey key,
            string valuePath,
            string valueColumnName
        )
        {
            var columns = new[]
            {
                new DbColumnModel(
                    RelationalNameConventions.DocumentIdColumnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("ItemOrdinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName(valueColumnName),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 20),
                    IsNullable: true,
                    SourceJsonPath: JsonPathExpressionCompiler.Compile(valuePath),
                    TargetResource: null
                ),
            };

            return new DbTableModel(tableName, scope, key, columns, Array.Empty<TableConstraint>());
        }
    }
}

/// <summary>
/// Test fixture for extension array uniqueness constraint alignment.
/// </summary>
[TestFixture]
public class Given_Extension_Array_Uniqueness_Constraint_Alignment
{
    private DbTableModel _periodTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessCoreProjectSchema();
        var extensionProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var contactModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "Contact"
            )
            .RelationalModel;

        _periodTable = contactModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*].periods[*]"
        );
    }

    /// <summary>
    /// It should align extension scoped constraints to base tables.
    /// </summary>
    [Test]
    public void It_should_align_extension_scoped_constraints_to_base_tables()
    {
        var uniqueConstraint = _periodTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("Contact_DocumentId", "AddressOrdinal", "BeginDate");
    }
}

/// <summary>
/// Test fixture for extension array uniqueness constraint with missing base table.
/// </summary>
[TestFixture]
public class Given_Extension_Array_Uniqueness_Constraint_With_Missing_Base_Table
{
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessCoreProjectSchema();
        var extensionProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildExtensionArrayUniquenessMissingExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new ArrayUniquenessConstraintPass(),
            }
        );

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when extension alignment has no target table.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_extension_alignment_has_no_target_table()
    {
        var exception = _action.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("$._ext.sample.missing[*]");
        exception.Message.Should().Contain("Contact");
    }
}

/// <summary>
/// Test type constraint derivation test schema builder.
/// </summary>
internal static class ConstraintDerivationTestSchemaBuilder
{
    /// <summary>
    /// Build reference constraint project schema.
    /// </summary>
    internal static JsonObject BuildReferenceConstraintProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildReferenceConstraintEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    /// <summary>
    /// Build reference constraint project schema with key unification on school identity columns.
    /// </summary>
    internal static JsonObject BuildReferenceConstraintProjectSchemaWithIdentityUnification()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildReferenceConstraintEnrollmentSchemaWithIdentityUnification(),
                ["schools"] = BuildReferenceConstraintSchoolSchemaWithIdentityUnification(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    /// <summary>
    /// Build reference constraint project schema with both root and child references to the same target.
    /// </summary>
    internal static JsonObject BuildReferenceConstraintProjectSchemaWithChildReference()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildReferenceConstraintEnrollmentSchema(),
                ["busRoutes"] = BuildBusRouteArrayUniquenessSchema(new JsonArray()),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    /// <summary>
    /// Build target unique mutation project schema.
    /// </summary>
    internal static JsonObject BuildTargetUniqueMutationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["assignments"] = BuildSingleSchoolReferenceSchema("Assignment"),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build multiple target unique mutation project schema.
    /// </summary>
    internal static JsonObject BuildMultipleTargetUniqueMutationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["assignments"] = BuildSingleSchoolReferenceSchema("Assignment"),
                ["transfers"] = BuildSingleSchoolReferenceSchema("Transfer"),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build duplicate reference path project schema.
    /// </summary>
    internal static JsonObject BuildDuplicateReferencePathProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildDuplicateReferencePathEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build reference constraint missing identity project schema.
    /// </summary>
    internal static JsonObject BuildReferenceConstraintMissingIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildIncompleteReferenceConstraintEnrollmentSchema(),
                ["schools"] = BuildReferenceConstraintSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build abstract reference project schema.
    /// </summary>
    internal static JsonObject BuildAbstractReferenceProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildAbstractReferenceEnrollmentSchema(),
                ["schools"] = BuildAbstractReferenceSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build abstract reference missing identity project schema.
    /// </summary>
    internal static JsonObject BuildAbstractReferenceMissingIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray
                    {
                        "$.educationOrganizationId",
                        "$.organizationCode",
                    },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildAbstractReferenceMissingIdentityEnrollmentSchema(),
                ["schools"] = BuildAbstractReferenceSchoolWithOrganizationCodeSchema(),
            },
        };
    }

    /// <summary>
    /// Build reference identity project schema.
    /// </summary>
    internal static JsonObject BuildReferenceIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    /// <summary>
    /// Build descriptor project schema.
    /// </summary>
    internal static JsonObject BuildDescriptorProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["gradeLevelDescriptors"] = BuildDescriptorSchema() },
        };
    }

    /// <summary>
    /// Build array identity project schema.
    /// </summary>
    internal static JsonObject BuildArrayIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildArrayIdentitySchema() },
        };
    }

    /// <summary>
    /// Build array uniqueness project schema.
    /// </summary>
    internal static JsonObject BuildArrayUniquenessProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteArrayUniquenessSchema(BuildBusRouteArrayUniquenessConstraints()),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build nested array uniqueness project schema.
    /// </summary>
    internal static JsonObject BuildNestedArrayUniquenessProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteNestedArrayUniquenessSchema(
                    BuildBusRouteNestedArrayUniquenessConstraints()
                ),
            },
        };
    }

    /// <summary>
    /// Build array uniqueness unmappable project schema.
    /// </summary>
    internal static JsonObject BuildArrayUniquenessUnmappableProjectSchema()
    {
        JsonArray arrayUniquenessConstraints =
        [
            new JsonObject { ["paths"] = new JsonArray { "$.addresses[*].schoolReference.link" } },
        ];

        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = BuildBusRouteArrayUniquenessSchema(arrayUniquenessConstraints),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build array uniqueness multiple candidate project schema.
    /// </summary>
    internal static JsonObject BuildArrayUniquenessMultipleCandidateProjectSchema()
    {
        JsonArray arrayUniquenessConstraints =
        [
            new JsonObject
            {
                ["basePath"] = "$.items[*]",
                ["paths"] = new JsonArray { "$.missing" },
            },
        ];

        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["samples"] = BuildArrayUniquenessMultipleCandidateSchema(arrayUniquenessConstraints),
            },
        };
    }

    /// <summary>
    /// Build extension array uniqueness core project schema.
    /// </summary>
    internal static JsonObject BuildExtensionArrayUniquenessCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactSchema() },
        };
    }

    /// <summary>
    /// Build extension array uniqueness extension project schema.
    /// </summary>
    internal static JsonObject BuildExtensionArrayUniquenessExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["contacts"] = BuildContactExtensionSchema(
                    BuildContactExtensionAddressesSchema(),
                    BuildContactExtensionArrayUniquenessConstraints()
                ),
            },
        };
    }

    /// <summary>
    /// Build extension array uniqueness missing extension project schema.
    /// </summary>
    internal static JsonObject BuildExtensionArrayUniquenessMissingExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["contacts"] = BuildContactExtensionSchema(
                    BuildContactExtensionMissingSchema(),
                    BuildContactExtensionMissingArrayUniquenessConstraints()
                ),
            },
        };
    }

    /// <summary>
    /// Build incomplete reference constraint enrollment schema.
    /// </summary>
    private static JsonObject BuildIncompleteReferenceConstraintEnrollmentSchema()
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
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
                    ["isRequired"] = true,
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
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build array uniqueness multiple candidate schema.
    /// </summary>
    private static JsonObject BuildArrayUniquenessMultipleCandidateSchema(
        JsonArray arrayUniquenessConstraints
    )
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string" },
                            ["altCode"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Sample",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build duplicate reference path enrollment schema.
    /// </summary>
    private static JsonObject BuildDuplicateReferencePathEnrollmentSchema()
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
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
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build reference constraint enrollment schema.
    /// </summary>
    private static JsonObject BuildReferenceConstraintEnrollmentSchema()
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
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
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build reference constraint enrollment schema with key-unified school identity paths.
    /// </summary>
    private static JsonObject BuildReferenceConstraintEnrollmentSchemaWithIdentityUnification()
    {
        var schema = BuildReferenceConstraintEnrollmentSchema();
        schema["equalityConstraints"] = new JsonArray
        {
            new JsonObject
            {
                ["sourceJsonPath"] = "$.schoolReference.schoolId",
                ["targetJsonPath"] = "$.schoolReference.educationOrganizationId",
            },
        };

        return schema;
    }

    /// <summary>
    /// Build single school reference schema.
    /// </summary>
    private static JsonObject BuildSingleSchoolReferenceSchema(string resourceName)
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = resourceName,
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
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build bus route array uniqueness constraints.
    /// </summary>
    private static JsonArray BuildBusRouteArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray
                {
                    "$.addresses[*].schoolReference.schoolId",
                    "$.addresses[*].schoolReference.educationOrganizationId",
                },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["basePath"] = "$.addresses[*]",
                        ["paths"] = new JsonArray { "$.periods[*].beginDate" },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Build bus route nested array uniqueness constraints.
    /// </summary>
    private static JsonArray BuildBusRouteNestedArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray { "$.addresses[*].addressType" },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["basePath"] = "$.addresses[*]",
                        ["paths"] = new JsonArray { "$.periods[*].beginDate" },
                        ["nestedConstraints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["basePath"] = "$.addresses[*].periods[*]",
                                ["paths"] = new JsonArray { "$.sessions[*].sessionName" },
                            },
                        },
                    },
                },
            },
        };
    }

    /// <summary>
    /// Build bus route array uniqueness schema.
    /// </summary>
    private static JsonObject BuildBusRouteArrayUniquenessSchema(JsonArray arrayUniquenessConstraints)
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            ["schoolReference"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["schoolId"] = new JsonObject { ["type"] = "integer" },
                                    ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                                    ["link"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                                },
                            },
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["beginDate"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["format"] = "date",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "BusRoute",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.addresses[*].schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.addresses[*].schoolReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build bus route nested array uniqueness schema.
    /// </summary>
    private static JsonObject BuildBusRouteNestedArrayUniquenessSchema(JsonArray arrayUniquenessConstraints)
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["addressType"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["beginDate"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["format"] = "date",
                                        },
                                        ["sessions"] = new JsonObject
                                        {
                                            ["type"] = "array",
                                            ["items"] = new JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new JsonObject
                                                {
                                                    ["sessionName"] = new JsonObject
                                                    {
                                                        ["type"] = "string",
                                                        ["maxLength"] = 60,
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "BusRoute",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build contact schema.
    /// </summary>
    private static JsonObject BuildContactSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["beginDate"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["format"] = "date",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build contact extension schema.
    /// </summary>
    private static JsonObject BuildContactExtensionSchema(
        JsonObject extensionProjectSchema,
        JsonArray arrayUniquenessConstraints
    )
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["sample"] = extensionProjectSchema },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints,
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build contact extension addresses schema.
    /// </summary>
    private static JsonObject BuildContactExtensionAddressesSchema()
    {
        var addressItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sponsorCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            },
                        },
                    },
                },
                ["periods"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["beginDate"] = new JsonObject { ["type"] = "string", ["format"] = "date" },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject { ["type"] = "array", ["items"] = addressItems },
            },
        };
    }

    /// <summary>
    /// Build contact extension missing schema.
    /// </summary>
    private static JsonObject BuildContactExtensionMissingSchema()
    {
        var missingItems = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["marker"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                            },
                        },
                    },
                },
                ["foo"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["missing"] = new JsonObject { ["type"] = "array", ["items"] = missingItems },
            },
        };
    }

    /// <summary>
    /// Build contact extension array uniqueness constraints.
    /// </summary>
    private static JsonArray BuildContactExtensionArrayUniquenessConstraints()
    {
        return
        [
            new JsonObject
            {
                ["basePath"] = "$._ext.sample.addresses[*]",
                ["paths"] = new JsonArray { "$.periods[*].beginDate" },
            },
        ];
    }

    /// <summary>
    /// Build contact extension missing array uniqueness constraints.
    /// </summary>
    private static JsonArray BuildContactExtensionMissingArrayUniquenessConstraints()
    {
        return
        [
            new JsonObject
            {
                ["basePath"] = "$._ext.sample.missing[*]",
                ["paths"] = new JsonArray { "$.foo" },
            },
        ];
    }

    /// <summary>
    /// Build reference constraint school schema.
    /// </summary>
    private static JsonObject BuildReferenceConstraintSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray("schoolId", "educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build reference constraint school schema with key-unified identity paths.
    /// </summary>
    private static JsonObject BuildReferenceConstraintSchoolSchemaWithIdentityUnification()
    {
        var schema = BuildReferenceConstraintSchoolSchema();
        schema["equalityConstraints"] = new JsonArray
        {
            new JsonObject
            {
                ["sourceJsonPath"] = "$.schoolId",
                ["targetJsonPath"] = "$.educationOrganizationId",
            },
        };

        return schema;
    }

    /// <summary>
    /// Build abstract reference enrollment schema.
    /// </summary>
    private static JsonObject BuildAbstractReferenceEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
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
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build abstract reference missing identity enrollment schema.
    /// </summary>
    private static JsonObject BuildAbstractReferenceMissingIdentityEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                        ["organizationCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
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
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build abstract reference school schema.
    /// </summary>
    private static JsonObject BuildAbstractReferenceSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray("educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build abstract reference school with organization code schema.
    /// </summary>
    private static JsonObject BuildAbstractReferenceSchoolWithOrganizationCodeSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["organizationCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
            ["required"] = new JsonArray("educationOrganizationId", "organizationCode"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.organizationCode" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["OrganizationCode"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.organizationCode",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build enrollment schema.
    /// </summary>
    private static JsonObject BuildEnrollmentSchema()
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.schoolReference.schoolId",
                "$.schoolReference.educationOrganizationId",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build school schema.
    /// </summary>
    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray("schoolId", "educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build student schema.
    /// </summary>
    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
            ["required"] = new JsonArray("studentUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build descriptor schema.
    /// </summary>
    private static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "GradeLevelDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build array identity schema.
    /// </summary>
    private static JsonObject BuildArrayIdentitySchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                        },
                        ["required"] = new JsonArray("streetNumberName"),
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.addresses[*].streetNumberName" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StreetNumberName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.addresses[*].streetNumberName",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
