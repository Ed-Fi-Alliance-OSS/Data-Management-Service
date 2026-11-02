// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

public abstract class AuthoritativeCompactDescriptorDdlGoldenFixtureTestBase : DdlGoldenFixtureTestBase
{
    protected virtual int ExpectedStoredDescriptorColumns => 595;
    protected virtual int ExpectedDescriptorBearingIndexes => 879;

    [TestCase("pgsql", "CREATE TABLE IF NOT EXISTS \"dms\".\"Document\"")]
    [TestCase("mssql", "CREATE TABLE [dms].[Document]")]
    public void It_should_emit_document_versions_and_authoritative_owner_timestamps(
        string dialect,
        string documentCreateStatement
    )
    {
        var sql = ReadActual($"{dialect}.sql");
        var documentStart = sql.IndexOf(documentCreateStatement, StringComparison.Ordinal);
        documentStart.Should().BeGreaterThanOrEqualTo(0);
        var documentEnd = sql.IndexOf("\n);", documentStart, StringComparison.Ordinal);
        documentEnd.Should().BeGreaterThan(documentStart);
        var documentDefinition = sql[documentStart..documentEnd];
        documentDefinition
            .Should()
            .Contain("ContentVersion")
            .And.NotContain("ContentLastModifiedAt")
            .And.NotContain("ContentCreatedAt");

        var descriptorCreateStatement = dialect is "pgsql"
            ? "CREATE TABLE IF NOT EXISTS \"dms\".\"Descriptor\""
            : "CREATE TABLE [dms].[Descriptor]";
        var descriptorStart = sql.IndexOf(descriptorCreateStatement, StringComparison.Ordinal);
        descriptorStart.Should().BeGreaterThanOrEqualTo(0);
        var descriptorEnd = sql.IndexOf("\n);", descriptorStart, StringComparison.Ordinal);
        descriptorEnd.Should().BeGreaterThan(descriptorStart);
        var descriptorDefinition = sql[descriptorStart..descriptorEnd];
        descriptorDefinition
            .Should()
            .Contain("ContentVersion")
            .And.Contain("ContentLastModifiedAt")
            .And.Contain("ContentCreatedAt");

        using var manifest = ReadManifest(dialect);
        var resources = manifest.RootElement.GetProperty("resource_details").EnumerateArray().ToArray();

        var ordinaryResources = resources
            .Where(resource => resource.GetProperty("storage_kind").GetString() is "RelationalTables")
            .ToArray();
        ordinaryResources.Should().NotBeEmpty();
        foreach (var resource in ordinaryResources)
        {
            var root = resource
                .GetProperty("tables")
                .EnumerateArray()
                .Single(table => table.GetProperty("scope").GetString() is "$");
            AssertOwnerStamps(root);
        }
    }

    [TestCase("pgsql")]
    [TestCase("mssql")]
    public void It_should_keep_the_creation_timestamp_on_resource_roots_only_and_out_of_indexes(
        string dialect
    )
    {
        using var manifest = ReadManifest(dialect);
        var resources = manifest.RootElement.GetProperty("resource_details").EnumerateArray().ToArray();

        var tablesWithCreatedAt = resources
            .SelectMany(resource =>
                resource
                    .GetProperty("tables")
                    .EnumerateArray()
                    .Where(table => table.GetProperty("columns").EnumerateArray().Any(IsContentCreatedAt))
                    .Select(table =>
                        (
                            StorageKind: resource.GetProperty("storage_kind").GetString(),
                            Scope: table.GetProperty("scope").GetString(),
                            Table: TableIdentity(table)
                        )
                    )
            )
            .ToArray();
        var ordinaryRootCount = resources.Count(resource =>
            resource.GetProperty("storage_kind").GetString() is "RelationalTables"
        );

        tablesWithCreatedAt.Should().HaveCount(ordinaryRootCount);
        tablesWithCreatedAt
            .Should()
            .OnlyContain(entry => entry.StorageKind == "RelationalTables" && entry.Scope == "$");
        manifest
            .RootElement.GetProperty("abstract_identity_tables")
            .EnumerateArray()
            .Should()
            .NotContain(identity =>
                identity.GetProperty("table").GetProperty("columns").EnumerateArray().Any(IsContentCreatedAt)
            );

        manifest
            .RootElement.GetProperty("indexes")
            .EnumerateArray()
            .Where(IndexIncludesContentCreatedAt)
            .Should()
            .BeEmpty();
    }

