// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.Management;

internal static class RepresentationRestampE2EHarness
{
    private const string DmsContainerName = "ed-fi-api";
    private const string ConfigurationServiceClientId = "CMSReadOnlyAccess";
    private const string ConfigurationServiceClientSecret = "ValidClientSecret1234567890!Abcd";
    private const string ConfigurationServiceScope = "edfi_admin_api/readonly_access";
    private const string ConfigurationServiceEncryptionKey = "secret!_32_chars_xxxxxxxxxxxxxxx";

    public static async Task ExecuteTrackingRestampAsync(Guid documentUuid)
    {
        string schemaCopyDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms1318-restamp-schema",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(schemaCopyDirectory);

        await RunProcessAsync("docker", ["cp", $"{DmsContainerName}:/app/ApiSchema", schemaCopyDirectory]);

        await RunProcessAsync("docker", ["stop", DmsContainerName]);

        try
        {
            await using DocumentCacheAdminCliTarget target =
                DocumentCacheAdminCliTarget.CreateExternalPostgresql(
                    AppSettings.DataStoreAdminConnectionString,
                    dataStoreId: 1,
                    Path.Combine(schemaCopyDirectory, "ApiSchema")
                );
            await SetTrackingLifecycleAsync();
            await using DocumentCacheAdminTestConfigurationService configurationService =
                DocumentCacheAdminTestConfigurationService.Start(target, ConfigurationServiceEncryptionKey);
            JsonObject preview = await RunCliAsync(
                "restamp-preview",
                target.DataStoreId,
                configurationService.BaseUri,
                target.ApiSchemaDirectory,
                [
                    "--mode",
                    "tracking",
                    "--reason",
                    "E2E observable representation restamp verification",
                    "--document-uuid",
                    documentUuid.ToString(),
                    "--offline-writer-admission",
                    "closedAndDrained",
                ]
            );
            Guid operationId = preview["result"]!["operationId"]!.GetValue<Guid>();

            JsonObject execute = await RunCliAsync(
                "restamp-execute",
                target.DataStoreId,
                configurationService.BaseUri,
                target.ApiSchemaDirectory,
                [
                    "--operation-id",
                    operationId.ToString(),
                    "--confirm",
                    "representationRestamp",
                    "--offline-writer-admission",
                    "closedAndDrained",
                ]
            );
            execute["result"]!["state"]!.GetValue<string>().Should().Be("completed");
            execute["result"]!["claimLevel"]!.GetValue<string>().Should().Be("projectionWorkQueued");
        }
        finally
        {
            await RunProcessAsync("docker", ["start", DmsContainerName]);
            await WaitForDmsAsync();
            Directory.Delete(schemaCopyDirectory, recursive: true);
        }
    }

    private static async Task SetTrackingLifecycleAsync()
    {
        await using var connection = new NpgsqlConnection(AppSettings.DataStoreAdminConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE dms.\"DocumentCacheState\" SET \"ProjectionLifecycleState\" = 'Tracking', \"CacheAheadRecoveryRequired\" = false WHERE \"StateId\" = 1";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonObject> RunCliAsync(
        string command,
        long dataStoreId,
        Uri configurationServiceBaseUri,
        string apiSchemaDirectory,
        IReadOnlyList<string> commandArguments
    )
    {
        List<string> arguments =
        [
            "run",
            "--project",
            ToolProjectPath(),
            "--no-build",
            "--",
            command,
            "--data-store-id",
            dataStoreId.ToString(),
        ];

        foreach (string commandArgument in commandArguments)
        {
            arguments.Add(commandArgument);
        }

        arguments.Add("--json");

        ProcessResult result = await RunProcessAsync(
            "dotnet",
            arguments,
            environment: new Dictionary<string, string>
            {
                ["AppSettings__Datastore"] = "postgresql",
                ["AppSettings__UseApiSchemaPath"] = "true",
                ["AppSettings__ApiSchemaPath"] = apiSchemaDirectory,
                ["ConfigurationServiceSettings__BaseUrl"] = configurationServiceBaseUri.ToString(),
                ["ConfigurationServiceSettings__ClientId"] = ConfigurationServiceClientId,
                ["ConfigurationServiceSettings__ClientSecret"] = ConfigurationServiceClientSecret,
                ["ConfigurationServiceSettings__Scope"] = ConfigurationServiceScope,
                ["ConfigurationServiceSettings__EncryptionKey"] = ConfigurationServiceEncryptionKey,
            }
        );

        result
            .ExitCode.Should()
            .Be(0, $"stdout: {result.StandardOutput}{Environment.NewLine}stderr: {result.StandardError}");
        return JsonNode.Parse(result.StandardOutput)!.AsObject();
    }

    private static async Task WaitForDmsAsync()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}"),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The restarted DMS process is not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }

        throw new TimeoutException("DMS did not become healthy after the representation restamp.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryRoot(),
            },
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start().Should().BeTrue();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new(process.ExitCode, await standardOutput, await standardError);
    }

    private static string ToolProjectPath() =>
        Path.Combine(
            RepositoryRoot(),
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
