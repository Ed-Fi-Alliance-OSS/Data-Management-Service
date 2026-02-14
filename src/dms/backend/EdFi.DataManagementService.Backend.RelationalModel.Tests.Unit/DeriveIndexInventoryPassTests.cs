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
/// Test fixture for FK leftmost-prefix suppression in index derivation.
/// </summary>
[TestFixture]
public class Given_FK_Columns_Covered_By_PK
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildSchoolWithReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should suppress FK-support index when PK covers FK columns.
    /// </summary>
    [Test]
    public void It_should_suppress_FK_support_index_when_PK_covers_FK_columns()
    {
        // The Enrollment root table has PK on DocumentId and FK to dms.Document on DocumentId.
        // The FK columns [DocumentId] are a leftmost prefix of PK [DocumentId], so the
        // FK-support index should be suppressed.
        var enrollmentFkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment"
            && index.Kind == DbIndexKind.ForeignKeySupport
            && index.KeyColumns.Count == 1
            && index.KeyColumns[0].Value == "DocumentId"
        );

        enrollmentFkIndexes.Should().BeEmpty("FK on DocumentId is covered by PK on DocumentId");
    }

    /// <summary>
    /// It should create PK-implied index for each table.
    /// </summary>
    [Test]
    public void It_should_create_PK_implied_index_for_each_table()
    {
        var enrollmentPkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.PrimaryKey
        );

        enrollmentPkIndexes.Should().ContainSingle();
        var pk = enrollmentPkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should create UK-implied index for natural key.
    /// </summary>
    [Test]
    public void It_should_create_UK_implied_index_for_natural_key()
    {
        var enrollmentUkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.UniqueConstraint
        );

        enrollmentUkIndexes.Should().ContainSingle();
        var uk = enrollmentUkIndexes.Single();
        uk.IsUnique.Should().BeTrue();
        uk.KeyColumns.Select(c => c.Value).Should().Equal("School_DocumentId", "Student_DocumentId");
    }

    /// <summary>
    /// It should create FK-support indexes for non-covered FK columns.
    /// </summary>
    [Test]
    public void It_should_create_FK_support_indexes_for_non_covered_FK_columns()
    {
        // The Enrollment table has FKs to School and Student (via DocumentId columns).
        // These are NOT covered by PK [DocumentId] so they should produce FK-support indexes.
        var enrollmentFkIndexes = _indexes
            .Where(index => index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.ForeignKeySupport)
            .ToArray();

        enrollmentFkIndexes.Should().NotBeEmpty();

        foreach (var fkIndex in enrollmentFkIndexes)
        {
            fkIndex.IsUnique.Should().BeFalse();
            fkIndex.Name.Value.Should().StartWith("IX_Enrollment_");
        }
    }
}

/// <summary>
/// Test fixture for composite FK index naming.
/// </summary>
[TestFixture]
public class Given_Composite_FK_Index
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildSchoolWithReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should name composite FK index with all column names.
    /// </summary>
    [Test]
    public void It_should_name_composite_FK_index_with_all_column_names()
    {
        // The reference FK to School has two identity columns, producing a composite FK
        // with columns like [School_DocumentId]. Single-column FKs should be named IX_{Table}_{Col}.
        var schoolFkIndexes = _indexes
            .Where(index =>
                index.Table.Name == "Enrollment"
                && index.Kind == DbIndexKind.ForeignKeySupport
                && index.KeyColumns.Any(c => c.Value.Contains("School"))
            )
            .ToList();

        schoolFkIndexes.Should().NotBeEmpty("expected FK-support index with School columns");

        foreach (var fkIndex in schoolFkIndexes)
        {
            fkIndex.IsUnique.Should().BeFalse();
            // Verify all FK column names appear in the index name
            foreach (var keyColumn in fkIndex.KeyColumns)
            {
                fkIndex
                    .Name.Value.Should()
                    .Contain(
                        keyColumn.Value,
                        $"FK-support index name should include column '{keyColumn.Value}'"
                    );
            }
        }
    }
}

