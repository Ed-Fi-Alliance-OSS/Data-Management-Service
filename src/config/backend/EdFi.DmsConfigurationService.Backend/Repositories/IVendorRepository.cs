// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Vendor;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IVendorRepository : IRepository<Vendor>
{
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
