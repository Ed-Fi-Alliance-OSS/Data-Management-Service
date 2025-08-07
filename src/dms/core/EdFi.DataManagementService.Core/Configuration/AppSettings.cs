// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

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
    /// If true, uses the UseApiSchemaPath file system path to find and load ApiSchema.json
    /// files. Otherwise, the bundled ApiSchema.json files will be loaded.
    /// </summary>
    public bool UseApiSchemaPath { get; set; }

    /// <summary>
    /// Provides the file system path for ApiSchema.json files loaded at startup,
    /// including both core and extension files.
    /// </summary>
    public string? ApiSchemaPath { get; set; }

    /// <summary>
    /// Get or set the authentication service URL.
    /// </summary>
    public string? AuthenticationService { get; set; }

    /// <summary>
    /// If true, enables management endpoints like schema reload.
    /// </summary>
    public bool EnableManagementEndpoints { get; set; }

    /// <summary>
    /// Comma-separated list of domain names to exclude from OpenAPI documentation generation.
    /// Domains listed here will not appear in the generated OpenAPI specifications.
    /// </summary>
    public string DomainsExcludedFromOpenApi { get; set; } = string.Empty;
}