/// <summary>
/// Test fixture for abstract identity table indexes.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_Indexes
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildAbstractResourceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should derive PK index for abstract identity table.
    /// </summary>
    [Test]
    public void It_should_derive_PK_index_for_abstract_identity_table()
    {
        var identityTablePkIndexes = _indexes.Where(index =>
            index.Table.Name == "EducationOrganizationIdentity" && index.Kind == DbIndexKind.PrimaryKey
        );

        identityTablePkIndexes.Should().ContainSingle();
        var pk = identityTablePkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should derive UK indexes for abstract identity table.
    /// </summary>
    [Test]
    public void It_should_derive_UK_indexes_for_abstract_identity_table()
    {
        var identityTableUkIndexes = _indexes.Where(index =>
            index.Table.Name == "EducationOrganizationIdentity" && index.Kind == DbIndexKind.UniqueConstraint
        );

        identityTableUkIndexes.Should().NotBeEmpty();

        foreach (var ukIndex in identityTableUkIndexes)
        {
            ukIndex.IsUnique.Should().BeTrue();
        }
    }
}

/// <summary>
/// Test fixture for descriptor resource index exclusion.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Index_Derivation
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should not derive indexes for descriptor resources.
    /// </summary>
    [Test]
    public void It_should_not_derive_indexes_for_descriptor_resources()
    {
        _indexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for extension table index derivation.
/// </summary>
[TestFixture]
public class Given_Extension_Table_Indexes
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProjectSchema = CommonInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should derive PK index for extension table.
    /// </summary>
    [Test]
    public void It_should_derive_PK_index_for_extension_table()
    {
        var extensionPkIndexes = _indexes.Where(index =>
            index.Table.Name == "ContactExtension" && index.Kind == DbIndexKind.PrimaryKey
        );

        extensionPkIndexes.Should().ContainSingle();
        var pk = extensionPkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should suppress FK support index when extension FK covered by PK.
    /// </summary>
    [Test]
    public void It_should_suppress_FK_support_index_when_extension_FK_covered_by_PK()
    {
        var extensionFkIndexes = _indexes.Where(index =>
            index.Table.Name == "ContactExtension"
            && index.Kind == DbIndexKind.ForeignKeySupport
            && index.KeyColumns.Count == 1
            && index.KeyColumns[0].Value == "DocumentId"
        );

        extensionFkIndexes.Should().BeEmpty("FK on DocumentId is covered by PK on DocumentId");
    }

    /// <summary>
    /// It should name extension PK index correctly.
    /// </summary>
    [Test]
    public void It_should_name_extension_PK_index_correctly()
    {
        var extensionPk = _indexes.Single(index =>
            index.Table.Name == "ContactExtension" && index.Kind == DbIndexKind.PrimaryKey
        );

        extensionPk.Name.Value.Should().StartWith("PK_ContactExtension");
    }
}

/// <summary>
/// Test fixture for long index name validation.
/// </summary>
[TestFixture]
public class Given_Long_Index_Names_After_Derivation
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildLongNameResourceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should complete derivation without name collision.
    /// </summary>
    [Test]
    public void It_should_complete_derivation_without_name_collision()
    {
        _indexes.Should().NotBeEmpty();

        var duplicates = _indexes
            .GroupBy(i => (i.Table.Schema.Value, i.Name.Value))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        duplicates.Should().BeEmpty("all index names should be unique within their schema");
    }

    /// <summary>
    /// It should produce FK support index with long name.
    /// </summary>
    [Test]
    public void It_should_produce_FK_support_index_with_long_name()
    {
        var fkIndexes = _indexes.Where(index =>
            index.Table.Name == "StudentEducationOrganizationAssociation"
            && index.Kind == DbIndexKind.ForeignKeySupport
        );

        fkIndexes.Should().NotBeEmpty();
        fkIndexes
            .Should()
            .AllSatisfy(index =>
                index.Name.Value.Should().StartWith("IX_StudentEducationOrganizationAssociation_")
            );
    }
}

