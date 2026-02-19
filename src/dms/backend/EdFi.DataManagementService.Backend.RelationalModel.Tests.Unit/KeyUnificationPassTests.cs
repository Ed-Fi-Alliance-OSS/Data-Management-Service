// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
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
        var builder = new DerivedRelationalModelSetBuilder(
            KeyUnificationPassTestSchemaBuilder.BuildPassesThroughKeyUnification()
        );

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
    /// It should keep foreign keys on storage-safe columns for invariant validation.
    /// </summary>
    [Test]
    public void It_should_keep_foreign_keys_on_storage_safe_columns()
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
        var builder = new DerivedRelationalModelSetBuilder(
            KeyUnificationPassTestSchemaBuilder.BuildPassesThroughKeyUnification()
        );

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
    private RelationalResourceModel _resourceModel = default!;
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
        _resourceModel = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "DescriptorExample"
            )
            .RelationalModel;
        _rootTable = _resourceModel.Root;
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

    /// <summary>
    /// It should emit one descriptor FK constraint per mapped storage column and report de-dup diagnostics.
    /// </summary>
    [Test]
    public void It_should_emit_one_descriptor_fk_per_storage_column_after_unification()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var descriptorForeignKeys = _rootTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Where(constraint =>
                constraint.TargetTable.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"))
            )
            .ToArray();
        var expectedBindingColumns = _rootTable
            .Columns.Where(column =>
                column.Kind == ColumnKind.DescriptorFk && column.SourceJsonPath is not null
            )
            .Select(column => column.ColumnName)
            .OrderBy(column => column.Value, StringComparer.Ordinal)
            .ToArray();
        var dedup = _resourceModel.DescriptorForeignKeyDeduplications.Should().ContainSingle().Subject;

        descriptorForeignKeys.Should().ContainSingle();
        descriptorForeignKeys[0].Columns.Should().Equal(keyUnificationClass.CanonicalColumn);
        descriptorForeignKeys[0].TargetColumns.Should().Equal(RelationalNameConventions.DocumentIdColumnName);
        dedup.Table.Should().Be(_rootTable.Table);
        dedup.StorageColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        dedup.BindingColumns.Should().Equal(expectedBindingColumns);
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
/// Test fixture for de-duplicating identical base+extension equality constraints.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_Duplicate_Base_And_Extension_Constraints
{
    private RelationalResourceModel _resourceModel = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            KeyUnificationPassTestSchemaBuilder.BuildExtensionConstraintDedupCoreProjectSchema();
        var extensionProjectSchema =
            KeyUnificationPassTestSchemaBuilder.BuildExtensionConstraintDedupExtensionProjectSchema();
        var result = KeyUnificationPassTestSchemaBuilder.BuildDerivedSet([
            (coreProjectSchema, IsExtensionProject: false),
            (extensionProjectSchema, IsExtensionProject: true),
        ]);
        _resourceModel = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "ConstraintMergeExample"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should process one undirected equality constraint when duplicated across base/extension.
    /// </summary>
    [Test]
    public void It_should_process_one_undirected_constraint_when_duplicated_across_base_and_extension()
    {
        var diagnostics = _resourceModel.KeyUnificationEqualityConstraints;
        var applied = diagnostics.Applied.Should().ContainSingle().Subject;
        var keyUnificationClass = _resourceModel.Root.KeyUnificationClasses.Should().ContainSingle().Subject;

        applied.EndpointAPath.Canonical.Should().Be("$.fiscalYear");
        applied.EndpointBPath.Canonical.Should().Be("$.localFiscalYear");
        applied.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        diagnostics.Redundant.Should().BeEmpty();
        diagnostics.Ignored.Should().BeEmpty();
        diagnostics.IgnoredByReason.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for root selection when multiple tables share one DbTableName.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_Duplicate_Table_Names_Across_Scopes
{
    private RelationalResourceModel _resourceModel = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildConstraintClassificationProjectSchema();
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
            new DuplicateTableNameAcrossScopesPass(
                resourceName: "ConstraintExample",
                duplicatedScope: "$.sections[*]"
            ),
            new KeyUnificationPass(),
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);

        _resourceModel = builder
            .Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules())
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "ConstraintExample"
            )
            .RelationalModel;
    }

    /// <summary>
    /// It should preserve the root table by JsonScope when table names collide.
    /// </summary>
    [Test]
    public void It_should_preserve_root_by_scope_when_table_names_collide()
    {
        var rootTableNameMatches = _resourceModel
            .TablesInDependencyOrder.Where(table => table.Table.Equals(_resourceModel.Root.Table))
            .ToArray();
        var rootByScope = _resourceModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Equals(_resourceModel.Root.JsonScope)
        );

        rootTableNameMatches.Should().HaveCount(2);
        _resourceModel.Root.JsonScope.Canonical.Should().Be("$");
        rootByScope.Should().BeSameAs(_resourceModel.Root);
        _resourceModel.Root.KeyUnificationClasses.Should().ContainSingle();
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
/// Test fixture for descriptor endpoint target-resource validation failures.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_A_Null_Descriptor_Target_Resource
{
    private Action _act = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema =
            KeyUnificationPassTestSchemaBuilder.BuildOptionalDescriptorUnificationProjectSchema();
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
            new NullDescriptorTargetResourcePass(
                resourceName: "DescriptorExample",
                sourcePath: "$.primarySchoolTypeDescriptor"
            ),
            new KeyUnificationPass(),
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        _act = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when a descriptor unification member has no target resource.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_descriptor_member_target_resource_is_null()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*descriptor target resource is required*resource 'Ed-Fi:DescriptorExample'*table 'edfi.DescriptorExample'*column 'PrimarySchoolTypeDescriptor_DescriptorId'*"
            );
    }
}

