// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Cli.Options;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
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
            const string InputPath = "/path/to/input/schema.json";

            // Act
            options.Input = InputPath;

            // Assert
            options.Input.Should().Be(InputPath);
        }

        [Test]
        public void CliOptions_SetOutput_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();
            const string OutputPath = "/path/to/output/directory";

            // Act
            options.Output = OutputPath;

            // Assert
            options.Output.Should().Be(OutputPath);
        }

        [Test]
        public void CliOptions_SetDb_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions();
            const string DbProvider = "pgsql";

            // Act
            options.Db = DbProvider;

            // Assert
            options.Db.Should().Be(DbProvider);
        }

        [Test]
        public void CliOptions_SetExtensions_ShouldReturnCorrectValue()
        {
            // Arrange
            var options = new CliOptions
            {
                // Act
                Extensions = true
            };

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