/// <summary>
/// Test fixture for key-unification storage-column FK index derivation.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Storage_Columns_For_FK_Indexes
{
    private IReadOnlyList<DbIndexInfo> _firstRunIndexes = default!;
    private IReadOnlyList<DbIndexInfo> _secondRunIndexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new KeyUnificationStorageForeignKeysFixturePass(),
            new DeriveIndexInventoryPass(),
        ]);

        _firstRunIndexes = builder
            .Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules())
            .IndexesInCreateOrder;
        _secondRunIndexes = builder
            .Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules())
            .IndexesInCreateOrder;
    }

    /// <summary>
    /// It should derive one FK-support index when multiple FK endpoints converge on one storage key set.
    /// </summary>
    [Test]
    public void It_should_derive_one_FK_support_index_for_converged_storage_key_sets()
    {
        var fkIndexes = _firstRunIndexes
            .Where(index => index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.ForeignKeySupport)
            .ToArray();

        fkIndexes.Should().ContainSingle();
        var fkIndex = fkIndexes.Single();

        fkIndex
            .KeyColumns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "School_SchoolId_Unified");
        fkIndex.Name.Value.Should().Be("IX_Enrollment_School_DocumentId_School_SchoolId_Unified");
    }

    /// <summary>
    /// It should remain deterministic and collision-free across runs.
    /// </summary>
    [Test]
    public void It_should_remain_deterministic_and_collision_free_across_runs()
    {
        _firstRunIndexes
            .Select(BuildIndexSignature)
            .Should()
            .Equal(_secondRunIndexes.Select(BuildIndexSignature));

        var duplicateIndexNames = _firstRunIndexes
            .GroupBy(index => $"{index.Table.Schema.Value}|{index.Name.Value}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        duplicateIndexNames.Should().BeEmpty();
    }

    /// <summary>
    /// Builds a stable index signature used to compare deterministic output.
    /// </summary>
    private static string BuildIndexSignature(DbIndexInfo index)
    {
        return $"{index.Kind}|{index.Table.Schema.Value}.{index.Table.Name}|{index.Name.Value}|"
            + $"{string.Join(",", index.KeyColumns.Select(column => column.Value))}|{index.IsUnique}";
    }
}

/// <summary>
/// Test fixture for key-unification alias-column FK invariant.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Alias_Columns_In_FKs
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new KeyUnificationAliasForeignKeyFixturePass(),
            new DeriveIndexInventoryPass(),
        ]);

        try
        {
            _ = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast when FK columns target alias columns while canonical storage columns exist.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_FKs_target_alias_columns()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("canonical storage column");
        _exception.Message.Should().Contain("School_SchoolId");
        _exception.Message.Should().Contain("School_SchoolId_Unified");
    }
}

/// <summary>
/// Test fixture for alias-column FK validation when canonical names do not follow suffix heuristics.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Alias_Columns_With_Custom_Canonical_Names_In_FKs
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new KeyUnificationAliasCustomCanonicalForeignKeyFixturePass(),
            new DeriveIndexInventoryPass(),
        ]);

        try
        {
            _ = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast for unified aliases even when canonical names do not end with "_Unified".
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_alias_FK_columns_use_custom_canonical_names()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("canonical storage column");
        _exception.Message.Should().Contain("School_SchoolId");
        _exception.Message.Should().Contain("SchoolStorageCanonical");
    }
}

/// <summary>
/// Test fixture for synthetic presence-column FK invariant.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Presence_Columns_In_FKs
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new KeyUnificationPresenceForeignKeyFixturePass(),
            new DeriveIndexInventoryPass(),
        ]);

        try
        {
            _ = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast when FK columns target synthetic presence columns from storage metadata.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_FKs_target_synthetic_presence_columns()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("synthetic presence column");
        _exception.Message.Should().Contain("SchoolPathGate");
    }
}

/// <summary>
/// Test fixture for stored columns that happen to end with key-unification suffix tokens.
/// </summary>
[TestFixture]
public class Given_Stored_Suffix_Columns_In_FKs
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new KeyUnificationStoredSuffixForeignKeyFixturePass(),
            new DeriveIndexInventoryPass(),
        ]);

        _indexes = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules()).IndexesInCreateOrder;
    }

    /// <summary>
    /// It should allow stored FK columns whose names end with "_Present" or "_Unified".
    /// </summary>
    [Test]
    public void It_should_allow_stored_FK_columns_with_unification_suffix_tokens()
    {
        var fkIndex = _indexes.Single(index =>
            index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.ForeignKeySupport
        );

        fkIndex
            .KeyColumns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "SchoolPath_Present");
        fkIndex.Name.Value.Should().Be("IX_Enrollment_School_DocumentId_SchoolPath_Present");
    }
}

