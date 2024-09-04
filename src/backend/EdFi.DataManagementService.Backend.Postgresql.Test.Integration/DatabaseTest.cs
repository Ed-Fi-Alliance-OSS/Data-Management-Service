// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Postgresql.Operation;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using ImpromptuInterface;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

public abstract class DatabaseTest : DatabaseTestBase
{
    protected NpgsqlConnection? Connection { get; set; }
    protected NpgsqlTransaction? Transaction { get; set; }

    [SetUp]
    public async Task ConnectionSetup()
    {
        Connection = await DataSource!.OpenConnectionAsync();
        Transaction = await Connection.BeginTransactionAsync(ConfiguredIsolationLevel);
    }

    [TearDown]
    public void ConnectionTeardown()
    {
        Transaction?.Dispose();
        Connection?.Dispose();
    }

    protected static SqlAction CreateSqlAction()
    {
        return new SqlAction(NullLogger<SqlAction>.Instance);
    }

    protected static UpsertDocument CreateUpsert()
    {
        return new UpsertDocument(CreateSqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    protected static UpdateDocumentById CreateUpdate()
    {
        return new UpdateDocumentById(CreateSqlAction(), NullLogger<UpdateDocumentById>.Instance);
    }

    protected static GetDocumentById CreateGetById()
    {
        return new GetDocumentById(CreateSqlAction(), NullLogger<GetDocumentById>.Instance);
    }

    protected static QueryDocument CreateQueryDocument()
    {
        return new QueryDocument(CreateSqlAction(), NullLogger<QueryDocument>.Instance);
    }

    protected static DeleteDocumentById CreateDeleteById()
    {
        return new DeleteDocumentById(CreateSqlAction(), NullLogger<DeleteDocumentById>.Instance);
    }

    protected static T AsValueType<T, TU>(TU value)
        where T : class
    {
        return (new { Value = value }).ActLike<T>();
    }

    protected static ResourceInfo CreateResourceInfo(string resourceName, bool allowIdentityUpdates = false)
    {
        return new(
            ResourceVersion: new("5.0.0"),
            AllowIdentityUpdates: allowIdentityUpdates,
            ProjectName: new("ProjectName"),
            ResourceName: new(resourceName),
            IsDescriptor: false
        );
    }

    protected static DocumentInfo CreateDocumentInfo(
        Guid referentialId,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null
    )
    {
        return new(
            DocumentIdentity: new([new(IdentityValue: "", IdentityJsonPath: new("$"))]),
            ReferentialId: new ReferentialId(referentialId),
            DocumentReferences: documentReferences ?? [],
            DescriptorReferences: descriptorReferences ?? [],
            SuperclassIdentity: superclassIdentity
        );
    }

    public record Reference(string ResourceName, Guid ReferentialIdGuid);

    protected static DocumentReference CreateDocumentReference(Reference reference)
    {
        return new(
            ResourceInfo: CreateResourceInfo(reference.ResourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(reference.ReferentialIdGuid)
        );
    }

    protected static DescriptorReference CreateDescriptorReference(Reference reference)
    {
        return new(
            ResourceInfo: CreateResourceInfo(reference.ResourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(reference.ReferentialIdGuid),
            Path: new JsonPath()
        );
    }

    protected static SuperclassIdentity CreateSuperclassIdentity(string resourceName, Guid referentialIdGuid)
    {
        return new(
            ResourceInfo: CreateResourceInfo(resourceName),
            DocumentIdentity: new([]),
            ReferentialId: new ReferentialId(referentialIdGuid)
        );
    }

    protected static DocumentReference[] CreateDocumentReferences(Reference[] references)
    {
        return references.Select(x => CreateDocumentReference(x)).ToArray();
    }

    protected static DescriptorReference[] CreateDescriptorReferences(Reference[] references)
    {
        return references.Select(x => CreateDescriptorReference(x)).ToArray();
    }

    protected static IUpsertRequest CreateUpsertRequest(
        string resourceName,
        Guid documentUuidGuid,
        Guid referentialIdGuid,
        string edfiDocString,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null,
        bool allowIdentityUpdates = false
    )
    {
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName, allowIdentityUpdates),
                DocumentInfo = CreateDocumentInfo(referentialIdGuid, documentReferences, descriptorReferences, superclassIdentity),
                EdfiDoc = JsonNode.Parse(edfiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpsertRequest>();
    }

    protected static List<IUpsertRequest> CreateMultipleInsertRequest(string resourceName, string[] documents)
    {
        List<IUpsertRequest> result = [];
        foreach (var document in documents)
        {
            result.Add(CreateUpsertRequest(resourceName, Guid.NewGuid(), Guid.NewGuid(), document));
        }
        return result;
    }

    protected static IUpdateRequest CreateUpdateRequest(
        string resourceName,
        Guid documentUuidGuid,
        Guid referentialIdGuid,
        string edFiDocString,
        DocumentReference[]? documentReferences = null,
        DescriptorReference[]? descriptorReferences = null,
        SuperclassIdentity? superclassIdentity = null,
        bool allowIdentityUpdates = false
    )
    {
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName, allowIdentityUpdates),
                DocumentInfo = CreateDocumentInfo(referentialIdGuid, documentReferences, descriptorReferences, superclassIdentity),
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpdateRequest>();
    }

    protected static IGetRequest CreateGetRequest(string resourceName, Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IGetRequest>();
    }

    protected static IQueryRequest CreateQueryRequest(
        string resourceName,
        Dictionary<string, string>? searchParameters,
        PaginationParameters? paginationParameters
    )
    {
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                SearchParameters = searchParameters,
                PaginationParameters = paginationParameters,
                TraceId = new TraceId("123")
            }
        ).ActLike<IQueryRequest>();
    }

    protected static IDeleteRequest CreateDeleteRequest(string resourceName, Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo = CreateResourceInfo(resourceName),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IDeleteRequest>();
    }

    /// <summary>
    /// Takes a DB setup function and two DB operation functions. The setup function is given
    /// a managed connection and transaction and runs on the current thread. Transaction
    /// commit is provided and no results are returned to the caller.
    ///
    /// The two functions which perform DB operations have their transactions orchestrated
    /// such that the first transaction executes without committing, then the second transaction
    /// executes and blocks in the DB until the first transaction is committed, at which
    /// point the second transaction either completes or aborts due to a concurrency problem.
    ///
    /// The first operation is given a managed connection and transaction provided
    /// on the current thread, whereas the second operation has a managed connection and
    /// transaction provided to it on a separate thread.
    ///
    /// This method returns the operation results for each operation performed.
    ///
    /// </summary>
    protected async Task<(T?, U?)> OrchestrateOperations<T, U>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task> setup,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> dbOperation1,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<U>> dbOperation2
    )
    {
        // Connection and transaction for the setup
        await using var connectionForSetup = await DataSource!.OpenConnectionAsync();
        await using var transactionForSetup = await connectionForSetup.BeginTransactionAsync(
            IsolationLevel.RepeatableRead
        );

        // Run the setup
        await setup(connectionForSetup, transactionForSetup);

        // Commit the setup
        await transactionForSetup.CommitAsync();

        // Cleanup setup connection/transaction to avoid confusion
        await transactionForSetup.DisposeAsync();
        await connectionForSetup.DisposeAsync();

        // Connection and transaction managed in this method for DB transaction 1
        await using var connection1 = await DataSource!.OpenConnectionAsync();
        await using var transaction1 = await connection1.BeginTransactionAsync(ConfiguredIsolationLevel);

        // Use these for threads to signal each other for coordination
        using EventWaitHandle Transaction1Go = new AutoResetEvent(false);
        using EventWaitHandle Transaction2Go = new AutoResetEvent(false);

        // Connection and transaction managed in this method for DB transaction 2
        NpgsqlConnection? connection2 = null;
        NpgsqlTransaction? transaction2 = null;

        T? result1 = default;
        U? result2 = default;

        // DB Transaction 2's thread
        _ = Task.Run(async () =>
        {
            // Step #0: Wait for signal from transaction 1 thread to start
            Transaction2Go?.WaitOne();

            // Step #3: Create new connection and begin DB transaction 2
            connection2 = await DataSource!.OpenConnectionAsync();
            transaction2 = await connection2.BeginTransactionAsync(ConfiguredIsolationLevel);

            // Step #4: Signal to transaction 1 thread to continue in parallel
            Transaction1Go?.Set();

            // Step #6: Execute DB operation 2, which will block in DB until transaction 1 commits
            result2 = await dbOperation2(connection2, transaction2);

            // Step #9: DB operation unblocked by DB transaction 1 commit, so now we can commit
            try
            {
                await transaction2.CommitAsync();
            }
            catch (Exception)
            {
                //This transaction was completed or rolled back as part of a retry, we must
                //swallow this exception
            }

            // Step #10: Tell transaction 1 thread we are done
            Transaction1Go?.Set();
        });

        // Step #1: Execute DB operation 1 without committing
        result1 = await dbOperation1(connection1, transaction1);

        // Step #2: Yield to transaction 2 thread
        Transaction2Go?.Set();
        Transaction1Go?.WaitOne();

        // Step #5: Give transaction 2 thread time for it's blocking DB operation to execute on DB
        Thread.Sleep(1000);

        // Step #7: Commit DB transaction 1, which will unblock transaction 2's DB operation
        await transaction1.CommitAsync();

        // Step #8: Wait for transaction 2 thread to finish
        Transaction1Go?.WaitOne();

        // Step #11: Cleanup and return
        transaction2?.Dispose();
        connection2?.Dispose();

        return (result1, result2);
    }
}
