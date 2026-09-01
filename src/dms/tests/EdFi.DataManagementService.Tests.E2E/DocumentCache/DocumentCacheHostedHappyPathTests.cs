// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

[TestFixture]
[NonParallelizable]
[Category("DocumentCacheHostedHappyPath")]
public sealed partial class DocumentCacheHostedHappyPathTests
{
    private const int TargetDataStoreId = 1;
    private const string LocalDevelopmentDataStoreName = "Local Development Data Store";
    private const string CmsReadOnlyClientId = "CMSReadOnlyAccess";
    private const string CmsReadOnlyClientSecret = "ValidClientSecret1234567890!Abcd";
    private const string CmsReadOnlyScope = "edfi_admin_api/readonly_access";
    private const string CmsEncryptionKey = "secret!_32_chars_xxxxxxxxxxxxxxx";
    private const string StudentResourcePath = "data/ed-fi/students";
    private const string SchoolResourcePath = "data/ed-fi/schools";
    private const string DocumentCacheStatusPath = "health/document-cache";
    private const string CacheOnlyStudentFirstNameSentinel = "cache-only hosted student sentinel";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly List<string> _tempDirectories = [];

    private string _repositoryRoot = string.Empty;
    private HttpClient _configurationServiceClient = null!;
    private HttpClient _dmsClient = null!;

    [SetUp]
    public async Task SetUp()
    {
        if (
            string.IsNullOrWhiteSpace(AppSettings.DataStoreAdminConnectionString)
            || string.IsNullOrWhiteSpace(AppSettings.DataStoreConnectionString)
        )
        {
            Assert.Ignore(
                "DocumentCache hosted E2E smoke requires build-dms.ps1 E2ETest so the test process receives the resolved E2E data-store connection strings."
            );
        }

        _repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        _configurationServiceClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _dmsClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        await ResetDocumentCacheDatabaseStateAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (!string.IsNullOrWhiteSpace(AppSettings.DataStoreAdminConnectionString))
        {
            try
            {
                await ResetDocumentCacheDatabaseStateAsync();
            }
            catch (Exception exception)
                when (exception
                        is InvalidOperationException
                            or DbException
                            or IOException
                            or UnauthorizedAccessException
                )
            {
                await TestContext.Error.WriteLineAsync(
                    $"DocumentCache hosted E2E cleanup failed: {exception.GetType().Name}: {exception.Message}"
                );
            }
        }

        if (_dmsClient is not null)
        {
            _dmsClient.Dispose();
        }

        if (_configurationServiceClient is not null)
        {
            _configurationServiceClient.Dispose();
        }

        foreach (string tempDirectory in _tempDirectories)
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Test]
    public async Task It_runs_the_hosted_projection_cache_status_smoke()
    {
        await RegisterSystemAdministratorAsync();
        int dataStoreId = await GetConfiguredDataStoreIdAsync();
        dataStoreId
            .Should()
            .Be(
                TargetDataStoreId,
                "the DocumentCache hosted E2E target configured in .env.e2e must match the provisioned CMS data store"
            );

        ClientCredentials credentials = await CreateClientCredentialsForDataStoreAsync(dataStoreId);
        string dmsToken = await GetDmsTokenAsync(credentials);
        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dmsToken);

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
        activation["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        activation["status"]!.GetValue<string>().Should().Be("completed");
        activation["classification"]!.GetValue<string>().Should().Be("succeeded");
        activation["lifecycle"]!.GetValue<string>().Should().Be("tracking");

        JsonObject emptyCaughtUpTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
        AssertTargetIsHostedReadAccelerationTarget(emptyCaughtUpTarget, dataStoreId);

        string studentUniqueId = $"doc-cache-{Guid.NewGuid():N}"[..32];
        Guid studentId = await PostStudentAsync(studentUniqueId, firstName: "Hosted Create");

        await AssertGetByIdAsync(studentId, studentUniqueId, expectedFirstName: "Hosted Create");
        await AssertGetManyAsync(studentUniqueId, expectedFirstName: "Hosted Create");

        await PutStudentAsync(studentId, studentUniqueId, firstName: "Hosted Update");
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        DocumentCacheProjection projection = await ReadDocumentCacheProjectionAsync(studentId);
        projection.CacheContentVersion.Should().Be(projection.DocumentContentVersion);
        projection.WorkRows.Should().Be(0);
        projection.ResourceName.Should().Be("Student");
        projection.DocumentJson.Should().Contain(studentUniqueId);
        projection.DocumentJson.Should().Contain("Hosted Update");
        projection.DocumentJson.Should().NotContain(CacheOnlyStudentFirstNameSentinel);

        await AssertGetByIdAsync(studentId, studentUniqueId, expectedFirstName: "Hosted Update");
        await AssertGetManyAsync(studentUniqueId, expectedFirstName: "Hosted Update");
        await AssertCanonicalStudentFirstNameAsync(studentId, expectedFirstName: "Hosted Update");

        DocumentCacheSentinel sentinel = await ApplyDocumentCacheSentinelAsync(
            studentId,
            documentJson => documentJson["firstName"] = CacheOnlyStudentFirstNameSentinel
        );
        try
        {
            await AssertCanonicalStudentFirstNameAsync(studentId, expectedFirstName: "Hosted Update");
            await AssertGetByIdAsync(
                studentId,
                studentUniqueId,
                expectedFirstName: CacheOnlyStudentFirstNameSentinel
            );
            await AssertGetManyAsync(studentUniqueId, expectedFirstName: CacheOnlyStudentFirstNameSentinel);
            await AssertCanonicalStudentFirstNameAsync(studentId, expectedFirstName: "Hosted Update");
        }
        finally
        {
            await RestoreDocumentCacheSentinelAsync(sentinel);
        }

        await AssertGetByIdAsync(studentId, studentUniqueId, expectedFirstName: "Hosted Update");
        await AssertGetManyAsync(studentUniqueId, expectedFirstName: "Hosted Update");

        await DeleteStudentAsync(studentId);
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
        await AssertDocumentCacheRowsDeletedAsync(studentId);
    }

