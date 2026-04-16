// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public partial class Given_Relational_Provisioning_Helper
{
    private string _scriptContents = null!;

    [SetUp]
    public void Setup()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot.FullName,
            "eng",
            "docker-compose",
            "provision-relational-e2e-database.ps1"
        );

        _scriptContents = File.ReadAllText(scriptPath);
    }

    [Test]
    public void It_uses_the_resolved_postgres_user_for_readiness_and_reset_operations()
    {
        _scriptContents.Should().Contain("[string]$PostgresUsername");
        _scriptContents
            .Should()
            .Contain(
                "Wait-ForPostgresql -ContainerName $PostgresContainerName -PostgresUsername $postgresUsername"
            );
        _scriptContents.Should().Contain("-PostgresUsername $postgresUsername");
        HardcodedPostgresUserRegex().Matches(_scriptContents).Should().BeEmpty();
    }

    [Test]
    public async Task It_fails_fast_when_the_relational_database_matches_the_bootstrap_database_name()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("must be dedicated");
        result.Output.Should().Contain("POSTGRES_DB_NAME");
    }

    [Test]
    public async Task It_fails_fast_when_a_bootstrap_connection_string_targets_the_relational_database()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            RELATIONAL_E2E_DATABASE_NAME=edfi_datamanagementservice_relational
            DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_relational;NoResetOnClose=true;
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("must stay separate from DATABASE_CONNECTION_STRING");
    }

    [Test]
    public async Task It_fails_fast_when_the_relational_database_targets_a_system_database()
    {
        var result = await RunProvisioningHelper(
            """
            POSTGRES_PORT=5435
            POSTGRES_PASSWORD=abcdefgh1!
            POSTGRES_DB_NAME=edfi_datamanagementservice
            RELATIONAL_E2E_DATABASE_NAME=postgres
            """
        );

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("reserved PostgreSQL system database");
        result.Output.Should().Contain("postgres");
    }

    private async Task<ScriptResult> RunProvisioningHelper(string environmentFileContents)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot.FullName,
            "eng",
            "docker-compose",
            "provision-relational-e2e-database.ps1"
        );
        var environmentFilePath = Path.Combine(
            Path.GetTempPath(),
            $"relational-provisioning-helper-{Guid.NewGuid():N}.env"
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
                WorkingDirectory = repositoryRoot.FullName,
            };

            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-EnvironmentFile");
            startInfo.ArgumentList.Add(environmentFilePath);
            startInfo.ArgumentList.Add("-Configuration");
            startInfo.ArgumentList.Add("Release");

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();

            var standardOutputTask = process!.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            return new ScriptResult(process.ExitCode, await standardOutputTask + await standardErrorTask);
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

    [GeneratedRegex(@"-U\s+postgres\b", RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedPostgresUserRegex();

    private sealed record ScriptResult(int ExitCode, string Output);
}
