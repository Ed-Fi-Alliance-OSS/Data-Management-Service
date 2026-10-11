// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class CompactDescriptorCacheScenario
{
    public static async Task ExecuteAsync(
        DbConnection connection,
        MappingSet mappingSet,
        string connectionString,
        IDescriptorWriteHandler handler,
        IDocumentCacheMaterializer materializer,
        IDocumentCacheWriter writer,
        Func<string, DescriptorWriteRequest> postRequest
    )
    {
        ISqlDialect dialect =
            mappingSet.Key.Dialect == SqlDialect.Pgsql
                ? new PgsqlDialect(new PgsqlDialectRules())
                : new MssqlDialect(new MssqlDialectRules());
        string Q(string name) => dialect.QuoteIdentifier(name);
        string Table(string name) => $"{Q("dms")}.{Q(name)}";
        async Task ExecuteSql(string sql)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteSql(
            mappingSet.Key.Dialect == SqlDialect.Pgsql
                ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
        );
        await ExecuteSql(
            $"UPDATE {Table("DocumentCacheState")} SET {Q("ProjectionLifecycleState")} = 'Tracking';"
        );

        const string Namespace = "uri://ed-fi.org/SchoolTypeDescriptor";
        string Body(string ns, string code, string description = "Original") =>
            new JsonObject
            {
                ["namespace"] = ns,
                ["codeValue"] = code,
                ["shortDescription"] = "Cache descriptor",
                ["description"] = description,
            }.ToJsonString();

        DescriptorWriteRequest create = postRequest(Body(Namespace, "Campus#MixedCase"));
        (await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(create))
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        StoredDescriptor original = await ReadStoredAsync();
        original.DocumentId.Should().BeGreaterThan(int.MaxValue);
        ((long)original.DescriptorId).Should().NotBe(original.DocumentId);
        var context = new DocumentCacheMaterializationTargetContext(
            new DocumentCacheProjectionTargetKey("", new DataStoreId(1)),
            mappingSet,
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            connectionString
        );
        DocumentCacheMaterializationCandidate initial = await ProjectAsync(
            original,
            Namespace,
            "Campus#MixedCase",
            "Original"
        );

        // A different component pair with the identical whole URI remains an accepted representation update.
        string componentsBody = Body($"{Namespace}#Campus", "MixedCase");
        (await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(postRequest(componentsBody)))
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        StoredDescriptor components = await ReadStoredAsync();
        AssertChanged(components, original);
        DocumentCacheMaterializationCandidate componentCandidate = await ProjectAsync(
            components,
            $"{Namespace}#Campus",
            "MixedCase",
            "Original"
        );
        componentCandidate.StreamEtag.Should().NotBe(initial.StreamEtag);

        // RI matching is case insensitive; the cache must retain the newly accepted original casing.
        string casingBody = Body($"{Namespace}#CAMPUS", "MIXEDCASE");
        (await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(postRequest(casingBody)))
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        StoredDescriptor casing = await ReadStoredAsync();
        AssertChanged(casing, components);
        DocumentCacheMaterializationCandidate casingCandidate = await ProjectAsync(
            casing,
            $"{Namespace}#CAMPUS",
            "MIXEDCASE",
            "Original"
        );
        casingCandidate.StreamEtag.Should().NotBe(componentCandidate.StreamEtag);

        string descriptiveBody = Body($"{Namespace}#CAMPUS", "MIXEDCASE", "Changed description");
        DescriptorWriteRequest put = postRequest(descriptiveBody) with
        {
            DocumentUuid = create.DocumentUuid,
            ReferentialId = null,
        };
        (await handler.HandlePutAsync(put)).Should().BeOfType<UpdateResult.UpdateSuccess>();
        StoredDescriptor descriptive = await ReadStoredAsync();
        AssertChanged(descriptive, casing);
        DocumentCacheMaterializationCandidate descriptiveCandidate = await ProjectAsync(
            descriptive,
            $"{Namespace}#CAMPUS",
            "MIXEDCASE",
            "Changed description"
        );
        descriptiveCandidate.StreamEtag.Should().NotBe(casingCandidate.StreamEtag);

        (await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(postRequest(descriptiveBody)))
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        (await handler.HandlePutAsync(put)).Should().BeOfType<UpdateResult.UpdateSuccess>();
        (await ReadStoredAsync()).Should().Be(descriptive);
        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
        DocumentCacheMaterializationCandidate noOp = await MaterializeAsync(descriptive);
        noOp.StreamEtag.Should().Be(descriptiveCandidate.StreamEtag);
        JsonNode.DeepEquals(noOp.DocumentJson, descriptiveCandidate.DocumentJson).Should().BeTrue();

        (
            await handler.HandleDeleteAsync(
                new(mappingSet, create.Resource, create.DocumentUuid, new TraceId("cache-delete"))
            )
        )
            .Should()
            .BeOfType<DeleteResult.DeleteSuccess>();
        (
            await materializer.MaterializeAsync(
                new(
                    context,
                    original.DocumentId,
                    null,
                    DocumentCacheMaterializationPurpose.Fixture,
                    CancellationToken.None
                )
            )
        )
            .Should()
            .BeSameAs(DocumentCacheMaterializationResult.MissingSource.Instance);
        // The writer handles the durable delete observation using the owning document key.
        await writer.WriteAsync(
            new(
                context,
                original.DocumentId,
                null,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                null,
                CancellationToken.None
            )
        );
        (await CountAsync("DocumentCache")).Should().Be(0);
        (await CountAsync("DocumentProjectionWork")).Should().Be(0);
        await ExecuteSql(
            $"UPDATE {Table("DocumentCacheState")} SET {Q("ProjectionLifecycleState")} = 'Disabled';"
        );

        void AssertChanged(StoredDescriptor after, StoredDescriptor before)
        {
            after.DocumentId.Should().Be(original.DocumentId);
            after.DescriptorId.Should().Be(original.DescriptorId);
            after.Version.Should().BeGreaterThan(before.Version);
            after.At.Should().BeOnOrAfter(before.At);
        }

        async Task<StoredDescriptor> ReadStoredAsync()
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT descriptor.{Q("DescriptorId")}, document.{Q("DocumentId")},
                       document.{Q("ContentVersion")}, document.{Q("ContentLastModifiedAt")},
                       descriptor.{Q("ContentVersion")}, descriptor.{Q("ContentLastModifiedAt")}
                FROM {Table("Document")} document
                JOIN {Table("Descriptor")} descriptor ON descriptor.{Q("DocumentId")} = document.{Q(
                    "DocumentId"
                )}
                WHERE document.{Q("DocumentUuid")} = @uuid;
                """;
            AddParameter(command, "uuid", create.DocumentUuid.Value);
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var stored = new StoredDescriptor(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetDateTime(3)
            );
            reader.GetInt64(4).Should().Be(stored.Version);
            reader.GetDateTime(5).Should().Be(stored.At);
            return stored;
        }

        async Task<DocumentCacheMaterializationCandidate> MaterializeAsync(StoredDescriptor stored)
        {
            var result = await materializer.MaterializeAsync(
                new(
                    context,
                    stored.DocumentId,
                    stored.Version,
                    DocumentCacheMaterializationPurpose.Fixture,
                    CancellationToken.None
                )
            );
            var candidate = result
                .Should()
                .BeOfType<DocumentCacheMaterializationResult.Success>()
                .Subject.Candidate;
            candidate.DocumentId.Should().Be(original.DocumentId);
            candidate.DocumentUuid.Should().Be(create.DocumentUuid);
            candidate.ContentVersion.Should().Be(stored.Version);
            candidate
                .StreamEtag.Should()
                .Be($"{stored.Version}-{mappingSet.Key.EffectiveSchemaHash[..8]}.j._.n.i");
            candidate.LastModifiedAt.UtcDateTime.Should().Be(stored.At);
            return candidate;
        }

        async Task<DocumentCacheMaterializationCandidate> ProjectAsync(
            StoredDescriptor stored,
            string ns,
            string code,
            string description
        )
        {
            await using (DbCommand work = connection.CreateCommand())
            {
                work.CommandText =
                    $"SELECT {Q("DocumentId")}, {Q("RequiredContentVersion")} FROM {Table("DocumentProjectionWork")};";
                await using DbDataReader reader = await work.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetInt64(0).Should().Be(original.DocumentId);
                reader.GetInt64(1).Should().Be(stored.Version);
                (await reader.ReadAsync()).Should().BeFalse();
            }
            var candidate = await MaterializeAsync(stored);
            candidate.DocumentJson["namespace"]!.GetValue<string>().Should().Be(ns);
            candidate.DocumentJson["codeValue"]!.GetValue<string>().Should().Be(code);
            candidate.DocumentJson["description"]!.GetValue<string>().Should().Be(description);
            candidate.DocumentJson["id"]!
                .GetValue<string>()
                .Should()
                .Be(create.DocumentUuid.Value.ToString());
            (
                await writer.WriteAsync(
                    new(
                        context,
                        stored.DocumentId,
                        stored.Version,
                        DocumentCacheWriterPurpose.DurableWorkProjection,
                        candidate,
                        CancellationToken.None
                    )
                )
            )
                .Should()
                .BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
            (await CountAsync("DocumentProjectionWork")).Should().Be(0);
            await using DbCommand cache = connection.CreateCommand();
            cache.CommandText =
                $"SELECT {Q("DocumentId")}, {Q("ContentVersion")}, {Q("DocumentJson")}, {Q("StreamEtag")} FROM {Table("DocumentCache")};";
            await using DbDataReader cacheReader = await cache.ExecuteReaderAsync();
            (await cacheReader.ReadAsync()).Should().BeTrue();
            cacheReader.GetInt64(0).Should().Be(original.DocumentId);
            cacheReader.GetInt64(1).Should().Be(stored.Version);
            JsonNode
                .DeepEquals(JsonNode.Parse(cacheReader.GetString(2)), candidate.DocumentJson)
                .Should()
                .BeTrue();
            cacheReader.GetString(3).Should().Be(candidate.StreamEtag);
            (await cacheReader.ReadAsync()).Should().BeFalse();
            return candidate;
        }

        async Task<long> CountAsync(string table)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {Table(table)};";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record StoredDescriptor(int DescriptorId, long DocumentId, long Version, DateTime At);
}
