// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

public sealed partial class DocumentCacheHostedHappyPathTests
{
    private const string HostedProjectorDefaultPollInterval = "00:00:01";
    private const string HostedProjectorOutagePollInterval = "00:10:00";

    [Test]
    [Category("DocumentCacheHostedProjectorResume")]
    [Category("DocumentCacheProjectorResume")]
    public async Task It_resumes_projection_from_queued_work_after_projector_outage()
    {
        await RegisterSystemAdministratorAsync();
        int dataStoreId = await GetConfiguredDataStoreIdAsync();
        dataStoreId
            .Should()
            .Be(
                TargetDataStoreId,
                "the focused DocumentCache E2E overlay target must match the provisioned CMS data store"
            );

        ClientCredentials credentials = await CreateClientCredentialsForDataStoreAsync(dataStoreId);
        string dmsToken = string.Empty;
        bool restoredDefaultPollInterval = false;

        try
        {
            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorOutagePollInterval);
            dmsToken = await GetDmsTokenAsync(credentials);
            SetDmsBearerToken(dmsToken);
            await AssertStudentRouteRemainsAvailableAsync();

            JsonObject activation = await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
                dataStoreId,
                [
                    "activate-new-empty",
                    "--data-store-id",
                    dataStoreId.ToString(CultureInfo.InvariantCulture),
                    "--confirm",
                    "newEmptyActivation",
                    "--json",
                    "--datastore",
                    CliDatastoreOptionValue(),
                    "--command-timeout-seconds",
                    "90",
                ]
            );
            activation["status"]!.GetValue<string>().Should().Be("completed");
            activation["classification"]!.GetValue<string>().Should().Be("succeeded");

            JsonObject longPollTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(longPollTarget, dataStoreId);
            ReadDouble(longPollTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(600);

            List<HostedOutageStudent> students = [];
            for (int index = 1; index <= 3; index++)
            {
                string studentUniqueId = $"doc-cache-outage-{Guid.NewGuid():N}"[..32];
                Guid studentId = await PostStudentAsync(studentUniqueId, $"Outage Create {index}");
                string updatedFirstName = $"Outage Update {index}";
                await PutStudentAsync(studentId, studentUniqueId, updatedFirstName);
                students.Add(new HostedOutageStudent(studentId, studentUniqueId, updatedFirstName));
            }

            IReadOnlyList<DocumentCacheQueuedState> queuedStates = await WaitForQueuedProjectionWorkAsync(
                students.Select(student => student.Id).ToArray()
            );
            queuedStates.Should().HaveCount(students.Count);
            queuedStates
                .Select(state => state.WorkRequiredContentVersion)
                .Should()
                .OnlyContain(requiredContentVersion => requiredContentVersion.HasValue);
            queuedStates
                .Should()
                .OnlyContain(state =>
                    state.WorkRequiredContentVersion == state.DocumentContentVersion
                    && !state.CacheContentVersion.HasValue
                );

            JsonObject backlogStatus = await GetDocumentCacheStatusAsync();
            JsonObject backlogTarget = TargetByDataStoreId(backlogStatus, dataStoreId);
            ReadString(backlogTarget, "lifecycle", "state").Should().Be("tracking");
            ReadString(backlogTarget, "queueSummary", "presence").Should().Be("notEmpty");
            ReadString(backlogTarget, "caughtUp", "status").Should().Be("notCaughtUp");

            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
            restoredDefaultPollInterval = true;
            SetDmsBearerToken(dmsToken);

            JsonObject caughtUpTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(caughtUpTarget, dataStoreId);
            ReadDouble(caughtUpTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(1);

            foreach (HostedOutageStudent student in students)
            {
                DocumentCacheProjection projection = await ReadDocumentCacheProjectionAsync(student.Id);
                projection.CacheContentVersion.Should().Be(projection.DocumentContentVersion);
                projection.WorkRows.Should().Be(0);
                projection.DocumentJson.Should().Contain(student.UniqueId);
                projection.DocumentJson.Should().Contain(student.ExpectedFirstName);

                await AssertGetByIdAsync(student.Id, student.UniqueId, student.ExpectedFirstName);
                await AssertGetManyAsync(student.UniqueId, student.ExpectedFirstName);
            }
        }
        finally
        {
            if (!restoredDefaultPollInterval)
            {
                await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
                if (!string.IsNullOrWhiteSpace(dmsToken))
                {
                    SetDmsBearerToken(dmsToken);
                }
            }
        }
    }