/// <summary>
/// Test fixture for key-unification member compatibility validation.
/// </summary>
[TestFixture]
public class Given_Key_Unification_With_Incompatible_Class_Members
{
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "Example");

    /// <summary>
    /// It should fail fast when a class mixes descriptor and scalar members.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_mixed_scalar_and_descriptor_members()
    {
        IReadOnlyList<DbColumnModel> members =
        [
            CreateDescriptorColumn(
                "PrimaryDescriptor_DescriptorId",
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
            ),
            CreateScalarColumn("LocalFiscalYear", ScalarKind.Int32),
        ];

        Action act = () => InvokeValidateUnificationMembers(members);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*resource 'Ed-Fi:Example'*table 'edfi.Example'*cannot mix scalar and descriptor members*'LocalFiscalYear'*'PrimaryDescriptor_DescriptorId'*"
            );
    }

    /// <summary>
    /// It should fail fast when scalar members do not share one scalar type.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_scalar_type_mismatch()
    {
        IReadOnlyList<DbColumnModel> members =
        [
            CreateScalarColumn("FiscalYear", ScalarKind.Int32),
            CreateScalarColumn("LocalFiscalYear", ScalarKind.Int64),
        ];

        Action act = () => InvokeValidateUnificationMembers(members);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*scalar type mismatch*resource 'Ed-Fi:Example'*table 'edfi.Example'*'LocalFiscalYear'*'FiscalYear'*"
            );
    }

    /// <summary>
    /// It should fail fast when descriptor members resolve to different descriptor resources.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_descriptor_target_resource_mismatch()
    {
        IReadOnlyList<DbColumnModel> members =
        [
            CreateDescriptorColumn(
                "PrimaryDescriptor_DescriptorId",
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
            ),
            CreateDescriptorColumn(
                "SecondaryDescriptor_DescriptorId",
                new QualifiedResourceName("Ed-Fi", "ProgramDescriptor")
            ),
        ];

        Action act = () => InvokeValidateUnificationMembers(members);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*descriptor target mismatch*resource 'Ed-Fi:Example'*table 'edfi.Example'*'SecondaryDescriptor_DescriptorId'*'PrimaryDescriptor_DescriptorId'*"
            );
    }

    /// <summary>
    /// Invoke private ValidateUnificationMembers and unwrap invocation exceptions.
    /// </summary>
    private static void InvokeValidateUnificationMembers(IReadOnlyList<DbColumnModel> members)
    {
        var method = typeof(KeyUnificationPass).GetMethod(
            "ValidateUnificationMembers",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method.Should().NotBeNull();

        try
        {
            _ = method!.Invoke(null, [members, Resource, CreateTable()]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Create one scalar member column for validation tests.
    /// </summary>
    private static DbColumnModel CreateScalarColumn(string columnName, ScalarKind scalarKind)
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind),
            IsNullable: true,
            SourceJsonPath: null,
            TargetResource: null
        );
    }

    /// <summary>
    /// Create one descriptor member column for validation tests.
    /// </summary>
    private static DbColumnModel CreateDescriptorColumn(
        string columnName,
        QualifiedResourceName? targetResource
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.DescriptorFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            SourceJsonPath: null,
            targetResource
        );
    }

    /// <summary>
    /// Create a minimal table for validation context.
    /// </summary>
    private static DbTableModel CreateTable()
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Example"),
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                "PK_Example",
                [new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart)]
            ),
            [],
            []
        );
    }
}

