// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class ApiSchemaFileLoaderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ApiSchemaFileLoaderTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    protected string CreateSchemaFile(string fileName, JsonNode content)
    {
        var filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, content.ToJsonString());
        return filePath;
    }

    protected static JsonNode CreateValidCoreSchema()
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = "ed-fi",
                ["projectVersion"] = "5.0.0",
                ["projectEndpointName"] = "ed-fi",
                ["isExtensionProject"] = false,
                ["resourceSchemas"] = new JsonObject(),
                ["abstractResources"] = new JsonObject(),
            },
        };
    }

    protected static JsonNode CreateValidExtensionSchema(string endpointName)
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = endpointName,
                ["projectVersion"] = "1.0.0",
                ["projectEndpointName"] = endpointName,
                ["isExtensionProject"] = true,
                ["resourceSchemas"] = new JsonObject(),
                ["abstractResources"] = new JsonObject(),
            },
        };
    }

    [TestFixture]
    public class Given_Valid_Core_Schema_File : ApiSchemaFileLoaderTests
    {
        private ApiSchemaFileLoader _loader = null!;
        private ApiSchemaFileLoadResult _result = null!;

        [SetUp]
        public new void SetUp()
        {
            base.SetUp();

            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _loader = new ApiSchemaFileLoader(normalizer, NullLogger<ApiSchemaFileLoader>.Instance);

            var coreSchemaPath = CreateSchemaFile("core.json", CreateValidCoreSchema());
            _result = _loader.Load(coreSchemaPath, []);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaFileLoadResult.SuccessResult>();
        }

        [Test]
        public void It_returns_normalized_nodes()
        {
            var success = (ApiSchemaFileLoadResult.SuccessResult)_result;
            success.NormalizedNodes.Should().NotBeNull();
            success.NormalizedNodes.CoreApiSchemaRootNode.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_Valid_Core_And_Extension_Files : ApiSchemaFileLoaderTests
    {
        private ApiSchemaFileLoader _loader = null!;
        private ApiSchemaFileLoadResult _result = null!;

        [SetUp]
        public new void SetUp()
        {
            base.SetUp();

            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _loader = new ApiSchemaFileLoader(normalizer, NullLogger<ApiSchemaFileLoader>.Instance);

            var coreSchemaPath = CreateSchemaFile("core.json", CreateValidCoreSchema());
            var ext1Path = CreateSchemaFile("tpdm.json", CreateValidExtensionSchema("tpdm"));
            var ext2Path = CreateSchemaFile("sample.json", CreateValidExtensionSchema("sample"));

            _result = _loader.Load(coreSchemaPath, [ext1Path, ext2Path]);
        }

        [Test]
        public void It_returns_success_result()
        {
            _result.Should().BeOfType<ApiSchemaFileLoadResult.SuccessResult>();
        }

        [Test]
        public void It_includes_both_extensions()
        {
            var success = (ApiSchemaFileLoadResult.SuccessResult)_result;
            success.NormalizedNodes.ExtensionApiSchemaRootNodes.Should().HaveCount(2);
        }

        [Test]
        public void It_sorts_extensions_by_endpoint_name()
        {
            var success = (ApiSchemaFileLoadResult.SuccessResult)_result;
            var endpointNames = success
                .NormalizedNodes.ExtensionApiSchemaRootNodes.Select(n =>
                    n["projectSchema"]?["projectEndpointName"]?.GetValue<string>()
                )
                .ToList();

            endpointNames.Should().Equal("sample", "tpdm");
        }
    }

    [TestFixture]
    public class Given_Nonexistent_Core_File : ApiSchemaFileLoaderTests
    {
        private ApiSchemaFileLoader _loader = null!;
        private ApiSchemaFileLoadResult _result = null!;

        [SetUp]
        public new void SetUp()
        {
            base.SetUp();

            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _loader = new ApiSchemaFileLoader(normalizer, NullLogger<ApiSchemaFileLoader>.Instance);

            _result = _loader.Load(Path.Combine(_tempDir, "nonexistent.json"), []);
        }

        [Test]
        public void It_returns_file_not_found_result()
        {
            _result.Should().BeOfType<ApiSchemaFileLoadResult.FileNotFoundResult>();
        }

        [Test]
        public void It_includes_the_file_path()
        {
            var failure = (ApiSchemaFileLoadResult.FileNotFoundResult)_result;
            failure.FilePath.Should().Contain("nonexistent.json");
        }
    }

    [TestFixture]
    public class Given_Invalid_Json_File : ApiSchemaFileLoaderTests
    {
        private ApiSchemaFileLoader _loader = null!;
        private ApiSchemaFileLoadResult _result = null!;

        [SetUp]
        public new void SetUp()
        {
            base.SetUp();

            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _loader = new ApiSchemaFileLoader(normalizer, NullLogger<ApiSchemaFileLoader>.Instance);

            var invalidJsonPath = Path.Combine(_tempDir, "invalid.json");
            File.WriteAllText(invalidJsonPath, "{ not valid json }");

            _result = _loader.Load(invalidJsonPath, []);
        }

        [Test]
        public void It_returns_invalid_json_result()
        {
            _result.Should().BeOfType<ApiSchemaFileLoadResult.InvalidJsonResult>();
        }

        [Test]
        public void It_includes_the_file_path()
        {
            var failure = (ApiSchemaFileLoadResult.InvalidJsonResult)_result;
            failure.FilePath.Should().Contain("invalid.json");
        }
    }

    [TestFixture]
    public class Given_Schema_Normalization_Failure : ApiSchemaFileLoaderTests
    {
        private ApiSchemaFileLoader _loader = null!;
        private ApiSchemaFileLoadResult _result = null!;

        [SetUp]
        public new void SetUp()
        {
            base.SetUp();

            var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
            _loader = new ApiSchemaFileLoader(normalizer, NullLogger<ApiSchemaFileLoader>.Instance);

            // Create a schema missing projectSchema
            var malformedSchema = new JsonObject { ["apiSchemaVersion"] = "1.0.0" };
            var malformedPath = CreateSchemaFile("malformed.json", malformedSchema);

            _result = _loader.Load(malformedPath, []);
        }

        [Test]
        public void It_returns_normalization_failure_result()
        {
            _result.Should().BeOfType<ApiSchemaFileLoadResult.NormalizationFailureResult>();
        }

        [Test]
        public void It_wraps_the_underlying_failure()
        {
            var failure = (ApiSchemaFileLoadResult.NormalizationFailureResult)_result;
            failure
                .FailureResult.Should()
                .BeOfType<ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult>();
        }
    }
}
