// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

// Provider-neutral harness for link-injection integration tests (DMS-1145 task 29).
// The real DocumentLinkSlugResolver has its own unit coverage; the deterministic resolver
// below lets provider integration tests focus on the auxiliary-lookup -> hydration ->
// reconstitution -> link.rel/href path without dragging IApiSchemaProvider into the
// fixture.

internal sealed class DeterministicLinkSlugResolver(
    IReadOnlyDictionary<short, DocumentLinkSlugTriple> slugsByResourceKeyId
) : IDocumentLinkSlugResolver
{
    private readonly IReadOnlyDictionary<short, DocumentLinkSlugTriple> _slugsByResourceKeyId =
        slugsByResourceKeyId ?? throw new ArgumentNullException(nameof(slugsByResourceKeyId));

    public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId)
    {
        if (!_slugsByResourceKeyId.TryGetValue(resourceKeyId, out DocumentLinkSlugTriple? triple))
        {
            throw new InvalidOperationException(
                $"DeterministicLinkSlugResolver has no triple registered for ResourceKeyId {resourceKeyId.ToString(CultureInfo.InvariantCulture)}. "
                    + "Register every ResourceKeyId the test expects to encounter."
            );
        }

        return triple;
    }
}

internal static class LinkInjectionAssertions
{
    public static void AssertLink(
        JsonNode referenceObject,
        string expectedRel,
        string expectedProjectEndpointName,
        string expectedEndpointName,
        Guid expectedDocumentUuid
    )
    {
        ArgumentNullException.ThrowIfNull(referenceObject);

        JsonNode? link = referenceObject["link"];
        link.Should()
            .NotBeNull(
                "link must be emitted on a fully-defined document reference when ResourceLinksOptions.Enabled is true"
            );

        JsonNode? rel = link!["rel"];
        rel.Should().NotBeNull("link.rel must be present");
        rel!.GetValue<string>().Should().Be(expectedRel);

        JsonNode? href = link!["href"];
        href.Should().NotBeNull("link.href must be present");
        href!
            .GetValue<string>()
            .Should()
            .Be(
                $"/{expectedProjectEndpointName}/{expectedEndpointName}/{expectedDocumentUuid.ToString("D", CultureInfo.InvariantCulture)}"
            );
    }
}

/// <summary>
/// Path.Value/semantic locator: resolves a JsonPath expression against a materialized document,
/// returning all matching nodes. Deliberately supports a small subset sufficient for the
/// DMS-1145 reference-shape coverage:
///   "$"            — document root
///   ".member"      — object property access
///   "[*]"          — array wildcard
/// Anything else throws so a typo doesn't silently match nothing. Use this instead of
/// indexing arrays positionally (memory: ordering of references is not an explicit
/// contract).
/// </summary>
internal static class ReferenceLocator
{
    public static IReadOnlyList<JsonNode> ResolveAll(JsonNode document, string pathExpression)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(pathExpression) || pathExpression[0] != '$')
        {
            throw new ArgumentException(
                $"Path expression must start with '$'. Got '{pathExpression}'.",
                nameof(pathExpression)
            );
        }

        List<JsonNode> current = [document];
        int cursor = 1;

        while (cursor < pathExpression.Length)
        {
            char head = pathExpression[cursor];
            if (head == '.')
            {
                cursor++;
                int memberStart = cursor;
                while (
                    cursor < pathExpression.Length
                    && pathExpression[cursor] != '.'
                    && pathExpression[cursor] != '['
                )
                {
                    cursor++;
                }

                string member = pathExpression[memberStart..cursor];
                if (member.Length == 0)
                {
                    throw new ArgumentException(
                        $"Empty member name after '.' in path '{pathExpression}'.",
                        nameof(pathExpression)
                    );
                }

                List<JsonNode> next = [];
                foreach (JsonNode node in current)
                {
                    JsonNode? child = node[member];
                    if (child is not null)
                    {
                        next.Add(child);
                    }
                }
                current = next;
            }
            else if (
                cursor + 2 < pathExpression.Length
                && pathExpression[cursor] == '['
                && pathExpression[cursor + 1] == '*'
                && pathExpression[cursor + 2] == ']'
            )
            {
                cursor += 3;
                List<JsonNode> next = [];
                foreach (JsonNode node in current)
                {
                    if (node is JsonArray array)
                    {
                        foreach (JsonNode? element in array)
                        {
                            if (element is not null)
                            {
                                next.Add(element);
                            }
                        }
                    }
                }
                current = next;
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported path syntax at position {cursor.ToString(CultureInfo.InvariantCulture)} in '{pathExpression}'. Only '.member' and '[*]' are supported.",
                    nameof(pathExpression)
                );
            }
        }

        return current;
    }

    public static JsonNode RequireSingle(JsonNode document, string pathExpression)
    {
        IReadOnlyList<JsonNode> matches = ResolveAll(document, pathExpression);
        matches
            .Should()
            .HaveCount(
                1,
                $"path '{pathExpression}' must resolve to exactly one node, found {matches.Count.ToString(CultureInfo.InvariantCulture)}"
            );
        return matches[0];
    }
}
