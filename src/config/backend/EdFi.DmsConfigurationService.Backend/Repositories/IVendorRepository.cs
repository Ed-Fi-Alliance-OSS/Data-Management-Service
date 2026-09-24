// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IVendorRepository
{
    Task<VendorInsertResult> InsertVendor(VendorInsertCommand command);
    Task<VendorQueryResult> QueryVendor(VendorQuery query);
    Task<VendorGetResult> GetVendor(int id);
    Task<VendorUpdateResult> UpdateVendor(VendorUpdateCommand command);
    Task<VendorDeleteResult> DeleteVendor(int id);
    Task<VendorApplicationsResult> GetVendorApplications(int vendorId);

    /// <summary>
    /// Reads the update-relevant state of a Vendor and every ApiClient it owns inside one
    /// transaction that row-locks the Vendor row. Locking the row waits out any in-flight vendor
    /// update, so the returned snapshot reflects that transaction's final outcome, and the client
    /// list belongs to the same snapshot.
    /// </summary>
    Task<VendorUpdateStateResult> GetVendorUpdateState(int vendorId);
}

public record VendorInsertResult
{
    /// <summary>
    /// Successful vendor insert or update (upsert by natural key).
    /// </summary>
    /// <param name="Id">The Id of the inserted or updated vendor.</param>
    /// <param name="IsNewVendor">True if the vendor was newly inserted; false if an existing vendor was updated.</param>
    public record Success(int Id, bool IsNewVendor) : VendorInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorInsertResult();

    /// <summary>
    /// Company Name must be unique
    /// </summary>
    public record FailureDuplicateCompanyName() : VendorInsertResult();
}

public record VendorQueryResult
{
    /// <summary>
    /// Successfully queried and returning list of vendor responses
    /// </summary>
    public record Success(IEnumerable<VendorResponse> VendorResponses) : VendorQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorQueryResult();
}

public record VendorGetResult
{
    /// <summary>
    /// Successfully retrieved vendor and returning vendor response
    /// </summary>
    public record Success(VendorResponse VendorResponse) : VendorGetResult();

    /// <summary>
    /// Vendor does not exist in the datastore
    /// </summary>
    public record FailureNotFound() : VendorGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorGetResult();
}

public record VendorUpdateResult
{
    /// <summary>
    /// Successfully updated vendor. The affected clients are not reported here: a caller that
    /// needs them resolves them through <see cref="IVendorRepository.GetVendorUpdateState"/>
    /// before mutating anything, which reads them under the lock rather than after the commit.
    /// </summary>
    public record Success() : VendorUpdateResult();

    /// <summary>
    /// Vendor id not found
    /// </summary>
    public record FailureNotExists() : VendorUpdateResult();

    /// <summary>
    /// Another vendor in the same tenant already has the requested company name. The unique
    /// violation rolled the update back, so nothing was committed.
    /// </summary>
    public record FailureDuplicateCompanyName() : VendorUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorUpdateResult();
}

/// <summary>
/// One ApiClient owned by a vendor, carrying the stable identity a namespace-claim update needs:
/// the row to write, the identity-provider client to mutate, and the application aggregate whose
/// lock serializes the mutation.
/// </summary>
public record VendorApiClient(int Id, string ClientId, Guid ClientUuid, int ApplicationId);

/// <summary>
/// The complete state a Vendor update mutates, together with every ApiClient the vendor owns.
/// </summary>
public record VendorUpdateState(
    string Company,
    string? ContactName,
    string? ContactEmailAddress,
    string NamespacePrefixes,
    VendorApiClient[] Clients
);

public record VendorUpdateStateResult
{
    public record Success(VendorUpdateState State) : VendorUpdateStateResult();

    /// <summary>
    /// The vendor does not exist, or belongs to another tenant.
    /// </summary>
    public record FailureNotExists() : VendorUpdateStateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorUpdateStateResult();
}

public record VendorDeleteResult
{
    public record Success() : VendorDeleteResult();

    /// <summary>
    /// Vendor id not found
    /// </summary>
    public record FailureNotExists() : VendorDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorDeleteResult();
}

public record VendorApplicationsResult
{
    /// <summary>
    /// Successfully fetch applications for vendor and returning application responses
    /// </summary>
    public record Success(IEnumerable<ApplicationResponse> ApplicationResponses) : VendorApplicationsResult();

    /// <summary>
    /// Referenced vendor not found in data store
    /// </summary>
    public record FailureNotExists() : VendorApplicationsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorApplicationsResult();
}