/// <summary>
/// Test pass that injects a key-unification-like resource model where two FK endpoints share one storage key set.
/// </summary>
file sealed class KeyUnificationStorageForeignKeysFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyUnificationIndexInventoryFixtureBuilder.AddFixtureResource(
            context,
            useAliasColumnInForeignKey: false,
            addDuplicateForeignKeyEndpoint: true
        );
    }
}

/// <summary>
/// Test pass that injects a key-unification-like resource model with an FK targeting an alias column.
/// </summary>
file sealed class KeyUnificationAliasForeignKeyFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyUnificationIndexInventoryFixtureBuilder.AddFixtureResource(
            context,
            useAliasColumnInForeignKey: true,
            addDuplicateForeignKeyEndpoint: false
        );
    }
}

/// <summary>
/// Test pass that injects a key-unification-like resource model with a custom canonical storage name.
/// </summary>
file sealed class KeyUnificationAliasCustomCanonicalForeignKeyFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyUnificationIndexInventoryFixtureBuilder.AddFixtureResource(
            context,
            useAliasColumnInForeignKey: true,
            addDuplicateForeignKeyEndpoint: false,
            localCanonicalStorageColumnName: "SchoolStorageCanonical",
            targetCanonicalStorageColumnName: "SchoolStorageCanonical"
        );
    }
}

/// <summary>
/// Test pass that injects a key-unification-like resource model with an FK targeting a synthetic presence column.
/// </summary>
file sealed class KeyUnificationPresenceForeignKeyFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyUnificationIndexInventoryFixtureBuilder.AddFixtureResource(
            context,
            useAliasColumnInForeignKey: false,
            addDuplicateForeignKeyEndpoint: false,
            localCanonicalStorageColumnName: "SchoolStorageCanonical",
            targetCanonicalStorageColumnName: "SchoolStorageCanonical",
            aliasPresenceColumnName: "SchoolPathGate",
            usePresenceColumnInForeignKey: true
        );
    }
}

/// <summary>
/// Test pass that injects a key-unification-like resource model using stored suffix-named FK columns.
/// </summary>
file sealed class KeyUnificationStoredSuffixForeignKeyFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyUnificationIndexInventoryFixtureBuilder.AddFixtureResource(
            context,
            useAliasColumnInForeignKey: false,
            addDuplicateForeignKeyEndpoint: false,
            localCanonicalStorageColumnName: "SchoolPath_Present",
            targetCanonicalStorageColumnName: "SchoolPath_Present"
        );
    }
}

