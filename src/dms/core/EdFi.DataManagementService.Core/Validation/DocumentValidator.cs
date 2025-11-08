// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Json.Pointer;
using Json.Schema;

namespace EdFi.DataManagementService.Core.Validation;

internal interface IDocumentValidator
{
    /// <summary>
    /// Validates a document body against a JSON Schema
    /// </summary>
    (string[], Dictionary<string, string[]>) Validate(RequestInfo requestInfo);
}

internal class DocumentValidator(ICompiledSchemaCache schemaCache) : IDocumentValidator
{
    private readonly ICompiledSchemaCache _schemaCache = schemaCache;
    private readonly HashSet<string> trimmableProperties = new HashSet<string>
    {
        "codeValue",
        "shortDescription",
    };

    private static readonly EvaluationOptions ValidatorEvaluationOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private JsonSchema GetSchema(RequestInfo requestInfo) =>
        _schemaCache.GetOrAdd(
            requestInfo.ResourceSchema.ResourceName,
            requestInfo.Method,
            requestInfo.ApiSchemaReloadId,
            () => CompileSchema(requestInfo.ResourceSchema, requestInfo.Method)
        );

    private static JsonSchema CompileSchema(ResourceSchema resourceSchema, RequestMethod method)
    {
        JsonNode jsonSchemaForResource = resourceSchema.JsonSchemaForRequestMethod(method);
        string stringifiedJsonSchema = JsonSerializer.Serialize(jsonSchemaForResource);
        return JsonSchema.FromText(stringifiedJsonSchema);
    }

