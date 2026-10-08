// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.OpenApi.OpenApiSnapshotContractAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Pins the served snapshot response metadata to the runtime responses DMS actually sends, so the two
/// cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// The failure contract has two independent authors. The runtime bodies are built in this repository
/// by <see cref="SnapshotFailureResponse" /> (DMS-1368), while the documents that describe them are
/// authored upstream in MetaEd and arrive inside ApiSchema packages. Nothing in either repository
/// forces them to agree, and a client that trusts the document would be misled by any divergence.
/// </para>
/// <para>
/// Each assertion therefore reads one value from the runtime response and the matching value from
/// every packaged document that declares the response, rather than comparing either against a literal
/// written here. A constant duplicated into the test would simply be a third place to drift.
/// </para>
/// </remarks>
[TestFixture]
public class SnapshotOpenApiRuntimeParityTests
{
    private const string CorrelationId = "df2b4e1a-8c6f-4a9b-9d31-0f7c2b5e8a44";

    /// <summary>
    /// The base documents that declare each snapshot response, named so a failure says which published
    /// document disagreed with the runtime. Asserted as an exact set, because a document silently
    /// dropping out would otherwise leave the remaining ones to carry a passing result.
    /// </summary>
    private static readonly string[] _sourcesDeclaringNotFound =
    [
        "Data Standard 5.2 resources",
        "Data Standard 5.2 descriptors",
        "Data Standard 5.2 changeQueries",
        "Data Standard 6.1 resources",
        "Data Standard 6.1 descriptors",
        "Data Standard 6.1 changeQueries",
    ];

    /// <summary>
    /// The snapshot 405 is declared only by the documents that serve mutations, so the standalone
    /// Change Queries document is deliberately absent.
    /// </summary>
    private static readonly string[] _sourcesDeclaringMethodNotAllowed =
    [
        "Data Standard 5.2 resources",
        "Data Standard 5.2 descriptors",
        "Data Standard 6.1 resources",
        "Data Standard 6.1 descriptors",
    ];

    private IFrontendResponse _runtimeNotFound = null!;
    private IFrontendResponse _runtimeMethodNotAllowed = null!;
    private IReadOnlyList<PublishedResponse> _publishedNotFound = null!;
    private IReadOnlyList<PublishedResponse> _publishedMethodNotAllowed = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TraceId traceId = new(CorrelationId);
        _runtimeNotFound = SnapshotFailureResponse.NotFound(traceId);
        _runtimeMethodNotAllowed = SnapshotFailureResponse.MethodNotAllowed(traceId);

        List<PublishedResponse> notFound = [];
        List<PublishedResponse> methodNotAllowed = [];

        foreach (
            (string packageLabel, string metadataKey) in new[]
            {
                ("Data Standard 5.2", "DataStandard52ApiSchemaPackageRoot"),
                ("Data Standard 6.1", "DataStandard61ApiSchemaPackageRoot"),
            }
        )
        {
            JsonNode root = PackagedApiSchemaContract.LoadPackagedRootNode(metadataKey);

            if (root["projectSchema"]?["openApiBaseDocuments"] is not JsonObject baseDocuments)
            {
                continue;
            }

            foreach ((string documentType, JsonNode? baseDocument) in baseDocuments)
            {
                if (baseDocument is null)
                {
                    continue;
                }

                string source = $"{packageLabel} {documentType}";

                Collect(notFound, baseDocument, source, SnapshotNotFoundResponseName);
                Collect(methodNotAllowed, baseDocument, source, SnapshotMethodNotAllowedResponseName);
            }
        }

