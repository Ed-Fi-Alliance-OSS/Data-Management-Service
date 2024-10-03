// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class RequestDataBodyLoggingMiddleware(ILogger _logger, bool _maskRequestBody) : IPipelineStep
{
    private const string MessageBody = "Incoming {Method} request to {Path} with body structure: {Body}";

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Entering RequestDataBodyLoggingMiddleware - {TraceId}",
                context.FrontendRequest.TraceId
            );

            if (context.FrontendRequest.Body != null)
            {
                string body = SanitizeInput(context.FrontendRequest.Body);

                if (!_maskRequestBody)
                {
                    _logger.LogDebug(MessageBody, context.Method, context.FrontendRequest.Path, body);
                }
                else
                {
                    if (context.FrontendRequest.Body != null)
                    {
                        _logger.LogDebug(
                            MessageBody,
                            context.Method,
                            context.FrontendRequest.Path,
                            MaskRequestBody(body, _logger)
                        );
                    }
                }
            }
        }
        await next();
    }

    private static string SanitizeInput(string input)
    {
        // Deletes new line, line feed, carriage return and tab characters
        return Regex.Replace(
            input.Replace(Environment.NewLine, "").Replace("\n", "").Replace("\r", "").Replace("\t", ""),
            @"\s+",
            " "
        );
    }

    private static string MaskRequestBody(string body, ILogger logger)
    {
        try
        {
            // Deserialize the JSON body in a dynamic object
            JsonDocument jsonDoc = JsonDocument.Parse(body);
            JsonElement maskedJson = MaskJsonElement(jsonDoc.RootElement);
            return JsonSerializer.Serialize(maskedJson);
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
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dictionary));

            case JsonValueKind.Array:
                List<JsonElement> maskedArray = [];
                maskedArray.AddRange(element.EnumerateArray().Select(MaskJsonElement));
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(maskedArray));

            default:
                return JsonSerializer.Deserialize<JsonElement>("\"*\"");
        }
    }
}
