// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class SchemaRestampMigrationScenario
{
    internal sealed record Evidence(
        int RestampExitCode,
        string RestampError,
        int ProvisionExitCode,
        HttpStatusCode BeforeStampStatus,
        HttpStatusCode ExistingWidgetStatus,
        HttpStatusCode CreateStatus,
        JsonObject? ExistingWidget,
        JsonObject? CreatedWidget,
        string[] BeforeDocumentRow,
        string[] AfterDocumentRow
    );

    internal static async Task<Evidence> RunAsync(
        SqlDialect dialect,
        string dataStore,
        FixtureContext source,
        FixtureContext target,
        string connectionString,
        bool applyPhysicalMigration
    )
    {
        var sourceClient = CreateHost(source, connectionString, dataStore);
        string resourceLocation;
        Guid originalDocumentUuid;
        using (sourceClient)
        {
            using HttpResponseMessage created = await sourceClient.Client.PostAsJsonAsync(
                "/data/testproject/widgets",
                new { widgetId = 1, widgetName = "before-migration" }
            );
            string responseBody = await created.Content.ReadAsStringAsync();
            created.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
            resourceLocation =
                created.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("Widget POST did not return a Location header.");
            using HttpResponseMessage initialRead = await sourceClient.Client.GetAsync(resourceLocation);
            string initialBody = await initialRead.Content.ReadAsStringAsync();
            initialRead.StatusCode.Should().Be(HttpStatusCode.OK, initialBody);
            JsonObject initialWidget = JsonNode.Parse(initialBody)!.AsObject();
            originalDocumentUuid = Guid.Parse(initialWidget["id"]!.GetValue<string>());
        }
        string[] beforeDocumentRow = await CaptureDocumentRowAsync(
            connectionString,
            dialect,
            originalDocumentUuid
        );

        var targetBeforeStamp = CreateHost(target, connectionString, dataStore);
        HttpStatusCode beforeStampStatus;
        using (targetBeforeStamp)
        using (HttpResponseMessage response = await targetBeforeStamp.Client.GetAsync(resourceLocation))
        {
            beforeStampStatus = response.StatusCode;
        }

        if (applyPhysicalMigration)
        {
            string migrationPath = Path.Combine(
                FixtureRepositoryPaths.ResolveFixtureDirectory(FixtureKey.SchemaRestampSource),
                $"../migration.{(dialect == SqlDialect.Pgsql ? "pgsql" : "mssql")}.sql"
            );
            await ExecuteSqlFileAsync(connectionString, dialect, migrationPath);
        }

        string targetSchema = Path.Combine(target.ApiSchemaDirectory, "inputs", "api-schema.json");
        string dialectName = dialect == SqlDialect.Pgsql ? "pgsql" : "mssql";
        ProcessResult restamp = await RunToolAsync(
            "ddl",
            "re-stamp",
            "--schema",
            targetSchema,
            "--connection-string",
            connectionString,
            "--dialect",
            dialectName,
            "--migration-completed"
        );
        ProcessResult provision;
        if (applyPhysicalMigration)
        {
            provision = await RunToolAsync(
                "ddl",
                "provision",
                "--schema",
                targetSchema,
                "--connection-string",
                connectionString,
                "--dialect",
                dialectName
            );
        }
        else
        {
            provision = new ProcessResult(-1, string.Empty);
        }

        var freshTarget = CreateHost(target, connectionString, dataStore);
        using (freshTarget)
        {
            HttpStatusCode existingStatus;
            JsonObject? existingWidget = null;
            using (HttpResponseMessage existing = await freshTarget.Client.GetAsync(resourceLocation))
            {
                existingStatus = existing.StatusCode;
                if (existing.IsSuccessStatusCode)
                {
                    existingWidget = JsonNode.Parse(await existing.Content.ReadAsStringAsync())!.AsObject();
                }
            }

            using HttpResponseMessage create = await freshTarget.Client.PostAsJsonAsync(
                "/data/testproject/widgets",
                new
                {
                    widgetId = 2,
                    widgetName = "after-migration",
                    widgetNote = "migration-proof",
                }
            );
            JsonObject? createdWidget = null;
            if (create.IsSuccessStatusCode && create.Headers.Location is { } createdLocation)
            {
                using HttpResponseMessage createdRead = await freshTarget.Client.GetAsync(createdLocation);
                if (createdRead.IsSuccessStatusCode)
                {
                    createdWidget = JsonNode.Parse(await createdRead.Content.ReadAsStringAsync())!.AsObject();
                }
            }
            string[] afterDocumentRow = beforeDocumentRow;
            if (existingWidget is not null)
            {
                Guid documentUuid = Guid.Parse(existingWidget["id"]!.GetValue<string>());
                afterDocumentRow = await CaptureDocumentRowAsync(connectionString, dialect, documentUuid);
            }

            return new Evidence(
                restamp.ExitCode,
                restamp.Error,
                provision.ExitCode,
                beforeStampStatus,
                existingStatus,
                create.StatusCode,
                existingWidget,
                createdWidget,
                beforeDocumentRow,
                afterDocumentRow
            );
        }
    }

    private sealed class ApiHost(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string startupStatusFilePath
    ) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
            if (File.Exists(startupStatusFilePath))
            {
                File.Delete(startupStatusFilePath);
            }
        }
    }

    private static ApiHost CreateHost(FixtureContext fixture, string connectionString, string dataStore)
    {
        string startupPath = Path.Combine(Path.GetTempPath(), $"schema-restamp-{Guid.NewGuid():N}.json");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:UseApiSchemaPath", "true");
            builder.UseSetting("AppSettings:ApiSchemaPath", fixture.ApiSchemaDirectory);
            builder.UseSetting("AppSettings:StartupStatusFilePath", startupPath);
            builder.UseSetting("AppSettings:Datastore", dataStore);
            builder.UseSetting("AppSettings:BypassAuthorization", "true");
            builder.UseSetting("JwtAuthentication:Authority", FakeOidcConfigurationManager.Issuer);
            builder.UseSetting("ConfigurationServiceSettings:BaseUrl", "http://localhost/test-cms");
            builder.UseSetting("ConfigurationServiceSettings:ClientId", "test-cms-client");
            builder.UseSetting("ConfigurationServiceSettings:ClientSecret", "test-cms-secret");
            builder.UseSetting("ConfigurationServiceSettings:Scope", "edfi_admin_api/full_access");
            builder.ConfigureServices(services =>
                ExternalDoublesRegistration.RegisterAll(
                    services,
                    fixture,
                    connectionString,
                    new AllowAllClaimSetProvider(fixture),
                    []
                )
            );
        });
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ExternalDoublesConstants.SmokeToken
        );
        return new ApiHost(factory, client, startupPath);
    }

    private static async Task ExecuteSqlFileAsync(string connectionString, SqlDialect dialect, string path)
    {
        await using DbConnection connection =
            dialect == SqlDialect.Pgsql
                ? new global::Npgsql.NpgsqlConnection(connectionString)
                : new global::Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(path);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> CaptureDocumentRowAsync(
        string connectionString,
        SqlDialect dialect,
        Guid documentUuid
    )
    {
        await using DbConnection connection =
            dialect == SqlDialect.Pgsql
                ? new global::Npgsql.NpgsqlConnection(connectionString)
                : new global::Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            dialect == SqlDialect.Pgsql
                ? "SELECT * FROM dms.\"Document\" WHERE \"DocumentUuid\" = @documentUuid"
                : "SELECT * FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "documentUuid";
        parameter.Value = documentUuid;
        command.Parameters.Add(parameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The migrated Widget document was not found.");
        }
        return Enumerable
            .Range(0, reader.FieldCount)
            .Select(index =>
                reader.GetValue(index) is byte[] bytes
                    ? Convert.ToHexString(bytes)
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty
            )
            .ToArray();
    }

    private sealed record ProcessResult(int ExitCode, string Error);

    private static async Task<ProcessResult> RunToolAsync(params string[] arguments)
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string assemblyPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "clis",
                "EdFi.DataManagementService.SchemaTools",
                "bin",
                configuration,
                "net10.0",
                "api-schema-tools.dll"
            )
        );
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start api-schema-tools.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await WaitForToolExitAsync(process, deadline.Token);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(deadline.Token);
            return new ProcessResult(process.ExitCode, await errorTask);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Give redirected pipes a bounded opportunity to drain after process termination.
            try
            {
                await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException) { }

            string output = outputTask.IsCompletedSuccessfully ? outputTask.Result : "(not captured)";
            string error = errorTask.IsCompletedSuccessfully ? errorTask.Result : "(not captured)";
            for (int index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] is "--connection-string" or "-c")
                {
                    output = output.Replace(arguments[index + 1], "[redacted]", StringComparison.Ordinal);
                    error = error.Replace(arguments[index + 1], "[redacted]", StringComparison.Ordinal);
                }
            }
            throw new TimeoutException(
                $"api-schema-tools exceeded its five-minute process deadline.\nstdout: {output}\nstderr: {error}"
            );
        }
    }

    internal static async Task WaitForToolExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The child may have exited between cancellation and termination.
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }
    }
}
