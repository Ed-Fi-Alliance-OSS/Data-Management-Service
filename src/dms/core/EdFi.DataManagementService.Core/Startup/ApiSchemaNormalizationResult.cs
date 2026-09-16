// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Core.Validation;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Result type for API schema normalization operations.
/// Uses discriminated union pattern to represent success or various failure modes.
/// </summary>
public abstract record ApiSchemaNormalizationResult
{
    private ApiSchemaNormalizationResult() { }

    /// <summary>
    /// Normalization succeeded with the provided normalized nodes.
    /// </summary>
    public sealed record SuccessResult(ApiSchemaDocumentNodes NormalizedNodes) : ApiSchemaNormalizationResult;

    /// <summary>
    /// A schema is missing the projectSchema node or has malformed structure.
    /// </summary>
    public sealed record MissingOrMalformedProjectSchemaResult(string SchemaSource, string Details)
        : ApiSchemaNormalizationResult;

    /// <summary>
    /// Extension schema has a different apiSchemaVersion than the core schema.
    /// </summary>
    public sealed record ApiSchemaVersionMismatchResult(
        string ExpectedVersion,
        string ActualVersion,
        string SchemaSource
    ) : ApiSchemaNormalizationResult;

    /// <summary>
    /// Multiple schemas have the same projectEndpointName, which must be unique.
    /// Reports all collisions found, not just the first one.
    /// </summary>
    public sealed record ProjectEndpointNameCollisionResult(IReadOnlyList<EndpointNameCollision> Collisions)
        : ApiSchemaNormalizationResult;

    /// <summary>
    /// Represents a single projectEndpointName collision.
    /// </summary>
    public sealed record EndpointNameCollision(string ProjectEndpointName, string[] ConflictingSources);

    /// <summary>
    /// One or more resources declare a query field spelled like a query parameter DMS consumes as a
    /// control parameter. Reports every collision found, not just the first one.
    /// </summary>
    /// <remarks>
    /// A reserved name is removed from resource-filter matching before the query-field lookup runs, so
    /// such a property is not served as the schema declares it. For the seven names reserved on every
    /// operation that means the filter is silently never applied on a collection GET, and reported as
    /// an invalid query field on the Change Query endpoints. The partition count is reserved on
    /// <c>/partitions</c> alone, so a property of that name filters on the collection GET and is
    /// shadowed on the sibling operation of the same resource, which is the worse of the two outcomes
    /// because it differs by route rather than being uniformly absent.
    /// </remarks>
    /// <remarks>
    /// MetaEd protects only three of the reserved names, so a model declaring any of the others is
    /// accepted upstream and the defect first becomes visible to an API client. Refusing the schema
    /// here is what makes it visible to the person who can fix it.
    /// </remarks>
    public sealed record ReservedQueryParameterCollisionResult(
        IReadOnlyList<ReservedQueryParameterCollision> Collisions
    ) : ApiSchemaNormalizationResult
    {
        /// <summary>
        /// The operator-facing description: one line per collision, in the order they were found,
        /// under a count and above the remediation.
        /// </summary>
        /// <remarks>
        /// Built once here rather than at each call site, because this text is both thrown as a startup
        /// failure and written to the log, and the two must not drift. Every schema-supplied fragment is
        /// sanitized, because a project, resource, or field name arrives from a file this process did
        /// not author and reaches a structured log.
        /// </remarks>
        public string Describe()
        {
            StringBuilder description = new();

            description.Append(
                Collisions.Count == 1
                    ? "1 resource query field collides with a query parameter name DMS reserves."
                    : $"{Collisions.Count} resource query fields collide with query parameter names DMS reserves."
            );

            foreach (ReservedQueryParameterCollision collision in Collisions)
            {
                description.Append("\n  - ").Append(collision.Describe());
            }

            // The reason is stated per route rather than absolutely, because the two shapes of this
            // fault differ. A name reserved on every operation is never matched as a filter at all,
            // while the partition count is matched on a collection GET and consumed on that resource's
            // /partitions sibling. "On at least one route" is the claim that covers both without
            // telling an operator something untrue of the collision in front of them.
            description.Append(
                "\nRename the colliding propert"
                    + (Collisions.Count == 1 ? "y" : "ies")
                    + " in the MetaEd model and rebuild the ApiSchema. A reserved name is consumed as a "
                    + "control parameter before resource filters are matched on at least one route the "
                    + "resource exposes, so the field would not be served as its schema declares it."
            );

            return description.ToString();
        }
    }