/// <summary>
/// Test fixture for canonical base-token derivation.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Canonical_Base_Token_Derivation
{
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "Example");

    /// <summary>
    /// It should derive the base token from property-relative segments for a reference identity binding.
    /// </summary>
    [Test]
    public void It_should_derive_base_token_from_property_relative_segments()
    {
        var sourcePath = JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId");
        var table = CreateTable(JsonPathExpressionCompiler.Compile("$"));
        var memberColumn = CreateScalarColumn("School_SchoolId", sourcePath);
        var referenceBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
            table.Table,
            new DbColumnName("School_DocumentId"),
            new QualifiedResourceName("Ed-Fi", "School"),
            [new ReferenceIdentityBinding(sourcePath, memberColumn.ColumnName)]
        );
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath = new Dictionary<
            string,
            DocumentReferenceBinding
        >(StringComparer.Ordinal)
        {
            [sourcePath.Canonical] = referenceBinding,
        };

        var baseToken = InvokeBuildMemberBaseToken(memberColumn, table, referenceBindingByIdentityPath);

        baseToken.Should().Be("SchoolId");
    }

    /// <summary>
    /// It should derive the base token from the last prefix property when source path equals wildcard scope.
    /// </summary>
    [Test]
    public void It_should_derive_base_token_from_last_prefix_property_when_source_path_equals_scope()
    {
        var sourcePath = JsonPathExpressionCompiler.Compile("$.programDescriptors[*]");
        var table = CreateTable(sourcePath);
        var memberColumn = CreateDescriptorColumn("ProgramDescriptorId", sourcePath);

        var baseToken = InvokeBuildMemberBaseToken(
            memberColumn,
            table,
            new Dictionary<string, DocumentReferenceBinding>(StringComparer.Ordinal)
        );

        baseToken.Should().Be("ProgramDescriptor");
    }

    /// <summary>
    /// It should fail fast when the stripped relative segment list is non-empty but contains no properties.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_relative_segments_are_non_empty_and_non_property()
    {
        var sourcePath = new JsonPathExpression(
            "$.programDescriptors.<unsupported>",
            [new JsonPathSegment.Property("programDescriptors"), new UnsupportedJsonPathSegment()]
        );
        var table = CreateTable(JsonPathExpressionCompiler.Compile("$.programDescriptors"));
        var memberColumn = CreateScalarColumn("ProgramDescriptors_Unsupported", sourcePath);

        Action act = () =>
            _ = InvokeBuildMemberBaseToken(
                memberColumn,
                table,
                new Dictionary<string, DocumentReferenceBinding>(StringComparer.Ordinal)
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*contains unsupported non-property segments after prefix stripping*");
    }

    /// <summary>
    /// Invokes private BuildMemberBaseToken and unwraps invocation exceptions.
    /// </summary>
    private static string InvokeBuildMemberBaseToken(
        DbColumnModel member,
        DbTableModel table,
        IReadOnlyDictionary<string, DocumentReferenceBinding> referenceBindingByIdentityPath
    )
    {
        var method = typeof(KeyUnificationPass).GetMethod(
            "BuildMemberBaseToken",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method.Should().NotBeNull();

        try
        {
            return (string)method!.Invoke(null, [member, table, referenceBindingByIdentityPath, Resource])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Creates a minimal table for member-token derivation tests.
    /// </summary>
    private static DbTableModel CreateTable(JsonPathExpression jsonScope)
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Example"),
            jsonScope,
            new TableKey(
                "PK_Example",
                [new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart)]
            ),
            [],
            []
        );
    }

    /// <summary>
    /// Creates a scalar member column with SourceJsonPath for token derivation tests.
    /// </summary>
    private static DbColumnModel CreateScalarColumn(string columnName, JsonPathExpression sourcePath)
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(ScalarKind.Int32),
            IsNullable: true,
            sourcePath,
            TargetResource: null
        );
    }

    /// <summary>
    /// Creates a descriptor member column with SourceJsonPath for token derivation tests.
    /// </summary>
    private static DbColumnModel CreateDescriptorColumn(string columnName, JsonPathExpression sourcePath)
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.DescriptorFk,
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: true,
            sourcePath,
            new QualifiedResourceName("Ed-Fi", "ProgramDescriptor")
        );
    }

    /// <summary>
    /// Test-only unsupported segment kind to validate fail-fast behavior for malformed relative paths.
    /// </summary>
    private sealed record UnsupportedJsonPathSegment : JsonPathSegment;
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
/// Test-only set pass that clears one descriptor column TargetResource to exercise fail-fast validation.
/// </summary>
file sealed class NullDescriptorTargetResourcePass(string resourceName, string sourcePath)
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
                .RelationalModel.TablesInDependencyOrder.Select(NullDescriptorTarget)
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.JsonScope.Equals(concreteResource.RelationalModel.Root.JsonScope)
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
    /// Rewrite the matched descriptor column to null TargetResource.
    /// </summary>
    private DbTableModel NullDescriptorTarget(DbTableModel table)
    {
        var updatedColumns = table
            .Columns.Select(column =>
                column.Kind == ColumnKind.DescriptorFk
                && string.Equals(column.SourceJsonPath?.Canonical, sourcePath, StringComparison.Ordinal)
                    ? column with
                    {
                        TargetResource = null,
                    }
                    : column
            )
            .ToArray();

        return table with
        {
            Columns = updatedColumns,
        };
    }
}

