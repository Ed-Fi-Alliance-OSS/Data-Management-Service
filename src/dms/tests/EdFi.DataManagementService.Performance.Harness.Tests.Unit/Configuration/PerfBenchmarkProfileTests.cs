// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

internal static class ProfileTestValues
{
    public const string ReferenceCommit = "df7e23425beef2de46fa98b3b5b48578cb2d3692";

    public static JsonObject Committed() =>
        JsonNode.Parse(File.ReadAllText(PerfBenchmarkProfileLoader.DefaultPath))!.AsObject();

    public static PerfLoadedBenchmarkProfile Load(JsonObject profile) =>
        PerfBenchmarkProfileLoader.Load(Encoding.UTF8.GetBytes(profile.ToJsonString()));

    /// <summary>
    /// The committed profile frozen as task 9 would freeze it: version 1.0.0, a fingerprint
    /// for each provider and every injection factor sized.
    /// </summary>
    public static JsonObject Frozen()
    {
        JsonObject profile = Committed();
        profile["profileVersion"] = "1.0.0";

        foreach (string provider in new[] { "postgresql", "mssql" })
        {
            profile["environment"]!["qualifiedEnvironments"]![provider] = Fingerprint(profile, provider);

            JsonObject factors = profile["detection"]!["injectionFactors"]![provider]!.AsObject();
            foreach (string scenario in factors.Select(factor => factor.Key).ToList())
            {
                factors[scenario] = 0.4m;
            }
        }

        return profile;
    }

    private static JsonObject Fingerprint(JsonObject profile, string provider) =>
        new()
        {
            ["osFamily"] = "Windows",
            ["architecture"] = "x64",
            ["dotnetRuntime"] = "10.0",
            ["machineFingerprint"] = "0f1e2d3c4b5a",
            ["cpuModel"] = "Example CPU",
            ["logicalCores"] = 16,
            ["totalMemoryBytes"] = 34_359_738_368,
            ["powerPlan"] = "High performance",
            ["acPowerOnline"] = true,
            ["dockerVmCpus"] = 8,
            ["dockerVmMemoryBytes"] = 17_179_869_184,
            ["databaseCpuLimit"] = 0,
            ["databaseMemoryLimitBytes"] = 8_589_934_592,
            ["imageDigest"] = "sha256:" + new string('a', 64),
            ["serverVersion"] = "18.6",
            ["databaseSettings"] = new JsonArray(
                new JsonObject { ["name"] = "max_connections", ["value"] = "100" }
            ),
            ["storageMountType"] = "volume",
            ["connection"] = profile["workload"]!["connection"]![provider]!.DeepClone(),
        };
}

[TestFixture]
public class Given_The_Committed_Benchmark_Profile
{
    private PerfLoadedBenchmarkProfile _loaded = null!;
    private PerfBenchmarkProfile _profile = null!;

    [SetUp]
    public void Setup()
    {
        _loaded = PerfBenchmarkProfileLoader.LoadFile(PerfBenchmarkProfileLoader.DefaultPath);
        _profile = _loaded.Profile;
    }

