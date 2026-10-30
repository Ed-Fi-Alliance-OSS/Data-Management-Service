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
    public static Task ExecuteAsync(
        DbConnection connection,
        IDescriptorWriteHandler handler,
        IDescriptorReadHandler reader,
        Func<QualifiedResourceName, string, DescriptorWriteRequest> postRequest,
        QualifiedResourceName resource,
        QualifiedResourceName otherResource
    ) =>
        ExecuteCoreAsync(
            connection,
            handler,
            reader,
            postRequest,
            resource,
            otherResource,
            casingUpdate: false
        );

    /// <summary>
    /// Re-POSTs the stored descriptor in a different casing, alone and with a descriptive change: the probe matches
    /// it on both engines and the stored components are kept.
    /// </summary>
    public static Task ExecuteCasingUpdateAsync(
        DbConnection connection,
        IDescriptorWriteHandler handler,
        IDescriptorReadHandler reader,
        Func<QualifiedResourceName, string, DescriptorWriteRequest> postRequest,
        QualifiedResourceName resource,
        QualifiedResourceName otherResource
    ) =>
        ExecuteCoreAsync(
            connection,
            handler,
            reader,
            postRequest,
            resource,
            otherResource,
            casingUpdate: true
        );

    private static async Task ExecuteCoreAsync(
        DbConnection connection,
        IDescriptorWriteHandler handler,
        IDescriptorReadHandler reader,
        Func<QualifiedResourceName, string, DescriptorWriteRequest> postRequest,
        QualifiedResourceName resource,
        QualifiedResourceName otherResource,
        bool casingUpdate
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

        await BackdateOwnerAsync();
        initial = await SnapshotAsync(create.DocumentUuid);

        // The probe matches by descriptor identity and the stored components bind, so a POST whose components
        // reconstruct the stored URI differently changes nothing.
        UpsertResult.UpdateSuccess components = (
            await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                postRequest(resource, Body($"{Namespace}#Campus", "MiXeD"))
            )
        )
            .Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Subject;
        components.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
        components.ETag.Should().Be(inserted.ETag);
        (await SnapshotAsync(create.DocumentUuid)).Should().Be(initial);

        if (casingUpdate)
        {
            UpsertResult.UpdateSuccess casing = (
                await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                    postRequest(resource, Body($"{Namespace}#CAMPUS", "MIXED"))
                )
            )
                .Should()
                .BeOfType<UpsertResult.UpdateSuccess>()
                .Subject;
            casing.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
            casing.ETag.Should().Be(inserted.ETag);
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(initial);

            UpsertResult.UpdateSuccess casingWithDescription = (
                await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                    postRequest(resource, Body($"{Namespace}#CAMPUS", "MIXED", "Recased description"))
                )
            )
                .Should()
                .BeOfType<UpsertResult.UpdateSuccess>()
                .Subject;
            casingWithDescription.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
            casingWithDescription.ETag.Should().NotBe(inserted.ETag);
            StoredDescriptor afterCasing = await SnapshotAsync(create.DocumentUuid);
            AssertChanged(afterCasing, initial, Namespace, "Campus#MiXeD");
            afterCasing.Description.Should().Be("Recased description");
            await AssertReadEtagAsync(resource, create.DocumentUuid, casingWithDescription.ETag);
            return;
        }

        // The database's descriptor-identity verdict decides a PUT: a case-only or equal-URI identity keeps the
        // stored components and is a no-op, and only a different identity is rejected.
        foreach (
            string rejectedBody in new[]
            {
                Body($"{Namespace}Changed#Campus", "MiXeD"),
                Body($"{Namespace}#Campus", "Other"),
            }
        )
        {
            (await handler.HandlePutAsync(Put(rejectedBody)))
                .Should()
                .BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(initial);
            await AssertReadEtagAsync(resource, create.DocumentUuid, inserted.ETag);
        }

        foreach (
            string equalIdentityBody in new[]
            {
                Body($"{Namespace}#Campus", "mixed"),
                Body($"{Namespace}#Campus", "MiXeD"),
            }
        )
        {
            UpdateResult.UpdateSuccess equalIdentity = (await handler.HandlePutAsync(Put(equalIdentityBody)))
                .Should()
                .BeOfType<UpdateResult.UpdateSuccess>()
                .Subject;
            equalIdentity.ETag.Should().Be(inserted.ETag);
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(initial);
        }

        UpdateResult.UpdateSuccess descriptive = (
            await handler.HandlePutAsync(
                Put(Body($"{Namespace}#CAMPUS", "mixed", "Updated description", "2025-02-03"))
            )
        )
            .Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Subject;
        StoredDescriptor afterDescription = await SnapshotAsync(create.DocumentUuid);
        AssertChanged(afterDescription, initial, Namespace, "Campus#MiXeD");
        afterDescription.Description.Should().Be("Updated description");
        afterDescription.BeginDate.Should().Be(new DateTime(2025, 2, 3, 0, 0, 0, DateTimeKind.Unspecified));
        descriptive.ETag.Should().NotBe(inserted.ETag);
        await AssertReadEtagAsync(resource, create.DocumentUuid, descriptive.ETag);

        string descriptiveBody = Body(Namespace, "Campus#MiXeD", "Updated description", "2025-02-03");
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

        if (!pgsql)
        {
            // SQL Server ignores final spaces for identity equality, so the probe matches the stored descriptor
            // and the stored components are kept.
            UpsertResult.UpdateSuccess trailingSpace = (
                await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(
                    postRequest(
                        resource,
                        Body(Namespace, "Campus#MiXeD ", "Updated description", "2025-02-03")
                    )
                )
            )
                .Should()
                .BeOfType<UpsertResult.UpdateSuccess>()
                .Subject;
            trailingSpace.ExistingDocumentUuid.Should().Be(create.DocumentUuid);
            trailingSpace.ETag.Should().Be(descriptive.ETag);
            (await SnapshotAsync(create.DocumentUuid)).Should().Be(afterDescription);
        }

        long documentCount = await CountAsync("Document");
        long descriptorCount = await CountAsync("Descriptor");

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
        async Task BackdateOwnerAsync()
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE "dms"."Descriptor" SET "ContentLastModifiedAt" = '2000-01-01T00:00:00'
                WHERE "DocumentId" = @documentId;
                """;
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@documentId";
            parameter.Value = initial.DocumentId;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync();
        }
        async Task<StoredDescriptor> SnapshotAsync(DocumentUuid uuid)
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT descriptor."DescriptorId", document."DocumentId", descriptor."ResourceKeyId",
                       descriptor."Namespace", descriptor."CodeValue", descriptor."Description", descriptor."EffectiveBeginDate",
                       document."ContentVersion", descriptor."ContentLastModifiedAt", descriptor."ContentVersion"
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
            return new(
                data.GetInt32(0),
                data.GetInt64(1),
                data.GetInt16(2),
                data.GetString(3),
                data.GetString(4),
                data.GetString(5),
                data.GetDateTime(6),
                version,
                at
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
        DateTime At
    );
}
