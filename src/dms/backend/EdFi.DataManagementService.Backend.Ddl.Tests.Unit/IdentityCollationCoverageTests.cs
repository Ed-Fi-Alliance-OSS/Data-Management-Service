// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// A column identified by its schema, table and column name.
/// </summary>
internal sealed record QualifiedColumn(string Schema, string Table, string Column)
{
    public override string ToString() => $"{Schema}.{Table}.{Column}";
}

/// <summary>
/// Builds the SQL Server derived model and DDL for an authoritative fixture once per test run, and parses
/// the emitted <c>CREATE TABLE</c> column definitions.
/// </summary>
internal static partial class AuthoritativeMssqlIdentityCollation
{
    internal const string IdentityCollateClause = "COLLATE SQL_Latin1_General_CP1_CI_AS";

    private static readonly ConcurrentDictionary<
        string,
        Lazy<(DerivedRelationalModelSet ModelSet, string Sql)>
    > _builds = new(StringComparer.Ordinal);

    internal static (DerivedRelationalModelSet ModelSet, string Sql) Build(string fixtureName)
    {
        return _builds
            .GetOrAdd(
                fixtureName,
                name => new Lazy<(DerivedRelationalModelSet, string)>(() =>
                {
                    var fixtureDirectory = Path.GetFullPath(
                        Path.Combine(
                            FixtureTestHelper.FindProjectRoot(),
                            "..",
                            "Fixtures",
                            "authoritative",
                            name
                        )
                    );
                    var config = FixtureConfigReader.Read(fixtureDirectory);
                    var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadEffectiveSchemaSet(
                        fixtureDirectory,
                        config.ApiSchemaFiles
                    );

                    return DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, SqlDialect.Mssql);
                })
            )
            .Value;
    }

    /// <summary>
    /// Parses every column definition line inside every <c>CREATE TABLE [schema].[table]</c> block.
    /// </summary>
    internal static IReadOnlyDictionary<QualifiedColumn, string> ParseColumnDefinitions(string sql)
    {
        Dictionary<QualifiedColumn, string> definitions = [];
        (string Schema, string Table)? currentTable = null;

        foreach (var rawLine in sql.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var header = CreateTableHeader().Match(line);

            if (header.Success)
            {
                currentTable = (header.Groups["schema"].Value, header.Groups["table"].Value);
                continue;
            }

            if (currentTable is null)
            {
                continue;
            }

            if (line.StartsWith(')'))
            {
                currentTable = null;
                continue;
            }

            var column = ColumnDefinition().Match(line);

            if (column.Success)
            {
                definitions[
                    new QualifiedColumn(
                        currentTable.Value.Schema,
                        currentTable.Value.Table,
                        column.Groups["column"].Value
                    )
                ] = line.Trim();
            }
        }

        return definitions;
    }

    /// <summary>
    /// The relational-tables resource tables and abstract identity tables of the derived model.
    /// </summary>
    internal static IEnumerable<DbTableModel> ResourceAndAbstractIdentityTables(DerivedRelationalModelSet set)
    {
        return set
            .ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind == ResourceStorageKind.RelationalTables
            )
            .SelectMany(resource => resource.RelationalModel.TablesInDependencyOrder)
            .Concat(set.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel));
    }

    /// <summary>
    /// The shared <c>dms.Descriptor</c> table model (identical across descriptor resources).
    /// </summary>
    internal static DbTableModel DescriptorTable(DerivedRelationalModelSet set)
    {
        return set
            .ConcreteResourcesInNameOrder.First(resource =>
                resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .RelationalModel.Root;
    }

    internal static QualifiedColumn Qualify(DbTableName table, DbColumnName column) =>
        new(table.Schema.Value, table.Name, column.Value);

    internal static bool IsString(DbColumnModel column) => column.ScalarType?.Kind == ScalarKind.String;

    [GeneratedRegex(@"^CREATE TABLE \[(?<schema>[^\]]+)\]\.\[(?<table>[^\]]+)\]$")]
    private static partial Regex CreateTableHeader();

    [GeneratedRegex(@"^\s+\[(?<column>[^\]]+)\] ")]
    private static partial Regex ColumnDefinition();
}

