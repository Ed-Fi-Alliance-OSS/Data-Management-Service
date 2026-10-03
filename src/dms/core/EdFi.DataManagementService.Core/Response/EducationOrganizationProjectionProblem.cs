// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// A problem document the education-organization projection endpoint owns. Each has a fixed
/// status, type, title and detail, pinned by the contract's fixed-literal table
/// (<c>docs/EDUCATION-ORGANIZATION-PROJECTION.md</c>), and is rendered by
/// <see cref="FailureResponse.ForEducationOrganizationProjection"/> with an empty <c>errors</c>
/// array.
/// </summary>
/// <remarks>
/// The Configuration Service classifies these by status and exact type, so a literal here is part
/// of the contract: changing one is a contract change, not a wording fix.
/// </remarks>
internal sealed record EducationOrganizationProjectionProblem(
    int Status,
    string Type,
    string Title,
    string Detail
)
{
    private const string TypePrefix = "urn:ed-fi:api:education-organization-projection";

    public static readonly EducationOrganizationProjectionProblem InvalidCursor = new(
        400,
        $"{TypePrefix}:invalid-cursor",
        "Invalid Cursor",
        "The cursor is not valid for this request. Restart the read without a cursor."
    );

    public static readonly EducationOrganizationProjectionProblem UnsupportedContractVersion = new(
        400,
        $"{TypePrefix}:unsupported-contract-version",
        "Unsupported Contract Version",
        "The requested contract version is not supported."
    );

    public static readonly EducationOrganizationProjectionProblem TargetNotFound = new(
        404,
        $"{TypePrefix}:target-not-found",
        "Target Not Found",
        "The data store could not be found."
    );

    public static readonly EducationOrganizationProjectionProblem TargetProviderUnsupported = new(
        409,
        $"{TypePrefix}:target-provider-unsupported",
        "Target Provider Unsupported",
        "The data store uses a database provider that this Ed-Fi API deployment does not serve."
    );

    public static readonly EducationOrganizationProjectionProblem TargetSchemaIncompatible = new(
        409,
        $"{TypePrefix}:target-schema-incompatible",
        "Target Schema Incompatible",
        "The data store's database schema is not compatible with this Ed-Fi API deployment."
    );

    public static readonly EducationOrganizationProjectionProblem ProjectionUnsupported = new(
        409,
        $"{TypePrefix}:projection-unsupported",
        "Projection Unsupported",
        "The education organization projection is not supported by the data model in use."
    );

    public static readonly EducationOrganizationProjectionProblem ProjectionTooLarge = new(
        409,
        $"{TypePrefix}:projection-too-large",
        "Projection Too Large",
        "The education organization set is larger than this deployment is configured to project."
    );

    public static readonly EducationOrganizationProjectionProblem ProjectionDataInvalid = new(
        409,
        $"{TypePrefix}:projection-data-invalid",
        "Projection Data Invalid",
        "The education organization data contains duplicate identifiers or contradictory relationships."
    );

    public static readonly EducationOrganizationProjectionProblem ProjectionChanged = new(
        409,
        $"{TypePrefix}:projection-changed",
        "Projection Changed",
        "The education organization set changed during the read. Restart the read without a cursor."
    );

    public static readonly EducationOrganizationProjectionProblem TargetUnavailable = new(
        503,
        $"{TypePrefix}:target-unavailable",
        "Target Unavailable",
        "The data store's database is temporarily unavailable. Retry the request later."
    );

    /// <summary>
    /// The shared <c>service-configuration-error</c> type with the projection's fixed detail and no
    /// <c>errors</c> entry, reserved for a missing or undecryptable connection configuration.
    /// </summary>
    public static readonly EducationOrganizationProjectionProblem ServiceConfigurationError = new(
        503,
        "urn:ed-fi:api:service-configuration-error",
        "Service Configuration Error",
        "The data store's database connection is not configured."
    );

    /// <summary>
    /// The shared <c>database-not-provisioned</c> type with the projection's fixed detail and no
    /// <c>errors</c> entry.
    /// </summary>
    public static readonly EducationOrganizationProjectionProblem DatabaseNotProvisioned = new(
        503,
        "urn:ed-fi:api:database-not-provisioned",
        "Database Not Provisioned",
        "The data store's database has not been provisioned."
    );

    /// <summary>Every problem the endpoint owns, in contract-table order.</summary>
    public static IReadOnlyList<EducationOrganizationProjectionProblem> All { get; } =
    [
        InvalidCursor,
        UnsupportedContractVersion,
        TargetNotFound,
        TargetProviderUnsupported,
        TargetSchemaIncompatible,
        ProjectionUnsupported,
        ProjectionTooLarge,
        ProjectionDataInvalid,
        ProjectionChanged,
        TargetUnavailable,
        ServiceConfigurationError,
        DatabaseNotProvisioned,
    ];
}
