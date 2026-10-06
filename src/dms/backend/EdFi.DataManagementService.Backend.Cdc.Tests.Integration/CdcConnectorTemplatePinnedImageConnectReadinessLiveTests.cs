// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category(CdcControllerCategories.Admission)]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_PinnedImageFixtureConnectReadinessLiveRecovery(CdcProvider provider)
{
    [Test]
    public async Task It_recreates_connect_after_one_injected_readiness_timeout_and_cleans_both_containers()
    {
        var settings = CdcConnectorTemplateSmokeSettings.FromEnvironment(provider);
        settings.StopIfNotConfigured(provider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var docker = new DockerCli();
        await settings.StopOnPrerequisiteFailureAsync(
            provider,
            docker.RequireDockerAsync(timeout.Token),
            "Docker is required for Connect readiness recovery qualification."
        );
        string prefix = $"dms-cdc-connect-{Guid.NewGuid():N}";
        string pattern = "admission-evidence-connect-readiness-*.json";
        string[] before = Directory.GetFiles(TestContext.CurrentContext.WorkDirectory, pattern);
        List<string> ids = [];
        int preparations = 0;
        await using (
            var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                provider,
                settings with
                {
                    KeepContainers = false,
                },
                docker,
                prefix,
                timeout.Token,
                applyPrerequisitePolicy: false,
                beforeWorker: (_, _) =>
                {
                    preparations++;
                    return Task.CompletedTask;
                },
                exposeBroker: true,
                nativeKafka: true,
                waitForConnect: async (resources, token) =>
                {
                    var result = await docker.RunAsync(
                        ["inspect", "--format", "{{.Id}}", prefix + "-connect"],
                        token
                    );
                    ids.Add(result.StandardOutput.Trim());
                    if (ids.Count == 1)
                    {
                        // Inject only the timeout decision. Inspection, logs, removal, recreation,
                        // replacement HTTP readiness and final cleanup all use real infrastructure.
                        throw new ConnectReadinessTimeoutException(1, 0, "InjectedTimeout", injected: true);
                    }
                    await resources.WaitForKafkaConnectAsync(token);
                }
            )
        )
        {
            preparations.Should().Be(1);
            ids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            string[] paths = Directory
                .GetFiles(TestContext.CurrentContext.WorkDirectory, pattern)
                .Except(before)
                .ToArray();
            paths.Should().HaveCount(2);
            List<string> outcomes = [];
            foreach (string path in paths)
            {
                string json = await File.ReadAllTextAsync(path, timeout.Token);
                json.Should()
                    .NotContain(prefix)
                    .And.NotContain(CdcConnectorTemplatePinnedImageFixture.ConnectorDatabasePassword);
                using var document = JsonDocument.Parse(json);
                document.RootElement.GetProperty("Injected").GetBoolean().Should().BeTrue();
                outcomes.Add(document.RootElement.GetProperty("Outcome").GetString()!);
            }
            outcomes.Should().BeEquivalentTo("TimedOut", "Recovered");
        }
        foreach (string id in ids)
        {
            (await docker.RunAllowingFailureAsync(["inspect", id], timeout.Token)).ExitCode.Should().NotBe(0);
        }
        var networks = await docker.RunAsync(
            ["network", "ls", "--filter", $"name={prefix}", "--format", "{{.Name}}"],
            timeout.Token
        );
        networks.StandardOutput.Should().BeNullOrWhiteSpace();
    }
}