    public (string[], Dictionary<string, string[]>) Validate(RequestInfo requestInfo)
    {
        var resourceSchemaValidator = GetSchema(requestInfo);
        var results = resourceSchemaValidator.Evaluate(requestInfo.ParsedBody, ValidatorEvaluationOptions);

        var overpostPruneResult = PruneOverpostedData(requestInfo.ParsedBody, results);

        if (overpostPruneResult is PruneResult.Pruned pruned)
        {
            // Used pruned body for the remainder of pipeline
            requestInfo.ParsedBody = pruned.prunedDocumentBody;

            // Now re-evaluate the pruned body
            results = resourceSchemaValidator.Evaluate(requestInfo.ParsedBody, ValidatorEvaluationOptions);
        }

        var nullPruneResult = PruneNullData(requestInfo.ParsedBody, results);

        if (nullPruneResult is PruneResult.Pruned nullPruned)
        {
            // Used pruned body for the remainder of pipeline
            requestInfo.ParsedBody = nullPruned.prunedDocumentBody;

            // Now re-evaluate the pruned body
            results = resourceSchemaValidator.Evaluate(requestInfo.ParsedBody, ValidatorEvaluationOptions);
        }

        var whitespacePruneResult = PruneCodeValueWhitespaceData(requestInfo.ParsedBody, results);

        if (whitespacePruneResult is PruneResult.Pruned whitespacePruned)
        {
            // Used pruned body for the remainder of pipeline
            requestInfo.ParsedBody = whitespacePruned.prunedDocumentBody;

            // Now re-evaluate the pruned body
            results = resourceSchemaValidator.Evaluate(requestInfo.ParsedBody, ValidatorEvaluationOptions);
        }

        return (new List<string>().ToArray(), ValidationErrorsFrom(results));

        PruneResult PruneOverpostedData(JsonNode? documentBody, EvaluationResults evaluationResults)
        {
            if (documentBody == null)
            {
                return new PruneResult.NotPruned();
            }

            var additionalProperties = evaluationResults
                .Details.Where(r =>
                    r.EvaluationPath.Count > 0 && r.EvaluationPath[^1] == "additionalProperties"
                    || IsEmptyArray(documentBody, r.InstanceLocation)
                )
                .ToList();

            if (additionalProperties.Count == 0)
            {
                return new PruneResult.NotPruned();
            }

            bool modified = false;

            foreach (var additionalProperty in additionalProperties)
            {
                JsonObject jsonObject = documentBody.AsObject();
                jsonObject.RemoveProperty([.. additionalProperty.InstanceLocation]);
                modified = true;
            }

            return modified ? new PruneResult.Pruned(documentBody) : new PruneResult.NotPruned();
        }

        bool IsEmptyArray(JsonNode documentBody, JsonPointer instanceLocation)
        {
            if (instanceLocation.Count == 0)
            {
                return false;
            }

            JsonObject jsonObject = documentBody.AsObject();
            string propertyName = instanceLocation[^1];

            if (jsonObject.TryGetPropertyValue(propertyName, out var value) && value is JsonArray jsonArray)
            {
                return jsonArray.Count == 0 || jsonArray.All(x => x is JsonObject { Count: 0 });
            }

            return false;
        }

        PruneResult PruneNullData(JsonNode? documentBody, EvaluationResults evaluationResults)
        {
            if (documentBody == null)
            {
                return new PruneResult.NotPruned();
            }

            var nullProperties = evaluationResults
                .Details.Where(r =>
                    r.Errors != null && r.Errors.Values.Any(e => e.StartsWith("Value is \"null\""))
                )
                .ToList();

            if (nullProperties.Count == 0)
            {
                return new PruneResult.NotPruned();
            }

            bool modified = false;

            foreach (var nullProperty in nullProperties)
            {
                JsonObject jsonObject = documentBody.AsObject();
                jsonObject.RemoveProperty([.. nullProperty.InstanceLocation]);
                modified = true;
            }

            return modified ? new PruneResult.Pruned(documentBody) : new PruneResult.NotPruned();
        }

        PruneResult PruneCodeValueWhitespaceData(JsonNode? documentBody, EvaluationResults evaluationResults)
        {
            if (documentBody == null)
            {
                return new PruneResult.NotPruned();
            }

            var trimTargets = evaluationResults
                .Details.Where(r =>
                    r.Errors != null
                    && r.Errors.Values.Any(e =>
                        e.Contains("The string value is not a match for the indicated regular expression")
                    )
                    && trimmableProperties.Contains(r.InstanceLocation.ToString().TrimStart('/'))
                )
                .ToList();

            if (trimTargets.Count == 0)
            {
                return new PruneResult.NotPruned();
            }

            bool modified = false;

            foreach (var target in trimTargets)
            {
                string propertyName = target.InstanceLocation.ToString().TrimStart('/');

                if (
                    documentBody is JsonObject obj
                    && obj[propertyName] is JsonValue val
                    && val.TryGetValue<string>(out var str)
                )
                {
                    var trimmed = str.Trim();
                    if (trimmed != str)
                    {
                        obj[propertyName] = trimmed;
                        modified = true;
                    }
                }
            }

            return modified ? new PruneResult.Pruned(documentBody) : new PruneResult.NotPruned();
        }

        Dictionary<string, string[]> ValidationErrorsFrom(EvaluationResults results)
        {
            var validationErrors = new Dictionary<string, string[]>();
            foreach (var detail in results.Details)
            {
                var propertyName = string.Empty;
                if (detail.InstanceLocation != null && detail.InstanceLocation.Count != 0)
                {
                    propertyName = $"{detail.InstanceLocation[^1]}";
                }
                if (detail.Errors != null && detail.Errors.Any())
                {
                    foreach (var errorDetail in detail.Errors)
                    {
                        // Custom validation error for strings with white spaces
                        var error = errorDetail.Value;
                        if (
                            errorDetail.Key.Equals("pattern", StringComparison.InvariantCultureIgnoreCase)
                            && error.Contains(
                                "value is not a match for the indicated regular expression",
                                StringComparison.InvariantCultureIgnoreCase
                            )
                        )
                        {
                            error = "cannot contain leading or trailing spaces.";
                            if (IsEmptyString(detail.InstanceLocation))
                            {
                                error = "is required and should not be left empty.";
                            }
                        }

                        var splitErrors = SplitErrorDetail(error, propertyName);

                        foreach (var splitError in splitErrors)
                        {
                            if (validationErrors.ContainsKey(splitError.Key))
                            {
                                var existingErrors = validationErrors[splitError.Key].ToList();
                                existingErrors.AddRange(splitError.Value);
                                validationErrors[splitError.Key] = existingErrors.ToArray();
                            }
                            else
                            {
                                validationErrors.Add(splitError.Key, splitError.Value);
                            }
                        }
                    }
                }
            }
            return validationErrors;

            bool IsEmptyString(JsonPointer? instanceLocation)
            {
                if (instanceLocation == null)
                {
                    return false;
                }

                var jsonObject = requestInfo.ParsedBody.AsObject();
                string propertyName = instanceLocation[^1];
                bool propertyExists = jsonObject.TryGetPropertyValue(propertyName, out var value);

                if (propertyExists && value != null && value.ToString() == string.Empty)
                {
                    return true;
                }
                return false;
            }
        }
    }

