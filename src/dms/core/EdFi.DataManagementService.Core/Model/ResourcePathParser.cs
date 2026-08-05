// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// The outcome of classifying a resource request path.
/// </summary>
internal abstract record ResourcePathParseResult
{
    private ResourcePathParseResult() { }

    /// <summary>
    /// The path does not have the resource-path shape at all: empty, malformed, or carrying an
    /// additional segment. Callers answer with the not-found response.
    /// </summary>
    public sealed record Unmatched : ResourcePathParseResult
    {
        internal static Unmatched Instance { get; } = new();
    }

    /// <summary>
    /// A third segment is present but names neither the partitions operation nor a well-formed
    /// document uuid.
    /// </summary>
    /// <param name="SuppliedSegment">
    /// The segment exactly as the client supplied it, so the response can echo it verbatim.
    /// </param>
    public sealed record InvalidIdentifier(string SuppliedSegment) : ResourcePathParseResult;

    /// <summary>
    /// A recognized resource path.
    /// </summary>
    /// <param name="PathComponents">The classified path in object form.</param>
    /// <param name="SuppliedOperationSegment">
    /// The raw third segment when one was present, so a caller can echo the client's exact text;
    /// null for a collection path.
    /// </param>
    public sealed record Recognized(PathComponents PathComponents, string? SuppliedOperationSegment)
        : ResourcePathParseResult;
}

/// <summary>
/// The single definition of resource path-shape recognition.
/// </summary>
/// <remarks>
/// Pure and dependency-free so that both the pipeline step that validates a path and the dispatch
/// that chooses a pipeline for it consume one implementation. Two callers each running their own
/// regex is how the shape a request is dispatched as and the shape it is later parsed as drift apart.
/// </remarks>
internal static class ResourcePathParser
{
    /// <summary>
    /// The third route segment naming the sibling partitions operation.
    /// </summary>
    internal const string PartitionsSegment = "partitions";

    /// <summary>
    /// Classifies a request path. The partitions operation is recognized before uuid parsing, so a
    /// path naming it is never reported as a malformed identifier.
    /// </summary>
    internal static ResourcePathParseResult Parse(string path)
    {
        Match match = UtilityService.PathExpressionRegex().Match(path);

        if (!match.Success)
        {
            return ResourcePathParseResult.Unmatched.Instance;
        }

        ProjectEndpointName projectEndpointName = new(match.Groups["projectNamespace"].Value.ToLower());
        EndpointName endpointName = new(match.Groups["endpointName"].Value);

        string suppliedSegment = match.Groups["documentUuid"].Value;

        if (suppliedSegment.Length == 0)
        {
            return new ResourcePathParseResult.Recognized(
                new PathComponents(
                    projectEndpointName,
                    endpointName,
                    ResourcePathOperation.Collection.Instance
                ),
                SuppliedOperationSegment: null
            );
        }

        // Matched case-insensitively for the same reason endpoint names are: every other segment of
        // the route is, so an ordinal-only match here would make one segment behave unlike its
        // neighbors.
        if (string.Equals(suppliedSegment, PartitionsSegment, StringComparison.OrdinalIgnoreCase))
        {
            return new ResourcePathParseResult.Recognized(
                new PathComponents(
                    projectEndpointName,
                    endpointName,
                    ResourcePathOperation.Partitions.Instance
                ),
                suppliedSegment
            );
        }

        if (!IsDocumentUuidWellFormed(suppliedSegment))
        {
            return new ResourcePathParseResult.InvalidIdentifier(suppliedSegment);
        }

        return new ResourcePathParseResult.Recognized(
            new PathComponents(
                projectEndpointName,
                endpointName,
                new ResourcePathOperation.ById(new DocumentUuid(new Guid(suppliedSegment)))
            ),
            suppliedSegment
        );
    }

    /// <summary>
    /// Check that this is a well-formed UUID string
    /// </summary>
    private static bool IsDocumentUuidWellFormed(string documentUuidString)
    {
        return UtilityService.UuidRegex().IsMatch(documentUuidString.ToLower());
    }
}
