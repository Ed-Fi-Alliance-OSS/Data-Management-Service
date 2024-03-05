// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Core.ResponseBody
{
    public static class DataValidationFailureElements
    {
        public static readonly string FailureTitle = "Data Validation Failed";
        public static readonly string FailureType = "urn:ed-fi:api:bad-request:data";
        public static readonly string FailureDetail = "Data validation failed. See errors for details.";
    }
}
