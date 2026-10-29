// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Response;

/// <summary>
/// Responses for the claim-set resolution outcomes every claim-set consumer answers the same way.
/// A no-match keeps each consumer's own established response, so it is not here. Neither response
/// reflects scope values or claim-set names.
/// </summary>
internal static class ClaimSetResolutionResponses
{
    private const string AmbiguousError = "The token identifies multiple claim sets.";

    private const string InvalidMetadataError = "The security metadata contains duplicate claim-set names.";

    /// <summary>
    /// The token's scope values recognize more than one claim set, so none is chosen.
    /// </summary>
    public static FrontendResponse Ambiguous(TraceId traceId) =>
        new(
            StatusCode: 403,
            Body: FailureResponse.ForForbidden(traceId: traceId, errors: [AmbiguousError]),
            Headers: [],
            ContentType: "application/problem+json"
        );

    /// <summary>
    /// The claim-set metadata holds duplicate names matching the token, so no record can be chosen.
    /// </summary>
    public static FrontendResponse InvalidMetadata(TraceId traceId) =>
        new(
            StatusCode: 500,
            Body: FailureResponse.ForSecurityConfiguration(traceId, [InvalidMetadataError]),
            Headers: [],
            ContentType: "application/problem+json"
        );
}
