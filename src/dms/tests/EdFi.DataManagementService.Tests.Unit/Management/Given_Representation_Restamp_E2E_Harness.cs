// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms1318-restamp-harness-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_finds_the_repository_root_for_normal_and_linked_worktree_git_markers(bool useGitFile)
    {
        string repositoryRoot = Path.Combine(_temporaryDirectory, "repository");
        string nestedDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");
        string solutionDirectory = Path.Combine(repositoryRoot, "src", "dms");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(solutionDirectory);
        File.WriteAllText(Path.Combine(solutionDirectory, "EdFi.DataManagementService.sln"), "");

        string gitMarker = Path.Combine(repositoryRoot, ".git");
        if (useGitFile)
        {
            File.WriteAllText(gitMarker, "gitdir: ../worktrees/repository");
        }
        else
        {
            Directory.CreateDirectory(gitMarker);
        }

        RepresentationRestampE2EHarness
            .RepositoryRoot(new DirectoryInfo(nestedDirectory))
            .Should()
            .Be(repositoryRoot);
    }

    [Test]
    public async Task It_loads_the_real_manifest_workspace_for_the_ordinary_projector()
    {
        string apiSchemaDirectory = Path.Combine(AppContext.BaseDirectory, "ApiSchema");

        await using ServiceProvider serviceProvider =
            await RepresentationRestampE2EHarness.CreateProjectionServiceProviderAsync(
                "postgresql",
                apiSchemaDirectory,
                CancellationToken.None
            );

        IEffectiveSchemaSetProvider schemaSetProvider =
            serviceProvider.GetRequiredService<IEffectiveSchemaSetProvider>();
        schemaSetProvider.IsInitialized.Should().BeTrue();
        schemaSetProvider.EffectiveSchemaSet.EffectiveSchema.ResourceKeyCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task It_translates_poll_cancellation_into_a_contextual_timeout()
    {
        const string timeoutContext =
            "representation-restamp projection for document 9622f938-2c1a-4f99-9bc4-10970b1c2649";

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.WaitForConditionAsync(
                _ => Task.FromResult(false),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMinutes(1),
                timeoutContext
            );

        await act.Should().ThrowAsync<TimeoutException>().WithMessage($"*{timeoutContext}*");
    }

    [Test]
    public async Task It_removes_the_schema_copy_when_projection_waiting_fails()
    {
        string schemaCopyDirectory = Path.Combine(_temporaryDirectory, "schema-copy");
        Directory.CreateDirectory(schemaCopyDirectory);
        await File.WriteAllTextAsync(Path.Combine(schemaCopyDirectory, "schema.json"), "{}");
        var expectedException = new TimeoutException("projection state context");

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunWithDirectoryCleanupAsync(
                schemaCopyDirectory,
                () => Task.FromException(expectedException)
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception.Should().BeSameAs(expectedException);
        Directory.Exists(schemaCopyDirectory).Should().BeFalse();
    }
}
