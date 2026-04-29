// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_FixtureRunner_With_Profile_Collection_Aligned_Extension_Fixture
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string _actualDir = default!;
    private FixtureConfig _config = default!;
    private FixtureCompareResult _result = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension"
        );

        _config = FixtureConfigReader.Read(fixtureDirectory);
        _actualDir = FixtureRunner.Run(fixtureDirectory);
        NormalizeGeneratedSqlFiles(_actualDir);
        _result = FixtureComparer.Compare(fixtureDirectory);
    }

    [Test]
    public void It_should_produce_actual_output_files()
    {
        Directory.Exists(_actualDir).Should().BeTrue("FixtureRunner should create actual/ directory");
        Directory.GetFiles(_actualDir).Should().NotBeEmpty("FixtureRunner should emit artifacts");
    }

    [Test]
    public void It_should_emit_dialect_sql_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"{dialect}.sql"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_actualDir, "effective-schema.manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_ddl_manifest_when_configured()
    {
        var exists = File.Exists(Path.Combine(_actualDir, "ddl.manifest.json"));

        if (_config.EmitDdlManifest)
        {
            exists.Should().BeTrue("emitDdlManifest is true in fixture.json");
        }
        else
        {
            exists.Should().BeFalse("emitDdlManifest is false in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_relational_model_manifest_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"relational-model.{dialect}.manifest.json"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_match_expected_golden_files()
    {
        _result
            .Passed.Should()
            .BeTrue(
                $"expected/ and actual/ should match. Set UPDATE_GOLDENS=1 to regenerate.\n\n{_result.Message}"
            );
    }

    private static void NormalizeGeneratedSqlFiles(string actualDir)
    {
        foreach (var path in Directory.GetFiles(actualDir, "*.sql"))
        {
            var content = File.ReadAllText(path);
            while (content.EndsWith("\n\n", StringComparison.Ordinal))
            {
                content = content[..^1];
            }

            File.WriteAllText(path, content, _utf8NoBom);
        }
    }
}

[TestFixture]
public class Given_FixtureRunner_With_Profile_Nested_And_Root_Extension_Children_Fixture
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private string _actualDir = default!;
    private FixtureConfig _config = default!;
    private FixtureCompareResult _result = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-nested-and-root-extension-children"
        );

        _config = FixtureConfigReader.Read(fixtureDirectory);
        _actualDir = FixtureRunner.Run(fixtureDirectory);
        NormalizeGeneratedSqlFiles(_actualDir);
        _result = FixtureComparer.Compare(fixtureDirectory);
    }

    [Test]
    public void It_should_produce_actual_output_files()
    {
        Directory.Exists(_actualDir).Should().BeTrue("FixtureRunner should create actual/ directory");
        Directory.GetFiles(_actualDir).Should().NotBeEmpty("FixtureRunner should emit artifacts");
    }

    [Test]
    public void It_should_emit_dialect_sql_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"{dialect}.sql"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_effective_schema_manifest()
    {
        File.Exists(Path.Combine(_actualDir, "effective-schema.manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_should_emit_ddl_manifest_when_configured()
    {
        var exists = File.Exists(Path.Combine(_actualDir, "ddl.manifest.json"));

        if (_config.EmitDdlManifest)
        {
            exists.Should().BeTrue("emitDdlManifest is true in fixture.json");
        }
        else
        {
            exists.Should().BeFalse("emitDdlManifest is false in fixture.json");
        }
    }

    [Test]
    public void It_should_emit_relational_model_manifest_for_each_declared_dialect()
    {
        foreach (var dialect in _config.Dialects)
        {
            File.Exists(Path.Combine(_actualDir, $"relational-model.{dialect}.manifest.json"))
                .Should()
                .BeTrue($"dialect '{dialect}' is declared in fixture.json");
        }
    }

    [Test]
    public void It_should_match_expected_golden_files()
    {
        _result
            .Passed.Should()
            .BeTrue(
                $"expected/ and actual/ should match. Set UPDATE_GOLDENS=1 to regenerate.\n\n{_result.Message}"
            );
    }

    private static void NormalizeGeneratedSqlFiles(string actualDir)
    {
        foreach (var path in Directory.GetFiles(actualDir, "*.sql"))
        {
            var content = File.ReadAllText(path);
            while (content.EndsWith("\n\n", StringComparison.Ordinal))
            {
                content = content[..^1];
            }

            File.WriteAllText(path, content, _utf8NoBom);
        }
    }
}
