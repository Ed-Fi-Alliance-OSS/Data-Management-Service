// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// The repository DMS Core uses to access the relational document store.
/// </summary>
public interface IDocumentStoreRepository
{
    /// <summary>
    /// Entry point for upsert document requests.
    /// </summary>
    Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest);

    /// <summary>
    /// Entry point for get document by id requests.
    /// </summary>
    Task<GetResult> GetDocumentById(IGetRequest getRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Entry point for update document by id requests.
    /// </summary>
    Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest);

    /// <summary>
    /// Entry point for delete document by id requests.
    /// </summary>
    Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest);
}

/// <summary>
/// The handler DMS Core uses to perform document queries.
/// </summary>
public interface IQueryHandler
{
    /// <summary>
    /// Entry point for query documents requests.
    /// </summary>
    Task<QueryResult> QueryDocuments(
        IQueryRequest queryRequest,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// The handler DMS Core uses to calculate partition boundaries.
/// </summary>
/// <remarks>
/// Dedicated rather than another <see cref="IQueryHandler" /> operation: the query contract is built
/// around hydrated documents and a total count, and a partition request selects identifiers only. The
/// two contracts also differ in what they may carry — a partition request has no page, no profile
/// projection, and no token text.
/// </remarks>
public interface IPartitionQueryHandler
{
    /// <summary>
    /// Entry point for partition boundary requests.
    /// </summary>
    Task<PartitionResult> QueryPartitions(
        IPartitionRequest partitionRequest,
        CancellationToken cancellationToken = default
    );
}
