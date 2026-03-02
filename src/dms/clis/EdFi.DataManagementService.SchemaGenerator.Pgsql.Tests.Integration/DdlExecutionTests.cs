// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Pgsql.Tests.Integration;

[TestFixture]
public class Given_Generated_Ddl_For_Basic_Schema
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _databaseName = $"edfi_schemagen_test_{Guid.NewGuid():N}";
        await DatabaseHelper.CreateDatabaseAsync(_databaseName);
        _connectionString = DatabaseHelper.GetTestDatabaseConnectionString(_databaseName);

        // Create the Document stub table for FK references
        await DatabaseHelper.ExecuteSqlAsync(_connectionString, DatabaseHelper.GetDocumentTableStubSql());

        // Generate and execute DDL
        var logger = NullLoggerFactory.Instance.CreateLogger<PgsqlDdlGeneratorStrategy>();
        var strategy = new PgsqlDdlGeneratorStrategy(logger);
        var schema = CreateBasicApiSchema();
        var options = new DdlGenerationOptions
        {
            GenerateForeignKeyConstraints = true,
            GenerateNaturalKeyConstraints = true,
            SkipNaturalKeyViews = true,
            SkipUnionViews = true,
        };
        var ddl = strategy.GenerateDdlString(schema, options);
        await DatabaseHelper.ExecuteSqlAsync(_connectionString, ddl);
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        if (!string.IsNullOrEmpty(_databaseName))
        {
            await DatabaseHelper.DropDatabaseAsync(_databaseName);
        }
    }

    [Test]
    public async Task It_should_create_the_expected_table()
    {
        var tables = await DatabaseHelper.GetTablesInSchemaAsync(_connectionString, "dms");
        tables.Should().Contain("edfi_student");
    }

    [Test]
    public async Task It_should_create_expected_columns()
    {
        var columns = await DatabaseHelper.GetColumnsAsync(_connectionString, "dms", "edfi_student");
        var columnNames = columns.Select(c => c.Name).ToList();

        columnNames.Should().Contain("id");
        columnNames.Should().Contain("document_id");
        columnNames.Should().Contain("document_partitionkey");
        columnNames.Should().Contain("studentuniqueid");
        columnNames.Should().Contain("firstname");
        columnNames.Should().Contain("lastsurname");
    }

    [Test]
    public async Task It_should_create_unique_constraints_for_natural_keys()
    {
        var constraints = await DatabaseHelper.GetConstraintsAsync(_connectionString, "dms", "edfi_student");
        constraints
            .Where(c => c.Type == "u")
            .Select(c => c.Name.ToLowerInvariant())
            .Should()
            .Contain(name => name.Contains("student"));
    }

    [Test]
    public async Task It_should_create_foreign_key_to_document_table()
    {
        var constraints = await DatabaseHelper.GetConstraintsAsync(_connectionString, "dms", "edfi_student");
        constraints
            .Where(c => c.Type == "f")
            .Select(c => c.Name.ToLowerInvariant())
            .Should()
            .Contain(name => name.Contains("document"));
    }

    private static ApiSchema CreateBasicApiSchema()
    {
        return new ApiSchema
        {
            ProjectSchema = new ProjectSchema
            {
                ProjectName = "EdFi",
                ProjectVersion = "1.0.0",
                IsExtensionProject = false,
                Description = "Basic test schema.",
                ResourceSchemas = new Dictionary<string, ResourceSchema>
                {
                    ["students"] = new ResourceSchema
                    {
                        ResourceName = "Student",
                        FlatteningMetadata = new FlatteningMetadata
                        {
                            Table = new TableMetadata
                            {
                                BaseName = "Student",
                                JsonPath = "$.Student",
                                Columns =
                                [
                                    new ColumnMetadata
                                    {
                                        ColumnName = "StudentUniqueId",
                                        ColumnType = "string",
                                        MaxLength = "32",
                                        IsNaturalKey = true,
                                        IsRequired = true,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "FirstName",
                                        ColumnType = "string",
                                        MaxLength = "75",
                                        IsRequired = true,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "LastSurname",
                                        ColumnType = "string",
                                        MaxLength = "75",
                                        IsRequired = true,
                                    },
                                ],
                                ChildTables = [],
                            },
                        },
                    },
                },
            },
        };
    }
}
