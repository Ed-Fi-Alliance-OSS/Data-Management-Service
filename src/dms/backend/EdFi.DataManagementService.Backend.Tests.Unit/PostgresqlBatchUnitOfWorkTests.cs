// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Be.Vlaanderen.Basisregisters.Generators.Guid;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Postgresql.Model;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class PostgresqlBatchUnitOfWorkTests
{
    private static PostgresqlBatchUnitOfWork CreateUnitOfWork(
        NpgsqlConnection? connection = null,
        NpgsqlTransaction? transaction = null,
        IUpsertDocument? upsert = null,
        IUpdateDocumentById? update = null,
        IDeleteDocumentById? delete = null,
        ISqlAction? sqlAction = null
    )
    {
        return new PostgresqlBatchUnitOfWork(
            connection ?? CreateConnection(),
            transaction ?? CreateTransaction(),
            NullLogger<PostgresqlBatchUnitOfWork>.Instance,
            upsert ?? A.Fake<IUpsertDocument>(),
            update ?? A.Fake<IUpdateDocumentById>(),
            delete ?? A.Fake<IDeleteDocumentById>(),
            sqlAction ?? A.Fake<ISqlAction>()
        );
    }

    [Test]
    public async Task Operations_ReUse_Same_Connection_And_Transaction()
    {
        var connection = CreateConnection();
        var transaction = CreateTransaction();
        var upsert = A.Fake<IUpsertDocument>();
        var update = A.Fake<IUpdateDocumentById>();
        var delete = A.Fake<IDeleteDocumentById>();

        var uow = CreateUnitOfWork(connection, transaction, upsert, update, delete, A.Fake<ISqlAction>());

        var upsertRequest = A.Fake<IUpsertRequest>();
        var updateRequest = A.Fake<IUpdateRequest>();
        var deleteRequest = A.Fake<IDeleteRequest>();

        await uow.UpsertDocumentAsync(upsertRequest);
        await uow.UpdateDocumentByIdAsync(updateRequest);
        await uow.DeleteDocumentByIdAsync(deleteRequest);

        A.CallTo(() => upsert.Upsert(upsertRequest, connection, transaction)).MustHaveHappened();
        A.CallTo(() => update.UpdateById(updateRequest, connection, transaction)).MustHaveHappened();
        A.CallTo(() => delete.DeleteById(deleteRequest, connection, transaction)).MustHaveHappened();
    }

    [Test]
    public async Task ResolveDocumentUuid_UsesDeterministic_ReferentialId()
    {
        var connection = CreateConnection();
        var transaction = CreateTransaction();
        var sqlAction = A.Fake<ISqlAction>();
        var expectedUuid = Guid.NewGuid();

        Document document = new(
            DocumentPartitionKey: 1,
            DocumentUuid: expectedUuid,
            ResourceName: "Student",
            ResourceVersion: "5.0.0",
            IsDescriptor: false,
            ProjectName: "ed-fi",
            EdfiDoc: JsonDocument.Parse("""{"studentUniqueId":"1"}""").RootElement,
            LastModifiedTraceId: Guid.NewGuid().ToString()
        );

        A.CallTo(() =>
                sqlAction.FindDocumentByReferentialId(
                    A<ReferentialId>._,
                    A<PartitionKey>._,
                    connection,
                    transaction,
                    A<TraceId>._
                )
            )
            .Returns(Task.FromResult<Document?>(document));

        var uow = CreateUnitOfWork(connection, transaction, sqlAction: sqlAction);

        var resourceInfo = new ResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("Student"),
            false,
            new SemVer("5.0.0"),
            false,
            new EducationOrganizationHierarchyInfo(false, 0, null),
            []
        );

        var identity = new DocumentIdentity(
            [new DocumentIdentityElement(new JsonPath("$.studentUniqueId"), "1")]
        );

        var traceId = new TraceId("trace");
        var result = await uow.ResolveDocumentUuidAsync(resourceInfo, identity, traceId);

        result.Should().BeEquivalentTo(new DocumentUuid(expectedUuid));

        Guid expectedReferential = Deterministic.Create(
            new Guid("edf1edf1-3df1-3df1-3df1-3df1edf1edf1"),
            "Ed-FiStudent$.studentUniqueId=1"
        );

        var expectedPartitionKey = PartitionUtility.PartitionKeyFor(new ReferentialId(expectedReferential));

        A.CallTo(() =>
                sqlAction.FindDocumentByReferentialId(
                    A<ReferentialId>.That.Matches(r => r.Value == expectedReferential),
                    A<PartitionKey>.That.Matches(pk => pk.Value == expectedPartitionKey.Value),
                    connection,
                    transaction,
                    traceId
                )
            )
            .MustHaveHappened();
    }

    [Test]
    public async Task Dispose_Without_Commit_Rolls_Back()
    {
        var connection = CreateConnection();
        var transaction = CreateTransaction();

        A.CallTo(() => transaction.RollbackAsync(default)).Returns(Task.CompletedTask);
        A.CallTo(() => transaction.DisposeAsync()).Returns(ValueTask.CompletedTask);
        A.CallTo(() => connection.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var uow = CreateUnitOfWork(connection, transaction);
        await uow.DisposeAsync();

        A.CallTo(() => transaction.RollbackAsync(default)).MustHaveHappened();
    }

    private static NpgsqlConnection CreateConnection() =>
        new NpgsqlConnection("Host=localhost;Username=test;Password=test;Database=test");

    private static NpgsqlTransaction CreateTransaction() => A.Fake<NpgsqlTransaction>();
}
