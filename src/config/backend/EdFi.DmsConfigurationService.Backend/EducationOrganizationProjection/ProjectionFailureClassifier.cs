// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>
/// The DMS-1440 spec §5.5 classification of DMS responses: the rows that apply at every stage, then the Discovery, Token
/// and Page rows. Caller cancellation is not classified here; callers check it first and let it propagate. Problem
/// types are matched exactly (ordinal).
/// </summary>
internal static class ProjectionFailureClassifier
{
    /// <summary>The one 500 problem type that is permanent; matched exactly (ordinal).</summary>
    public const string SecurityConfigurationProblemType = "urn:ed-fi:api:system:configuration:security";

    private const string ProjectionProblemTypePrefix = "urn:ed-fi:api:education-organization-projection:";

    /// <summary>The 400 that allows a restart without a cursor (§5.4).</summary>
    public const string InvalidCursorProblemType = ProjectionProblemTypePrefix + "invalid-cursor";

    public const string UnsupportedContractVersionProblemType =
        ProjectionProblemTypePrefix + "unsupported-contract-version";

    /// <summary>The only 409 that allows a restart (§5.4).</summary>
    public const string ProjectionChangedProblemType = ProjectionProblemTypePrefix + "projection-changed";

    public const string TargetSchemaIncompatibleProblemType =
        ProjectionProblemTypePrefix + "target-schema-incompatible";

    public const string TargetProviderUnsupportedProblemType =
        ProjectionProblemTypePrefix + "target-provider-unsupported";

    public const string ProjectionUnsupportedProblemType =
        ProjectionProblemTypePrefix + "projection-unsupported";

    public const string ProjectionTooLargeProblemType = ProjectionProblemTypePrefix + "projection-too-large";

    public const string ProjectionDataInvalidProblemType =
        ProjectionProblemTypePrefix + "projection-data-invalid";

    /// <summary>Whether an exception from sending a request or reading its body is a transport failure.</summary>
    public static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException or SocketException;

    /// <summary>
    /// The rows for any stage: 3xx, 429 and 5xx. <c>null</c> when the status is left to the stage's own rows.
    /// </summary>
    public static Code? ClassifyAnyStageStatus(HttpStatusCode status, string? problemType)
    {
        int value = (int)status;
        return value switch
        {
            >= 300 and <= 399 => Code.DiscoveryInvalid,
            429 => Code.RateLimited,
            500 when string.Equals(problemType, SecurityConfigurationProblemType, StringComparison.Ordinal) =>
                Code.Forbidden,
            >= 500 and <= 599 => Code.ServiceUnavailable,
            _ => null,
        };
    }

    /// <summary>A Discovery response other than 200: the rows for any stage, then 404, then everything else.</summary>
    public static Code ClassifyDiscoveryStatus(HttpStatusCode status, string? problemType) =>
        ClassifyAnyStageStatus(status, problemType)
        ?? (status == HttpStatusCode.NotFound ? Code.TargetNotFound : Code.UnexpectedResponse);

    /// <summary>A token response other than 200: the rows for any stage, then 400 and 401, then everything else.</summary>
    public static Code ClassifyTokenStatus(HttpStatusCode status, string? problemType) =>
        ClassifyAnyStageStatus(status, problemType)
        ?? (
            status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                ? Code.TokenRejected
                : Code.UnexpectedResponse
        );

    /// <summary>
    /// A page response other than 200: the rows for any stage, then the Page rows. A 400 <c>invalid-cursor</c> is
    /// <c>InvalidRequest</c> and a 409 <c>projection-changed</c> is <c>ProjectionChanged</c>; whether either restarts
    /// the read first is the reader's decision, as is the token refresh before a 401 is final.
    /// </summary>
    public static Code ClassifyPageStatus(HttpStatusCode status, string? problemType) =>
        ClassifyAnyStageStatus(status, problemType)
        ?? (int)status switch
        {
            400 when problemType == UnsupportedContractVersionProblemType => Code.UnsupportedContract,
            400 => Code.InvalidRequest,
            401 => Code.Unauthorized,
            403 => Code.Forbidden,
            404 => Code.TargetNotFound,
            409 => problemType switch
            {
                ProjectionChangedProblemType => Code.ProjectionChanged,
                TargetSchemaIncompatibleProblemType or TargetProviderUnsupportedProblemType =>
                    Code.TargetSchemaIncompatible,
                ProjectionUnsupportedProblemType => Code.Unsupported,
                ProjectionTooLargeProblemType => Code.LimitExceeded,
                ProjectionDataInvalidProblemType => Code.DataInvalid,
                _ => Code.UnexpectedResponse,
            },
            _ => Code.UnexpectedResponse,
        };

    /// <summary>Whether a page response is the 400 <c>invalid-cursor</c> problem.</summary>
    public static bool IsInvalidCursor(HttpStatusCode status, string? problemType) =>
        status == HttpStatusCode.BadRequest && problemType == InvalidCursorProblemType;
}
