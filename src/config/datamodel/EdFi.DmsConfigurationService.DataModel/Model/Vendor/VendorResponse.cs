// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Vendor;

public class VendorResponse
{
    public long Id { get; set; }
    public required string Company { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmailAddress { get; set; }
    public required string NamespacePrefixes { get; set; }
}
