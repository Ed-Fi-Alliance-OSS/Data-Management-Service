// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// The deterministic fixture definition shared between this baseline and the final-gate story,
/// so both measure byte-identical data. Row k (1-based) maps to the k-th positive integer that
/// is not congruent to 1 modulo 10: every complete block of ten ids carries exactly one gap,
/// and leading each block with its gap keeps the gap share of the id space at or above 10%
/// even when the final block is partial (a trailing gap would leave it fractionally under).
/// Relative to the row count the gap share is one per nine rows.
///
/// Every student carries one row in each of the four child collection tables and a
/// birth-sex descriptor, backed by a fixed catalog of descriptor documents at the ids
/// directly above the student range, so the hydration batch's collection and
/// descriptor-URI-resolution statements do real, uniform work at every page offset. The
/// optional person reference stays null by design: a faithful nonzero shape would need one
/// Person document per student (doubling dms.Document and shifting every Document-join
/// measurement), and a shared person would be an unfaithful fan-in — so the batch's
/// reference-resolution statement legitimately returns zero rows.
///
/// Every identity derivation here must stay expressible in set-based SQL on both PostgreSQL
/// and SQL Server, because the loaders generate rows with generate_series/GENERATE_SERIES and
/// must produce exactly these values.
/// </summary>
public sealed record PerfFixtureDefinition(PerfFixtureKind Kind)
{
    public const string DefinitionVersion = "2.0.0";

    public const string ResourceEndpoint = "/data/ed-fi/students";

    public const string ProjectName = "Ed-Fi";

    public const string ResourceName = "Student";

    public const string FirstName = "Perf";

    public const string LastSurname = "Student";

    public const string BirthDateIso = "2010-01-01";

    /// <summary>
    /// Fixed prefix of every fixture DocumentUuid; the final twelve hex digits are the row
    /// ordinal. The '4' and '8' nibbles keep the value RFC-4122-shaped.
    /// </summary>
    public const string DocumentUuidPrefix = "8f7a0000-0000-4000-8000-";

    public const string DescriptorCodeValue = "Perf";

    public const string SexDescriptorResource = "SexDescriptor";

    public const string OtherNameTypeDescriptorResource = "OtherNameTypeDescriptor";

    public const string IdentificationDocumentUseDescriptorResource = "IdentificationDocumentUseDescriptor";

    public const string PersonalInformationVerificationDescriptorResource =
        "PersonalInformationVerificationDescriptor";

    public const string VisaDescriptorResource = "VisaDescriptor";

    /// <summary>
    /// The fixed descriptor catalog every student's descriptor-backed values point at. The
    /// list position (1-based) fixes each descriptor's DocumentId directly above the student
    /// id range, so the student id/gap scheme is untouched.
    /// </summary>
    public static readonly IReadOnlyList<string> DescriptorResourceNames =
    [
        SexDescriptorResource,
        OtherNameTypeDescriptorResource,
        IdentificationDocumentUseDescriptorResource,
        PersonalInformationVerificationDescriptorResource,
        VisaDescriptorResource,
    ];

    public static int DescriptorCount => DescriptorResourceNames.Count;

    /// <summary>
    /// Rows per student in each of the four child collection tables.
    /// </summary>
    public const int ChildCollectionRowsPerStudent = 1;

    public long RowCount => Kind.RowCount;

    public static long MinDocumentId => DocumentIdFor(1);

    public long MaxDocumentId => DocumentIdFor(RowCount);

    /// <summary>
    /// The highest DocumentId the loader emits: the descriptor catalog sits directly above
    /// the student range, and the identity reseed hands out the next value after it.
    /// </summary>
    public long ReseedTargetDocumentId => MaxDocumentId + DescriptorCount;

    public long DescriptorDocumentIdFor(string resourceName) =>
        MaxDocumentId + DescriptorPositionOf(resourceName);

    public Guid DescriptorDocumentUuidFor(string resourceName) =>
        DocumentUuidFor(RowCount + DescriptorPositionOf(resourceName));

    public static string DescriptorNamespaceFor(string resourceName) => "uri://ed-fi.org/" + resourceName;

    public static string DescriptorUriFor(string resourceName) =>
        DescriptorNamespaceFor(resourceName) + "#" + DescriptorCodeValue;

    private static int DescriptorPositionOf(string resourceName)
    {
        int index = -1;
        for (int candidate = 0; candidate < DescriptorResourceNames.Count; candidate++)
        {
            if (DescriptorResourceNames[candidate] == resourceName)
            {
                index = candidate;
                break;
            }
        }

        return index >= 0
            ? index + 1
            : throw new ArgumentException(
                $"'{resourceName}' is not in the fixture descriptor catalog.",
                nameof(resourceName)
            );
    }

    /// <summary>
    /// Missing ids inside [1, MaxDocumentId], counted analytically.
    /// </summary>
    public long GapCount => MaxDocumentId - RowCount;

    /// <summary>
    /// Gap share of the id space [1, MaxDocumentId].
    /// </summary>
    public double GapDensity => (double)GapCount / MaxDocumentId;

    /// <summary>
    /// Sum of every generated DocumentId, used as a cross-provider parity checksum by the
    /// loader verification queries.
    /// </summary>
    public long DocumentIdSum()
    {
        long sum = 0;
        for (long ordinal = 1; ordinal <= RowCount; ordinal++)
        {
            sum += DocumentIdFor(ordinal);
        }

        return sum;
    }

    public static long DocumentIdFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        long block = (ordinal - 1) / 9;
        long positionInBlock = (ordinal - 1) % 9;
        return (block * 10) + positionInBlock + 2;
    }

    public static string StudentUniqueIdFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return "perf-" + ordinal.ToString("D9", CultureInfo.InvariantCulture);
    }

    public static Guid DocumentUuidFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return Guid.ParseExact(
            DocumentUuidPrefix + ordinal.ToString("x12", CultureInfo.InvariantCulture),
            "D"
        );
    }
}
