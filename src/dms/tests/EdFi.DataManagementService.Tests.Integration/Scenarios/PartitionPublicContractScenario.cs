// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The public partitions contract as a client observes it over HTTP: which of the two ProblemDetails
/// shells answers a query string that is faulty in more than one way, how many messages that answer
/// carries and in what order, and how many tokens a requested count actually produces.
///
/// <para>
/// Partition validation is phase-gated differently from cursor validation, and the difference is
/// observable rather than internal: the change-version window is validated ahead of filters, filters
/// ahead of the two partition phases, and the last phase may report several messages at once. Validator
/// tests pin each phase in isolation, but only a served response shows which phase answered a request
/// that faults in two phases at once — and that is exactly what the four recorded ordering consequences
/// are about.
/// </para>
///
/// <para>
/// The sizing tests are the other half. A requested count is an upper bound rather than a promise,
/// because every partition is at least five maximum-sized pages, and the boundaries are selected by
/// real provider SQL. They are therefore asserted on both engines, while the validation rows above are
/// answered before any provider is involved and are asserted once.
/// </para>
///
/// <para>
/// Count canonicalization sits across that split. Recognizing the count under either letter case and
/// collapsing repeats to the last value happens at the HTTP boundary, before any provider, but the
/// surviving value is only observable in the boundaries a provider then computes. That row is therefore
/// asserted once, like the validation rows, even though it reaches provider SQL like the sizing rows.
/// </para>
/// </summary>
internal static class PartitionPublicContractScenario
{
    /// <summary>
    /// The maximum page size the fixtures binding this scenario must configure their host with.
    /// </summary>
    /// <remarks>
    /// The mandatory minimum partition size is <c>MaximumPageSize * 5</c>, so at the deployed value of
    /// 500 no collection this scenario could seed over HTTP would be cut into more than one partition
    /// and every sizing assertion below would pass vacuously. A page size of two puts the minimum at
    /// ten rows, which the seed clears. The wrappers read this constant rather than restating it, so
    /// the sizing this scenario reasons about cannot drift from the host it runs against.
    /// </remarks>
    internal const int HostMaximumPageSize = 2;

    /// <summary>
    /// Enough documents for the minimum partition size to be exceeded at a requested count of three,
    /// so that count really produces three partitions rather than collapsing into one.
    /// </summary>
    private const int SeededDocumentCount = 25;

    /// <summary>
    /// The partition count a seed of <see cref="SeededDocumentCount"/> documents yields once the
    /// minimum partition size binds: a size of ten over twenty-five candidates starts partitions at
    /// candidate rows one, eleven, and twenty-one.
    /// </summary>
    private const int PartitionsWhenMinimumSizeBinds = 3;

    private const string NumberOutOfRange = "Number of partitions must be between 1 and 200.";

    private const string UnknownFieldName = "notAField";

    private const string MalformedChangeVersion =
        "MinChangeVersion must be a numeric value greater than or equal to 0.";

    /// <summary>
    /// A rejected partitions request answers in the parameter-validation shell, the same shell a
    /// rejected page request uses.
    /// </summary>
    public static async Task It_answers_a_partition_parameter_fault_with_the_parameter_validation_shell(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=abc"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, NumberOutOfRange);
    }

