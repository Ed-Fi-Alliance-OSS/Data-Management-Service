// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.OpenApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.OpenApiGenerator.Tests.Integration;

/// <summary>
/// Assembles the complete resource and descriptor documents from the shipped authoritative Data Standard
/// 5.2 ApiSchema and verifies that every local reference in the result resolves. The other fixtures in this
/// project assert emissions against small synthetic inputs, which cannot show whether the contract holds at
/// customer scale or whether the assembled document leaves a dangling reference behind.
/// </summary>
[TestFixture]
public class AuthoritativeApiSchemaAssemblyTests
{
    private const string AuthoritativeApiSchemaRelativePath =
        "backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";

    /// <summary>
    /// A document of this size resolves thousands of references. The floor sits far below the real count
    /// and exists only so a walker that silently stops collecting cannot pass by finding nothing to check.
    /// </summary>
    private const int MinimumExpectedReferenceCount = 500;

    private JsonNode _resources = null!;
    private JsonNode _descriptors = null!;

    [OneTimeSetUp]
    public void Assemble()
    {
        string apiSchemaJson = File.ReadAllText(LocateAuthoritativeApiSchema(), Encoding.UTF8);

        _resources = CreateDocument(apiSchemaJson, OpenApiDocument.OpenApiDocumentType.Resource);
        _descriptors = CreateDocument(apiSchemaJson, OpenApiDocument.OpenApiDocumentType.Descriptor);
    }

    [Test]
    public void It_should_resolve_every_local_reference_in_the_resource_document()
    {
        AssertEveryLocalReferenceResolves(_resources, "resources");
    }

    [Test]
    public void It_should_resolve_every_local_reference_in_the_descriptor_document()
    {
        AssertEveryLocalReferenceResolves(_descriptors, "descriptors");
    }

    [Test]
    public void It_should_publish_the_cursor_paging_contract_at_full_scale()
    {
        _resources["paths"]!.AsObject().Should().ContainKey("/ed-fi/students/partitions");
        _descriptors["paths"]!.AsObject().Should().ContainKey("/ed-fi/academicSubjectDescriptors/partitions");
    }

    /// <summary>
    /// The generated operation inherits the failure responses its collection GET publishes, so at full
    /// scale it must carry the shipped fragment's statuses rather than only its own success shape. The
    /// reference-resolution tests above prove the inherited references are not left dangling.
    /// </summary>
    [Test]
    public void It_should_publish_the_partition_failure_contract_at_full_scale()
    {
        JsonObject partitionResponses = _resources["paths"]!["/ed-fi/students/partitions"]!["get"]![
            "responses"
        ]!.AsObject();

        partitionResponses.Should().ContainKeys("200", "400", "401", "403", "404", "500", "501");
        partitionResponses.Should().NotContainKey("304");
    }

    /// <summary>
    /// Assembly mutates the nodes it is given, so each document is assembled from its own parse of the
    /// input rather than from a shared one.
    /// </summary>
    private static JsonNode CreateDocument(
        string apiSchemaJson,
        OpenApiDocument.OpenApiDocumentType documentType
    )
    {
        JsonNode apiSchemaRootNode =
            JsonNode.Parse(apiSchemaJson)
            ?? throw new InvalidOperationException("The authoritative ApiSchema did not parse.");

        OpenApiDocument openApiDocument = new(
            NullLogger.Instance,
            pagingSettings: OpenApiPagingSettings.Default
        );

        return openApiDocument.CreateDocument(
            new ApiSchemaDocumentNodes(apiSchemaRootNode, []),
            documentType
        );
    }

    /// <summary>
    /// Walks up from the test assembly location until the shipped fixture is found, so the test does not
    /// depend on the build configuration or target framework in the output path.
    /// </summary>
    private static string LocateAuthoritativeApiSchema()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                AuthoritativeApiSchemaRelativePath.Replace('/', Path.DirectorySeparatorChar)
            );

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{AuthoritativeApiSchemaRelativePath}' above '{AppContext.BaseDirectory}'."
        );
    }

    private static void AssertEveryLocalReferenceResolves(JsonNode document, string documentName)
    {
        List<string> references = CollectReferences(document);

        references
            .Should()
            .HaveCountGreaterThan(
                MinimumExpectedReferenceCount,
                "the assembled {0} document must contain the references being checked",
                documentName
            );

        List<string> unresolved =
        [
            .. references
                .Distinct(StringComparer.Ordinal)
                .Where(reference => reference.StartsWith('#') && Resolve(document, reference) is null)
                .OrderBy(reference => reference, StringComparer.Ordinal),
        ];

        unresolved.Should().BeEmpty("every local reference in the {0} document must resolve", documentName);
    }

    /// <summary>
    /// Every reference value anywhere in the document, including references cursor-paging assembly did not
    /// emit, because a document with one dangling reference is invalid regardless of which pass wrote it.
    /// </summary>
    private static List<string> CollectReferences(JsonNode root)
    {
        List<string> references = [];
        Collect(root);
        return references;

        void Collect(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject jsonObject:
                    foreach ((string key, JsonNode? value) in jsonObject)
                    {
                        if (
                            key == "$ref"
                            && value is JsonValue jsonValue
                            && jsonValue.TryGetValue(out string? reference)
                        )
                        {
                            references.Add(reference);
                            continue;
                        }

                        Collect(value);
                    }

                    break;
                case JsonArray jsonArray:
                    foreach (JsonNode? item in jsonArray)
                    {
                        Collect(item);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Resolves a local reference as an RFC 6901 JSON Pointer against the document root, returning null
    /// when any segment is absent.
    /// </summary>
    private static JsonNode? Resolve(JsonNode document, string reference)
    {
        string pointer = reference[1..];

        if (pointer.Length == 0)
        {
            return document;
        }

        if (!pointer.StartsWith('/'))
        {
            return null;
        }

        JsonNode? current = document;

        foreach (string rawSegment in pointer[1..].Split('/'))
        {
            string segment = rawSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            current = current switch
            {
                JsonObject jsonObject => jsonObject.TryGetPropertyValue(segment, out JsonNode? value)
                    ? value
                    : null,
                JsonArray jsonArray
                    when int.TryParse(segment, out int index) && index >= 0 && index < jsonArray.Count =>
                    jsonArray[index],
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
