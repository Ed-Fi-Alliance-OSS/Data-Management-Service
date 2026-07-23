// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Writes an Ed-Fi failure-response body directly to an <see cref="HttpResponse"/> for producers that
/// cannot return an <see cref="IResult"/> (middleware short-circuits and framework-response shaping).
///
/// The HTTP status code is derived from the failure node's own <c>status</c> member, so the transport
/// status and the body <c>status</c> can never disagree; there is no independent status argument to get
/// out of sync. The <c>correlationId</c> is always set to <see cref="HttpContext.TraceIdentifier"/>, and
/// the content type is the configured problem-details media type. The write is a no-op when the response
/// has already started.
/// </summary>
internal static class FailureResponseWriter
{
    private const string ProblemJsonContentType = "application/problem+json";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Task WriteAsync(
        HttpContext context,
        JsonNode failureResponse,
        CancellationToken cancellationToken = default
    )
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        int status =
            failureResponse["status"]?.GetValue<int>()
            ?? throw new ArgumentException(
                "Failure response must include a 'status' member.",
                nameof(failureResponse)
            );

        // Single source of truth: the transport status is taken from the body, and the body's
        // correlationId is aligned with this request so the two can never diverge.
        failureResponse["correlationId"] = context.TraceIdentifier;

        context.Response.StatusCode = status;
        context.Response.ContentType = ProblemJsonContentType;

        return context.Response.WriteAsync(
            failureResponse.ToJsonString(_serializerOptions),
            cancellationToken
        );
    }
}