/// <summary>
/// Test-only set pass that rewrites one table name to collide with the resource root table name.
/// </summary>
file sealed class DuplicateTableNameAcrossScopesPass(string resourceName, string duplicatedScope)
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

            var rootTableName = concreteResource.RelationalModel.Root.Table;
            var updatedTables = concreteResource
                .RelationalModel.TablesInDependencyOrder.Select(table =>
                    string.Equals(table.JsonScope.Canonical, duplicatedScope, StringComparison.Ordinal)
                        ? table with
                        {
                            Table = rootTableName,
                        }
                        : table
                )
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.JsonScope.Equals(concreteResource.RelationalModel.Root.JsonScope)
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
}

/// <summary>
/// Schema builders for key-unification pass tests.
/// </summary>
file static class KeyUnificationPassTestSchemaBuilder
{
    /// <summary>
    /// Build the default pass list through key unification, excluding index and trigger derivation
    /// passes that require non-nullable identity paths (which these test schemas intentionally omit).
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughKeyUnification()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ValidateUnifiedAliasMetadataPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new DescriptorForeignKeyConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new ValidateForeignKeyStorageInvariantPass(),
            new ApplyDialectIdentifierShorteningPass(),
            new CanonicalizeOrderingPass(),
        ];
    }

    /// <summary>
    /// Build a derived relational model set from one in-memory project schema.
    /// </summary>
    internal static DerivedRelationalModelSet BuildDerivedSet(JsonObject projectSchema)
    {
        return BuildDerivedSet([(projectSchema, IsExtensionProject: false)]);
    }

    /// <summary>
    /// Build a derived relational model set from in-memory project schemas.
    /// </summary>
    internal static DerivedRelationalModelSet BuildDerivedSet(
        IReadOnlyList<(JsonObject ProjectSchema, bool IsExtensionProject)> projectSchemas
    )
    {
        ArgumentNullException.ThrowIfNull(projectSchemas);

        if (projectSchemas.Count == 0)
        {
            throw new ArgumentException(
                "At least one project schema must be provided.",
                nameof(projectSchemas)
            );
        }

        var projects = projectSchemas
            .Select(project =>
                EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                    project.ProjectSchema,
                    project.IsExtensionProject
                )
            )
            .ToArray();
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(projects);
        var builder = new DerivedRelationalModelSetBuilder(
            KeyUnificationPassTestSchemaBuilder.BuildPassesThroughKeyUnification()
        );

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
    /// Build core project schema for base+extension constraint de-dup testing.
    /// </summary>
    internal static JsonObject BuildExtensionConstraintDedupCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["constraintMergeExamples"] = BuildExtensionConstraintDedupCoreResourceSchema(),
            },
        };
    }

    /// <summary>
    /// Build extension project schema for base+extension constraint de-dup testing.
    /// </summary>
    internal static JsonObject BuildExtensionConstraintDedupExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["constraintMergeExamples"] = BuildExtensionConstraintDedupExtensionResourceSchema(),
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
    /// Build core resource schema for base+extension constraint de-dup testing.
    /// </summary>
    private static JsonObject BuildExtensionConstraintDedupCoreResourceSchema()
    {
        var schema = BuildOptionalScalarResourceSchema();
        schema["resourceName"] = "ConstraintMergeExample";
        schema["equalityConstraints"] = new JsonArray
        {
            new JsonObject { ["sourceJsonPath"] = "$.localFiscalYear", ["targetJsonPath"] = "$.fiscalYear" },
        };

        return schema;
    }

    /// <summary>
    /// Build extension resource schema for base+extension constraint de-dup testing.
    /// </summary>
    private static JsonObject BuildExtensionConstraintDedupExtensionResourceSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ConstraintMergeExample",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.localFiscalYear",
                    ["targetJsonPath"] = "$.fiscalYear",
                },
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.fiscalYear",
                    ["targetJsonPath"] = "$.localFiscalYear",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
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
                                    ["extensionOnlyValue"] = new JsonObject { ["type"] = "integer" },
                                },
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