/// <summary>
/// AC2: the derived ds-5.2 SQL Server model flags one representative column of every identity category and
/// leaves non-identity strings unflagged.
/// </summary>
[TestFixture]
[Category("Authoritative")]
public class Given_The_Authoritative_Ds52_Mssql_Derived_Model_Identity_Inventory
{
    private DerivedRelationalModelSet _set = default!;

    [SetUp]
    public void Setup()
    {
        _set = AuthoritativeMssqlIdentityCollation.Build("ds-5.2").ModelSet;
    }

    private DbColumnModel Column(string tableName, string columnName) =>
        _set
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(_set.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel))
            .First(table => table.Table.Schema.Value == "edfi" && table.Table.Name == tableName)
            .Columns.Single(column => column.ColumnName.Value == columnName);

    private TrackedChangeColumnInfo TrackedColumn(string tableName, string oldColumnName) =>
        _set
            .TrackedChangeTablesInNameOrder.Single(table =>
                table.Table.Schema.Value == "tracked_changes_edfi" && table.Table.Name == tableName
            )
            .ValueColumnsInTableOrder.Single(column => column.OldColumnName.Value == oldColumnName);

    [Test]
    public void It_flags_canonical_identity()
    {
        Column("Student", "StudentUniqueId").UsesSqlServerIdentityCollation.Should().BeTrue();
    }

    [Test]
    public void It_flags_copied_identity()
    {
        Column("StudentSchoolAssociation", "Student_StudentUniqueId")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_flags_abstract_identity()
    {
        Column("GeneralStudentProgramAssociationIdentity", "Program_ProgramName")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_flags_descriptor_identity()
    {
        var descriptorTable = AuthoritativeMssqlIdentityCollation.DescriptorTable(_set);

        descriptorTable
            .Columns.Where(column => DescriptorIdentityTextColumns.All.Contains(column.ColumnName))
            .Should()
            .HaveCount(2)
            .And.OnlyContain(column => column.UsesSqlServerIdentityCollation);
    }

    [Test]
    public void It_flags_tracked_change_identity()
    {
        TrackedColumn("AcademicWeek", "OldWeekIdentifier").UsesSqlServerIdentityCollation.Should().BeTrue();
        TrackedColumn("Descriptor", "OldNamespace").UsesSqlServerIdentityCollation.Should().BeTrue();
        TrackedColumn("Descriptor", "OldCodeValue").UsesSqlServerIdentityCollation.Should().BeTrue();
    }

    [Test]
    public void It_flags_collection_identity()
    {
        Column("StudentEducationOrganizationAssociationAddress", "City")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_leaves_non_identity_strings_unflagged()
    {
        Column("School", "NameOfInstitution").UsesSqlServerIdentityCollation.Should().BeFalse();
        Column("GeneralStudentProgramAssociationIdentity", "Discriminator")
            .UsesSqlServerIdentityCollation.Should()
            .BeFalse();
        AuthoritativeMssqlIdentityCollation
            .DescriptorTable(_set)
            .Columns.Single(column => column.ColumnName.Value == "ShortDescription")
            .UsesSqlServerIdentityCollation.Should()
            .BeFalse();
    }
}

/// <summary>
/// AC1: every identity text column of an authoritative SQL Server schema is flagged and emitted with the
/// identity collation, and no other generated column is.
/// </summary>
[TestFixture("ds-5.2")]
[TestFixture("ds-5.2-tpdm")]
[TestFixture("sample")]
[Category("Authoritative")]
public class Given_Authoritative_Mssql_Identity_Collation_Coverage(string fixtureName)
{
    private DerivedRelationalModelSet _set = default!;
    private IReadOnlyDictionary<QualifiedColumn, string> _definitions = default!;

    [SetUp]
    public void Setup()
    {
        var (modelSet, sql) = AuthoritativeMssqlIdentityCollation.Build(fixtureName);

        _set = modelSet;
        _definitions = AuthoritativeMssqlIdentityCollation.ParseColumnDefinitions(sql);
    }

    /// <summary>
    /// Independent oracle: identity values are what unique and foreign key constraints compare, so every
    /// string column named in a Unique or ForeignKey column list must carry the identity-text role. The
    /// oracle reads constraint column lists, not names, so a missed binding category fails here instead of
    /// silently inheriting the database default. Shared descriptors are excluded: the core-owned
    /// dms.Descriptor table is covered by Given_The_Descriptor_Identity_Text_Columns.
    /// </summary>
    [Test]
    public void It_flags_every_string_column_in_a_unique_or_foreign_key()
    {
        var unflagged = AuthoritativeMssqlIdentityCollation
            .ResourceAndAbstractIdentityTables(_set)
            .SelectMany(table =>
            {
                var columnsByName = table.Columns.ToDictionary(column => column.ColumnName);

                return table
                    .Constraints.SelectMany(constraint =>
                        constraint switch
                        {
                            TableConstraint.Unique unique => unique.Columns,
                            TableConstraint.ForeignKey foreignKey => foreignKey.Columns,
                            _ => [],
                        }
                    )
                    .Select(columnName => columnsByName[columnName])
                    .Where(column =>
                        AuthoritativeMssqlIdentityCollation.IsString(column)
                        && !column.UsesSqlServerIdentityCollation
                    )
                    .Select(column =>
                        AuthoritativeMssqlIdentityCollation.Qualify(table.Table, column.ColumnName)
                    );
            })
            .Distinct()
            .ToArray();

        unflagged.Should().BeEmpty();
    }

    [Test]
    public void It_emits_the_identity_collation_on_every_flagged_stored_column()
    {
        var missing = AuthoritativeMssqlIdentityCollation
            .ResourceAndAbstractIdentityTables(_set)
            .SelectMany(table =>
                table
                    .Columns.Where(column =>
                        column.UsesSqlServerIdentityCollation && column.Storage is ColumnStorage.Stored
                    )
                    .Select(column =>
                        AuthoritativeMssqlIdentityCollation.Qualify(table.Table, column.ColumnName)
                    )
            )
            .Where(column =>
                !_definitions[column].Contains(AuthoritativeMssqlIdentityCollation.IdentityCollateClause)
            )
            .ToArray();

        missing.Should().BeEmpty();
    }

    [Test]
    public void It_routes_every_flagged_unified_alias_to_a_flagged_collated_canonical_column()
    {
        var aliasCount = 0;

        foreach (var table in AuthoritativeMssqlIdentityCollation.ResourceAndAbstractIdentityTables(_set))
        {
            foreach (var alias in table.Columns.Where(column => column.UsesSqlServerIdentityCollation))
            {
                if (alias.Storage is not ColumnStorage.UnifiedAlias unifiedAlias)
                {
                    continue;
                }

                aliasCount++;
                var canonical = table.Columns.Single(column =>
                    column.ColumnName == unifiedAlias.CanonicalColumn
                );
                var aliasDefinition = _definitions[
                    AuthoritativeMssqlIdentityCollation.Qualify(table.Table, alias.ColumnName)
                ];

                canonical.UsesSqlServerIdentityCollation.Should().BeTrue();
                canonical.Storage.Should().BeOfType<ColumnStorage.Stored>();
                _definitions[AuthoritativeMssqlIdentityCollation.Qualify(table.Table, canonical.ColumnName)]
                    .Should()
                    .Contain(AuthoritativeMssqlIdentityCollation.IdentityCollateClause);
                aliasDefinition.Should().Contain(") PERSISTED").And.NotContain("COLLATE");
            }
        }

        aliasCount.Should().BePositive("every authoritative fixture has key-unified identity aliases");
    }

    /// <summary>
    /// Inverse: the emitted identity-collated columns are exactly the flagged stored model columns, the
    /// flagged tracked-change Old/New pairs and the descriptor identity text columns.
    /// </summary>
    [Test]
    public void It_emits_the_identity_collation_on_no_other_column()
    {
        var expected = AuthoritativeMssqlIdentityCollation
            .ResourceAndAbstractIdentityTables(_set)
            .SelectMany(table =>
                table
                    .Columns.Where(column =>
                        column.UsesSqlServerIdentityCollation && column.Storage is ColumnStorage.Stored
                    )
                    .Select(column =>
                        AuthoritativeMssqlIdentityCollation.Qualify(table.Table, column.ColumnName)
                    )
            )
            .Concat(
                _set.TrackedChangeTablesInNameOrder.SelectMany(table =>
                    table
                        .ValueColumnsInTableOrder.Where(column => column.UsesSqlServerIdentityCollation)
                        .SelectMany(column =>
                            new[]
                            {
                                AuthoritativeMssqlIdentityCollation.Qualify(
                                    table.Table,
                                    column.OldColumnName
                                ),
                                AuthoritativeMssqlIdentityCollation.Qualify(
                                    table.Table,
                                    column.NewColumnName
                                ),
                            }
                        )
                )
            )
            .Concat(
                DescriptorIdentityTextColumns.All.Select(column => new QualifiedColumn(
                    "dms",
                    "Descriptor",
                    column.Value
                ))
            )
            .ToHashSet();

        var emitted = _definitions
            .Where(entry => entry.Value.Contains(AuthoritativeMssqlIdentityCollation.IdentityCollateClause))
            .Select(entry => entry.Key)
            .ToHashSet();

        emitted.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void It_preserves_the_lifecycle_BIN2_collation()
    {
        _definitions
            .Where(entry =>
                entry.Value.Contains("COLLATE")
                && !entry.Value.Contains(AuthoritativeMssqlIdentityCollation.IdentityCollateClause)
            )
            .Select(entry => entry.Value)
            .Should()
            .Equal("[ProjectionLifecycleState] varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,");
    }
}

/// <summary>
/// The derived <c>dms.Descriptor</c> model and the core-emitted <c>dms.Descriptor</c> DDL both follow the
/// shared <see cref="DescriptorIdentityTextColumns.All"/> list.
/// </summary>
[TestFixture]
[Category("Authoritative")]
public class Given_The_Descriptor_Identity_Text_Columns
{
    private DerivedRelationalModelSet _set = default!;
    private IReadOnlyDictionary<QualifiedColumn, string> _definitions = default!;

    [SetUp]
    public void Setup()
    {
        var (modelSet, sql) = AuthoritativeMssqlIdentityCollation.Build("ds-5.2");

        _set = modelSet;
        _definitions = AuthoritativeMssqlIdentityCollation.ParseColumnDefinitions(sql);
    }

    [Test]
    public void It_flags_exactly_the_shared_list_on_every_descriptor_model()
    {
        _set.ConcreteResourcesInNameOrder.Where(resource =>
                resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(resource =>
                resource
                    .RelationalModel.Root.Columns.Where(column => column.UsesSqlServerIdentityCollation)
                    .Select(column => column.ColumnName)
                    .ToHashSet()
                    .SetEquals(DescriptorIdentityTextColumns.All)
            );
    }

    [Test]
    public void It_collates_exactly_the_shared_list_in_the_emitted_descriptor_table()
    {
        var descriptorDefinitions = _definitions
            .Where(entry => entry.Key.Schema == "dms" && entry.Key.Table == "Descriptor")
            .ToArray();

        descriptorDefinitions
            .Should()
            .Contain(entry => entry.Key.Column == "ShortDescription")
            .And.Contain(entry => entry.Key.Column == "Uri");
        descriptorDefinitions
            .Where(entry => entry.Value.Contains(AuthoritativeMssqlIdentityCollation.IdentityCollateClause))
            .Select(entry => entry.Key.Column)
            .Should()
            .BeEquivalentTo(DescriptorIdentityTextColumns.All.Select(column => column.Value));
    }
}
