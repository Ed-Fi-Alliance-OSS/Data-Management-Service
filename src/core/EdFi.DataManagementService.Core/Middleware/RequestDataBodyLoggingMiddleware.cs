// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

internal class RequestDataBodyLoggingMiddleware(ILogger _logger, IOptions<RequestLoggingOptions> options)
    : IPipelineStep
{
    private const string MessageBody = "Incoming {Method} request to {Path} with body structure: {Body}";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering RequestDataBodyLoggingMiddleware - {TraceId}",
            context.FrontendRequest.TraceId
        );

        if (!options.Value.MaskRequestBody)
        {
            _logger.LogDebug(
                MessageBody,
                context.Method,
                context.FrontendRequest.Path,
                context.FrontendRequest.Body
            );
        }
        else
        {
            if (context.FrontendRequest.Body != null)
            {
                _logger.LogDebug(
                    MessageBody,
                    context.Method,
                    context.FrontendRequest.Path,
                    MaskRequestBody(context.FrontendRequest.Body, _logger)
                );
            }
        }

        await next();
    }

    private static string MaskRequestBody(string body, ILogger logger)
    {
        try
        {
            // Deserialize the JSON body in a dynamic object
            JsonDocument jsonDoc = JsonDocument.Parse(body);
            JsonElement maskedJson = MaskJsonElement(jsonDoc.RootElement);
            return JsonSerializer.Serialize(maskedJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Error while masking request body.");
            return body; // In case of error, it returns the original body.
        }
    }

    private static JsonElement MaskJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, JsonElement> dictionary = new();
                foreach (var property in element.EnumerateObject())
                {
                    dictionary[property.Name] = MaskJsonElement(property.Value);
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dictionary, _jsonOptions));

            case JsonValueKind.Array:
                List<JsonElement> maskedArray = [];
                maskedArray.AddRange(element.EnumerateArray().Select(MaskJsonElement));
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(maskedArray, _jsonOptions));

            default:
                return JsonSerializer.Deserialize<JsonElement>("\"*\"");
        }
    }
}
