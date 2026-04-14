// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_EffectiveSchemaFixtureLoader
{
    private const string AuthoritativeFixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string FocusedFixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private string _authoritativeFixtureDirectory = null!;
    private string _focusedFixtureDirectory = null!;
    private EffectiveSchemaSet _authoritativeEffectiveSchemaSet = null!;
    private EffectiveSchemaSet _authoritativeEffectiveSchemaSetFromSecondLoad = null!;
    private IReadOnlyList<EffectiveSchemaSet> _parallelFocusedEffectiveSchemaSets = null!;
    private EffectiveSchemaSet _focusedEffectiveSchemaSet = null!;

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

        _authoritativeEffectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(
            _authoritativeFixtureDirectory
        );
        _authoritativeEffectiveSchemaSetFromSecondLoad =
            EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(_authoritativeFixtureDirectory);
        _parallelFocusedEffectiveSchemaSets = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    Task.Run(() =>
                        EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(_focusedFixtureDirectory)
                    )
                )
        );
        _focusedEffectiveSchemaSet = _parallelFocusedEffectiveSchemaSets[0];
    }

    [Test]
    public void It_reuses_the_same_effective_schema_set_for_repeated_loads()
    {
        _authoritativeEffectiveSchemaSetFromSecondLoad.Should().BeSameAs(_authoritativeEffectiveSchemaSet);
    }

    [Test]
    public void It_reuses_the_same_effective_schema_set_during_parallel_loads()
    {
        _parallelFocusedEffectiveSchemaSets
            .Should()
            .AllSatisfy(effectiveSchemaSet =>
                effectiveSchemaSet.Should().BeSameAs(_focusedEffectiveSchemaSet)
            );
    }

    [Test]
    public void It_keeps_cache_entries_separate_per_fixture_directory()
    {
        _focusedEffectiveSchemaSet.Should().NotBeSameAs(_authoritativeEffectiveSchemaSet);
    }
}
