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
/// GlobalExceptionHandler. The Knowledge Base defines no 413 type, so that case uses the generic
/// bad-request type with the 413 status preserved; details are generic so framework/request text is never
/// reflected back.
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
            _ => null,
        };
}
