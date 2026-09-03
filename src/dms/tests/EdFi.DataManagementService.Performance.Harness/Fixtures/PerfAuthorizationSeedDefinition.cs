// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// The analytic definition of the authorized-variant seeding over an already-loaded primary
/// fixture. One school and one grade-level descriptor are created, and every second student
/// (the authorized candidate selection) receives one StudentSchoolAssociation enrolling it at
/// that school. Only durable source tables are written — the generated authorization view
/// over StudentSchoolAssociation and the education-organization hierarchy then makes exactly
/// those students reachable by a principal holding the school's education organization id.
/// The hierarchy self-edge and every referential identity come from the production triggers
/// the inserts fire, never from direct writes.
///
/// The seed runs strictly after the pristine measurement phase because its association
/// documents change the dms.Document population the baseline-comparable cells measure.
/// </summary>
public sealed record PerfAuthorizationSeedDefinition(PerfFixtureDefinition Primary)
{
    /// <summary>
    /// The school's SchoolId, which is also the education organization id the authorized
    /// principal's claim carries. Far above the fixture id ranges so nothing can collide.
    /// </summary>
    public const long SchoolId = 8_990_001;

    public const string SchoolResourceName = "School";

    public const string StudentSchoolAssociationResourceName = "StudentSchoolAssociation";

    public const string GradeLevelDescriptorResource = "GradeLevelDescriptor";

    public const string NameOfInstitution = "Perf Authorized School";

    public const string EntryDateIso = "2025-08-11";

    /// <summary>
    /// Fixed prefix of every association DocumentUuid; the final twelve hex digits are the
    /// 1-based candidate index. Distinct from the student prefix so the two ranges can never
    /// collide.
    /// </summary>
    public const string SsaDocumentUuidPrefix = "8f7a1000-0000-4000-8000-";

    public static readonly Guid SchoolDocumentUuid = Guid.Parse("8f7a2000-0000-4000-8000-000000000001");

    public static readonly Guid GradeLevelDescriptorDocumentUuid = Guid.Parse(
        "8f7a2000-0000-4000-8000-000000000002"
    );

    public static string GradeLevelDescriptorUri =>
        PerfFixtureDefinition.DescriptorUriFor(GradeLevelDescriptorResource);

    /// <summary>
    /// How many students the seed enrolls: the authorized candidate selection over the
    /// primary fixture.
    /// </summary>
    public long EnrolledStudentCount =>
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Authorized, Primary.RowCount);

    /// <summary>
    /// The seed's documents sit directly above the primary fixture's reseed target: the
    /// school first, the grade-level descriptor second, then the association block.
    /// </summary>
    public long SchoolDocumentId => Primary.ReseedTargetDocumentId + 1;

    public long GradeLevelDescriptorDocumentId => Primary.ReseedTargetDocumentId + 2;

    /// <summary>
    /// The association for candidate index k occupies DocumentId base + k.
    /// </summary>
    public long SsaDocumentIdBase => Primary.ReseedTargetDocumentId + 2;

    public long SsaMaxDocumentId => SsaDocumentIdBase + EnrolledStudentCount;

    /// <summary>
    /// The highest DocumentId the seed emits; the identity reseed hands out the next value.
    /// </summary>
    public long ReseedTargetDocumentId => SsaMaxDocumentId;

    /// <summary>
    /// The student row ordinal the k-th association enrolls.
    /// </summary>
    public static long EnrolledStudentOrdinal(long candidateIndex) =>
        PerfVariantCandidates.RowOrdinalOfCandidate(PerfFinalGateVariant.Authorized, candidateIndex);

    /// <summary>
    /// Sum of every enrolled student's DocumentId, the cross-provider checksum the seed
    /// verification holds the association rows to.
    /// </summary>
    public long EnrolledStudentDocumentIdSum()
    {
        long sum = 0;
        for (long candidateIndex = 1; candidateIndex <= EnrolledStudentCount; candidateIndex++)
        {
            sum += PerfFixtureDefinition.DocumentIdFor(EnrolledStudentOrdinal(candidateIndex));
        }

        return sum;
    }

    public static Guid SsaDocumentUuidFor(long candidateIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateIndex, 1);
        return Guid.ParseExact(
            SsaDocumentUuidPrefix + candidateIndex.ToString("x12", CultureInfo.InvariantCulture),
            "D"
        );
    }
}