    private void SetDmsBearerToken(string dmsToken)
    {
        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dmsToken);
    }

    private async Task AssertStudentRouteRemainsAvailableAsync()
    {
        using HttpResponseMessage response = await _dmsClient.GetAsync($"{StudentResourcePath}?limit=1");
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET students availability check failed: {body}");
    }

    private async Task RestartDmsWithProjectorPollIntervalAsync(string pollInterval)
    {
        await RestartDmsWithDocumentCacheEnvironmentAsync(
            new Dictionary<string, string> { ["DMS_DOCUMENTCACHE_PROJECTOR_POLL_INTERVAL"] = pollInterval },
            $"restart DMS with DocumentCache projector poll interval {pollInterval}"
        );
    }

    private async Task RestartDmsWithDocumentCacheTargetDataStoreIdAsync(int targetDataStoreId)
    {
        await RestartDmsWithDocumentCacheEnvironmentAsync(
            new Dictionary<string, string>
            {
                ["DMS_DOCUMENTCACHE_TARGET_DATA_STORE_ID"] = targetDataStoreId.ToString(
                    CultureInfo.InvariantCulture
                ),
                ["DMS_DOCUMENTCACHE_PROJECTOR_POLL_INTERVAL"] = HostedProjectorDefaultPollInterval,
            },
            $"restart DMS with DocumentCache target data store id {targetDataStoreId}"
        );
    }

    private async Task RestartDmsWithDocumentCacheEnvironmentAsync(
        IReadOnlyDictionary<string, string> environmentOverrides,
        string description
    )
    {
        string environmentFile = CreateDmsEnvironmentFile(environmentOverrides);
        string dockerComposeDirectory = Path.Combine(_repositoryRoot, "eng", "docker-compose");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dockerComposeDirectory,
        };
        foreach (string argument in ComposeDmsOnlyRestartArguments(environmentFile))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in environmentOverrides)
        {
            process.StartInfo.Environment[key] = value;
        }

        await RunProcessAndAssertSuccessAsync(process, TimeSpan.FromSeconds(180), description);
        RecreateDmsClient();
        await WaitForDmsHealthAsync();
    }

    private static IReadOnlyList<string> ComposeDmsOnlyRestartArguments(string environmentFile)
    {
        List<string> arguments =
        [
            "compose",
            "-f",
            IsMssql() ? "mssql.yml" : "postgresql.yml",
            "-f",
            "local-dms.yml",
            "-f",
            "local-dms-document-cache.yml",
        ];

        if (DmsDotnetDiagnosticsEnabled(environmentFile))
        {
            arguments.Add("-f");
            arguments.Add("local-dms-diagnostics.yml");
        }

        arguments.Add("-f");
        arguments.Add("local-config.yml");

        arguments.AddRange([
            "--env-file",
            environmentFile,
            "-p",
            "dms-local",
            "up",
            "--detach",
            "--no-deps",
            "--force-recreate",
            "dms",
        ]);

        return arguments;
    }

    private string CreateDmsEnvironmentFile(IReadOnlyDictionary<string, string> environmentOverrides)
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-document-cache-e2e",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempDirectory);
        _tempDirectories.Add(tempDirectory);

        string baseEnvironmentFile = E2EEnvironmentFilePath();
        string[] environmentLines = File.ReadAllLines(baseEnvironmentFile);
        List<string> updatedEnvironmentLines = [.. environmentLines];
        foreach ((string key, string value) in environmentOverrides)
        {
            UpsertEnvironmentValue(updatedEnvironmentLines, key, value);
        }

        string environmentFile = Path.Combine(tempDirectory, ".env.e2e");
        File.WriteAllLines(environmentFile, updatedEnvironmentLines);
        return environmentFile;
    }

    private void RecreateDmsClient()
    {
        _dmsClient.Dispose();
        _dmsClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private async Task WaitForDmsHealthAsync()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        Exception? lastException = null;
        string lastBody = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await _dmsClient.GetAsync("health");
                lastBody = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }
            catch (TaskCanceledException exception)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new AssertionException(
            $"DMS health did not recover after restart. Last body: {lastBody}. Last exception: {lastException?.Message}"
        );
    }

    private static async Task RunProcessAndAssertSuccessAsync(
        Process process,
        TimeSpan timeout,
        string description
    )
    {
        process.Start().Should().BeTrue($"{description} must start");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using var cancellationSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Assert.Fail($"{description} timed out.");
        }

        string output = await standardOutput;
        string error = await standardError;
        process.ExitCode.Should().Be(0, "{0} failed. stderr:\n{1}\nstdout:\n{2}", description, error, output);
    }

    private static async Task<IReadOnlyList<DocumentCacheQueuedState>> WaitForQueuedProjectionWorkAsync(
        IReadOnlyList<Guid> documentUuids
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<DocumentCacheQueuedState> lastStates = [];

        while (DateTimeOffset.UtcNow < deadline)
        {
            lastStates = await ReadDocumentCacheQueuedStatesAsync(documentUuids);
            if (
                lastStates.Count == documentUuids.Count
                && lastStates.All(state =>
                    state.WorkRequiredContentVersion == state.DocumentContentVersion
                    && state.CacheContentVersion is null
                )
            )
            {
                return lastStates;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new AssertionException(
            $"DocumentCache work did not remain queued before projector resume. Last states: {string.Join("; ", lastStates)}"
        );
    }

    private static async Task<IReadOnlyList<DocumentCacheQueuedState>> ReadDocumentCacheQueuedStatesAsync(
        IReadOnlyList<Guid> documentUuids
    )
    {
        List<DocumentCacheQueuedState> states = [];
        foreach (Guid documentUuid in documentUuids)
        {
            states.Add(await ReadDocumentCacheQueuedStateAsync(documentUuid));
        }

        return states;
    }

    private static async Task<DocumentCacheQueuedState> ReadDocumentCacheQueuedStateAsync(Guid documentUuid)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT
                    d.[DocumentId],
                    d.[ContentVersion] AS [DocumentContentVersion],
                    c.[ContentVersion] AS [CacheContentVersion],
                    w.[RequiredContentVersion] AS [WorkRequiredContentVersion]
                FROM [dms].[Document] AS d
                LEFT JOIN [dms].[DocumentCache] AS c ON c.[DocumentId] = d.[DocumentId]
                LEFT JOIN [dms].[DocumentProjectionWork] AS w ON w.[DocumentId] = d.[DocumentId]
                WHERE d.[DocumentUuid] = @documentUuid;
                """
            : """
                SELECT
                    d."DocumentId",
                    d."ContentVersion" AS "DocumentContentVersion",
                    c."ContentVersion" AS "CacheContentVersion",
                    w."RequiredContentVersion" AS "WorkRequiredContentVersion"
                FROM "dms"."Document" AS d
                LEFT JOIN "dms"."DocumentCache" AS c ON c."DocumentId" = d."DocumentId"
                LEFT JOIN "dms"."DocumentProjectionWork" AS w ON w."DocumentId" = d."DocumentId"
                WHERE d."DocumentUuid" = @documentUuid;
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new AssertionException($"Document {documentUuid} was not found in dms.Document.");
        }

        return new DocumentCacheQueuedState(
            DocumentId: ReadInt64(reader, "DocumentId"),
            DocumentContentVersion: ReadInt64(reader, "DocumentContentVersion"),
            CacheContentVersion: ReadOptionalInt64(reader, "CacheContentVersion"),
            WorkRequiredContentVersion: ReadOptionalInt64(reader, "WorkRequiredContentVersion")
        );
    }

    private static long? ReadOptionalInt64(DbDataReader reader, string name)
    {
        object value = reader[name];
        return value is DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(JsonObject root, params string[] path) =>
        RequiredNode(root, path).GetValue<double>();

    private static void UpsertEnvironmentValue(List<string> lines, string name, string value)
    {
        string prefix = $"{name}=";
        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartsWith(prefix, StringComparison.Ordinal))
            {
                lines[index] = $"{name}={value}";
                return;
            }
        }

        lines.Add($"{name}={value}");
    }

    private static bool DmsDotnetDiagnosticsEnabled(string environmentFile) =>
        Array.Exists(
            File.ReadAllLines(environmentFile),
            line =>
                string.Equals(
                    line.Trim(),
                    "DMS_ENABLE_DOTNET_DIAGNOSTICS=true",
                    StringComparison.OrdinalIgnoreCase
                )
        );

    private sealed record HostedOutageStudent(Guid Id, string UniqueId, string ExpectedFirstName);

    private sealed record DocumentCacheQueuedState(
        long DocumentId,
        long DocumentContentVersion,
        long? CacheContentVersion,
        long? WorkRequiredContentVersion
    );
}
