// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class CompactDescriptorWriteScenario
{
    public static async Task ExecuteAsync(
        DbConnection connection,
        IDescriptorWriteHandler handler,
        IDescriptorReadHandler reader,
        Func<QualifiedResourceName, string, DescriptorWriteRequest> postRequest,
        QualifiedResourceName resource,
        QualifiedResourceName otherResource
    )
    {
        const string Namespace = "uri://ed-fi.org/CompactDescriptor";
        string Body(string ns, string code, string description = "Original", string date = "2025-01-01") =>
            new JsonObject
            {
                ["namespace"] = ns,
                ["codeValue"] = code,
                ["shortDescription"] = "Compact descriptor",
                ["description"] = description,
                ["effectiveBeginDate"] = date,
            }.ToJsonString();

        bool pgsql = connection.GetType().Name == "NpgsqlConnection";
        await ExecuteSqlAsync(
            pgsql
                ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
        );
        DescriptorWriteRequest create = postRequest(resource, Body(Namespace, "Campus#MiXeD"));
        UpsertResult.InsertSuccess inserted = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(create)
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Subject;
        inserted.NewDocumentUuid.Should().Be(create.DocumentUuid);
        StoredDescriptor initial = await SnapshotAsync(create.DocumentUuid);
        initial.DocumentId.Should().BeGreaterThan(int.MaxValue);
        ((long)initial.DescriptorId).Should().NotBe(initial.DocumentId);
        await AssertReadEtagAsync(resource, inserted.NewDocumentUuid, inserted.ETag);

        string componentsBody = Body($"{Namespace}#Campus", "MiXeD");
        UpsertResult.UpdateSuccess components = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                postRequest(resource, componentsBody)
            )
        )
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Subject;
        components.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
        components.ETag.Should().NotBe(inserted.ETag);
        StoredDescriptor afterComponents = await SnapshotAsync(create.DocumentUuid);
        AssertChanged(afterComponents, initial, $"{Namespace}#Campus", "MiXeD");
        await AssertReadEtagAsync(resource, components.ExistingDocumentUuid, components.ETag);

        string casingBody = Body($"{Namespace}#CAMPUS", "MIXED");
        UpsertResult.UpdateSuccess casing = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(postRequest(resource, casingBody))
        )
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Subject;
        casing.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
        casing.ETag.Should().NotBe(components.ETag);
        StoredDescriptor afterCasing = await SnapshotAsync(create.DocumentUuid);
        AssertChanged(afterCasing, afterComponents, $"{Namespace}#CAMPUS", "MIXED");

        // Ordinal whole-URI equality is stricter than RI or provider identity equality.
        foreach (
            string rejectedBody in new[]
            {
                Body($"{Namespace}#CAMPUS", "mixed"),
                Body($"{Namespace}Changed#CAMPUS", "MIXED"),
                Body($"{Namespace}#CAMPUS", "Other"),
            }
        )
        {
            (await handler.HandlePutAsync(Put(rejectedBody)))
                .Should()
                .BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterCasing);
            await AssertReadEtagAsync(resource, create.DocumentUuid, casing.ETag);
        }

        string equalUriBody = Body(Namespace, "CAMPUS#MIXED");
        UpdateResult.UpdateSuccess equalUri = (await handler.HandlePutAsync(Put(equalUriBody)))
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Subject;
        equalUri.ETag.Should().NotBe(casing.ETag);
        StoredDescriptor afterEqualUri = await SnapshotAsync(create.DocumentUuid);
        AssertChanged(afterEqualUri, afterCasing, Namespace, "CAMPUS#MIXED");
        await AssertReadEtagAsync(resource, create.DocumentUuid, equalUri.ETag);

        string descriptiveBody = Body(Namespace, "CAMPUS#MIXED", "Updated description", "2025-02-03");
        UpdateResult.UpdateSuccess descriptive = (await handler.HandlePutAsync(Put(descriptiveBody)))
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Subject;
        StoredDescriptor afterDescription = await SnapshotAsync(create.DocumentUuid);
        AssertChanged(afterDescription, afterEqualUri, Namespace, "CAMPUS#MIXED");
        afterDescription.Description.Should().Be("Updated description");
        afterDescription.BeginDate.Should().Be(new DateTime(2025, 2, 3, 0, 0, 0, DateTimeKind.Unspecified));
        descriptive.ETag.Should().NotBe(equalUri.ETag);
        await AssertReadEtagAsync(resource, create.DocumentUuid, descriptive.ETag);

        UpsertResult.UpdateSuccess postNoOp = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                postRequest(resource, descriptiveBody)
            )
        )
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Subject;
        postNoOp.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
        postNoOp.ETag.Should().Be(descriptive.ETag);
        (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);
        UpdateResult.UpdateSuccess putNoOp = (await handler.HandlePutAsync(Put(descriptiveBody)))
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Subject;
        putNoOp.ETag.Should().Be(descriptive.ETag);
        (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);

        // Bypass the RI match explicitly. Equal storage alone must preserve the conflict result.
        long documentCount = await CountAsync("Document");
        long descriptorCount = await CountAsync("Descriptor");
        DescriptorWriteRequest unmatched = postRequest(resource, descriptiveBody) with
        {
            ReferentialId = new ReferentialId(Guid.NewGuid()),
        };
        (await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(unmatched))
            .Should()
            .BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);
        (await CountAsync("Document")).Should().Be(documentCount);
        (await CountAsync("Descriptor")).Should().Be(descriptorCount);
        if (!pgsql)
        {
            // SQL Server ignores final spaces for indexed URI equality; RI still includes them.
            (
                await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                    postRequest(resource, Body(Namespace, "CAMPUS#MIXED "))
                )
            )
                .Should()
                .BeOfType<UpsertResult.UpsertFailureWriteConflict>();
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);
            (await CountAsync("Document")).Should().Be(documentCount);
        }

        DescriptorWriteRequest otherCreate = postRequest(otherResource, descriptiveBody);
        UpsertResult.InsertSuccess other = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(otherCreate)
        )
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Subject;
        StoredDescriptor otherState = await SnapshotAsync(other.NewDocumentUuid);
        otherState.DescriptorId.Should().NotBe(initial.DescriptorId);
        otherState.DocumentId.Should().NotBe(initial.DocumentId);
        otherState.ResourceKeyId.Should().NotBe(initial.ResourceKeyId);
        await AssertReadEtagAsync(otherResource, other.NewDocumentUuid, other.ETag);
        (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);

        (
            await handler.HandleDeleteAsync(
                new(create.MappingSet, resource, create.DocumentUuid, new TraceId("compact-delete"))
            )
        )
            .Should()
            .BeOfType<DeleteResult.DeleteSuccess>();
        (await CountAsync("Document")).Should().Be(documentCount);
        (await CountAsync("Descriptor")).Should().Be(descriptorCount);
        (await SnapshotAsync(other.NewDocumentUuid)).Should().Be(otherState);
        (await reader.HandleGetByIdAsync(GetRequest(resource, create.DocumentUuid)))
            .Should()
            .BeOfType<GetResult.GetFailureNotExists>();

        DescriptorWriteRequest Put(string body) =>
            postRequest(resource, body) with
            {
                DocumentUuid = create.DocumentUuid,
                ReferentialId = null,
            };
        DescriptorGetByIdRequest GetRequest(QualifiedResourceName type, DocumentUuid uuid) =>
            new(
                create.MappingSet,
                type,
                uuid,
                RelationalGetRequestReadMode.ExternalResponse,
                authorizationStrategyEvaluators: [],
                readableProfileProjectionContext: null,
                traceId: new TraceId("compact-read")
            );
        async Task AssertReadEtagAsync(QualifiedResourceName type, DocumentUuid uuid, string etag)
        {
            GetResult read = await reader.HandleGetByIdAsync(GetRequest(type, uuid));
            RelationalGetIntegrationTestHelper.ReadResultEtag(read).Should().Be(etag);
        }
        void AssertChanged(StoredDescriptor after, StoredDescriptor before, string ns, string code)
        {
            after.DescriptorId.Should().Be(initial.DescriptorId);
            after.DocumentId.Should().Be(initial.DocumentId);
            after.ResourceKeyId.Should().Be(initial.ResourceKeyId);
            after.Version.Should().Be(before.Version + 1);
            after.At.Should().BeAfter(before.At);
            after.Namespace.Should().Be(ns);
            after.CodeValue.Should().Be(code);
            after.RiCount.Should().Be(1);
        }
        async Task ExecuteSqlAsync(string sql)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        async Task<long> CountAsync(string table)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"dms\".\"{table}\"";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task<StoredDescriptor> SnapshotAsync(DocumentUuid uuid)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT descriptor."DescriptorId", document."DocumentId", descriptor."ResourceKeyId",
                       descriptor."Namespace", descriptor."CodeValue", descriptor."Description", descriptor."EffectiveBeginDate",
                       document."ContentVersion", document."ContentLastModifiedAt", descriptor."ContentVersion", descriptor."ContentLastModifiedAt",
                       (SELECT COUNT(*) FROM "dms"."ReferentialIdentity" ri WHERE ri."DocumentId" = document."DocumentId")
                FROM "dms"."Document" document
                JOIN "dms"."Descriptor" descriptor ON descriptor."DocumentId" = document."DocumentId"
                WHERE document."DocumentUuid" = @uuid
                """;
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@uuid";
            parameter.Value = uuid.Value;
            command.Parameters.Add(parameter);
            await using DbDataReader data = await command.ExecuteReaderAsync();
            (await data.ReadAsync()).Should().BeTrue();
            long version = data.GetInt64(7);
            DateTime at = data.GetDateTime(8);
            data.GetInt64(9).Should().Be(version);
            data.GetDateTime(10).Should().Be(at);
            return new(
                data.GetInt32(0),
                data.GetInt64(1),
                data.GetInt16(2),
                data.GetString(3),
                data.GetString(4),
                data.GetString(5),
                data.GetDateTime(6),
                version,
                at,
                Convert.ToInt32(data.GetValue(11))
            );
        }
    }

    private sealed record StoredDescriptor(
        int DescriptorId,
        long DocumentId,
        short ResourceKeyId,
        string Namespace,
        string CodeValue,
        string Description,
        DateTime BeginDate,
        long Version,
        DateTime At,
        int RiCount
    );
}
