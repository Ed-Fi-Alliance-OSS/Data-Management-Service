// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_A_Valid_Fixture_Json
{
    private FixtureConfig _config = default!;
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "inputs", "edfi"));

        File.WriteAllText(Path.Combine(_tempDir, "inputs", "edfi", "ApiSchema.json"), "{}");

        File.WriteAllText(
            Path.Combine(_tempDir, "fixture.json"),
            """
            {
              "apiSchemaFiles": ["edfi/ApiSchema.json"],
              "dialects": ["pgsql", "mssql"],
              "buildMappingPack": false
            }
            """
        );

        _config = FixtureConfigReader.Read(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_parse_apiSchemaFiles()
    {
        _config.ApiSchemaFiles.Should().BeEquivalentTo("edfi/ApiSchema.json");
    }

    [Test]
    public void It_should_parse_dialects()
    {
        _config.Dialects.Should().BeEquivalentTo("pgsql", "mssql");
    }

    [Test]
    public void It_should_parse_buildMappingPack()
    {
        _config.BuildMappingPack.Should().BeFalse();
    }
}

[TestFixture]
public class Given_A_Fixture_Json_With_Defaults
{
    private FixtureConfig _config = default!;
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "inputs", "edfi"));

        File.WriteAllText(Path.Combine(_tempDir, "inputs", "edfi", "ApiSchema.json"), "{}");

        File.WriteAllText(
            Path.Combine(_tempDir, "fixture.json"),
            """
            {
              "apiSchemaFiles": ["edfi/ApiSchema.json"],
              "dialects": ["pgsql"]
            }
            """
        );

        _config = FixtureConfigReader.Read(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_default_buildMappingPack_to_false()
    {
        _config.BuildMappingPack.Should().BeFalse();
    }
}

[TestFixture]
public class Given_A_Missing_Fixture_Json
{
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
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

    [Test]
    public void It_should_throw_FileNotFoundException()
    {
        var act = () => FixtureConfigReader.Read(_tempDir);
        act.Should().Throw<FileNotFoundException>().WithMessage("*fixture.json*");
    }
}

[TestFixture]
public class Given_A_Fixture_Json_With_Empty_ApiSchemaFiles
{
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(
            Path.Combine(_tempDir, "fixture.json"),
            """
            {
              "apiSchemaFiles": [],
              "dialects": ["pgsql"]
            }
            """
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        var act = () => FixtureConfigReader.Read(_tempDir);
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one entry*apiSchemaFiles*");
    }
}

[TestFixture]
public class Given_A_Fixture_Json_With_Unknown_Dialect
{
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "inputs"));

        File.WriteAllText(Path.Combine(_tempDir, "inputs", "ApiSchema.json"), "{}");

        File.WriteAllText(
            Path.Combine(_tempDir, "fixture.json"),
            """
            {
              "apiSchemaFiles": ["ApiSchema.json"],
              "dialects": ["oracle"]
            }
            """
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_throw_InvalidOperationException()
    {
        var act = () => FixtureConfigReader.Read(_tempDir);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Unknown dialect*oracle*");
    }
}

[TestFixture]
public class Given_A_Fixture_Json_With_Missing_Input_File
{
    private string _tempDir = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "inputs"));

        File.WriteAllText(
            Path.Combine(_tempDir, "fixture.json"),
            """
            {
              "apiSchemaFiles": ["edfi/ApiSchema.json"],
              "dialects": ["pgsql"]
            }
            """
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_throw_FileNotFoundException()
    {
        var act = () => FixtureConfigReader.Read(_tempDir);
        act.Should().Throw<FileNotFoundException>().WithMessage("*ApiSchema file*not found*");
    }
}
