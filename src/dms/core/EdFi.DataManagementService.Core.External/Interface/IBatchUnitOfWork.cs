// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// Abstraction for executing multiple operations against a shared backend transaction.
/// </summary>
public interface IBatchUnitOfWork : IAsyncDisposable
{
    Task<UpsertResult> UpsertDocumentAsync(IUpsertRequest request);
    Task<UpdateResult> UpdateDocumentByIdAsync(IUpdateRequest request);
    Task<DeleteResult> DeleteDocumentByIdAsync(IDeleteRequest request);

    /// <summary>
    /// Resolves a document UUID from the provided natural key identity.
    /// Returns null when the document does not exist.
    /// </summary>
    Task<DocumentUuid?> ResolveDocumentUuidAsync(
        ResourceInfo resourceInfo,
        DocumentIdentity identity,
        TraceId traceId
    );

    Task CommitAsync();
    Task RollbackAsync();
}

public interface IBatchUnitOfWorkFactory
{
    Task<IBatchUnitOfWork> BeginAsync(TraceId traceId, IReadOnlyDictionary<string, string> headers);
}