/// <summary>
/// Helpers that build synthetic key-unification fixtures for index inventory testing.
/// </summary>
file static class KeyUnificationIndexInventoryFixtureBuilder
{
    /// <summary>
    /// Adds one synthetic resource model to the context with configurable FK column choices.
    /// </summary>
    public static void AddFixtureResource(
        RelationalModelSetBuilderContext context,
        bool useAliasColumnInForeignKey,
        bool addDuplicateForeignKeyEndpoint,
        string localCanonicalStorageColumnName = "School_SchoolId_Unified",
        string targetCanonicalStorageColumnName = "SchoolId_Unified",
        string? aliasPresenceColumnName = null,
        bool usePresenceColumnInForeignKey = false
    )
    {
        var resourceKey = context.EffectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder[0];
        var schema = new DbSchemaName("edfi");
        var rootTableName = new DbTableName(schema, "Enrollment");
        var targetTable = new DbTableName(schema, "School");
        var localCanonicalStorageColumn = new DbColumnName(localCanonicalStorageColumnName);
        var targetCanonicalStorageColumn = new DbColumnName(targetCanonicalStorageColumnName);
        var aliasPresenceColumn = aliasPresenceColumnName is not null
            ? new DbColumnName(aliasPresenceColumnName)
            : (DbColumnName?)null;

        if (usePresenceColumnInForeignKey && aliasPresenceColumn is null)
        {
            throw new InvalidOperationException(
                $"Fixture option '{nameof(usePresenceColumnInForeignKey)}' requires "
                    + $"'{nameof(aliasPresenceColumnName)}'."
            );
        }

        DbColumnName selectedIdentityColumn;

        if (usePresenceColumnInForeignKey)
        {
            selectedIdentityColumn = aliasPresenceColumn!.Value;
        }
        else if (useAliasColumnInForeignKey)
        {
            selectedIdentityColumn = new DbColumnName("School_SchoolId");
        }
        else
        {
            selectedIdentityColumn = localCanonicalStorageColumn;
        }

        List<TableConstraint> constraints =
        [
            new TableConstraint.ForeignKey(
                "FK_Enrollment_SchoolPrimary_RefKey",
                [new DbColumnName("School_DocumentId"), selectedIdentityColumn],
                targetTable,
                [RelationalNameConventions.DocumentIdColumnName, targetCanonicalStorageColumn],
                OnDelete: ReferentialAction.NoAction,
                OnUpdate: ReferentialAction.NoAction
            ),
        ];

        if (addDuplicateForeignKeyEndpoint)
        {
            constraints.Add(
                new TableConstraint.ForeignKey(
                    "FK_Enrollment_SchoolAlias_RefKey",
                    [new DbColumnName("School_DocumentId"), selectedIdentityColumn],
                    targetTable,
                    [RelationalNameConventions.DocumentIdColumnName, targetCanonicalStorageColumn],
                    OnDelete: ReferentialAction.NoAction,
                    OnUpdate: ReferentialAction.NoAction
                )
            );
        }

        List<DbColumnModel> columns =
        [
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("School_DocumentId"),
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "School")
            ),
            new DbColumnModel(
                new DbColumnName("School_SchoolId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                TargetResource: null
            )
            {
                Storage = new ColumnStorage.UnifiedAlias(localCanonicalStorageColumn, aliasPresenceColumn),
            },
            new DbColumnModel(
                new DbColumnName("SchoolAlias_SchoolId"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolAliasReference.schoolId"),
                TargetResource: null
            )
            {
                Storage = new ColumnStorage.UnifiedAlias(localCanonicalStorageColumn, aliasPresenceColumn),
            },
            new DbColumnModel(
                localCanonicalStorageColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];

        if (aliasPresenceColumn is not null)
        {
            columns.Add(
                new DbColumnModel(
                    aliasPresenceColumn.Value,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                )
            );
        }

        var rootTable = new DbTableModel(
            rootTableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey(
                "PK_Enrollment",
                [new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart)]
            ),
            columns,
            constraints
        );

        var relationalModel = new RelationalResourceModel(
            resourceKey.Resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        context.ConcreteResourcesInNameOrder.Add(
            new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)
        );
    }
}

/// <summary>
/// Test schema builder for index inventory pass tests.
/// </summary>
internal static class IndexInventoryTestSchemaBuilder
{
    /// <summary>
    /// Build the standard pass list through index derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughIndexDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
        ];
    }

    /// <summary>
    /// Build project schema with references (School, Student, Enrollment).
    /// </summary>
    internal static JsonObject BuildSchoolWithReferenceProjectSchema()
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
    /// Build project schema with abstract resource.
    /// </summary>
    internal static JsonObject BuildAbstractResourceProjectSchema()
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
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSubclassSchoolSchema() },
        };
    }

    /// <summary>
    /// Build project schema with long resource name and references.
    /// </summary>
    internal static JsonObject BuildLongNameResourceProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["studentEducationOrganizationAssociations"] =
                    BuildStudentEducationOrganizationAssociationSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

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
            ["identityJsonPaths"] = new JsonArray
            {
                "$.schoolReference.schoolId",
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
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

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
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
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

    private static JsonObject BuildSubclassSchoolSchema()
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

    private static JsonObject BuildStudentEducationOrganizationAssociationSchema()
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
            ["resourceName"] = "StudentEducationOrganizationAssociation",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.educationOrganizationReference.educationOrganizationId",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
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
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
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
}
