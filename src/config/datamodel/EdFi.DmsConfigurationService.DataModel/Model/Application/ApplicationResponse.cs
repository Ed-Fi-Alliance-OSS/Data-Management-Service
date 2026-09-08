// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

public class ApplicationResponse
{
    public int Id { get; set; }
    public required string ApplicationName { get; set; }
    public int VendorId { get; set; }
    public required string ClaimSetName { get; set; }
    public List<long> EducationOrganizationIds { get; set; } = [];
    public List<int> DataStoreIds { get; set; } = [];
    public List<int> ProfileIds { get; set; } = [];
    public bool Enabled { get; set; }
}
