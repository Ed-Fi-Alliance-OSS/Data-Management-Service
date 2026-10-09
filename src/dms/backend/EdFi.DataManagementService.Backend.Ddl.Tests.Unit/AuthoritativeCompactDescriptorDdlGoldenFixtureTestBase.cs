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

    private static (string Schema, string Name) TableIdentity(JsonElement table) =>
        (table.GetProperty("schema").GetString()!, ReadName(table));

    private static string ReadName(JsonElement element) => element.GetProperty("name").GetString()!;
}
