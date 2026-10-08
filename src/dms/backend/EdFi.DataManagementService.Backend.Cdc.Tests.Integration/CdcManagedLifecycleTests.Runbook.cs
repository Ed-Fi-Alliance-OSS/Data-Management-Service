// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

public sealed partial class Given_Cdc_Controller_Managed_Lifecycle
{
    private DocumentCacheAdminCliProcessHarness _retirementCommandRuntime = null!;
    private string _retirementSettingsPath = "";
    private static bool IsRetirementCase =>
        TestContext.CurrentContext.Test.MethodName
        == nameof(It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history);

    private static string[] RetirementSchemaFiles()
    {
        if (!IsRetirementCase)
        {
            return null!; // Preserve the original controller fixtures' minimal schema.
        }
        string workspace = DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory;
        var manifest = ApiSchemaAssetManifestReader.ReadFromFile(
            workspace,
            Path.Combine(workspace, ApiSchemaAssetManifestReader.ManifestFileName)
        );
        return manifest.Projects.Select(p => Path.Combine(workspace, p.SchemaPath)).ToArray();
    }

    private async Task PrepareRetirementRunbookAsync()
    {
        _retirementSettingsPath = Path.Combine(_fixture.Infrastructure.StateRoot, "retirement-settings.json");
        _retirementCommandRuntime = await DocumentCacheAdminCliProcessHarness.CreateAsync(
            DocumentCacheAdminCliTarget.ForManagedSource(
                provider == Ddl.CdcProvider.SqlServer,
                _fixture.ConnectionString
            )
        );
        var settings = new ConfigurationBuilder().AddJsonFile(_retirementCommandRuntime.SettingsPath).Build();
        using var lifetime = (IDisposable)settings;
        var runtimeSettings = settings
            .AsEnumerable()
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!);
        runtimeSettings["ConfigurationServiceSettings:ClientSecret"] =
            _retirementCommandRuntime.SecretFromEnvironment;
        runtimeSettings["Cdc:Timing:CallMilliseconds"] = "180000";
        runtimeSettings["Cdc:Timing:WaitMilliseconds"] = "300000";
        // Match the public setup snippets: SQL Server capture/poll cadence can exceed
        // the shared observation helper's one-second threshold while caught up.
        runtimeSettings["Cdc:LagThresholdMilliseconds"] = "5000";
        await CdcRunbookLiveCommands.WriteSettingsAsync(
            _fixture,
            provider,
            _retirementSettingsPath,
            Token,
            runtimeSettings
        );
        var images = new Dictionary<string, string>();
        foreach (string role in new[] { "provider", "broker", "connect" })
        {
            var inspected = await new DockerCli().RunAsync(
                [
                    "inspect",
                    _fixture.Infrastructure.Resources.ControllerProject + "-" + role,
                    "--format",
                    "{{.Image}}",
                ],
                Token
            );
            string image = inspected.StandardOutput.Trim();
            image.Should().MatchRegex("^sha256:[a-f0-9]{64}$");
            images[role] = image;
        }
        _evidence.Add(new { Profile = "LocalSingleBroker/AuthorizationDisabledLocal", Images = images });
    }

    private Task<JsonElement> InvokeRetirementSnippetAsync(string id, long generation, bool confirm = true) =>
        CdcRunbookLiveCommands.InvokeAsync(
            id,
            _retirementSettingsPath,
            _fixture.Infrastructure.StateRoot,
            Token,
            new Dictionary<string, string>
            {
                ["<binding-generation>"] = generation.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
            },
            confirmDestructiveCleanup: confirm
        );

    private async Task AssertRetirementGuardsAndHandoffsAsync()
    {
        var request = _fixture.Request;
        var journal = await _fixture.JournalAsync(Token);
        var noIntent = await InvokeRetirementSnippetAsync(
            "cdc-retire",
            request.Binding.Generation,
            confirm: false
        );
        noIntent.GetProperty("exitCode").GetInt32().Should().Be(2);
        var wrongGeneration = await InvokeRetirementSnippetAsync(
            "cdc-retire",
            request.Binding.Generation + 1
        );
        wrongGeneration.GetProperty("exitCode").GetInt32().Should().Be(2);
        (await _fixture.JournalAsync(Token)).Should().BeEquivalentTo(journal);

        var status = await InvokeRetirementSnippetAsync(
            "cdc-restamp-handoff-status",
            request.Binding.Generation
        );
        status.GetProperty("exitCode").GetInt32().Should().Be(1);
        status.GetProperty("data").GetProperty("targets").GetArrayLength().Should().Be(1);
        status
            .GetProperty("data")
            .GetProperty("targets")[0]
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Should()
            .Contain(d =>
                d.GetProperty("component").GetString() == "Projection"
                && d.GetProperty("failure").GetString() == "Unavailable"
            );
        var stopped = await InvokeRetirementSnippetAsync(
            "cdc-disclosure-containment-result",
            request.Binding.Generation
        );
        stopped.GetProperty("exitCode").GetInt32().Should().Be(0);
        stopped.GetProperty("data").GetProperty("targetShutdownVerified").GetBoolean().Should().BeTrue();
        stopped.GetProperty("data").GetProperty("ready").GetBoolean().Should().BeFalse();
        // Running offsets may advance until stop is verified. Sample only after that
        // boundary, require a retained streaming offset, then prove no new work is consumed.
        var offset = await OffsetAsync();
        await WriteHeartbeatAsync();
        (await OffsetAsync()).Should().BeEquivalentTo(offset);
        (await _fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, Token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        _evidence.Add(
            new
            {
                MarkedStopVerified = true,
                OffsetsPreserved = true,
                BindingPreserved = true,
                ConsumerAccessRevoked = false,
                PlatformPurgeProven = false,
            }
        );
    }

    private async Task AssertMarkedRetirementCompleteAsync()
    {
        string settings = await File.ReadAllTextAsync(_retirementSettingsPath, Token);
        var original = (await _fixture.JournalAsync(Token)).Operations.Single(o =>
            o.Effect == CdcWorkflowEffect.Retire
        );
        var result = await InvokeRetirementSnippetAsync("cdc-retire", _fixture.Request.Binding.Generation);
        result.GetProperty("exitCode").GetInt32().Should().Be(0);
        result.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        result.GetProperty("data").GetProperty("succeeded").GetBoolean().Should().BeTrue();
        result.GetProperty("data").GetProperty("operationId").GetGuid().Should().Be(original.OperationId);
        result.GetProperty("data").GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        result.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        (await File.ReadAllTextAsync(_retirementSettingsPath, Token)).Should().Be(settings);
    }
}
