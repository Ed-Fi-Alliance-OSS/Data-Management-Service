// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

internal class DisallowDuplicateReferencesMiddleware(ILogger logger) : IPipelineStep
{
    private static readonly Regex _numericIndexRegex =
        new(pattern: @"\[\d+\]", options: RegexOptions.Compiled);

    private static readonly Regex _arrayNameRegex =
        new(pattern: @"\$\.(\w+)\[(\d+)\]", options: RegexOptions.Compiled);

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferencesMiddleware - {TraceId}",
            context.FrontendRequest.TraceId
        );

        var validationErrors = new Dictionary<string, string[]>();

        // Find duplicates in document references
        if (context.DocumentInfo.DocumentReferences.GroupBy(d => d.ReferentialId).Any(g => g.Count() > 1))
        {
            // if duplicates are found, they should be reported
            ValidateDuplicates(
                context.DocumentInfo.DocumentReferences,
                item => item.ReferentialId.Value,
                item => item.ResourceInfo.ResourceName.Value,
                validationErrors
            );
        }

        // Find duplicates in descriptor references
        if (context.DocumentInfo.DescriptorReferences.GroupBy(d => d.ReferentialId).Any(g => g.Count() > 1))
        {
            var groupedReferences = GroupByArrayNameAndIndex(
                context.DocumentInfo.DescriptorReferences.Select(x => x).ToList()
            );

            var combinedIds = new List<string>();
            foreach (var descriptorReferences in groupedReferences.Where(x => x.indexGroups.Count > 1))
            {
                foreach (var indexGroup in descriptorReferences.indexGroups)
                {
                    var combinedId = new StringBuilder();
                    foreach (var reference in indexGroup.references)
                    {
                        combinedId.Append(reference.ReferentialId.Value);
                    }
                    combinedIds.Add(combinedId.ToString());
                }
            }

            if (combinedIds.GroupBy(d => d).Any(g => g.Count() > 1))
            {
                // if duplicates are found, they should be reported
                ValidateDuplicates(
                    context.DocumentInfo.DescriptorReferences,
                    item => item.ReferentialId.Value,
                    item => item.Path.Value,
                    validationErrors,
                    true
                );
            }
        }

        if (validationErrors.Any())
        {
            logger.LogDebug("Duplicated reference Id - {TraceId}", context.FrontendRequest.TraceId);

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

        await next();
    }

    private static void ValidateDuplicates<T>(
        IEnumerable<T> items,
        Func<T, Guid> getReferentialId,
        Func<T, string> getPath,
        Dictionary<string, string[]> validationErrors,
        bool IsDescriptor = false
    )
    {
        var seenItems = new HashSet<Guid>();
        var positions = new Dictionary<string, int>();

        foreach (var item in items)
        {
            Guid referentialId = getReferentialId(item);
            string path = getPath(item);

            // The descriptor reference path includes index value, which groups error messages by index.
            // To prevent this, converting the index to *
            if (IsDescriptor)
            {
                path = _numericIndexRegex.Replace(path, "[*]");
            }

            // the propertyName varies according to the origin (DescriptorReference or DocumentReferences)
            string propertyName = path.StartsWith("$", StringComparison.InvariantCultureIgnoreCase)
                ? path
                : $"$.{path}";

            positions.TryAdd(propertyName, 1);

            if (!seenItems.Add(referentialId))
            {
                path = path.StartsWith("$", StringComparison.InvariantCultureIgnoreCase)
                    ? ExtractArrayName(path)
                    : path;

                string errorMessage =
                    $"The {GetOrdinal(positions[propertyName])} item of the {path} has the same identifying values as another item earlier in the list.";

                if (validationErrors.TryGetValue(propertyName, out string[]? value))
                {
                    var existingMessages = value.ToList();
                    existingMessages.Add(errorMessage);
                    validationErrors[propertyName] = [.. existingMessages];
                }
                else
                {
                    validationErrors[propertyName] = [errorMessage];
                }
            }
            positions[propertyName]++;
        }
    }

    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
        {
            return $"{number}th";
        }

        return (number % 10) switch
        {
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        };
    }

    private static string ExtractArrayName(string path)
    {
        // Logic to extract the array name from the JSON path, e.g., "gradeLevels".
        string[] parts = path.Split('.');
        return parts.Length > 1 ? parts[1].Trim('[', ']', '*') : string.Empty;
    }

    private static List<KeyReferenceGroup> GroupByArrayNameAndIndex(IList<DescriptorReference> jsonNodes)
    {
        // Select reference node and match ( e.g., match = $.performanceLevels[1])
        var descriptorReferenceMatches = jsonNodes
            .Select(reference => new
            {
                Node = reference,
                Match = _arrayNameRegex.Match(reference.Path.Value),
            })
            .Where(x => x.Match.Success);

        // Group by reference array name (e.g., Key = "performanceLevels", Value = {Index = 0, Node = DescriptorReference})
        // Selects DescriptorReference with index value
        var groupedByReferenceArrayName = descriptorReferenceMatches.GroupBy(
            reference => reference.Match.Groups[1].Value,
            reference => new { Index = int.Parse(reference.Match.Groups[2].Value), reference.Node }
        );

        // Inner group by index
        // e.g., Key : performanceLevels
        //           Key: 0
        //           Value:[
        //                  PerformanceLevelDescriptor reference
        //                  AssessmentReportingMethodDescriptor reference
        //                 ]
        //           Key: 1
        //           Value:[
        //                  PerformanceLevelDescriptor reference
        //                  AssessmentReportingMethodDescriptor reference
        //                 ]
        var groupedByIndex = groupedByReferenceArrayName
            .Select(group => new KeyReferenceGroup(
                group.Key,
                group
                    .GroupBy(reference => reference.Index)
                    .Select(indexGroup => new IndexedReferenceGroup(
                        indexGroup.Key,
                        indexGroup.Select(x => x.Node).ToList()
                    ))
                    .ToList()
            ))
            .ToList();

        return groupedByIndex;
    }
}

internal record IndexedReferenceGroup(int index, List<DescriptorReference> references);

internal record KeyReferenceGroup(string key, List<IndexedReferenceGroup> indexGroups);
