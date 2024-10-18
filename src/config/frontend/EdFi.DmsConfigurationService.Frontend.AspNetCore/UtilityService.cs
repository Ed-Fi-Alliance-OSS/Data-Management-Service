// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore
{
    public static partial class UtilityService
    {
        // Matches all of the following sample expressions:
        // /v2/vendors
        // /v2/vendors/
        // /v2/vendors/id
        [GeneratedRegex(@"\/(?<version>[^/]+)\/(?<endpointName>[^/]+)(\/|$)((?<Id>[^/]*$))?")]
        public static partial Regex PathExpressionRegex();
    }
}
