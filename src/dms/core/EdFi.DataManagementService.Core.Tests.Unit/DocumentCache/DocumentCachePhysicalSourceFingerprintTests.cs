// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
public class DocumentCachePhysicalSourceFingerprintTests
{
    private static readonly Guid _conformanceSourceIdentity = Guid.Parse(
        "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
    );

    [TestCase(
        RelationalProviderToken.PostgresqlValue,
        "sha256:193c47b34d9751c73d06dbf5ccf2655a1cce46154a4808f152d3db0e91b676bc"
    )]
    [TestCase(
        RelationalProviderToken.SqlServerValue,
        "sha256:1780ea8893149195e89a46c70698dfdf64e8e6f9b31c7b7e9a9872baff498d75"
    )]
    public void It_should_compute_the_design_conformance_fingerprint(
        string providerTokenValue,
        string expectedFingerprint
    )
    {
        RelationalProviderToken
            .TryNormalize(providerTokenValue, out RelationalProviderToken? providerToken)
            .Should()
            .BeTrue();

        DocumentCachePhysicalSourceFingerprint fingerprint =
            DocumentCachePhysicalSourceFingerprintCalculator.Compute(
                providerToken!,
                _conformanceSourceIdentity
            );

        fingerprint.Value.Should().Be(expectedFingerprint);
    }

    [Test]
    public void It_should_reject_the_zero_source_identity()
    {
        Action compute = () =>
            _ = DocumentCachePhysicalSourceFingerprintCalculator.Compute(
                RelationalProviderToken.Postgresql,
                Guid.Empty
            );

        compute.Should().Throw<ArgumentException>().WithMessage("*zero UUID*");
    }

    [Test]
    public void It_should_map_successful_reads_to_satisfied_inventory()
    {
        DocumentCachePhysicalSourceFingerprintReadResult result =
            DocumentCachePhysicalSourceFingerprintReadResult.Success(
                new DocumentCachePhysicalSourceFingerprint("sha256:0123456789abcdef")
            );

        result.Succeeded.Should().BeTrue();
        result.Fingerprint!.Value.Should().Be("sha256:0123456789abcdef");
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
    }

    [TestCase(DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentityMissing)]
    [TestCase(DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentitySingletonMissing)]
    public void It_should_map_missing_source_identity_reads_to_missing_inventory(
        DocumentCachePhysicalSourceFingerprintReadStatus status
    )
    {
        DocumentCachePhysicalSourceFingerprintReadResult result =
            DocumentCachePhysicalSourceFingerprintReadResult.Failure(status, "Missing.");

        result.Succeeded.Should().BeFalse();
        result.Fingerprint.Should().BeNull();
        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Missing);
    }

    [TestCase(DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed)]
    [TestCase(DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityAllZero)]
    public void It_should_map_invalid_source_identity_reads_to_invalid_inventory(
        DocumentCachePhysicalSourceFingerprintReadStatus status
    )
    {
        DocumentCachePhysicalSourceFingerprintReadResult result =
            DocumentCachePhysicalSourceFingerprintReadResult.Failure(status, "Invalid.");

        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public void It_should_map_unreadable_source_identity_reads_to_unreadable_inventory()
    {
        DocumentCachePhysicalSourceFingerprintReadResult result =
            DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
                "Unreadable."
            );

        result.ToInventoryValidationResult().Status.Should().Be(DocumentCacheInventoryStatus.Unreadable);
    }

    [Test]
    public void It_should_sanitize_and_bound_failure_messages()
    {
        string unsafeMessage = new string('x', 600) + "\r\n";

        DocumentCachePhysicalSourceFingerprintReadResult result =
            DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed,
                unsafeMessage
            );

        result.Message.Should().HaveLength(512);
        result.Message.Should().NotContain("\r").And.NotContain("\n");
    }

    [Test]
    public void It_should_reject_success_results_without_fingerprints()
    {
        Action create = () =>
            _ = DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded,
                "bad"
            );

        create.Should().Throw<ArgumentException>();
    }
}