    private async Task RegisterSystemAdministratorAsync()
    {
        string clientId = $"DocumentCacheE2E{Guid.NewGuid():N}";
        await SystemAdministrator.Register(clientId, SystemAdministrator.DefaultClientSecret);
        string token = await SystemAdministrator.GetToken();
        _configurationServiceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token
        );
    }

    private async Task<int> GetConfiguredDataStoreIdAsync()
    {
        using HttpResponseMessage response = await _configurationServiceClient.GetAsync("v3/dataStores/");
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"CMS data-store query failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement dataStore = document
            .RootElement.EnumerateArray()
            .Single(dataStore =>
                string.Equals(
                    dataStore.GetProperty("name").GetString(),
                    LocalDevelopmentDataStoreName,
                    StringComparison.Ordinal
                ) && !dataStore.GetProperty("dataStoreContexts").EnumerateArray().Any()
            );

        return dataStore.GetProperty("id").GetInt32();
    }

    private async Task<CmsDataStore> GetCmsDataStoreAsync(int dataStoreId)
    {
        using HttpResponseMessage response = await _configurationServiceClient.GetAsync(
            $"v3/dataStores/{dataStoreId}"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"CMS data-store get failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement dataStore = document.RootElement;

        return new CmsDataStore(
            dataStore.GetProperty("id").GetInt32(),
            dataStore.GetProperty("dataStoreType").GetString() ?? string.Empty,
            dataStore.GetProperty("name").GetString() ?? string.Empty,
            ReadOptionalString(dataStore, "provider")
        );
    }

    private async Task UpdateCmsDataStoreConnectionStringAsync(
        CmsDataStore dataStore,
        string connectionString
    )
    {
        var request = new
        {
            id = dataStore.Id,
            dataStoreType = dataStore.DataStoreType,
            name = dataStore.Name,
            connectionString,
            provider = dataStore.Provider,
        };

        using StringContent content = JsonContent(request);
        using HttpResponseMessage response = await _configurationServiceClient.PutAsync(
            $"v3/dataStores/{dataStore.Id}",
            content
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"CMS data-store update failed: {body}");
    }

    private async Task<ClientCredentials> CreateClientCredentialsForDataStoreAsync(
        int dataStoreId,
        string claimSetName = AuthorizationClaimSetNames.NoFurtherAuthRequired,
        long[]? educationOrganizationIds = null,
        int[]? profileIds = null
    )
    {
        int vendorId = await CreateVendorAsync();
        var request = new
        {
            vendorId,
            applicationName = $"DocCache E2E {Guid.NewGuid():N}"[..30],
            claimSetName,
            educationOrganizationIds = educationOrganizationIds ?? [],
            dataStoreIds = new[] { dataStoreId },
            profileIds = profileIds ?? [],
        };

        using StringContent content = JsonContent(request);
        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v3/applications",
            content
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"CMS application creation failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        string key = document.RootElement.GetProperty("key").GetString() ?? string.Empty;
        string secret = document.RootElement.GetProperty("secret").GetString() ?? string.Empty;
        key.Should().NotBeEmpty("CMS must return a DMS client key");
        secret.Should().NotBeEmpty("CMS must return a DMS client secret");

        return new ClientCredentials(key, secret);
    }

    private async Task<int> CreateVendorAsync()
    {
        var request = new
        {
            company = $"DocCache E2E {Guid.NewGuid():N}"[..30],
            contactName = "DocumentCache E2E",
            contactEmailAddress = "document-cache-e2e@example.com",
            namespacePrefixes = "uri://ed-fi.org",
        };

        using StringContent content = JsonContent(request);
        using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
            "v3/vendors",
            content
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"CMS vendor creation failed: {body}");

        Uri vendorLocation = RequireLocation(response, _configurationServiceClient.BaseAddress!);
        using HttpResponseMessage getResponse = await _configurationServiceClient.GetAsync(vendorLocation);
        string vendorBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"CMS vendor retrieval failed: {vendorBody}");

        using JsonDocument document = JsonDocument.Parse(vendorBody);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<string> GetDmsTokenAsync(ClientCredentials credentials)
    {
        using var formData = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
        ]);
        string basicCredentials = OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(
            credentials.key,
            credentials.secret
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);
        request.Content = formData;

        using HttpResponseMessage response = await _dmsClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"DMS token request failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private async Task<Guid> PostStudentAsync(string studentUniqueId, string firstName)
    {
        using StringContent content = StudentContent(studentUniqueId, firstName);
        using HttpResponseMessage response = await _dmsClient.PostAsync(StudentResourcePath, content);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST student failed: {body}");

        return ExtractResourceId(response);
    }

    private async Task PutStudentAsync(Guid studentId, string studentUniqueId, string firstName)
    {
        using StringContent content = StudentContent(studentUniqueId, firstName, studentId);
        using HttpResponseMessage response = await _dmsClient.PutAsync(StudentPath(studentId), content);
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"PUT student failed: {body}");
    }

    private async Task DeleteStudentAsync(Guid studentId)
    {
        using HttpResponseMessage response = await _dmsClient.DeleteAsync(StudentPath(studentId));
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"DELETE student failed: {body}");
    }

    private async Task AssertGetByIdAsync(Guid studentId, string studentUniqueId, string expectedFirstName)
    {
        using HttpResponseMessage response = await _dmsClient.GetAsync(StudentPath(studentId));
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET-by-id student failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement student = document.RootElement;
        student.GetProperty("id").GetString().Should().Be(studentId.ToString());
        student.GetProperty("studentUniqueId").GetString().Should().Be(studentUniqueId);
        student.GetProperty("firstName").GetString().Should().Be(expectedFirstName);
    }

    private async Task AssertGetManyAsync(string studentUniqueId, string expectedFirstName)
    {
        using HttpResponseMessage response = await _dmsClient.GetAsync(
            $"{StudentResourcePath}?studentUniqueId={Uri.EscapeDataString(studentUniqueId)}&totalCount=true"
        );
        string body = await ReadBodyAndAssertNoCacheDisclosureAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET-many student failed: {body}");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement student = document
            .RootElement.EnumerateArray()
            .Single(student => student.GetProperty("studentUniqueId").GetString() == studentUniqueId);
        student.GetProperty("firstName").GetString().Should().Be(expectedFirstName);
    }

    private static async Task AssertCanonicalStudentFirstNameAsync(Guid studentId, string expectedFirstName)
    {
        string firstName = await ReadCanonicalStudentFirstNameAsync(studentId);
        firstName
            .Should()
            .Be(expectedFirstName, "the cache-only sentinel must not mutate canonical student data");
    }

    private static async Task AssertCanonicalSchoolNameAsync(Guid schoolId, string expectedNameOfInstitution)
    {
        string nameOfInstitution = await ReadCanonicalSchoolNameAsync(schoolId);
        nameOfInstitution
            .Should()
            .Be(expectedNameOfInstitution, "the cache-only sentinel must not mutate canonical school data");
    }

    private async Task<JsonObject> RunDocumentCacheAdminAsync(params string[] arguments)
    {
        string settingsPath = CreateDocumentCacheAdminSettingsFile();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _repositoryRoot,
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(DocumentCacheAdminProjectPath());
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add(CurrentBuildConfiguration());
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--");

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.ArgumentList.Add("--settings");
        process.StartInfo.ArgumentList.Add(settingsPath);
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = string.Empty;
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = string.Empty;
        process.StartInfo.Environment["ConfigurationServiceSettings__ClientSecret"] = ConfigurationSecret();

        process.Start().Should().BeTrue("DocumentCache admin CLI must start");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try
        {
            await process.WaitForExitAsync(cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            Assert.Fail("DocumentCache admin CLI timed out before activation completed.");
        }

        string output = await standardOutput;
        string error = await standardError;
        process.ExitCode.Should().Be(0, "DocumentCache admin CLI stderr:\n{0}\nstdout:\n{1}", error, output);
        error.Should().NotContain(AppSettings.DataStoreConnectionString);

        JsonNode? parsed = JsonNode.Parse(output);
        return parsed as JsonObject
            ?? throw new InvalidOperationException("DocumentCache admin CLI stdout was not a JSON object.");
    }

    private async Task<JsonObject> RunDocumentCacheAdminWithHostReachableDataStoreAsync(
        int dataStoreId,
        string[] arguments
    )
    {
        CmsDataStore dataStore = await GetCmsDataStoreAsync(dataStoreId);
        await UpdateCmsDataStoreConnectionStringAsync(dataStore, AppSettings.DataStoreAdminConnectionString);

        try
        {
            return await RunDocumentCacheAdminAsync(arguments);
        }
        finally
        {
            await UpdateCmsDataStoreConnectionStringAsync(dataStore, AppSettings.DataStoreConnectionString);
        }
    }

    private async Task<JsonObject> WaitForDocumentCacheCaughtUpAsync(long dataStoreId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        string lastStatus = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            JsonObject status = await GetDocumentCacheStatusAsync();
            JsonObject target = TargetByDataStoreId(status, dataStoreId);
            lastStatus = status.ToJsonString();

            if (
                ReadString(target, "lifecycle", "state") == "tracking"
                && ReadString(target, "queueSummary", "presence") == "empty"
                && ReadString(target, "caughtUp", "status") == "caughtUp"
            )
            {
                return target;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new AssertionException(
            $"DocumentCache target {dataStoreId} did not become caught up. Last status: {lastStatus}"
        );
    }

    private async Task<JsonObject> GetDocumentCacheStatusAsync()
    {
        using HttpResponseMessage response = await _dmsClient.GetAsync(DocumentCacheStatusPath);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET /health/document-cache failed: {body}");

        JsonNode? parsed = JsonNode.Parse(body);
        return parsed as JsonObject
            ?? throw new InvalidOperationException("DocumentCache status response was not a JSON object.");
    }

    private static void AssertTargetIsHostedReadAccelerationTarget(JsonObject target, long dataStoreId)
    {
        target["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(dataStoreId);
        ReadString(target, "lifecycle", "state").Should().Be("tracking");
        ReadString(target, "caughtUp", "status").Should().Be("caughtUp");
        ReadString(target, "queueSummary", "presence").Should().Be("empty");
        ReadBool(target, "effectiveSettings", "readAcceleration", "enabled").Should().BeTrue();
    }

    private static async Task<DocumentCacheProjection> ReadDocumentCacheProjectionAsync(Guid documentUuid)
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
                    c.[StreamEtag],
                    c.[ResourceName],
                    c.[DocumentJson],
                    (SELECT COUNT_BIG(1) FROM [dms].[DocumentProjectionWork] AS w WHERE w.[DocumentId] = d.[DocumentId]) AS [WorkRows]
                FROM [dms].[Document] AS d
                LEFT JOIN [dms].[DocumentCache] AS c ON c.[DocumentId] = d.[DocumentId]
                WHERE d.[DocumentUuid] = @documentUuid;
                """
            : """
                SELECT
                    d."DocumentId",
                    d."ContentVersion" AS "DocumentContentVersion",
                    c."ContentVersion" AS "CacheContentVersion",
                    c."StreamEtag",
                    c."ResourceName",
                    c."DocumentJson"::text AS "DocumentJson",
                    (SELECT COUNT(*) FROM "dms"."DocumentProjectionWork" AS w WHERE w."DocumentId" = d."DocumentId") AS "WorkRows"
                FROM "dms"."Document" AS d
                LEFT JOIN "dms"."DocumentCache" AS c ON c."DocumentId" = d."DocumentId"
                WHERE d."DocumentUuid" = @documentUuid;
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new AssertionException($"Document {documentUuid} was not found in dms.Document.");
        }

        return new DocumentCacheProjection(
            DocumentId: ReadInt64(reader, "DocumentId"),
            DocumentContentVersion: ReadInt64(reader, "DocumentContentVersion"),
            CacheContentVersion: ReadRequiredInt64(reader, "CacheContentVersion"),
            StreamEtag: ReadRequiredString(reader, "StreamEtag"),
            ResourceName: ReadRequiredString(reader, "ResourceName"),
            DocumentJson: ReadRequiredString(reader, "DocumentJson"),
            WorkRows: ReadInt64(reader, "WorkRows")
        );
    }

    private static async Task<DocumentCacheSentinel> ApplyDocumentCacheSentinelAsync(
        Guid documentUuid,
        Action<JsonObject> mutateDocumentJson
    )
    {
        DocumentCacheProjection originalProjection = await ReadDocumentCacheProjectionAsync(documentUuid);
        originalProjection
            .CacheContentVersion.Should()
            .Be(originalProjection.DocumentContentVersion, "cache sentinel setup requires a fresh cache row");

        JsonObject documentJson = ParseDocumentJsonObject(originalProjection.DocumentJson);
        documentJson.ContainsKey("_etag").Should().BeFalse("DocumentJson stores fixed stream content");

        mutateDocumentJson(documentJson);
        documentJson.ContainsKey("_etag").Should().BeFalse("cache sentinel must not add _etag");

        await UpdateDocumentCacheJsonAsync(originalProjection.DocumentId, documentJson.ToJsonString());

        DocumentCacheProjection sentinelProjection = await ReadDocumentCacheProjectionAsync(documentUuid);
        sentinelProjection
            .DocumentContentVersion.Should()
            .Be(originalProjection.DocumentContentVersion, "the cache-only sentinel must not restamp source");
        sentinelProjection
            .CacheContentVersion.Should()
            .Be(
                originalProjection.CacheContentVersion,
                "the cache-only sentinel must preserve cache freshness"
            );
        ParseDocumentJsonObject(sentinelProjection.DocumentJson)
            .ContainsKey("_etag")
            .Should()
            .BeFalse("DocumentJson stores fixed stream content");

        return new DocumentCacheSentinel(documentUuid, originalProjection.DocumentId, originalProjection);
    }

    private static async Task RestoreDocumentCacheSentinelAsync(DocumentCacheSentinel sentinel)
    {
        await UpdateDocumentCacheJsonAsync(sentinel.DocumentId, sentinel.OriginalProjection.DocumentJson);

        DocumentCacheProjection restoredProjection = await ReadDocumentCacheProjectionAsync(
            sentinel.DocumentUuid
        );
        restoredProjection
            .DocumentContentVersion.Should()
            .Be(
                sentinel.OriginalProjection.DocumentContentVersion,
                "sentinel restore must not restamp source"
            );
        restoredProjection
            .CacheContentVersion.Should()
            .Be(
                sentinel.OriginalProjection.CacheContentVersion,
                "sentinel restore must preserve cache freshness"
            );
        JsonNode
            .DeepEquals(
                ParseDocumentJsonObject(restoredProjection.DocumentJson),
                ParseDocumentJsonObject(sentinel.OriginalProjection.DocumentJson)
            )
            .Should()
            .BeTrue("the cache sentinel helper must restore the original DocumentJson");
    }

    private static async Task UpdateDocumentCacheJsonAsync(long documentId, string documentJson)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                UPDATE [dms].[DocumentCache]
                SET [DocumentJson] = @documentJson
                WHERE [DocumentId] = @documentId;
                """
            : """
                UPDATE "dms"."DocumentCache"
                SET "DocumentJson" = CAST(@documentJson AS jsonb)
                WHERE "DocumentId" = @documentId;
                """;
        AddParameter(command, "@documentId", documentId);
        AddParameter(command, "@documentJson", documentJson);

        int updatedRows = await command.ExecuteNonQueryAsync();
        updatedRows.Should().Be(1, "cache sentinel update should affect exactly one cache row");
    }

    private static async Task<string> ReadCanonicalStudentFirstNameAsync(Guid documentUuid)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT s.[FirstName]
                FROM [edfi].[Student] AS s
                INNER JOIN [dms].[Document] AS d ON d.[DocumentId] = s.[DocumentId]
                WHERE d.[DocumentUuid] = @documentUuid;
                """
            : """
                SELECT s."FirstName"
                FROM "edfi"."Student" AS s
                INNER JOIN "dms"."Document" AS d ON d."DocumentId" = s."DocumentId"
                WHERE d."DocumentUuid" = @documentUuid;
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        object? firstName = await command.ExecuteScalarAsync();
        firstName.Should().NotBeNull("canonical student row must exist");
        return Convert.ToString(firstName, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> ReadCanonicalSchoolNameAsync(Guid documentUuid)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT s.[NameOfInstitution]
                FROM [edfi].[School] AS s
                INNER JOIN [dms].[Document] AS d ON d.[DocumentId] = s.[DocumentId]
                WHERE d.[DocumentUuid] = @documentUuid;
                """
            : """
                SELECT s."NameOfInstitution"
                FROM "edfi"."School" AS s
                INNER JOIN "dms"."Document" AS d ON d."DocumentId" = s."DocumentId"
                WHERE d."DocumentUuid" = @documentUuid;
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        object? nameOfInstitution = await command.ExecuteScalarAsync();
        nameOfInstitution.Should().NotBeNull("canonical school row must exist");
        return Convert.ToString(nameOfInstitution, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static JsonObject ParseDocumentJsonObject(string documentJson) =>
        JsonNode.Parse(documentJson)?.AsObject()
        ?? throw new AssertionException($"DocumentJson was not a JSON object: {documentJson}");

    private static async Task AssertDocumentCacheRowsDeletedAsync(Guid documentUuid)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT
                    (SELECT COUNT_BIG(1) FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid) AS [DocumentRows],
                    (SELECT COUNT_BIG(1) FROM [dms].[DocumentCache] WHERE [DocumentUuid] = @documentUuid) AS [CacheRows];
                """
            : """
                SELECT
                    (SELECT COUNT(*) FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid) AS "DocumentRows",
                    (SELECT COUNT(*) FROM "dms"."DocumentCache" WHERE "DocumentUuid" = @documentUuid) AS "CacheRows";
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the delete verification query must return one row");
        ReadInt64(reader, "DocumentRows").Should().Be(0);
        ReadInt64(reader, "CacheRows").Should().Be(0);
    }

    private static async Task ResetDocumentCacheDatabaseStateAsync()
    {
        await ContainerSetupBase.ResetDatabase();

        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                IF EXISTS (SELECT 1 FROM [dms].[DocumentCacheState] WHERE [StateId] = 1)
                    UPDATE [dms].[DocumentCacheState]
                    SET [ProjectionLifecycleState] = 'Disabled',
                        [CacheAheadRecoveryRequired] = CAST(0 AS bit)
                    WHERE [StateId] = 1;
                ELSE
                    INSERT INTO [dms].[DocumentCacheState] ([StateId], [ProjectionLifecycleState], [CacheAheadRecoveryRequired])
                    VALUES (1, 'Disabled', CAST(0 AS bit));
                """
            : """
                INSERT INTO "dms"."DocumentCacheState" (
                    "StateId",
                    "ProjectionLifecycleState",
                    "CacheAheadRecoveryRequired"
                )
                VALUES (1, 'Disabled', false)
                ON CONFLICT ("StateId") DO UPDATE
                SET "ProjectionLifecycleState" = 'Disabled',
                    "CacheAheadRecoveryRequired" = false;
                """;
        await command.ExecuteNonQueryAsync();
    }

    private string CreateDocumentCacheAdminSettingsFile()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms-document-cache-e2e",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tempDirectory);
        _tempDirectories.Add(tempDirectory);

        string settingsPath = Path.Combine(tempDirectory, "appsettings.document-cache-e2e.json");
        string apiSchemaPath = ApiSchemaPath();
        var settings = new JsonObject
        {
            ["AppSettings"] = new JsonObject
            {
                ["Datastore"] = AppSettingsDatastoreValue(),
                ["UseApiSchemaPath"] = true,
                ["ApiSchemaPath"] = apiSchemaPath,
                ["AllowIdentityUpdateOverrides"] = string.Empty,
                ["MaximumPageSize"] = 500,
                ["DefaultPartitionCount"] = 10,
                ["BypassAuthorization"] = true,
            },
            ["ConfigurationServiceSettings"] = new JsonObject
            {
                ["BaseUrl"] = _configurationServiceClient.BaseAddress!.ToString(),
                ["ClientId"] = CmsReadOnlyClientId,
                ["Scope"] = CmsReadOnlyScope,
                ["EncryptionKey"] = ConfigurationEncryptionKey(),
            },
            ["DataManagement"] = new JsonObject
            {
                ["DocumentCache"] = new JsonObject
                {
                    ["ReadAcceleration"] = new JsonObject { ["Enabled"] = false },
                    ["Projector"] = new JsonObject
                    {
                        ["PollInterval"] = "00:00:01",
                        ["PageSize"] = 100,
                        ["MaxConcurrentTargets"] = 1,
                        ["FailureBackoff"] = "00:00:05",
                        ["BaselineHighWaterMark"] = 100,
                    },
                    ["Administration"] = new JsonObject { ["WorkflowTimeout"] = "00:05:00" },
                    ["Status"] = new JsonObject
                    {
                        ["StatusObservationTimeout"] = "00:00:01",
                        ["EndpointTimeout"] = "00:00:10",
                    },
                },
            },
        };

        File.WriteAllText(settingsPath, settings.ToJsonString(_jsonOptions));
        return settingsPath;
    }

    private string ApiSchemaPath()
    {
        string configuredMountSource =
            Environment.GetEnvironmentVariable("DMS_API_SCHEMA_MOUNT_SOURCE") ?? string.Empty;
        bool usesDefaultBootstrapWorkspace = string.IsNullOrWhiteSpace(configuredMountSource);
        string apiSchemaPath = string.IsNullOrWhiteSpace(configuredMountSource)
            ? Path.Combine(_repositoryRoot, "eng", "docker-compose", ".bootstrap", "ApiSchema")
            : ResolveDockerComposeRelativePath(configuredMountSource);

        if (usesDefaultBootstrapWorkspace && !Directory.Exists(apiSchemaPath))
        {
            StageDefaultApiSchemaWorkspace();
        }

        Directory
            .Exists(apiSchemaPath)
            .Should()
            .BeTrue(
                "the admin CLI must read the same schema package workspace used by the hosted DMS container"
            );
        return apiSchemaPath;
    }

    private void StageDefaultApiSchemaWorkspace()
    {
        string dockerComposeDirectory = Path.Combine(_repositoryRoot, "eng", "docker-compose");
        string prepareSchemaScript = Path.Combine(dockerComposeDirectory, "prepare-dms-schema.ps1");
        string environmentFile = Path.Combine(dockerComposeDirectory, ".env.e2e");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dockerComposeDirectory,
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(prepareSchemaScript);
        process.StartInfo.ArgumentList.Add("-EnvironmentFile");
        process.StartInfo.ArgumentList.Add(environmentFile);

        process.Start().Should().BeTrue("prepare-dms-schema.ps1 must start");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            TryKill(process);
            Assert.Fail("prepare-dms-schema.ps1 timed out before staging the ApiSchema workspace.");
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        process.ExitCode.Should().Be(0, "prepare-dms-schema.ps1 stderr:\n{0}\nstdout:\n{1}", error, output);
    }

    private string ResolveDockerComposeRelativePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_repositoryRoot, "eng", "docker-compose", path));
    }

    private string DocumentCacheAdminProjectPath() =>
        Path.Combine(
            _repositoryRoot,
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string CurrentBuildConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.Name is "Debug" or "Release")
            {
                return directory.Name;
            }

            directory = directory.Parent;
        }

        return "Debug";
    }

    private static DbConnection CreateDataStoreConnection() =>
        IsMssql()
            ? new SqlConnection(AppSettings.DataStoreAdminConnectionString)
            : new NpgsqlConnection(AppSettings.DataStoreAdminConnectionString);

    private static StringContent JsonContent<T>(T request) =>
        new(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

    private static StringContent StudentContent(string studentUniqueId, string firstName, Guid? id = null)
    {
        var student = new JsonObject
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = firstName,
            ["lastSurname"] = "Cache",
            ["birthDate"] = "2010-05-01",
        };

        if (id is not null)
        {
            student["id"] = id.Value.ToString();
        }

        return new StringContent(student.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static string StudentPath(Guid studentId) => $"{StudentResourcePath}/{studentId}";

    private static async Task<string> ReadBodyAndAssertNoCacheDisclosureAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        IEnumerable<string> headerNames = response
            .Headers.Select(header => header.Key)
            .Concat(response.Content.Headers.Select(header => header.Key));

        headerNames
            .Should()
            .NotContain(header =>
                header.Contains("DocumentCache", StringComparison.OrdinalIgnoreCase)
                || header.Contains("ReadAcceleration", StringComparison.OrdinalIgnoreCase)
            );
        body.Should().NotContain("DocumentCache");
        body.Should().NotContain("ReadAcceleration");
        return body;
    }

    private static Uri RequireLocation(HttpResponseMessage response, Uri baseAddress)
    {
        Uri? location = response.Headers.Location;
        location.Should().NotBeNull("successful CMS create responses must include a Location header");
        return location!.IsAbsoluteUri ? location : new Uri(baseAddress, location);
    }

    private static Guid ExtractResourceId(HttpResponseMessage response)
    {
        Uri? location = response.Headers.Location;
        location.Should().NotBeNull("successful DMS resource creates must include a Location header");
        string lastSegment = location!.IsAbsoluteUri
            ? location.Segments[^1].TrimEnd('/')
            : location.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

        return Guid.Parse(lastSegment);
    }

    private static JsonObject TargetByDataStoreId(JsonObject status, long dataStoreId)
    {
        JsonArray targets =
            status["targets"]?.AsArray()
            ?? throw new InvalidOperationException("DocumentCache status response omitted targets.");

        foreach (JsonNode? node in targets)
        {
            JsonObject target =
                node?.AsObject()
                ?? throw new InvalidOperationException("DocumentCache status target was not an object.");
            long observedDataStoreId = target["targetKey"]!["dataStoreId"]!.GetValue<long>();

            if (observedDataStoreId == dataStoreId)
            {
                return target;
            }
        }

        throw new AssertionException($"DocumentCache status did not include dataStoreId {dataStoreId}.");
    }

    private static string ReadString(JsonObject root, params string[] path) =>
        RequiredNode(root, path).GetValue<string>();

    private static bool ReadBool(JsonObject root, params string[] path) =>
        RequiredNode(root, path).GetValue<bool>();

    private static JsonNode RequiredNode(JsonNode root, params string[] path)
    {
        JsonNode? node = root;
        foreach (string segment in path)
        {
            node = node?[segment];
        }

        return node ?? throw new InvalidOperationException($"JSON path '{string.Join(".", path)}' missing.");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static long ReadInt64(DbDataReader reader, string name) =>
        Convert.ToInt64(reader[name], CultureInfo.InvariantCulture);

    private static long ReadRequiredInt64(DbDataReader reader, string name)
    {
        object value = reader[name];
        value.Should().NotBe(DBNull.Value, $"{name} should not be null");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string ReadRequiredString(DbDataReader reader, string name)
    {
        object value = reader[name];
        value.Should().NotBe(DBNull.Value, $"{name} should not be null");
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    private static bool IsMssql() =>
        string.Equals(AppSettings.DatabaseEngine, "mssql", StringComparison.OrdinalIgnoreCase);

    private static string AppSettingsDatastoreValue() => IsMssql() ? "mssql" : "postgresql";

    private static string CliDatastoreOptionValue() => IsMssql() ? "sqlserver" : "postgresql";

    private static string ConfigurationSecret() =>
        Environment.GetEnvironmentVariable("CONFIG_SERVICE_CLIENT_SECRET") ?? CmsReadOnlyClientSecret;

    private static string ConfigurationEncryptionKey() =>
        Environment.GetEnvironmentVariable("DMS_CONFIG_DATABASE_ENCRYPTION_KEY") ?? CmsEncryptionKey;

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            string markerPath = Path.Combine(
                directory.FullName,
                "src",
                "dms",
                "EdFi.DataManagementService.sln"
            );

            if (File.Exists(markerPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate repository root from '{startDirectory}'.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the timeout and kill attempt.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for diagnostics-friendly temp files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for diagnostics-friendly temp files.
        }
    }

    private sealed record CmsDataStore(int Id, string DataStoreType, string Name, string? Provider);

    private sealed record DocumentCacheProjection(
        long DocumentId,
        long DocumentContentVersion,
        long CacheContentVersion,
        string StreamEtag,
        string ResourceName,
        string DocumentJson,
        long WorkRows
    );

    private sealed record DocumentCacheSentinel(
        Guid DocumentUuid,
        long DocumentId,
        DocumentCacheProjection OriginalProjection
    );
}
