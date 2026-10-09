// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class CompactDescriptorResourceScenario
{
    public const string CustomViewStrategy = "SchoolTypeDescriptorWithCompactResourceAccess";
    private const string Items = "/data/ed-fi/compactDescriptorItems";
    private const string Bridges = "/data/ed-fi/compactDescriptorBridges";
    private const string Consumers = "/data/ed-fi/compactDescriptorConsumers";

    public static async Task It_stores_compact_keys_and_hydrates_root_collection_extension_and_copied_URIs(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed edfi = await SeedDescriptorAsync(harness);
        DescriptorSeed sample = await SeedDescriptorAsync(harness, project: "sample", wholeUri: edfi.Uri);
        sample.Keys.DescriptorId.Should().NotBe(edfi.Keys.DescriptorId);
        JsonObject payload = Item("item", edfi.Uri.ToUpperInvariant());
        payload["choices"] = new JsonArray(
            new JsonObject
            {
                ["choiceCode"] = "choice",
                ["schoolTypeDescriptor"] = edfi.Uri.ToLowerInvariant(),
                ["variants"] = new JsonArray(
                    new JsonObject
                    {
                        ["variantCode"] = "variant",
                        ["schoolTypeDescriptor"] = edfi.Uri.ToUpperInvariant(),
                    }
                ),
            }
        );
        payload["_ext"] = new JsonObject
        {
            ["sample"] = new JsonObject
            {
                ["schoolTypeDescriptor"] = sample.Uri.ToLowerInvariant(),
                ["tags"] = new JsonArray(
                    new JsonObject
                    {
                        ["tagCode"] = "tag",
                        ["schoolTypeDescriptor"] = sample.Uri.ToUpperInvariant(),
                    }
                ),
            },
        };
        string item = await PostAsync(harness, Items, payload);
        string bridge = await PostAsync(
            harness,
            Bridges,
            Bridge("bridge", "item", edfi.Uri.ToLowerInvariant())
        );
        string consumer = await PostAsync(
            harness,
            Consumers,
            Consumer("consumer", "bridge", "item", edfi.Uri.ToUpperInvariant())
        );
        JsonObject actual = await GetAsync(harness, item);
        actual["schoolTypeDescriptor"]!.GetValue<string>().Should().Be(edfi.Uri);
        actual["choices"]![0]!["schoolTypeDescriptor"]!.GetValue<string>().Should().Be(edfi.Uri);
        actual["choices"]![0]!["variants"]![0]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(edfi.Uri);
        actual["_ext"]!["sample"]!["schoolTypeDescriptor"]!.GetValue<string>().Should().Be(sample.Uri);
        actual["_ext"]!["sample"]!["tags"]![0]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(sample.Uri);
        (await GetAsync(harness, bridge))["itemReference"]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(edfi.Uri);
        (await GetAsync(harness, consumer))["bridgeReference"]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(edfi.Uri);
        await AssertStoredKeysAsync(harness, edfi.Keys, sample.Keys, expectedColumns: 7);
    }

    public static async Task It_matches_repeated_POST_and_transitive_document_identities_with_mixed_case_descriptors(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed seed = await SeedDescriptorAsync(harness);
        JsonObject[] payloads =
        [
            Item("item", seed.Uri),
            Bridge("bridge", "item", seed.Uri),
            Consumer("consumer", "bridge", "item", seed.Uri),
        ];
        string[] endpoints = [Items, Bridges, Consumers];
        for (int i = 0; i < endpoints.Length; i++)
        {
            string path = await PostAsync(harness, endpoints[i], payloads[i]);
            JsonObject before = await GetAsync(harness, path);
            var stamp = await ReadStampAsync(harness, path);
            ReplaceUri(payloads[i], seed.Uri, seed.Uri.ToUpperInvariant());
            (await PostAsync(harness, endpoints[i], payloads[i], HttpStatusCode.OK)).Should().Be(path);
            JsonNode
                .DeepEquals(await GetAsync(harness, path), before)
                .Should()
                .BeTrue("the resolved compact key makes casing-only resource POST a no-op");
            (await ReadStampAsync(harness, path)).Should().Be(stamp);
            (
                await ScalarAsync(
                    harness,
                    "SELECT COUNT(*) FROM \"dms\".\"ReferentialIdentity\" WHERE \"DocumentId\" = @id",
                    ("@id", stamp.DocumentId)
                )
            )
                .Should()
                .Be(1);
        }
        await AssertStoredKeysAsync(harness, seed.Keys, seed.Keys, expectedColumns: 3);
    }

    public static async Task It_filters_whole_descriptor_URIs_on_direct_and_transitive_identity_paths(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed seed = await SeedDescriptorAsync(harness, codeValue: "Campus#MiXeD");
        string item = await PostAsync(harness, Items, Item("item", seed.Uri));
        string bridge = await PostAsync(
            harness,
            Bridges,
            Bridge("bridge", "item", seed.Uri.ToUpperInvariant())
        );
        string consumer = await PostAsync(
            harness,
            Consumers,
            Consumer("consumer", "bridge", "item", seed.Uri.ToLowerInvariant())
        );
        string[] endpoints = [Items, Bridges, Consumers];
        string[] paths = [item, bridge, consumer];
        for (int i = 0; i < endpoints.Length; i++)
        {
            foreach (
                string uri in new[] { seed.Uri, seed.Uri.ToUpperInvariant(), seed.Uri.ToLowerInvariant() }
            )
            {
                JsonArray page = await QueryAsync(harness, endpoints[i], uri);
                page.Select(row => row!["id"]!.GetValue<string>()).Should().Equal(paths[i].Split('/')[^1]);
            }
            (await QueryAsync(harness, endpoints[i], seed.Uri + "#Missing")).Should().BeEmpty();
        }
        DescriptorSeed wrongType = await SeedDescriptorAsync(harness, endpoint: "academicSubjectDescriptors");
        foreach (string endpoint in endpoints)
        {
            (await QueryAsync(harness, endpoint, wrongType.Uri))
                .Should()
                .BeEmpty("URI resolution must retain descriptor type identity");
        }
        // Moving the delimiter between stored components retains the same whole URI and RI.
        string[] components = seed.Uri.Split('#', 2);
        await PostAsync(
            harness,
            "/data/ed-fi/schoolTypeDescriptors",
            new JsonObject
            {
                ["namespace"] = components[0] + "#Campus",
                ["codeValue"] = "MiXeD",
                ["shortDescription"] = "Compact descriptor resource test",
            },
            HttpStatusCode.OK
        );
        (await QueryAsync(harness, Items, seed.Uri))
            .Select(row => row!["id"]!.GetValue<string>())
            .Should()
            .Equal(item.Split('/')[^1]);
        // Internal whitespace before the delimiter is part of the whole string.
        DescriptorSeed spaced = await SeedDescriptorAsync(
            harness,
            namespaceSuffix: "Space ",
            codeValue: "Code#Extra"
        );
        string spacedItem = await PostAsync(harness, Items, Item("spaced", spaced.Uri.ToUpperInvariant()));
        (await QueryAsync(harness, Items, spaced.Uri))
            .Select(row => row!["id"]!.GetValue<string>())
            .Should()
            .Equal(spacedItem.Split('/')[^1]);
        string withoutSpace = spaced.Uri.Replace(" #", "#", StringComparison.Ordinal);
        (await QueryAsync(harness, Items, withoutSpace)).Should().BeEmpty();
        using HttpResponseMessage invalid = await SendAsync(
            harness,
            HttpMethod.Post,
            Items,
            Item("invalid", withoutSpace)
        );
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
        (await QueryAsync(harness, Items, spaced.Uri)).Count.Should().Be(1);
        (await GetAsync(harness, spacedItem))["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(spaced.Uri);
    }

    public static async Task It_protects_referenced_descriptors_without_changing_rows_or_stamps(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed seed = await SeedDescriptorAsync(harness);
        string item = await PostAsync(harness, Items, Item("item", seed.Uri));
        JsonObject before = await GetAsync(harness, item);
        var descriptorStamp = await ReadStampAsync(harness, seed.Path);
        var itemStamp = await ReadStampAsync(harness, item);
        using HttpResponseMessage denied = await harness.HttpClient.DeleteAsync(seed.Path);
        denied.StatusCode.Should().Be(HttpStatusCode.Conflict, await denied.Content.ReadAsStringAsync());
        JsonObject problem = JsonNode.Parse(await denied.Content.ReadAsStringAsync())!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:data-conflict:dependent-item-exists");
        problem["detail"]!.GetValue<string>().Should().Contain("CompactDescriptorItem");
        (await ReadStampAsync(harness, seed.Path)).Should().Be(descriptorStamp);
        (await ReadStampAsync(harness, item)).Should().Be(itemStamp);
        JsonNode.DeepEquals(await GetAsync(harness, item), before).Should().BeTrue();
        await AssertStoredKeysAsync(harness, seed.Keys, seed.Keys, expectedColumns: 1);
        using HttpResponseMessage removedItem = await harness.HttpClient.DeleteAsync(item);
        removedItem.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using HttpResponseMessage removedDescriptor = await harness.HttpClient.DeleteAsync(seed.Path);
        removedDescriptor.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    public static async Task It_maintains_RI_and_copied_descriptor_keys_after_resource_identity_updates(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed beforeDescriptor = await SeedDescriptorAsync(harness);
        DescriptorSeed afterDescriptor = await SeedDescriptorAsync(harness);
        JsonObject original = Item("item", beforeDescriptor.Uri);
        string item = await PostAsync(harness, Items, original);
        string bridge = await PostAsync(harness, Bridges, Bridge("bridge", "item", beforeDescriptor.Uri));
        string consumer = await PostAsync(
            harness,
            Consumers,
            Consumer("consumer", "bridge", "item", beforeDescriptor.Uri)
        );
        var itemBefore = await ReadStampAsync(harness, item);
        var bridgeBefore = await ReadStampAsync(harness, bridge);
        var consumerBefore = await ReadStampAsync(harness, consumer);
        JsonObject changed = Item("item", afterDescriptor.Uri.ToUpperInvariant());
        changed["id"] = item.Split('/')[^1];
        using HttpResponseMessage put = await SendAsync(harness, HttpMethod.Put, item, changed);
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        (await GetAsync(harness, bridge))["itemReference"]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(afterDescriptor.Uri);
        (await GetAsync(harness, consumer))["bridgeReference"]!["schoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(afterDescriptor.Uri);
        foreach (
            var (path, before) in new[]
            {
                (item, itemBefore),
                (bridge, bridgeBefore),
                (consumer, consumerBefore),
            }
        )
        {
            var after = await ReadStampAsync(harness, path);
            after.DocumentId.Should().Be(before.DocumentId);
            after.Version.Should().BeGreaterThan(before.Version);
        }
        await AssertStoredKeysAsync(harness, afterDescriptor.Keys, afterDescriptor.Keys, expectedColumns: 3);
        changed.Remove("id");
        var unchanged = await ReadStampAsync(harness, item);
        (await PostAsync(harness, Items, changed, HttpStatusCode.OK)).Should().Be(item);
        (await ReadStampAsync(harness, item)).Should().Be(unchanged);
        (
            await PostAsync(
                harness,
                Bridges,
                Bridge("bridge", "item", afterDescriptor.Uri.ToLowerInvariant()),
                HttpStatusCode.OK
            )
        )
            .Should()
            .Be(bridge);
        (
            await PostAsync(
                harness,
                Consumers,
                Consumer("consumer", "bridge", "item", afterDescriptor.Uri.ToUpperInvariant()),
                HttpStatusCode.OK
            )
        )
            .Should()
            .Be(consumer);
        using HttpResponseMessage stale = await SendAsync(
            harness,
            HttpMethod.Post,
            Consumers,
            Consumer("stale", "bridge", "item", beforeDescriptor.Uri)
        );
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict, await stale.Content.ReadAsStringAsync());
        (await QueryAsync(harness, Consumers, beforeDescriptor.Uri)).Should().BeEmpty();
        (await QueryAsync(harness, Consumers, afterDescriptor.Uri)).Count.Should().Be(1);
    }

    public static async Task It_preserves_scalar_namespace_authorization_with_compact_descriptor_references(
        ApiIntegrationHarness harness,
        MutableNamespacePrefixJwtValidationService identity
    )
    {
        DescriptorSeed allowed = await SeedDescriptorAsync(harness);
        DescriptorSeed denied = await SeedDescriptorAsync(harness, namespaceSuffix: "Denied");
        string allowedNamespace = "uri://ed-fi.org/DMS-1404/allowed/";
        string deniedNamespace = "uri://ed-fi.org/DMS-1404/denied/";
        string item = await PostAsync(harness, Items, Item("item", allowed.Uri, allowedNamespace));
        var before = await ReadStampAsync(harness, item);
        identity.SetNamespacePrefixes([allowedNamespace]);
        using HttpResponseMessage proposed = await SendAsync(
            harness,
            HttpMethod.Post,
            Items,
            Item("new", denied.Uri, deniedNamespace)
        );
        proposed.StatusCode.Should().Be(HttpStatusCode.Forbidden, await proposed.Content.ReadAsStringAsync());
        JsonObject putPayload = Item("item", denied.Uri, deniedNamespace);
        putPayload["id"] = item.Split('/')[^1];
        using HttpResponseMessage put = await SendAsync(harness, HttpMethod.Put, item, putPayload);
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden, await put.Content.ReadAsStringAsync());
        (await ReadStampAsync(harness, item)).Should().Be(before);
        identity.SetNamespacePrefixes(["uri://denied.example/"]);
        using HttpResponseMessage read = await harness.HttpClient.GetAsync(item);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage delete = await harness.HttpClient.DeleteAsync(item);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage post = await SendAsync(
            harness,
            HttpMethod.Post,
            Items,
            Item("item", allowed.Uri, allowedNamespace)
        );
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await QueryAsync(harness, Items, allowed.Uri)).Should().BeEmpty();
        (await ReadStampAsync(harness, item)).Should().Be(before);
        identity.SetNamespacePrefixes(["uri://ed-fi.org/"]);
        (await GetAsync(harness, item))["schoolTypeDescriptor"]!.GetValue<string>().Should().Be(allowed.Uri);
        await AssertStoredKeysAsync(harness, allowed.Keys, allowed.Keys, expectedColumns: 1);
    }

    public static async Task It_authorizes_custom_view_membership_by_the_owning_descriptor_document(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed allowed = await SeedDescriptorAsync(harness);
        DescriptorSeed denied = await SeedDescriptorAsync(harness);
        await SeedDescriptorAsync(harness, project: "sample", wholeUri: allowed.Uri);
        bool postgresql = harness.DbConnection.GetType().Name == "NpgsqlConnection";
        await ExecuteAsync(
            harness,
            postgresql
                ? "CREATE SCHEMA IF NOT EXISTS auth;"
                : "IF SCHEMA_ID(N'auth') IS NULL EXEC(N'CREATE SCHEMA [auth]');"
        );
        await ExecuteAsync(
            harness,
            $"""CREATE VIEW "auth"."{CustomViewStrategy}" AS SELECT "DocumentId" FROM "dms"."Descriptor" WHERE "DocumentId" = {allowed.Keys.DocumentId}"""
        );
        string item = await PostAsync(harness, Items, Item("item", allowed.Uri.ToUpperInvariant()));
        string bridge = await PostAsync(harness, Bridges, Bridge("bridge", "item", allowed.Uri));
        string consumer = await PostAsync(
            harness,
            Consumers,
            Consumer("consumer", "bridge", "item", allowed.Uri.ToLowerInvariant())
        );
        using HttpResponseMessage deniedCreate = await SendAsync(
            harness,
            HttpMethod.Post,
            Items,
            Item("denied", denied.Uri)
        );
        deniedCreate
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden, await deniedCreate.Content.ReadAsStringAsync());
        JsonObject changed = Item("item", denied.Uri);
        changed["id"] = item.Split('/')[^1];
        var before = await ReadStampAsync(harness, item);
        using HttpResponseMessage deniedUpdate = await SendAsync(harness, HttpMethod.Put, item, changed);
        deniedUpdate
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden, await deniedUpdate.Content.ReadAsStringAsync());
        (await ReadStampAsync(harness, item)).Should().Be(before);
        string[] paths = [item, bridge, consumer];
        string[] endpoints = [Items, Bridges, Consumers];
        JsonObject[] bodies =
        [
            Item("item", allowed.Uri),
            Bridge("bridge", "item", allowed.Uri),
            Consumer("consumer", "bridge", "item", allowed.Uri),
        ];
        for (int i = 0; i < paths.Length; i++)
        {
            var stored = await ReadStampAsync(harness, paths[i]);
            JsonObject canonical = await GetAsync(harness, paths[i]);
            canonical["id"]!.GetValue<string>().Should().Be(paths[i].Split('/')[^1]);
            (await QueryAsync(harness, endpoints[i], allowed.Uri))
                .Select(row => row!["id"]!.GetValue<string>())
                .Should()
                .Equal(paths[i].Split('/')[^1]);
            (await PostAsync(harness, endpoints[i], bodies[i], HttpStatusCode.OK)).Should().Be(paths[i]);
            (await ReadStampAsync(harness, paths[i])).Should().Be(stored);
        }
        await ExecuteAsync(harness, $"""DROP VIEW "auth"."{CustomViewStrategy}" """);
        await ExecuteAsync(
            harness,
            $"""CREATE VIEW "auth"."{CustomViewStrategy}" AS SELECT "DocumentId" FROM "dms"."Descriptor" WHERE "DocumentId" = {denied.Keys.DocumentId}"""
        );
        for (int i = 0; i < paths.Length; i++)
        {
            var stored = await ReadStampAsync(harness, paths[i]);
            using HttpResponseMessage read = await harness.HttpClient.GetAsync(paths[i]);
            read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using HttpResponseMessage delete = await harness.HttpClient.DeleteAsync(paths[i]);
            delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using HttpResponseMessage post = await SendAsync(
                harness,
                HttpMethod.Post,
                endpoints[i],
                bodies[i]
            );
            post.StatusCode.Should().Be(HttpStatusCode.Forbidden, await post.Content.ReadAsStringAsync());
            (await QueryAsync(harness, endpoints[i], allowed.Uri)).Should().BeEmpty();
            (await ReadStampAsync(harness, paths[i])).Should().Be(stored);
        }
        await AssertStoredKeysAsync(harness, allowed.Keys, allowed.Keys, expectedColumns: 3);
    }

    public static void RecordWriteCommands(IServiceCollection services)
    {
        ServiceDescriptor factory = services.Single(service =>
            service.ServiceType == typeof(IRelationalWriteSessionFactory)
        );
        Type factoryType = factory.ImplementationType!;
        services.AddSingleton<RelationalWriteSessionCommandRecorder>();
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalWriteSessionFactory>(
                provider => new RecordingRelationalWriteSessionFactory(
                    (IRelationalWriteSessionFactory)ActivatorUtilities.CreateInstance(provider, factoryType),
                    provider.GetRequiredService<RelationalWriteSessionCommandRecorder>()
                )
            )
        );
    }

    public static async Task It_batches_repeated_descriptor_lookups_in_the_existing_write_command_stream(
        ApiIntegrationHarness harness
    )
    {
        DescriptorSeed seed = await SeedDescriptorAsync(harness);
        var recorder = harness.Services.GetRequiredService<RelationalWriteSessionCommandRecorder>();
        JsonObject payload = Item("item", seed.Uri);
        payload["choices"] = new JsonArray(
            new JsonObject
            {
                ["choiceCode"] = "first",
                ["schoolTypeDescriptor"] = seed.Uri.ToUpperInvariant(),
            },
            new JsonObject
            {
                ["choiceCode"] = "second",
                ["schoolTypeDescriptor"] = seed.Uri.ToLowerInvariant(),
            }
        );
        recorder.Reset();
        string item = await PostAsync(harness, Items, payload);
        recorder.ShouldHaveCommandCount(2);
        recorder.ShouldHaveTransactionBoundary(1, 1, 0);
        recorder.Commands[0].CommandText.Should().Contain("ReferentialIdentity").And.Contain("DescriptorId");
        recorder.Commands[1].CommandText.Should().NotContain("ReferentialIdentity");
        AssertSingleDescriptorLookup(recorder, seed.ReferentialId);
        recorder.Reset();
        (await PostAsync(harness, Items, payload, HttpStatusCode.OK)).Should().Be(item);
        recorder.ShouldHaveCommandCount(2);
        recorder.ShouldHaveTransactionBoundary(1, 1, 0);
        recorder
            .Commands.Count(command =>
                command.CommandText.Contains("ReferentialIdentity", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
        AssertSingleDescriptorLookup(recorder, seed.ReferentialId);
        await AssertStoredKeysAsync(harness, seed.Keys, seed.Keys, expectedColumns: 2);
    }

    private static void AssertSingleDescriptorLookup(
        RelationalWriteSessionCommandRecorder recorder,
        Guid referentialId
    )
    {
        var boundIds = recorder
            .Commands[0]
            .Parameters.SelectMany(parameter =>
                parameter.Value switch
                {
                    Guid[] values => values,
                    Guid value => [value],
                    _ => Array.Empty<Guid>(),
                }
            );
        boundIds
            .Count(value => value == referentialId)
            .Should()
            .Be(1, "root and collection occurrences must share one batched descriptor lookup");
    }

    private static JsonObject Item(string code, string uri, string ns = "uri://ed-fi.org/DMS-1404/items") =>
        new()
        {
            ["itemCode"] = code,
            ["displayName"] = "Compact descriptor resource test",
            ["schoolTypeDescriptor"] = uri,
            ["namespace"] = ns,
        };

    private static JsonObject Bridge(string code, string item, string uri) =>
        new()
        {
            ["bridgeCode"] = code,
            ["itemReference"] = new JsonObject { ["itemCode"] = item, ["schoolTypeDescriptor"] = uri },
        };

    private static JsonObject Consumer(string code, string bridge, string item, string uri) =>
        new()
        {
            ["consumerCode"] = code,
            ["bridgeReference"] = new JsonObject
            {
                ["bridgeCode"] = bridge,
                ["itemCode"] = item,
                ["schoolTypeDescriptor"] = uri,
            },
        };

    private static void ReplaceUri(JsonObject payload, string before, string after)
    {
        foreach (var (name, value) in payload.ToArray())
        {
            if (value is JsonObject child)
            {
                ReplaceUri(child, before, after);
            }
            else if (value is JsonValue scalar && scalar.GetValue<string>() == before)
            {
                payload[name] = after;
            }
        }
    }

    private static async Task<DescriptorSeed> SeedDescriptorAsync(
        ApiIntegrationHarness harness,
        string project = "ed-fi",
        string endpoint = "schoolTypeDescriptors",
        string wholeUri = "",
        string namespaceSuffix = "Type",
        string codeValue = "MiXeD"
    )
    {
        await ExecuteAsync(
            harness,
            harness.DbConnection.GetType().Name == "NpgsqlConnection"
                ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
        );
        string ns = $"uri://ed-fi.org/DMS-1404/{Guid.NewGuid():N}/{namespaceSuffix}";
        if (wholeUri.Length > 0)
        {
            string[] components = wholeUri.Split('#', 2);
            ns = components[0];
            codeValue = components[1];
        }
        string path = await PostAsync(
            harness,
            $"/data/{project}/{endpoint}",
            new JsonObject
            {
                ["namespace"] = ns,
                ["codeValue"] = codeValue,
                ["shortDescription"] = "Compact descriptor resource test",
            }
        );
        await using DbCommand command = Command(
            harness,
            "SELECT descriptor.\"DescriptorId\", descriptor.\"DocumentId\", (SELECT ri.\"ReferentialId\" FROM \"dms\".\"ReferentialIdentity\" ri WHERE ri.\"DocumentId\" = descriptor.\"DocumentId\") FROM \"dms\".\"Descriptor\" descriptor JOIN \"dms\".\"Document\" document ON document.\"DocumentId\" = descriptor.\"DocumentId\" WHERE document.\"DocumentUuid\" = @uuid",
            ("@uuid", Guid.Parse(path.Split('/')[^1]))
        );
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        SeededDescriptor keys = new(reader.GetInt32(0), reader.GetInt64(1));
        keys.DocumentId.Should().BeGreaterThan(int.MaxValue);
        ((long)keys.DescriptorId).Should().NotBe(keys.DocumentId);
        return new(path, $"{ns}#{codeValue}", keys, reader.GetGuid(2));
    }

    private static async Task AssertStoredKeysAsync(
        ApiIntegrationHarness harness,
        SeededDescriptor edfi,
        SeededDescriptor sample,
        int expectedColumns
    )
    {
        SqlDialect dialect =
            harness.DbConnection.GetType().Name == "NpgsqlConnection" ? SqlDialect.Pgsql : SqlDialect.Mssql;
        var (modelSet, _) = DdlPipelineHelpers.BuildDdlForDialect(
            EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(harness.Fixture.ApiSchemaDirectory),
            dialect
        );
        int populatedColumns = 0;
        var descriptorColumns = modelSet
            .ConcreteResourcesInNameOrder.Where(model =>
                model.StorageKind == ResourceStorageKind.RelationalTables
            )
            .SelectMany(model => model.RelationalModel.TablesInDependencyOrder)
            .SelectMany(table =>
                table
                    .Columns.Where(column => column.Kind == ColumnKind.DescriptorFk)
                    .Select(column => (Table: table, Column: column))
            );
        foreach (var (table, column) in descriptorColumns)
        {
            column.ScalarType!.Kind.Should().Be(ScalarKind.Int32);
            await using DbCommand command = Command(
                harness,
                $"SELECT \"{column.ColumnName.Value}\" FROM \"{table.Table.Schema.Value}\".\"{table.Table.Name}\" WHERE \"{column.ColumnName.Value}\" IS NOT NULL"
            );
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            int count = 0;
            while (await reader.ReadAsync())
            {
                reader.GetFieldType(0).Should().Be(typeof(int));
                reader
                    .GetInt32(0)
                    .Should()
                    .Be(
                        column.TargetResource!.Value.ProjectName == "Sample"
                            ? sample.DescriptorId
                            : edfi.DescriptorId,
                        $"{table.Table}.{column.ColumnName} must store the independently allocated key"
                    );
                count++;
            }
            if (count > 0)
            {
                populatedColumns++;
            }
        }
        populatedColumns.Should().Be(expectedColumns);
    }

    private static async Task<string> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload,
        HttpStatusCode expected = HttpStatusCode.Created
    )
    {
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Post, endpoint, payload);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();
        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static Task<HttpResponseMessage> SendAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject payload
    ) =>
        harness.HttpClient.SendAsync(
            new HttpRequestMessage(method, endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            }
        );

    private static async Task<JsonObject> GetAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task<JsonArray> QueryAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string uri
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{endpoint}?schoolTypeDescriptor={Uri.EscapeDataString(uri)}&totalCount=true"
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonArray page = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        response.Headers.GetValues("Total-Count").Single().Should().Be(page.Count.ToString());
        return page;
    }

    private static DbCommand Command(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static async Task ExecuteAsync(ApiIntegrationHarness harness, string sql)
    {
        await using DbCommand command = Command(harness, sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = Command(harness, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<Stamp> ReadStampAsync(ApiIntegrationHarness harness, string path)
    {
        await using DbCommand command = Command(
            harness,
            "SELECT \"DocumentId\", \"ContentVersion\", \"ContentLastModifiedAt\" FROM \"dms\".\"Document\" WHERE \"DocumentUuid\" = @uuid",
            ("@uuid", Guid.Parse(path.Split('/')[^1]))
        );
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetValue(2));
    }

    private sealed record DescriptorSeed(string Path, string Uri, SeededDescriptor Keys, Guid ReferentialId);

    private sealed record Stamp(long DocumentId, long Version, object ModifiedAt);
}
