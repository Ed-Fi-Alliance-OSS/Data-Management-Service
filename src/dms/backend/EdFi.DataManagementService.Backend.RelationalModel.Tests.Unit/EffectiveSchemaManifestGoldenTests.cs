// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for effective schema manifest emission with resource keys included.
/// </summary>
[TestFixture]
public class Given_A_Small_EffectiveSchemaInfo
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "effective-schema");
        var expectedPath = Path.Combine(fixtureRoot, "expected", "effective-schema.manifest.json");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "effective-schema",
            "effective-schema.manifest.json"
        );

        var effectiveSchema = EffectiveSchemaManifestGoldenHelpers.BuildSmallEffectiveSchemaInfo();
        var manifest = EffectiveSchemaManifestEmitter.Emit(effectiveSchema, includeResourceKeys: true);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the expected manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

/// <summary>
/// Test fixture for effective schema manifest emission without resource keys.
/// </summary>
[TestFixture]
public class Given_A_Small_EffectiveSchemaInfo_Without_ResourceKeys
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "effective-schema");
        var expectedPath = Path.Combine(fixtureRoot, "expected", "effective-schema-no-keys.manifest.json");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "effective-schema",
            "effective-schema-no-keys.manifest.json"
        );

        var effectiveSchema = EffectiveSchemaManifestGoldenHelpers.BuildSmallEffectiveSchemaInfo();
        var manifest = EffectiveSchemaManifestEmitter.Emit(effectiveSchema, includeResourceKeys: false);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"effective-schema no-keys manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the expected manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

/// <summary>
/// Test fixture verifying the default Emit behavior excludes resource keys.
/// </summary>
[TestFixture]
public class Given_A_Small_EffectiveSchemaInfo_With_Default_Parameters
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "effective-schema");
        var expectedPath = Path.Combine(fixtureRoot, "expected", "effective-schema-no-keys.manifest.json");
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "effective-schema",
            "effective-schema-default.manifest.json"
        );

        var effectiveSchema = EffectiveSchemaManifestGoldenHelpers.BuildSmallEffectiveSchemaInfo();
        var manifest = EffectiveSchemaManifestEmitter.Emit(effectiveSchema);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"effective-schema no-keys manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the no-keys manifest by default.
    /// </summary>
    [Test]
    public void It_should_match_the_no_keys_manifest_by_default()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

/// <summary>
/// Authoritative golden test: loads ds-5.2 core schema, builds EffectiveSchemaSet,
/// emits manifest, and compares against golden file.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_Core_EffectiveSchemaManifest
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
        );
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            authoritativeFixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var expectedPath = Path.Combine(
            fixtureRoot,
            "ds-5.2",
            "expected",
            "authoritative-effective-schema.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "ds-5.2",
            "authoritative-effective-schema.manifest.json"
        );

        File.Exists(coreInputPath).Should().BeTrue($"fixture missing at {coreInputPath}");

        var coreSchema = EffectiveSchemaManifestGoldenHelpers.LoadProjectSchema(coreInputPath);
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(coreSchema, false);
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);

        var manifest = EffectiveSchemaManifestEmitter.Emit(
            effectiveSchemaSet.EffectiveSchema,
            includeResourceKeys: true
        );

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"authoritative effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the authoritative manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_authoritative_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

/// <summary>
/// Authoritative golden test: loads ds-5.2 core + sample extension schemas,
/// builds EffectiveSchemaSet, emits manifest, and compares against golden file.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_Core_And_Extension_EffectiveSchemaManifest
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
        );
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            authoritativeFixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var extensionInputPath = Path.Combine(
            authoritativeFixtureRoot,
            "sample",
            "inputs",
            "sample-api-schema-authoritative.json"
        );
        var expectedPath = Path.Combine(
            fixtureRoot,
            "sample",
            "expected",
            "authoritative-effective-schema.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "sample",
            "authoritative-effective-schema.manifest.json"
        );

        File.Exists(coreInputPath).Should().BeTrue($"fixture missing at {coreInputPath}");
        File.Exists(extensionInputPath).Should().BeTrue($"fixture missing at {extensionInputPath}");

        var coreSchema = EffectiveSchemaManifestGoldenHelpers.LoadProjectSchema(coreInputPath);
        var extensionSchema = EffectiveSchemaManifestGoldenHelpers.LoadProjectSchema(extensionInputPath);

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(coreSchema, false);
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            true
        );

        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);

        var manifest = EffectiveSchemaManifestEmitter.Emit(
            effectiveSchemaSet.EffectiveSchema,
            includeResourceKeys: true
        );

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"authoritative effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the authoritative manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_authoritative_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }
}

/// <summary>
/// Test fixture verifying that the effective schema manifest emitter produces
/// byte-for-byte identical output when called twice with the same input.
/// </summary>
[TestFixture]
public class Given_EffectiveSchemaManifestEmitter_Emitting_Twice_With_Same_Input
{
    private string _first = default!;
    private string _second = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var effectiveSchema = effectiveSchemaSet.EffectiveSchema;

        _first = EffectiveSchemaManifestEmitter.Emit(effectiveSchema, includeResourceKeys: true);
        _second = EffectiveSchemaManifestEmitter.Emit(effectiveSchema, includeResourceKeys: true);
    }

    /// <summary>
    /// It should produce byte for byte identical output.
    /// </summary>
    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }

    /// <summary>
    /// It should produce non empty output.
    /// </summary>
    [Test]
    public void It_should_produce_non_empty_output()
    {
        _first.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// Shared helpers for effective schema manifest golden tests.
/// </summary>
file static class EffectiveSchemaManifestGoldenHelpers
{
    public static EffectiveSchemaInfo BuildSmallEffectiveSchemaInfo()
    {
        var schemaComponents = new SchemaComponentInfo[]
        {
            new(
                "ed-fi",
                "Ed-Fi",
                "5.2.0",
                false,
                "aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff6677889900"
            ),
            new(
                "tpdm",
                "TPDM",
                "1.0.0",
                true,
                "1122334455667788990011223344556677889900aabbccddeeff0011aabbccdd"
            ),
        };

        var resourceKeys = new ResourceKeyEntry[]
        {
            new(1, new QualifiedResourceName("Ed-Fi", "School"), "5.2.0", false),
            new(2, new QualifiedResourceName("Ed-Fi", "Student"), "5.2.0", false),
            new(3, new QualifiedResourceName("TPDM", "Candidate"), "1.0.0", false),
        };

        var seedHash = new byte[]
        {
            0x01,
            0x23,
            0x45,
            0x67,
            0x89,
            0xAB,
            0xCD,
            0xEF,
            0xFE,
            0xDC,
            0xBA,
            0x98,
            0x76,
            0x54,
            0x32,
            0x10,
            0x01,
            0x23,
            0x45,
            0x67,
            0x89,
            0xAB,
            0xCD,
            0xEF,
            0xFE,
            0xDC,
            0xBA,
            0x98,
            0x76,
            0x54,
            0x32,
            0x10,
        };

        return new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            ResourceKeyCount: 3,
            ResourceKeySeedHash: seedHash,
            SchemaComponentsInEndpointOrder: schemaComponents,
            ResourceKeysInIdOrder: resourceKeys
        );
    }

    public static JsonObject LoadProjectSchema(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"ApiSchema parsed null or non-object: {path}");
        }

        return RequireObject(rootObject["projectSchema"], "projectSchema");
    }
}
