// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// The two benchmark workloads the Phase 3 cutover gate is measured over, built once and shared by both
/// dialect fixtures so the batch shape cannot drift between engines.
/// </summary>
/// <remarks>
/// Every reference is seeded on BOTH terms — an unseeded referential id would miss on the hash resolver
/// while the natural-key resolver still found the row, and a benchmark comparing a full lookup against a
/// short-circuited miss measures nothing. So each seeded row gets a <c>dms.Document</c> row, a
/// <c>dms.ReferentialIdentity</c> row (the hash arm's index) and a distinct identity value in its own root
/// table (the natural-key arm's index).
/// </remarks>
public sealed class ReferenceResolverBenchmarkWorkload
{
    /// <summary>Distinct <c>School</c> references in one request — the bulk-batch case.</summary>
    public const string BulkCase = "bulk-school-references";

    /// <summary>
    /// Wide-RefKey document references (six scalar columns, one per kind the probe must type) mixed with
    /// descriptor references in one request — the deep-identity case. The widest probe and the descriptor
    /// URI probe in the same round trip.
    /// </summary>
    public const string DeepIdentityCase = "deep-identity-and-descriptor-references";

    /// <summary>
    /// Wide-identity references in the deep-identity batch. 256 × 6 probe columns + 256 descriptor
    /// parameters = 1792, inside SQL Server's 2098-parameter per-command ceiling, so the deep-identity case
    /// is exactly one round trip on both engines and the two arms are compared on equal terms.
    /// </summary>
    public const int DeepIdentityReferenceCount = 256;

    /// <summary>Descriptor references in the deep-identity batch.</summary>
    public const int DescriptorReferenceCount = 256;

    /// <summary>
    /// The same mix at the size a real document write actually resolves — tens of references, not thousands.
    /// Diagnostic rather than a spec case: the two spec cases measure the batch extremes, and a gate
    /// verdict that only reproduces at bulk sizes means something different from one that reproduces here.
    /// </summary>
    public const string SmallBatchCase = "small-batch-mixed-references";

    /// <summary>Wide-identity and descriptor references each contribute this many to the small batch.</summary>
    public const int SmallBatchReferenceCountPerKind = 32;

    private const long BulkSchoolIdentityBase = 300_000;
    private const long BulkDocumentIdBase = 2_000;
    private const long DeepIdentityDocumentIdBase = 600_000;
    private const long DescriptorDocumentIdBase = 700_000;
    private const long DeepIdentityInt64KeyOffset = 1_000;

    private readonly ReferenceResolverIntegrationFixture _fixture;
    private readonly short _schoolResourceKeyId;
    private readonly short _wideIdentityResourceKeyId;
    private readonly short _schoolTypeDescriptorResourceKeyId;

