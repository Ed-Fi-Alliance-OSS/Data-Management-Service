// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;

namespace EdFi.DataManagementService.SchemaGenerator.Tests
{
    public class DdlGeneratorTests
    {
        private ApiSchema GetSampleSchema()
        {
            return new ApiSchema
            {
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test schema for DDL generation.",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestTable"] = new ResourceSchema
                        {
                            ResourceName = "TestTable",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.TestTable",
                                    Columns = new List<ColumnMetadata>
                                    {
                                        new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "Name", ColumnType = "string", MaxLength = "100", IsRequired = true },
                                        new ColumnMetadata { ColumnName = "IsActive", ColumnType = "bool", IsRequired = false }
                                    },
                                    ChildTables = new List<TableMetadata>()
                                }
                            }
                        }
                    }
                }
            };
        }

        [Test]
        public void Pgsql_Generates_Idempotent_CreateTable()
        {
            var schema = GetSampleSchema();
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new PgsqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-pgsql.sql"));
            Assert.That(sql, Does.Contain("DO $$"));
            Assert.That(sql, Does.Contain("CREATE TABLE \"TestTable\""));
            Assert.That(sql, Does.Contain("Id BIGSERIAL"));
            Assert.That(sql, Does.Contain("Name TEXT"));
            Assert.That(sql, Does.Contain("IsActive BOOLEAN"));
        }

        [Test]
        public void Mssql_Generates_Idempotent_CreateTable()
        {
            var schema = GetSampleSchema();
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new MssqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-mssql.sql"));
            Assert.That(sql, Does.Contain("IF NOT EXISTS"));
            Assert.That(sql, Does.Contain("CREATE TABLE [TestTable]"));
            Assert.That(sql, Does.Contain("Id BIGINT IDENTITY(1,1)"));
            Assert.That(sql, Does.Contain("Name NVARCHAR(MAX)"));
            Assert.That(sql, Does.Contain("IsActive BIT"));
        }
    }
}
