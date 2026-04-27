// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class FixtureTestHelper
{
    private const string CsprojFileName = "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj";

    public static string FindProjectRoot() =>
        GoldenFixtureTestHelpers.FindProjectRoot(TestContext.CurrentContext.TestDirectory, CsprojFileName);
}

[TestFixture]
public class Given_FixtureRunner_With_Minimal_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "minimal");
}

[TestFixture]
public class Given_FixtureRunner_With_Nested_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "nested");
}

[TestFixture]
public class Given_FixtureRunner_With_Polymorphic_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "polymorphic");
}

[TestFixture]
public class Given_FixtureRunner_With_Ext_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "ext");
}

[TestFixture]
public class Given_FixtureRunner_With_Focused_Stable_Key_Extension_Child_Collections_Fixture
    : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "focused", "stable-key-extension-child-collections");
}

[TestFixture]
public class Given_FixtureRunner_With_Focused_TopLevel_ReferenceBackedCollection_Fixture
    : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "focused", "top-level-reference-backed-collection");
}

[TestFixture]
public class Given_FixtureRunner_With_NamingStress_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "naming-stress");
}

[TestFixture]
public class Given_FixtureRunner_With_ReferentialIdentity_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "referential-identity");
}

[TestFixture]
public class Given_FixtureRunner_With_ProfileRootOnlyMerge_Fixture : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "profile-root-only-merge");
}

[TestFixture]
public class Given_FixtureRunner_With_EmitDdlManifest_False : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "Fixtures", "small", "no-ddl-manifest");
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
        GoldenFixtureTestHelpers.CopyDirectory(sourceFixtureDirectory, _tempFixtureDirectory);

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
}
