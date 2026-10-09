// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The job error codes for permanent projection read failures (DMS-1440 spec §5.5), registered at startup by
/// <see cref="ServiceCollectionExtensions.AddDmsEducationOrganizationProjectionReader"/>. Each message is fixed text
/// that names no tenant, data store, URL or DMS response value. Transient failures have no code: the job layer retries
/// them and ends in <c>AttemptsExhausted</c>.
/// </summary>
public static class EducationOrganizationProjectionJobErrorCodes
{
    public const string NotConfigured = "EdOrgProjectionNotConfigured";
    public const string DiscoveryInvalid = "EdOrgProjectionDiscoveryInvalid";

    /// <summary>Also used for an unsupported contract version.</summary>
    public const string Unsupported = "EdOrgProjectionUnsupported";

    /// <summary>Also used for a rejected token request.</summary>
    public const string Unauthorized = "EdOrgProjectionUnauthorized";

    public const string Forbidden = "EdOrgProjectionForbidden";
    public const string TargetNotFound = "EdOrgProjectionTargetNotFound";
    public const string TargetNotRoutable = "EdOrgProjectionTargetNotRoutable";
    public const string TargetSchemaIncompatible = "EdOrgProjectionTargetSchemaIncompatible";
    public const string DataInvalid = "EdOrgProjectionDataInvalid";
    public const string InvalidRequest = "EdOrgProjectionInvalidRequest";
    public const string MalformedResponse = "EdOrgProjectionMalformedResponse";
    public const string UnexpectedResponse = "EdOrgProjectionUnexpectedResponse";
    public const string LimitExceeded = "EdOrgProjectionLimitExceeded";

    /// <summary>Every code with its fixed public message, in registration order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> All { get; } =
    [
        new(
            NotConfigured,
            "The Configuration Service is not configured to read education organizations from the Data Management Service."
        ),
        new(
            DiscoveryInvalid,
            "The Data Management Service Discovery document could not be used to locate the education organization projection."
        ),
        new(
            Unsupported,
            "The Data Management Service does not offer an education organization projection contract this service supports."
        ),
        new(
            Unauthorized,
            "The Data Management Service did not accept the Configuration Service credentials for the education organization projection."
        ),
        new(
            Forbidden,
            "The Configuration Service client is not permitted to read the education organization projection."
        ),
        new(
            TargetNotFound,
            "The Data Management Service did not find the data store for the education organization projection."
        ),
        new(
            TargetNotRoutable,
            "The data store's contexts do not complete the Data Management Service education organization projection route."
        ),
        new(
            TargetSchemaIncompatible,
            "The data store's database is not compatible with the education organization projection."
        ),
        new(
            DataInvalid,
            "The education organization data in the data store cannot be projected because it is invalid."
        ),
        new(
            InvalidRequest,
            "The Data Management Service rejected the education organization projection request."
        ),
        new(
            MalformedResponse,
            "The Data Management Service returned a malformed education organization projection response."
        ),
        new(
            UnexpectedResponse,
            "The Data Management Service returned an unexpected response to the education organization projection request."
        ),
        new(LimitExceeded, "The education organization projection exceeded a configured size limit."),
    ];
}