        _publishedNotFound = notFound;
        _publishedMethodNotAllowed = methodNotAllowed;
    }

    [Test]
    public void It_reads_every_document_that_publishes_a_snapshot_response()
    {
        _publishedNotFound
            .Select(published => published.Source)
            .Should()
            .BeEquivalentTo(
                _sourcesDeclaringNotFound,
                "every independently served document declares the Snapshot Not Found response"
            );

        _publishedMethodNotAllowed
            .Select(published => published.Source)
            .Should()
            .BeEquivalentTo(
                _sourcesDeclaringMethodNotAllowed,
                "the snapshot 405 is declared exactly by the documents that serve mutations"
            );
    }

    [Test]
    public void It_publishes_the_snapshot_not_found_body_the_runtime_sends()
    {
        AssertExampleMatchesRuntimeBody(_publishedNotFound, _runtimeNotFound);
    }

    [Test]
    public void It_publishes_the_snapshot_method_not_allowed_body_the_runtime_sends()
    {
        AssertExampleMatchesRuntimeBody(_publishedMethodNotAllowed, _runtimeMethodNotAllowed);
    }

    [Test]
    public void It_publishes_the_content_type_the_runtime_sends()
    {
        foreach (PublishedResponse published in _publishedNotFound.Concat(_publishedMethodNotAllowed))
        {
            published
                .ContentTypes.Should()
                .ContainSingle(
                    "the {0} {1} response describes exactly the media type the runtime sends",
                    published.Source,
                    published.ResponseName
                )
                .Which.Should()
                .Be(RuntimeContentTypeFor(published));
        }
    }

    [Test]
    public void It_publishes_the_allow_header_the_runtime_sends()
    {
        string runtimeAllow = _runtimeMethodNotAllowed
            .Headers.Should()
            .ContainKey("Allow", "RFC 9110 requires Allow on a 405")
            .WhoseValue;

        foreach (PublishedResponse published in _publishedMethodNotAllowed)
        {
            published
                .AllowHeaderExample.Should()
                .Be(
                    runtimeAllow,
                    "the {0} snapshot 405 must advertise the Allow value the runtime sends, which states "
                        + "what is permitted against a read-only snapshot rather than what the route "
                        + "permits on the primary",
                    published.Source
                );
        }
    }

    [Test]
    public void It_publishes_a_problem_details_schema_describing_the_runtime_body()
    {
        (PublishedResponse Published, IFrontendResponse Runtime)[] pairs =
        [
            .. _publishedNotFound.Select(published => (published, _runtimeNotFound)),
            .. _publishedMethodNotAllowed.Select(published => (published, _runtimeMethodNotAllowed)),
        ];

        foreach ((PublishedResponse published, IFrontendResponse runtime) in pairs)
        {
            published
                .SchemaReference.Should()
                .Be(
                    "#/components/schemas/ProblemDetails",
                    "the {0} {1} response must describe the shared DMS ProblemDetails envelope",
                    published.Source,
                    published.ResponseName
                );

            IReadOnlyList<string> declaredProperties = published.SchemaPropertyNames;

            declaredProperties
                .Should()
                .NotBeEmpty(
                    "the {0} document must resolve {1} to a schema with properties",
                    published.Source,
                    published.SchemaReference
                );

            // The runtime envelope is the authority on which fields a client sees. The published schema
            // must describe each of them; whether it also marks them required is upstream's call and is
            // deliberately not asserted here.
            RuntimeBodyPropertyNames(runtime)
                .Should()
                .BeSubsetOf(
                    declaredProperties,
                    "every field the runtime {0} body carries must be described by the {1} schema",
                    published.ResponseName,
                    published.Source
                );
        }
    }

    private static void AssertExampleMatchesRuntimeBody(
        IReadOnlyList<PublishedResponse> publishedResponses,
        IFrontendResponse runtime
    )
    {
        JsonNode runtimeBody = runtime.Body.Should().NotBeNull().And.Subject.As<JsonNode>();

        foreach (PublishedResponse published in publishedResponses)
        {
            JsonNode example = published
                .Example.Should()
                .NotBeNull(
                    "the {0} {1} response must publish an example of the body a client receives",
                    published.Source,
                    published.ResponseName
                )
                .And.Subject.As<JsonNode>();

            foreach (string field in new[] { "type", "title", "detail" })
            {
                TextAt(example[field])
                    .Should()
                    .Be(
                        TextAt(runtimeBody[field]),
                        "the {0} {1} response must publish the '{2}' the runtime sends",
                        published.Source,
                        published.ResponseName,
                        field
                    );
            }

            int? publishedStatus = IntAt(example["status"]);

            publishedStatus
                .Should()
                .Be(
                    IntAt(runtimeBody["status"]),
                    "the {0} {1} response must publish the status the runtime body carries",
                    published.Source,
                    published.ResponseName
                );
            publishedStatus
                .Should()
                .Be(
                    runtime.StatusCode,
                    "the {0} {1} response body and the response it is returned with must agree",
                    published.Source,
                    published.ResponseName
                );
        }
    }

    private string RuntimeContentTypeFor(PublishedResponse published) =>
        published.ResponseName == SnapshotNotFoundResponseName
            ? _runtimeNotFound.ContentType!
            : _runtimeMethodNotAllowed.ContentType!;

    private static IReadOnlyList<string> RuntimeBodyPropertyNames(IFrontendResponse runtime) =>
        runtime.Body is JsonObject body ? [.. body.Select(property => property.Key)] : [];

    private static void Collect(
        List<PublishedResponse> collected,
        JsonNode baseDocument,
        string source,
        string responseName
    )
    {
        if (Component(baseDocument, "responses", responseName) is not JsonNode response)
        {
            return;
        }

        JsonObject? content = response["content"]?.AsObject();
        string? schemaReference = TextAt(content?.FirstOrDefault().Value?["schema"]?["$ref"]);

        collected.Add(
            new PublishedResponse(
                source,
                responseName,
                response["content"]?.AsObject()?.Select(media => media.Key).ToArray() ?? [],
                content?.FirstOrDefault().Value?["example"],
                schemaReference,
                ResolveSchemaPropertyNames(baseDocument, schemaReference),
                TextAt(response["headers"]?["Allow"]?["example"])
            )
        );
    }

    /// <summary>
    /// Resolves the response's schema reference inside the document that carries it, which is the only
    /// place an independently served document can resolve it.
    /// </summary>
    private static IReadOnlyList<string> ResolveSchemaPropertyNames(
        JsonNode baseDocument,
        string? schemaReference
    )
    {
        const string SchemaPrefix = "#/components/schemas/";

        if (schemaReference is null || !schemaReference.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        return
            Component(baseDocument, "schemas", schemaReference[SchemaPrefix.Length..])?["properties"]
                is JsonObject properties
            ? [.. properties.Select(property => property.Key)]
            : [];
    }

    private static string? TextAt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static int? IntAt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int parsed) ? parsed : null;

    private sealed record PublishedResponse(
        string Source,
        string ResponseName,
        IReadOnlyList<string> ContentTypes,
        JsonNode? Example,
        string? SchemaReference,
        IReadOnlyList<string> SchemaPropertyNames,
        string? AllowHeaderExample
    );
}
