// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The approved header-gating contract, observed as a client receives it: a page whose selected rows
/// are gone by the time the body is built returns HTTP 200 with an empty array <em>and</em> a
/// continuation that advances past the keys it selected.
///
/// <para>
/// This is the difference from ODS 7.3.2 that the epic records. ODS gates its continuation on the
/// hydrated body count, so an empty body ends its walk; DMS gates on a non-null selected maximum, so a
/// client that treats the header rather than the body as the signal to continue keeps walking and does
/// not silently skip the rest of the collection.
/// </para>
///
/// <para>
/// <strong>What this proves and what it does not.</strong> Page selection and body projection are
/// statements inside one command batch, so no test process can land a concurrent delete between them,
/// and forcing that interleaving would require a production seam. The rows here are therefore dropped
/// at the <c>IDocumentHydrator</c> boundary by a test-only decorator, after the real provider SQL has
/// run and produced a real non-null selected maximum. Everything downstream of hydration is real: the
/// Core handler, the header rule, response assembly, and the HTTP response asserted below. What is
/// simulated is only the disappearance of the rows, not the header rule that answers it. There is no
/// race and no sleep; the decorator is deterministic and one-shot.
/// </para>
/// </summary>
internal static class CursorEmptyHydrationScenario
{
    /// <summary>
    /// Enough documents that several pages remain after the suppressed one, so the continuation has
    /// something to return and the walk it starts is a real walk rather than one page.
    /// </summary>
    private const int SeededDocumentCount = 8;

    /// <summary>
    /// The page size every request below uses. Two keeps the suppressed page small, so the documents it
    /// selected are a known prefix of the seed and the continuation's contents can be asserted exactly.
    /// </summary>
    private const int PageSize = 2;

    /// <summary>
    /// A page whose rows all vanished before the body was built still advances the walk, and the walk it
    /// hands off to returns the documents that come after the ones it selected — not those documents
    /// again.
    /// </summary>
    public static async Task It_advances_past_a_page_whose_rows_vanished_before_hydration(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await CursorContractSupport.SeedMergeItemsAsync(
            harness,
            "empty-hydration",
            SeededDocumentCount
        );

        var suppressedPage = await CursorContractSupport.ReadPageAsync(
            harness,
            $"{CursorContractSupport.MergeItemsEndpoint}?pageToken={CursorContractSupport.EntryPageToken}"
                + $"&pageSize={PageSize}"
        );

        suppressedPage
            .DocumentIds.Should()
            .BeEmpty("every row this page selected was gone by the time its body was built");
        suppressedPage
            .NextPageToken.Should()
            .NotBeNull(
                "the continuation is gated on the selected maximum, which page selection really produced, "
                    + "rather than on the body, which is empty"
            );
        suppressedPage
            .TotalCount.Should()
            .BeNull("an empty body does not acquire a count header the request never asked for");

        // ReadPageAsync already round-tripped the continuation through the codec that produced it, so
        // reaching here means the token is decodable and resumes above zero.
        var continuedIds = await CursorContractSupport.WalkFromTokenAsync(
            harness,
            CursorContractSupport.MergeItemsEndpoint,
            suppressedPage.NextPageToken!,
            PageSize
        );

        // The suppressed page selected the lowest identities, which are the documents seeded first.
        string[] selectedByTheSuppressedPage = [.. seededIds.Take(PageSize)];
        string[] expectedAfterIt = [.. seededIds.Skip(PageSize)];

        continuedIds
            .Should()
            .NotIntersectWith(
                selectedByTheSuppressedPage,
                "the continuation resumes after the keys the suppressed page selected rather than "
                    + "replaying them"
            );
        continuedIds
            .Should()
            .BeEquivalentTo(
                expectedAfterIt,
                "suppression was one-shot, so the walk the continuation starts returns real documents "
                    + "and reaches the end of the collection"
            );
    }
}
