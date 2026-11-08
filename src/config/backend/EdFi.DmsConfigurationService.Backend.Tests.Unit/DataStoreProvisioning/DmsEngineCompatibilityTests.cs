// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// D-9 and 6.4 step 2 as one rule (Step 4.9 review): the manifest's major is the leading integer of
/// <c>engineVersion</c>, an unparsable one is never reached, a server accepts a template whose major it reaches,
/// and readiness reports one <c>EngineIncompatible</c> reason per template that needs a newer engine.
/// </summary>
public class DmsEngineCompatibilityTests
{
    [TestFixture]
    public class Given_manifest_engine_versions
    {
        [TestCase("17.0.900.7", 17)]
        [TestCase("16.8", 16)]
        [TestCase("18", 18)]
        [TestCase("latest", int.MaxValue)]
        [TestCase("", int.MaxValue)]
        public void It_reads_the_leading_major(string engineVersion, int expected) =>
            DmsEngineCompatibility.RequiredMajorOf(engineVersion).Should().Be(expected);

        [TestCase(16, "16.8", true)]
        [TestCase(17, "16.8", true)]
        [TestCase(16, "17.0.900.7", false)]
        [TestCase(17, "17.0.900.7", true)]
        [TestCase(99, "latest", false)]
        public void It_accepts_a_server_that_reaches_the_manifest_major(
            int serverMajor,
            string engineVersion,
            bool expected
        ) => DmsEngineCompatibility.Accepts(serverMajor, engineVersion).Should().Be(expected);
    }

    [TestFixture]
    public class Given_authenticated_templates_and_a_server_major
    {
        private static DmsReadinessReason Incompatible(DmsDatabaseTemplate template) =>
            new(
                DmsProvisioningFailureCode.EngineIncompatible,
                "EngineCompatibility." + template.WireValue,
                DmsProvisioningDiagnostic.ForOperation(
                    "EngineCompatibility." + template.WireValue + ".ServerBelowManifest"
                )
            );

        private static readonly TemplateEngineRequirement[] _sql2025Templates =
        [
            new(DmsDatabaseTemplate.Minimal, "17.0.900.7"),
            new(DmsDatabaseTemplate.Sample, "17.0.900.7"),
        ];

        [Test]
        public void It_reports_every_template_a_sql_server_2022_cannot_restore() =>
            DmsEngineCompatibility
                .Evaluate(16, _sql2025Templates)
                .Should()
                .Equal(Incompatible(DmsDatabaseTemplate.Minimal), Incompatible(DmsDatabaseTemplate.Sample));

        [Test]
        public void It_accepts_a_server_of_the_templates_major() =>
            DmsEngineCompatibility.Evaluate(17, _sql2025Templates).Should().BeEmpty();

        [Test]
        public void It_reports_only_the_template_that_needs_a_newer_engine() =>
            DmsEngineCompatibility
                .Evaluate(
                    16,
                    [
                        new(DmsDatabaseTemplate.Minimal, "16.0.4135.4"),
                        new(DmsDatabaseTemplate.Sample, "17.0.900.7"),
                    ]
                )
                .Should()
                .Equal(Incompatible(DmsDatabaseTemplate.Sample));

        [Test]
        public void It_reports_nothing_without_authenticated_templates() =>
            DmsEngineCompatibility.Evaluate(16, []).Should().BeEmpty();
    }
}
