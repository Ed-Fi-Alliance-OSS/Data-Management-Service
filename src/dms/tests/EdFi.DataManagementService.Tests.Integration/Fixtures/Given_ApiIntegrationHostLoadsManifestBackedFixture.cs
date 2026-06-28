// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

public sealed class Given_ApiIntegrationHostLoadsManifestBackedFixture
{
    private WebApplicationFactory<Program>? _factory;
    private string? _workspace;
    private string? _startupStatusFilePath;

    [TearDown]
    public async Task TearDown()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        if (_workspace is not null && Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
            _workspace = null;
        }

        if (_startupStatusFilePath is not null && File.Exists(_startupStatusFilePath))
        {
            File.Delete(_startupStatusFilePath);
            _startupStatusFilePath = null;
        }
    }

    [Test]
    public async Task It_starts_from_the_bootstrap_manifest_and_ignores_loose_ApiSchema_files()
    {
        FixtureContext sourceFixture = FixtureContextLoader.Load(FixtureKey.ProfileRootOnlyMerge);
        _workspace = CopyApiSchemaWorkspace(sourceFixture.ApiSchemaDirectory);
        WriteLooseStaleCoreApiSchema(_workspace);

        FixtureContext hostFixture = sourceFixture with { ApiSchemaDirectory = _workspace };
        _startupStatusFilePath = Path.Combine(
            Path.GetTempPath(),
            $"api-int-manifest-backed-startup-{Guid.NewGuid():N}.json"
        );

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            builder.UseSetting("AppSettings:UseRelationalBackend", "false");
            builder.UseSetting("AppSettings:UseApiSchemaPath", "true");
            builder.UseSetting("AppSettings:ApiSchemaPath", hostFixture.ApiSchemaDirectory);
            builder.UseSetting("AppSettings:StartupStatusFilePath", _startupStatusFilePath);
            builder.UseSetting("AppSettings:Datastore", "postgresql");
            builder.UseSetting("AppSettings:BypassAuthorization", "true");
            builder.UseSetting("ConfigurationServiceSettings:BaseUrl", "http://localhost/test-cms");
            builder.UseSetting("ConfigurationServiceSettings:ClientId", "test-cms-client");
            builder.UseSetting("ConfigurationServiceSettings:ClientSecret", "test-cms-secret");
            builder.UseSetting("ConfigurationServiceSettings:Scope", "edfi_admin_api/full_access");

            builder.ConfigureServices(services =>
                ExternalDoublesRegistration.RegisterAll(
                    services,
                    hostFixture,
                    leasedConnectionString: "Host=localhost;Database=unused;Username=unused;Password=unused",
                    new AllowAllClaimSetProvider(hostFixture),
                    clientEducationOrganizationIds: []
                )
            );
        });

        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        IEffectiveSchemaSetProvider effectiveSchemaSetProvider =
            _factory.Services.GetRequiredService<IEffectiveSchemaSetProvider>();
        effectiveSchemaSetProvider.IsInitialized.Should().BeTrue();
        effectiveSchemaSetProvider
            .EffectiveSchemaSet.ProjectsInEndpointOrder.Select(project => project.ProjectEndpointName)
            .Should()
            .Equal("ed-fi");

        JsonObject startupStatus = ReadJsonObject(_startupStatusFilePath);
        startupStatus["State"]?.GetValue<string>().Should().Be("Ready");
    }

    private static string CopyApiSchemaWorkspace(string sourceDirectory)
    {
        string destinationDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-api-integration-fixture-host",
            Guid.NewGuid().ToString("N")
        );

        foreach (
            string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
        )
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return destinationDirectory;
    }

    private static void WriteLooseStaleCoreApiSchema(string workspace)
    {
        string selectedSchemaPath = Path.Combine(workspace, "inputs", "ApiSchema.json");
        JsonNode staleRoot =
            JsonNode.Parse(File.ReadAllText(selectedSchemaPath))
            ?? throw new InvalidOperationException($"ApiSchema file '{selectedSchemaPath}' parsed to null.");

        JsonObject staleProjectSchema =
            staleRoot["projectSchema"]?.AsObject()
            ?? throw new InvalidOperationException(
                $"ApiSchema file '{selectedSchemaPath}' is missing projectSchema."
            );

        staleProjectSchema["projectName"] = "Stale";
        staleProjectSchema["projectEndpointName"] = "stale";
        staleProjectSchema["isExtensionProject"] = false;

        File.WriteAllText(Path.Combine(workspace, "ApiSchema-Stale.json"), staleRoot.ToJsonString());
    }

    private static JsonObject ReadJsonObject(string path)
    {
        JsonNode node =
            JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"JSON document at '{path}' parsed to null.");

        return node.AsObject();
    }
}
