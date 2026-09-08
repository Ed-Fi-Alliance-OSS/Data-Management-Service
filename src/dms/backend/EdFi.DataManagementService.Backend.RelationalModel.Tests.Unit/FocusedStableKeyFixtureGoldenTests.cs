// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Focused_Stable_Key_Positive_Fixture_Golden
{
    private const string ProjectFileName =
        "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj";
    private const string FixtureRelativePath =
        "Fixtures/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
    private const string ExpectedFileName = "focused-stable-key-derived-relational-model-set.json";
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private string _diffOutput = null!;

    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            ProjectFileName
        );
        var fixtureRoot = Path.Combine(
            projectRoot,
            "Fixtures",
            "focused-stable-key",
            "positive",
            "extension-child-collections"
        );
        var expectedPath = Path.Combine(fixtureRoot, "expected", ExpectedFileName);
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "focused-stable-key",
            "positive",
            "extension-child-collections",
            ExpectedFileName
        );

        var derivedSet = FocusedStableKeyFixtureDerivedModelSetBuilder.Build(
            FixtureRelativePath,
            SqlDialect.Pgsql
        );
        var manifest = DerivedModelSetManifestEmitter.Emit(
            derivedSet,
            new HashSet<QualifiedResourceName>([_schoolResource])
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
                $"focused stable-key manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

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
}
