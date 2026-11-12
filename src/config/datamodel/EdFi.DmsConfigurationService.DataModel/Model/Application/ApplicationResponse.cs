// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

public class ApplicationResponse
{
    public int Id { get; set; }
    public required string ApplicationName { get; set; }
    public int VendorId { get; set; }
    public required string ClaimSetName { get; set; }
    public List<long> EducationOrganizationIds { get; set; } = [];

    [Description(
        "The data stores assigned to the Application's API clients. An Application with no API "
            + "clients reports an empty array."
    )]
    public List<int> DataStoreIds { get; set; } = [];
    public List<int> ProfileIds { get; set; } = [];

    [Description(
        "Computed from the Application's API clients; no Application request sets it. True only "
            + "when there is at least one API client and every API client the read considers is "
            + "approved; the Application and vendor Application reads consider all of the "
            + "Application's API clients. An Application with no API clients reports false."
    )]
    public bool Enabled { get; set; }
}
