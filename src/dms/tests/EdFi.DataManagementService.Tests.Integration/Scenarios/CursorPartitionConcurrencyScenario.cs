// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// What a walk actually observes when the collection changes underneath it. Cursor paging is not a
/// snapshot protocol, and these tests document the behavior the design promises rather than promising
/// something stronger.
///
/// <para>
/// Nothing here asserts snapshot consistency, and nothing here depends on timing. Each test performs
/// its writes between two ordinary HTTP requests, so the interleaving is established by the order of
/// the requests alone: there is no sleep, no background task, and no race to lose.
/// </para>
///
/// <para>
/// The three properties under test are the ones a client can rely on. A partition issued before a write
/// keeps the bounds it was issued with, so a later insert cannot move into a partition the client has
/// already finished. A document that disappears before its page is reached simply does not arrive, and
/// the walk still terminates. And filters are reapplied on every request rather than frozen into the
/// token, so a document whose eligibility changes mid-walk is answered against the data as it is now.
/// </para>
/// </summary>
internal static class CursorPartitionConcurrencyScenario
{
    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with, for the
    /// same reason the walk coverage needs it: at the deployed value the seeds below would each be a
    /// single partition and the boundary claims would hold vacuously.
    /// </summary>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// Enough documents that a requested count of three really produces three partitions at a minimum
    /// partition size of ten, so there is a finite partition to protect and a final unbounded one for a
    /// later insert to land in.
    /// </summary>
    private const int SeededDocumentCount = 25;

    /// <summary>
    /// A seed with alternating eligibility. Twelve of the twenty-four match, which still exceeds the
    /// minimum partition size, and the matching and non-matching documents are interleaved by identity
    /// so a non-matching one really sits inside a partition the walk has not reached yet.
    /// </summary>
    private const int InterleavedSeedCount = 24;

    private const int RequestedPartitionCount = 3;

    private const int InsertedAfterBoundariesCount = 3;

    private const string MatchingLabel = "included";
    private const string OtherLabel = "excluded";
    private const int SharedNumber = 105;

    /// <summary>
    /// A document created after the boundaries were handed out cannot move into a partition the client
    /// has already finished: every partition except the last is bounded above.
    /// </summary>
    /// <remarks>
    /// The proof is a before-and-after comparison using the <em>same</em> tokens. Each partition is
    /// walked once, three documents are then created, and each partition is walked again. The finite
    /// partitions must return exactly what they returned the first time; only the final unbounded
    /// partition may grow, and it must grow by exactly the new documents.
    /// </remarks>
    public static async Task It_admits_a_later_insert_only_to_the_final_unbounded_partition(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            SeededDocumentCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedNumber
        );

        var pageTokens = await ReadPartitionTokensAsync(harness);

        var before = await WalkEachPartitionAsync(harness, pageTokens, querySuffix: string.Empty);

