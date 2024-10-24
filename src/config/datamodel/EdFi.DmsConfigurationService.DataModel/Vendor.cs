// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel;

public class Vendor
{
    public long? Id { get; set; }
    public required string Company { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmailAddress { get; set; }
    public required IList<string> NamespacePrefixes { get; set; } = [];
    public IList<Application> Applications { get; set; } = [];
}

public class Application
{
    public long Id { get; set; }
    public required string ApplicationName { get; set; }
    public long VendorId { get; set; }
    public required string ClaimSetName { get; set; }
    public IList<long> EducationOrganizationIds { get; set; } = [];
}
