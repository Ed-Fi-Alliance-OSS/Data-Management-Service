// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for schema-scoped index collisions in PostgreSQL after shortening.
/// </summary>
[TestFixture]
public class Given_Index_Name_Shortening_Collision_Across_Tables_For_Pgsql
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new IndexShorteningCollisionAcrossTablesPass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IX_LongAlpha"] = "IX_Collision",
                ["IX_LongBeta"] = "IX_Collision",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with a schema-scoped index collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_schema_scoped_index_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("index name collision");
        _exception.Message.Should().Contain("schema 'edfi'");
    }
}

/// <summary>
/// Test fixture for table-scoped index collisions in SQL Server after shortening.
/// </summary>
[TestFixture]
public class Given_Index_Name_Shortening_Collision_Across_Tables_For_Mssql
{
    private Exception? _exception;
    private DerivedRelationalModelSet? _result;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new IndexShorteningCollisionAcrossTablesPass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Mssql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IX_LongAlpha"] = "IX_Collision",
                ["IX_LongBeta"] = "IX_Collision",
            }
        );

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate shortened index names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_shortened_index_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for table-scoped trigger collisions in PostgreSQL after shortening.
/// </summary>
[TestFixture]
public class Given_Trigger_Name_Shortening_Collision_Across_Tables_For_Pgsql
{
    private Exception? _exception;
    private DerivedRelationalModelSet? _result;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new TriggerShorteningCollisionAcrossTablesPass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TR_LongAlpha"] = "TR_Collision",
                ["TR_LongBeta"] = "TR_Collision",
            }
        );

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate shortened trigger names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_shortened_trigger_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for schema-scoped trigger collisions in SQL Server after shortening.
/// </summary>
[TestFixture]
public class Given_Trigger_Name_Shortening_Collision_Across_Tables_For_Mssql
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new TriggerShorteningCollisionAcrossTablesPass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Mssql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TR_LongAlpha"] = "TR_Collision",
                ["TR_LongBeta"] = "TR_Collision",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with a schema-scoped trigger collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_schema_scoped_trigger_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("trigger name collision");
        _exception.Message.Should().Contain("schema 'edfi'");
    }
}

/// <summary>
/// Verifies that identifier shortening rewrites a DocumentStamping trigger's
/// <see cref="DbTriggerInfo.MirrorStampTargetTable"/> consistently with every other table
/// reference, including when the trigger's own table is unaffected (the mirror target points at a
/// different, shortened table — the root for a child / extension trigger).
/// </summary>
[TestFixture]
public class Given_DocumentStamping_Trigger_With_Shortened_Mirror_Stamp_Target_Table
{
    private DerivedRelationalModelSet _result = null!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new MirrorStampTargetShorteningPass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        // Map only the resource-root table name to a shorter identifier. The mirror-stamp target on
        // both triggers references that root, so both must be shortened to match it.
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["School"] = "Sch" }
        );

        _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
    }

    /// <summary>
    /// It should shorten the mirror-stamp target on the root trigger together with its own table.
    /// </summary>
    [Test]
    public void It_should_shorten_the_mirror_stamp_target_on_the_root_trigger()
    {
        var rootTrigger = _result.TriggersInCreateOrder.Single(trigger =>
            trigger.Name.Value == "TR_RootStamp"
        );
        rootTrigger.Table.Name.Should().Be("Sch");
        rootTrigger.MirrorStampTargetTable.Should().NotBeNull();
        rootTrigger.MirrorStampTargetTable!.Value.Name.Should().Be("Sch");
    }

    /// <summary>
    /// It should shorten the mirror-stamp target on a child trigger whose own table is unchanged.
    /// </summary>
    [Test]
    public void It_should_shorten_the_mirror_stamp_target_on_a_child_trigger_with_unchanged_table()
    {
        var childTrigger = _result.TriggersInCreateOrder.Single(trigger =>
            trigger.Name.Value == "TR_ChildStamp"
        );
        childTrigger.Table.Name.Should().Be("Student");
        childTrigger.MirrorStampTargetTable.Should().NotBeNull();
        childTrigger.MirrorStampTargetTable!.Value.Name.Should().Be("Sch");
    }
}

