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
public class Given_Ddl_With_Fk_Constraints_Disabled
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _databaseName = $"edfi_schemagen_test_{Guid.NewGuid():N}";
        await DatabaseHelper.CreateDatabaseAsync(_databaseName);
        _connectionString = DatabaseHelper.GetTestDatabaseConnectionString(_databaseName);

        // Deliberately NOT creating the Document stub table — FKs are disabled
        var logger = NullLoggerFactory.Instance.CreateLogger<PgsqlDdlGeneratorStrategy>();
        var strategy = new PgsqlDdlGeneratorStrategy(logger);
        var schema = CreateBasicApiSchema();
        var options = new DdlGenerationOptions
        {
            GenerateForeignKeyConstraints = false,
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
    public async Task It_should_execute_without_document_table()
    {
        // The DDL executed successfully without the Document table
        var tables = await DatabaseHelper.GetTablesInSchemaAsync(_connectionString, "dms");
        tables.Should().Contain("edfi_student");
    }

    [Test]
    public async Task It_should_not_create_fk_constraints()
    {
        var constraints = await DatabaseHelper.GetConstraintsAsync(_connectionString, "dms", "edfi_student");
        constraints.Where(c => c.Type == "f").Should().BeEmpty();
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
                Description = "FK disabled test schema.",
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

[TestFixture]
public class Given_Schema_With_Audit_Columns_Enabled
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;

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
            IncludeAuditColumns = true,
            GenerateForeignKeyConstraints = false,
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
    public async Task It_should_include_audit_columns()
    {
        var columns = await DatabaseHelper.GetColumnsAsync(_connectionString, "dms", "edfi_student");
        var columnNames = columns.Select(c => c.Name).ToList();

        columnNames.Should().Contain("createdate");
        columnNames.Should().Contain("lastmodifieddate");
        columnNames.Should().Contain("changeversion");
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
                Description = "Audit columns test schema.",
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

[TestFixture]
public class Given_Schema_With_Audit_Columns_Disabled
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;

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
            IncludeAuditColumns = false,
            GenerateForeignKeyConstraints = false,
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
    public async Task It_should_exclude_audit_columns()
    {
        var columns = await DatabaseHelper.GetColumnsAsync(_connectionString, "dms", "edfi_student");
        var columnNames = columns.Select(c => c.Name).ToList();

        columnNames.Should().NotContain("createdate");
        columnNames.Should().NotContain("lastmodifieddate");
        columnNames.Should().NotContain("changeversion");
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
                Description = "No audit columns test schema.",
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

[TestFixture]
public class Given_Schema_With_Prefixed_Table_Names_Disabled
{
    private string _databaseName = string.Empty;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _databaseName = $"edfi_schemagen_test_{Guid.NewGuid():N}";
        await DatabaseHelper.CreateDatabaseAsync(_databaseName);
        _connectionString = DatabaseHelper.GetTestDatabaseConnectionString(_databaseName);

        // Create Document stub in the edfi schema (since prefixed table names are disabled,
        // the FK will reference edfi.Document instead of dms.Document)
        await DatabaseHelper.ExecuteSqlAsync(
            _connectionString,
            """
            CREATE SCHEMA IF NOT EXISTS edfi;
            CREATE TABLE IF NOT EXISTS edfi.Document (
                Id BIGINT NOT NULL,
                DocumentPartitionKey SMALLINT NOT NULL,
                PRIMARY KEY (Id, DocumentPartitionKey)
            );
            """
        );

        var logger = NullLoggerFactory.Instance.CreateLogger<PgsqlDdlGeneratorStrategy>();
        var strategy = new PgsqlDdlGeneratorStrategy(logger);
        var schema = CreateBasicApiSchema();
        var options = new DdlGenerationOptions
        {
            UsePrefixedTableNames = false,
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
    public async Task It_should_create_separate_database_schemas()
    {
        // With UsePrefixedTableNames=false, the table should be in the edfi schema
        // as edfi.student instead of dms.edfi_student
        var schemas = await DatabaseHelper.GetSchemasAsync(_connectionString);
        schemas.Should().Contain("edfi");

        var tables = await DatabaseHelper.GetTablesInSchemaAsync(_connectionString, "edfi");
        tables.Should().Contain("student");
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
                Description = "Non-prefixed table names test schema.",
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
