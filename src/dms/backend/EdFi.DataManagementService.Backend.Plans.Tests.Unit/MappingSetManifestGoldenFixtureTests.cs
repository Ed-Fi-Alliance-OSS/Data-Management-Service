// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetManifest_GoldenFixture
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private string _diffOutput = null!;

    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj"
        );

        var expectedPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "runtime-plan-compilation",
            "minimal",
            "expected",
            "mappingset.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "runtime-plan-compilation",
            "minimal",
            "actual",
            "mappingset.manifest.json"
        );

        var manifest = BuildManifest();

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"mappingset manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
    }

    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }

    private static string BuildManifest()
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql)),
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql)),
        };

        return MappingSetManifestJsonEmitter.Emit(mappingSets);
    }
}
