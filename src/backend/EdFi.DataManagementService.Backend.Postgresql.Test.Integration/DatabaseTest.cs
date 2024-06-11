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
        Transaction = await Connection.BeginTransactionAsync(IsolationLevel.Serializable);
    }

    [TearDown]
    public void ConnectionTeardown()
    {
        Transaction?.Dispose();
        Connection?.Dispose();
    }

    protected static UpsertDocument CreateUpsert()
    {
        return new UpsertDocument(new SqlAction(), NullLogger<UpsertDocument>.Instance);
    }

    protected static UpdateDocumentById CreateUpdate()
    {
        return new UpdateDocumentById(new SqlAction(), NullLogger<UpdateDocumentById>.Instance);
    }

    protected static GetDocumentById CreateGetById()
    {
        return new GetDocumentById(new SqlAction(), NullLogger<GetDocumentById>.Instance);
    }

    protected static GetDocumentByKey GetDocumentByKey(NpgsqlDataSource dataSource)
    {
        return new GetDocumentByKey(dataSource, new SqlAction(), NullLogger<GetDocumentByKey>.Instance);
    }

    protected static DeleteDocumentById CreateDeleteById(NpgsqlDataSource dataSource)
    {
        return new DeleteDocumentById(new SqlAction(), NullLogger<DeleteDocumentById>.Instance);
    }

    protected static T AsValueType<T, TU>(TU value)
        where T : class
    {
        return (new { Value = value }).ActLike<T>();
    }

    public static readonly IResourceInfo ResourceInfo = (
        new
        {
            ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
            AllowIdentityUpdates = false,
            ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
            ResourceName = AsValueType<IMetaEdResourceName, string>("ResourceName"),
            IsDescriptor = false
        }
    ).ActLike<IResourceInfo>();

    public static readonly IDocumentInfo DocumentInfo = (
        new
        {
            DocumentIdentity = (
                new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
            ).ActLike<IResourceInfo>(),
            ReferentialId = new ReferentialId(Guid.Empty),
            DocumentReferences = new List<IDocumentReference>(),
            DescriptorReferences = new List<IDocumentReference>(),
            SuperclassIdentity = null as ISuperclassIdentity
        }
    ).ActLike<IDocumentInfo>();

    protected static IDocumentInfo CreateDocumentInfo(Guid referentialId)
    {
        return (
            new
            {
                DocumentIdentity = (
                    new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
                ).ActLike<IResourceInfo>(),
                ReferentialId = new ReferentialId(referentialId),
                DocumentReferences = new List<IDocumentReference>(),
                DescriptorReferences = new List<IDocumentReference>(),
                SuperclassIdentity = null as ISuperclassIdentity
            }
        ).ActLike<IDocumentInfo>();
    }

    protected static IUpsertRequest CreateUpsertRequest(
        Guid documentUuidGuid,
        Guid referentialId,
        string edFiDocString
    )
    {
        return (
            new
            {
                ResourceInfo,
                DocumentInfo = CreateDocumentInfo(referentialId),
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpsertRequest>();
    }

    protected static List<IUpsertRequest> CreateMultipleUpsertRequest(
        Dictionary<Guid, string> documents,
        string? resourceName
    )
    {
        var upsertRequests = new List<IUpsertRequest>();

        foreach (var document in documents)
        {
            var documentUuidGuid = document.Key;
            var edFiDocString = document.Value;

            // To avoid being taken as an update
            var documentInfo = (
                new
                {
                    DocumentIdentity = (
                        new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
                    ).ActLike<IResourceInfo>(),
                    ReferentialId = new ReferentialId(documentUuidGuid),
                    DocumentReferences = new List<IDocumentReference>(),
                    DescriptorReferences = new List<IDocumentReference>(),
                    SuperclassIdentity = null as ISuperclassIdentity
                }
            ).ActLike<IDocumentInfo>();

            // To set a different resource name if necessary
            IResourceInfo? resourceInfo;
            if (resourceName == string.Empty)
            {
                resourceInfo = _resourceInfo;
            }
            else
            {
                resourceInfo = (
                    new
                    {
                        ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                        AllowIdentityUpdates = false,
                        ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                        ResourceName = AsValueType<IMetaEdProjectName, string>(
                            resourceName == null ? "" : resourceName
                        ),
                        IsDescriptor = false
                    }
                ).ActLike<IResourceInfo>();
            }

            var upsertRequest = (
                new
                {
                    ResourceInfo = resourceInfo,
                    DocumentInfo = documentInfo,
                    EdfiDoc = JsonNode.Parse(edFiDocString),
                    TraceId = new TraceId("123"),
                    DocumentUuid = new DocumentUuid(documentUuidGuid)
                }
            ).ActLike<IUpsertRequest>();

            upsertRequests.Add(upsertRequest);
        }
        return upsertRequests;
    }

    protected static IUpdateRequest CreateUpdateRequest(
        Guid documentUuidGuid,
        Guid referentialIdGuid,
        string edFiDocString
    )
    {
        return (
            new
            {
                ResourceInfo,
                DocumentInfo = (
                    new
                    {
                        DocumentIdentity = (
                            new { IdentityValue = "", IdentityJsonPath = AsValueType<IJsonPath, string>("$") }
                        ).ActLike<IResourceInfo>(),
                        ReferentialId = new ReferentialId(referentialIdGuid),
                        DocumentReferences = new List<IDocumentReference>(),
                        DescriptorReferences = new List<IDocumentReference>(),
                        SuperclassIdentity = null as ISuperclassIdentity
                    }
                ).ActLike<IDocumentInfo>(),
                EdfiDoc = JsonNode.Parse(edFiDocString),
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IUpdateRequest>();
    }

    protected static IGetRequest CreateGetRequest(Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo,
                TraceId = new TraceId("123"),
                DocumentUuid = new DocumentUuid(documentUuidGuid)
            }
        ).ActLike<IGetRequest>();
    }

    protected static IQueryRequest CreateGetRequestByKey(
        Dictionary<string, string>? searchParameters,
        IPaginationParameters? paginationParameters,
        string? resourceName
    )
    {
        // To set a different resource name if necessary
        IResourceInfo? resourceInfo;
        if (resourceName == string.Empty)
        {
            resourceInfo = _resourceInfo;
        }
        else
        {
            resourceInfo = (
                new
                {
                    ResourceVersion = AsValueType<ISemVer, string>("5.0.0"),
                    AllowIdentityUpdates = false,
                    ProjectName = AsValueType<IMetaEdProjectName, string>("ProjectName"),
                    ResourceName = AsValueType<IMetaEdProjectName, string>(
                        resourceName == null ? "" : resourceName
                    ),
                    IsDescriptor = false
                }
            ).ActLike<IResourceInfo>();
        }
        return (
            new
            {
                resourceInfo,
                searchParameters,
                paginationParameters,
                TraceId = new TraceId("123")
            }
        ).ActLike<IQueryRequest>();
    }

    protected static IDeleteRequest CreateDeleteRequest(Guid documentUuidGuid)
    {
        return (
            new
            {
                ResourceInfo,
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
            IsolationLevel.Serializable
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
        await using var transaction1 = await connection1.BeginTransactionAsync(IsolationLevel.Serializable);

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
            transaction2 = await connection2.BeginTransactionAsync(IsolationLevel.Serializable);

            // Step #4: Signal to transaction 1 thread to continue in parallel
            Transaction1Go?.Set();

            // Step #6: Execute DB operation 2, which will block in DB until transaction 1 commits
            result2 = await dbOperation2(connection2, transaction2);

            // Step #9: DB operation unblocked by DB transaction 1 commit, so now we can commit
            await transaction2.CommitAsync();

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
