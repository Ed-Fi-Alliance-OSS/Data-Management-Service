// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class InjectVersionMetadataToEdFiDocumentMiddleware(ILogger _logger) : IPipelineStep
    {
        public async Task Execute(RequestData requestData, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering InjectPropertiesToEdFiDocumentMiddleware - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );

            var parsedBody = requestData.ParsedBody.DeepClone() as JsonObject;

            parsedBody!.Remove("_etag");
            parsedBody!.Remove("_lastModifiedDate");

            string json = JsonSerializer.Serialize(parsedBody);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));

            requestData.ParsedBody["_etag"] = Convert.ToBase64String(hash);

            requestData.ParsedBody["_lastModifiedDate"] = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ssZ"
            );

            await next();
        }
    }
}
