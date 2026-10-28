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

    /// <summary>
    /// Saves an API client update. When <see cref="ApiClientUpdateCommand.CompletesIdentityProviderSync"/>
    /// is set, the same transaction also clears the client's pending identity provider
    /// synchronization, while the client's Application still has
    /// <see cref="ApiClientUpdateCommand.SentClaimSetName"/>; otherwise any pending marker is kept
    /// with its original time.
    /// </summary>
    Task<ApiClientUpdateResult> UpdateApiClient(ApiClientUpdateCommand command);
    Task<ApiClientDeleteResult> DeleteApiClient(int id);
    Task<ApiClientQueryResult> QueryApiClient(ApiClientQuery query);
    Task<ApiClientGetResult> GetApiClientByClientId(string clientId);
    Task<ApiClientGetResult> GetApiClientById(int id);

    /// <summary>
    /// Reads the complete update-relevant state of an ApiClient inside a row-locking
    /// transaction. Locking the row waits out any in-flight update transaction, so the returned
    /// snapshot reflects that transaction's final outcome.
    /// </summary>
    Task<ApiClientResolutionResult> GetApiClientResolutionState(int id);

    /// <summary>
    /// Atomically sets the stored identity-provider client UUID, guarded by its expected current
    /// value, inside one row-locking transaction. When the target row is missing, the result
    /// distinguishes whether any row still references the new UUID so the caller can decide
    /// whether deleting the recreated provider client is safe.
    /// </summary>
    Task<ApiClientUuidSyncResult> SyncApiClientUuid(int id, Guid expectedClientUuid, Guid newClientUuid);

    /// <summary>
    /// Reports whether any ApiClient row references the given identity-provider client UUID.
    /// The check is deliberately cross-tenant: it protects a provider-level object before a
    /// compensation deletion and exposes no tenant data.
    /// </summary>
    Task<ApiClientUuidReferenceResult> HasApiClientUuidReference(Guid clientUuid);

    /// <summary>
    /// Reads an ApiClient's education organization state: its inheritance flag, stored list,
    /// effective list and identity-provider sync-pending timestamp, as one committed state: never
    /// a mix of states before and after a concurrent scope write, move or Application save.
    /// PostgreSQL reads it in one statement, which sees one snapshot. SQL Server holds shared
    /// locks to commit, taken Application first and then the client and its rows, the order an
    /// Application save takes them; it restarts, a bounded number of times, when the client moves
    /// before its lock is taken.
    /// </summary>
    Task<ApiClientEducationOrganizationStateResult> GetApiClientEducationOrganizationState(int id);

    /// <summary>
    /// Clears the time since which an ApiClient awaits an identity-provider update for an
    /// Application change, once the provider has confirmed that update. The marker is cleared only
    /// while the client's Application still has <paramref name="sentClaimSetName"/>, the claim set
    /// name the provider was sent; a claim set rename committed since then keeps it pending.
    /// </summary>
    Task<ApiClientSyncPendingResult> ClearApiClientIdentityProviderSyncPending(
        int id,
        string sentClaimSetName
    );
}

/// <summary>
/// An ApiClient's education organization state. <paramref name="StoredEducationOrganizationIds"/>
/// are the client's own rows; <paramref name="EffectiveEducationOrganizationIds"/> are the current
/// Application's rows for an inheriting client and the stored rows for a narrowed one. Both are
/// sorted, and either can be empty.
/// </summary>
public record ApiClientEducationOrganizationState(
    int ApplicationId,
    bool InheritsApplicationEducationOrganizations,
    long[] StoredEducationOrganizationIds,
    long[] EffectiveEducationOrganizationIds,
    DateTime? IdentityProviderSyncPendingSince
);

public record ApiClientEducationOrganizationStateResult
{
    public record Success(ApiClientEducationOrganizationState State)
        : ApiClientEducationOrganizationStateResult();

    /// <summary>
    /// The ApiClient does not exist in the current tenant.
    /// </summary>
    public record FailureNotFound() : ApiClientEducationOrganizationStateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientEducationOrganizationStateResult();
}

public record ApiClientSyncPendingResult
{
    public record Success() : ApiClientSyncPendingResult();

    /// <summary>
    /// The ApiClient does not exist in the current tenant; nothing was written.
    /// </summary>
    public record FailureNotFound() : ApiClientSyncPendingResult();

    /// <summary>
    /// The client's Application no longer has the claim set name the provider was sent, so the
    /// marker is kept; nothing was written.
    /// </summary>
    public record FailureClaimSetChanged() : ApiClientSyncPendingResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApiClientSyncPendingResult();
}

/// <summary>
/// The complete state an ApiClient update mutates, including the exact data store set and the
/// education organization scope: the inheritance flag and the client's own rows.
/// </summary>
public record ApiClientResolutionState(
    int ApplicationId,
    string Name,
    bool IsApproved,
    string ClientId,
    Guid ClientUuid,
    int[] DataStoreIds,
    bool InheritsApplicationEducationOrganizations,
    long[] StoredEducationOrganizationIds,
    // When the client's identity provider synchronization began pending (UTC); null when none is
    DateTime? IdentityProviderSyncPendingSince = null
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
    /// The target row matched the expected UUID, but another row already references the new UUID;
    /// nothing was written, and the provider client behind the new UUID belongs to that other row.
    /// </summary>
    public record FailureNewUuidInUse() : ApiClientUuidSyncResult();

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
    public record Success(int Id) : ApiClientInsertResult();

    /// <summary>
    /// Referenced application not found exception thrown and caught
    /// </summary>
    public record FailureApplicationNotFound() : ApiClientInsertResult();

    /// <summary>
    /// Referenced Data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : ApiClientInsertResult();

    /// <summary>
    /// Another API client of the same application already has this name; nothing was written
    /// </summary>
    public record FailureDuplicateName() : ApiClientInsertResult();

    /// <summary>
    /// Another API client, under any application or tenant, already has this client key; nothing was written
    /// </summary>
    public record FailureDuplicateClientId() : ApiClientInsertResult();

    /// <summary>
    /// Another API client, under any application or tenant, already references this identity-provider
    /// client UUID; nothing was written
    /// </summary>
    public record FailureDuplicateClientUuid() : ApiClientInsertResult();

    /// <summary>
    /// The requested narrowed scope has education organizations that are not on the Application;
    /// nothing was written
    /// </summary>
    public record FailureRequestedEducationOrganizationsNotOnApplication(long[] EducationOrganizationIds)
        : ApiClientInsertResult();

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
    /// Another API client of the same application already has this name; nothing was written
    /// </summary>
    public record FailureDuplicateName() : ApiClientUpdateResult();

    /// <summary>
    /// The identity-provider client UUID the update would store is already referenced by another API
    /// client; nothing was written
    /// </summary>
    public record FailureDuplicateClientUuid() : ApiClientUpdateResult();

    /// <summary>
    /// The update requests no scope and moves a narrowed client to an Application that lacks some
    /// of its education organizations; nothing was written
    /// </summary>
    public record FailureEducationOrganizationsNotOnApplication(long[] EducationOrganizationIds)
        : ApiClientUpdateResult();

    /// <summary>
    /// The requested narrowed scope has education organizations that are not on the client's
    /// Application, its new one on a move; nothing was written
    /// </summary>
    public record FailureRequestedEducationOrganizationsNotOnApplication(long[] EducationOrganizationIds)
        : ApiClientUpdateResult();

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
