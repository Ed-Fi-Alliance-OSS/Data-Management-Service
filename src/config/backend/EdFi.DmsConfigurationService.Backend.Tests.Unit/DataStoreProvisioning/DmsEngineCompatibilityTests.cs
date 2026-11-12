// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// D-9 and 6.4 step 2 as one rule (Step 4.9 review): the manifest's major is read from <c>engineVersion</c> as the
/// delivered restore core reads it, an unparsable one is refused, a server accepts a template whose major it
/// reaches, and readiness reports one <c>EngineIncompatible</c> reason per template that needs a newer engine.
/// Readiness, the operation precheck, and target validation agree on every vector (V4-05).
/// </summary>
public class DmsEngineCompatibilityTests
{
    [TestFixture]
    public class Given_manifest_engine_versions
    {
        [TestCase("17.0.900.7", 17)]
        [TestCase("16.8", 16)]
        [TestCase("18", 18)]
        [TestCase("  18.1", 18)]
        [TestCase("\t17.0.900.7", 17)]
        [TestCase("16.8 (Debian 16.8-1.pgdg120+1)", 16)]
        [TestCase("18beta1", 18)]
        [TestCase("018.1", 18)]
        [TestCase("+18.1", null)]
        [TestCase("-18.1", null)]
        [TestCase(".18", null)]
        [TestCase("   ", null)]
        [TestCase("99999999999.0", null)]
        [TestCase("١٨.1", null)]
        [TestCase("latest", null)]
        [TestCase("", null)]
        public void It_reads_the_leading_major_or_refuses(string engineVersion, int? expected) =>
            (DmsEngineCompatibility.TryParseRequiredMajor(engineVersion, out int major) ? major : (int?)null)
                .Should()
                .Be(expected);

        [TestCase(16, "16.8", true)]
        [TestCase(17, "16.8", true)]
        [TestCase(16, "17.0.900.7", false)]
        [TestCase(17, "17.0.900.7", true)]
        [TestCase(99, "latest", false)]
        [TestCase(int.MaxValue, "latest", false)]
        public void It_accepts_a_server_that_reaches_the_manifest_major(
            int serverMajor,
            string engineVersion,
            bool expected
        ) => DmsEngineCompatibility.Accepts(serverMajor, engineVersion).Should().Be(expected);
    }

    /// <summary>
    /// V4-05: each manifest <c>engineVersion</c> goes through readiness (<see cref="DmsEngineCompatibility.Evaluate"/>),
    /// the operation precheck (<see cref="DmsEngineCompatibility.Accepts"/>), and target validation
    /// (<see cref="DmsTargetValidator.Validate"/>, otherwise a passing PostgreSQL or SQL Server target), which must all
    /// reach the expected verdict. Before V4-05 the precheck refused <c>18beta1</c> and accepted <c>+18.1</c>, and
    /// target validation did the reverse.
    /// </summary>
    [TestFixture]
    public class Given_engine_versions_read_by_every_consumer
    {
        [TestCase("postgresql", "18.1", 18, true)]
        [TestCase("postgresql", "18.1", 17, false)]
        [TestCase("postgresql", "18.1", 19, true)]
        [TestCase("postgresql", "  18.1", 18, true)]
        [TestCase("postgresql", "18beta1", 18, true)]
        [TestCase("postgresql", "18beta1", 17, false)]
        [TestCase("postgresql", "16.8 (Debian 16.8-1.pgdg120+1)", 18, true)]
        [TestCase("postgresql", "+18.1", 18, false)]
        [TestCase("postgresql", "-18.1", 18, false)]
        [TestCase("postgresql", "latest", 18, false)]
        [TestCase("postgresql", " ", 18, false)]
        [TestCase("postgresql", "99999999999.0", 18, false)]
        [TestCase("mssql", "17.0.900.7", 17, true)]
        [TestCase("mssql", "17.0.900.7", 16, false)]
        [TestCase("mssql", "\t17.0.900.7", 17, true)]
        [TestCase("mssql", "17.0.900.7-CU1", 17, true)]
        [TestCase("mssql", "+17.0.900.7", 17, false)]
        public void It_reaches_the_same_verdict_on_every_path(
            string engine,
            string engineVersion,
            int serverMajor,
            bool accepted
        )
        {
            bool readiness =
                DmsEngineCompatibility
                    .Evaluate(
                        serverMajor,
                        [new TemplateEngineRequirement(DmsDatabaseTemplate.Minimal, engineVersion)]
                    )
                    .Count == 0;
            bool precheck = DmsEngineCompatibility.Accepts(serverMajor, engineVersion);
            DmsTargetValidation validation = Validate(engine, engineVersion, serverMajor);

            (readiness, precheck, validation)
                .Should()
                .Be(
                    (
                        accepted,
                        accepted,
                        accepted
                            ? new DmsTargetValidation.Valid()
                            : new DmsTargetValidation.Invalid(
                                DmsProvisioningFailureCode.TargetIncompatible,
                                DmsTargetValidator.EngineMajorCheck
                            )
                    )
                );
        }

        /// <summary>A target that passes every other check, so only the engine major can refuse it.</summary>
        private static DmsTargetValidation Validate(string engine, string engineVersion, int serverMajor)
        {
            bool mssql = engine == "mssql";
            DmsTargetFacts facts = (
                mssql ? TargetFactsSample.MssqlFacts() : TargetFactsSample.PostgresqlFacts()
            ) with
            {
                EngineMajorVersion = serverMajor,
            };
            RestoreManifest manifest = mssql
                ? TargetFactsSample.ManifestFor(
                    facts,
                    databaseEngine: RestoreManifest.MssqlEngine,
                    engineVersion: engineVersion,
                    databaseCompatibilityLevel: 170
                )
                : TargetFactsSample.ManifestFor(facts, engineVersion: engineVersion);

            return DmsTargetValidator.Validate(
                facts,
                TargetFactsSample.Identity(),
                manifest,
                TargetFactsSample.Projects,
                new DmsTargetValidationRule.Staging(Guid.Parse("2c7e9d10-8f4a-4b6c-9e1d-3a5b7c9d1e2f"))
            );
        }
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