    [Test]
    public void It_hashes_the_exact_file_bytes()
    {
        _loaded
            .Sha256.Should()
            .Be(
                Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(PerfBenchmarkProfileLoader.DefaultPath))
                )
            );
    }

    [Test]
    public void It_declares_the_workload_definition_version_of_the_code()
    {
        _profile.WorkloadDefinitionVersion.Should().Be(PerfBenchmarkScenarios.WorkloadDefinitionVersion);
    }

    [Test]
    public void It_pins_the_approved_reference_as_the_supported_floor()
    {
        _profile.ApprovedReferenceCommit.Should().Be(ProfileTestValues.ReferenceCommit);
        _profile.SupportedRange.FloorCommit.Should().Be(ProfileTestValues.ReferenceCommit);
    }

    [Test]
    public void It_has_a_threshold_for_every_provider_and_scenario()
    {
        _profile.Comparison.P50Thresholds.Keys.Should().BeEquivalentTo("postgresql", "mssql");
        foreach (IReadOnlyDictionary<string, decimal> thresholds in _profile.Comparison.P50Thresholds.Values)
        {
            thresholds.Keys.Should().BeEquivalentTo(PerfBenchmarkScenarios.AllIds);
            thresholds.Values.Should().AllSatisfy(threshold => threshold.Should().Be(0.10m));
        }
    }

    [Test]
    public void It_measures_the_six_paging_cells_and_the_write()
    {
        PerfBenchmarkScenarios
            .AllIds.Should()
            .Equal(
                "traditional-offset-zero/25",
                "traditional-offset-zero/500",
                "traditional-offset-shallow/25",
                "traditional-offset-shallow/500",
                "traditional-offset-deep/25",
                "traditional-offset-deep/500",
                "student-put-collection-update"
            );
    }

    [Test]
    public void It_demonstrates_detection_on_the_write_and_the_zero_offset_small_page()
    {
        _profile
            .Detection.Scenarios.Should()
            .Equal("student-put-collection-update", "traditional-offset-zero/25");
        _profile.Detection.Scenarios.Should().BeSubsetOf(PerfBenchmarkScenarios.AllIds);
    }

    [Test]
    public void It_declares_the_detection_rule_before_any_evidence_run()
    {
        _profile.Detection.TargetIncrease.Should().Be(0.20m);
        _profile.Detection.Tolerance.Should().Be(0.02m);
        _profile.Detection.Margin.Should().Be(0.05m);
        _profile.Detection.CapAboveTarget.Should().Be(0.05m);
        _profile.Detection.RequiredRepeats.Should().Be(3);
    }

    [Test]
    public void It_disables_p95_warnings()
    {
        _profile.Comparison.P95WarningsEnabled.Should().BeFalse();
    }

    [Test]
    public void It_compares_five_pairs_by_the_median_of_pair_p50_ratios()
    {
        _profile.Comparison.Pairs.Should().Be(5);
        _profile.Comparison.Rule.Should().Be(PerfBenchmarkProfileLoader.MedianOfPairP50Ratios);
    }

    [Test]
    public void It_keeps_every_iteration_count_at_or_above_its_floor()
    {
        foreach (
            (PerfBenchmarkIterations reads, PerfBenchmarkIterations writes) in new[]
            {
                (_profile.Workload.Reads, _profile.Workload.Writes),
                (_profile.SmokeWorkload.Reads, _profile.SmokeWorkload.Writes),
            }
        )
        {
            reads
                .WarmupIterations.Should()
                .BeGreaterThanOrEqualTo(PerfRunConfigurationLoader.MinimumWarmupIterations);
            reads
                .MeasuredIterations.Should()
                .BeGreaterThanOrEqualTo(PerfRunConfigurationLoader.MinimumMeasuredIterations);
            writes
                .WarmupIterations.Should()
                .BeGreaterThanOrEqualTo(PerfBenchmarkProfileLoader.MinimumWriteWarmupIterations);
            writes
                .MeasuredIterations.Should()
                .BeGreaterThanOrEqualTo(PerfBenchmarkProfileLoader.MinimumWriteMeasuredIterations);
        }
    }

    [Test]
    public void It_pins_the_evidence_workload()
    {
        _profile.Workload.Fixture.Should().Be(PerfFixtureKind.Primary500k.Id);
        _profile.Workload.DeepOffset.Should().Be(450_000);
        _profile.Workload.Reads.Should().Be(new PerfBenchmarkIterations(50, 60));
        _profile.Workload.Writes.Should().Be(new PerfBenchmarkIterations(1000, 200));
        _profile.Workload.Concurrency.Should().Be(1);
        _profile.Workload.LogLevel.Should().Be("Warning");
    }

    [Test]
    public void It_keeps_the_smoke_deep_offset_within_the_smoke_fixture()
    {
        _profile.SmokeWorkload.Fixture.Should().Be(PerfFixtureKind.Smoke10k.Id);
        _profile
            .SmokeWorkload.DeepOffset.Should()
            .Be(9_000)
            .And.BeLessThanOrEqualTo(PerfRunConfigurationLoader.MaximumDeepOffset(PerfFixtureKind.Smoke10k));
    }

    [Test]
    public void It_sets_the_rehearsal_factor_on_both_demonstration_scenarios()
    {
        _profile
            .SmokeWorkload.InjectionFactors.Should()
            .BeEquivalentTo(
                new Dictionary<string, decimal?>
                {
                    ["student-put-collection-update"] = 1.0m,
                    ["traditional-offset-zero/25"] = 1.0m,
                }
            );
    }

    [Test]
    public void It_pins_the_driver_default_connection_settings()
    {
        _profile
            .Workload.Connection["postgresql"]
            .Should()
            .Be(new PerfBenchmarkConnectionSettings(true, 0, 100, true, 3, 256));
        _profile.Workload.Connection["mssql"].Should().Be(new PerfBenchmarkConnectionSettings(true, 0, 100));
    }

    [Test]
    public void It_requires_the_documented_platform()
    {
        _profile
            .RequiredEnvironment.Should()
            .Be(new PerfBenchmarkRequiredEnvironment("Windows", "x64", true, "volume"));
    }

    [Test]
    public void It_is_not_frozen_and_has_nothing_sized_or_qualified_yet()
    {
        _profile.IsFrozen.Should().BeFalse();
        _profile
            .Environment.QualifiedEnvironments.Values.Should()
            .AllSatisfy(value => value.Should().BeNull());
        _profile
            .Detection.InjectionFactors.Values.SelectMany(factors => factors.Values)
            .Should()
            .AllSatisfy(factor => factor.Should().BeNull());
    }
}

