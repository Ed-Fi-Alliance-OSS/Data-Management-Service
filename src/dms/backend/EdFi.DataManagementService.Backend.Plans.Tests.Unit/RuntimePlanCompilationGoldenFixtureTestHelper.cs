// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class RuntimePlanCompilationGoldenFixtureTestHelper
{
    private const string ProjectFileName = "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj";
    private const string ManifestFileName = "mappingset.manifest.json";
    private const string RuntimePlanCompilationFixtureRoot = "runtime-plan-compilation";

    public static RuntimePlanCompilationGoldenFixtureResult BuildAndDiffManifest(
        string fixtureFolderName,
        Func<string> manifestBuilder
    )
    {
        return BuildAndDiffManifest(RuntimePlanCompilationFixtureRoot, fixtureFolderName, manifestBuilder);
    }

    public static RuntimePlanCompilationGoldenFixtureResult BuildAndDiffManifest(
        string fixtureRootFolderName,
        string fixtureFolderName,
        Func<string> manifestBuilder
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureRootFolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureFolderName);
        ArgumentNullException.ThrowIfNull(manifestBuilder);

        var manifest = manifestBuilder();
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            ProjectFileName
        );
        var expectedPath = Path.Combine(
            projectRoot,
            "Fixtures",
            fixtureRootFolderName,
            fixtureFolderName,
            "expected",
            ManifestFileName
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            fixtureRootFolderName,
            fixtureFolderName,
            "actual",
            ManifestFileName
        );

        WriteFile(actualPath, manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            WriteFile(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"mappingset manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        var diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);

        return new RuntimePlanCompilationGoldenFixtureResult(manifest, diffOutput);
    }

    private static void WriteFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Unable to resolve parent directory for golden fixture path '{path}'."
            );
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
    }
}

internal sealed record RuntimePlanCompilationGoldenFixtureResult(string Manifest, string DiffOutput);
