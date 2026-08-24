// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Tests.Integration.OdsParity;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Executes the static ODS 7.3.2 comparison cases against this host and holds each observation against
/// both the DMS outcome the case declares and the ODS outcome it records.
///
/// <para>
/// Fail-closed in both directions. An observation that differs from the recorded ODS outcome must name
/// an approved difference from the committed catalog, or the case fails as an unmapped difference. A
/// case that declares a difference which no longer materializes fails too, so a catalog entry cannot
/// outlive the behavior it describes. A case declaring parity must observe exactly the recorded ODS
/// outcome.
/// </para>
///
/// <para>
/// Only the DMS half is executed. No ODS instance is stood up, which is explicitly out of scope; the
/// ODS column is static expected data derived from the pinned sources the reference metadata names.
/// </para>
/// </summary>
internal static class OdsComparisonScenario
{
    /// <summary>The page size fixtures binding the sizing group must configure their host with.</summary>
    /// <remarks>
    /// Two puts the minimum partition size at ten rows, which is what lets a twenty-five document seed
    /// be cut by a computed size rather than by the minimum — the only arrangement in which a true
    /// ceiling and a floor produce different boundaries.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// The deployed maximum page size, used by the group whose whole point is a runtime maximum above
    /// the published Ed-Fi default of twenty-five.
    /// </summary>
    internal const int DeployedMaximumPageSize = 500;

    internal const int HostDefaultPartitionCount = 10;

    private const string CollisionNumber = "105";

    /// <summary>
    /// The collection whose profile document and runtime profile behavior the profile group observes. It
    /// belongs to the profile fixture, which is the only one carrying profile XML.
    /// </summary>
    private const string ProfileMergeItemsPartitionsEndpoint =
        "/data/ed-fi/profileRootOnlyMergeItems/partitions";

    /// <summary>
    /// Names the write-only profile with the readable usage, which is how a client explicitly asks to
    /// read through a profile that has no readable content type for the resource.
    /// </summary>
    private const string WriteOnlyReadableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-writeonly.readable+json";

    internal const string ProfileDocumentProfileName = "AcademicWeek-WriteOnly";

    private const string CoreResourcesDocument = "/metadata/specifications/resources-spec.json";

    private const string ProfileResourcesDocument =
        $"/metadata/specifications/profiles/{ProfileDocumentProfileName}/resources-spec.json";

    /// <summary>
    /// The Data Standard 5.2 collection the write-only profile covers, and its partition sibling. The
    /// authoritative document really publishes both, which is what makes the profile document's omission
    /// of the second one observable.
    /// </summary>
    private const string ProfiledCollectionPath = "/ed-fi/academicWeeks";

    private const string ProfiledPartitionsPath = $"{ProfiledCollectionPath}/partitions";

    /// <summary>Runs every case in one group and reports them all before failing.</summary>
    public static async Task RunGroupAsync(
        ApiIntegrationHarness harness,
        string group,
        int hostMaximumPageSize
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        ComparisonCase[] cases =
        [
            .. OdsComparisonCatalog.Definitions.Cases.Where(comparisonCase => comparisonCase.Group == group),
        ];

        cases.Should().NotBeEmpty($"group '{group}' must contain at least one comparison case");

        foreach (ComparisonCase comparisonCase in cases)
        {
            await RunCaseAsync(harness, comparisonCase, hostMaximumPageSize);
        }
    }

    private static async Task RunCaseAsync(
        ApiIntegrationHarness harness,
        ComparisonCase comparisonCase,
        int hostMaximumPageSize
    )
    {
        IReadOnlyDictionary<string, string> rawPlaceholders = Placeholders(hostMaximumPageSize);
        IReadOnlyDictionary<string, string> placeholders = ForQueryString(rawPlaceholders);

        ExpectedOutcome dms = comparisonCase.Dms.Resolve(rawPlaceholders);
        ExpectedOutcome ods = comparisonCase.Ods.Resolve(rawPlaceholders);

        ObservedOutcome observation = await ObserveAsync(
            harness,
            comparisonCase,
            placeholders,
            rawPlaceholders,
            hostMaximumPageSize
        );

        AssertMatches(observation, dms, comparisonCase, "DMS");

        bool matchesOds = OdsOutcomeComparer.Matches(observation, ods);

        if (comparisonCase.DeclaresDifference)
        {
            matchesOds
                .Should()
                .BeFalse(
                    "case '{0}' declares a difference from ODS 7.3.2, so the observed outcome must not "
                        + "be the recorded ODS outcome; if the behavior converged, the case and the "
                        + "approved difference it names are both out of date",
                    comparisonCase.Id
                );

            comparisonCase
                .ApprovedDifference.Should()
                .NotBeNullOrWhiteSpace(
                    "case '{0}' differs from ODS 7.3.2 and must name an approved difference",
                    comparisonCase.Id
                );

            OdsComparisonCatalog
                .Definitions.Catalog.Select(entry => entry.Id)
                .Should()
                .Contain(
                    comparisonCase.ApprovedDifference!,
                    "case '{0}' names an approved difference that must resolve in the committed catalog",
                    comparisonCase.Id
                );
        }
        else
        {
            matchesOds
                .Should()
                .BeTrue(
                    "case '{0}' declares parity with ODS 7.3.2, so the observed outcome must be the "
                        + "recorded ODS outcome",
                    comparisonCase.Id
                );
        }
    }

