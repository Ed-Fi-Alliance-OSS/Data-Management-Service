// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    /// <summary>
    /// Steps for the snapshot contract (DMS-1369) in the OpenAPI documents a running DMS serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A partial of the main step definitions rather than a new binding class, for the same reason the
    /// cursor-paging steps are: the request context, authorization headers, and current-response
    /// handling are then the ones every other scenario uses.
    /// </para>
    /// <para>
    /// Only the assertions the existing served-OpenAPI steps do not already cover are added here.
    /// Path presence, operation presence, and parameter references are reused rather than restated.
    /// </para>
    /// </remarks>
    public partial class StepDefinitions
    {
        [Then("the served OpenAPI document declares boolean header parameter {string} defaulting to false")]
        public async Task ThenTheServedOpenApiDocumentDeclaresBooleanHeaderParameter(
            string parameterComponent
        )
        {
            JsonNode? parameter = await ServedComponentAsync("parameters", parameterComponent);

            parameter
                .Should()
                .NotBeNull(
                    $"an independently served document must define '{parameterComponent}' rather than "
                        + "resolve it out of a sibling document"
                );
            parameter!["name"]?.GetValue<string>().Should().Be(parameterComponent);
            parameter["in"]?.GetValue<string>().Should().Be("header");
            parameter["schema"]?["type"]?.GetValue<string>().Should().Be("boolean");
            parameter["schema"]
                ?["default"]?.GetValue<bool>()
                .Should()
                .BeFalse("an unaware client must be unaffected by the header existing");
        }

        [Then("the served OpenAPI document declares response {string} with content type {string}")]
        public async Task ThenTheServedOpenApiDocumentDeclaresResponseWithContentType(
            string responseComponent,
            string contentType
        )
        {
            JsonNode? response = await ServedComponentAsync("responses", responseComponent);

            response.Should().NotBeNull($"the served document must define '{responseComponent}'");
            response!
                ["content"]
                ?[contentType].Should()
                .NotBeNull($"'{responseComponent}' must be served as {contentType}");
            response["content"]![contentType]!
                ["schema"]
                ?["$ref"]?.GetValue<string>()
                .Should()
                .Be(
                    "#/components/schemas/ProblemDetails",
                    $"'{responseComponent}' must carry the shared ProblemDetails envelope"
                );
        }

        [Then("the served OpenAPI document declares response {string} with header {string} example {string}")]
        public async Task ThenTheServedOpenApiDocumentDeclaresResponseHeaderExample(
            string responseComponent,
            string header,
            string example
        )
        {
            JsonNode? response = await ServedComponentAsync("responses", responseComponent);

            response.Should().NotBeNull($"the served document must define '{responseComponent}'");
            response!
                ["headers"]
                ?[header].Should()
                .NotBeNull($"'{responseComponent}' must declare the {header} response header");
            response["headers"]![header]!["example"]?.GetValue<string>().Should().Be(example);
        }

        [Then(
            "the served OpenAPI operation {string} on path {string} answers {string} with response {string}"
        )]
        public async Task ThenTheServedOpenApiOperationAnswersWithResponse(
            string operation,
            string path,
            string statusCode,
            string responseComponent
        )
        {
            JsonObject paths = await ServedOpenApiPathsAsync();

            paths.Should().ContainKey(path);

            JsonNode? answered = paths[path]![operation.ToLowerInvariant()]
                ?["responses"]
                ?[statusCode]
                ?["$ref"];

            answered
                .Should()
                .NotBeNull(
                    $"'{operation} {path}' must answer {statusCode} with a referenced response component"
                );
            answered!
                .GetValue<string>()
                .Should()
                .Be(
                    $"#/components/responses/{responseComponent}",
                    $"'{operation} {path}' must answer {statusCode} with '{responseComponent}'"
                );
        }

        [Then("every local reference in the served OpenAPI document resolves")]
        public async Task ThenEveryLocalReferenceInTheServedOpenApiDocumentResolves()
        {
            JsonNode document = await ServedOpenApiDocumentAsync();

            UnresolvedReferencesOf(document)
                .Should()
                .BeEmpty(
                    "a client fetching this document on its own cannot resolve a reference out of a "
                        + "sibling document"
                );
        }

        private async Task<JsonNode> ServedOpenApiDocumentAsync()
        {
            string body = await CurrentApiResponse().TextAsync();
            JsonNode? document = JsonNode.Parse(body);

            document.Should().NotBeNull("a served OpenAPI document must be JSON");

            return document!;
        }

        /// <summary>
        /// The response of the most recent request, whichever binding class issued it.
        /// </summary>
        /// <remarks>
        /// Read from the scenario context rather than from this class's own field. Some features are
        /// served by a feature-scoped request step in a different binding class, which fills its own
        /// field and leaves this one null; every request step publishes the response here, so this is
        /// the one place all of them agree on.
        /// </remarks>
        private IAPIResponse CurrentApiResponse()
        {
            IAPIResponse? response =
                _scenarioContext.TryGetValue(ApiResponseContextKey, out IAPIResponse? published)
                && published is not null
                    ? published
                    : _apiResponse;

            response
                .Should()
                .NotBeNull("a request must have been made before asserting on the document it returned");

            return response!;
        }

        private async Task<JsonObject> ServedOpenApiPathsAsync() =>
            (await ServedOpenApiDocumentAsync())["paths"]?.AsObject() ?? [];

        private async Task<JsonNode?> ServedComponentAsync(string section, string name) =>
            (await ServedOpenApiDocumentAsync())["components"]?[section]?[name];

        /// <summary>
        /// Every reference in the document that does not resolve inside that same document.
        /// </summary>
        /// <remarks>
        /// A local copy of the walk the unit fixtures use, kept local on purpose: one shared
        /// implementation could be broken once and have every layer agree that nothing was wrong.
        /// </remarks>
        private static IReadOnlyList<string> UnresolvedReferencesOf(JsonNode document)
        {
            List<string> unresolved = [];
            Walk(document, "$");
            return unresolved;

            void Walk(JsonNode? node, string location)
            {
                if (node is JsonArray array)
                {
                    for (int index = 0; index < array.Count; index++)
                    {
                        Walk(array[index], $"{location}[{index}]");
                    }

                    return;
                }

                if (node is not JsonObject jsonObject)
                {
                    return;
                }

                foreach ((string key, JsonNode? value) in jsonObject)
                {
                    // An "example" is response data rather than schema, so a reference-shaped key inside
                    // one is a value and not a reference.
                    if (key == "example")
                    {
                        continue;
                    }

                    if (key != "$ref")
                    {
                        Walk(value, $"{location}.{key}");
                        continue;
                    }

                    string? reference =
                        value is JsonValue referenceValue && referenceValue.TryGetValue(out string? text)
                            ? text
                            : null;

                    if (reference is null || !Resolves(document, reference))
                    {
                        unresolved.Add($"{location} -> {reference ?? "non-string $ref"}");
                    }
                }
            }
        }

        private static bool Resolves(JsonNode document, string reference)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return false;
            }

            JsonNode? current = document;

            foreach (string segment in reference[2..].Split('/'))
            {
                string token = segment
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);

                if (current is JsonObject currentObject)
                {
                    if (!currentObject.TryGetPropertyValue(token, out current))
                    {
                        return false;
                    }

                    continue;
                }

                if (
                    current is JsonArray currentArray
                    && int.TryParse(token, out int index)
                    && index >= 0
                    && index < currentArray.Count
                )
                {
                    current = currentArray[index];
                    continue;
                }

                return false;
            }

            return current is not null;
        }
    }
}
