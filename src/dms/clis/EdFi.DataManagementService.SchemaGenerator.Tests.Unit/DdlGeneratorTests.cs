// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    public class DdlGeneratorTests
    {
        private ApiSchema GetSampleSchema(bool withAbstractResource = false)
        {
            var schema = new ApiSchema
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
                                    Columns =
                                    [
                                        new ColumnMetadata { ColumnName = "Id", ColumnType = "bigint", IsNaturalKey = true, IsRequired = true },
                                        new ColumnMetadata { ColumnName = "Name", ColumnType = "string", MaxLength = "100", IsRequired = true },
                                        new ColumnMetadata { ColumnName = "IsActive", ColumnType = "bool", IsRequired = false }
                                    ],
                                    ChildTables = []
                                }
                            }
                        }
                    }
                }
            };
            if (withAbstractResource)
            {
                // Add a fake abstractResources property to ProjectSchema via reflection (simulate JSON)
                var projectSchemaType = typeof(ProjectSchema);
                var dict = new Dictionary<string, object>
                {
                    ["TestAbstract"] = new
                    {
                        flatteningMetadata = new
                        {
                            subclassTypes = new[] { "TestTable" },
                            unionViewName = "TestAbstractView"
                        }
                    }
                };
                var backingField = projectSchemaType.GetField("<AdditionalData>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (backingField != null)
                {
                    backingField.SetValue(schema.ProjectSchema, dict);
                }
                else
                {
                    // Use System.Text.Json to add the property dynamically if possible
                    // (In real code, this would be handled by the JSON model, but for test, we simulate)
                }
            }
            return schema;
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
            Assert.That(sql, Does.Contain("CREATE TABLE \"TestTable\""));
            Assert.That(sql, Does.Contain("Id BIGINT NOT NULL"));
            Assert.That(sql, Does.Contain("Name VARCHAR(100) NOT NULL"));
            Assert.That(sql, Does.Contain("IsActive BOOLEAN"));
            Assert.That(sql, Does.Contain("PRIMARY KEY (\"Id\")"));
        }

        [Test]
        public void Pgsql_Generates_UnionView_ByDefault()
        {
            var schema = GetSampleSchema(withAbstractResource: true);
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new PgsqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-pgsql.sql"));
            Assert.That(sql, Does.Contain("CREATE OR REPLACE VIEW TestAbstractView AS"));
        }

        [Test]
        public void Pgsql_Skips_UnionView_When_Requested()
        {
            var schema = GetSampleSchema(withAbstractResource: true);
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new PgsqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false, skipUnionViews: true);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-pgsql.sql"));
            Assert.That(sql, Does.Not.Contain("CREATE OR REPLACE VIEW TestAbstractView AS"));
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
            Assert.That(sql, Does.Contain("CREATE TABLE [TestTable]"));
            Assert.That(sql, Does.Contain("Id BIGINT NOT NULL"));
            Assert.That(sql, Does.Contain("Name NVARCHAR(100) NOT NULL"));
            Assert.That(sql, Does.Contain("IsActive BIT"));
            Assert.That(sql, Does.Contain("PRIMARY KEY ([Id])"));
        }

        [Test]
        public void Mssql_Generates_UnionView_ByDefault()
        {
            var schema = GetSampleSchema(withAbstractResource: true);
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new MssqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-mssql.sql"));
            Assert.That(sql, Does.Contain("CREATE VIEW TestAbstractView AS"));
        }

        [Test]
        public void Mssql_Skips_UnionView_When_Requested()
        {
            var schema = GetSampleSchema(withAbstractResource: true);
            var outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(outputDir);
            var generator = new MssqlDdlGeneratorStrategy();
            generator.GenerateDdl(schema, outputDir, false, skipUnionViews: true);
            var sql = File.ReadAllText(Path.Combine(outputDir, "schema-mssql.sql"));
            Assert.That(sql, Does.Not.Contain("CREATE VIEW TestAbstractView AS"));
        }
    }
}