[TestFixture]
public class Given_A_Frozen_Profile_With_Every_Fingerprint_And_Factor
{
    [Test]
    public void It_loads_as_frozen()
    {
        ProfileTestValues.Load(ProfileTestValues.Frozen()).Profile.IsFrozen.Should().BeTrue();
    }
}

[TestFixture]
public class Given_Demonstration_Thresholds_At_The_Margin
{
    [Test]
    public void It_accepts_thirteen_percent()
    {
        JsonObject profile = ProfileTestValues.Committed();
        profile["comparison"]!["p50Thresholds"]!["postgresql"]!["student-put-collection-update"] = 0.13m;
        profile["comparison"]!["p50Thresholds"]!["mssql"]!["traditional-offset-zero/25"] = 0.13m;

        ProfileTestValues
            .Load(profile)
            .Profile.Comparison.P50Thresholds["mssql"]["traditional-offset-zero/25"]
            .Should()
            .Be(0.13m);
    }
}

[TestFixture]
public class Given_A_Smoke_Deep_Offset_At_The_Fixture_Maximum
{
    [Test]
    public void It_accepts_the_last_offset_with_a_full_page()
    {
        JsonObject profile = ProfileTestValues.Committed();
        profile["smokeWorkload"]!["deepOffset"] = 9_500;

        ProfileTestValues.Load(profile).Profile.SmokeWorkload.DeepOffset.Should().Be(9_500);
    }
}

/// <summary>
/// Each derived fixture corrupts one field of an otherwise valid profile and expects the
/// loader to reject it with an error naming that field.
/// </summary>
public abstract class ProfileDefectFixture
{
    protected virtual JsonObject Start() => ProfileTestValues.Committed();

    protected abstract void Corrupt(JsonObject profile);

    protected abstract string ExpectedField { get; }

    [Test]
    public void It_rejects_the_profile_naming_the_field()
    {
        JsonObject profile = Start();
        Corrupt(profile);

        Action load = () => ProfileTestValues.Load(profile);

        load.Should().Throw<PerfConfigurationException>().Which.Message.Should().Contain(ExpectedField);
    }
}