    private static Dictionary<string, string> Placeholders(int hostMaximumPageSize) =>
        new(StringComparer.Ordinal)
        {
            ["{maximumPageSize}"] = hostMaximumPageSize.ToString(CultureInfo.InvariantCulture),
            ["{defaultPartitionCount}"] = HostDefaultPartitionCount.ToString(CultureInfo.InvariantCulture),
            ["{validToken}"] = PageTokenCodec.Encode(new CursorRange(1, 100)),
            ["{invalidToken}"] = "!!!",
            ["{leadingPlusDecimalToken}"] = EncodePayload("+1,100"),
            ["{whitespaceDecimalToken}"] = EncodePayload("1, 100"),
            ["{beyondInt32Token}"] = PageTokenCodec.Encode(new CursorRange(3_000_000_000L, 4_000_000_000L)),
            ["{unpaddedToken}"] = PageTokenCodec.Encode(new CursorRange(1, 100)),
            ["{paddedToken}"] = Pad(PageTokenCodec.Encode(new CursorRange(1, 100))),
            ["{forbiddenAlphabetToken}"] = "MSwx+DA",
            ["{invalidPaddingToken}"] = Pad(PageTokenCodec.Encode(new CursorRange(1, 100))) + "=",
            ["{invalidUtf8Token}"] = System.Buffers.Text.Base64Url.EncodeToString([0xFF, 0xFE, 0xFD]),
            ["{extraFieldToken}"] = EncodePayload("1,100,7"),
        };

    /// <summary>
    /// Restores the padding the encoder omits, giving the correctly padded form of the same token. The
    /// approved decoder accepts both forms, and a case asserts each explicitly.
    /// </summary>
    private static string Pad(string token) =>
        token.Length % 4 == 0 ? token : token + new string('=', 4 - (token.Length % 4));

    /// <summary>
    /// Percent-encodes every placeholder value for use inside a query-string value.
    /// </summary>
    /// <remarks>
    /// Load-bearing rather than defensive. A token carrying a standard-base64 <c>+</c> is the whole
    /// point of the forbidden-alphabet case, and an unescaped <c>+</c> in a query value decodes to a
    /// space: the request would still be rejected, but for whitespace rather than for the forbidden
    /// character, and the case would prove nothing about the alphabet. Padding characters have the same
    /// hazard. Escaping every value keeps what the application receives equal to what the case names,
    /// and <see cref="AssertQueryCarriesTheIntendedValues" /> proves it for each request.
    /// </remarks>
    private static Dictionary<string, string> ForQueryString(
        IReadOnlyDictionary<string, string> placeholders
    ) =>
        placeholders.ToDictionary(
            placeholder => placeholder.Key,
            placeholder => Uri.EscapeDataString(placeholder.Value),
            StringComparer.Ordinal
        );