/// <summary>
/// Verifies that identifier shortening rewrites a tracked-change table's own name and its
/// <c>Old</c>/<c>New</c> value-column names.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_Table_And_Columns_Shortening
{
    private DerivedRelationalModelSet _result = null!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new TrackedChangeShorteningFixturePass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LongTrackedTable"] = "ShortTable",
                ["OldLongValueColumn"] = "OldShort",
                ["NewLongValueColumn"] = "NewShort",
            }
        );

        _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
    }

    /// <summary>
    /// It should shorten the tracked-change table name.
    /// </summary>
    [Test]
    public void It_should_shorten_the_tracked_change_table_name()
    {
        _result
            .TrackedChangeTablesInNameOrder.Should()
            .ContainSingle()
            .Which.Table.Name.Should()
            .Be("ShortTable");
    }

    /// <summary>
    /// It should shorten the Old and New value-column names.
    /// </summary>
    [Test]
    public void It_should_shorten_the_old_and_new_value_column_names()
    {
        var column = _result.TrackedChangeTablesInNameOrder.Single().ValueColumnsInTableOrder.Single();

        column.OldColumnName.Value.Should().Be("OldShort");
        column.NewColumnName.Value.Should().Be("NewShort");
    }
}

/// <summary>
/// Verifies that identifier shortening rewrites a DocumentStamping trigger's
/// <see cref="TrackedChangeAttachment.TrackedChangeTable"/> reference consistently with the
/// tracked-change table it points to.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_ChangeTracking_Reference_Shortening
{
    private DerivedRelationalModelSet _result = null!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new TrackedChangeAttachmentShorteningFixturePass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["LongTrackedTable"] = "ShortTable" }
        );

        _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
    }

    /// <summary>
    /// It should shorten the change-tracking reference to match the shortened tracked-change table.
    /// </summary>
    [Test]
    public void It_should_shorten_the_change_tracking_reference_to_match_the_table()
    {
        _result
            .TrackedChangeTablesInNameOrder.Should()
            .ContainSingle()
            .Which.Table.Name.Should()
            .Be("ShortTable");

        var stamping = (TriggerKindParameters.DocumentStamping)
            _result.TriggersInCreateOrder.Single(trigger => trigger.Name.Value == "TR_Stamp").Parameters;

        stamping.ChangeTracking.Should().NotBeNull();
        stamping.ChangeTracking!.TrackedChangeTable.Name.Should().Be("ShortTable");
    }
}

/// <summary>
/// Verifies that collision detection covers tracked-change value columns that shorten to the same name
/// within a single table.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_Column_Shortening_Collision_Within_Table
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new TrackedChangeColumnCollisionFixturePass(),
            new ApplyDialectIdentifierShorteningPass(),
        ]);
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OldAlphaLong"] = "OldDup",
                ["OldBetaLong"] = "OldDup",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with a tracked-change column-name collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_tracked_change_column_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("column name collision");
    }
}

file sealed class MirrorStampTargetShorteningPass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        var schema = new DbSchemaName("edfi");
        var rootTable = new DbTableName(schema, "School");
        var childTable = new DbTableName(schema, "Student");

        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_RootStamp"),
                rootTable,
                [],
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: rootTable
            )
        );
        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_ChildStamp"),
                childTable,
                [],
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: rootTable
            )
        );
    }
}

file sealed class IndexShorteningCollisionAcrossTablesPass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        var schema = new DbSchemaName("edfi");
        var tableAlpha = new DbTableName(schema, "School");
        var tableBeta = new DbTableName(schema, "Student");

        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName("IX_LongAlpha"),
                tableAlpha,
                [],
                false,
                DbIndexKind.ForeignKeySupport
            )
        );
        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName("IX_LongBeta"),
                tableBeta,
                [],
                false,
                DbIndexKind.ForeignKeySupport
            )
        );
    }
}

file sealed class TriggerShorteningCollisionAcrossTablesPass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        var schema = new DbSchemaName("edfi");
        var tableAlpha = new DbTableName(schema, "School");
        var tableBeta = new DbTableName(schema, "Student");

        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_LongAlpha"),
                tableAlpha,
                [],
                [],
                new TriggerKindParameters.DocumentStamping()
            )
        );
        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_LongBeta"),
                tableBeta,
                [],
                [],
                new TriggerKindParameters.DocumentStamping()
            )
        );
    }
}

