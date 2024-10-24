// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel
{
    public class ApplicationInsertCommand
    {
        public required string ApplicationName { get; set; }
        public long VendorId { get; set; }
        public required string ClaimSetName { get; set; }
        public long[] EducationOrganizationIds { get; set; } = [];
    }

    public class ApplicationUpdateCommand
    {
        public long Id { get; set; }
        public required string ApplicationName { get; set; }
        public long VendorId { get; set; }
        public required string ClaimSetName { get; set; }
        public long[] EducationOrganizationIds { get; set; } = [];
    }

    public class ApplicationQuery
    {
        public int Offset { get; set; }
        public int Limit { get; set; }
    }

    public class ApplicationInsertResponse
    {
        public long Id { get; set; }
        public required string Key { get; set; }
        public required string Secret { get; set; }
    }

    public class ApplicationResponse
    {
        public long Id { get; set; }
        public required string ApplicationName { get; set; }
        public long VendorId { get; set; }
        public required string ClaimSetName { get; set; }
        public List<long> EducationOrganizationIds { get; set; } = [];
    }
}