    /// <summary>
    /// Proves the built query really delivers the value the case names, by parsing it the way the
    /// server does and comparing each substituted parameter against its unescaped value.
    /// </summary>
    /// <remarks>
    /// Without this, an escaping mistake turns a decoder case into evidence about something else
    /// entirely and still returns the expected status.
    /// </remarks>
    private static void AssertQueryCarriesTheIntendedValues(
        ComparisonCase comparisonCase,
        string query,
        IReadOnlyDictionary<string, string> rawPlaceholders
    )
    {
        if (string.IsNullOrEmpty(comparisonCase.Query))
        {
            return;
        }

        var parsed = QueryHelpers.ParseQuery(query);

        foreach (Match match in _substitutedParameter.Matches(comparisonCase.Query))
        {
            string parameterName = match.Groups["name"].Value;
            string placeholder = match.Groups["placeholder"].Value;

            if (!rawPlaceholders.TryGetValue(placeholder, out string? intended))
            {
                throw new InvalidOperationException(
                    $"Comparison case '{comparisonCase.Id}' names unknown placeholder '{placeholder}'."
                );
            }

            parsed
                .Should()
                .ContainKey(parameterName, "case '{0}' sends '{1}'", comparisonCase.Id, parameterName);
            parsed[parameterName]
                .ToString()
                .Should()
                .Be(
                    intended,
                    "case '{0}' must deliver '{1}' to the application exactly as written, not as the "
                        + "query-string decoder happens to reinterpret it",
                    comparisonCase.Id,
                    placeholder
                );
        }
    }

    /// <summary>Matches a query parameter whose value is a single placeholder.</summary>
    private static readonly Regex _substitutedParameter = new(
        @"[?&](?<name>[^=&]+)=(?<placeholder>\{[A-Za-z0-9]+\})",
        RegexOptions.None,
        TimeSpan.FromSeconds(1)
    );

