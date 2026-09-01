// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// The deterministic descriptor fixture for the final gate's descriptor namespace scenarios:
/// one descriptor resource loaded dense from DocumentId 1 in its own leased database, split
/// across an accessible and an inaccessible namespace. Odd ordinals carry the accessible
/// namespace — interleaved rather than contiguous, so an accessible page always spans
/// excluded rows and a candidate relation that lost its namespace predicate cannot return a
/// coincidentally plausible range. Every identity derivation must stay expressible in
/// set-based SQL on both providers.
/// </summary>
public sealed record PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind Kind)
{
    public const string ProjectName = "Ed-Fi";

    public const string ResourceName = "AcademicSubjectDescriptor";

    public const string ResourceEndpoint = "/data/ed-fi/academicSubjectDescriptors";

    /// <summary>
    /// The namespace prefix the reading principal holds; only descriptors under it are
    /// accessible to the namespace-based claim.
    /// </summary>
    public const string AccessibleNamespacePrefix = "uri://perf-accessible.ed-fi.org";

    public const string AccessibleNamespace = AccessibleNamespacePrefix + "/" + ResourceName;

    public const string InaccessibleNamespace = "uri://perf-denied.example/" + ResourceName;

    /// <summary>
    /// Fixed prefix of every fixture DocumentUuid; the final twelve hex digits are the row
    /// ordinal.
    /// </summary>
    public const string DocumentUuidPrefix = "8f7a3000-0000-4000-8000-";

    public long RowCount => Kind.RowCount;

    /// <summary>
    /// The fixture loads into an empty document table, so DocumentIds are dense: the row
    /// ordinal is the DocumentId.
    /// </summary>
    public static long DocumentIdFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return ordinal;
    }

    public long MaxDocumentId => RowCount;

    public long ReseedTargetDocumentId => MaxDocumentId;

    public long AccessibleCount =>
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Descriptor, RowCount);

    public static bool IsAccessible(long ordinal) =>
        PerfVariantCandidates.IsCandidateRowOrdinal(PerfFinalGateVariant.Descriptor, ordinal);

    public static string NamespaceFor(long ordinal) =>
        IsAccessible(ordinal) ? AccessibleNamespace : InaccessibleNamespace;

    public static string CodeValueFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return "perf-" + ordinal.ToString("D9", CultureInfo.InvariantCulture);
    }

    public static string UriFor(long ordinal) => NamespaceFor(ordinal) + "#" + CodeValueFor(ordinal);

    public static Guid DocumentUuidFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return Guid.ParseExact(
            DocumentUuidPrefix + ordinal.ToString("x12", CultureInfo.InvariantCulture),
            "D"
        );
    }

    /// <summary>
    /// Sum of every DocumentId, used as a cross-provider parity checksum: dense ids make it
    /// the triangular number of the row count.
    /// </summary>
    public long DocumentIdSum() => RowCount * (RowCount + 1) / 2;
}