/// <summary>
/// Shared builders for tracked-change inventory entries used by shortening fixtures.
/// </summary>
file static class TrackedChangeShorteningFixtures
{
    internal static readonly DbSchemaName TrackedSchema = new("tracked_changes_edfi");
    internal static readonly DbSchemaName SourceSchema = new("edfi");

    internal static TrackedChangeColumnInfo ValueColumn(string oldName, string newName)
    {
        return new TrackedChangeColumnInfo(
            new DbColumnName(oldName),
            new DbColumnName(newName),
            "$.value",
            CanonicalStorageColumn: null,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            new RelationalScalarType(ScalarKind.String, MaxLength: 50),
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity
        );
    }

    internal static TrackedChangeTableInfo Table(
        string tableName,
        IReadOnlyList<TrackedChangeColumnInfo> valueColumns
    )
    {
        return new TrackedChangeTableInfo(
            new DbTableName(TrackedSchema, tableName),
            TrackedChangeTableKind.Resource,
            new DbTableName(SourceSchema, "School"),
            valueColumns,
            SystemColumns: [],
            PrimaryKeyColumns: [new DbColumnName("ChangeVersion")],
            DescriptorJoins: [],
            PersonJoins: []
        );
    }
}

file sealed class TrackedChangeShorteningFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        context.TrackedChangeInventory.Add(
            TrackedChangeShorteningFixtures.Table(
                "LongTrackedTable",
                [TrackedChangeShorteningFixtures.ValueColumn("OldLongValueColumn", "NewLongValueColumn")]
            )
        );
    }
}

file sealed class TrackedChangeAttachmentShorteningFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        var trackedTable = new DbTableName(TrackedChangeShorteningFixtures.TrackedSchema, "LongTrackedTable");
        var sourceTable = new DbTableName(TrackedChangeShorteningFixtures.SourceSchema, "School");

        context.TrackedChangeInventory.Add(
            TrackedChangeShorteningFixtures.Table(
                "LongTrackedTable",
                [TrackedChangeShorteningFixtures.ValueColumn("OldValue", "NewValue")]
            )
        );

        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_Stamp"),
                sourceTable,
                [],
                [],
                new TriggerKindParameters.DocumentStamping(new TrackedChangeAttachment(trackedTable)),
                MirrorStampTargetTable: sourceTable
            )
        );
    }
}

file sealed class TrackedChangeColumnCollisionFixturePass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        context.TrackedChangeInventory.Add(
            TrackedChangeShorteningFixtures.Table(
                "TrackedTable",
                [
                    TrackedChangeShorteningFixtures.ValueColumn("OldAlphaLong", "NewAlphaLong"),
                    TrackedChangeShorteningFixtures.ValueColumn("OldBetaLong", "NewBetaLong"),
                ]
            )
        );
    }
}

file sealed class MappedDialectRules : ISqlDialectRules
{
    private readonly SqlDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _mapping;
    private readonly SqlScalarTypeDefaults _defaults;

    public MappedDialectRules(SqlDialect dialect, IReadOnlyDictionary<string, string> mapping)
    {
        _dialect = dialect;
        _mapping = mapping;
        _defaults = dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialectRules().ScalarTypeDefaults,
            SqlDialect.Mssql => new MssqlDialectRules().ScalarTypeDefaults,
            _ => new PgsqlDialectRules().ScalarTypeDefaults,
        };
    }

    /// <summary>
    /// Gets dialect.
    /// </summary>
    public SqlDialect Dialect => _dialect;

    /// <summary>
    /// Gets max identifier length.
    /// </summary>
    public int MaxIdentifierLength =>
        _dialect switch
        {
            SqlDialect.Pgsql => 63,
            SqlDialect.Mssql => 128,
            _ => 128,
        };

    /// <summary>
    /// Gets scalar type defaults.
    /// </summary>
    public SqlScalarTypeDefaults ScalarTypeDefaults => _defaults;

    /// <summary>
    /// Shorten identifier.
    /// </summary>
    public string ShortenIdentifier(string identifier)
    {
        return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
    }
}
