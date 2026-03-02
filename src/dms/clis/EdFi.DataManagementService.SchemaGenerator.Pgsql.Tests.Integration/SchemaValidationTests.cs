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
public class Given_Schema_With_Child_Tables
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
        var schema = CreateSchemaWithChildTable();
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
    public async Task It_should_create_parent_table()
    {
        var tables = await DatabaseHelper.GetTablesInSchemaAsync(_connectionString, "dms");
        tables.Should().Contain("edfi_student");
    }

    [Test]
    public async Task It_should_create_child_table()
    {
        var tables = await DatabaseHelper.GetTablesInSchemaAsync(_connectionString, "dms");
        tables.Should().Contain("edfi_studentaddress");
    }

    [Test]
    public async Task It_should_create_foreign_key_from_child_to_parent()
    {
        var constraints = await DatabaseHelper.GetConstraintsAsync(
            _connectionString,
            "dms",
            "edfi_studentaddress"
        );

        // Child table should have FK constraints (to Document and possibly to parent)
        constraints.Where(c => c.Type == "f").Should().NotBeEmpty();
    }

    private static ApiSchema CreateSchemaWithChildTable()
    {
        return new ApiSchema
        {
            ProjectSchema = new ProjectSchema
            {
                ProjectName = "EdFi",
                ProjectVersion = "1.0.0",
                IsExtensionProject = false,
                Description = "Schema with child table.",
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
                                ChildTables =
                                [
                                    new TableMetadata
                                    {
                                        BaseName = "StudentAddress",
                                        JsonPath = "$.Student.StudentAddress",
                                        Columns =
                                        [
                                            new ColumnMetadata
                                            {
                                                ColumnName = "AddressTypeDescriptorId",
                                                ColumnType = "int32",
                                                IsNaturalKey = true,
                                                IsRequired = true,
                                            },
                                            new ColumnMetadata
                                            {
                                                ColumnName = "StreetNumberName",
                                                ColumnType = "string",
                                                MaxLength = "150",
                                                IsRequired = true,
                                            },
                                        ],
                                        ChildTables = [],
                                    },
                                ],
                            },
                        },
                    },
                },
            },
        };
    }
}

[TestFixture]
public class Given_Schema_With_All_Column_Types
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
        var schema = CreateSchemaWithAllColumnTypes();
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
    public async Task It_should_create_columns_with_correct_types()
    {
        var columns = await DatabaseHelper.GetColumnsAsync(_connectionString, "dms", "edfi_testtable");
        var columnMap = columns.ToDictionary(c => c.Name, c => c.DataType);

        // string -> character varying
        columnMap.Should().ContainKey("stringcol");
        columnMap["stringcol"].Should().Be("character varying");

        // int32 -> integer
        columnMap.Should().ContainKey("int32col");
        columnMap["int32col"].Should().Be("integer");

        // bigint -> bigint
        columnMap.Should().ContainKey("bigintcol");
        columnMap["bigintcol"].Should().Be("bigint");

        // bool -> boolean
        columnMap.Should().ContainKey("boolcol");
        columnMap["boolcol"].Should().Be("boolean");

        // decimal -> numeric
        columnMap.Should().ContainKey("decimalcol");
        columnMap["decimalcol"].Should().Be("numeric");

        // date -> date
        columnMap.Should().ContainKey("datecol");
        columnMap["datecol"].Should().Be("date");

        // datetime -> timestamp with time zone
        columnMap.Should().ContainKey("datetimecol");
        columnMap["datetimecol"].Should().Be("timestamp with time zone");

        // short -> smallint
        columnMap.Should().ContainKey("shortcol");
        columnMap["shortcol"].Should().Be("smallint");

        // time -> time without time zone
        columnMap.Should().ContainKey("timecol");
        columnMap["timecol"].Should().Be("time without time zone");

        // year -> smallint
        columnMap.Should().ContainKey("yearcol");
        columnMap["yearcol"].Should().Be("smallint");

        // duration -> character varying
        columnMap.Should().ContainKey("durationcol");
        columnMap["durationcol"].Should().Be("character varying");
    }

    private static ApiSchema CreateSchemaWithAllColumnTypes()
    {
        return new ApiSchema
        {
            ProjectSchema = new ProjectSchema
            {
                ProjectName = "EdFi",
                ProjectVersion = "1.0.0",
                IsExtensionProject = false,
                Description = "Schema with all column types.",
                ResourceSchemas = new Dictionary<string, ResourceSchema>
                {
                    ["testtable"] = new ResourceSchema
                    {
                        ResourceName = "TestTable",
                        FlatteningMetadata = new FlatteningMetadata
                        {
                            Table = new TableMetadata
                            {
                                BaseName = "TestTable",
                                JsonPath = "$.TestTable",
                                Columns =
                                [
                                    new ColumnMetadata
                                    {
                                        ColumnName = "TestId",
                                        ColumnType = "bigint",
                                        IsNaturalKey = true,
                                        IsRequired = true,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "StringCol",
                                        ColumnType = "string",
                                        MaxLength = "100",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "Int32Col",
                                        ColumnType = "int32",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "BigintCol",
                                        ColumnType = "bigint",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "BoolCol",
                                        ColumnType = "bool",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "DecimalCol",
                                        ColumnType = "decimal",
                                        Precision = "18",
                                        Scale = "4",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "DateCol",
                                        ColumnType = "date",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "DateTimeCol",
                                        ColumnType = "datetime",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "ShortCol",
                                        ColumnType = "short",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "TimeCol",
                                        ColumnType = "time",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "YearCol",
                                        ColumnType = "year",
                                        IsRequired = false,
                                    },
                                    new ColumnMetadata
                                    {
                                        ColumnName = "DurationCol",
                                        ColumnType = "duration",
                                        IsRequired = false,
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
