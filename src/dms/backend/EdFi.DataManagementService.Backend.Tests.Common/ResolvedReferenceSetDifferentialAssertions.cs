// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Asserts that the natural-key resolver produced exactly the <see cref="ResolvedReferenceSet"/> the
/// hash-based resolver produced for the same request against the same database.
/// </summary>
/// <remarks>
/// Shared by the PostgreSQL and SQL Server differential fixtures so both dialects hold the same bar. Every
/// public member of the contract is compared except <c>ReferenceLookupResult.VerificationIdentityKey</c>,
/// which is the witness string of the corruption check the natural-key resolver deliberately does not have
/// — it has no natural-key analogue and is deleted with the old resolver in Phase 4.
/// </remarks>
public static class ResolvedReferenceSetDifferentialAssertions
{
    public static void ShouldResolveIdenticallyTo(
        this ResolvedReferenceSet naturalKeyResult,
        ResolvedReferenceSet hashResult,
        string scenario
    )
    {
        ArgumentNullException.ThrowIfNull(naturalKeyResult);
        ArgumentNullException.ThrowIfNull(hashResult);

        DescribeDocumentSuccesses(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDocumentSuccesses(hashResult),
                $"[{scenario}] successful document references must match the hash resolver"
            );

        DescribeDescriptorSuccesses(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDescriptorSuccesses(hashResult),
                $"[{scenario}] successful descriptor references must match the hash resolver"
            );

        DescribeDocumentFailures(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDocumentFailures(hashResult),
                $"[{scenario}] invalid document references (paths, reasons and order) must match the hash resolver"
            );

        DescribeDescriptorFailures(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDescriptorFailures(hashResult),
                $"[{scenario}] invalid descriptor references (paths, reasons and order) must match the hash resolver"
            );

        DescribeLookups(naturalKeyResult)
            .Should()
            .Equal(
                DescribeLookups(hashResult),
                $"[{scenario}] LookupsByReferentialId must stay a faithful hash-keyed view for Phase 3 consumers"
            );

        DescribeDocumentOccurrences(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDocumentOccurrences(hashResult),
                $"[{scenario}] per-occurrence document diagnostics must match the hash resolver"
            );

        DescribeDescriptorOccurrences(naturalKeyResult)
            .Should()
            .Equal(
                DescribeDescriptorOccurrences(hashResult),
                $"[{scenario}] per-occurrence descriptor diagnostics must match the hash resolver"
            );

        naturalKeyResult
            .HasFailures.Should()
            .Be(hashResult.HasFailures, $"[{scenario}] the failure gate must agree");
    }

    private static IReadOnlyList<string> DescribeDocumentSuccesses(ResolvedReferenceSet set) =>
        [
            .. set
                .SuccessfulDocumentReferencesByPath.OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
                .Select(entry =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{entry.Key.Value} -> documentId={entry.Value.DocumentId}, resourceKeyId={entry.Value.ResourceKeyId}, "
                            + $"target={Describe(entry.Value.Reference.ResourceInfo)}, identity={Describe(entry.Value.Reference.DocumentIdentity)}"
                    )
                ),
        ];

    private static IReadOnlyList<string> DescribeDescriptorSuccesses(ResolvedReferenceSet set) =>
        [
            .. set
                .SuccessfulDescriptorReferencesByPath.OrderBy(
                    entry => entry.Key.Value,
                    StringComparer.Ordinal
                )
                .Select(entry =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{entry.Key.Value} -> documentId={entry.Value.DocumentId}, resourceKeyId={entry.Value.ResourceKeyId}, "
                            + $"target={Describe(entry.Value.Reference.ResourceInfo)}, identity={Describe(entry.Value.Reference.DocumentIdentity)}"
                    )
                ),
        ];

    private static IReadOnlyList<string> DescribeDocumentFailures(ResolvedReferenceSet set) =>
        [
            .. set.InvalidDocumentReferences.Select(failure =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{failure.Path.Value} -> {failure.Reason}, target={Describe(failure.TargetResource)}, "
                        + $"referentialId={failure.ReferentialId.Value}, identity={Describe(failure.DocumentIdentity)}"
                )
            ),
        ];

    private static IReadOnlyList<string> DescribeDescriptorFailures(ResolvedReferenceSet set) =>
        [
            .. set.InvalidDescriptorReferences.Select(failure =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{failure.Path.Value} -> {failure.Reason}, target={Describe(failure.TargetResource)}, "
                        + $"referentialId={failure.ReferentialId.Value}, identity={Describe(failure.DocumentIdentity)}"
                )
            ),
        ];

    private static IReadOnlyList<string> DescribeLookups(ResolvedReferenceSet set) =>
        [
            .. set
                .LookupsByReferentialId.OrderBy(entry => entry.Key.Value.ToString(), StringComparer.Ordinal)
                .Select(entry =>
                    entry.Value.Result is { } result
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"{entry.Key.Value} -> documentId={result.DocumentId}, resourceKeyId={result.ResourceKeyId}, "
                                + $"referentialIdentityResourceKeyId={result.ReferentialIdentityResourceKeyId}, isDescriptor={result.IsDescriptor}"
                        )
                        : string.Create(CultureInfo.InvariantCulture, $"{entry.Key.Value} -> <miss>")
                ),
        ];

    private static IReadOnlyList<string> DescribeDocumentOccurrences(ResolvedReferenceSet set) =>
        [
            .. set.DocumentReferenceOccurrences.Select(occurrence =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{occurrence.Reference.Path.Value} -> {DescribeSnapshot(occurrence.Lookup)}"
                )
            ),
        ];

    private static IReadOnlyList<string> DescribeDescriptorOccurrences(ResolvedReferenceSet set) =>
        [
            .. set.DescriptorReferenceOccurrences.Select(occurrence =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{occurrence.Reference.Path.Value} -> {DescribeSnapshot(occurrence.Lookup)}"
                )
            ),
        ];

    private static string DescribeSnapshot(ReferenceLookupSnapshot snapshot) =>
        snapshot.Result is { } result
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"documentId={result.DocumentId}, resourceKeyId={result.ResourceKeyId}, isDescriptor={result.IsDescriptor}"
            )
            : "<miss>";

    private static string Describe(BaseResourceInfo resourceInfo) =>
        $"{resourceInfo.ProjectName.Value}.{resourceInfo.ResourceName.Value}";

    private static string Describe(DocumentIdentity documentIdentity) =>
        string.Join(
            "#",
            documentIdentity.DocumentIdentityElements.Select(element =>
                $"{element.IdentityJsonPath.Value}={element.IdentityValue}"
            )
        );
}
