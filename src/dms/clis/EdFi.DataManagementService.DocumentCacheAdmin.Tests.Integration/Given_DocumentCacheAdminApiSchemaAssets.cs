// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[Category("ApiSchema")]
public sealed class Given_DocumentCacheAdminApiSchemaAssets
{
    private string _repositoryRoot = null!;
    private string _toolOutputDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = FixturePathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        _toolOutputDirectory = Path.Combine(
            _repositoryRoot,
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "bin",
            CurrentBuildConfiguration(),
            "net10.0"
        );
    }

    [Test]
    public void It_builds_the_default_bundled_api_schema_assets()
    {
        string manifestPath = Path.Combine(
            _toolOutputDirectory,
            "ApiSchema",
            "bootstrap-api-schema-manifest.json"
        );
        File.Exists(manifestPath).Should().BeTrue("the CLI output must carry the bootstrap manifest");

        JsonObject manifest = ReadJsonObject(manifestPath);
        JsonArray projects = manifest["projects"]!.AsArray();
        projects.Should().HaveCount(2);

        AssertBundledSchemaProject(projects, "EdFi.DataStandard52.ApiSchema", "Ed-Fi", "ed-fi", false);
        AssertBundledSchemaProject(projects, "EdFi.DataStandard52.TPDM.ApiSchema", "TPDM", "tpdm", true);

        File.Exists(
                Path.Combine(
                    _toolOutputDirectory,
                    "ApiSchema",
                    "Packages",
                    "EdFi.DataStandard52.ApiSchema",
                    "ApiSchema.json"
                )
            )
            .Should()
            .BeTrue();
        File.Exists(
                Path.Combine(
                    _toolOutputDirectory,
                    "ApiSchema",
                    "Packages",
                    "EdFi.DataStandard52.TPDM.ApiSchema",
                    "ApiSchema.json"
                )
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public async Task It_loads_the_default_bundled_api_schema_before_reporting_downstream_status()
    {
        string toolAssemblyPath = Path.Combine(
            _toolOutputDirectory,
            $"{DocumentCacheAdminCliConstants.ToolCommandName}.dll"
        );
        DocumentCacheAdminCliProcessResult result = await RunProcessAsync(
            "dotnet",
            [
                toolAssemblyPath,
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                DocumentCacheAdminCommandSurface.JsonOptionName,
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
                "1",
                DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
                "0.1",
            ],
            ConfigureDefaultBundledSchemaEnvironment
        );

        result
            .ExitCode.Should()
            .Be(
                DocumentCacheAdminExitCodes.Success,
                "stderr:\n{0}\nstdout:\n{1}",
                result.StandardError,
                result.StandardOutput
            );
        result.StandardError.Should().NotContain("ApiSchema");
        result.StandardOutput.Should().NotContain("bootstrap-api-schema-manifest");

        JsonObject root = result.ReadStandardOutputJsonObject();
        JsonObject targetStatus = root["targets"]!.AsArray()[0]!.AsObject();
        targetStatus["resolution"]!["status"]!.GetValue<string>().Should().Be("unresolved");
        targetStatus["resolution"]!["reason"]!.GetValue<string>().Should().Be("cmsUnavailable");
    }

    private static void AssertBundledSchemaProject(
        JsonArray projects,
        string packageId,
        string projectName,
        string projectEndpointName,
        bool isExtensionProject
    )
    {
        JsonObject project = projects
            .Select(node => node!.AsObject())
            .Single(node => node["schemaPath"]!.GetValue<string>() == $"Packages/{packageId}/ApiSchema.json");

        project["projectName"]!.GetValue<string>().Should().Be(projectName);
        project["projectEndpointName"]!.GetValue<string>().Should().Be(projectEndpointName);
        project["isExtensionProject"]!.GetValue<bool>().Should().Be(isExtensionProject);

        project["schemaPath"]!.GetValue<string>().Should().Be($"Packages/{packageId}/ApiSchema.json");
    }

    private static JsonObject ReadJsonObject(string path)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{path}' to contain a JSON object.");
    }

    private static string CurrentBuildConfiguration()
    {
        var targetFrameworkDirectory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
        );
        return targetFrameworkDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Unable to determine current build configuration.");
    }

    private static async Task<DocumentCacheAdminCliProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<IDictionary<string, string?>>? configureEnvironment = null
    )
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        configureEnvironment?.Invoke(process.StartInfo.Environment);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{fileName}'.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static void ConfigureDefaultBundledSchemaEnvironment(IDictionary<string, string?> environment)
    {
        environment["DOTNET_ENVIRONMENT"] = string.Empty;
        environment["ASPNETCORE_ENVIRONMENT"] = string.Empty;
        environment["AppSettings__AllowIdentityUpdateOverrides"] = string.Empty;
        environment["AppSettings__MaximumPageSize"] = "500";
        environment["AppSettings__DefaultPartitionCount"] = "10";
        environment["AppSettings__BypassAuthorization"] = "true";
        environment["AppSettings__UseApiSchemaPath"] = "false";
        environment.Remove("AppSettings__ApiSchemaPath");
        environment.Remove("ConfigurationServiceSettings__BaseUrl");
        environment.Remove("ConfigurationServiceSettings__ClientId");
        environment.Remove("ConfigurationServiceSettings__ClientSecret");
        environment.Remove("ConfigurationServiceSettings__Scope");
        environment.Remove("ConfigurationServiceSettings__EncryptionKey");
    }
}
