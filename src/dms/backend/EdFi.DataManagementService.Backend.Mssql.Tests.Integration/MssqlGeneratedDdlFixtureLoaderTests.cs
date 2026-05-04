// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlFixtureLoader
{
    private const string AuthoritativeFixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string FocusedFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string SmallExtensionFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/ext";

    private string _authoritativeFixtureDirectory = null!;
    private string _focusedFixtureDirectory = null!;
    private MssqlGeneratedDdlFixture _authoritativeFixtureFromRelativePath = null!;
    private MssqlGeneratedDdlFixture _authoritativeFixtureFromDirectory = null!;
    private IReadOnlyList<MssqlGeneratedDdlFixture> _parallelFocusedFixtures = null!;
    private MssqlGeneratedDdlFixture _focusedFixtureFromDirectory = null!;

    [SetUp]
    public async Task Setup()
    {
        _authoritativeFixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            AuthoritativeFixtureRelativePath
        );
        _focusedFixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            FocusedFixtureRelativePath
        );

        _authoritativeFixtureFromRelativePath = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeFixtureRelativePath
        );
        _authoritativeFixtureFromDirectory = MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
            _authoritativeFixtureDirectory
        );
        _parallelFocusedFixtures = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    Task.Run(() =>
                        MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
                            _focusedFixtureDirectory,
                            strict: false
                        )
                    )
                )
        );
        _focusedFixtureFromDirectory = _parallelFocusedFixtures[0];
    }

    [Test]
    public void It_reuses_the_same_fixture_artifacts_for_repeated_loads()
    {
        _authoritativeFixtureFromDirectory.Should().BeSameAs(_authoritativeFixtureFromRelativePath);
        _authoritativeFixtureFromDirectory
            .EffectiveSchemaSet.Should()
            .BeSameAs(_authoritativeFixtureFromRelativePath.EffectiveSchemaSet);
        _authoritativeFixtureFromDirectory
            .ModelSet.Should()
            .BeSameAs(_authoritativeFixtureFromRelativePath.ModelSet);
        _authoritativeFixtureFromDirectory
            .MappingSet.Should()
            .BeSameAs(_authoritativeFixtureFromRelativePath.MappingSet);
        _authoritativeFixtureFromDirectory
            .GeneratedDdl.Should()
            .BeSameAs(_authoritativeFixtureFromRelativePath.GeneratedDdl);
    }

    [Test]
    public void It_reuses_the_same_fixture_artifacts_during_parallel_loads()
    {
        _parallelFocusedFixtures
            .Should()
            .AllSatisfy(fixture => fixture.Should().BeSameAs(_focusedFixtureFromDirectory));
        _parallelFocusedFixtures
            .Select(fixture => fixture.EffectiveSchemaSet)
            .Should()
            .AllSatisfy(effectiveSchemaSet =>
                effectiveSchemaSet.Should().BeSameAs(_focusedFixtureFromDirectory.EffectiveSchemaSet)
            );
        _parallelFocusedFixtures
            .Select(fixture => fixture.ModelSet)
            .Should()
            .AllSatisfy(modelSet => modelSet.Should().BeSameAs(_focusedFixtureFromDirectory.ModelSet));
        _parallelFocusedFixtures
            .Select(fixture => fixture.MappingSet)
            .Should()
            .AllSatisfy(mappingSet => mappingSet.Should().BeSameAs(_focusedFixtureFromDirectory.MappingSet));
        _parallelFocusedFixtures
            .Select(fixture => fixture.GeneratedDdl)
            .Should()
            .AllSatisfy(generatedDdl =>
                generatedDdl.Should().BeSameAs(_focusedFixtureFromDirectory.GeneratedDdl)
            );
    }

    [Test]
    public void It_keeps_cache_entries_separate_per_fixture_directory()
    {
        _focusedFixtureFromDirectory.Should().NotBeSameAs(_authoritativeFixtureFromDirectory);
        _focusedFixtureFromDirectory
            .EffectiveSchemaSet.Should()
            .NotBeSameAs(_authoritativeFixtureFromDirectory.EffectiveSchemaSet);
        _focusedFixtureFromDirectory
            .ModelSet.Should()
            .NotBeSameAs(_authoritativeFixtureFromDirectory.ModelSet);
        _focusedFixtureFromDirectory
            .MappingSet.Should()
            .NotBeSameAs(_authoritativeFixtureFromDirectory.MappingSet);
        _focusedFixtureFromDirectory.FixtureDirectory.Should().Be(_focusedFixtureDirectory);
        _authoritativeFixtureFromDirectory.FixtureDirectory.Should().Be(_authoritativeFixtureDirectory);
    }

    [Test]
    public void It_reloads_fixture_artifacts_when_a_fixture_schema_file_changes_in_place()
    {
        var tempFixtureDirectory = CreateTemporaryFixtureCopy(SmallExtensionFixtureRelativePath);

        try
        {
            var initialFixture = MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
                tempFixtureDirectory,
                strict: false
            );
            var schemaPath = Path.Combine(tempFixtureDirectory, "inputs", "ApiSchema-Sample.json");
            File.WriteAllText(schemaPath, File.ReadAllText(schemaPath) + Environment.NewLine);

            var reloadedFixture = MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
                tempFixtureDirectory,
                strict: false
            );

            reloadedFixture.Should().NotBeSameAs(initialFixture);
            reloadedFixture.EffectiveSchemaSet.Should().NotBeSameAs(initialFixture.EffectiveSchemaSet);
            reloadedFixture.ModelSet.Should().NotBeSameAs(initialFixture.ModelSet);
            reloadedFixture.MappingSet.Should().NotBeSameAs(initialFixture.MappingSet);
        }
        finally
        {
            Directory.Delete(tempFixtureDirectory, recursive: true);
        }
    }

    [Test]
    public void It_reloads_fixture_artifacts_when_the_fixture_manifest_changes_in_place()
    {
        var tempFixtureDirectory = CreateTemporaryFixtureCopy(SmallExtensionFixtureRelativePath);

        try
        {
            var initialFixture = MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
                tempFixtureDirectory,
                strict: false
            );

            ReplaceFixtureManifestWithBaseOnlyManifest(tempFixtureDirectory);

            var reloadedFixture = MssqlGeneratedDdlFixtureLoader.LoadFromFixtureDirectory(
                tempFixtureDirectory,
                strict: false
            );

            reloadedFixture.Should().NotBeSameAs(initialFixture);
            reloadedFixture.EffectiveSchemaSet.Should().NotBeSameAs(initialFixture.EffectiveSchemaSet);
            reloadedFixture.ModelSet.Should().NotBeSameAs(initialFixture.ModelSet);
            reloadedFixture.MappingSet.Should().NotBeSameAs(initialFixture.MappingSet);
            reloadedFixture.GeneratedDdl.Should().NotBe(initialFixture.GeneratedDdl);
        }
        finally
        {
            Directory.Delete(tempFixtureDirectory, recursive: true);
        }
    }

    private static void ReplaceFixtureManifestWithBaseOnlyManifest(string fixtureDirectory)
    {
        File.WriteAllText(
            Path.Combine(fixtureDirectory, "fixture.json"),
            """
            {
              "apiSchemaFiles": ["ApiSchema.json"],
              "dialects": ["pgsql", "mssql"],
              "emitDdlManifest": true
            }
            """
        );
    }

    private static string CreateTemporaryFixtureCopy(string fixtureRelativePath)
    {
        var sourceFixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            fixtureRelativePath
        );
        var tempFixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mssql-fixture-loader-{Guid.NewGuid():N}"
        );

        GoldenFixtureTestHelpers.CopyDirectory(sourceFixtureDirectory, tempFixtureDirectory);
        return tempFixtureDirectory;
    }
}
