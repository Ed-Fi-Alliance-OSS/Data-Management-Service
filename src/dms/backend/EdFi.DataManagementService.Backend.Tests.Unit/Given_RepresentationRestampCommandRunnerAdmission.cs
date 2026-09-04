// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RepresentationRestampCommandRunnerAdmission
{
    private static readonly DocumentCacheAdministrativeTargetKey Target = new("tenant", 1);

    [Test]
    public void It_requires_exact_confirmation_and_closed_and_drained_admission()
    {
        DocumentCacheAdministrativeCommandContracts.RequiresOfflineWriterAdmission(
            DocumentCacheAdministrativeCommand.RepresentationRestamp
        ).Should().BeTrue();
        DocumentCacheAdministrativeCommandContracts.ExpectedCommandConfirmation(
            DocumentCacheAdministrativeCommand.RepresentationRestamp
        ).Should().Be(DocumentCacheAdministrativeCommandConfirmation.RepresentationRestamp);

        var admission = new DocumentCacheOfflineWriterAdmission(
            true,
            DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
        );
        DocumentCachePreflightClassifier.ClassifyOfflineWriterAdmission(
            DocumentCacheAdministrativeCommand.RepresentationRestamp,
            Target,
            admission
        ).Should().BeNull();
    }

    [Test]
    public void It_admits_preview_without_confirmation_but_rejects_execute_without_exact_confirmation()
    {
        var preview = new DocumentCacheRepresentationRestampPreviewRequest(
            Target,
            JsonSerializer.Deserialize<DocumentCacheOfflineWriterAdmission>("\"closedAndDrained\"")!,
            DocumentCacheRepresentationRestampMode.Tracking,
            "correction",
            new DocumentCacheRepresentationRestampResourceScope("Ed-Fi", "students")
        );
        DocumentCacheAdministrativeCommandRunnerRequest previewRunner =
            DocumentCacheAdministrativeCommandRunnerRequest.From(preview);
        previewRunner.RequiresCommandConfirmation.Should().BeFalse();
        DocumentCachePreflightClassifier.ClassifyOfflineWriterAdmission(
            previewRunner.Command,
            Target,
            previewRunner.OfflineWriterAdmission
        ).Should().BeNull();

        var execute = new DocumentCacheRepresentationRestampExecuteRequest(Target, Guid.NewGuid(), preview.OfflineWriterAdmission);
        DocumentCachePreflightClassifier.ClassifyCommandConfirmation(
            DocumentCacheAdministrativeCommand.RepresentationRestamp,
            Target,
            execute.Confirmation
        )!.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.MissingCommandConfirmation);
    }

    [Test]
    public void It_rejects_an_unrecognized_admission_acknowledgement()
    {
        Action deserialize = () => JsonSerializer.Deserialize<DocumentCacheOfflineWriterAdmission>("\"otherToken\"");
        deserialize.Should().Throw<JsonException>();
    }

    [Test]
    public void It_binds_preview_and_execute_request_json_and_applies_their_admission_rules()
    {
        const string previewJson = """
            {
              "targetKey": { "tenantKey": "tenant", "dataStoreId": 1 },
              "offlineWriterAdmission": "closedAndDrained",
              "mode": "tracking",
              "reason": "representation correction",
              "scope": { "scopeType": "resource", "projectName": "Ed-Fi", "resourceName": "students" }
            }
            """;
        DocumentCacheRepresentationRestampPreviewRequest preview =
            JsonSerializer.Deserialize<DocumentCacheRepresentationRestampPreviewRequest>(
                previewJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            )!;
        DocumentCacheAdministrativeCommandRunnerRequest previewRunner =
            DocumentCacheAdministrativeCommandRunnerRequest.From(preview);
        previewRunner.RequiresCommandConfirmation.Should().BeFalse();
        previewRunner.AcceptedOfflineWriterAdmissionConfirmation
            .Should()
            .Be(DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained);
        DocumentCachePreflightClassifier.ClassifyOfflineWriterAdmission(
            previewRunner.Command,
            previewRunner.TargetKey,
            previewRunner.OfflineWriterAdmission
        ).Should().BeNull();

        string executeJson = $$"""
            {
              "targetKey": { "tenantKey": "tenant", "dataStoreId": 1 },
              "operationId": "00000000-0000-0000-0000-000000000001",
              "offlineWriterAdmission": "closedAndDrained",
              "confirmation": "representationRestamp"
            }
            """;
        DocumentCacheRepresentationRestampExecuteRequest execute =
            JsonSerializer.Deserialize<DocumentCacheRepresentationRestampExecuteRequest>(
                executeJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            )!;
        DocumentCacheAdministrativeCommandRunnerRequest executeRunner =
            DocumentCacheAdministrativeCommandRunnerRequest.From(execute);
        executeRunner.RequiresCommandConfirmation.Should().BeTrue();
        DocumentCachePreflightClassifier.ClassifyCommandConfirmation(
            executeRunner.Command,
            executeRunner.TargetKey,
            executeRunner.Confirmation
        ).Should().BeNull();
        DocumentCachePreflightClassifier.ClassifyOfflineWriterAdmission(
            executeRunner.Command,
            executeRunner.TargetKey,
            executeRunner.OfflineWriterAdmission
        ).Should().BeNull();

        DocumentCachePreflightClassifier.ClassifyCommandConfirmation(
            executeRunner.Command,
            executeRunner.TargetKey,
            DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
        )!.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.MismatchedCommandConfirmation);
    }
}
