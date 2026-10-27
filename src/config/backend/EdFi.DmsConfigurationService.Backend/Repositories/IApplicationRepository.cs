// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IApplicationRepository
{
    /// <summary>
    /// Inserts an Application with its education organizations and profiles. When
    /// <see cref="ApplicationInsertCommand.CreateDefaultCredentials"/> is true, the default API
    /// client described by <paramref name="clientCommand"/> and its data store assignments are
    /// inserted too; when false, the Application has no API client and the client command is
    /// not used.
    /// </summary>
    Task<ApplicationInsertResult> InsertApplication(
        ApplicationInsertCommand command,
        ApiClientCommand clientCommand
    );
    Task<ApplicationQueryResult> QueryApplication(ApplicationQuery query);
    Task<ApplicationGetResult> GetApplication(int id);
    Task<ApplicationUpdateResult> UpdateApplication(
        ApplicationUpdateCommand command,
        ApiClientCommand clientCommand
    );

    /// <summary>
    /// Saves an Application change and marks every API client it affects as awaiting an identity
    /// provider update, in one transaction. A name or claim set change affects every client; an
    /// added or removed education organization affects every inheriting client; a removed one also
    /// affects each narrowed client that held it. Affected clients are found before the removal
    /// cascades to their rows. A client already marked keeps its original time. Client keys,
    /// UUIDs, names, approval, data stores and education organization scope choices are unchanged.
    /// On success the result carries the state this transaction committed, including every pending
    /// client, whether or not this save marked it. Only
    /// <see cref="ApplicationSaveResult.FailureCommitOutcomeUnknown"/> leaves the outcome open;
    /// every other failure committed nothing. The caller holds the Application's aggregate lock
    /// (<see cref="IApplicationLockManager"/>), which the API client workflows also take, so no client
    /// of the Application changes during the save; the row locks taken here order the save against
    /// writers outside that lock without deadlocking them.
    /// </summary>
    Task<ApplicationSaveResult> SaveApplication(ApplicationUpdateCommand command);

    /// <summary>
    /// Reads an Application's committed state and its pending clients, in the shape
    /// <see cref="SaveApplication"/> returns. Locking the Application row waits out any in-flight
    /// save, so the result reflects that save's final outcome. Like the save, it is read under the
    /// caller's aggregate lock.
    /// </summary>
    Task<ApplicationSyncStateResult> GetApplicationSyncState(int applicationId);

    Task<ApplicationDeleteResult> DeleteApplication(int id);
    Task<ApplicationApiClientsResult> GetApplicationApiClients(int id);

    /// <summary>
    /// Reads the complete update-relevant state of an Application and one of its clients inside
    /// a row-locking transaction. Locking the Application row waits out any in-flight update
    /// transaction, so the returned snapshot reflects that transaction's final outcome; it also
    /// carries the selected client's exact data store set, which aggregate reads cannot supply.
    /// </summary>
    Task<ApplicationUpdateStateResult> GetApplicationUpdateState(int applicationId, string clientId);

    /// <summary>
    /// Atomically sets the stored identity-provider client UUID, guarded by its expected current
    /// value, inside one row-locking transaction. When the target row is missing, the result
    /// distinguishes whether any row still references the new UUID so the caller can decide
    /// whether deleting the recreated provider client is safe.
    /// </summary>
    Task<ApiClientUuidSyncResult> SyncApplicationApiClientUuid(
        int applicationId,
        string clientId,
        Guid expectedClientUuid,
        Guid newClientUuid
    );
}

/// <summary>
/// The complete state an Application update touches: the Application scalars, its mapping sets,
/// and the selected client's identity and approval. The selected client's exact data store set
/// is read so the update can re-send it unchanged to the identity provider; the update never
/// changes it. The selected client's inheritance flag and own education organization rows decide
/// its effective list: an inheriting client's rows can lag the Application's list, since an
/// Application save never copies added education organizations to them.
/// </summary>
public record ApplicationUpdateState(
    string ApplicationName,
    int VendorId,
    string ClaimSetName,
    // Education organization ids stay 64-bit: the Management API v3 draft declares them int64,
    // deliberately widening relative to Admin API's int. Only CMS resource ids narrow.
    long[] EducationOrganizationIds,
    int[] ProfileIds,
    string ClientId,
    Guid ClientUuid,
    bool IsApproved,
    int[] ClientDataStoreIds,
    bool ClientInheritsApplicationEducationOrganizations,
    long[] ClientStoredEducationOrganizationIds
);

public record ApplicationUpdateStateResult
{
    public record Success(ApplicationUpdateState State) : ApplicationUpdateStateResult();

    /// <summary>
    /// The application or the selected client no longer exists.
    /// </summary>
    public record FailureNotExists() : ApplicationUpdateStateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationUpdateStateResult();
}

public record ApplicationInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(int Id) : ApplicationInsertResult();

    /// <summary>
    /// Referenced vendor not found exception thrown and caught
    /// </summary>
    public record FailureVendorNotFound() : ApplicationInsertResult();

    /// <summary>
    /// Referenced Data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : ApplicationInsertResult();

    /// <summary>
    /// Referenced profile not found exception thrown and caught
    /// </summary>
    public record FailureProfileNotFound() : ApplicationInsertResult();

    /// <summary>
    /// Application name already exists for the vendor
    /// </summary>
    public record FailureDuplicateApplication(string ApplicationName) : ApplicationInsertResult();

    /// <summary>
    /// Another API client, under any application or tenant, already has the initial client's key; the
    /// whole aggregate was rolled back
    /// </summary>
    public record FailureDuplicateClientId() : ApplicationInsertResult();

    /// <summary>
    /// Another API client, under any application or tenant, already references the initial client's
    /// identity-provider UUID; the whole aggregate was rolled back
    /// </summary>
    public record FailureDuplicateClientUuid() : ApplicationInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationInsertResult();
}