    [TestCase("pgsql")]
    [TestCase("mssql")]
    public void It_should_preserve_the_authoritative_descriptor_column_and_index_inventory(string dialect)
    {
        using var manifest = ReadManifest(dialect);
        var tables = ReadTables(manifest.RootElement);
        var storedColumns = tables
            .SelectMany(table =>
                ReadDescriptorColumns(table)
                    .Where(column =>
                        column.GetProperty("storage").GetProperty("kind").GetString() is "Stored"
                    )
                    .Select(column => (Table: table, Column: column))
            )
            .ToArray();

        storedColumns.Should().HaveCount(ExpectedStoredDescriptorColumns);
        storedColumns
            .Count(entry => entry.Table.GetProperty("schema").GetString() is "edfi")
            .Should()
            .Be(595, "the DS 5.2 baseline must remain covered in core and extension fixtures");

        var descriptorColumnsByTable = storedColumns
            .GroupBy(entry => TableIdentity(entry.Table))
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => ReadName(entry.Column)).ToHashSet()
            );
        manifest
            .RootElement.GetProperty("indexes")
            .EnumerateArray()
            .Count(index =>
                descriptorColumnsByTable.TryGetValue(
                    TableIdentity(index.GetProperty("table")),
                    out var columns
                )
                && index
                    .GetProperty("key_columns")
                    .EnumerateArray()
                    .Any(column => columns.Contains(column.GetString()!))
            )
            .Should()
            .Be(
                ExpectedDescriptorBearingIndexes,
                "descriptor-bearing index coverage is retained, not a savings measurement"
            );
    }

    [TestCase("pgsql")]
    [TestCase("mssql")]
    public void It_should_reject_wide_descriptor_storage_and_stale_foreign_key_targets(string dialect)
    {
        using var manifest = ReadManifest(dialect);
        var abstractTables = manifest
            .RootElement.GetProperty("abstract_identity_tables")
            .EnumerateArray()
            .Select(identity => TableIdentity(identity.GetProperty("table")))
            .ToHashSet();
        foreach (var table in ReadTables(manifest.RootElement))
        {
            var columns = ReadDescriptorColumns(table).ToArray();
            foreach (var column in columns)
            {
                column
                    .GetProperty("type")
                    .GetProperty("kind")
                    .GetString()
                    .Should()
                    .Be(
                        "Int32",
                        $"{TableIdentity(table)}.{ReadName(column)} is a compact descriptor value, including copied identities and aliases"
                    );
            }

            var descriptorForeignKeys = table
                .GetProperty("constraints")
                .EnumerateArray()
                .Where(constraint =>
                    constraint.GetProperty("kind").GetString() is "ForeignKey"
                    && TableIdentity(constraint.GetProperty("target_table")) == ("dms", "Descriptor")
                )
                .ToArray();
            foreach (var foreignKey in descriptorForeignKeys)
            {
                foreignKey
                    .GetProperty("target_columns")
                    .EnumerateArray()
                    .Select(column => column.GetString())
                    .Should()
                    .Equal("DescriptorId");
                foreignKey.GetProperty("on_delete").GetString().Should().Be("NoAction");
                foreignKey.GetProperty("on_update").GetString().Should().Be("NoAction");
            }

            // Abstract identity projections copy descriptor values but need no additional descriptor FK.
            if (!abstractTables.Contains(TableIdentity(table)))
            {
                descriptorForeignKeys
                    .SelectMany(fk => fk.GetProperty("columns").EnumerateArray())
                    .Select(column => column.GetString())
                    .Should()
                    .BeEquivalentTo(
                        columns
                            .Where(column =>
                                column.GetProperty("storage").GetProperty("kind").GetString() is "Stored"
                            )
                            .Select(ReadName)
                    );
            }
        }

        var projections = manifest
            .RootElement.GetProperty("abstract_union_views")
            .EnumerateArray()
            .SelectMany(view => view.GetProperty("output_columns").EnumerateArray())
            .Where(column =>
                column.TryGetProperty("is_descriptor_reference", out var flag) && flag.GetBoolean()
            )
            .ToArray();
        projections.Should().HaveCount(1);
        projections
            .Should()
            .OnlyContain(column => column.GetProperty("type").GetProperty("kind").GetString() == "Int32");
    }

    private JsonDocument ReadManifest(string dialect) =>
        JsonDocument.Parse(ReadActual($"relational-model.{dialect}.manifest.json"));

    private static JsonElement[] ReadTables(JsonElement manifest) =>
        [
            .. manifest
                .GetProperty("resource_details")
                .EnumerateArray()
                .SelectMany(resource => resource.GetProperty("tables").EnumerateArray()),
            .. manifest
                .GetProperty("abstract_identity_tables")
                .EnumerateArray()
                .Select(identity => identity.GetProperty("table")),
        ];

    private static IEnumerable<JsonElement> ReadDescriptorColumns(JsonElement table) =>
        table
            .GetProperty("columns")
            .EnumerateArray()
            .Where(column => column.GetProperty("kind").GetString() is "DescriptorFk");

    private static void AssertOwnerStamps(JsonElement table)
    {
        var columns = table.GetProperty("columns").EnumerateArray().ToArray();
        var timestamp = columns.Single(column => ReadName(column) is "ContentLastModifiedAt");
        timestamp.GetProperty("kind").GetString().Should().Be("AuthoritativeContentLastModifiedAt");
        timestamp.GetProperty("is_nullable").GetBoolean().Should().BeFalse();
        timestamp.GetProperty("storage").GetProperty("kind").GetString().Should().Be("Stored");

        var version = columns.Single(column => ReadName(column) is "ContentVersion");
        version.GetProperty("kind").GetString().Should().Be("MirroredContentVersion");
        version.GetProperty("is_nullable").GetBoolean().Should().BeFalse();

        var createdAt = columns.Single(IsContentCreatedAt);
        createdAt.GetProperty("kind").GetString().Should().Be("AuthoritativeContentCreatedAt");
        createdAt.GetProperty("type").GetProperty("kind").GetString().Should().Be("DateTime");
        createdAt.GetProperty("is_nullable").GetBoolean().Should().BeFalse();
        createdAt.GetProperty("source_path").ValueKind.Should().Be(JsonValueKind.Null);
        createdAt.GetProperty("storage").GetProperty("kind").GetString().Should().Be("Stored");

        AssertCreatedAtPlacement(table, columns);
    }

    /// <summary>
    /// Canonical ordering places the key columns first, then the stored columns without a source path
    /// (the stamps plus key-unification support columns) in ordinal name order. A unification column
    /// such as <c>AssessmentIdentifier_Unified</c> can sort ahead of the creation timestamp, so the
    /// check locates it within that group rather than at a fixed position.
    /// </summary>
    private static void AssertCreatedAtPlacement(JsonElement table, JsonElement[] columns)
    {
        var keyNames = table.GetProperty("key_columns").EnumerateArray().Select(ReadName).ToArray();
        columns.Take(keyNames.Length).Select(ReadName).Should().Equal(keyNames);

        var sourcelessStoredGroup = columns
            .Skip(keyNames.Length)
            .TakeWhile(column =>
                column.GetProperty("storage").GetProperty("kind").GetString() is "Stored"
                && column.GetProperty("source_path").ValueKind is JsonValueKind.Null
            )
            .Select(ReadName)
            .ToList();

        sourcelessStoredGroup
            .Should()
            .Contain(
                "ContentCreatedAt",
                $"{TableIdentity(table)} must place the creation timestamp in its group"
            )
            .And.BeInAscendingOrder(StringComparer.Ordinal);
        sourcelessStoredGroup[sourcelessStoredGroup.IndexOf("ContentCreatedAt") + 1]
            .Should()
            .Be("ContentLastModifiedAt");
    }

    private static bool IsContentCreatedAt(JsonElement column) => ReadName(column) is "ContentCreatedAt";

    private static bool IndexIncludesContentCreatedAt(JsonElement index) =>
        index
            .GetProperty("key_columns")
            .EnumerateArray()
            .Concat(
                index.TryGetProperty("include_columns", out var include)
                    ? include.EnumerateArray()
                    : Enumerable.Empty<JsonElement>()
            )
            .Any(column => column.GetString() == "ContentCreatedAt");

    private static (string Schema, string Name) TableIdentity(JsonElement table) =>
        (table.GetProperty("schema").GetString()!, ReadName(table));

    private static string ReadName(JsonElement element) => element.GetProperty("name").GetString()!;
}
