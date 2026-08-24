// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    /// <summary>
    /// Steps for the public cursor and partition contract: walking a collection by continuation,
    /// consuming every partition a response hands out, and reading the served OpenAPI documents.
    /// </summary>
    /// <remarks>
    /// A partial of the main step definitions rather than a new binding class, so the request context,
    /// authorization headers, and current-response handling are the ones every other scenario uses. A
    /// second class would need its own client and would drift from them.
    /// </remarks>
    public partial class StepDefinitions
    {
        private const string NextPageTokenHeader = "next-page-token";

        /// <summary>
        /// A walk cannot loop forever on a continuation that fails to advance: it exhausts this and
        /// fails with the pages it did retrieve rather than hanging.
        /// </summary>
        private const int MaximumWalkedPages = 60;

        private readonly List<string> _walkedDocumentIds = [];
        private readonly List<JsonNode> _walkedDocuments = [];
        private readonly List<string> _partitionTokens = [];
        private string _partitionsCollectionUrl = string.Empty;
        private int _walkedPages;
        private bool _walkEndedEmptyWithoutContinuation;

        /// <summary>
        /// Enters a cursor walk the way a client does: an ordinary page first, then the continuation it
        /// hands back, until a request offers none.
        /// </summary>
        [When("a cursor walk is made over {string} with page size {int}")]
        public Task WhenACursorWalkIsMadeOver(string url, int pageSize) =>
            CursorWalkAsync(url, pageSize, string.Empty);

        [When("a cursor walk is made over {string} with page size {int} repeating the query {string}")]
        public Task WhenACursorWalkIsMadeOverRepeating(string url, int pageSize, string repeatedQuery) =>
            CursorWalkAsync(url, pageSize, repeatedQuery);

        private async Task CursorWalkAsync(string url, int pageSize, string repeatedQuery)
        {
            string resolved = AddDataPrefixIfNecessary(url);
            string separator = string.IsNullOrEmpty(repeatedQuery) ? string.Empty : "&" + repeatedQuery;

            _walkedDocumentIds.Clear();
            _walkedDocuments.Clear();
            _walkedPages = 0;
            _walkEndedEmptyWithoutContinuation = false;

            var firstPage = await GetAsync($"{resolved}?limit={pageSize}{separator}");
            (List<JsonNode> documents, string? continuation) = await ReadPageAsync(firstPage, pageSize);

            _walkedPages++;
            Record(documents);

            while (continuation is not null && _walkedPages < MaximumWalkedPages)
            {
                var page = await GetAsync(
                    $"{resolved}?pageToken={Uri.EscapeDataString(continuation)}"
                        + $"&pageSize={pageSize}{separator}"
                );
                (documents, continuation) = await ReadPageAsync(page, pageSize);

                _walkedPages++;
                Record(documents);

                if (continuation is null)
                {
                    _walkEndedEmptyWithoutContinuation = documents.Count == 0;
                }
            }

            _walkedPages
                .Should()
                .BeLessThan(
                    MaximumWalkedPages,
                    "a walk that failed to advance would otherwise loop until the run is killed"
                );
        }

        /// <summary>
        /// Requests the partitions of a collection and remembers the tokens it handed out.
        /// </summary>
        [When("the partitions of {string} are requested")]
        public async Task WhenThePartitionsAreRequested(string url) =>
            await RequestPartitionsAsync(url, string.Empty);

        [When("the partitions of {string} are requested with {string}")]
        public async Task WhenThePartitionsAreRequestedWith(string url, string query) =>
            await RequestPartitionsAsync(url, query);

        private async Task RequestPartitionsAsync(string url, string query)
        {
            _partitionsCollectionUrl = AddDataPrefixIfNecessary(url);
            _partitionTokens.Clear();

            string requestUri = string.IsNullOrEmpty(query)
                ? $"{_partitionsCollectionUrl}/partitions"
                : $"{_partitionsCollectionUrl}/partitions?{query}";

            var response = await GetAsync(requestUri);
            string body = await response.TextAsync();

            response.Status.Should().Be(200, body);

            _partitionTokens.AddRange(
                JsonNode.Parse(body)!["pageTokens"]!.AsArray().Select(token => token!.GetValue<string>())
            );
        }

        /// <summary>
        /// Walks every partition the last partitions response handed out, to its terminal empty page,
        /// repeating the supplied query on every page request.
        /// </summary>
        /// <remarks>
        /// The repeated query is the page query, not the partitions query: the token stores no filter, so
        /// a walk that dropped it would widen its own candidate set. It is deliberately separate from the
        /// count the partitions request carried, which is not a collection query field at all.
        /// </remarks>
        [When("every returned partition is walked with page size {int}")]
        public async Task WhenEveryReturnedPartitionIsWalked(int pageSize) =>
            await WalkEveryPartitionAsync(pageSize, string.Empty);

        [When("every returned partition is walked with page size {int} repeating the query {string}")]
        public async Task WhenEveryReturnedPartitionIsWalkedRepeating(int pageSize, string repeatedQuery) =>
            await WalkEveryPartitionAsync(pageSize, repeatedQuery);

        private async Task WalkEveryPartitionAsync(int pageSize, string repeatedQuery)
        {
            string separator = string.IsNullOrEmpty(repeatedQuery) ? string.Empty : "&" + repeatedQuery;

            _walkedDocumentIds.Clear();
            _walkedDocuments.Clear();

            List<List<string>> perPartition = [];

            foreach (string partitionToken in _partitionTokens)
            {
                List<string> partitionIds = [];
                string? continuation = partitionToken;
                var pages = 0;

                while (continuation is not null && pages < MaximumWalkedPages)
                {
                    var page = await GetAsync(
                        $"{_partitionsCollectionUrl}?pageToken={Uri.EscapeDataString(continuation)}"
                            + $"&pageSize={pageSize}{separator}"
                    );
                    (List<JsonNode> documents, continuation) = await ReadPageAsync(page, pageSize);

                    pages++;
                    partitionIds.AddRange(Record(documents));

                    if (continuation is null)
                    {
                        documents.Should().BeEmpty("a partition ends on the request that selected nothing");
                    }
                }

                pages.Should().BeLessThan(MaximumWalkedPages);
                partitionIds.Should().OnlyHaveUniqueItems("a partition must not return a document twice");
                perPartition.Add(partitionIds);
            }

            for (var partition = 0; partition < perPartition.Count; partition++)
            {
                for (var other = partition + 1; other < perPartition.Count; other++)
                {
                    perPartition[partition]
                        .Should()
                        .NotIntersectWith(
                            perPartition[other],
                            $"partitions {partition} and {other} must cover disjoint ranges"
                        );
                }
            }
        }

        /// <summary>
        /// Walks every returned partition at the same time, each worker keeping its own state.
        /// </summary>
        /// <remarks>
        /// Parallel consumption is what a client actually does with a partition set, and it is not
        /// merely a faster spelling of the sequential walk: the ranges are consumed concurrently against
        /// one deployment, so a boundary that depended on request order would show up here and nowhere
        /// else.
        /// <para>
        /// Each worker issues its own requests and accumulates its own documents. Nothing here touches
        /// the shared current-response field, because concurrent writes to it would make every
        /// response-scoped assertion race; the shared accumulators are populated only after every worker
        /// has finished.
        /// </para>
        /// </remarks>
        [When("every returned partition is walked concurrently with page size {int}")]
        public async Task WhenEveryReturnedPartitionIsWalkedConcurrently(int pageSize)
        {
            _walkedDocumentIds.Clear();
            _walkedDocuments.Clear();

            List<JsonNode>[] perPartition = await Task.WhenAll(
                _partitionTokens.Select(partitionToken =>
                    WalkOnePartitionConcurrentlyAsync(partitionToken, pageSize)
                )
            );

            for (var partition = 0; partition < perPartition.Length; partition++)
            {
                List<string> partitionIds = [.. perPartition[partition].Select(DocumentId)];

                partitionIds
                    .Should()
                    .OnlyHaveUniqueItems($"partition {partition} must not return a document twice");

                for (var other = partition + 1; other < perPartition.Length; other++)
                {
                    partitionIds
                        .Should()
                        .NotIntersectWith(
                            perPartition[other].Select(DocumentId),
                            $"partitions {partition} and {other} must cover disjoint ranges"
                        );
                }

                _walkedDocuments.AddRange(perPartition[partition]);
                _walkedDocumentIds.AddRange(partitionIds);
            }
        }

        /// <summary>
        /// One concurrent worker: walks a single range to its terminal empty page using its own request
        /// calls and its own accumulator.
        /// </summary>
        private async Task<List<JsonNode>> WalkOnePartitionConcurrentlyAsync(
            string partitionToken,
            int pageSize
        )
        {
            List<JsonNode> documents = [];
            string? continuation = partitionToken;
            var pages = 0;

            while (continuation is not null && pages < MaximumWalkedPages)
            {
                IAPIResponse page = await _playwrightContext.ApiRequestContext?.GetAsync(
                    $"{_partitionsCollectionUrl}?pageToken={Uri.EscapeDataString(continuation)}"
                        + $"&pageSize={pageSize}",
                    new() { Headers = GetHeaders() }
                )!;

                (List<JsonNode> pageDocuments, continuation) = await ReadPageAsync(page, pageSize);

                pages++;
                documents.AddRange(pageDocuments);

                if (continuation is null)
                {
                    pageDocuments
                        .Should()
                        .BeEmpty(
                            "each concurrent worker must reach its own terminal request, the one that "
                                + "selected nothing and offered no continuation"
                        );
                }
            }

            pages
                .Should()
                .BeLessThan(
                    MaximumWalkedPages,
                    "a worker whose continuation failed to advance would otherwise loop until the run "
                        + "is killed"
                );

            return documents;
        }

        private static string DocumentId(JsonNode document) => document["id"]!.GetValue<string>();

        [Then("the walk returned {int} documents with no duplicates")]
        public void ThenTheWalkReturnedDocumentsWithNoDuplicates(int expectedCount)
        {
            _walkedDocumentIds.Should().OnlyHaveUniqueItems("a walk must not return a document twice");
            _walkedDocumentIds
                .Should()
                .HaveCount(
                    expectedCount,
                    "the walk covers exactly the documents this scenario seeded, because the suite "
                        + "resets the data before each scenario"
                );
        }

        /// <summary>
        /// Asserts the walk returned exactly the documents the scenario seeded, compared by a stable
        /// natural-key field rather than by count.
        /// </summary>
        /// <remarks>
        /// A count plus uniqueness would still pass if one seeded member were missing and a different
        /// member of the same collection took its place. Naming the expected key values in the feature
        /// makes the seed an independent oracle rather than something inferred from the response.
        /// </remarks>
        [Then("the walk returned exactly these {string} values")]
        public void ThenTheWalkReturnedExactlyTheseValues(string field, DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            string[] expected = [.. table.Rows.Select(row => row[0])];

            string[] observed =
            [
                .. _walkedDocuments.Select(document =>
                    document[field]?.ToString()
                    ?? throw new InvalidOperationException(
                        $"A returned document has no '{field}' to compare: {document.ToJsonString()}"
                    )
                ),
            ];

            observed
                .Should()
                .BeEquivalentTo(
                    expected,
                    $"the walk must return exactly the seeded '{field}' values, no more and no fewer"
                );
        }

        [Then("the walk ended with an empty page and no continuation")]
        public void ThenTheWalkEndedWithAnEmptyPageAndNoContinuation() =>
            _walkEndedEmptyWithoutContinuation
                .Should()
                .BeTrue(
                    "the implementation does not fetch an extra row to predict the terminal page, so "
                        + "the last useful page is followed by exactly one empty request"
                );

        [Then("at most {int} partition tokens were returned")]
        public void ThenAtMostPartitionTokensWereReturned(int maximum) =>
            _partitionTokens
                .Should()
                .HaveCountLessThanOrEqualTo(
                    maximum,
                    "the requested count is an upper bound the response never exceeds"
                );

        [Then("at least {int} partition tokens were returned")]
        public void ThenAtLeastPartitionTokensWereReturned(int minimum) =>
            _partitionTokens
                .Should()
                .HaveCountGreaterThanOrEqualTo(
                    minimum,
                    "the candidate set and the configured page size must really produce this many "
                        + "ranges; a smaller count means the request reached a differently configured "
                        + "deployment than the one this scenario needs"
                );

        [Then("at least one partition token was returned")]
        public void ThenAtLeastOnePartitionTokenWasReturned() =>
            _partitionTokens.Should().NotBeEmpty("a non-empty collection yields at least one partition");

        /// <summary>
        /// Captures a response header into the variable dictionary the request steps substitute from.
        /// </summary>
        /// <remarks>
        /// Deliberately not the existing "is stored as variable" step: that one lives in the profile step
        /// definitions, which construct their own <c>ScenarioVariables</c>, so a value it stores is
        /// invisible to the request steps in this class and the placeholder would reach the server
        /// unsubstituted. This one shares the dictionary the substitution actually reads.
        /// </remarks>
        [Then("the response header {string} is captured as {string}")]
        public void ThenTheResponseHeaderIsCapturedAs(string header, string variableName)
        {
            string key = header.ToLowerInvariant();

            _apiResponse.Headers.Should().ContainKey(key, $"'{header}' must be present to be captured");

            _scenarioVariables.Add(variableName, _apiResponse.Headers[key]);
        }

        [Then("the response header {string} is present")]
        public void ThenTheResponseHeaderIsPresent(string header) =>
            _apiResponse
                .Headers.Should()
                .ContainKey(header.ToLowerInvariant(), $"'{header}' must be present");

        [Then("the response header {string} is absent")]
        public void ThenTheResponseHeaderIsAbsent(string header) =>
            _apiResponse
                .Headers.Should()
                .NotContainKey(header.ToLowerInvariant(), $"'{header}' must be absent");

        /// <summary>
        /// Asserts the response's media type exactly, ignoring ordinary parameters such as charset.
        /// </summary>
        /// <remarks>
        /// A prefix comparison would accept any media type whose token merely begins with the expected
        /// one, so the header is parsed and its media type compared rather than matched textually.
        /// </remarks>
        [Then("the response content type is {string}")]
        public void ThenTheResponseContentTypeIs(string expected)
        {
            _apiResponse.Headers.Should().ContainKey("content-type");

            string contentType = _apiResponse.Headers["content-type"];

            MediaTypeHeaderValue
                .TryParse(contentType, out MediaTypeHeaderValue? parsed)
                .Should()
                .BeTrue($"'{contentType}' must be a parseable media type");

            parsed!.MediaType.Should().Be(expected, "the response media type is part of the public contract");
        }

        [Then("the response body has exactly one error {string}")]
        public async Task ThenTheResponseBodyHasExactlyOneError(string expectedError)
        {
            string body = await _apiResponse.TextAsync();

            JsonNode.Parse(body)!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal([expectedError], $"a rejected cursor request reports exactly one error. Body: {body}");
        }

        [Then("the response body errors are")]
        public async Task ThenTheResponseBodyErrorsAre(DataTable table)
        {
            ArgumentNullException.ThrowIfNull(table);

            string body = await _apiResponse.TextAsync();

            JsonNode.Parse(body)!["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(
                    [.. table.Rows.Select(row => row[0])],
                    $"the errors are reported in the contract's canonical order. Body: {body}"
                );
        }

        [Then("the response body is the parameter validation shell")]
        public async Task ThenTheResponseBodyIsTheParameterValidationShell() =>
            await AssertProblemShellAsync(
                "Parameters supplied to the request were invalid.",
                "urn:ed-fi:api:bad-request:parameter-validation-failed",
                "Parameter Validation Failed"
            );

        [Then("the response body is the bad request shell")]
        public async Task ThenTheResponseBodyIsTheBadRequestShell() =>
            await AssertProblemShellAsync(
                "The request could not be processed. See 'errors' for details.",
                "urn:ed-fi:api:bad-request",
                "Bad Request"
            );

        private async Task AssertProblemShellAsync(string detail, string type, string title)
        {
            string body = await _apiResponse.TextAsync();
            JsonNode problem = JsonNode.Parse(body)!;

            problem["detail"]!.GetValue<string>().Should().Be(detail, body);
            problem["type"]!.GetValue<string>().Should().Be(type, body);
            problem["title"]!.GetValue<string>().Should().Be(title, body);
            problem["status"]!.GetValue<int>().Should().Be(400, body);
            problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace(body);
            problem["validationErrors"]!.AsObject().Should().BeEmpty(body);
        }

        [Then("the served OpenAPI document contains path {string}")]
        public async Task ThenTheServedOpenApiDocumentContainsPath(string path) =>
            (await ServedPathsAsync())
                .Should()
                .ContainKey(path, $"the served document must publish '{path}'");

        [Then("the served OpenAPI document does not contain path {string}")]
        public async Task ThenTheServedOpenApiDocumentDoesNotContainPath(string path) =>
            (await ServedPathsAsync())
                .Should()
                .NotContainKey(path, $"the served document must not publish '{path}'");

        [Then("the served OpenAPI document publishes at least one path")]
        public async Task ThenTheServedOpenApiDocumentPublishesAtLeastOnePath() =>
            (await ServedPathsAsync())
                .Should()
                .NotBeEmpty(
                    "a document with no paths at all would satisfy every absence assertion vacuously"
                );

        [Then("the served OpenAPI path {string} has operation {string}")]
        public async Task ThenTheServedOpenApiPathHasOperation(string path, string operation)
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);
            paths[path]!
                .AsObject()
                .Should()
                .ContainKey(
                    operation.ToLowerInvariant(),
                    $"'{path}' must publish its {operation.ToUpperInvariant()} operation"
                );
        }

        [Then("the served OpenAPI path {string} does not have operation {string}")]
        public async Task ThenTheServedOpenApiPathDoesNotHaveOperation(string path, string operation)
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);
            paths[path]!
                .AsObject()
                .Should()
                .NotContainKey(
                    operation.ToLowerInvariant(),
                    $"'{path}' must not publish a {operation.ToUpperInvariant()} operation"
                );
        }

        [Then("the served OpenAPI operation {string} on path {string} references parameter {string}")]
        public async Task ThenTheServedOpenApiOperationReferencesParameter(
            string operation,
            string path,
            string parameterComponent
        )
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);

            JsonArray parameters = paths[path]![operation.ToLowerInvariant()]!["parameters"]!.AsArray();

            parameters
                .Select(parameter => parameter?["$ref"]?.GetValue<string>())
                .Should()
                .Contain(
                    $"#/components/parameters/{parameterComponent}",
                    $"'{operation} {path}' must reference the '{parameterComponent}' parameter"
                );
        }

        [Then("the served OpenAPI operation {string} on path {string} declares response header {string}")]
        public async Task ThenTheServedOpenApiOperationDeclaresResponseHeader(
            string operation,
            string path,
            string header
        )
        {
            JsonObject paths = await ServedPathsAsync();

            paths.Should().ContainKey(path);

            JsonNode? headers = paths[path]![operation.ToLowerInvariant()]!["responses"]!["200"]!["headers"];

            headers.Should().NotBeNull($"'{operation} {path}' must declare HTTP 200 response headers");
            headers!.AsObject().Should().ContainKey(header);
        }

        private async Task<JsonObject> ServedPathsAsync()
        {
            string body = await _apiResponse.TextAsync();
            JsonNode? document = JsonNode.Parse(body);

            document.Should().NotBeNull("a served OpenAPI document must be JSON");

            return document!["paths"]?.AsObject() ?? [];
        }

        private async Task<IAPIResponse> GetAsync(string url)
        {
            _logger.log.Information(url);

            IAPIResponse response = await _playwrightContext.ApiRequestContext?.GetAsync(
                url,
                new() { Headers = GetHeaders() }
            )!;

            SetCurrentApiResponse(response);

            return response;
        }

        /// <summary>
        /// Reads one page: the document ids it carries and the continuation it offers.
        /// </summary>
        private static async Task<(List<JsonNode> Documents, string? Continuation)> ReadPageAsync(
            IAPIResponse response,
            int pageSize
        )
        {
            string body = await response.TextAsync();

            response.Status.Should().Be(200, body);

            List<JsonNode> documents = [.. JsonNode.Parse(body)!.AsArray().Select(document => document!)];

            documents.Should().HaveCountLessThanOrEqualTo(pageSize, "a page cannot exceed its page size");

            return response.Headers.TryGetValue(NextPageTokenHeader, out string? continuation)
                ? (documents, continuation)
                : (documents, null);
        }

        /// <summary>
        /// Remembers one page's documents and returns the ids they carry, so uniqueness and disjointness
        /// keep comparing response identities while the exact-union assertion compares natural keys.
        /// </summary>
        private List<string> Record(List<JsonNode> documents)
        {
            _walkedDocuments.AddRange(documents);

            List<string> ids = [.. documents.Select(document => document["id"]!.GetValue<string>())];

            _walkedDocumentIds.AddRange(ids);

            return ids;
        }
    }
}
