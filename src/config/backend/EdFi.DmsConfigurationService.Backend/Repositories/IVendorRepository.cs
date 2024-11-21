// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IVendorRepository
{
    Task<VendorInsertResult> InsertVendor(VendorInsertCommand command);
    Task<VendorQueryResult> QueryVendor(PagingQuery query);
    Task<VendorGetResult> GetVendor(long id);
    Task<VendorUpdateResult> UpdateVendor(VendorUpdateCommand command);
    Task<VendorDeleteResult> DeleteVendor(long id);
    Task<VendorApplicationsResult> GetVendorApplications(long vendorId);
}

public record VendorInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : VendorInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorInsertResult();
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
    /// Successfully updated vendor
    /// </summary>
    public record Success() : VendorUpdateResult();

    /// <summary>
    /// Vendor id not found
    /// </summary>
    public record FailureNotExists() : VendorUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorUpdateResult();
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