        var inserted = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            InsertedAfterBoundariesCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedNumber
        );

        var after = await WalkEachPartitionAsync(harness, pageTokens, querySuffix: string.Empty);

        for (var partition = 0; partition < pageTokens.Count - 1; partition++)
        {
            after[partition]
                .Should()
                .BeEquivalentTo(
                    before[partition],
                    "partition {0} is bounded above, so a document created after its token was issued "
                        + "cannot appear in it",
                    partition
                );
        }

        string[] insertedIds = [.. inserted.Select(item => item.Id)];

        after[^1]
            .Should()
            .BeEquivalentTo(
                before[^1].Concat(insertedIds),
                "the final partition is unbounded above, so it is where a later insert can appear"
            );
        after
            .SelectMany(static partition => partition)
            .Should()
            .BeEquivalentTo(
                seeded.Select(item => item.Id).Concat(insertedIds),
                "the partitions still cover the whole collection, now including the later documents"
            );
    }

    /// <summary>
    /// A document deleted before its page is reached simply does not arrive. The walk terminates
    /// normally and replays nothing it already returned.
    /// </summary>
    /// <remarks>
    /// The deleted document is the one seeded last, so it carries the highest identity and belongs to
    /// the final partition — which the walk has deliberately not reached when the delete happens. That
    /// placement is what makes this a not-yet-paged deletion rather than a deletion of something already
    /// returned.
    /// </remarks>
    public static async Task It_drops_a_member_deleted_before_its_page_was_reached(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            SeededDocumentCount,
            labelFor: _ => MatchingLabel,
            numberFor: _ => SharedNumber
        );

        var pageTokens = await ReadPartitionTokensAsync(harness);

        var firstPartition = await CursorContractSupport.WalkFromTokenAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            pageTokens[0],
            HostMaximumPageSize
        );

        var doomed = seeded[^1];

        firstPartition
            .Should()
            .NotContain(
                doomed.Id,
                "the document about to be deleted must belong to a partition the walk has not reached"
            );

        await CursorContractSupport.DeleteAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            doomed.Id
        );

        List<string> remaining = [.. firstPartition];

        for (var partition = 1; partition < pageTokens.Count; partition++)
        {
            var walked = await CursorContractSupport.WalkFromTokenAsync(
                harness,
                CursorContractSupport.ExtensionItemsEndpoint,
                pageTokens[partition],
                HostMaximumPageSize
            );

            walked
                .Should()
                .NotIntersectWith(
                    remaining,
                    "resuming after a delete must not replay a document an earlier partition returned"
                );

            remaining.AddRange(walked);
        }

        remaining
            .Should()
            .BeEquivalentTo(
                seeded.Take(seeded.Count - 1).Select(item => item.Id),
                "the deleted document is simply absent; every other member still arrives exactly once"
            );
    }

    /// <summary>
    /// Filters are reapplied on every request rather than frozen into the token, so a document whose
    /// eligibility changes mid-walk is answered against the data as it is now — in both directions.
    /// </summary>
    /// <remarks>
    /// Two documents inside the partition the walk has not yet reached are flipped: one that matched the
    /// filter stops matching, and one that did not match starts matching. The seed alternates the two
    /// labels, so the newly eligible document sits <em>between</em> documents the filter already
    /// selected; a resumed request that ignored the filter and returned everything in its range would
    /// admit the other non-matching documents too, and a resumed request that had frozen the original
    /// filter result would return the document that no longer qualifies. Only reapplying the supplied
    /// filter over current data produces the set asserted below.
    /// </remarks>
    public static async Task It_reevaluates_a_filter_for_a_document_whose_eligibility_changed(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seeded = await CursorContractSupport.SeedExtensionItemsAsync(
            harness,
            InterleavedSeedCount,
            labelFor: index => index % 2 == 0 ? MatchingLabel : OtherLabel,
            numberFor: _ => SharedNumber
        );

        string filter = $"&label={MatchingLabel}";

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}"
                + $"?number={RequestedPartitionCount.ToString(CultureInfo.InvariantCulture)}{filter}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the matching half exceeds the minimum partition size, so there is a later partition to "
                    + "resume into"
            );

        var firstPartition = await CursorContractSupport.WalkFromTokenAsync(
            harness,
            CursorContractSupport.ExtensionItemsEndpoint,
            pageTokens[0],
            HostMaximumPageSize,
            filter
        );

        // The last matching document and the last non-matching one both carry identities above every
        // document the first partition returned, so both sit in a partition the walk has not reached.
        var becomesIneligible = seeded[^2];
        var becomesEligible = seeded[^1];

        becomesIneligible.Label.Should().Be(MatchingLabel);
        becomesEligible.Label.Should().Be(OtherLabel);
        firstPartition
            .Should()
            .NotContain(
                new[] { becomesIneligible.Id, becomesEligible.Id },
                "both documents about to change must belong to a partition the walk has not reached"
            );

        await CursorContractSupport.UpdateExtensionItemLabelAsync(harness, becomesIneligible, OtherLabel);
        await CursorContractSupport.UpdateExtensionItemLabelAsync(harness, becomesEligible, MatchingLabel);

        List<string> resumed = [];

        for (var partition = 1; partition < pageTokens.Count; partition++)
        {
            resumed.AddRange(
                await CursorContractSupport.WalkFromTokenAsync(
                    harness,
                    CursorContractSupport.ExtensionItemsEndpoint,
                    pageTokens[partition],
                    HostMaximumPageSize,
                    filter
                )
            );
        }

        resumed
            .Should()
            .Contain(
                becomesEligible.Id,
                "a document that became eligible mid-walk is returned by the request that reaches it"
            );
        resumed
            .Should()
            .NotContain(
                becomesIneligible.Id,
                "a document that stopped being eligible mid-walk is not returned, even though it "
                    + "matched when the boundaries were calculated"
            );

        string[] expected =
        [
            .. seeded
                .Where(item => item.Label == MatchingLabel && item.Id != becomesIneligible.Id)
                .Select(item => item.Id),
            becomesEligible.Id,
        ];

        firstPartition
            .Concat(resumed)
            .Should()
            .BeEquivalentTo(
                expected,
                "the walk returns exactly the documents the supplied filter selects from the data as it "
                    + "stands when each request is answered"
            );
    }

    private static async Task<IReadOnlyList<string>> ReadPartitionTokensAsync(ApiIntegrationHarness harness)
    {
        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.ExtensionItemsPartitionsEndpoint}"
                + $"?number={RequestedPartitionCount.ToString(CultureInfo.InvariantCulture)}"
        );

        pageTokens
            .Should()
            .HaveCountGreaterThan(
                1,
                "the seed exceeds the minimum partition size, so there is a bounded partition to protect "
                    + "as well as the final unbounded one"
            );

        // The bounded/unbounded distinction the assertions rest on is read out of the tokens rather than
        // assumed: every partition but the last names a finite upper bound, and the last does not.
        for (var index = 0; index < pageTokens.Count; index++)
        {
            // The anchor is discarded: what these assertions rest on is which partitions are bounded
            // above, and that is a property of the range whichever column the range is expressed in.
            PageTokenCodec
                .TryDecode(pageTokens[index], out CursorRange? range, out _)
                .Should()
                .BeTrue("a token the partitions response handed out must decode through the codec");

            if (index + 1 < pageTokens.Count)
            {
                range!
                    .InclusiveMaximum.Should()
                    .BeLessThan(long.MaxValue, "partition {0} is bounded above", index);
            }
            else
            {
                range!.InclusiveMaximum.Should().Be(long.MaxValue, "the final partition is unbounded above");
            }
        }

        return pageTokens;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<string>>> WalkEachPartitionAsync(
        ApiIntegrationHarness harness,
        IReadOnlyList<string> pageTokens,
        string querySuffix
    )
    {
        List<IReadOnlyList<string>> walked = [];

        foreach (string pageToken in pageTokens)
        {
            walked.Add(
                await CursorContractSupport.WalkFromTokenAsync(
                    harness,
                    CursorContractSupport.ExtensionItemsEndpoint,
                    pageToken,
                    HostMaximumPageSize,
                    querySuffix
                )
            );
        }

        return walked;
    }
}
