// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Mssql;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Engines.Mssql
{
    /// <summary>
    /// Unit tests for SQL Server naming helper functionality.
    /// </summary>
    [TestFixture]
    public class MssqlNamingHelperTests
    {
        [Test]
        public void MakeMssqlIdentifier_WithShortName_ReturnsOriginalName()
        {
            // Arrange
            var name = "TestTable";

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(name);

            // Assert
            result.Should().Be("TestTable");
        }

        [Test]
        public void MakeMssqlIdentifier_WithDots_ReplacesDots()
        {
            // Arrange
            var name = "ContentStandard.MandatingEducationOrganization";

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(name);

            // Assert
            result.Should().Be("ContentStandard_MandatingEducationOrganization");
            result.Should().NotContain(".");
        }

        [Test]
        public void MakeMssqlIdentifier_WithHyphens_ReplacesHyphens()
        {
            // Arrange
            var name = "Table-With-Hyphens";

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(name);

            // Assert
            result.Should().Be("Table_With_Hyphens");
            result.Should().NotContain("-");
        }

        [Test]
        public void MakeMssqlIdentifier_WithVeryLongName_TruncatesAndAddsHash()
        {
            // Arrange - Create a name longer than 128 characters
            var longName = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganizationReference_VeryLong";

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(longName);

            // Assert
            result.Length.Should().BeLessOrEqualTo(128);
            result.Should().Contain("_"); // Contains hash separator
            result.Length.Should().Be(128); // Should be exactly at the limit
        }

        [Test]
        public void MakeMssqlIdentifier_WithCustomMaxLength_RespectsMaxLength()
        {
            // Arrange
            var name = "VeryLongTableNameForTesting";
            var maxLength = 20;

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(name, maxLength);

            // Assert
            result.Length.Should().BeLessOrEqualTo(maxLength);
        }

        [Test]
        public void MakeMssqlIdentifier_WithCustomHashLength_UsesCorrectHashLength()
        {
            // Arrange
            var longName = "VeryLongTableNameThatWillExceedTheMaximumLengthLimit";
            var hashLength = 12;

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(longName, 30, hashLength);

            // Assert
            result.Length.Should().Be(30);
            var hashPart = result.Substring(result.LastIndexOf('_') + 1);
            hashPart.Length.Should().Be(hashLength);
        }

        [Test]
        public void MakeMssqlIdentifier_WithIdenticalLongNames_GeneratesSameHash()
        {
            // Arrange
            var longName1 = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganizationReference";
            var longName2 = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganizationReference";

            // Act
            var result1 = MssqlNamingHelper.MakeMssqlIdentifier(longName1);
            var result2 = MssqlNamingHelper.MakeMssqlIdentifier(longName2);

            // Assert
            result1.Should().Be(result2);
        }

        [Test]
        public void MakeMssqlIdentifier_WithDifferentLongNames_GeneratesDifferentHashes()
        {
            // Arrange
            var longName1 = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganization1";
            var longName2 = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganization2";

            // Act
            var result1 = MssqlNamingHelper.MakeMssqlIdentifier(longName1);
            var result2 = MssqlNamingHelper.MakeMssqlIdentifier(longName2);

            // Assert
            result1.Should().NotBe(result2);
        }

        [Test]
        public void SanitizeIdentifier_WithShortName_ReturnsOriginalName()
        {
            // Arrange
            var name = "TestTable";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(name);

            // Assert
            result.Should().Be("TestTable");
        }

        [Test]
        public void SanitizeIdentifier_WithDots_ReplacesDots()
        {
            // Arrange
            var name = "ContentStandard.MandatingEducationOrganization";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(name);

            // Assert
            result.Should().Be("ContentStandard_MandatingEducationOrganization");
            result.Should().NotContain(".");
        }

        [Test]
        public void SanitizeIdentifier_WithHyphens_ReplacesHyphens()
        {
            // Arrange
            var name = "Table-With-Hyphens";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(name);

            // Assert
            result.Should().Be("Table_With_Hyphens");
            result.Should().NotContain("-");
        }

        [Test]
        public void SanitizeIdentifier_WithBothDotsAndHyphens_ReplacesBoth()
        {
            // Arrange
            var name = "Content.Standard-Reference";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(name);

            // Assert
            result.Should().Be("Content_Standard_Reference");
            result.Should().NotContain(".");
            result.Should().NotContain("-");
        }

        [Test]
        public void SanitizeIdentifier_WithVeryLongName_DoesNotTruncate()
        {
            // Arrange - Create a name longer than 128 characters
            var longName = "StudentEducationOrganizationAssociationStudentCharacteristicPeriodStudentCharacteristicDescriptorReferenceEducationOrganizationReference_VeryLong";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(longName);

            // Assert
            result.Length.Should().Be(longName.Length); // Should not truncate
            result.Should().Be(longName); // Should be unchanged (no special chars)
        }

        [Test]
        public void MakeMssqlIdentifier_WithEmptyString_HandlesGracefully()
        {
            // Arrange
            var name = "";

            // Act
            var result = MssqlNamingHelper.MakeMssqlIdentifier(name);

            // Assert
            result.Should().NotBeNull();
        }

        [Test]
        public void SanitizeIdentifier_WithEmptyString_ReturnsEmptyString()
        {
            // Arrange
            var name = "";

            // Act
            var result = MssqlNamingHelper.SanitizeIdentifier(name);

            // Assert
            result.Should().Be("");
        }
    }
}