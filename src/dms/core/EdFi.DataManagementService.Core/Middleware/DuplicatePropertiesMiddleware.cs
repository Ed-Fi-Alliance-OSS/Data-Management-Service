// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// Parse did not find errors in repeated values, it is identified until an attempt is made to use the JsonNode
/// Please see https://github.com/dotnet/runtime/issues/70604 for information on the JsonNode bug this code is working-around.
internal class DuplicatePropertiesMiddleware(ILogger logger) : IPipelineStep
{
    // This key should never exist in the document
    private const string TestForDuplicateObjectKeyWorkaround = "x";
    private const string Pattern = @"Key: (.*?) \((.*?)\)\.(.*?)$";

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicatePropertiesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId
        );

        if (context.FrontendRequest.Body != null)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(context.FrontendRequest.Body);

                if (node is JsonObject jsonObject)
                {
                    // This validation will identify if the problem is at the first level. It does not identify if it is at a second or third level.
                    _ = node[TestForDuplicateObjectKeyWorkaround];

                    // If you are in this line there are no First level exceptions, recursively check the rest of the body to find the first exception
                    CheckForDuplicateProperties(jsonObject, "$");
                }
            }
            catch (ArgumentException ae)
            {
                // This match works when the error is after the first level of node
                //  e.g. An item with the same key has already been added. Key: classPeriodName (Parameter 'propertyName').$.classPeriods[0].classPeriodReference
                Match match = Regex.Match(ae.Message, Pattern);

                string propertyName;
                if (match.Success)
                {
                    string keyName = match.Groups[1].Value;
                    string errorPath = match.Groups[3].Value;
                    propertyName = $"{errorPath}.{keyName}";
                }
                else
                {
                    var propertyNameMatch = Regex.Match(
                        ae.Message,
                        @"Key: (\w+) \(Parameter 'propertyName'\)"
                    );
                    propertyName = propertyNameMatch.Success
                        ? "$." + propertyNameMatch.Groups[1].Value
                        : "unknown";
                }

                var validationErrors = new Dictionary<string, string[]>
                {
                    { $"{propertyName}", new[] { "An item with the same key has already been added." } }
                };

                logger.LogDebug(ae, "Duplicate key found - {TraceId}", context.FrontendRequest.TraceId);

                context.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: ForDataValidation(
                        "Data validation failed. See 'validationErrors' for details.",
                        traceId: context.FrontendRequest.TraceId,
                        validationErrors,
                        []
                    ),
                    Headers: []
                );
                return;
            }
            catch (Exception e)
            {
                logger.LogDebug(
                    e,
                    "Unable to evaluate the request body - {TraceId}",
                    context.FrontendRequest.TraceId
                );
                return;
            }
        }

        await next();
    }

    private void CheckForDuplicateProperties(JsonNode node, string path)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                string propertyPath = $"{path}.{property.Key}";
                try
                {
                    if (property.Value is JsonObject nestedObject)
                    {
                        CheckForDuplicateProperties(nestedObject, propertyPath);
                    }
                    else if (property.Value is JsonArray jsonArray)
                    {
                        for (int i = 0; i < jsonArray.Count; i++)
                        {
                            var itemPath = $"{propertyPath}[{i}]";
                            var item = jsonArray[i];
                            if (item is JsonObject)
                            {
                                CheckForDuplicateProperties(item, itemPath);
                            }
                        }
                    }
                }
                catch (ArgumentException ex)
                {
                    string detailedPath = string.Empty;
                    // To avoid when it enters more than one time and the path is modified
                    if (!ex.Message.Contains(propertyPath))
                    {
                        detailedPath = "." + propertyPath;
                    }
                    throw new ArgumentException(ex.Message + detailedPath);
                }
            }
        }
    }
}
