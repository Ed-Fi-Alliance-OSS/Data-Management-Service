// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration
{
    public class AppSettings
    {
        public int BeginAllowedSchoolYear { get; set; }
        public int EndAllowedSchoolYear { get; set; }
        public required string AuthenticationService { get; set; }
        public required string DatabaseEngine { get; set; }
        public bool DeployDatabaseOnStartup { get; set; }
        public bool BypassStringTypeCoercion { get; set; }
    }
}
