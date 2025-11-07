// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Cli;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Simplified unit tests for Program class that focus on what actually works.
    /// </summary>
    [TestFixture]
    public class ProgramBasicTests
    {
        private string _tempDirectory = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                try
                {
                    Directory.Delete(_tempDirectory, true);
                }
                catch (IOException)
                {
                    // Ignore file access issues during cleanup
                }
            }
        }

        [Test]
        [TestCase("/h")]
        [TestCase("--help")]
        [TestCase("-h")]
        [TestCase("/?")]
        public async Task Main_WithHelpFlag_ShouldDisplayHelpAndReturnZero(string helpFlag)
        {
            // Arrange
            var args = new[] { helpFlag };
            var originalOut = Console.Out;
            var stringWriter = new StringWriter();

            try
            {
                Console.SetOut(stringWriter);

                // Act
                var result = await Program.Main(args);

                // Assert
                result.Should().Be(0);
                var output = stringWriter.ToString();
                output.Should().Contain("Ed-Fi Data Management Service - Schema Generator CLI");
                output.Should().Contain("Usage:");
                output.Should().Contain("--input");
                output.Should().Contain("--output");
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Test]
        public async Task Main_WithMissingRequiredParameters_ShouldReturnOne()
        {
            // Arrange
            var args = Array.Empty<string>();

            // Act
            var result = await Program.Main(args);

            // Assert - Should return error code 1 (logged via Serilog, not Console.Error)
            result.Should().Be(1);
        }

        [Test]
        public async Task Main_WithInvalidInputFile_ShouldReturnTwo()
        {
            // Arrange
            var args = new[] { "--input", "non-existent-file.json", "--output", _tempDirectory };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(2);
        }

        [Test]
        public async Task Main_WithValidParametersAllDatabases_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithValidParametersPostgreSQLOnly_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--provider", "pgsql" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithValidParametersSqlServerOnly_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "-i", inputFile, "-o", outputDir, "-p", "mssql" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithExtensionsFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--extensions" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithSkipUnionViewsFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--skip-union-views" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithAllShortFlags_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "-i", inputFile, "-o", outputDir, "-p", "all", "-e", "-s" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithDatabaseFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--database", "pgsql" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithIncompleteArguments_ShouldIgnoreIncompleteArguments()
        {
            // Arrange
            var args = new[]
            {
                "--input", // Missing value for input
            };

            // Act
            var result = await Program.Main(args);

            // Assert - Should return error code 1 (logged via Serilog, not Console.Error)
            result.Should().Be(1);
        }

        [Test]
        public async Task Main_WithWhitespaceOnlyInputPath_ShouldReturnOne()
        {
            // Arrange
            var args = new[] { "--input", "   ", "--output", _tempDirectory };

            // Act
            var result = await Program.Main(args);

            // Assert - Should return error code 1 (logged via Serilog, not Console.Error)
            result.Should().Be(1);
        }

        [Test]
        public async Task Main_WithWhitespaceOnlyOutputPath_ShouldReturnOne()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var args = new[] { "--input", inputFile, "--output", "   " };

            // Act
            var result = await Program.Main(args);

            // Assert - Should return error code 1 (logged via Serilog, not Console.Error)
            result.Should().Be(1);
        }

        [Test]
        public async Task Main_WithInvalidJson_ShouldReturnTwo()
        {
            // Arrange
            var inputFile = Path.Combine(_tempDirectory, "invalid-schema.json");
            File.WriteAllText(inputFile, "{ invalid json }");

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(2);
        }

        private static ApiSchema CreateValidApiSchema()
        {
            return new ApiSchema
            {
                ApiSchemaVersion = "1.0.0",
                ProjectSchema = new ProjectSchema
                {
                    ProjectName = "TestProject",
                    ProjectVersion = "1.0.0",
                    IsExtensionProject = false,
                    Description = "Test project for unit tests",
                    ResourceSchemas = new Dictionary<string, ResourceSchema>
                    {
                        ["TestResource"] = new ResourceSchema
                        {
                            ResourceName = "TestResource",
                            FlatteningMetadata = new FlatteningMetadata
                            {
                                Table = new TableMetadata
                                {
                                    BaseName = "TestTable",
                                    JsonPath = "$.test",
                                    Columns =
                                    [
                                        new ColumnMetadata
                                        {
                                            JsonPath = "$.id",
                                            ColumnName = "Id",
                                            ColumnType = "int",
                                            IsRequired = true,
                                            IsNaturalKey = true,
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

        [Test]
        public async Task Main_WithBothInputAndUrl_ShouldReturnOne()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[]
            {
                "--input",
                inputFile,
                "--url",
                "https://example.com/schema.json",
                "--output",
                outputDir,
            };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(1); // Should return error code 1 (logged via Serilog, not Console.Error)
        }

        [Test]
        public async Task Main_WithUrlOnly_ShouldReturnOne()
        {
            // Arrange
            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[]
            {
                "--url",
                "https://example.com/invalid-url-that-does-not-exist-for-testing.json",
                "--output",
                outputDir,
            };

            // Act
            var result = await Program.Main(args);

            // Assert
            // Should fail to fetch the URL (error code 2)
            result.Should().Be(2);
        }

        [Test]
        public async Task Main_WithValidParametersPgsqlAlias_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--database", "postgresql" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithSeparateSchemasFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--use-schemas" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithPrefixedTablesFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--use-prefixed-names" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithAllProviderValue_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--provider", "all" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithExtensionsAndSkipUnionViews_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[]
            {
                "--input",
                inputFile,
                "--output",
                outputDir,
                "--extensions",
                "--skip-union-views",
            };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
        }

        [Test]
        public async Task Main_WithInferFksFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[] { "--input", inputFile, "--output", outputDir, "--infer-fks" };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
            // Optionally check output files exist
            Directory.GetFiles(outputDir).Should().NotBeEmpty();
        }

        [Test]
        public async Task Main_WithSeparateInferredFksFlag_ShouldReturnZero()
        {
            // Arrange
            var apiSchema = CreateValidApiSchema();
            var inputFile = Path.Combine(_tempDirectory, "test-schema.json");
            File.WriteAllText(
                inputFile,
                JsonSerializer.Serialize(
                    apiSchema,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                )
            );

            var outputDir = Path.Combine(_tempDirectory, "output");
            Directory.CreateDirectory(outputDir);

            var args = new[]
            {
                "--input",
                inputFile,
                "--output",
                outputDir,
                "--infer-fks",
                "--separate-inferred-fks",
            };

            // Act
            var result = await Program.Main(args);

            // Assert
            result.Should().Be(0);
            // Optionally check output files exist and are split
            var files = Directory.GetFiles(outputDir);
            files.Should().Contain(f => f.Contains("01_"));
            files.Should().Contain(f => f.Contains("02_"));
        }
    }
}
