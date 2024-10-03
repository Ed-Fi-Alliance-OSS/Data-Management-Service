// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using EdFi.DataManagementService.Core.External.Backend;
using Json.More;
using Json.Path;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Backend;

public class UpdateCascadeHandler(IApiSchemaProvider _apiSchemaProvider, ILogger _logger)
    : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        string originalDocumentProjectName,
        string originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        string referencingProjectName,
        string referencingResourceName
    )
    {
        var isIdentityUpdate = false;
        JsonNode returnEdFiDoc = referencingEdFiDoc.DeepClone();

        var apiSchemaDocument = new ApiSchemaDocument(_apiSchemaProvider.ApiSchemaRootNode, _logger);

        var originalResourceNode =
            apiSchemaDocument.FindResourceNode(originalDocumentProjectName, originalDocumentResourceName)
            ?? throw new InvalidOperationException(
                $"ResourceSchema not found for {originalDocumentProjectName}.{originalDocumentResourceName}"
            );

        var originalIdentityJsonPaths = (
            originalResourceNode.SelectNodeFromPath("$.identityJsonPaths", _logger)
            ?? throw new InvalidOperationException(
                $"{originalDocumentResourceName} identityJsonPaths not found"
            )
        ).AsArray();

        var referencingResourceNode =
            apiSchemaDocument.FindResourceNode(referencingProjectName, referencingResourceName)
            ?? throw new InvalidOperationException(
                $"ResourceSchema not found for {referencingProjectName}.{referencingResourceName}"
            );

        var referencingIdentityJsonPaths = (
            referencingResourceNode.SelectNodeFromPath("$.identityJsonPaths", _logger)
            ?? throw new InvalidOperationException($"{referencingResourceName} identityJsonPaths not found")
        ).AsArray();

        var referencingDocumentPathsMapping =
            referencingResourceNode.SelectNodeFromPath(
                $"$.documentPathsMapping[\"{originalDocumentResourceName}\"]",
                _logger
            )
            ?? throw new InvalidOperationException(
                $"{referencingResourceName} documentPathsMapping not found"
            );

        var referencingReferenceJsonPaths = (
            referencingDocumentPathsMapping.SelectNodeFromPath("$.referenceJsonPaths", _logger)
            ?? throw new InvalidOperationException(
                $"{referencingResourceName} {originalDocumentResourceName} referenceJsonPaths not found"
            )
        ).AsArray();

        isIdentityUpdate = referencingIdentityJsonPaths
            .Select(o => o?.GetValue<string>())
            .Any(o => referencingIdentityJsonPaths.Select(a => a?.GetValue<string>()).Contains(o));

        var isList = referencingReferenceJsonPaths.Any(x =>
            x != null && x.SelectNodeFromPathAs<string>("$.referenceJsonPath", _logger)!.Contains("[*]")
        );

        if (isList)
        {
            // When the referenceJsonPath points to a list, we must construct a filter to
            // find the specific element(s) in the list that needs to be updated.
            // For example, we need to turn this: "$.classPeriods[*].classPeriodReference.classPeriodName"
            // into this: $.classPeriods[?(@.classPeriodReference.classPeriodName == "Third period edited" && @.classPeriodReference.schoolId == 123)]

            List<string> filters = [];
            var firstSegment = "";

            foreach (
                var originalIdentityJsonPath in originalIdentityJsonPaths
                    .Where(x => x != null)
                    .Select(x => JsonPath.Parse(x!.GetValue<string>()))
            )
            {
                var referenceJsonPath =
                    referencingReferenceJsonPaths.Single(a =>
                        a != null
                        && a.SelectNodeFromPathAs<string>("$.identityJsonPath", _logger)
                            == originalIdentityJsonPath.ToString()
                    )
                    ?? throw new InvalidOperationException(
                        $"Unexpected null finding {referencingResourceName}.documentPathsMapping.{originalDocumentResourceName}.identityJsonPath = {originalIdentityJsonPath}"
                    );

                var referenceJsonPathString =
                    referenceJsonPath.SelectNodeFromPathAs<string>("$.referenceJsonPath", _logger)
                    ?? string.Empty;

                // split at the array marker
                var split = referenceJsonPathString.Split("[*]");
                if (firstSegment == string.Empty)
                {
                    firstSegment = split[0];
                }
                referenceJsonPathString = split[1];

                originalIdentityJsonPath
                    .Evaluate(originalEdFiDoc.AsNode())
                    .Matches.TryGetSingleValue(out JsonNode? originalValue);
                if (originalValue == null)
                {
                    throw new InvalidOperationException(
                        $"Unexpected error finding original value for {originalIdentityJsonPath}"
                    );
                }

                switch (originalValue.GetValueKind())
                {
                    case JsonValueKind.Number:
                        filters.Add($"@{referenceJsonPathString} == {originalValue.GetValue<long>()}");
                        break;
                    default:
                        filters.Add($"@{referenceJsonPathString} == \"{originalValue.GetValue<string>()}\"");
                        break;
                }
            }

            var filterExpression = $"{firstSegment}[?({string.Join(" && ", filters)})]";

            // Evaluate
            var filterPath = JsonPath.Parse(filterExpression);
            var pathResult = filterPath.Evaluate(returnEdFiDoc);
            // should have at least one match
            if (pathResult == null || pathResult.Matches.Count == 0)
            {
                throw new InvalidOperationException($"Error evaluating filter expression {filterExpression}");
            }
            // more than one match should be logged as a warning
            if (pathResult.Matches.Count > 1)
            {
                _logger.LogWarning(
                    "More than one matching identity was found in document {DocumentUuid} the list with this filter expression {FilterExpression}",
                    referencingDocumentUuid,
                    filterExpression
                );
            }

            foreach (var match in pathResult.Matches)
            {
                var arrayLocation = match.Location?.ToString() ?? string.Empty;

                // now do the update
                foreach (
                    var originalIdentityJsonPath in originalIdentityJsonPaths
                        .Where(x => x != null)
                        .Select(x => JsonPath.Parse(x!.GetValue<string>()))
                )
                {
                    Update(
                        referencingReferenceJsonPaths,
                        originalIdentityJsonPath,
                        modifiedEdFiDoc,
                        returnEdFiDoc,
                        arrayLocation
                    );
                }
            }
        }
        else
        {
            foreach (
                var originalIdentityJsonPath in originalIdentityJsonPaths
                    .Where(x => x != null)
                    .Select(x => JsonPath.Parse(x!.GetValue<string>()))
            )
            {
                Update(
                    referencingReferenceJsonPaths,
                    originalIdentityJsonPath,
                    modifiedEdFiDoc,
                    returnEdFiDoc
                );
            }
        }
        return new UpdateCascadeResult(
            referencingEdFiDoc,
            returnEdFiDoc,
            referencingDocumentId,
            referencingDocumentPartitionKey,
            referencingDocumentUuid,
            referencingProjectName,
            referencingResourceName,
            isIdentityUpdate
        );
    }

    private void Update(
        JsonArray referencingReferenceJsonPaths,
        JsonPath originalIdentityJsonPath,
        JsonNode modifiedEdFiDoc,
        JsonNode returnEdFiDoc,
        string? listLocation = null
    )
    {
        var referenceJsonPath =
            referencingReferenceJsonPaths.Single(a =>
                a != null
                && a.SelectNodeFromPathAs<string>("$.identityJsonPath", _logger)
                    == originalIdentityJsonPath.ToString()
            )
            ?? throw new InvalidOperationException(
                $"Unexpected null finding identityJsonPath = {originalIdentityJsonPath}"
            );

        var referenceJsonPathString =
            referenceJsonPath.SelectNodeFromPathAs<string>("$.referenceJsonPath", _logger) ?? string.Empty;

        if (!string.IsNullOrEmpty(listLocation))
        {
            // list location will be the path to a specific array element. eg $['classPeriods'][2]
            var split = referenceJsonPathString.Split("[*]");
            // eg $['classPeriods'][2].classPeriodReference.classPeriodName
            referenceJsonPathString = $"{listLocation}{split[1]}";
        }

        originalIdentityJsonPath
            .Evaluate(modifiedEdFiDoc)
            .Matches.TryGetSingleValue(out JsonNode? modifiedValue);
        if (modifiedValue == null)
        {
            throw new InvalidOperationException(
                $"Unexpected error finding modified value for {originalIdentityJsonPath}"
            );
        }

        ModifyJsonNodeAtPath(returnEdFiDoc, referenceJsonPathString, modifiedValue);
    }

    private void ModifyJsonNodeAtPath(JsonNode nodeToModify, string jsonPath, JsonNode modifiedNode)
    {
        switch (modifiedNode.GetValueKind())
        {
            case JsonValueKind.Number:
                nodeToModify
                    .SelectNodeFromPath(jsonPath, _logger)!
                    .ReplaceWith(modifiedNode.GetValue<long>());
                break;
            default:
                nodeToModify
                    .SelectNodeFromPath(jsonPath, _logger)!
                    .ReplaceWith(modifiedNode.GetValue<string>());
                break;
        }
    }
}
