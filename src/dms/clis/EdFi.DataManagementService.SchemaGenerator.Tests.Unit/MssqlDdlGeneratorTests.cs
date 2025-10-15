// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Mssql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Unit tests for SQL Server DDL generation.
    /// </summary>
    [TestFixture]
    public class MssqlDdlGeneratorTests
    {
        [Test]
        public void GeneratesIdempotentCreateTable()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TestTable')");
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().Contain("[Document_Id] BIGINT NOT NULL");
            sql.Should().Contain("[Document_PartitionKey] TINYINT NOT NULL");
            sql.Should().Contain("[Name] NVARCHAR(100) NOT NULL");
            sql.Should().Contain("[IsActive] BIT");
            sql.Should().Contain("CONSTRAINT [FK_TestTable_Document]");
            sql.Should().Contain("REFERENCES [Document]([(Id, DocumentPartitionKey)]) ON DELETE CASCADE");
            sql.Should().Contain("CONSTRAINT [UQ_TestTable_NaturalKey]");
        }

        [Test]
        public void GeneratesPrimaryKeyConstraint()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().NotContain("CONSTRAINT [PK_TestTable]");
        }

        [Test]
        public void GeneratesNotNullConstraintsForRequiredColumns()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[Id] BIGINT PRIMARY KEY IDENTITY(1,1)");
            sql.Should().Contain("[Document_Id] BIGINT NOT NULL");
            sql.Should().Contain("[Document_PartitionKey] TINYINT NOT NULL");
            sql.Should().Contain("[Name] NVARCHAR(100) NOT NULL");
            sql.Should().Contain("[IsActive] BIT");
            sql.Should().NotContain("[IsActive] BIT NOT NULL");
        }

        [Test]
        public void GeneratesUnionViewForPolymorphicReferenceByDefault()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE VIEW [EducationOrganizationReference] AS");
            sql.Should().Contain("SELECT [EducationOrganizationId], 'School' AS [Discriminator] FROM [School]");
            sql.Should().Contain("UNION ALL");
            sql.Should().Contain("SELECT [EducationOrganizationId], 'LocalEducationAgency' AS [Discriminator] FROM [LocalEducationAgency]");
        }

        [Test]
        public void SkipsUnionViewWhenFlagIsSet()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert
            sql.Should().NotContain("CREATE OR ALTER VIEW");
            sql.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GeneratesChildTables()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[School]");
            sql.Should().Contain("[LocalEducationAgency]");
            sql.Should().Contain("[EducationOrganizationReference_Id] BIGINT NOT NULL");
            sql.Should().Contain("CONSTRAINT [FK_School_EducationOrganizationReference]");
            sql.Should().Contain("REFERENCES [EducationOrganizationReference]([Id]) ON DELETE CASCADE");
        }

        [Test]
        public void UnionViewIncludesOnlyNaturalKeyColumns()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("SELECT [EducationOrganizationId], 'School' AS [Discriminator] FROM [School]");
            sql.Should().NotContain("SELECT [EducationOrganizationId], [SchoolName]"); // Should not include non-key columns
        }

        [Test]
        public void UsesBracketedIdentifiersForTableAndColumnNames()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new MssqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("[TestTable]");
            sql.Should().Contain("[Id]");
            sql.Should().Contain("[Name]");
            sql.Should().Contain("[IsActive]");
        }
    }
}
