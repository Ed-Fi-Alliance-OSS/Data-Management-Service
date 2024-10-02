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

internal class RequestBodyLoggingMiddleware(ILogger _logger, IOptions<RequestLoggingOptions> options)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering RequestBodyLoggingMiddleware - {TraceId}",
            context.FrontendRequest.TraceId
        );

        string endpoint = context.FrontendRequest.Path;
        
        if (!options.Value.MaskRequestBody)
        {
            _logger.LogDebug("Incoming request to {endpoint} with body: {Body}", endpoint, context.FrontendRequest.Body);
        }
        else
        {
            if (context.FrontendRequest.Body != null)
            {
                string maskedBody = MaskRequestBody(context.FrontendRequest.Body, _logger);
                _logger.LogDebug("Incoming request to {endpoint} with body: {Body}", endpoint, maskedBody);
            }
        }

        await next();
    }

    private string MaskRequestBody(string body, ILogger logger)
    {
        try
        {
            // Deserialize the JSON body in a dynamic object
            var jsonDoc = JsonDocument.Parse(body);
            var maskedJson = MaskJsonElement(jsonDoc.RootElement);
            return JsonSerializer.Serialize(maskedJson);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Error while masking request body.");
            return body; // In case of error, it returns the original body.
        }
    }

    private JsonElement MaskJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dictionary = new Dictionary<string, JsonElement>();
                foreach (var property in element.EnumerateObject())
                {
                    dictionary[property.Name] = MaskJsonElement(property.Value);
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dictionary));

            case JsonValueKind.Array:
                var maskedArray = new List<JsonElement>();
                foreach (var item in element.EnumerateArray())
                {
                    maskedArray.Add(MaskJsonElement(item));
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(maskedArray));

            default:
                return JsonSerializer.Deserialize<JsonElement>("\"*\"");
        }
    }
}