    /// <summary>
    /// One resource query field colliding with one reserved query parameter.
    /// </summary>
    /// <param name="SchemaSource">
    /// Which loaded schema declares it, in the vocabulary the other normalization failures use:
    /// <c>core</c>, or <c>extension[n]</c> by load position. Composed by the loader from a load index,
    /// never read out of a schema, which is why it is the one fragment reported verbatim: the log
    /// sanitizer's whitelist excludes brackets, so sanitizing it would report <c>extension0</c> and
    /// leave a reader counting loaded extensions to work out which file to open.
    /// </param>
    /// <param name="ProjectEndpointName">The declaring project's endpoint name.</param>
    /// <param name="ResourceEndpointName">The declaring resource's endpoint name.</param>
    /// <param name="QueryFieldName">
    /// The field name exactly as the schema spells it, which may differ in case from the reserved name
    /// it collides with. Reported verbatim so the reader can find it in the file.
    /// </param>
    /// <param name="Reserved">
    /// The catalog entry it collides with. The whole entry is carried rather than its name alone, so
    /// the message can say what the name is taken for and on which operations without a second lookup
    /// that could disagree with the detection.
    /// </param>
    public sealed record ReservedQueryParameterCollision(
        string SchemaSource,
        string ProjectEndpointName,
        string ResourceEndpointName,
        string QueryFieldName,
        ReservedQueryParameter Reserved
    )
    {
        /// <summary>
        /// How each operation is named to an operator. Held beside the message rather than on the
        /// catalog because it is presentation, and the catalog is data.
        /// </summary>
        private static readonly (
            ReservedQueryParameterOperations Operation,
            string Description
        )[] _operationDescriptions =
        [
            (ReservedQueryParameterOperations.CollectionGet, "the resource and descriptor collection GET"),
            (ReservedQueryParameterOperations.Partitions, "/partitions"),
            (
                ReservedQueryParameterOperations.ChangeQueries,
                "the Change Query /deletes and /keyChanges endpoints"
            ),
        ];

        /// <summary>One line naming the schema, the resource, the field, and what the name is taken for.</summary>
        public string Describe() =>
            $"Schema '{SchemaSource}' resource "
            + $"'{Sanitize(ProjectEndpointName)}/{Sanitize(ResourceEndpointName)}' declares query field "
            + $"'{Sanitize(QueryFieldName)}', which DMS reserves as the {Reserved.Purpose} on "
            + $"{DescribeOperations()}.";

        /// <summary>
        /// The operations this name is reserved on, in catalog order, joined for prose.
        /// </summary>
        /// <remarks>
        /// An operation the table does not describe is still reported, by its flag name, rather than
        /// silently dropped. This text exists to explain a refusal, so a gap in it must not make the
        /// refusal look narrower than it is; a unit test holds the table against the declared flags so
        /// the fallback stays unreachable in practice.
        /// </remarks>
        private string DescribeOperations()
        {
            string[] described =
            [
                .. _operationDescriptions
                    .Where(entry => Reserved.ReservedOn.HasFlag(entry.Operation))
                    .Select(entry => entry.Description),
            ];

            ReservedQueryParameterOperations undescribed =
                Reserved.ReservedOn
                & ~_operationDescriptions.Aggregate(
                    ReservedQueryParameterOperations.None,
                    (combined, entry) => combined | entry.Operation
                );

            string[] parts =
                undescribed == ReservedQueryParameterOperations.None
                    ? described
                    : [.. described, undescribed.ToString()];

            return parts.Length switch
            {
                0 => "no operation",
                1 => parts[0],
                2 => $"{parts[0]} and {parts[1]}",
                _ => $"{string.Join(", ", parts[..^1])}, and {parts[^1]}",
            };
        }

        private static string Sanitize(string value) => LoggingSanitizer.SanitizeForLogging(value);
    }
}
