// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Configuration
{
    public class AppSettings
    {
        public int BeginAllowedSchoolYear { get; set; }
        public int EndAllowedSchoolYear { get; set; }
        public required AuthenticationSettings Authentication { get; set; }
    }

    public class AuthenticationSettings
    {
        public required string SigningKey { get; set; }
        public required string Authority { get; set; }
        public required string Issuer { get; set; }
    }
}
