// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("PhysicalSourceFingerprint")]
public class Given_CdcPhysicalSourceFingerprintCalculator
{
    private static readonly Guid SourceIdentity = Guid.Parse("f81d4fae-7dec-11d0-a765-00a0c91e6bf6");

    [TestCase(
        CdcProvider.Postgresql,
        "sha256:193c47b34d9751c73d06dbf5ccf2655a1cce46154a4808f152d3db0e91b676bc"
    )]
    [TestCase(
        CdcProvider.SqlServer,
        "sha256:1780ea8893149195e89a46c70698dfdf64e8e6f9b31c7b7e9a9872baff498d75"
    )]
    public void It_computes_the_design_physical_source_fingerprint_vectors(
        CdcProvider provider,
        string expectedFingerprint
    )
    {
        CdcPhysicalSourceFingerprintResult result = CdcPhysicalSourceFingerprintCalculator.Compute(
            provider,
            SourceIdentity
        );

        result.Succeeded.Should().BeTrue();
        result.Fingerprint.Should().Be(expectedFingerprint);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_returns_typed_diagnostics_for_expected_invalid_inputs()
    {
        CdcPhysicalSourceFingerprintResult result = CdcPhysicalSourceFingerprintCalculator.Compute(
            (CdcProvider)999,
            Guid.Empty
        );

        result.Succeeded.Should().BeFalse();
        result.Fingerprint.Should().BeNull();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Path == "$.provider"
                && diagnostic.Category == CdcDiagnosticCategory.InvalidEnumValue
            );
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Path == "$.sourceIdentity"
                && diagnostic.Category == CdcDiagnosticCategory.MalformedPayload
            );
    }
}
