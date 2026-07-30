// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IApiClientRepository
{
    Task<ApiClientInsertResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientCommand clientCommand
    );
    Task<ApiClientUpdateResult> UpdateApiClient(ApiClientUpdateCommand command);
    Task<ApiClientDeleteResult> DeleteApiClient(long id);
    Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query);
    Task<ApiClientGetResult> GetApiClientByClientId(string clientId);
    Task<ApiClientGetResult> GetApiClientById(long id);

    /// <summary>
    /// Reads the complete update-relevant state of an ApiClient inside a row-locking
    /// transaction. Locking the row waits out any in-flight update transaction, so the returned
    /// snapshot reflects that transaction's final outcome.
    /// </summary>
    Task<ApiClientResolutionResult> GetApiClientResolutionState(long id);

    /// <summary>
    /// Atomically sets the stored identity-provider client UUID, guarded by its expected current
    /// value, inside one row-locking transaction. When the target row is missing, the result
    /// distinguishes whether any row still references the new UUID so the caller can decide
    /// whether deleting the recreated provider client is safe.
    /// </summary>
    Task<ApiClientUuidSyncResult> SyncApiClientUuid(long id, Guid expectedClientUuid, Guid newClientUuid);

    /// <summary>
    /// Reports whether any ApiClient row references the given identity-provider client UUID.
    /// The check is deliberately cross-tenant: it protects a provider-level object before a
    /// compensation deletion and exposes no tenant data.
    /// </summary>
    Task<ApiClientUuidReferenceResult> HasApiClientUuidReference(Guid clientUuid);
}

/// <summary>
/// The complete state an ApiClient update mutates, including the exact data store set.
/// </summary>
public record ApiClientResolutionState(
    long ApplicationId,
    string Name,
    bool IsApproved,
    string ClientId,
    Guid ClientUuid,
    long[] DataStoreIds
);

public record ApiClientResolutionResult
{
    public record Success(ApiClientResolutionState State) : ApiClientResolutionResult();

    /// <summary>
    /// The ApiClient no longer exists.
    /// </summary>
    public record FailureNotExists() : ApiClientResolutionResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientResolutionResult();
}

public record ApiClientUuidSyncResult
{
    /// <summary>
    /// The stored UUID matched the expected value and was replaced.
    /// </summary>
    public record Success() : ApiClientUuidSyncResult();

    /// <summary>
    /// The stored UUID already equals the new value; nothing was written.
    /// </summary>
    public record AlreadyApplied() : ApiClientUuidSyncResult();

    /// <summary>
    /// The target row is missing and another row still references the new UUID; deleting the
    /// recreated provider client is not safe.
    /// </summary>
    public record FailureNotExists() : ApiClientUuidSyncResult();

    /// <summary>
    /// The target row is missing and no row references the new UUID; deleting the recreated
    /// provider client is safe while the aggregate lock is held.
    /// </summary>
    public record FailureNotExistsSafeToDelete() : ApiClientUuidSyncResult();

    /// <summary>
    /// The stored UUID matches neither the expected nor the new value; nothing was written.
    /// </summary>
    public record FailureStaleState() : ApiClientUuidSyncResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientUuidSyncResult();
}

public record ApiClientUuidReferenceResult
{
    /// <summary>
    /// No ApiClient row references the UUID.
    /// </summary>
    public record None() : ApiClientUuidReferenceResult();

    /// <summary>
    /// At least one ApiClient row references the UUID.
    /// </summary>
    public record Referenced() : ApiClientUuidReferenceResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientUuidReferenceResult();
}

public record ApiClientInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : ApiClientInsertResult();

    /// <summary>
    /// Referenced application not found exception thrown and caught
    /// </summary>
    public record FailureApplicationNotFound() : ApiClientInsertResult();

    /// <summary>
    /// Referenced Data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : ApiClientInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientInsertResult();
}

public record ApiClientQueryResult
{
    /// <summary>
    /// Successful query.
    /// </summary>
    /// <param name="ApiClientResponses">The ApiClient responses.</param>
    public record Success(List<ApiClientResponse> ApiClientResponses) : ApiClientQueryResult();

    /// <summary>
    /// Unknown failure.
    /// </summary>
    /// <param name="FailureMessage">The failure message.</param>
    public record FailureUnknown(string FailureMessage) : ApiClientQueryResult();
}

public record ApiClientGetResult
{
    /// <summary>
    /// Successful get.
    /// </summary>
    /// <param name="ApiClientResponse">The ApiClient response.</param>
    public record Success(ApiClientResponse ApiClientResponse) : ApiClientGetResult();

    /// <summary>
    /// ApiClient not found.
    /// </summary>
    public record FailureNotFound() : ApiClientGetResult();

    /// <summary>
    /// Unknown failure.
    /// </summary>
    /// <param name="FailureMessage">The failure message.</param>
    public record FailureUnknown(string FailureMessage) : ApiClientGetResult();
}

public record ApiClientUpdateResult
{
    /// <summary>
    /// Successful update.
    /// </summary>
    public record Success() : ApiClientUpdateResult();

    /// <summary>
    /// ApiClient not found.
    /// </summary>
    public record FailureNotFound() : ApiClientUpdateResult();

    /// <summary>
    /// Referenced application not found exception thrown and caught
    /// </summary>
    public record FailureApplicationNotFound() : ApiClientUpdateResult();

    /// <summary>
    /// Referenced Data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : ApiClientUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientUpdateResult();
}

public record ApiClientDeleteResult
{
    /// <summary>
    /// Successful delete.
    /// </summary>
    public record Success() : ApiClientDeleteResult();

    /// <summary>
    /// ApiClient not found.
    /// </summary>
    public record FailureNotFound() : ApiClientDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientDeleteResult();
}