    /// <summary>
    /// A present-but-blank count is a malformed count rather than an absent one.
    /// </summary>
    /// <remarks>
    /// A client that typed <c>number=</c> asked for a partition count, so the parameter it typed is
    /// answered rather than silently ignored and defaulted. The distinction is invisible unless the
    /// request is served: defaulting would have produced HTTP 200 with a token array.
    /// </remarks>
    public static async Task It_treats_a_blank_partition_count_as_malformed(ApiIntegrationHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number="
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, NumberOutOfRange);
    }

    /// <summary>
    /// A count above the supported maximum is refused with the same single message as one below the
    /// minimum.
    /// </summary>
    public static async Task It_refuses_a_partition_count_above_the_supported_maximum(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=201"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, NumberOutOfRange);
    }

    /// <summary>
    /// Every reserved paging parameter present is reported, in the canonical order, without its value
    /// being parsed.
    /// </summary>
    /// <remarks>
    /// Two claims in one request. The five parameters are supplied in the reverse of the order they are
    /// reported in, so the response order is the contract's rather than the query string's. And every
    /// supplied value is malformed: a parsed <c>pageToken</c> would have answered that the token was
    /// invalid, and a parsed <c>offset</c> or <c>totalCount</c> would have answered with a range or
    /// boolean message. Getting five unsupported-parameter messages instead shows the complaint is that
    /// the parameter does not apply here at all.
    /// </remarks>
    public static async Task It_reports_every_reserved_parameter_in_canonical_order(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}"
                + "?totalCount=perhaps&offset=nonsense&limit=nonsense&pageSize=nonsense&pageToken=!!!"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(
            response,
            "The 'pageToken' parameter is not supported by the partitions endpoint.",
            "The 'pageSize' parameter is not supported by the partitions endpoint.",
            "The 'limit' parameter is not supported by the partitions endpoint.",
            "The 'offset' parameter is not supported by the partitions endpoint.",
            "The 'totalCount' parameter is not supported by the partitions endpoint."
        );
    }

    /// <summary>
    /// A malformed count alongside an unknown query field is answered with the unknown-field message
    /// alone, in the bad-request shell.
    /// </summary>
    /// <remarks>
    /// Both are client mistakes. Answering the field first is what keeps this operation's unknown-field
    /// behavior identical to the collection GET's, so a client that discriminates on the problem type
    /// does not have to know which of the two sibling endpoints it called.
    /// </remarks>
    public static async Task It_answers_a_malformed_count_and_an_unknown_field_with_the_unknown_field_alone(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=abc&{UnknownFieldName}=1"
        );

        await BadRequestProblemDetails.AssertShellAsync(
            response,
            BadRequestProblemDetails.UnknownQueryField(UnknownFieldName)
        );
    }

    /// <summary>
    /// A malformed count alongside a malformed change-version window is answered with the window
    /// message alone, because the window is validated first.
    /// </summary>
    public static async Task It_answers_a_malformed_count_and_a_malformed_window_with_the_window_alone(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=abc&minChangeVersion=bogus"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, MalformedChangeVersion);
    }

    /// <summary>
    /// An unknown query field alongside a reserved paging parameter is answered with the unknown-field
    /// message alone.
    /// </summary>
    /// <remarks>
    /// Filters are validated ahead of the reserved-parameter phase, and the reserved names are excluded
    /// from filter matching before that happens. Excluding them is what lets <c>?limit=5</c> be reported
    /// as a parameter that does not apply here rather than as an unknown query field — and this request
    /// shows the exclusion does not also promote the reserved parameter ahead of a real unknown field.
    /// </remarks>
    public static async Task It_answers_an_unknown_field_and_a_reserved_parameter_with_the_unknown_field_alone(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?{UnknownFieldName}=1&limit=5"
        );

        await BadRequestProblemDetails.AssertShellAsync(
            response,
            BadRequestProblemDetails.UnknownQueryField(UnknownFieldName)
        );
    }

    /// <summary>
    /// A malformed change-version window alongside an unknown query field is answered with the window
    /// message alone, in the parameter-validation shell rather than the bad-request one.
    /// </summary>
    /// <remarks>
    /// This is the consequence that pins the shell as well as the message: the window is validated
    /// before filters, so the answer is the parameter-validation problem type even though an unknown
    /// field — which would answer with the bad-request type — is also present.
    /// </remarks>
    public static async Task It_answers_a_malformed_window_and_an_unknown_field_in_the_parameter_validation_shell(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        using var response = await harness.HttpClient.GetAsync(
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?minChangeVersion=bogus&{UnknownFieldName}=1"
        );

        await ParameterValidationProblemDetails.AssertShellAsync(response, MalformedChangeVersion);
    }

    /// <summary>
    /// A partition count supplied twice in different letter cases is recognized under either casing and
    /// resolved to the last value supplied.
    /// </summary>
    /// <remarks>
    /// One request separates four ways this can go wrong. If <c>NUMBER</c> were treated as an unknown
    /// parameter, the answer would be the unknown-field message; if the variants were not collapsed, the
    /// surviving <c>number=abc</c> would be answered with the range message; if the first value won,
    /// <c>abc</c> would be answered the same way; and if collapsing threw, nothing would be served at
    /// all. Only recognition under either casing plus last-value-wins reaches a served token array.
    ///
    /// <para>
    /// Which value survived is then asserted through the boundaries it produces rather than through the
    /// response merely succeeding. One token is a result only a count of one yields over this seed: the
    /// minimum partition size binds at ten rows, so a dropped or defaulted count would have cut the same
    /// twenty-five documents into <see cref="PartitionsWhenMinimumSizeBinds"/> instead.
    /// </para>
    /// </remarks>
    public static async Task It_resolves_a_partition_count_supplied_in_two_letter_cases_to_the_last_value(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await CursorContractSupport.SeedMergeItemsAsync(harness, "case-variant-count", SeededDocumentCount);

        var caseVariantTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=abc&NUMBER=1"
        );

        caseVariantTokens
            .Should()
            .HaveCount(
                1,
                "the last value supplied under either casing is the count that binds, and only a count "
                    + "of one leaves this seed in a single partition"
            );

        var singleCasingTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=1"
        );

        caseVariantTokens
            .Should()
            .Equal(
                singleCasingTokens,
                "a count reached through case-variant collapsing produces the same boundary as the same "
                    + "count supplied once"
            );
    }

    /// <summary>
    /// A single requested partition produces one token, and that token covers the whole collection.
    /// </summary>
    /// <remarks>
    /// One token is the boundary case of the sizing rule: the computed size is the whole candidate
    /// count, so there is nothing above the first starting identity. Walking it is what distinguishes a
    /// correct single partition from a token that happens to be alone but starts in the middle of the
    /// collection.
    /// </remarks>
    public static async Task It_covers_the_collection_with_one_partition_when_one_is_requested(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        var seededIds = await CursorContractSupport.SeedMergeItemsAsync(
            harness,
            "single-partition",
            SeededDocumentCount
        );

        var pageTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=1"
        );

        pageTokens.Should().HaveCount(1, "a single requested partition is sized to the whole candidate set");

        var walkedIds = await CursorContractSupport.WalkFromTokenAsync(
            harness,
            CursorContractSupport.MergeItemsEndpoint,
            pageTokens[0],
            HostMaximumPageSize
        );

        walkedIds.Should().OnlyHaveUniqueItems("a walk must not return a document twice");
        walkedIds
            .Should()
            .BeEquivalentTo(seededIds, "the one partition covers every document in the collection");
    }

    /// <summary>
    /// Once the minimum partition size binds, asking for far more partitions returns the same
    /// boundaries as asking for the number the collection can actually support — fewer than requested,
    /// and not an error.
    /// </summary>
    /// <remarks>
    /// Twenty-five candidates at a minimum size of ten support three partitions. Requesting three and
    /// requesting two hundred therefore compute the same size, and the assertion is that they hand out
    /// the same tokens rather than merely the same number of them: a sizing rule that ignored the
    /// minimum for the larger request would return more tokens, and one that mis-selected the starting
    /// identities would return the same count over different ranges.
    /// </remarks>
    public static async Task It_returns_the_same_boundaries_once_the_minimum_partition_size_binds(
        ApiIntegrationHarness harness
    )
    {
        ArgumentNullException.ThrowIfNull(harness);

        await CursorContractSupport.SeedMergeItemsAsync(harness, "minimum-size-binds", SeededDocumentCount);

        var supportedCountTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number={PartitionsWhenMinimumSizeBinds}"
        );

        supportedCountTokens
            .Should()
            .HaveCount(
                PartitionsWhenMinimumSizeBinds,
                "the seed exceeds the minimum partition size, so the requested count is reachable"
            );

        var oversizedRequestTokens = await CursorContractSupport.ReadPageTokensAsync(
            harness,
            $"{CursorContractSupport.MergeItemsPartitionsEndpoint}?number=200"
        );

        oversizedRequestTokens
            .Should()
            .HaveCountLessThan(
                200,
                "a collection cannot be cut into more partitions than the minimum allows"
            );
        oversizedRequestTokens
            .Should()
            .Equal(
                supportedCountTokens,
                "requesting more partitions than the minimum size allows returns the boundaries the "
                    + "minimum produces rather than an error"
            );
    }
}
