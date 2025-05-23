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
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering InjectPropertiesToEdFiDocumentMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );

            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            string formattedUtcDateTime = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            context.ParsedBody["_lastModifiedDate"] = formattedUtcDateTime;

            if (context.FrontendRequest.Header != null &&
                 context.FrontendRequest.Header.TryGetValue("If-Match", out var ifMatch) &&
                 !string.IsNullOrWhiteSpace(ifMatch))
            {
                context.ParsedBody["IfMatch"] = ifMatch;
            }

            if (context.ParsedBody.DeepClone() is JsonObject cloneForHash)
            {
                cloneForHash.Remove("_etag");
                cloneForHash.Remove("_lastModifiedDate");

                // Compute _etag from clone
                string json = JsonSerializer.Serialize(cloneForHash);
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                context.ParsedBody["_etag"] = Convert.ToBase64String(hash);
            }
            await next();
        }
    }
}
