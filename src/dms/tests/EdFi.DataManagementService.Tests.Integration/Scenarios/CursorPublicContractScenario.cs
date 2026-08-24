// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The public cursor contract as a client observes it over HTTP: the shell a rejected cursor request
/// answers with, how many messages it carries, which supplied value the validator reasoned about, and
/// the pages and continuation headers a successful request produces.
///
/// <para>
/// Validator tests already pin every precedence rule, but they read a parameter dictionary and return
/// a result object. They cannot show the status code, the media type, the ProblemDetails fields, or
/// that a rejected request produced no page at all. Handler tests already pin the continuation-header
/// rules, but they construct a <c>QuerySuccess</c> rather than selecting one. These tests observe both
/// through the assembled host, which is the only place the frontend's parameter canonicalization, Core
/// validation, page selection, and response assembly are the ones a client meets.
/// </para>
///
/// <para>
/// The exhaustive sweep of the approved precedence table is not repeated here: it belongs to the
/// ODS-comparison cases, which execute every row and additionally record how ODS 7.3.2 answers it.
/// What is asserted here is the shell those rows share, the one-error cardinality a cursor fault
/// promises, and the behaviors a comparison case has no ODS column for.
/// </para>
/// </summary>
internal static class CursorPublicContractScenario
{
    /// <summary>
    /// Enough documents that a page size of two cannot deliver them all, so a walk must genuinely
    /// continue and an offset page has something after it to continue into.
    /// </summary>
    private const int SeededDocumentCount = 5;

    /// <summary>
    /// A walk that failed to advance would exhaust this and fail with the pages it did retrieve rather
    /// than hanging.
    /// </summary>
    private const int MaximumWalkedPages = 25;

    private const string PageTokenRequired = "PageToken is required when pageSize is specified.";

    private const string LimitWithPageToken =
        "Use pageSize instead of limit when using cursor paging with pageToken.";

