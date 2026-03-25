// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_A_Test_Directory_Beneath_A_Dms_Repository_Root
{
    private string _repositoryRoot = null!;
    private string _startDirectory = null!;
    private string _repositoryRelativeFilePath = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), $"fixture-path-resolver-{Guid.NewGuid():N}");
        _startDirectory = Path.Combine(
            _repositoryRoot,
            "src",
            "dms",
            "backend",
            "Project.Tests",
            "bin",
            "Release",
            "net10.0"
        );
        _repositoryRelativeFilePath = Path.Combine(_repositoryRoot, "src", "dms", "backend", "fixture.json");

        Directory.CreateDirectory(_startDirectory);
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, "src", "dms"));
        Directory.CreateDirectory(Path.GetDirectoryName(_repositoryRelativeFilePath)!);

        File.WriteAllText(Path.Combine(_repositoryRoot, "src", "dms", "EdFi.DataManagementService.sln"), "");
        File.WriteAllText(_repositoryRelativeFilePath, "{}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    [Test]
    public void It_should_locate_the_repository_root()
    {
        FixturePathResolver.FindRepositoryRoot(_startDirectory).Should().Be(_repositoryRoot);
    }

    [Test]
    public void It_should_resolve_repository_relative_paths()
    {
        FixturePathResolver
            .ResolveRepositoryRelativePath(_startDirectory, "src/dms/backend/fixture.json")
            .Should()
            .Be(_repositoryRelativeFilePath);
    }
}

[TestFixture]
public class Given_A_Test_Directory_Outside_A_Known_Repository_Root
{
    private string _startDirectory = null!;
    private string _tempRoot = null!;

    [SetUp]
    public void Setup()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"fixture-path-resolver-{Guid.NewGuid():N}");
        _startDirectory = Path.Combine(_tempRoot, "bin", "Release", "net10.0");

        Directory.CreateDirectory(_startDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public void It_should_throw_DirectoryNotFoundException()
    {
        var act = () => FixturePathResolver.FindRepositoryRoot(_startDirectory);

        act.Should().Throw<DirectoryNotFoundException>().WithMessage("*Unable to locate repository root*");
    }
}
