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
[Category("CdcSourcePartitionHash")]
public class Given_CdcSourcePartitionHash
{
    [TestCase("edfi.dms", "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40")]
    public void It_computes_the_design_postgresql_source_partition_hash_vector(
        string topicPrefix,
        string expectedHash
    )
    {
        CdcSourcePartitionHashResult result = CdcSourcePartitionHashCalculator.ComputePostgresql(topicPrefix);

        result.Succeeded.Should().BeTrue();
        result.Hash.Should().Be(expectedHash);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("EdFi_DMS_CDC", "sha256:678792175a93a7e810f3904d8d8e42e654289b147c3313a5c6d6a5c6593beab2")]
    [TestCase("EdFi \"DMS\"\\CDC", "sha256:588192bb6f07725229bc478dcdec4761cbc362edcaebe304428c386bf6cfb90b")]
    [TestCase("EdFi <DMS>&CDC", "sha256:dbeeade9fcb65353dce0b01f950778bff722b3933cf4f46680de46ae1839ed27")]
    public void It_computes_the_design_sql_server_source_partition_hash_vectors(
        string rawCatalogName,
        string expectedHash
    )
    {
        CdcSourcePartitionHashResult result = CdcSourcePartitionHashCalculator.ComputeSqlServer(
            "edfi.dms",
            rawCatalogName
        );

        result.Succeeded.Should().BeTrue();
        result.Hash.Should().Be(expectedHash);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_returns_sanitized_diagnostics_for_invalid_inputs()
    {
        CdcSourcePartitionHashResult result = CdcSourcePartitionHashCalculator.Compute(
            (CdcProvider)999,
            "../not-safe",
            "Secret_Database"
        );
        CdcSourcePartitionHashResult sqlServerResult = CdcSourcePartitionHashCalculator.ComputeSqlServer(
            "edfi.dms",
            null
        );

        result.Succeeded.Should().BeFalse();
        result.Hash.Should().BeNull();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidEnumValue)
            .And.Contain(CdcDiagnosticCategory.MalformedPayload);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("Secret_Database"));
        sqlServerResult.Succeeded.Should().BeFalse();
        sqlServerResult
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MissingRequiredField);
    }
}