[TestFixture]
public class Given_A_Profile_With_An_Unknown_Member : ProfileDefectFixture
{
    protected override string ExpectedField => "maxConcurrency";

    protected override void Corrupt(JsonObject profile) => profile["workload"]!["maxConcurrency"] = 4;
}

[TestFixture]
public class Given_A_Profile_With_A_Missing_Member : ProfileDefectFixture
{
    protected override string ExpectedField => "pairs";

    protected override void Corrupt(JsonObject profile) => profile["comparison"]!.AsObject().Remove("pairs");
}

[TestFixture]
public class Given_A_Profile_With_A_Missing_Threshold : ProfileDefectFixture
{
    protected override string ExpectedField =>
        "comparison.p50Thresholds.mssql: missing 'traditional-offset-deep/500'";

    protected override void Corrupt(JsonObject profile) =>
        profile["comparison"]!["p50Thresholds"]!["mssql"]!.AsObject().Remove("traditional-offset-deep/500");
}

[TestFixture]
public class Given_A_Profile_With_A_Zero_Threshold : ProfileDefectFixture
{
    protected override string ExpectedField =>
        "comparison.p50Thresholds.postgresql.traditional-offset-deep/500";

    protected override void Corrupt(JsonObject profile) =>
        profile["comparison"]!["p50Thresholds"]!["postgresql"]!["traditional-offset-deep/500"] = 0m;
}

[TestFixture]
public class Given_A_Profile_With_Fewer_Than_Two_Pairs : ProfileDefectFixture
{
    protected override string ExpectedField => "comparison.pairs";

    protected override void Corrupt(JsonObject profile) => profile["comparison"]!["pairs"] = 1;
}

[TestFixture]
public class Given_A_Profile_With_Read_Iterations_Below_The_Floor : ProfileDefectFixture
{
    protected override string ExpectedField => "workload.reads.measuredIterations";

    protected override void Corrupt(JsonObject profile) =>
        profile["workload"]!["reads"]!["measuredIterations"] =
            PerfRunConfigurationLoader.MinimumMeasuredIterations - 1;
}

[TestFixture]
public class Given_A_Profile_With_Write_Iterations_Below_The_Floor : ProfileDefectFixture
{
    protected override string ExpectedField => "smokeWorkload.writes.measuredIterations";

    protected override void Corrupt(JsonObject profile) =>
        profile["smokeWorkload"]!["writes"]!["measuredIterations"] =
            PerfBenchmarkProfileLoader.MinimumWriteMeasuredIterations - 1;
}

[TestFixture]
public class Given_A_Profile_With_An_Unknown_Scenario : ProfileDefectFixture
{
    protected override string ExpectedField => "unknown key 'traditional-offset-middle/25'";

    protected override void Corrupt(JsonObject profile) =>
        profile["comparison"]!["p50Thresholds"]!["postgresql"]!["traditional-offset-middle/25"] = 0.10m;
}

[TestFixture]
public class Given_A_Profile_With_A_Smoke_Deep_Offset_Out_Of_Bounds : ProfileDefectFixture
{
    protected override string ExpectedField => "smokeWorkload.deepOffset";

    protected override void Corrupt(JsonObject profile) =>
        profile["smokeWorkload"]!["deepOffset"] =
            PerfRunConfigurationLoader.MaximumDeepOffset(PerfFixtureKind.Smoke10k) + 1;
}

[TestFixture]
public class Given_A_Profile_With_A_Demonstration_Threshold_Inside_The_Margin : ProfileDefectFixture
{
    protected override string ExpectedField =>
        "comparison.p50Thresholds.postgresql.student-put-collection-update";

    protected override void Corrupt(JsonObject profile) =>
        profile["comparison"]!["p50Thresholds"]!["postgresql"]!["student-put-collection-update"] = 0.14m;
}

[TestFixture]
public class Given_A_Profile_With_A_Workload_Version_Mismatch : ProfileDefectFixture
{
    protected override string ExpectedField => "workloadDefinitionVersion";