    // Matches any text string enclosed in double quotation marks
    private static readonly Regex _findErrorsRegex = new Regex("\"([^\"]*)\"", RegexOptions.Compiled);

    private static Dictionary<string, string[]> SplitErrorDetail(string error, string propertyName)
    {
        var validations = new Dictionary<string, string[]>();
        if (error.Contains("[") && error.Contains("]"))
        {
            MatchCollection hits = _findErrorsRegex.Matches(error);

            foreach (var hit in hits.Select(hit => hit.Groups))
            {
                var value = new List<string>();
                value.Add($"{hit[1].Value} is required.");
                var additional =
                    propertyName == string.Empty ? "" : propertyName.Replace(":", "").TrimEnd() + ".";
                validations.Add("$." + additional + hit[1].Value, value.ToArray());
            }
        }
        else
        {
            var value = new List<string>();
            value.Add($"{propertyName} {error}");
            validations.Add("$." + propertyName, value.ToArray());
        }
        return validations;
    }
}

/// <summary>
/// The result of pruning "additionalProperties" aka overposted data from a JsonNode.
/// If none were found, result should be NotPruned. Otherwise, Pruned with the pruneDocumentBody
/// </summary>
internal abstract record PruneResult
{
    public record NotPruned() : PruneResult;

    public record Pruned(JsonNode prunedDocumentBody) : PruneResult;
}

internal static class JsonObjectExtensions
{
    internal static JsonObject RemoveProperty(this JsonObject jsonObject, string[] segments)
    {
        if (segments.Length == 0)
        {
            return jsonObject;
        }

        if (segments.Length == 1)
        {
            jsonObject.Remove(segments[0]);
            return jsonObject;
        }

        var currentSegment = segments[0];
        var remainingSegments = segments.Skip(1).ToArray();
        var node = jsonObject[currentSegment];

        Trace.Assert(node != null, $"PointerSegment '{currentSegment}' not found on JsonObject");

        if (node is JsonObject nodeObj)
        {
            nodeObj.RemoveProperty(remainingSegments);
        }
        else if (node is JsonArray nodeArray && int.TryParse(remainingSegments[0], out int index))
        {
            if (index >= 0 && index < nodeArray.Count)
            {
                var item = nodeArray[index];
                if (item is JsonObject itemObj)
                {
                    itemObj.RemoveProperty(remainingSegments.Skip(1).ToArray());
                }
            }
            else
            {
                Trace.Assert(false, $"Index '{index}' out of bounds for JsonArray");
            }
        }
        else
        {
            Trace.Assert(
                false,
                $"Node is not a JsonObject or JsonArray or invalid index for array: {currentSegment}"
            );
        }

        return jsonObject;
    }
}
