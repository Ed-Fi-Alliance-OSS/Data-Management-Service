// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Validation;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Finds the query fields a schema declares that DMS would consume as control parameters.
/// </summary>
/// <remarks>
/// Pure: it reads a schema node and returns what it found, touching no state and reporting nothing.
/// What to do about a collision belongs to the caller, which is what lets the same detection serve
/// DMS startup and the schema tooling without either deciding for the other.
/// </remarks>
/// <remarks>
/// Only <c>queryFieldMapping</c> is read, because that is the map the request pipeline matches a
/// supplied query key against. A property absent from it cannot be filtered on whatever it is named,
/// so it cannot collide. Abstract resources declare no query fields and are not examined.
/// </remarks>
internal static class ReservedQueryParameterCollisionDetector
{
    /// <summary>
    /// Returns every reserved-name collision the schema declares, ordered by resource endpoint name
    /// and then by query field name, both ordinally.
    /// </summary>
    /// <param name="schemaNode">One loaded ApiSchema root node.</param>
    /// <param name="schemaSource">
    /// How the caller names this schema in a diagnostic: <c>core</c>, or <c>extension[n]</c> by load
    /// position. Passed through rather than derived, because only the caller knows the load order.
    /// </param>
    /// <remarks>
    /// The order is imposed here rather than taken from the document, so two schemas differing only in
    /// the order their keys happen to be written produce the same diagnostic. A startup failure a
    /// reader is comparing against a previous run should not appear to have changed because a
    /// generator emitted its keys differently.
    /// </remarks>
    /// <remarks>
    /// A node of an unexpected shape yields no collisions rather than an exception. Structure is the
    /// JSON Schema validator's subject and the normalizer checks the project-level nodes before this
    /// runs, so throwing here would replace whichever of those two reports the real fault with a stack
    /// trace naming this file. Detection is a strict addition to those checks: it can only refuse a
    /// schema that would otherwise have been accepted.
    /// </remarks>
    internal static IReadOnlyList<ApiSchemaNormalizationResult.ReservedQueryParameterCollision> Detect(
        JsonNode schemaNode,
        string schemaSource
    )
    {
        ArgumentNullException.ThrowIfNull(schemaNode);
        ArgumentNullException.ThrowIfNull(schemaSource);

        if (schemaNode["projectSchema"] is not JsonObject projectSchema)
        {
            return [];
        }

        if (projectSchema["resourceSchemas"] is not JsonObject resourceSchemas)
        {
            return [];
        }

        // Read null-safe rather than required. The normalizer validates this field before detection
        // runs, and a schema missing it is rejected there with a message naming that fault; reporting
        // a collision under an empty project name is a better outcome than throwing over a schema that
        // is about to be refused anyway.
        string projectEndpointName = projectSchema["projectEndpointName"]?.GetValue<string>() ?? string.Empty;

        List<ApiSchemaNormalizationResult.ReservedQueryParameterCollision> collisions = [];

        foreach (
            (string resourceEndpointName, JsonNode? resourceSchema) in resourceSchemas.OrderBy(
                entry => entry.Key,
                StringComparer.Ordinal
            )
        )
        {
            if (resourceSchema?["queryFieldMapping"] is not JsonObject queryFieldMapping)
            {
                continue;
            }

            foreach (
                string queryFieldName in queryFieldMapping
                    .Select(field => field.Key)
                    .OrderBy(fieldName => fieldName, StringComparer.Ordinal)
            )
            {
                if (
                    ReservedQueryParameters.TryGetReserved(
                        queryFieldName,
                        out ReservedQueryParameter? reserved
                    )
                )
                {
                    collisions.Add(
                        new ApiSchemaNormalizationResult.ReservedQueryParameterCollision(
                            schemaSource,
                            projectEndpointName,
                            resourceEndpointName,
                            queryFieldName,
                            reserved
                        )
                    );
                }
            }
        }

        return collisions;
    }
}