    protected override void Corrupt(JsonObject profile) => profile["workloadDefinitionVersion"] = "1.0.1";
}

[TestFixture]
public class Given_A_Profile_With_PostgreSQL_Settings_On_The_Mssql_Connection : ProfileDefectFixture
{
    protected override string ExpectedField => "workload.connection.mssql";

    protected override void Corrupt(JsonObject profile) =>
        profile["workload"]!["connection"]!["mssql"]!["noResetOnClose"] = true;
}

[TestFixture]
public class Given_A_Profile_With_A_Malformed_Fingerprint : ProfileDefectFixture
{
    protected override string ExpectedField => "environment.qualifiedEnvironments.mssql.imageDigest";

    protected override JsonObject Start() => ProfileTestValues.Frozen();

    protected override void Corrupt(JsonObject profile) =>
        profile["environment"]!["qualifiedEnvironments"]!["mssql"]!["imageDigest"] = "latest";
}

[TestFixture]
public class Given_A_Profile_With_A_Fingerprint_Off_The_Pinned_Connection : ProfileDefectFixture
{
    protected override string ExpectedField => "environment.qualifiedEnvironments.postgresql.connection";

    protected override JsonObject Start() => ProfileTestValues.Frozen();

    protected override void Corrupt(JsonObject profile) =>
        profile["environment"]!["qualifiedEnvironments"]!["postgresql"]!["connection"]!["pooling"] = false;
}

[TestFixture]
public class Given_A_Profile_With_A_Fingerprint_On_A_Bind_Mount : ProfileDefectFixture
{
    protected override string ExpectedField =>
        "environment.qualifiedEnvironments.postgresql.storageMountType";

    protected override JsonObject Start() => ProfileTestValues.Frozen();

    protected override void Corrupt(JsonObject profile) =>
        profile["environment"]!["qualifiedEnvironments"]!["postgresql"]!["storageMountType"] = "bind";
}

[TestFixture]
public class Given_A_Frozen_Profile_Missing_A_Fingerprint : ProfileDefectFixture
{
    protected override string ExpectedField => "environment.qualifiedEnvironments.mssql";

    protected override JsonObject Start() => ProfileTestValues.Frozen();

    protected override void Corrupt(JsonObject profile) =>
        profile["environment"]!["qualifiedEnvironments"]!["mssql"] = null;
}

[TestFixture]
public class Given_A_Frozen_Profile_Missing_An_Injection_Factor : ProfileDefectFixture
{
    protected override string ExpectedField =>
        "detection.injectionFactors.postgresql.traditional-offset-zero/25";

    protected override JsonObject Start() => ProfileTestValues.Frozen();

    protected override void Corrupt(JsonObject profile) =>
        profile["detection"]!["injectionFactors"]!["postgresql"]!["traditional-offset-zero/25"] = null;
}

[TestFixture]
public class Given_A_Profile_With_An_Injection_Factor_Above_The_Maximum : ProfileDefectFixture
{
    protected override string ExpectedField => "smokeWorkload.injectionFactors.student-put-collection-update";

    protected override void Corrupt(JsonObject profile) =>
        profile["smokeWorkload"]!["injectionFactors"]!["student-put-collection-update"] =
            PerfBenchmarkProfileLoader.MaximumInjectionFactor + 0.1m;
}

[TestFixture]
public class Given_A_Profile_With_A_Duplicate_Member
{
    [Test]
    public void It_rejects_the_profile_naming_the_member()
    {
        string json = File.ReadAllText(PerfBenchmarkProfileLoader.DefaultPath)
            .Replace("\"pairs\": 5,", "\"pairs\": 5, \"pairs\": 9,", StringComparison.Ordinal);

        Action load = () => PerfBenchmarkProfileLoader.Load(Encoding.UTF8.GetBytes(json));

        load.Should().Throw<PerfConfigurationException>().Which.Message.Should().Contain("pairs");
    }
}
