// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;

namespace EdFi.DmsConfigurationService.Backend;

public interface IVendorRepository : IRepository<Vendor>
{
    /// <summary>
    /// Get a collection of applications associated with a vendor
    /// </summary>
    Task<GetResult<Vendor>> GetVendorByIdWithApplicationsAsync(long vendorId);
}
