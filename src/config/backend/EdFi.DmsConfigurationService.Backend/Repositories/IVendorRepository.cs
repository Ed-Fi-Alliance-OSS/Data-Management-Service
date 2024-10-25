// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IVendorRepository
{
    Task<VendorInsertResult> InsertVendor(VendorInsertCommand command);
    Task<VendorQueryResult> QueryVendor(PagingQuery query);
    Task<VendorGetResult> GetVendor(long id);
    Task<VendorUpdateResult> UpdateVendor(VendorUpdateCommand command);
    Task<VendorDeleteResult> DeleteVendor(long id);

    /// <summary>
    /// Get a collection of applications associated with a vendor
    /// </summary>
    Task<GetResult<Vendor>> GetVendorByIdWithApplicationsAsync(long vendorId);
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
    public record Success(IEnumerable<VendorResponse> VendorResponses) : VendorQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorQueryResult();
}

public record VendorGetResult
{
    public record Success(VendorResponse VendorResponse) : VendorGetResult();

    public record FailureNotFound() : VendorGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : VendorGetResult();
}

public record VendorUpdateResult
{
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
