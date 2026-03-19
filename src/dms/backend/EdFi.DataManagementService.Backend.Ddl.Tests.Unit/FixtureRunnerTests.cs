// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class FixtureTestHelper
{
    public static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (
                File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj"
                    )
                )
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj in parent directories."
        );
    }
}

[TestFixture]
public class Given_FixtureRunner_With_EmitDdlManifest_False
{
    private string _fixtureDirectory = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        _fixtureDirectory = Path.Combine(projectRoot, "Fixtures", "small", "no-ddl-manifest");

        FixtureRunner.Run(_fixtureDirectory);
    }

    [Test]
    public void It_should_not_emit_ddl_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "ddl.manifest.json")).Should().BeFalse();
    }

    [Test]
    public void It_should_still_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "effective-schema.manifest.json"))
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_should_still_emit_dialect_sql()
    {
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "pgsql.sql")).Should().BeTrue();
        File.Exists(Path.Combine(_fixtureDirectory, "actual", "mssql.sql")).Should().BeTrue();
    }
}

[TestFixture("minimal")]
[TestFixture("nested")]
[TestFixture("polymorphic")]
[TestFixture("ext")]
[TestFixture("naming-stress")]
public class Given_FixtureRunner_With_Small_Fixture(string fixtureName)
{
    private string _fixtureDirectory = default!;
    private FixtureCompareResult _result = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        _fixtureDirectory = Path.Combine(projectRoot, "Fixtures", "small", fixtureName);

        FixtureRunner.Run(_fixtureDirectory);
        _result = FixtureComparer.Compare(_fixtureDirectory);
    }

    [Test]
    public void It_should_match_expected_output()
    {
        _result
            .Passed.Should()
            .BeTrue(
                $"expected/ and actual/ should match. Set UPDATE_GOLDENS=1 to regenerate.\n\n{_result.Message}"
            );
    }

    [Test]
    public void It_should_emit_all_expected_artifacts()
    {
        var actualDir = Path.Combine(_fixtureDirectory, "actual");
        File.Exists(Path.Combine(actualDir, "effective-schema.manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(actualDir, "relational-model.pgsql.manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(actualDir, "relational-model.mssql.manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(actualDir, "pgsql.sql")).Should().BeTrue();
        File.Exists(Path.Combine(actualDir, "mssql.sql")).Should().BeTrue();
        File.Exists(Path.Combine(actualDir, "ddl.manifest.json")).Should().BeTrue();
    }
}

[TestFixture]
public class Given_FixtureComparer_When_UpdateGoldens_Is_Set
{
    private string _tempFixtureDirectory = default!;
    private FixtureCompareResult _result = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var projectRoot = FixtureTestHelper.FindProjectRoot();
        var sourceFixtureDirectory = Path.Combine(projectRoot, "Fixtures", "small", "no-ddl-manifest");

        // Work on a temp copy so we never mutate the checked-in expected/ directory
        _tempFixtureDirectory = Path.Combine(Path.GetTempPath(), $"ddl-fixture-{Guid.NewGuid():N}");
        CopyDirectory(sourceFixtureDirectory, _tempFixtureDirectory);

        FixtureRunner.Run(_tempFixtureDirectory);

        // Remove expected/ so UpdateGoldens has to create it
        var expectedDir = Path.Combine(_tempFixtureDirectory, "expected");
        if (Directory.Exists(expectedDir))
        {
            Directory.Delete(expectedDir, recursive: true);
        }

        _result = FixtureComparer.Compare(_tempFixtureDirectory, updateGoldens: true);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempFixtureDirectory))
        {
            Directory.Delete(_tempFixtureDirectory, recursive: true);
        }
    }

    [Test]
    public void It_should_report_passed()
    {
        _result.Passed.Should().BeTrue();
    }

    [Test]
    public void It_should_populate_expected_directory()
    {
        var expectedDir = Path.Combine(_tempFixtureDirectory, "expected");
        Directory.Exists(expectedDir).Should().BeTrue();
        Directory.GetFiles(expectedDir).Should().NotBeEmpty();
    }

    [Test]
    public void It_should_copy_all_actual_files_to_expected()
    {
        var actualDir = Path.Combine(_tempFixtureDirectory, "actual");
        var expectedDir = Path.Combine(_tempFixtureDirectory, "expected");

        var actualFiles = Directory
            .GetFiles(actualDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actualDir, f))
            .OrderBy(f => f)
            .ToList();

        var expectedFiles = Directory
            .GetFiles(expectedDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expectedDir, f))
            .OrderBy(f => f)
            .ToList();

        expectedFiles.Should().BeEquivalentTo(actualFiles);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }
}
