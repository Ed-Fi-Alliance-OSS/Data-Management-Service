// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DocumentCachePrerequisite")]
public class Given_A_Postgresql_DocumentCachePrerequisite_Validator
{
    private readonly PostgresqlDocumentCacheProviderPrerequisiteValidator _validator = new();

    [Test]
    public void It_reports_the_postgresql_provider_token()
    {
        _validator.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
    }

    [Test]
    public async Task It_reports_sqlserver_prerequisites_as_not_applicable_for_initialization()
    {
        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateInitializationAsync(
                "unused",
                new DocumentCacheLifecycleObservation(
                    DocumentCacheLifecycleState.Tracking,
                    CacheAheadRecoveryRequired: false
                )
            );

        result.IsSatisfied.Should().BeTrue();
        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
    }

    [Test]
    public async Task It_reports_sqlserver_prerequisites_as_not_applicable_for_activation_preflight()
    {
        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateActivationPreflightAsync("unused");

        result.IsSatisfied.Should().BeTrue();
        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
    }
}
