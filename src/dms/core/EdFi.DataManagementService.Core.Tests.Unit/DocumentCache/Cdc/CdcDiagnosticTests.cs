// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcDiagnostic")]
public class Given_CdcDiagnostic
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    [Test]
    public void It_serializes_the_required_lower_camel_shape_without_legacy_path()
    {
        CdcDiagnosticContract contract = new(
            CdcJsonContract.CurrentContractVersion,
            [
                new(
                    "providerMismatch",
                    CdcDiagnosticCategory.ProviderMismatch,
                    CdcDiagnosticSeverity.Error,
                    CdcDiagnosticComponent.ObservationValidation,
                    SampleObservedAt,
                    "CDC provider mismatch.\r\n<payload>",
                    false,
                    artifactKind: "connector\nname",
                    artifactName: "server:edfi;pwd:secret",
                    expected: "sqlServer",
                    observed: "postgresql"
                ),
            ]
        );

        JsonObject diagnostic = JsonNode.Parse(CdcJsonContract.Serialize(contract))!["diagnostics"]![
            0
        ]!.AsObject();

        diagnostic["code"]!.GetValue<string>().Should().Be("providerMismatch");
        diagnostic["category"]!.GetValue<string>().Should().Be("providerMismatch");
        diagnostic["severity"]!.GetValue<string>().Should().Be("error");
        diagnostic["component"]!.GetValue<string>().Should().Be("observationValidation");
        diagnostic["observedAt"]!.GetValue<DateTimeOffset>().Should().Be(SampleObservedAt);
        diagnostic["retryable"]!.GetValue<bool>().Should().BeFalse();
        diagnostic["artifactKind"]!.GetValue<string>().Should().Be("connectorname");
        diagnostic["artifactName"]!.GetValue<string>().Should().Be("redacted");
        diagnostic["expected"]!.GetValue<string>().Should().Be("sqlServer");
        diagnostic["observed"]!.GetValue<string>().Should().Be("postgresql");
        diagnostic.Should().NotContainKey("path");
        diagnostic["message"]!.GetValue<string>().Should().NotContain("\r").And.NotContain("\n");
    }

    [Test]
    public void It_bounds_sanitized_text_fields()
    {
        CdcDiagnostic diagnostic = new(
            "invalidObservation",
            CdcDiagnosticCategory.InvalidObservation,
            CdcDiagnosticSeverity.Error,
            CdcDiagnosticComponent.ObservationValidation,
            SampleObservedAt,
            new string('m', CdcDiagnostic.MaximumMessageLength + 20),
            false,
            expected: new string('e', CdcDiagnostic.MaximumTextLength + 20),
            observed: new string('o', CdcDiagnostic.MaximumTextLength + 20)
        );

        diagnostic.Message.Should().HaveLength(CdcDiagnostic.MaximumMessageLength);
        diagnostic.Expected.Should().HaveLength(CdcDiagnostic.MaximumTextLength);
        diagnostic.Observed.Should().HaveLength(CdcDiagnostic.MaximumTextLength);
    }

    [Test]
    public void It_orders_caps_and_appends_a_truncation_diagnostic()
    {
        CdcDiagnostic[] diagnostics =
        [
            .. Enumerable
                .Range(0, 18)
                .Select(index => new CdcDiagnostic(
                    $"code{index:00}",
                    CdcDiagnosticCategory.InvalidObservation,
                    CdcDiagnosticSeverity.Error,
                    index == 17 ? CdcDiagnosticComponent.Binding : CdcDiagnosticComponent.Lag,
                    SampleObservedAt.AddSeconds(index),
                    $"diagnostic {index}",
                    false,
                    artifactKind: $"kind{17 - index:00}",
                    artifactName: $"artifact{index:00}"
                )),
        ];

        IReadOnlyList<CdcDiagnostic> normalized = CdcDiagnostic.NormalizeDiagnostics(diagnostics);

        normalized.Should().HaveCount(CdcDiagnostic.MaximumDiagnostics);
        normalized[0].Component.Should().Be(CdcDiagnosticComponent.Binding);
        normalized[^1].Category.Should().Be(CdcDiagnosticCategory.DiagnosticsTruncated);
        normalized[^1].Observed.Should().Be("3");
    }

    [Test]
    public void It_keeps_legacy_paths_available_but_out_of_json()
    {
        CdcDiagnostic diagnostic = new(
            CdcDiagnosticCategory.MissingRequiredField,
            "$.targetIdentity.provider",
            "Missing required field `provider`."
        );

        diagnostic.Path.Should().Be("$.targetIdentity.provider");

        JsonObject root = JsonNode
            .Parse(
                CdcJsonContract.Serialize(
                    new CdcDiagnosticContract(CdcJsonContract.CurrentContractVersion, [diagnostic])
                )
            )!
            .AsObject();
        root["diagnostics"]![0]!.AsObject().Should().NotContainKey("path");
    }

    private sealed record CdcDiagnosticContract(
        [property: JsonRequired] int ContractVersion,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    ) : ICdcJsonContract;
}