public record ApplicationQueryResult
{
    /// <summary>
    /// A successful query result with responses
    /// </summary>
    public record Success(IEnumerable<ApplicationResponse> ApplicationResponses) : ApplicationQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationQueryResult();
}

public record ApplicationGetResult
{
    /// <summary>
    /// Successful get application with the application response
    /// </summary>
    /// <param name="ApplicationResponse"></param>
    public record Success(ApplicationResponse ApplicationResponse) : ApplicationGetResult();

    /// <summary>
    /// Application not found in data store
    /// </summary>
    public record FailureNotFound() : ApplicationGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationGetResult();
}

public record ApplicationUpdateResult
{
    /// <summary>
    /// The application was updated successfully
    /// </summary>
    public record Success() : ApplicationUpdateResult();

    /// <summary>
    /// Application id not found
    /// </summary>
    public record FailureNotExists() : ApplicationUpdateResult();

    /// <summary>
    /// Referenced vendor not found exception thrown and caught
    /// </summary>
    public record FailureVendorNotFound() : ApplicationUpdateResult();

    /// <summary>
    /// Referenced profile not found exception thrown and caught
    /// </summary>
    public record FailureProfileNotFound() : ApplicationUpdateResult();

    /// <summary>
    /// Application name already exists for the vendor
    /// </summary>
    public record FailureDuplicateApplication(string ApplicationName) : ApplicationUpdateResult();

    /// <summary>
    /// The identity-provider client UUID the update would store is already referenced by another API
    /// client; nothing was written
    /// </summary>
    public record FailureDuplicateClientUuid() : ApplicationUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationUpdateResult();
}

/// <summary>
/// An Application's committed state and the clients still awaiting an identity provider update.
/// </summary>
public record ApplicationSyncState(
    string ApplicationName,
    int VendorId,
    string ClaimSetName,
    long[] EducationOrganizationIds,
    int[] ProfileIds,
    ApplicationSyncPendingClient[] PendingClients
);

/// <summary>
/// A client awaiting an identity provider update, with what that update sends. An inheriting
/// client's education organizations are the Application's; a narrowed client's are its own rows.
/// </summary>
public record ApplicationSyncPendingClient(
    int Id,
    string ClientId,
    Guid ClientUuid,
    bool IsApproved,
    int[] DataStoreIds,
    long[] EducationOrganizationIds,
    DateTime PendingSince
);

public record ApplicationSaveResult
{
    public record Success(ApplicationSyncState State) : ApplicationSaveResult();

    /// <summary>
    /// Application id not found in the current tenant
    /// </summary>
    public record FailureNotExists() : ApplicationSaveResult();

    /// <summary>
    /// Referenced vendor not found
    /// </summary>
    public record FailureVendorNotFound() : ApplicationSaveResult();

    /// <summary>
    /// Referenced profile not found
    /// </summary>
    public record FailureProfileNotFound() : ApplicationSaveResult();

    /// <summary>
    /// Application name already exists for the vendor
    /// </summary>
    public record FailureDuplicateApplication(string ApplicationName) : ApplicationSaveResult();

    /// <summary>
    /// Unexpected exception before the commit; nothing was committed
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationSaveResult();

    /// <summary>
    /// The commit failed without a known outcome; the save may or may not have been committed.
    /// <see cref="IApplicationRepository.GetApplicationSyncState"/> reads what was.
    /// </summary>
    public record FailureCommitOutcomeUnknown(string FailureMessage) : ApplicationSaveResult();
}

public record ApplicationSyncStateResult
{
    public record Success(ApplicationSyncState State) : ApplicationSyncStateResult();

    /// <summary>
    /// Application id not found in the current tenant
    /// </summary>
    public record FailureNotExists() : ApplicationSyncStateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationSyncStateResult();
}

public record ApplicationDeleteResult
{
    /// <summary>
    /// The application was deleted successfully
    /// </summary>
    public record Success() : ApplicationDeleteResult();

    /// <summary>
    /// Application id does not exist in the datastore
    /// </summary>
    public record FailureNotExists() : ApplicationDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationDeleteResult();
}

public record ApplicationApiClientsResult
{
    /// <summary>
    /// Successful retrieval of clientUuids
    /// </summary>
    public record Success(ApiClient[] Clients) : ApplicationApiClientsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationApiClientsResult();
}

/// <summary>
/// Relevant keycloak identifying values for api clients
/// </summary>
public record ApiClient(
    /// <summary>
    /// The identifying string of a client. Must be unique per realm.
    /// </summary>
    string ClientId,
    /// <summary>
    /// The behind the scenes globally unique identifier for the client.
    /// This must be used for deleting the resource and resetting credentials.
    /// </summary>
    Guid ClientUuid,
    /// <summary>
    /// Whether the api client is approved to authenticate.
    /// </summary>
    bool IsApproved
);
