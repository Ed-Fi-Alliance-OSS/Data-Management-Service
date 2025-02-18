// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration
{
    public class AppSettings
    {
        public bool BypassStringTypeCoercion { get; set; }

        /// <summary>
        /// Comma separated list of resource names that allow identity updates,
        /// overriding the default behavior to reject identity updates.
        /// </summary>
        public required string AllowIdentityUpdateOverrides { get; set; }

        /// <summary>
        /// Know whether to mask the requested Body
        /// </summary>
        public bool MaskRequestBodyInLogs { get; set; }

        /// <summary>
        /// Indicates the maximum number of items that should be returned in the results
        /// </summary>
        public int MaximumPageSize { get; set; }

        /// <summary>
        /// Indicates the where ApiSchema Folder exists 
        /// </summary>
        public required string ApiSchemaFolder { get; set; }
    }
}
