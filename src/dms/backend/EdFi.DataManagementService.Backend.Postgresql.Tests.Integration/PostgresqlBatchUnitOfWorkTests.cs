// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Text.Json.Nodes;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class PostgresqlBatchUnitOfWorkTests : DatabaseTest
{
    private const string ResourceName = "BatchResource";
    private static readonly Guid ReferentialNamespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    [Test]
    public async Task Given_Multiple_Operations_When_Committed_AllChanges_Persist()
    {
        Guid firstDocumentUuid = Guid.NewGuid();
        Guid firstReferentialId = Guid.NewGuid();

        Guid secondDocumentUuid = Guid.NewGuid();
        Guid secondReferentialId = Guid.NewGuid();

        await using (PostgresqlBatchUnitOfWork batch = await CreateBatchUnitOfWorkAsync())
        {
            var insertOne = await batch.UpsertDocumentAsync(
                CreateUpsertRequest(
                    ResourceName,
                    firstDocumentUuid,
                    firstReferentialId,
                    """{"value":"initial"}"""
                )
            );
            insertOne.Should().BeOfType<UpsertResult.InsertSuccess>();

            var insertTwo = await batch.UpsertDocumentAsync(
                CreateUpsertRequest(
                    ResourceName,
                    secondDocumentUuid,
                    secondReferentialId,
                    """{"value":"second"}"""
                )
            );
            insertTwo.Should().BeOfType<UpsertResult.InsertSuccess>();

            var updateResult = await batch.UpdateDocumentByIdAsync(
                CreateUpdateRequest(
                    ResourceName,
                    firstDocumentUuid,
                    firstReferentialId,
                    """{"value":"updated"}"""
                )
            );
            updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();

            var deleteResult = await batch.DeleteDocumentByIdAsync(
                CreateDeleteRequest(ResourceName, secondDocumentUuid)
            );
            deleteResult.Should().BeOfType<DeleteResult.DeleteSuccess>();

            await batch.CommitAsync();
        }

        GetResult firstDocument = await GetDocumentAsync(firstDocumentUuid);
        firstDocument.Should().BeOfType<GetResult.GetSuccess>();
        string persistedJson = ((GetResult.GetSuccess)firstDocument).EdfiDoc.ToJsonString();
        persistedJson.Should().Contain("\"value\":\"updated\"");

        GetResult secondDocument = await GetDocumentAsync(secondDocumentUuid);
        secondDocument.Should().BeOfType<GetResult.GetFailureNotExists>();
    }

    [Test]
    public async Task Given_Uncommitted_Work_When_Disposed_Rolls_Back()
    {
        Guid transientDocumentUuid = Guid.NewGuid();
        Guid referentialId = Guid.NewGuid();

        await using (PostgresqlBatchUnitOfWork batch = await CreateBatchUnitOfWorkAsync())
        {
            var insert = await batch.UpsertDocumentAsync(
                CreateUpsertRequest(
                    ResourceName,
                    transientDocumentUuid,
                    referentialId,
                    """{"value":"transient"}"""
                )
            );
            insert.Should().BeOfType<UpsertResult.InsertSuccess>();
            // Intentionally no CommitAsync; DisposeAsync must roll the transaction back.
        }

        GetResult getResult = await GetDocumentAsync(transientDocumentUuid);
        getResult.Should().BeOfType<GetResult.GetFailureNotExists>();
    }

    [Test]
    public async Task Given_Existing_Document_When_Resolving_Natural_Key_Returns_DocumentUuid()
    {
        Guid existingDocumentUuid = Guid.NewGuid();
        ResourceInfo resourceInfo = CreateResourceInfo(ResourceName);

        DocumentIdentityElement[] identityElements = [new(new JsonPath("$.naturalKey"), "NK-1")];
        DocumentIdentity documentIdentity = new(identityElements);
        Guid referentialId = ComputeReferentialId(resourceInfo, documentIdentity);

        await using (NpgsqlConnection connection = await DataSource!.OpenConnectionAsync())
        await using (
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ConfiguredIsolationLevel)
        )
        {
            var upsertRequest = CreateUpsertRequest(
                ResourceName,
                existingDocumentUuid,
                referentialId,
                """{"naturalKey":"NK-1"}""",
                documentIdentityElements: identityElements
            );

            var insertResult = await CreateUpsert().Upsert(upsertRequest, connection, transaction);
            insertResult.Should().BeOfType<UpsertResult.InsertSuccess>();

            await transaction.CommitAsync();
        }

        await using PostgresqlBatchUnitOfWork batch = await CreateBatchUnitOfWorkAsync();
        DocumentUuid? resolved = await batch.ResolveDocumentUuidAsync(
            resourceInfo,
            documentIdentity,
            new TraceId("resolve-test")
        );

        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(new DocumentUuid(existingDocumentUuid));
    }

    private async Task<PostgresqlBatchUnitOfWork> CreateBatchUnitOfWorkAsync()
    {
        NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ConfiguredIsolationLevel);

        return new PostgresqlBatchUnitOfWork(
            connection,
            transaction,
            NullLogger<PostgresqlBatchUnitOfWork>.Instance,
            CreateUpsert(),
            CreateUpdate(),
            CreateDeleteById(),
            CreateSqlAction()
        );
    }

    private async Task<GetResult> GetDocumentAsync(Guid documentUuid)
    {
        await using NpgsqlConnection connection = await DataSource!.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            ConfiguredIsolationLevel
        );

        IGetRequest getRequest = CreateGetRequest(ResourceName, documentUuid);
        GetResult result = await CreateGetById().GetById(getRequest, connection, transaction);

        await transaction.RollbackAsync();
        return result;
    }

    private static Guid ComputeReferentialId(ResourceInfo resourceInfo, DocumentIdentity identity)
    {
        string resourceSegment = $"{resourceInfo.ProjectName.Value}{resourceInfo.ResourceName.Value}";
        string identitySegment = string.Join(
            "#",
            identity.DocumentIdentityElements.Select(element =>
                $"${element.IdentityJsonPath.Value}={element.IdentityValue}"
            )
        );

        return Deterministic.Create(ReferentialNamespace, resourceSegment + identitySegment);
    }
}
