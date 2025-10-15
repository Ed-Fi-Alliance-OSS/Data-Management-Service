// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Unit tests for PostgreSQL DDL generation.
    /// </summary>
    [TestFixture]
    public class PgsqlDdlGeneratorTests
    {
        [Test]
        public void GeneratesIdempotentCreateTable()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS TestTable");
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().Contain("Document_Id BIGINT NOT NULL");
            sql.Should().Contain("Document_PartitionKey SMALLINT NOT NULL");
            sql.Should().Contain("Name VARCHAR(100) NOT NULL");
            sql.Should().Contain("IsActive BOOLEAN");
            sql.Should().Contain("CONSTRAINT FK_TestTable_Document");
            sql.Should().Contain("REFERENCES Document((Id, DocumentPartitionKey)) ON DELETE CASCADE");
            sql.Should().Contain("CONSTRAINT UQ_TestTable_NaturalKey");
        }

        [Test]
        public void GeneratesPrimaryKeyConstraint()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().NotContain("CONSTRAINT PK_");
        }

        [Test]
        public void GeneratesNotNullConstraintsForRequiredColumns()
        {
            // Arrange
            var schema = TestHelpers.GetBasicSchema();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("Id BIGSERIAL PRIMARY KEY");
            sql.Should().Contain("Document_Id BIGINT NOT NULL");
            sql.Should().Contain("Document_PartitionKey SMALLINT NOT NULL");
            sql.Should().Contain("Name VARCHAR(100) NOT NULL");
            sql.Should().Contain("IsActive BOOLEAN");
            sql.Should().NotContain("IsActive BOOLEAN NOT NULL");
        }

        [Test]
        public void GeneratesUnionViewForPolymorphicReferenceByDefault()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE OR REPLACE VIEW EducationOrganizationReference AS");
            // Union views use unquoted identifiers for consistency with table DDL
            sql.Should().Contain("SELECT EducationOrganizationId, 'School' AS Discriminator FROM School");
            sql.Should().Contain("UNION ALL");
            sql.Should().Contain("SELECT EducationOrganizationId, 'LocalEducationAgency' AS Discriminator FROM LocalEducationAgency");
        }

        [Test]
        public void SkipsUnionViewWhenFlagIsSet()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false, skipUnionViews: true);

            // Assert
            sql.Should().NotContain("CREATE OR REPLACE VIEW");
            sql.Should().NotContain("UNION ALL");
        }

        [Test]
        public void GeneratesChildTables()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS School");
            sql.Should().Contain("CREATE TABLE IF NOT EXISTS LocalEducationAgency");
            sql.Should().Contain("EducationOrganizationReference_Id BIGINT NOT NULL");
            sql.Should().Contain("CONSTRAINT FK_School_EducationOrganizationReference");
            sql.Should().Contain("REFERENCES EducationOrganizationReference(Id) ON DELETE CASCADE");
        }

        [Test]
        public void UnionViewIncludesOnlyNaturalKeyColumns()
        {
            // Arrange
            var schema = TestHelpers.GetSchemaWithPolymorphicReference();
            var generator = new PgsqlDdlGeneratorStrategy();

            // Act
            var sql = generator.GenerateDdlString(schema, includeExtensions: false);

            // Assert
            // Union views use unquoted identifiers for consistency with table DDL
            sql.Should().Contain("SELECT EducationOrganizationId, 'School' AS Discriminator FROM School");
            sql.Should().NotContain("SELECT EducationOrganizationId, SchoolName"); // Should not include non-key columns
        }
    }
}