    public ReferenceResolverBenchmarkWorkload(
        ReferenceResolverIntegrationFixture fixture,
        MappingSet mappingSet,
        int bulkReferenceCount
    )
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bulkReferenceCount);

        _fixture = fixture;
        BulkReferenceCount = bulkReferenceCount;
        _schoolResourceKeyId = mappingSet.ResourceKeyIdByResource[fixture.SchoolResource];
        _wideIdentityResourceKeyId = mappingSet.ResourceKeyIdByResource[fixture.WideIdentityResource];
        _schoolTypeDescriptorResourceKeyId = mappingSet.ResourceKeyIdByResource[
            fixture.SchoolTypeDescriptorResource
        ];
    }

    /// <summary>
    /// References in the bulk batch. PostgreSQL uses the established 4096; SQL Server uses the 2500 the
    /// differential venue already proves end to end against a live server.
    /// </summary>
    public int BulkReferenceCount { get; }

    /// <summary>Total references in the deep-identity batch (documents + descriptors).</summary>
    public static int DeepIdentityBatchReferenceCount =>
        DeepIdentityReferenceCount + DescriptorReferenceCount;

    /// <summary>Total references in the small diagnostic batch (documents + descriptors).</summary>
    public static int SmallBatchReferenceCount => SmallBatchReferenceCountPerKind * 2;

    /// <summary>
    /// Everything every case resolves against, on top of the fixture's default seed. Applied once in
    /// <c>OneTimeSetUp</c>: the benchmark never re-seeds between iterations, so no iteration pays for
    /// another iteration's inserts. The small batch is a prefix of the deep-identity batch, so it needs no
    /// seed of its own.
    /// </summary>
    public ReferenceResolverSeedData CreateSeedData()
    {
        List<ReferenceResolverDocumentSeed> documents = [];
        List<ReferenceResolverReferentialIdentitySeed> referentialIdentities = [];
        List<ReferenceResolverSchoolSeed> schools = [];
        List<ReferenceResolverWideIdentitySeed> wideIdentities = [];
        List<ReferenceResolverDescriptorSeed> descriptors = [];

        for (var index = 0; index < BulkReferenceCount; index++)
        {
            var documentId = BulkDocumentIdBase + index;

            documents.Add(
                new ReferenceResolverDocumentSeed(
                    documentId,
                    CreateSeedUuid(0x80, index + 1),
                    _schoolResourceKeyId
                )
            );
            referentialIdentities.Add(
                new ReferenceResolverReferentialIdentitySeed(
                    BulkSchoolReferentialId(index),
                    documentId,
                    _schoolResourceKeyId
                )
            );
            schools.Add(new ReferenceResolverSchoolSeed(documentId, BulkSchoolIdentityBase + index));
        }

        for (var index = 0; index < DeepIdentityReferenceCount; index++)
        {
            var documentId = DeepIdentityDocumentIdBase + index;

            documents.Add(
                new ReferenceResolverDocumentSeed(
                    documentId,
                    CreateSeedUuid(0x81, index + 1),
                    _wideIdentityResourceKeyId
                )
            );
            referentialIdentities.Add(
                new ReferenceResolverReferentialIdentitySeed(
                    DeepIdentityReferentialId(index),
                    documentId,
                    _wideIdentityResourceKeyId
                )
            );
            wideIdentities.Add(
                new ReferenceResolverWideIdentitySeed(
                    documentId,
                    DeepIdentityInt64Key(index),
                    ReferenceResolverIntegrationFixture.WideIdentityDecimalValue,
                    ReferenceResolverIntegrationFixture.WideIdentityDateValue,
                    ReferenceResolverIntegrationFixture.WideIdentityDateTimeValue,
                    BooleanKey: true,
                    ReferenceResolverIntegrationFixture.WideIdentityStringValue
                )
            );
        }

        for (var index = 0; index < DescriptorReferenceCount; index++)
        {
            var documentId = DescriptorDocumentIdBase + index;
            var codeValue = DescriptorCodeValue(index);

            documents.Add(
                new ReferenceResolverDocumentSeed(
                    documentId,
                    CreateSeedUuid(0x82, index + 1),
                    _schoolTypeDescriptorResourceKeyId
                )
            );
            referentialIdentities.Add(
                new ReferenceResolverReferentialIdentitySeed(
                    DescriptorReferentialId(index),
                    documentId,
                    _schoolTypeDescriptorResourceKeyId
                )
            );
            descriptors.Add(
                new ReferenceResolverDescriptorSeed(
                    documentId,
                    "uri://ed-fi.org",
                    codeValue,
                    codeValue,
                    "SchoolTypeDescriptor",
                    DescriptorUri(index)
                )
            );
        }

        return new ReferenceResolverSeedData(
            ResourceKeys: [],
            Documents: documents,
            ReferentialIdentities: referentialIdentities,
            Schools: schools,
            LocalEducationAgencies: [],
            Descriptors: descriptors
        )
        {
            WideIdentities = wideIdentities,
        };
    }

    public IReadOnlyList<DocumentReference> CreateBulkDocumentReferences()
    {
        return
        [
            .. Enumerable
                .Range(0, BulkReferenceCount)
                .Select(index =>
                    _fixture.CreateSchoolReference(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"$.bulkSchools[{index}].schoolReference"
                        ),
                        BulkSchoolReferentialId(index),
                        schoolId: BulkSchoolIdentityBase + index
                    )
                ),
        ];
    }

    public IReadOnlyList<DocumentReference> CreateDeepIdentityDocumentReferences(int? count = null)
    {
        return
        [
            .. Enumerable
                .Range(0, count ?? DeepIdentityReferenceCount)
                .Select(index =>
                    _fixture.CreateWideIdentityReference(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"$.deepIdentities[{index}].wideIdentityReference"
                        ),
                        DeepIdentityReferentialId(index),
                        int64Key: DeepIdentityInt64Key(index).ToString(CultureInfo.InvariantCulture)
                    )
                ),
        ];
    }

    public IReadOnlyList<DescriptorReference> CreateDeepIdentityDescriptorReferences(int? count = null)
    {
        return
        [
            .. Enumerable
                .Range(0, count ?? DescriptorReferenceCount)
                .Select(index =>
                    _fixture.CreateSchoolTypeDescriptorReference(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"$.deepIdentities[{index}].schoolTypeDescriptor"
                        ),
                        DescriptorReferentialId(index),
                        DescriptorUri(index)
                    )
                ),
        ];
    }

    private static long DeepIdentityInt64Key(int index) =>
        ReferenceResolverIntegrationFixture.WideIdentityInt64Value + DeepIdentityInt64KeyOffset + index;

    private static string DescriptorCodeValue(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"Bench{index}");

    private static string DescriptorUri(int index) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"uri://ed-fi.org/SchoolTypeDescriptor#{DescriptorCodeValue(index)}"
        );

    private static ReferentialId BulkSchoolReferentialId(int index) => CreateReferentialId(0x90, index + 1);

    private static ReferentialId DeepIdentityReferentialId(int index) => CreateReferentialId(0x91, index + 1);

    private static ReferentialId DescriptorReferentialId(int index) => CreateReferentialId(0x92, index + 1);

    private static ReferentialId CreateReferentialId(byte prefix, int ordinal) =>
        new(CreateSeedUuid(prefix, ordinal));

    private static Guid CreateSeedUuid(byte prefix, int ordinal) =>
        Guid.Parse(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix:x2}000000-0000-0000-0000-{ordinal:000000000000}"
            )
        );
}
