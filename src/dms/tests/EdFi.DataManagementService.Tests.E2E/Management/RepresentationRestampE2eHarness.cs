// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

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

        await RunWithDirectoryCleanupAsync(
            schemaCopyDirectory,
            async () =>
            {
                var projectionExpected = false;
                await RunProcessAsync(
                    "docker",
                    ["cp", $"{DmsContainerName}:/app/ApiSchema", schemaCopyDirectory]
                );
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
                        DocumentCacheAdminTestConfigurationService.Start(
                            target,
                            ConfigurationServiceEncryptionKey
                        );
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
                    long restampedContentVersion = await ReadCanonicalContentVersionAsync(documentUuid);
                    (await ReadRequiredWorkVersionAsync(documentUuid)).Should().Be(restampedContentVersion);
                    await DrainOrdinaryProjectorAsync(target);
                    projectionExpected = true;
                }
                finally
                {
                    await RunProcessAsync("docker", ["start", DmsContainerName]);
                    await WaitForDmsAsync();
                    if (projectionExpected)
                    {
                        await WaitForProjectedCacheAsync(documentUuid);
                    }
                }
            }
        );
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

    private static async Task DrainOrdinaryProjectorAsync(DocumentCacheAdminCliTarget target)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await using ServiceProvider serviceProvider = await CreateProjectionServiceProviderAsync(
                target.AppSettingsDatastore,
                target.ApiSchemaDirectory,
                timeoutSource.Token
            );
            DocumentCacheTargetExecutionContext executionContext = new(
                DocumentCacheTargetKey.Create(target.TenantKey, target.DataStoreId),
                new DocumentCacheTargetContextGeneration(1),
                new DocumentCacheTargetEffectiveSettings(
                    true,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(10),
                    10,
                    1,
                    TimeSpan.FromSeconds(1),
                    1000,
                    TimeSpan.FromMinutes(1)
                ),
                new DocumentCacheTargetDataStoreMetadata(target.DataStoreId, target.AppSettingsDatastore),
                new DocumentCacheTargetConnectionInput(target.ProviderToken, target.ConnectionString),
                new DocumentCachePhysicalSourceFingerprint(
                    "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                ),
                new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
                new DocumentCacheInventoryValidationResult(
                    DocumentCacheInventoryStatus.Satisfied,
                    "Inventory satisfied."
                ),
                new DocumentCacheEnqueueTriggerValidationResult(
                    DocumentCacheEnqueueTriggerStatus.Satisfied,
                    "Enqueue trigger satisfied."
                ),
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );
            await using DocumentCacheProjectionTargetRuntimeContext context = await serviceProvider
                .GetRequiredService<IDocumentCacheProjectionTargetRuntimeContextFactory>()
                .CreateAsync(executionContext, timeoutSource.Token);
            IDocumentCacheProjectionDrainPageProcessor processor =
                serviceProvider.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();

            while (true)
            {
                DocumentCacheProjectionDrainPageResult result = await processor.ProcessPageAsync(
                    new DocumentCacheProjectionDrainPageRequest(
                        context,
                        DocumentCacheProjectionDrainInvocationKind.Ordinary
                    ),
                    timeoutSource.Token
                );
                if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
                {
                    return;
                }

                result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
            }
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out draining the ordinary projector for target '{target.TenantKey}':{target.DataStoreId}.",
                exception
            );
        }
    }

    internal static async Task<ServiceProvider> CreateProjectionServiceProviderAsync(
        string appSettingsDatastore,
        string apiSchemaDirectory,
        CancellationToken cancellationToken
    )
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = appSettingsDatastore,
                    ["AppSettings:UseApiSchemaPath"] = "true",
                    ["AppSettings:ApiSchemaPath"] = apiSchemaDirectory,
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services
            .AddOptions<EdFi.DataManagementService.Core.Configuration.AppSettings>()
            .Bind(configuration.GetSection("AppSettings"));
        services.AddDmsDefaultConfiguration(
            Log.Logger,
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            maskRequestBodyInLogs: false
        );
        services.AddSingleton<IOptions<DocumentCacheOptions>>(Options.Create(new DocumentCacheOptions()));
        services.AddSingleton(
            new DeadlockRetrySettings
            {
                MaxRetryAttempts = 0,
                BaseDelayMilliseconds = 1,
                UseJitter = false,
            }
        );
        services.AddPostgresqlDocumentCacheRuntimeServices(configuration);
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        try
        {
            await serviceProvider
                .GetRequiredService<IEffectiveSchemaBootstrapper>()
                .InitializeAsync(cancellationToken);
            return serviceProvider;
        }
        catch
        {
            await serviceProvider.DisposeAsync();
            throw;
        }
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

    private static async Task<long> ReadCanonicalContentVersionAsync(Guid documentUuid)
    {
        await using var connection = new NpgsqlConnection(AppSettings.DataStoreAdminConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ContentVersion"
            FROM dms."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """;
        command.Parameters.AddWithValue("documentUuid", documentUuid);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadRequiredWorkVersionAsync(Guid documentUuid)
    {
        await using var connection = new NpgsqlConnection(AppSettings.DataStoreAdminConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT work."RequiredContentVersion"
            FROM dms."DocumentProjectionWork" AS work
            INNER JOIN dms."Document" AS document ON document."DocumentId" = work."DocumentId"
            WHERE document."DocumentUuid" = @documentUuid;
            """;
        command.Parameters.AddWithValue("documentUuid", documentUuid);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForProjectedCacheAsync(Guid documentUuid)
    {
        await WaitForConditionAsync(
            async cancellationToken =>
            {
                await using var connection = new NpgsqlConnection(AppSettings.DataStoreAdminConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1
                        FROM dms."Document" AS document
                        INNER JOIN dms."DocumentCache" AS cache
                            ON cache."DocumentId" = document."DocumentId"
                        WHERE document."DocumentUuid" = @documentUuid
                          AND cache."ContentVersion" = document."ContentVersion"
                          AND NOT EXISTS (
                              SELECT 1
                              FROM dms."DocumentProjectionWork" AS work
                              WHERE work."DocumentId" = document."DocumentId"
                          )
                    );
                    """;
                command.Parameters.AddWithValue("documentUuid", documentUuid);
                return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
            },
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(2),
            $"representation-restamp projection for document {documentUuid}: projection work to be absent and the document-cache content version to equal the canonical content version"
        );
    }

    internal static async Task WaitForConditionAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string timeoutContext
    )
    {
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
            while (true)
            {
                if (await condition(timeoutSource.Token))
                {
                    return;
                }

                await Task.Delay(pollInterval, timeoutSource.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout} waiting for {timeoutContext}.", exception);
        }
    }

    internal static async Task RunWithDirectoryCleanupAsync(string directory, Func<Task> action)
    {
        try
        {
            await action();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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

    private static string RepositoryRoot() => RepositoryRoot(new DirectoryInfo(AppContext.BaseDirectory));

    internal static string RepositoryRoot(DirectoryInfo startDirectory)
    {
        DirectoryInfo? directory = startDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
