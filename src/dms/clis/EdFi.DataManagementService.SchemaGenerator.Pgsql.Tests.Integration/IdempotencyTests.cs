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
public class Given_Ddl_Executed_Twice
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;
    private Exception? _secondExecutionException;
    private int _constraintCountAfterSecondExecution;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _databaseName = $"edfi_schemagen_test_{Guid.NewGuid():N}";
        await DatabaseHelper.CreateDatabaseAsync(_databaseName);
        _connectionString = DatabaseHelper.GetTestDatabaseConnectionString(_databaseName);

        await DatabaseHelper.ExecuteSqlAsync(_connectionString, DatabaseHelper.GetDocumentTableStubSql());

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

        // Execute DDL the first time
        await DatabaseHelper.ExecuteSqlAsync(_connectionString, ddl);

        // Execute DDL a second time — should be idempotent
        try
        {
            await DatabaseHelper.ExecuteSqlAsync(_connectionString, ddl);
        }
        catch (Exception ex)
        {
            _secondExecutionException = ex;
        }

        // Count constraints after second execution
        var constraints = await DatabaseHelper.GetConstraintsAsync(_connectionString, "dms", "edfi_student");
        _constraintCountAfterSecondExecution = constraints.Count;
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
    public void It_should_succeed_without_errors()
    {
        _secondExecutionException.Should().BeNull("DDL should be idempotent and not throw on re-execution");
    }

    [Test]
    public async Task It_should_not_duplicate_constraints()
    {
        // Execute DDL a third time to confirm no duplicates accumulate
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

        var constraints = await DatabaseHelper.GetConstraintsAsync(_connectionString, "dms", "edfi_student");
        constraints.Count.Should().Be(_constraintCountAfterSecondExecution);
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
                Description = "Idempotency test schema.",
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
