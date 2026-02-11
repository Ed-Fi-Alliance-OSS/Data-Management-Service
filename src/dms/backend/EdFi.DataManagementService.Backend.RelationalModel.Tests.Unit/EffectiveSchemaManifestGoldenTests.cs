// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
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
        var projectRoot = EffectiveSchemaManifestGoldenHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory
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

        if (EffectiveSchemaManifestGoldenHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = EffectiveSchemaManifestGoldenHelpers.RunGitDiff(expectedPath, actualPath);
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
        var projectRoot = EffectiveSchemaManifestGoldenHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory
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

        if (EffectiveSchemaManifestGoldenHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"effective-schema no-keys manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = EffectiveSchemaManifestGoldenHelpers.RunGitDiff(expectedPath, actualPath);
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
        var projectRoot = EffectiveSchemaManifestGoldenHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory
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

        _diffOutput = EffectiveSchemaManifestGoldenHelpers.RunGitDiff(expectedPath, actualPath);
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
        var projectRoot = EffectiveSchemaManifestGoldenHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            fixtureRoot,
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

        if (EffectiveSchemaManifestGoldenHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"authoritative effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = EffectiveSchemaManifestGoldenHelpers.RunGitDiff(expectedPath, actualPath);
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
        var projectRoot = EffectiveSchemaManifestGoldenHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            fixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var extensionInputPath = Path.Combine(
            fixtureRoot,
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

        if (EffectiveSchemaManifestGoldenHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"authoritative effective-schema manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = EffectiveSchemaManifestGoldenHelpers.RunGitDiff(expectedPath, actualPath);
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

    public static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
            );
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj in parent directories."
        );
    }

    public static string RunGitDiff(string expectedPath, string actualPath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read both streams asynchronously to avoid deadlock when one stream's buffer fills
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outputTask.Result;
        var error = errorTask.Result;

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    public static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
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
