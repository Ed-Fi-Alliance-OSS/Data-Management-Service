// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(CdcControllerCategories.ManagedLifecycle)]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_CdcSqlServerFixtureStartupLiveRecreation
{
    [Test]
    public async Task It_recreates_only_the_failed_unprovisioned_provider_and_retains_the_attempt_evidence()
    {
        CdcConnectorTemplateSmokeSettings settings = CdcConnectorTemplateSmokeSettings.FromEnvironment(
            CdcProvider.SqlServer
        );
        settings.StopIfNotConfigured(CdcProvider.SqlServer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var docker = new DockerCli();
        await settings.StopOnPrerequisiteFailureAsync(
            CdcProvider.SqlServer,
            docker.RequireDockerAsync(timeout.Token),
            "Docker is required for SQL startup recreation qualification."
        );
        string prefix = $"dms-cdc-recreation-{Guid.NewGuid():N}";
        var controlled = new FirstSqlStartFailure(docker, prefix + "-provider");
        string[] before = Directory.GetFiles(
            TestContext.CurrentContext.WorkDirectory,
            "admission-evidence-sql-startup-*.json"
        );
        int beforeWorkerCalls = 0;
        await using (
            var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                CdcProvider.SqlServer,
                settings with
                {
                    KeepContainers = false,
                },
                controlled,
                prefix,
                timeout.Token,
                applyPrerequisitePolicy: false,
                beforeWorker: (_, _) =>
                {
                    beforeWorkerCalls++;
                    return Task.CompletedTask;
                },
                exposeBroker: true,
                composeKafka: true,
                offlineKafka: true
            )
        )
        {
            controlled.ProviderStarts.Should().Be(2);
            controlled.RecoveryRemovals.Should().Be(1);
            controlled.ContainerIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            beforeWorkerCalls.Should().Be(1, "the hook can run only after replacement readiness");
            var state = await docker.RunAsync(
                ["inspect", "--format", "{{.State.Running}}", fixture.ProviderContainerName],
                timeout.Token
            );
            state.StandardOutput.Trim().Should().Be("true");
            string attachment = Directory
                .GetFiles(TestContext.CurrentContext.WorkDirectory, "admission-evidence-sql-startup-*.json")
                .Except(before)
                .Should()
                .ContainSingle()
                .Subject;
            string json = await File.ReadAllTextAsync(attachment, timeout.Token);
            json.Should()
                .NotContain(prefix)
                .And.NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
            using var evidence = JsonDocument.Parse(json);
            evidence.RootElement.GetProperty("Attempt").GetInt32().Should().Be(1);
            evidence.RootElement.GetProperty("RecreationPermitted").GetBoolean().Should().BeTrue();
            evidence
                .RootElement.GetProperty("Container")
                .GetProperty("Logs")
                .GetProperty("LsaInitializationTimeout")
                .GetBoolean()
                .Should()
                .BeTrue();
        }
        foreach (string id in controlled.ContainerIds)
        {
            var removed = await docker.RunAllowingFailureAsync(["inspect", id], timeout.Token);
            removed.ExitCode.Should().NotBe(0, "both the failed and replacement containers must be removed");
        }
        var networks = await docker.RunAsync(
            ["network", "ls", "--filter", $"name={prefix}", "--format", "{{.Name}}"],
            timeout.Token
        );
        networks.StandardOutput.Should().BeNullOrWhiteSpace();
    }

    // Inject only the first process exit. All inspection, removal, replacement startup, readiness,
    // and final cleanup use real Docker operations and the unchanged configured SQL image.
    private sealed class FirstSqlStartFailure(IDockerCli inner, string providerName) : IDockerCli
    {
        public bool IsOffline => false;
        public int ProviderStarts { get; private set; }
        public int RecoveryRemovals { get; private set; }
        public List<string> ContainerIds { get; } = [];

        public Task RequireDockerAsync(CancellationToken cancellationToken) =>
            inner.RequireDockerAsync(cancellationToken);

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) => inner.RunAllowingFailureAsync(arguments, cancellationToken);

        public async Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            if (arguments[0] == "rm" && arguments.Contains(providerName))
            {
                RecoveryRemovals++;
            }
            if (arguments[0] != "run" || !arguments.Contains(providerName))
            {
                return await inner.RunAsync(arguments, cancellationToken);
            }
            ProviderStarts++;
            IReadOnlyList<string> command = arguments;
            if (ProviderStarts == 1)
            {
                command =
                [
                    "run",
                    "--entrypoint",
                    "/bin/sh",
                    .. arguments.Skip(1),
                    "-c",
                    "cat >&2 <<'CDC_LSA_FAILURE'\n"
                        + Given_CdcSqlServerFixtureStartup.LsaFailureLog
                        + "\nCDC_LSA_FAILURE\nexit 1",
                ];
            }
            DockerCommandResult result = await inner.RunAsync(command, cancellationToken);
            ContainerIds.Add(result.StandardOutput.Trim());
            return result;
        }
    }
}