    /// <summary>
    /// Encodes an arbitrary payload the way the codec encodes a range, so a token the decoder must
    /// reject differs from a valid one only in its payload rather than in its transport encoding.
    /// </summary>
    private static string EncodePayload(string payload) =>
        System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));

    private static async Task<ObservedOutcome> ObserveAsync(
        ApiIntegrationHarness harness,
        ComparisonCase comparisonCase,
        IReadOnlyDictionary<string, string> placeholders,
        IReadOnlyDictionary<string, string> rawPlaceholders,
        int hostMaximumPageSize
    )
    {
        string query = Substitute(comparisonCase.Query, placeholders);

        AssertQueryCarriesTheIntendedValues(comparisonCase, query, rawPlaceholders);

        return comparisonCase.Executor switch
        {
            "collection-get" => await ObserveRequestAsync(
                harness,
                $"{CursorContractSupport.ExtensionItemsEndpoint}{query}",
                comparisonCase
            ),
            "partitions-get" => await ObserveRequestAsync(
                harness,
                $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}{query}",
                comparisonCase
            ),
            "change-query-get" => await ObserveRequestAsync(
                harness,
                $"{CursorContractSupport.ExtensionItemsEndpoint}{comparisonCase.Path}{query}",
                comparisonCase
            ),
            "served-document" => await ObserveDocumentAsync(harness, comparisonCase),
            "sizing-true-ceiling" => await ObserveSizingAsync(
                harness,
                comparisonCase,
                query,
                hostMaximumPageSize
            ),
            "number-collision" => await ObserveCollisionAsync(harness, hostMaximumPageSize),
            "empty-hydration" => await ObserveEmptyHydrationAsync(harness, hostMaximumPageSize),
            "identity-maximum" => await ObserveIdentityMaximumAsync(harness),
            "profile-partitions-get" => await ObserveProfilePartitionsAsync(harness),
            "profile-document-partitions-omission" => await ObserveProfileDocumentOmissionAsync(harness),
            _ => throw new InvalidOperationException(
                $"Comparison case '{comparisonCase.Id}' names unknown executor "
                    + $"'{comparisonCase.Executor}'."
            ),
        };
    }

    private static async Task<ObservedOutcome> ObserveRequestAsync(
        ApiIntegrationHarness harness,
        string requestUri,
        ComparisonCase comparisonCase
    )
    {
        if (comparisonCase.Seed is { } seed)
        {
            await CursorContractSupport.SeedExtensionItemsAsync(
                harness,
                seed,
                labelFor: _ => "included",
                numberFor: _ => int.Parse(CollisionNumber, CultureInfo.InvariantCulture)
            );
        }

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(requestUri);
        string body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            AssertShell(response, body, comparisonCase);

            return new ObservedOutcome(
                (int)response.StatusCode,
                [.. JsonNode.Parse(body)!["errors"]!.AsArray().Select(error => error!.GetValue<string>())],
                ShellOf(body),
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            );
        }

        Dictionary<string, JsonNode?> expectations = new(StringComparer.Ordinal);

        if (response.IsSuccessStatusCode && body.StartsWith('['))
        {
            expectations["documentCount"] = JsonValue.Create(JsonNode.Parse(body)!.AsArray().Count);
            expectations["headerPresent"] = JsonValue.Create(
                response.Headers.Contains(CursorContractSupport.NextPageTokenHeaderName)
            );
        }

        return new ObservedOutcome((int)response.StatusCode, null, null, expectations);
    }

    /// <summary>
    /// Names which ProblemDetails shell a rejection body is, from the type it declares.
    /// </summary>
    /// <remarks>
    /// A body declaring neither of the DMS types is reported as unrecognized rather than as one of them.
    /// That is what lets a recorded ODS rejection whose body this suite does not reproduce still be
    /// written in terms an observation can produce: if DMS ever answered with a shell it does not own,
    /// the observation would equal the recorded outcome and the case's difference claim would fail.
    /// </remarks>
    private static string ShellOf(string body)
    {
        string? type = JsonNode.Parse(body)?["type"]?.GetValue<string>();

        return type switch
        {
            ParameterValidationProblemDetails.ProblemType => ComparisonCase.ParameterValidationShell,
            BadRequestProblemDetails.ProblemType => ComparisonCase.BadRequestShell,
            _ => OdsOutcomeComparer.UnrecognizedShell,
        };
    }

    /// <summary>
    /// Asserts the complete ProblemDetails shell a rejection answers in, including its media type. The
    /// case names which of the two shells applies, because several recorded differences turn on which
    /// one answers a request faulty in more than one way.
    /// </summary>
    private static void AssertShell(HttpResponseMessage response, string body, ComparisonCase comparisonCase)
    {
        JsonNode problem = JsonNode.Parse(body)!;

        response
            .Content.Headers.ContentType?.MediaType.Should()
            .Be("application/json", "case '{0}' answers in the DMS response media type", comparisonCase.Id);

        (string expectedType, string expectedTitle, string expectedDetail) =
            comparisonCase.Shell == ComparisonCase.BadRequestShell
                ? (
                    BadRequestProblemDetails.ProblemType,
                    BadRequestProblemDetails.ProblemTitle,
                    BadRequestProblemDetails.ProblemDetail
                )
                : (
                    ParameterValidationProblemDetails.ProblemType,
                    ParameterValidationProblemDetails.ProblemTitle,
                    ParameterValidationProblemDetails.ProblemDetail
                );

        problem["detail"]!.GetValue<string>().Should().Be(expectedDetail, "case '{0}'", comparisonCase.Id);
        problem["type"]!.GetValue<string>().Should().Be(expectedType, "case '{0}'", comparisonCase.Id);
        problem["title"]!.GetValue<string>().Should().Be(expectedTitle, "case '{0}'", comparisonCase.Id);
        problem["status"]!.GetValue<int>().Should().Be(400, "case '{0}'", comparisonCase.Id);
        problem["correlationId"]!
            .GetValue<string>()
            .Should()
            .NotBeNullOrWhiteSpace("case '{0}'", comparisonCase.Id);
        problem["validationErrors"]!.AsObject().Should().BeEmpty("case '{0}'", comparisonCase.Id);
    }

    private static async Task<ObservedOutcome> ObserveDocumentAsync(
        ApiIntegrationHarness harness,
        ComparisonCase comparisonCase
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(comparisonCase.Document!);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        JsonNode document = JsonNode.Parse(body)!;
        JsonObject values = [];

        foreach (
            var pointer in comparisonCase.Dms.Expect!["jsonValues"]!.AsObject().Select(member => member.Key)
        )
        {
            values[pointer] = ResolvePointer(document, pointer);
        }

        return new ObservedOutcome(
            (int)response.StatusCode,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["jsonValues"] = values }
        );
    }

    private static JsonNode? ResolvePointer(JsonNode document, string pointer)
    {
        JsonNode? current = document;

        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?[segment];

            if (current is null)
            {
                return null;
            }
        }

        return current.DeepClone();
    }

    private static async Task<ObservedOutcome> ObserveSizingAsync(
        ApiIntegrationHarness harness,
        ComparisonCase comparisonCase,
        string query,
        int hostMaximumPageSize
    )
    {
        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            comparisonCase.Seed
                ?? throw new InvalidOperationException(
                    $"Sizing case '{comparisonCase.Id}' must declare its seed."
                ),
            labelFor: _ => "included",
            numberFor: _ => int.Parse(CollisionNumber, CultureInfo.InvariantCulture)
        );

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}{query}"
        );

        AssertRangesTileTheIdentitySpace(pageTokens, comparisonCase);

        List<IReadOnlyList<string>> walkedPartitions = [];

        foreach (string pageToken in pageTokens)
        {
            walkedPartitions.Add(
                await CursorContractSupport.WalkFromTokenAsync(
                    harness,
                    CursorContractSupport.ExtensionItemsEndpoint,
                    pageToken,
                    hostMaximumPageSize
                )
            );
        }

        AssertPartitionsCoverTheSeed(walkedPartitions, seeded, comparisonCase);

        return new ObservedOutcome(
            200,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["tokenCount"] = JsonValue.Create(pageTokens.Count),
                ["firstPartitionDocumentCount"] = JsonValue.Create(walkedPartitions[0].Count),
                ["finalPartitionDocumentCount"] = JsonValue.Create(walkedPartitions[^1].Count),
            }
        );
    }

    /// <summary>
    /// Decodes every token a sizing case was handed and asserts the intervals they name really tile the
    /// identity space: each valid, each finite one ending exactly one below the next one's start, and the
    /// last unbounded above.
    /// </summary>
    /// <remarks>
    /// The counts the comparison reports cannot see this. A boundary bug inside an intermediate
    /// partition can leave the token count and the first and final document counts untouched while the
    /// ranges overlap or leave a gap, so these invariants are asserted directly rather than inferred.
    /// Adjacency rather than mere non-overlap is what also rules out a gap.
    /// </remarks>
    private static void AssertRangesTileTheIdentitySpace(
        IReadOnlyList<string> pageTokens,
        ComparisonCase comparisonCase
    )
    {
        List<CursorRange> ranges = [];

        foreach (string pageToken in pageTokens)
        {
            PageTokenCodec
                .TryDecode(pageToken, out CursorRange? range)
                .Should()
                .BeTrue(
                    "case '{0}': a token the partitions response handed out must decode through the codec",
                    comparisonCase.Id
                );
            ranges.Add(range!);
        }

        ranges.Should().NotBeEmpty("case '{0}' partitions a non-empty collection", comparisonCase.Id);
        ranges[0]
            .InclusiveMinimum.Should()
            .BePositive("case '{0}': the first partition starts at a real identity", comparisonCase.Id);

        for (var index = 0; index < ranges.Count; index++)
        {
            ranges[index]
                .InclusiveMaximum.Should()
                .BeGreaterThanOrEqualTo(
                    ranges[index].InclusiveMinimum,
                    "case '{0}': partition {1} must name a range that can match something",
                    comparisonCase.Id,
                    index
                );

            if (index + 1 < ranges.Count)
            {
                ranges[index]
                    .InclusiveMaximum.Should()
                    .Be(
                        ranges[index + 1].InclusiveMinimum - 1,
                        "case '{0}': partition {1} must end exactly where partition {2} begins, leaving "
                            + "neither a gap nor an overlap",
                        comparisonCase.Id,
                        index,
                        index + 1
                    );
            }
        }

        ranges[^1]
            .InclusiveMaximum.Should()
            .Be(long.MaxValue, "case '{0}': the final partition is unbounded above", comparisonCase.Id);
    }

    /// <summary>
    /// Asserts the partitions a sizing case walked hold every seeded document exactly once and nothing
    /// else, and that no two of them hold the same document.
    /// </summary>
    private static void AssertPartitionsCoverTheSeed(
        IReadOnlyList<IReadOnlyList<string>> walkedPartitions,
        IReadOnlyList<CursorContractSupport.SeededExtensionItem> seeded,
        ComparisonCase comparisonCase
    )
    {
        for (var partition = 0; partition < walkedPartitions.Count; partition++)
        {
            walkedPartitions[partition]
                .Should()
                .OnlyHaveUniqueItems(
                    "case '{0}': partition {1} must not return a document twice",
                    comparisonCase.Id,
                    partition
                );

            for (var other = partition + 1; other < walkedPartitions.Count; other++)
            {
                walkedPartitions[partition]
                    .Should()
                    .NotIntersectWith(
                        walkedPartitions[other],
                        "case '{0}': partitions {1} and {2} cover disjoint ranges",
                        comparisonCase.Id,
                        partition,
                        other
                    );
            }
        }

        walkedPartitions
            .SelectMany(static partition => partition)
            .Should()
            .BeEquivalentTo(
                seeded.Select(item => item.Id),
                "case '{0}': the partitions together cover every seeded document exactly once and "
                    + "nothing else",
                comparisonCase.Id
            );
    }

    private static async Task<ObservedOutcome> ObserveCollisionAsync(
        ApiIntegrationHarness harness,
        int hostMaximumPageSize
    )
    {
        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            25,
            labelFor: _ => "included",
            numberFor: index => 100 + index
        );

        var filtered = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsEndpoint}?number={CollisionNumber}"
        );

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}?number={CollisionNumber}"
        );

        List<string> walked = [];

        foreach (string pageToken in pageTokens)
        {
            walked.AddRange(
                await CursorContractSupport.WalkFromTokenAsync(
                    harness,
                    CursorContractSupport.ExtensionItemsEndpoint,
                    pageToken,
                    hostMaximumPageSize
                )
            );
        }

        return new ObservedOutcome(
            200,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["collectionFilteredCount"] = JsonValue.Create(filtered.DocumentIds.Count),
                ["partitionsCoverWholeCollection"] = JsonValue.Create(
                    walked.Count == seeded.Count
                        && seeded.All(item => walked.Contains(item.Id, StringComparer.Ordinal))
                ),
            }
        );
    }

    private static async Task<ObservedOutcome> ObserveEmptyHydrationAsync(
        ApiIntegrationHarness harness,
        int hostMaximumPageSize
    )
    {
        await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            8,
            labelFor: _ => "included",
            numberFor: _ => int.Parse(CollisionNumber, CultureInfo.InvariantCulture)
        );

        var page = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsEndpoint}"
                + $"?pageToken={CursorContractSupport.EntryPageToken}&pageSize={hostMaximumPageSize}"
        );

        return new ObservedOutcome(
            200,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["documentCount"] = JsonValue.Create(page.DocumentIds.Count),
                ["headerPresent"] = JsonValue.Create(page.NextPageToken is not null),
            }
        );
    }

    /// <summary>
    /// Reseeds the document identity sequence so a single created document lands on
    /// <see cref="long.MaxValue"/>, then observes what its page carries.
    /// </summary>
    /// <remarks>
    /// The database is leased per test, so the reseed cannot reach another test. Exactly one document is
    /// created afterwards, because a second would have no identity left to take.
    /// </remarks>
    private static async Task<ObservedOutcome> ObserveIdentityMaximumAsync(ApiIntegrationHarness harness)
    {
        await ReseedDocumentIdentityAsync(harness);

        await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            1,
            labelFor: _ => "included",
            numberFor: _ => int.Parse(CollisionNumber, CultureInfo.InvariantCulture)
        );

        long maximumDocumentId = await ReadMaximumDocumentIdAsync(harness);

        maximumDocumentId
            .Should()
            .Be(
                long.MaxValue,
                "the reseed must put the created document on the maximum identity, or the missing header "
                    + "below would be evidence of nothing"
            );

        var page = await CursorContractSupport.ReadPageAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint
        );

        return new ObservedOutcome(
            200,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["documentCount"] = JsonValue.Create(page.DocumentIds.Count),
                ["headerPresent"] = JsonValue.Create(page.NextPageToken is not null),
            }
        );
    }

    private static async Task ReseedDocumentIdentityAsync(ApiIntegrationHarness harness)
    {
        await using var command = harness.DbConnection.CreateCommand();

        // SQL Server's reseed is row-count dependent: on a table that already holds rows the next
        // identity is the reseed value plus the increment, while on an empty one it is the reseed value
        // itself. Choosing the value from the row count is what makes a single created document land on
        // the maximum either way; PostgreSQL's restart is unconditional.
        command.CommandText = IsPostgresql(harness)
            ? """ALTER TABLE dms."Document" ALTER COLUMN "DocumentId" RESTART WITH 9223372036854775807"""
            : """
                IF EXISTS (SELECT 1 FROM [dms].[Document])
                    DBCC CHECKIDENT ('dms.Document', RESEED, 9223372036854775806);
                ELSE
                    DBCC CHECKIDENT ('dms.Document', RESEED, 9223372036854775807);
                """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads the highest document identity the leased database holds, so the identity-maximum case can
    /// prove it really reached the maximum rather than merely observing a missing header.
    /// </summary>
    private static async Task<long> ReadMaximumDocumentIdAsync(ApiIntegrationHarness harness)
    {
        await using var command = harness.DbConnection.CreateCommand();
        command.CommandText = IsPostgresql(harness)
            ? "SELECT MAX(\"DocumentId\") FROM dms.\"Document\""
            : "SELECT MAX([DocumentId]) FROM [dms].[Document]";

        object? maximum = await command.ExecuteScalarAsync();

        return maximum is long value ? value : 0L;
    }

    private static bool IsPostgresql(ApiIntegrationHarness harness) =>
        harness.DbConnection.GetType().Name.Contains("Npgsql", StringComparison.Ordinal);

    private static async Task<ObservedOutcome> ObserveProfilePartitionsAsync(ApiIntegrationHarness harness)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileMergeItemsPartitionsEndpoint);
        request.Headers.TryAddWithoutValidation("Accept", WriteOnlyReadableContentType);

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);

        return new ObservedOutcome(
            (int)response.StatusCode,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// Observes which paths the core document and a write-only profile document publish for one
    /// resource.
    /// </summary>
    /// <remarks>
    /// Every observation but the last is reported so the comparison cannot pass vacuously. A profile
    /// document with no paths at all, or one that lost the collection path along with the partition
    /// path, would satisfy "the partitions path is absent" while proving nothing; requiring the core
    /// document to publish both paths, and the profile document to publish the collection path with a
    /// write operation and no read operation, is what makes the missing sibling the only thing under
    /// test.
    /// </remarks>
    private static async Task<ObservedOutcome> ObserveProfileDocumentOmissionAsync(
        ApiIntegrationHarness harness
    )
    {
        JsonObject corePaths = await ReadDocumentPathsAsync(harness, CoreResourcesDocument);
        JsonObject profilePaths = await ReadDocumentPathsAsync(harness, ProfileResourcesDocument);

        JsonNode? profileCollection = profilePaths[ProfiledCollectionPath];

        return new ObservedOutcome(
            200,
            null,
            null,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["coreDocumentHasPaths"] = JsonValue.Create(corePaths.Count > 0),
                ["coreCollectionPathPresent"] = JsonValue.Create(
                    corePaths.ContainsKey(ProfiledCollectionPath)
                ),
                ["corePartitionsPathPresent"] = JsonValue.Create(
                    corePaths.ContainsKey(ProfiledPartitionsPath)
                ),
                ["profileDocumentHasPaths"] = JsonValue.Create(profilePaths.Count > 0),
                ["profileCollectionPathPresent"] = JsonValue.Create(profileCollection is not null),
                ["profileCollectionHasWriteOperation"] = JsonValue.Create(
                    profileCollection?["post"] is not null
                ),
                ["profileCollectionHasReadOperation"] = JsonValue.Create(
                    profileCollection?["get"] is not null
                ),
                ["profilePartitionsPathPresent"] = JsonValue.Create(
                    profilePaths.ContainsKey(ProfiledPartitionsPath)
                ),
            }
        );
    }

    private static async Task<JsonObject> ReadDocumentPathsAsync(
        ApiIntegrationHarness harness,
        string document
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(document);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!["paths"]?.AsObject() ?? [];
    }

    private static void AssertMatches(
        ObservedOutcome observation,
        ExpectedOutcome expected,
        ComparisonCase comparisonCase,
        string side
    )
    {
        observation.Status.Should().Be(expected.Status, "{0} status for case '{1}'", side, comparisonCase.Id);

        if (expected.Errors is not null)
        {
            observation
                .Errors.Should()
                .NotBeNull("case '{0}' expects a rejection body", comparisonCase.Id)
                .And.Equal(expected.Errors, "{0} errors for case '{1}'", side, comparisonCase.Id);
        }

        if (expected.Expect is null)
        {
            return;
        }

        foreach (var member in expected.Expect)
        {
            observation
                .Expectations.Should()
                .ContainKey(member.Key, "case '{0}' observes '{1}'", comparisonCase.Id, member.Key);

            JsonNode
                .DeepEquals(observation.Expectations[member.Key], member.Value)
                .Should()
                .BeTrue(
                    "{0} expectation '{1}' for case '{2}': observed {3}, expected {4}",
                    side,
                    member.Key,
                    comparisonCase.Id,
                    observation.Expectations[member.Key]?.ToJsonString() ?? "null",
                    member.Value?.ToJsonString() ?? "null"
                );
        }
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        string resolved = text;

        foreach (var placeholder in placeholders)
        {
            resolved = resolved.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal);
        }

        return resolved;
    }
}
