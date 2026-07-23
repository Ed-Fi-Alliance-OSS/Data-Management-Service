// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Http;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Maps a framework-generated empty error status code — produced when a request fails before any
/// endpoint writes a body (routing 404/405, content-negotiation 415, model-binding 400, or a too-large
/// body 413) — to the Ed-Fi Problem Details contract, or null to leave the response untouched. Used by
/// the UseStatusCodePages handler (the non-throwing path) so it returns the same contract as the throwing
/// GlobalExceptionHandler. Any other client-error (4xx) status uses the generic bad-request type with its
/// status preserved, matching the throwing handler; the Knowledge Base defines no 413 type, so that case
/// uses the generic bad-request type as well. Authentication/authorization (401/403) responses already
/// carry the authorization handler's body and server errors (5xx) carry the exception handler's body, so
/// both are left untouched. Details are generic so framework/request text is never reflected back.
/// </summary>
internal static class FrameworkErrorResponse
{
    public static IResult? ForEmptyStatusCode(int statusCode, string correlationId) =>
        statusCode switch
        {
            StatusCodes.Status400BadRequest => FailureResults.BadRequest(
                "The request was invalid.",
                correlationId
            ),
            StatusCodes.Status404NotFound => FailureResults.NotFound(
                "The requested resource was not found.",
                correlationId
            ),
            StatusCodes.Status405MethodNotAllowed => FailureResults.MethodNotAllowed(
                "The request method is not allowed for this resource.",
                correlationId
            ),
            StatusCodes.Status415UnsupportedMediaType => FailureResults.UnsupportedMediaType(
                "The request content type is not supported.",
                correlationId
            ),
            StatusCodes.Status413PayloadTooLarge => FailureResults.BadRequest(
                "The request payload is too large.",
                correlationId,
                StatusCodes.Status413PayloadTooLarge
            ),
            // 401 and 403 already carry the authorization handler's Problem Details body, so they are not
            // rewritten as a bad request; listing them explicitly keeps them out of the client-error arm
            // below.
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => null,
            // Any other framework-generated request error (e.g. 406 Not Acceptable, 411 Length Required,
            // 431 Request Header Fields Too Large) uses the generic bad-request type with its status
            // preserved, matching the throwing GlobalExceptionHandler so both paths return the same
            // contract.
            >= 400 and <= 499 => FailureResults.BadRequest(
                "The request was invalid.",
                correlationId,
                statusCode
            ),
            // Server errors (5xx) are written with a body by the global exception handler, and any status
            // that is not an error carries no error body, so both are left untouched.
            _ => null,
        };
}
