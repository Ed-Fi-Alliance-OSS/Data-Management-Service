// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The single definition of the RFC 6749 §5.2 error body: a JSON object whose <c>error</c> and
/// <c>error_description</c> are top-level members, served as <c>application/json</c>. Endpoints return it
/// through <see cref="Create"/>; <see cref="GlobalExceptionHandler"/>, which cannot return an
/// <see cref="IResult"/>, writes the same bytes through <see cref="WriteAsync"/>. Callers pass fixed,
/// service-owned descriptions only, never caller or provider content.
/// </summary>
internal static class OAuthErrorResponseWriter
{
    private const string JsonContentType = "application/json";

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IResult Create(int statusCode, string error, string errorDescription) =>
        Results.Text(Serialize(error, errorDescription), JsonContentType, Encoding.UTF8, statusCode);

    /// <summary>Writes the error body unless the response has already started.</summary>
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string error,
        string errorDescription,
        CancellationToken cancellationToken = default
    )
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        byte[] payload = Encoding.UTF8.GetBytes(Serialize(error, errorDescription));

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = $"{JsonContentType}; charset=utf-8";
        context.Response.ContentLength = payload.Length;

        return context.Response.Body.WriteAsync(payload, 0, payload.Length, cancellationToken);
    }

    private static string Serialize(string error, string errorDescription) =>
        JsonSerializer.Serialize(new OAuthErrorResponse(error, errorDescription), _serializerOptions);

    /// <summary>
    /// The property names are the wire names the specification fixes, so they are spelled that way
    /// rather than renamed by a serializer policy that a future configuration change could alter.
    /// </summary>
    private sealed record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription
    );
}
