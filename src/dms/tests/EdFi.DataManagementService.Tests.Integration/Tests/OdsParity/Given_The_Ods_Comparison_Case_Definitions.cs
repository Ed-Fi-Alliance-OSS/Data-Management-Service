// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.OdsParity;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Tests.OdsParity;

/// <summary>
/// Fail-closed guardrails over the committed ODS 7.3.2 comparison definitions. Pure JSON reading: no
/// database is leased and no request is issued.
/// </summary>
/// <remarks>
/// The expected id lists below are explicit rather than derived from the files they check. A guardrail
/// that recomputed its expectations from the same data could not notice the data shrinking, which is the
/// failure this exists to catch: a case quietly deleted, or an approved difference quietly dropped,
/// would otherwise leave a green suite that no longer covers what the epic approved.
/// <para>
/// Carries the three CI-selection categories so both API integration lanes select it, exactly as
/// <c>Given_The_Api_Parity_Catalog_Resolution</c> does, while inheriting no database base.
/// </para>
/// </remarks>
[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_The_Ods_Comparison_Case_Definitions
{
    /// <summary>
    /// Every row of the epic's ODS Precedence Comparison table, in the order the table states them.
    /// </summary>
    private static readonly string[] _expectedPrecedenceCaseIds =
    [
        "cursor-precedence-01",
        "cursor-precedence-02",
        "cursor-precedence-03",
        "cursor-precedence-04",
        "cursor-precedence-05",
        "cursor-precedence-06",
        "cursor-precedence-07",
        "cursor-precedence-08",
        "cursor-precedence-09",
        "cursor-precedence-10",
        "cursor-precedence-11",
        "cursor-precedence-12",
        "cursor-precedence-13",
    ];

    /// <summary>
    /// Every bullet of the epic's Approved Intentional ODS Differences list, in the order that list
    /// states them.
    /// </summary>
    private static readonly string[] _expectedDifferenceIds =
    [
        "reject-limit-with-cursor-parameters",
        "totalcount-rejected-at-validation",
        "cursor-key-presence-is-significant",
        "number-reserved-from-filter-matching",
        "number-non-numeric-range-message",
        "number-blank-range-message",
        "cursor-parameters-rejected-on-change-queries",
        "partition-reserved-parameters-unsupported",
        "ods-only-partition-parameters-rejected",
        "true-ceiling-at-most-requested-count",
        "partitions-enforce-profile-method-usage",
        "header-gated-on-selected-keyset-maximum",
        "offset-message-text-retained",
        "limit-message-text-retained",
        "maximum-page-size-is-the-omitted-default",
        "openapi-publishes-runtime-page-size-bounds",
        "openapi-publishes-runtime-default-partition-count",
        "write-only-profile-omits-partitions-path",
        "int64-document-id-bounds",
        "no-header-overflow-at-int64-max",
        "strict-base64url-and-decimal-decoder",
    ];

    /// <summary>
    /// Every group a case may belong to, and therefore every group a fixture must bind. A case in a
    /// group nothing runs would never be executed, and a bound group with no cases would assert nothing.
    /// </summary>
    private static readonly string[] _expectedGroups =
    [
        "validation",
        "omitted-limit-default",
        "sizing",
        "number-collision",
        "int64-bounds",
        "identity-maximum",
        "empty-hydration",
        "profile",
        "metadata",
        "profile-metadata",
    ];

    private static readonly string[] _expectedExecutors =
    [
        "collection-get",
        "partitions-get",
        "change-query-get",
        "served-document",
        "sizing-true-ceiling",
        "number-collision",
        "empty-hydration",
        "identity-maximum",
        "profile-partitions-get",
        "profile-document-partitions-omission",
    ];

    [Test]
    public void It_records_the_reference_version_and_its_source_authorities()
    {
        var metadata = OdsComparisonCatalog.Definitions.Metadata;

        metadata["odsVersion"]!.GetValue<string>().Should().Be("7.3.2");
        metadata["live"]!
            .GetValue<bool>()
            .Should()
            .BeFalse("no ODS instance is stood up; the ODS column is static expected data");
        metadata["liveReason"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        metadata["derivedFrom"]!
            .GetValue<string>()
            .Should()
            .Be("reference/design/backend-redesign/epics/20-partitioned-cursor-paging/EPIC.md");
        metadata["odsSourceAuthorities"]!
            .AsArray()
            .Should()
            .HaveCount(6, "the epic's Compatibility Baseline pins six ODS 7.3.2 sources");
    }

    [Test]
    public void It_contains_every_precedence_row_exactly_once_and_in_order()
    {
        string[] precedenceCaseIds =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    comparisonCase.SourceFile == "cursor-precedence-cases.json"
                )
                .Select(comparisonCase => comparisonCase.Id),
        ];

        precedenceCaseIds
            .Should()
            .Equal(
                _expectedPrecedenceCaseIds,
                "every row of the epic's precedence table is a case, in the order the table states them"
            );
    }

    [Test]
    public void It_contains_every_approved_difference_exactly_once_and_in_order()
    {
        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Id)
            .Should()
            .Equal(
                _expectedDifferenceIds,
                "the catalog mirrors the epic's approved-difference list one for one"
            );

        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Bullet)
            .Should()
            .Equal(Enumerable.Range(1, _expectedDifferenceIds.Length), "bullets are numbered in order");
    }

    [Test]
    public void It_declares_every_approved_difference_executable()
    {
        OdsComparisonCatalog
            .Definitions.Catalog.Where(entry => !entry.Executable)
            .Select(entry => entry.Id)
            .Should()
            .BeEmpty("every approved difference is observable from a DMS target and is executed here");
    }

    [Test]
    public void It_gives_every_case_a_unique_id()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Id)
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Test]
    public void It_resolves_every_named_approved_difference()
    {
        string[] catalogIds = [.. OdsComparisonCatalog.Definitions.Catalog.Select(entry => entry.Id)];

        string[] unresolved =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Where(comparisonCase =>
                    comparisonCase.ApprovedDifference is not null
                    && !catalogIds.Contains(comparisonCase.ApprovedDifference, StringComparer.Ordinal)
                )
                .Select(comparisonCase => $"{comparisonCase.Id} -> {comparisonCase.ApprovedDifference}"),
        ];

        unresolved.Should().BeEmpty("a case may only name a difference the catalog declares");
    }

    [Test]
    public void It_references_every_approved_difference_from_at_least_one_case()
    {
        string[] referenced =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.ApprovedDifference)
                .Where(difference => difference is not null)
                .Select(difference => difference!)
                .Distinct(StringComparer.Ordinal),
        ];

        OdsComparisonCatalog
            .Definitions.Catalog.Select(entry => entry.Id)
            .Except(referenced, StringComparer.Ordinal)
            .Should()
            .BeEmpty("an approved difference nothing executes is not evidence of anything");
    }

    [Test]
    public void It_requires_every_difference_case_to_name_one()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Where(comparisonCase =>
                comparisonCase.DeclaresDifference
                && string.IsNullOrWhiteSpace(comparisonCase.ApprovedDifference)
            )
            .Select(comparisonCase => comparisonCase.Id)
            .Should()
            .BeEmpty("an unmapped difference is exactly what this suite exists to fail on");

        OdsComparisonCatalog
            .Definitions.Cases.Where(comparisonCase =>
                !comparisonCase.DeclaresDifference && comparisonCase.ApprovedDifference is not null
            )
            .Select(comparisonCase => comparisonCase.Id)
            .Should()
            .BeEmpty("a case declaring parity cannot also name a difference");
    }

    [Test]
    public void It_uses_only_declared_outcomes_groups_and_executors()
    {
        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Outcome)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(new[] { "parity", "difference" });

        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Group)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(_expectedGroups, "a case in an unbound group would never be executed");

        OdsComparisonCatalog
            .Definitions.Cases.Select(comparisonCase => comparisonCase.Executor)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(_expectedExecutors, "an unknown executor throws rather than skipping the case");
    }

    [Test]
    public void It_binds_every_declared_group_and_executor()
    {
        string[] usedGroups =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.Group)
                .Distinct(StringComparer.Ordinal),
        ];

        _expectedGroups
            .Should()
            .BeSubsetOf(usedGroups, "a declared group with no case would run an empty comparison");

        string[] usedExecutors =
        [
            .. OdsComparisonCatalog
                .Definitions.Cases.Select(comparisonCase => comparisonCase.Executor)
                .Distinct(StringComparer.Ordinal),
        ];

        _expectedExecutors
            .Should()
            .BeSubsetOf(usedExecutors, "an executor nothing uses is dead comparison machinery");
    }
}
