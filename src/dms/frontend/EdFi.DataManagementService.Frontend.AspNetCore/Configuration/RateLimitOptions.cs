// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration
{
    public class RateLimitOptions
    {
        public const string RateLimit = "RateLimit";

        //Number of requests per window
        public int PermitLimit { get; set; }

        //Number of seconds before permits are reset
        public int Window { get; set; }

        //Number of requests that can be put in queue when there are no more permits
        public int QueueLimit { get; set; }
    }
}
