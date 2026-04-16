// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_Build_Dms_E2E_Guardrails
{
    private DirectoryInfo _repositoryRoot = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = FindRepositoryRoot();
    }

    [Test]
    public async Task It_fails_fast_when_a_relational_environment_uses_a_legacy_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=true
            RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice_relational
            """,
            "Category!=@relational-backend"
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("Relational E2E environment");
        result.Output.Should().Contain("Category=@relational-backend");
    }

    [Test]
    public async Task It_fails_fast_when_a_legacy_environment_uses_a_relational_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=false
            """,
            "Category=@relational-backend"
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("Legacy E2E environment");
        result.Output.Should().Contain("@relational-backend");
        result.Output.Should().Contain("./.env.e2e.relational");
    }

    [Test]
    public async Task It_fails_fast_when_a_legacy_environment_omits_the_legacy_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=false
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("Legacy E2E environment");
        result.Output.Should().Contain("Category!=@relational-backend");
    }

    [Test]
    public async Task It_fails_fast_when_a_relational_environment_omits_the_relational_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=true
            RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice_relational
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("Relational E2E environment");
        result.Output.Should().Contain("Category=@relational-backend");
    }

    [Test]
    public async Task It_fails_fast_when_a_relational_environment_omits_the_relational_database_name()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=true
            """,
            "Category=@relational-backend"
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("Relational E2E environment");
        result.Output.Should().Contain("RELATIONAL_E2E_DATABASE_NAME");
    }

    [Test]
    public async Task It_fails_fast_when_a_relational_environment_uses_a_disjunctive_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=true
            RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice_relational
            """,
            "Category=@relational-backend|FullyQualifiedName~LegacySuite"
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("cannot use");
        result.Output.Should().Contain("Use '&'");
    }

    [Test]
    public async Task It_fails_fast_when_a_legacy_environment_uses_a_disjunctive_filter()
    {
        var result = await RunBuildDmsE2ETest(
            """
            USE_RELATIONAL_BACKEND=false
            """,
            "Category!=@relational-backend|FullyQualifiedName~RelationalCanary"
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("cannot use");
        result.Output.Should().Contain("Use '&'");
    }

    private async Task<BuildScriptResult> RunBuildDmsE2ETest(
        string environmentFileContents,
        string? testFilter = null
    )
    {
        var environmentFilePath = Path.Combine(
            Path.GetTempPath(),
            $"build-dms-e2e-guardrails-{Guid.NewGuid():N}.env"
        );
        await File.WriteAllTextAsync(environmentFilePath, environmentFileContents);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = _repositoryRoot.FullName,
            };

            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(_repositoryRoot.FullName, "build-dms.ps1"));
            startInfo.ArgumentList.Add("E2ETest");
            startInfo.ArgumentList.Add("-Configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-SkipDockerBuild");
            startInfo.ArgumentList.Add("-EnvironmentFile");
            startInfo.ArgumentList.Add(environmentFilePath);

            if (testFilter is not null)
            {
                startInfo.ArgumentList.Add("-TestFilter");
                startInfo.ArgumentList.Add(testFilter);
            }

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();

            var standardOutputTask = process!.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            return new BuildScriptResult(
                process.ExitCode,
                await standardOutputTask + await standardErrorTask
            );
        }
        finally
        {
            if (File.Exists(environmentFilePath))
            {
                File.Delete(environmentFilePath);
            }
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (
            currentDirectory is not null
            && !File.Exists(Path.Combine(currentDirectory.FullName, "tasks.json"))
        )
        {
            currentDirectory = currentDirectory.Parent;
        }

        return currentDirectory
            ?? throw new InvalidOperationException(
                "Could not locate repository root from the test assembly output."
            );
    }

    private sealed record BuildScriptResult(int ExitCode, string Output);
}