    /// <summary>
    /// A rejected cursor request answers in the parameter-validation shell with the current DMS
    /// response media type, and produces no page.
    /// </summary>
    public static async Task It_answers_a_cursor_parameter_fault_with_the_parameter_validation_shell(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsEndpoint}?pageSize=5&limit=10"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, PageTokenRequired);
    }

    /// <summary>
    /// A cursor request carrying several faults is answered with exactly one message, chosen by phase
    /// rather than by parameter position.
    /// </summary>
    /// <remarks>
    /// This request is faulty three ways at once: <c>limit</c> and <c>totalCount=true</c> are both
    /// mixed-mode conflicts with a valid token, and <c>pageSize</c> is out of range. The mixed-mode
    /// phase answers, and within it the <c>limit</c> rule precedes the <c>totalCount</c> rule, so the
    /// one message returned is the <c>limit</c> conflict. Any other single message, and any second
    /// message, would fail here.
    /// </remarks>
    public static async Task It_reports_one_error_from_the_first_failing_phase(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={CursorContractSupport.EntryPageToken}"
                + "&limit=10&pageSize=99999&totalCount=true"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, LimitWithPageToken);
    }

    /// <summary>
    /// A case-folded <c>limit</c> alongside a valid token is a paging-mode conflict, not an unknown
    /// query field.
    /// </summary>
    /// <remarks>
    /// <c>limit</c> has been case-insensitive at the HTTP boundary since long before cursor paging, so
    /// <c>LIMIT</c> reaches Core as <c>limit</c> and the request really does carry both paging modes.
    /// Answering it as an invalid query field would be the visible symptom of that fold being lost,
    /// which is why the conflict message rather than any 400 is what is asserted.
    /// </remarks>
    public static async Task It_reports_a_mixed_mode_conflict_for_a_case_folded_limit(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={CursorContractSupport.EntryPageToken}&LIMIT=10"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, LimitWithPageToken);
    }

    /// <summary>
    /// A parameter supplied twice under the same spelling keeps its last value.
    /// </summary>
    /// <remarks>
    /// The first value is out of range and the last is valid, so a first-value win would answer with a
    /// range error instead of a page. Asserting the page size the page actually used is therefore a
    /// direct observation of which value the validator parsed. Case-variant spellings are covered
    /// separately; this is the repeated-exact-name half of the same contract.
    /// </remarks>
    public static async Task It_keeps_only_the_last_value_of_a_repeated_page_size(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await CursorContractSupport.SeedMergeItemsAsync(harness, "repeated-page-size", SeededDocumentCount);

        var page = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={CursorContractSupport.EntryPageToken}"
                + "&pageSize=99999&pageSize=2"
        );

        page.DocumentIds.Should()
            .HaveCount(2, "the last supplied page size is the one the page was selected with");
    }

    /// <summary>
    /// A zero-size page succeeds, returns nothing, and cannot advance a walk.
    /// </summary>
    /// <remarks>
    /// It is a valid request rather than a fault, and its selected keyset is empty, so there is no
    /// selected maximum to anchor a continuation on. A client that treated the empty body as the end of
    /// a walk and a client that looked for the header would agree here, which is the point: the page
    /// terminates rather than silently restarting.
    /// </remarks>
    public static async Task It_returns_an_empty_page_without_a_continuation_for_a_zero_size_page(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await CursorContractSupport.SeedMergeItemsAsync(harness, "zero-size", SeededDocumentCount);

        var page = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={CursorContractSupport.EntryPageToken}&pageSize=0"
        );

        page.DocumentIds.Should().BeEmpty("a zero-size page selects no keys");
        page.NextPageToken.Should()
            .BeNull("a page with no selected maximum has nothing to anchor a continuation on");
    }

    /// <summary>
    /// A walk ends with exactly one empty request, and every seeded document arrives exactly once
    /// before it.
    /// </summary>
    /// <remarks>
    /// The implementation does not fetch an extra row to predict the terminal page, so the last useful
    /// page is followed by one request that selects nothing and offers no continuation. Asserting
    /// <em>one</em> such page rather than merely that the walk stopped is what would catch a walk that
    /// kept handing out continuations over an exhausted range. Each test leases its own database, so
    /// the union is asserted as equality against the seed rather than as containment.
    /// </remarks>
    public static async Task It_ends_a_walk_with_one_trailing_empty_page(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await CursorContractSupport.SeedMergeItemsAsync(
            harness,
            "terminal-page",
            SeededDocumentCount
        );

        List<string> returnedIds = [];
        var emptyPages = 0;
        string? pageToken = CursorContractSupport.EntryPageToken;

        for (var page = 0; page < MaximumWalkedPages; page++)
        {
            var pageResponse = await CursorContractSupport.ReadPageAsync(
                harness,
                $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={Uri.EscapeDataString(pageToken!)}&pageSize=2"
            );

            if (pageResponse.DocumentIds.Count == 0)
            {
                emptyPages++;
            }

            returnedIds.AddRange(pageResponse.DocumentIds);

            if (pageResponse.NextPageToken is null)
            {
                pageResponse
                    .DocumentIds.Should()
                    .BeEmpty("the request that ends a walk is the one that selected nothing");
                emptyPages
                    .Should()
                    .Be(
                        1,
                        "only the terminal request selects nothing; the pages before it all returned rows"
                    );
                returnedIds
                    .Should()
                    .BeEquivalentTo(seededIds, "a walk returns every seeded document exactly once");
                return;
            }

            pageToken = pageResponse.NextPageToken;
        }

        throw new InvalidOperationException(
            $"A cursor walk did not terminate within {MaximumWalkedPages} pages."
        );
    }

    /// <summary>
    /// A traditional page keeps its body, status, and <c>Total-Count</c>, and gains a continuation that
    /// resumes after the offset page rather than at the beginning of the collection.
    /// </summary>
    /// <remarks>
    /// The offset page is compared against the unoffset page rather than against a hardcoded ordering:
    /// asserting that offset one begins where the unoffset page's second document did states the
    /// preservation claim without assuming an ordering the traditional contract does not spell out.
    /// <c>Total-Count</c> is asserted both present with the seeded count when it was requested and
    /// absent when it was not, so the header remains driven by the request rather than by the page.
    /// </remarks>
    public static async Task It_preserves_the_traditional_page_contract_and_adds_a_continuation(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await CursorContractSupport.SeedMergeItemsAsync(
            harness,
            "traditional-preservation",
            SeededDocumentCount
        );

        var unoffsetPage = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?limit=2"
        );

        unoffsetPage.DocumentIds.Should().HaveCount(2);
        unoffsetPage
            .TotalCount.Should()
            .BeNull("a page that did not ask for a count is not given the count header");

        var offsetPage = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?limit=2&offset=1&totalCount=true"
        );

        offsetPage.DocumentIds.Should().HaveCount(2);
        offsetPage
            .TotalCount.Should()
            .Be(
                SeededDocumentCount.ToString(CultureInfo.InvariantCulture),
                "the count covers the whole collection rather than the page"
            );
        offsetPage
            .DocumentIds[0]
            .Should()
            .Be(unoffsetPage.DocumentIds[1], "an offset of one skips exactly one document");
        offsetPage
            .NextPageToken.Should()
            .NotBeNull("a traditional page ordered by document identity can begin a cursor walk");

        var continued = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={Uri.EscapeDataString(offsetPage.NextPageToken!)}&pageSize=2"
        );

        continued
            .DocumentIds.Should()
            .NotBeEmpty("the continuation from an offset page resumes after it, not at the collection start");
        continued
            .DocumentIds.Should()
            .NotIntersectWith(
                offsetPage.DocumentIds,
                "a continuation begins after the keys the page it came from selected"
            );
        seededIds.Should().Contain(continued.DocumentIds);
    }
}
