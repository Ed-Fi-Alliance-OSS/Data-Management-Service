// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class AuthoritativeResponseMetadataScenario
{
    private const string Descriptors = "/data/ed-fi/schoolTypeDescriptors";
    private const string Items = "/data/ed-fi/compactDescriptorItems";

    public static async Task It_serves_owner_metadata_after_creation_changes_no_ops_and_restamping(
        ApiIntegrationHarness harness,
        bool descriptor
    )
    {
        Resource resource = await CreateAsync(harness, descriptor);
        Stamp created = await ReadStampAsync(harness, resource);
        JsonObject initial = await AssertReadsAsync(harness, resource, created);
        string variant = initial["_etag"]!.GetValue<string>().Split('-', 2)[1];

        await BackdateAsync(harness, resource);
        Stamp before = await ReadStampAsync(harness, resource);
        JsonObject backdated = await AssertReadsAsync(harness, resource, before);
        backdated["_etag"]!.GetValue<string>().Should().Be(initial["_etag"]!.GetValue<string>());
        resource.Body[descriptor ? "shortDescription" : "displayName"] = "Changed representation";
        using (
            HttpResponseMessage changed = await PutAsync(
                harness,
                resource,
                initial["_etag"]!.GetValue<string>()
            )
        )
        {
            changed
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        }
        Stamp after = await ReadStampAsync(harness, resource);
        after.Version.Should().BeGreaterThan(before.Version);
        after.ModifiedAt.Should().BeAfter(before.ModifiedAt);
        JsonObject changedBody = await AssertReadsAsync(harness, resource, after);
        changedBody["_etag"]!.GetValue<string>().Should().Be($"{after.Version}-{variant}");

        using (
            HttpResponseMessage unchanged = await PutAsync(
                harness,
                resource,
                changedBody["_etag"]!.GetValue<string>()
            )
        )
        {
            unchanged
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await unchanged.Content.ReadAsStringAsync());
        }
        (await ReadStampAsync(harness, resource)).Should().Be(after);
        JsonNode.DeepEquals(await AssertReadsAsync(harness, resource, after), changedBody).Should().BeTrue();

        await BackdateAsync(harness, resource);
        Stamp beforeRestamp = await ReadStampAsync(harness, resource);
        await RestampAsync(harness, resource, beforeRestamp);
        Stamp restamped = await ReadStampAsync(harness, resource);
        restamped.Version.Should().Be(after.Version + 1);
        restamped.ModifiedAt.Should().BeAfter(beforeRestamp.ModifiedAt);
        JsonObject restampedBody = await AssertReadsAsync(harness, resource, restamped);
        restampedBody["_etag"]!.GetValue<string>().Should().Be($"{restamped.Version}-{variant}");
        foreach (string metadata in new[] { "_etag", "_lastModifiedDate" })
        {
            changedBody.Remove(metadata);
            restampedBody.Remove(metadata);
        }
        JsonNode.DeepEquals(restampedBody, changedBody).Should().BeTrue("restamping changes metadata only");
    }

    public static async Task It_preserves_wrong_resource_and_conditional_lookup_behavior(
        ApiIntegrationHarness harness,
        bool descriptor
    )
    {
        Resource resource = await CreateAsync(harness, descriptor);
        Stamp before = await ReadStampAsync(harness, resource);
        JsonObject body = await AssertReadsAsync(harness, resource, before);
        ApiIntegrationQueryRecorder recorder = harness.QueryRecorder!;
        var writes = harness.Services.GetRequiredService<RelationalWriteSessionCommandRecorder>();
        string wrongEndpoint = descriptor
            ? "/data/ed-fi/academicSubjectDescriptors"
            : "/data/ed-fi/compactDescriptorBridges";
        using (
            HttpResponseMessage wrong = await harness.HttpClient.GetAsync($"{wrongEndpoint}/{resource.Uuid}")
        )
        {
            wrong.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        Resource wrongResource = resource with { Path = $"{wrongEndpoint}/{resource.Uuid}" };
        // The payload must match the requested resource so UUID lookup is reached after validation.
        if (!descriptor)
        {
            wrongResource = wrongResource with
            {
                Body = new JsonObject
                {
                    ["bridgeCode"] = "wrong",
                    ["itemReference"] = new JsonObject
                    {
                        ["itemCode"] = resource.Body["itemCode"]!.DeepClone(),
                        ["schoolTypeDescriptor"] = resource.Body["schoolTypeDescriptor"]!.DeepClone(),
                    },
                },
            };
        }
        using (
            HttpResponseMessage wrongPut = await PutAsync(
                harness,
                wrongResource,
                body["_etag"]!.GetValue<string>()
            )
        )
        {
            wrongPut
                .StatusCode.Should()
                .Be(HttpStatusCode.NotFound, await wrongPut.Content.ReadAsStringAsync());
        }
        int start = recorder.RelationalCommandExecutions;
        writes.Reset();
        using (
            HttpResponseMessage stale = await PutAsync(
                harness,
                resource,
                $"{before.Version + 10000}-{body["_etag"]!.GetValue<string>().Split('-', 2)[1]}"
            )
        )
        {
            stale
                .StatusCode.Should()
                .Be(HttpStatusCode.PreconditionFailed, await stale.Content.ReadAsStringAsync());
        }
        AssertVersionLookup(
            recorder
                .RelationalCommands.Skip(start)
                .Select(command => command.CommandText)
                .Concat(writes.Commands.Select(command => command.CommandText))
                .ToArray()
        );
        if (descriptor)
        {
            recorder.RelationalCommandExecutions.Should().Be(start);
            writes
                .Commands.Should()
                .HaveCount(
                    3,
                    "UUID lookup, document lock, and descriptor content read are sufficient for the stale precondition"
                );
            writes
                .Commands.Should()
                .OnlyContain(command =>
                    !command.CommandText.Contains("ContentLastModifiedAt", StringComparison.Ordinal)
                );
        }
        if (!descriptor)
        {
            recorder.RelationalCommandExecutions.Should().Be(start);
            writes
                .Commands.Should()
                .ContainSingle(
                    "the rejected ordinary PUT uses one composite first-phase batch, including capture and hydration"
                );
        }
        (await ReadStampAsync(harness, resource)).Should().Be(before);

        start = recorder.RelationalCommandExecutions;
        writes.Reset();
        using (
            HttpResponseMessage unchanged = await PutAsync(
                harness,
                resource,
                body["_etag"]!.GetValue<string>()
            )
        )
        {
            unchanged
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await unchanged.Content.ReadAsStringAsync());
        }
        AssertVersionLookup(
            recorder
                .RelationalCommands.Skip(start)
                .Select(command => command.CommandText)
                .Concat(writes.Commands.Select(command => command.CommandText))
                .ToArray()
        );
        (await ReadStampAsync(harness, resource)).Should().Be(before);

        if (descriptor)
        {
            start = recorder.RelationalCommandExecutions;
            writes.Reset();
            using HttpResponseMessage wrongDelete = await DeleteAsync(
                harness,
                wrongResource,
                body["_etag"]!.GetValue<string>()
            );
            wrongDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
            AssertVersionLookup(
                recorder
                    .RelationalCommands.Skip(start)
                    .Select(command => command.CommandText)
                    .Concat(writes.Commands.Select(command => command.CommandText))
                    .ToArray()
            );
            start = recorder.RelationalCommandExecutions;
            writes.Reset();
            using HttpResponseMessage deleted = await DeleteAsync(
                harness,
                resource,
                body["_etag"]!.GetValue<string>()
            );
            deleted
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
            AssertVersionLookup(
                recorder
                    .RelationalCommands.Skip(start)
                    .Select(command => command.CommandText)
                    .Concat(writes.Commands.Select(command => command.CommandText))
                    .ToArray()
            );
        }
    }

    public static async Task It_keeps_page_metadata_in_one_batch_as_the_page_grows(
        ApiIntegrationHarness harness
    )
    {
        List<Resource> resources = [];
        for (int i = 0; i < 3; i++)
        {
            Resource resource = await CreateAsync(harness, descriptor: false);
            await BackdateAsync(harness, resource, i);
            resources.Add(resource);
        }
        HydrationCommands commands = harness.Services.GetRequiredService<HydrationCommands>();
        ApiIntegrationQueryRecorder recorder = harness.QueryRecorder!;
        foreach (int size in new[] { 1, 3 })
        {
            commands.Sql.Clear();
            int otherCommands = recorder.RelationalCommandExecutions;
            using HttpResponseMessage response = await harness.HttpClient.GetAsync(
                $"{Items}?limit={size}&totalCount=true"
            );
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            response.Headers.GetValues("Total-Count").Single().Should().Be("3");
            JsonArray page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
            page.Count.Should().Be(size);
            recorder
                .RelationalCommandExecutions.Should()
                .Be(otherCommands, "no per-document metadata queries");
            string sql = Normalize(commands.Sql.Should().ContainSingle().Subject);
            string metadata = sql.Split(';')
                .Single(statement => statement.Contains("r.ContentLastModifiedAt", StringComparison.Ordinal));
            metadata.Should().Contain("r.ContentLastModifiedAt");
            metadata.Should().Contain("JOIN edfi.CompactDescriptorItem r ON r.DocumentId = d.DocumentId");
            metadata.Should().NotContain("d.ContentLastModifiedAt");
            string fence = sql.Split(';')
                .Single(statement =>
                    statement.Contains("FROM dms.Document d", StringComparison.Ordinal)
                    && !statement.Contains("ContentLastModifiedAt", StringComparison.Ordinal)
                );
            fence.Should().Contain("d.ContentVersion").And.NotContain("edfi.");
            for (int i = 0; i < size; i++)
            {
                Stamp stamp = await ReadStampAsync(harness, resources[i]);
                AssertMetadata(page[i]!.AsObject(), resources[i], stamp);
            }
        }
    }

    private static void AssertVersionLookup(IReadOnlyList<string> commands)
    {
        string[] sql = commands.Select(Normalize).ToArray();
        if (
            Array.Exists(
                sql,
                command => command.Contains("document.DocumentUuid AS DocumentUuid", StringComparison.Ordinal)
            )
        )
        {
            string lookup = sql.Where(command =>
                    command.Contains("document.DocumentUuid AS DocumentUuid", StringComparison.Ordinal)
                )
                .Should()
                .ContainSingle()
                .Subject;
            lookup.Should().Contain("FROM dms.Document document");
            lookup.Should().NotContain("JOIN").And.NotContain("ContentLastModifiedAt");
        }
        else
        {
            // Ordinary PUT captures and locks the target inside its composite first command.
            // Hydration follows in that batch; only the capture statement is the version lookup.
            string capture = sql.SelectMany(command => command.Split(';'))
                .Where(statement => statement.Contains("WHERE d.DocumentUuid =", StringComparison.Ordinal))
                .Should()
                .ContainSingle()
                .Subject;
            capture.Should().Contain("FROM dms.Document d").And.Contain("d.ContentVersion");
            capture
                .Should()
                .NotContain("ContentLastModifiedAt")
                .And.NotContain("edfi.")
                .And.NotContain("dms.Descriptor");
        }
        sql.Should()
            .NotContain(command =>
                command.Contains("document.ContentLastModifiedAt", StringComparison.Ordinal)
            );
    }

    private static string Normalize(string sql) =>
        string.Join(
            ' ',
            sql.Replace("\"", "")
                .Replace("[", "")
                .Replace("]", "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );

    internal static async Task<Resource> CreateAsync(ApiIntegrationHarness harness, bool descriptor)
    {
        await using (DbCommand separate = harness.DbConnection.CreateCommand())
        {
            separate.CommandText =
                harness.DbConnection is NpgsqlConnection
                    ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                    : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql;
            await separate.ExecuteNonQueryAsync();
        }
        string code = Guid.NewGuid().ToString("N")[..20];
        JsonObject descriptorBody = new()
        {
            ["namespace"] = "uri://ed-fi.org/DMS-1401",
            ["codeValue"] = code,
            ["shortDescription"] = "Original representation",
        };
        Resource descriptorResource = await PostAsync(
            harness,
            Descriptors,
            descriptorBody,
            "dms",
            "Descriptor"
        );
        if (descriptor)
        {
            return descriptorResource;
        }
        return await PostAsync(
            harness,
            Items,
            new JsonObject
            {
                ["itemCode"] = code,
                ["schoolTypeDescriptor"] = $"uri://ed-fi.org/DMS-1401#{code}",
                ["displayName"] = "Original representation",
                ["namespace"] = "uri://ed-fi.org/DMS-1401/items",
            },
            "edfi",
            "CompactDescriptorItem"
        );
    }

    private static async Task<Resource> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body,
        string schema,
        string table
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(endpoint, JsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        string path = response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
        return new(path, endpoint, Guid.Parse(path.Split('/')[^1]), body, schema, table);
    }

    internal static async Task<HttpResponseMessage> PutAsync(
        ApiIntegrationHarness harness,
        Resource resource,
        string etag
    )
    {
        JsonObject payload = resource.Body.DeepClone().AsObject();
        payload["id"] = resource.Uuid.ToString();
        using HttpRequestMessage request = new(HttpMethod.Put, resource.Path)
        {
            Content = JsonContent(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        ApiIntegrationHarness harness,
        Resource resource,
        string etag
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, resource.Path);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        return await harness.HttpClient.SendAsync(request);
    }

    private static StringContent JsonContent(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonObject> AssertReadsAsync(
        ApiIntegrationHarness harness,
        Resource resource,
        Stamp stamp
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(resource.Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonObject body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        AssertMetadata(body, resource, stamp);
        response.TryReadRawEtag(out string etag).Should().BeTrue();
        etag.Should().Be(body["_etag"]!.GetValue<string>());
        foreach (var field in resource.Body)
        {
            JsonNode
                .DeepEquals(body[field.Key], field.Value)
                .Should()
                .BeTrue($"{field.Key} must match persisted representation");
        }
        using HttpResponseMessage pageResponse = await harness.HttpClient.GetAsync(
            $"{resource.Endpoint}?minChangeVersion={stamp.Version}&maxChangeVersion={stamp.Version}"
        );
        pageResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, await pageResponse.Content.ReadAsStringAsync());
        JsonArray page = JsonNode.Parse(await pageResponse.Content.ReadAsStringAsync())!.AsArray();
        page.Should().ContainSingle();
        JsonNode
            .DeepEquals(page[0], body)
            .Should()
            .BeTrue("GET-by-id and change query must serve identical body and metadata");
        return body;
    }

    private static void AssertMetadata(JsonObject body, Resource resource, Stamp stamp)
    {
        body["id"]!.GetValue<string>().Should().Be(resource.Uuid.ToString());
        body["_etag"]!.GetValue<string>().Should().StartWith($"{stamp.Version}-");
        body["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(LastModifiedDateFormatter.Format(stamp.ModifiedAt));
    }

    internal static async Task<Stamp> ReadStampAsync(ApiIntegrationHarness harness, Resource resource)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = $"""
            SELECT d."DocumentId", d."ResourceKeyId", d."ContentVersion", r."ContentLastModifiedAt", r."ContentVersion"
            {(resource.Table == "Descriptor" ? ", r.\"DescriptorId\"" : "")}
            FROM "dms"."Document" d JOIN "{resource.Schema}"."{resource.Table}" r ON r."DocumentId" = d."DocumentId"
            WHERE d."DocumentUuid" = @uuid
            """;
        Add(command, "@uuid", resource.Uuid);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(4).Should().Be(reader.GetInt64(2));
        if (resource.Table == "Descriptor")
        {
            reader.GetInt64(0).Should().BeGreaterThan(int.MaxValue).And.NotBe(reader.GetInt32(5));
        }
        object value = reader.GetValue(3);
        DateTimeOffset modified = value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
        return new(reader.GetInt64(0), reader.GetInt16(1), reader.GetInt64(2), modified);
    }

    internal static async Task BackdateAsync(ApiIntegrationHarness harness, Resource resource, int offset = 0)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = $"""
            UPDATE "{resource.Schema}"."{resource.Table}" SET "ContentLastModifiedAt" = @modified
            WHERE "DocumentId" = (SELECT "DocumentId" FROM "dms"."Document" WHERE "DocumentUuid" = @uuid)
            """;
        Add(command, "@uuid", resource.Uuid);
        Add(command, "@modified", new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddDays(offset));
        (await command.ExecuteNonQueryAsync()).Should().BeGreaterThanOrEqualTo(1);
    }

    internal static async Task RestampAsync(ApiIntegrationHarness harness, Resource resource, Stamp stamp)
    {
        await using DbConnection connection = OpenConnection(
            harness.Services.GetRequiredService<HydrationCommands>().ConnectionString,
            harness.DbConnection is NpgsqlConnection
        );
        await connection.OpenAsync();
        await using IRelationalWriteSession session = new RelationalWriteSession(
            connection,
            await connection.BeginTransactionAsync(IsolationLevel.Serializable)
        );
        IRepresentationRestampStore store =
            harness.DbConnection is NpgsqlConnection
                ? ActivatorUtilities.CreateInstance<PostgresqlRepresentationRestampStore>(harness.Services)
                : ActivatorUtilities.CreateInstance<MssqlRepresentationRestampStore>(harness.Services);
        RepresentationRestampPageCommit committed = await store.StampPageAsync(
            session,
            new RepresentationRestampPage([
                new RepresentationRestampDocument(
                    stamp.DocumentId,
                    resource.Uuid,
                    new(stamp.ResourceKeyId, resource.Schema, resource.Table)
                ),
            ]),
            CancellationToken.None
        );
        await session.CommitAsync();
        Stamp persisted = await ReadStampAsync(harness, resource);
        committed.CanonicalStamps.Should().ContainSingle();
        committed.CanonicalStamps[0].ContentVersion.Should().Be(persisted.Version);
        committed.CanonicalStamps[0].ContentLastModifiedAt.Should().Be(persisted.ModifiedAt);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DbConnection OpenConnection(string connectionString, bool postgresql) =>
        postgresql ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);

    // Replace only connection acquisition: execute the real production hydration batch and reader,
    // capturing each command at the executor's command-creation seam.
    public static void RecordHydrationCommands(
        IServiceCollection services,
        string connectionString,
        bool postgresql
    )
    {
        CompactDescriptorResourceScenario.RecordWriteCommands(services);
        services.AddSingleton(new HydrationCommands(connectionString));
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentHydrator>(provider => new CommandRecordingHydrator(
                connectionString,
                postgresql,
                provider.GetRequiredService<HydrationCommands>()
            ))
        );
    }

    private sealed class HydrationCommands(string connectionString)
    {
        public string ConnectionString { get; } = connectionString;
        public List<string> Sql { get; } = [];
    }

    private sealed class CommandRecordingHydrator(
        string connectionString,
        bool postgresql,
        HydrationCommands commands
    ) : IDocumentHydrator
    {
        public async Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        )
        {
            await using DbConnection connection = OpenConnection(connectionString, postgresql);
            await connection.OpenAsync(ct);
            return await ConsistentHydrationExecutor.ExecuteAsync(
                sql =>
                {
                    commands.Sql.Add(sql);
                    DbCommand command = connection.CreateCommand();
                    command.CommandText = sql;
                    return command;
                },
                plan,
                keyset,
                postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql,
                executionOptions,
                ct
            );
        }
    }

    internal sealed record Resource(
        string Path,
        string Endpoint,
        Guid Uuid,
        JsonObject Body,
        string Schema,
        string Table
    );

    internal sealed record Stamp(
        long DocumentId,
        short ResourceKeyId,
        long Version,
        DateTimeOffset ModifiedAt
    );
}
