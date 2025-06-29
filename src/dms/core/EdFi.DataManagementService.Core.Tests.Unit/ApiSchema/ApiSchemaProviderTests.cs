// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
[NonParallelizable]
public class ApiSchemaProviderTests
{
    private string _testDirectory = null!;
    private ApiSchemaProvider _loader = null!;
    private IOptions<AppSettings> _appSettings = null!;
    private IApiSchemaValidator _apiSchemaValidator = null!;

    [SetUp]
    public void Setup()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        _appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                UseApiSchemaPath = true,
                ApiSchemaPath = _testDirectory,
            }
        );

        _apiSchemaValidator = A.Fake<IApiSchemaValidator>();
        // By default, return no validation errors
        A.CallTo(() => _apiSchemaValidator.Validate(A<JsonNode>._))
            .Returns(new List<SchemaValidationFailure>());

        _loader = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            _appSettings,
            _apiSchemaValidator
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class ReloadIdManagementTests : ApiSchemaProviderTests
    {
        [Test]
        public void ReloadId_InitialValue_IsNotEmpty()
        {
            // Act
            var version = _loader.ReloadId;

            // Assert
            version.Should().NotBe(Guid.Empty);
        }

        [Test]
        public async Task ReloadId_AfterReload_Changes()
        {
            // Arrange
            var initialVersion = _loader.ReloadId;
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());

            // Act
            await _loader.ReloadApiSchemaAsync();
            var newVersion = _loader.ReloadId;

            // Assert
            newVersion.Should().NotBe(initialVersion);
        }

        [Test]
        public async Task ReloadId_ThreadSafe_MultipleReaders()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
            List<Guid> reloadIds = [];
            List<Task> tasks = [];

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(
                    Task.Run(() =>
                    {
                        for (int j = 0; j < 100; j++)
                        {
                            var reloadId = _loader.ReloadId;
                            lock (reloadIds)
                            {
                                reloadIds.Add(reloadId);
                            }
                        }
                    })
                );
            }

            await Task.WhenAll(tasks);

            // Assert
            reloadIds.Should().HaveCount(1000);
            reloadIds.Distinct().Should().HaveCount(1, "all reads should see the same version");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class ReloadFunctionalityTests : ApiSchemaProviderTests
    {
        [Test]
        public async Task ReloadApiSchemaAsync_ValidDirectory_ReturnsTrue()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());

            // Act
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeTrue();
            result.Failures.Should().NotBeNull();
            result.Failures.Should().BeEmpty();
        }

        [Test]
        public async Task ReloadApiSchemaAsync_InvalidDirectory_ReturnsFalse()
        {
            // Arrange
            _appSettings = Options.Create(
                new AppSettings
                {
                    AllowIdentityUpdateOverrides = "",
                    UseApiSchemaPath = true,
                    ApiSchemaPath = "/nonexistent/path",
                }
            );
            var apiSchemaValidator = A.Fake<IApiSchemaValidator>();
            A.CallTo(() => apiSchemaValidator.Validate(A<JsonNode>._))
                .Returns(new List<SchemaValidationFailure>());

            _loader = new ApiSchemaProvider(
                NullLogger<ApiSchemaProvider>.Instance,
                _appSettings,
                apiSchemaValidator
            );

            // Act
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeFalse();
        }

        [Test]
        public async Task ReloadApiSchemaAsync_EmptyDirectory_ReturnsFalse()
        {
            // Act - Empty directory will cause an exception when looking for core schema
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeFalse();
        }

        [Test]
        public async Task ReloadApiSchemaAsync_MalformedJson_ReturnsFalse()
        {
            // Arrange
            await File.WriteAllTextAsync(Path.Combine(_testDirectory, "ApiSchema.json"), "{ invalid json");

            // Act
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeFalse();
        }

        [Test]
        public async Task ReloadApiSchemaAsync_ConcurrentReloads_AtLeastOneSucceeds()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema());
            List<bool> results = [];
            Barrier barrier = new(5);

            // Act
            var tasks = Enumerable
                .Range(0, 5)
                .Select(_ =>
                    Task.Run(async () =>
                    {
                        barrier.SignalAndWait();
                        var reloadResult = await _loader.ReloadApiSchemaAsync();
                        lock (results)
                        {
                            results.Add(reloadResult.Success);
                        }
                    })
                )
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert
            results.Should().HaveCount(5);
            results.Count(r => r).Should().BeGreaterOrEqualTo(1, "at least one reload should succeed");
        }

        [Test]
        public async Task ReloadApiSchemaAsync_UpdatesSchemaContent()
        {
            // Arrange - Create core schemas (not extensions) with different project descriptions
            string initialSchema = CreateValidApiSchema("Ed-Fi");
            string updatedDescriptionSchema = CreateValidApiSchema("Ed-Fi");

            // Modify the updated schema to have different description
            var updatedSchemaJson = JsonNode.Parse(updatedDescriptionSchema)!;
            updatedSchemaJson["projectSchema"]!["description"] = "Updated Ed-Fi project";
            updatedDescriptionSchema = updatedSchemaJson.ToJsonString();

            await WriteTestSchemaFile("ApiSchema.json", initialSchema);
            await _loader.ReloadApiSchemaAsync();
            var initialNodes = _loader.GetApiSchemaNodes();

            // Act
            await WriteTestSchemaFile("ApiSchema.json", updatedDescriptionSchema);
            await _loader.ReloadApiSchemaAsync();
            var updatedNodes = _loader.GetApiSchemaNodes();

            // Assert - Check that the description changed
            var initialDescription = initialNodes
                .CoreApiSchemaRootNode["projectSchema"]
                ?["description"]?.GetValue<string>();
            var updatedDescription = updatedNodes
                .CoreApiSchemaRootNode["projectSchema"]
                ?["description"]?.GetValue<string>();

            initialDescription.Should().Be("Test Ed-Fi project");
            updatedDescription.Should().Be("Updated Ed-Fi project");
        }
    }

    [TestFixture]
    [NonParallelizable]
    public class FileLoadingTests : ApiSchemaProviderTests
    {
        [Test]
        public async Task LoadApiSchemas_FromDirectory_LoadsAllFiles()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema("Ed-Fi"));
            await WriteTestSchemaFile("ApiSchema.Extension1.json", CreateValidApiSchema("Extension1"));
            await WriteTestSchemaFile("ApiSchema.Extension2.json", CreateValidApiSchema("Extension2"));

            // Act
            await _loader.ReloadApiSchemaAsync();
            var nodes = _loader.GetApiSchemaNodes();

            // Assert
            var coreProjectName = nodes
                .CoreApiSchemaRootNode["projectSchema"]
                ?["projectName"]?.GetValue<string>();
            coreProjectName.Should().Be("Ed-Fi");

            nodes.ExtensionApiSchemaRootNodes.Should().HaveCount(2);
            nodes
                .ExtensionApiSchemaRootNodes.Select(n =>
                    n["projectSchema"]?["projectName"]?.GetValue<string>()
                )
                .Should()
                .BeEquivalentTo("Extension1", "Extension2");
        }

        [Test]
        public async Task LoadApiSchemas_MixedValidInvalid_LoadFails()
        {
            // Arrange
            await WriteTestSchemaFile("ApiSchema.json", CreateValidApiSchema("Ed-Fi"));
            await WriteTestSchemaFile("ApiSchema.Extension1.json", CreateValidApiSchema("Extension1"));
            await File.WriteAllTextAsync(
                Path.Combine(_testDirectory, "ApiSchema.Invalid.json"),
                "{ invalid json"
            );

            // Act
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeFalse("reload should fail due to invalid file");
        }

        [Test]
        public async Task LoadApiSchemas_FileSystemErrors_HandledGracefully()
        {
            // Arrange
            var lockedFilePath = Path.Combine(_testDirectory, "ApiSchema.Locked.json");
            await WriteTestSchemaFile("ApiSchema.Locked.json", CreateValidApiSchema());

            // Lock the file
            using var fileStream = new FileStream(
                lockedFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None
            );

            // Act
            var result = await _loader.ReloadApiSchemaAsync();

            // Assert
            result.Success.Should().BeFalse("reload should fail due to locked file");
        }
    }

    private async Task WriteTestSchemaFile(string fileName, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_testDirectory, fileName), content);
    }

    private static string CreateValidApiSchema(string projectName = "Ed-Fi")
    {
        var schema = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectName,
                ["projectVersion"] = "5.0.0",
                ["description"] = "Test Ed-Fi project",
                ["projectEndpointName"] = "ed-fi",
                ["isExtensionProject"] = projectName != "Ed-Fi",
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
                ["resourceNameMapping"] = new JsonObject(),
                ["resourceSchemas"] = new JsonObject(),
            },
        };

        return schema.ToJsonString();
    }

    [TestFixture]
    [NonParallelizable]
    public class ReadApiSchemaFilesTests : ApiSchemaProviderTests
    {
        [Test]
        public void ReadApiSchemaFiles_ValidDirectory_ReturnsJsonNodes()
        {
            // Arrange
            var validJson = @"{ ""test"": ""value"" }";
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.json"), validJson);
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Extension.json"), validJson);

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(2);
            failures.Should().BeEmpty();
            nodes![0]["test"]?.GetValue<string>().Should().Be("value");
            nodes[1]["test"]?.GetValue<string>().Should().Be("value");
        }

        [Test]
        public void ReadApiSchemaFiles_EmptyDirectory_ReturnsEmptyList()
        {
            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().BeEmpty();
            failures.Should().BeEmpty();
        }

        [Test]
        public void ReadApiSchemaFiles_NonExistentDirectory_ReturnsFailure()
        {
            // Arrange
            var nonExistentPath = Path.Combine(_testDirectory, "nonexistent");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(nonExistentPath);

            // Assert
            nodes.Should().BeNull();
            failures.Should().HaveCount(1);
            failures[0].FailureType.Should().Be("FileSystem");
            failures[0].Message.Should().Contain("Directory not found");
            failures[0].Message.Should().Contain(nonExistentPath);
        }

        [Test]
        public void ReadApiSchemaFiles_InvalidJsonFile_ReturnsParseError()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Invalid.json"), "{ invalid json");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().BeNull();
            failures.Should().HaveCount(1);
            failures[0].FailureType.Should().Be("ParseError");
            failures[0].Message.Should().Contain("Unable to parse ApiSchema file");
        }

        [Test]
        public void ReadApiSchemaFiles_MixedValidAndInvalidFiles_ReturnsAllFailures()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Valid.json"), @"{ ""valid"": true }");
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Invalid1.json"), "{ invalid");
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Invalid2.json"), "not json at all");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().BeNull();
            failures.Should().HaveCount(2);
            failures.TrueForAll(f => f.FailureType == "ParseError");
        }

        [Test]
        public void ReadApiSchemaFiles_NullContentInFile_ReturnsParseError()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Null.json"), "null");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().BeNull();
            failures.Should().HaveCount(1);
            failures[0].FailureType.Should().Be("ParseError");
            failures[0].Message.Should().Contain("parsed to null");
        }

        [Test]
        public void ReadApiSchemaFiles_SubdirectoriesIncluded_FindsAllFiles()
        {
            // Arrange
            var subDir = Path.Combine(_testDirectory, "subdir");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(
                Path.Combine(_testDirectory, "ApiSchema.Root.json"),
                @"{ ""location"": ""root"" }"
            );
            File.WriteAllText(Path.Combine(subDir, "ApiSchema.Sub.json"), @"{ ""location"": ""subdir"" }");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(2);
            failures.Should().BeEmpty();
            nodes!.Select(n => n["location"]?.GetValue<string>()).Should().BeEquivalentTo("root", "subdir");
        }

        [Test]
        public void ReadApiSchemaFiles_FilePatternFiltering_OnlyApiSchemaFiles()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.json"), @"{ ""isApiSchema"": true }");
            File.WriteAllText(
                Path.Combine(_testDirectory, "ApiSchemaTest.json"),
                @"{ ""isApiSchema"": true }"
            );
            File.WriteAllText(
                Path.Combine(_testDirectory, "NotApiSchema.json"),
                @"{ ""isApiSchema"": false }"
            );
            File.WriteAllText(Path.Combine(_testDirectory, "random.txt"), "not json");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(2);
            failures.Should().BeEmpty();
            nodes!.TrueForAll(n => n["isApiSchema"]?.GetValue<bool>() == true);
        }

        [Test]
        public void ReadApiSchemaFiles_EmptyJsonFile_ParsesSuccessfully()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Empty.json"), "{}");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(1);
            failures.Should().BeEmpty();
            nodes![0].AsObject().Should().BeEmpty();
        }

        [Test]
        public void ReadApiSchemaFiles_LargeJsonFile_ParsesSuccessfully()
        {
            // Arrange
            var largeObject = new JsonObject();
            for (int i = 0; i < 1000; i++)
            {
                largeObject[$"property{i}"] = $"value{i}";
            }
            File.WriteAllText(
                Path.Combine(_testDirectory, "ApiSchema.Large.json"),
                largeObject.ToJsonString()
            );

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(1);
            failures.Should().BeEmpty();
            nodes![0].AsObject().Count.Should().Be(1000);
        }

        [Test]
        public void ReadApiSchemaFiles_FileAccessDenied_ReturnsAccessDeniedFailure()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Arrange
                var restrictedFile = Path.Combine(_testDirectory, "ApiSchema.Restricted.json");
                File.WriteAllText(restrictedFile, @"{ ""restricted"": true }");

                // Make file unreadable
                File.SetUnixFileMode(restrictedFile, UnixFileMode.None);

                // Act
                var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

                // Assert
                nodes.Should().BeNull();
                failures.Should().HaveCount(1);
                failures[0].FailureType.Should().Be("AccessDenied");
                failures[0].Message.Should().Contain("Access denied");

                // Cleanup
                File.SetUnixFileMode(restrictedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        [Test]
        public void ReadApiSchemaFiles_DirectoryAccessDenied_ReturnsAccessDeniedFailure()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Arrange
                var restrictedDir = Path.Combine(_testDirectory, "restricted");
                Directory.CreateDirectory(restrictedDir);
                File.WriteAllText(Path.Combine(restrictedDir, "ApiSchema.json"), @"{ ""test"": true }");

                // Make directory unreadable
                File.SetUnixFileMode(restrictedDir, UnixFileMode.None);

                // Act
                var (nodes, failures) = _loader.ReadApiSchemaFiles(restrictedDir);

                // Assert
                nodes.Should().BeNull();
                failures.Should().HaveCount(1);
                failures[0].FailureType.Should().Be("AccessDenied");
                failures[0].Message.Should().Contain("Access denied to directory");

                // Cleanup
                File.SetUnixFileMode(
                    restrictedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
        }

        [Test]
        public void ReadApiSchemaFiles_SpecialCharactersInFileName_HandlesCorrectly()
        {
            // Arrange
            var specialFiles = new[]
            {
                "ApiSchema.Test-Extension.json",
                "ApiSchema_Test_2.json",
                "ApiSchema.Test.V2.json",
            };

            foreach (var fileName in specialFiles)
            {
                File.WriteAllText(Path.Combine(_testDirectory, fileName), $@"{{ ""file"": ""{fileName}"" }}");
            }

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().NotBeNull();
            nodes.Should().HaveCount(3);
            failures.Should().BeEmpty();
        }

        [Test]
        public void ReadApiSchemaFiles_FailureIncludesException_WhenAvailable()
        {
            // Arrange
            File.WriteAllText(Path.Combine(_testDirectory, "ApiSchema.Bad.json"), "{ unclosed");

            // Act
            var (nodes, failures) = _loader.ReadApiSchemaFiles(_testDirectory);

            // Assert
            nodes.Should().BeNull();
            failures.Should().HaveCount(1);
            failures[0].Exception.Should().NotBeNull();
            failures[0].Exception.Should().BeAssignableTo<JsonException>();
        }
    }
}
