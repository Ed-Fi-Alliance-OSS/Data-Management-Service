// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

public class AppSettings
{
    /// <summary>
    /// Bypasses schema-guided request value type coercion.
    /// </summary>
    public bool BypassTypeCoercion { get; set; }

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
    /// If true, loads manifest-backed ApiSchema workspace content from ApiSchemaPath.
    /// Otherwise, loads the bundled manifest-backed ApiSchema workspace from the application output.
    /// </summary>
    public bool UseApiSchemaPath { get; set; }

    /// <summary>
    /// Provides the file system path for a runtime ApiSchema workspace containing
    /// bootstrap-api-schema-manifest.json and its declared core and extension schema files.
    /// </summary>
    public string? ApiSchemaPath { get; set; }

    /// <summary>
    /// Get or set the authentication service URL.
    /// </summary>
    public string? AuthenticationService { get; set; }

    /// <summary>
    /// If true, enables management endpoints like claimset reload.
    /// </summary>
    public bool EnableManagementEndpoints { get; set; }

    /// <summary>
    /// If true, enables management endpoints for claimset reload operations.
    /// </summary>
    public bool EnableClaimsetReload { get; set; }

    /// <summary>
    /// Comma-separated list of domain names to exclude from OpenAPI documentation generation.
    /// Domains listed here will not appear in the generated OpenAPI specifications.
    /// </summary>
    public string? DomainsExcludedFromOpenApi { get; set; }

    /// <summary>
    /// If true, enables multi-tenancy support with tenant-scoped data isolation.
    /// When enabled, tenants are identified via URL path segments.
    /// </summary>
    public bool MultiTenancy { get; set; }

    /// <summary>
    /// If true, authorization is bypassed and OIDC metadata is not warmed up at startup.
    /// </summary>
    public bool BypassAuthorization { get; set; }
}
