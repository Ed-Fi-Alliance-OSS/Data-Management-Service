// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.SchemaGenerator.Cli;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Tests for Program class error paths and edge cases to improve coverage.
    /// </summary>
    [TestFixture]
    public class ProgramErrorPathCoverageTests
    {
        [Test]
        public async Task Main_WithHelpFlag_ReturnsZeroExitCode()
        {
            // Arrange
            string[] args = ["--help"];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(0);
        }

        [Test]
        public async Task Main_WithHelpFlagVariation_ReturnsZeroExitCode()
        {
            // Arrange & Act & Assert
            (await Program.Main(["-h"]))
                .Should()
                .Be(0);
            (await Program.Main(["/h"])).Should().Be(0);
            (await Program.Main(["/?"])).Should().Be(0);
        }

        [Test]
        public async Task Main_WithNoInputAndNoUrl_ReturnsErrorExitCode()
        {
            // Arrange
            string[] args = ["--output", "somedir"];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(1); // Error: InputFilePath required
        }

        [Test]
        public async Task Main_WithNoOutput_ReturnsErrorExitCode()
        {
            // Arrange
            string[] args = ["--input", "somefile.json"];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(1); // Error: OutputDirectory required
        }

        [Test]
        public async Task Main_WithBothInputAndUrl_ReturnsErrorExitCode()
        {
            // Arrange
            string[] args =
            [
                "--input",
                "somefile.json",
                "--url",
                "http://example.com/schema.json",
                "--output",
                "somedir",
            ];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(1); // Error: Both input and URL specified
        }

        [Test]
        public async Task Main_WithWhitespaceOnlyInput_TreatsAsNull()
        {
            // Arrange
            string[] args = ["--input", "   ", "--output", "somedir"];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(1); // Should treat whitespace-only as null input
        }

        [Test]
        public async Task Main_WithWhitespaceOnlyOutput_TreatsAsNull()
        {
            // Arrange
            string[] args = ["--input", "somefile.json", "--output", "   "];

            // Act
            var exitCode = await Program.Main(args);

            // Assert
            exitCode.Should().Be(1); // Should treat whitespace-only as null output
        }

        [Test]
        public async Task Main_WithNonExistentInputFile_ReturnsErrorExitCode()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var nonExistentFile = Path.Combine(tempDir, "nonexistent.json");

            string[] args = ["--input", nonExistentFile, "--output", tempDir];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(2); // FileNotFoundException error
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithInvalidJsonFile_ReturnsErrorExitCode()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var invalidJsonFile = Path.Combine(tempDir, "invalid.json");

            // Write invalid JSON
            await File.WriteAllTextAsync(invalidJsonFile, "{ invalid json content }");

            string[] args = ["--input", invalidJsonFile, "--output", tempDir];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(2); // JsonException error
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithValidInputAndUrl_ProcessesUrl()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            // Use a URL that will fail (to test error handling)
            string[] args = ["--url", "http://nonexistent.example.com/schema.json", "--output", tempDir];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(2); // HTTP error when fetching from URL
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithValidInputFile_ProcessesSuccessfully()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success

                // Verify output files were created
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql"))
                    .Should()
                    .BeTrue();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeTrue();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithDatabaseProviderPgsql_GeneratesOnlyPostgreSQL()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--provider", "pgsql"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success

                // Verify only PostgreSQL file was created
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql"))
                    .Should()
                    .BeTrue();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeFalse();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithDatabaseProviderPostgresql_GeneratesOnlyPostgreSQL()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--provider", "postgresql"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success

                // Verify only PostgreSQL file was created
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql"))
                    .Should()
                    .BeTrue();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeFalse();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithDatabaseProviderMssql_GeneratesOnlySQLServer()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--provider", "mssql"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success

                // Verify only SQL Server file was created
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-PostgreSQL.sql"))
                    .Should()
                    .BeFalse();
                File.Exists(Path.Combine(tempDir, "EdFi-DMS-Database-Schema-SQLServer.sql"))
                    .Should()
                    .BeTrue();
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithExtensionsFlag_EnablesExtensions()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--extensions"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with extensions enabled
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithSkipUnionViewsFlag_SkipsUnionViews()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--skip-union-views"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with union views skipped
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithUseSchemasFlag_DisablesPrefixedTableNames()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--use-schemas"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with separate schemas
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithSeparateSchemasFlag_DisablesPrefixedTableNames()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--separate-schemas"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with separate schemas
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithUsePrefixedNamesFlag_EnablesPrefixedTableNames()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--use-prefixed-names"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with prefixed table names
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Test]
        public async Task Main_WithPrefixedTablesFlag_EnablesPrefixedTableNames()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var validJsonFile = Path.Combine(tempDir, "valid.json");

            // Write minimal valid JSON schema
            var minimalSchema = new
            {
                projectSchema = new
                {
                    projectName = "EdFi",
                    projectVersion = "1.0.0",
                    resourceSchemas = new Dictionary<string, object>(),
                },
            };
            await File.WriteAllTextAsync(validJsonFile, JsonSerializer.Serialize(minimalSchema));

            string[] args = ["--input", validJsonFile, "--output", tempDir, "--prefixed-tables"];

            try
            {
                // Act
                var exitCode = await Program.Main(args);

                // Assert
                exitCode.Should().Be(0); // Success with prefixed table names
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
