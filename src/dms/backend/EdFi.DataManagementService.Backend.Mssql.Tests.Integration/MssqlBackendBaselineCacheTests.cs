// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlBackendBaselineCache
{
    [Test]
    public async Task It_reuses_the_baseline_for_the_same_fixture_signature_and_generated_ddl()
    {
        var fixtureSignature = $"{nameof(Given_MssqlBackendBaselineCache)}#reuse#{Guid.NewGuid():N}";
        const string GeneratedDdl = "CREATE TABLE [edfi].[School] ([SchoolId] int NOT NULL);";

        var firstBaseline = await MssqlBackendBaselineCache.CreateOrGetAsync(fixtureSignature, GeneratedDdl);
        var secondBaseline = await MssqlBackendBaselineCache.CreateOrGetAsync(fixtureSignature, GeneratedDdl);

        secondBaseline.Should().BeSameAs(firstBaseline);
    }

    [Test]
    public async Task It_rejects_the_same_fixture_signature_with_different_generated_ddl()
    {
        var fixtureSignature = $"{nameof(Given_MssqlBackendBaselineCache)}#hash-mismatch#{Guid.NewGuid():N}";
        const string OriginalGeneratedDdl = "CREATE TABLE [edfi].[School] ([SchoolId] int NOT NULL);";
        const string ChangedGeneratedDdl = "CREATE TABLE [edfi].[School] ([SchoolId] bigint NOT NULL);";

        await MssqlBackendBaselineCache.CreateOrGetAsync(fixtureSignature, OriginalGeneratedDdl);

        Func<Task> act = async () =>
            await MssqlBackendBaselineCache.CreateOrGetAsync(fixtureSignature, ChangedGeneratedDdl);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
