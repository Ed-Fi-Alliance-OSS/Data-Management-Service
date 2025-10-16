// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Cli.Options;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit
{
    /// <summary>
    /// Unit tests for CliOptions class.
    /// </summary>
    [TestFixture]
    public class CliOptionsTests
    {
        [Test]
        public void CliOptions_DefaultValues_ShouldBeCorrect()
        {
            // Act
            var options = new CliOptions();

            // Assert
            options.Input.Should().Be(string.Empty);
            options.Output.Should().Be(string.Empty);
            options.Db.Should().Be("all");
            options.Extensions.Should().BeFalse();
        }

        [Test]
        public void CliOptions_SetInput_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();
            const string inputPath = "/path/to/input/schema.json";

            // Act
            options.Input = inputPath;

            // Assert
            options.Input.Should().Be(inputPath);
        }

        [Test]
        public void CliOptions_SetOutput_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();
            const string outputPath = "/path/to/output/directory";

            // Act
            options.Output = outputPath;

            // Assert
            options.Output.Should().Be(outputPath);
        }

        [Test]
        public void CliOptions_SetDb_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();
            const string dbProvider = "pgsql";

            // Act
            options.Db = dbProvider;

            // Assert
            options.Db.Should().Be(dbProvider);
        }

        [Test]
        public void CliOptions_SetExtensions_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();

            // Act
            options.Extensions = true;

            // Assert
            options.Extensions.Should().BeTrue();
        }

        [Test]
        public void CliOptions_SetAllProperties_ShouldReturnCorrectValues()
        {
            // Arrange
            var options = new CliOptions
            {
                Input = "/input/schema.json",
                Output = "/output/ddl",
                Db = "mssql",
                Extensions = true
            };

            // Act & Assert
            options.Input.Should().Be("/input/schema.json");
            options.Output.Should().Be("/output/ddl");
            options.Db.Should().Be("mssql");
            options.Extensions.Should().BeTrue();
        }

        [Test]
        public void CliOptions_SetEmptyStrings_ShouldHandleCorrectly()
        {
            // Arrange
            var options = new CliOptions
            {
                Input = "",
                Output = "",
                Db = ""
            };

            // Act & Assert
            options.Input.Should().Be("");
            options.Output.Should().Be("");
            options.Db.Should().Be("");
        }

        [Test]
        public void CliOptions_SetNullStrings_ShouldHandleCorrectly()
        {
            // Arrange
            var options = new CliOptions
            {
                Input = null!,
                Output = null!,
                Db = null!
            };

            // Act & Assert
            options.Input.Should().BeNull();
            options.Output.Should().BeNull();
            options.Db.Should().BeNull();
        }
    }
}
