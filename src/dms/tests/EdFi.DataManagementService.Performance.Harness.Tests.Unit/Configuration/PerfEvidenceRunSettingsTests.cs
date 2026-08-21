// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

internal static class EvidenceSettingsTestValues
{
    public const string Digest = "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0";

    public static Dictionary<string, string?> Valid() =>
        new()
        {
            [PerfEnvironmentVariables.ImageTag] = "postgres:16.8-alpine",
            [PerfEnvironmentVariables.ImageDigest] = Digest,
            [PerfEnvironmentVariables.StorageNote] = "local docker volume, not tmpfs",
        };

    public static Func<string, string?> ReaderFor(Dictionary<string, string?> values) =>
        name => values.TryGetValue(name, out string? value) ? value : null;
}

[TestFixture]
public class Given_Valid_Evidence_Settings
{
    private PerfEvidenceRunSettings _settings = null!;

    [SetUp]
    public void Setup()
    {
        _settings = PerfEvidenceRunSettings.Load(
            EvidenceSettingsTestValues.ReaderFor(EvidenceSettingsTestValues.Valid())
        );
    }

    [Test]
    public void It_parses_the_image_pin()
    {
        _settings.ImageTag.Should().Be("postgres:16.8-alpine");
        _settings.ImageDigest.Should().Be(EvidenceSettingsTestValues.Digest);
    }

    [Test]
    public void It_refuses_ci_by_default()
    {
        _settings.AllowCi.Should().BeFalse();
    }

    [Test]
    public void It_defaults_the_dirty_allowlist_to_the_overlay()
    {
        _settings.AllowedDirtyPrefixes.Should().Equal(PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix);
    }
}

[TestFixture]
public class Given_Missing_Evidence_Settings
{
    [Test]
    public void It_reports_every_required_value()
    {
        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfEvidenceRunSettings.Load(_ => null)
        );
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ImageTag} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.ImageDigest} is required.");
        exception.Errors.Should().Contain($"{PerfEnvironmentVariables.StorageNote} is required.");
    }
}

[TestFixture]
public class Given_A_Malformed_Image_Digest
{
    [Test]
    public void It_rejects_the_digest()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.ImageDigest] = "sha256:not-hex";
        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfEvidenceRunSettings.Load(EvidenceSettingsTestValues.ReaderFor(values))
        );
        exception.Errors.Should().ContainSingle(error => error.Contains("sha256:<64 lowercase hex>"));
    }
}

[TestFixture]
public class Given_Explicit_Guardrail_Overrides
{
    [Test]
    public void It_parses_allow_ci_strictly()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.AllowCi] = "true";
        PerfEvidenceRunSettings.Load(EvidenceSettingsTestValues.ReaderFor(values)).AllowCi.Should().BeTrue();

        values[PerfEnvironmentVariables.AllowCi] = "yes";
        FluentActions
            .Invoking(() => PerfEvidenceRunSettings.Load(EvidenceSettingsTestValues.ReaderFor(values)))
            .Should()
            .Throw<PerfConfigurationException>();
    }

    [Test]
    public void It_splits_custom_dirty_prefixes()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.AllowedDirtyPrefixes] = "src/a; src/b";
        PerfEvidenceRunSettings
            .Load(EvidenceSettingsTestValues.ReaderFor(values))
            .AllowedDirtyPrefixes.Should()
            .Equal("src/a", "src/b");
    }

    [Test]
    public void It_rejects_empty_prefix_entries()
    {
        Dictionary<string, string?> values = EvidenceSettingsTestValues.Valid();
        values[PerfEnvironmentVariables.AllowedDirtyPrefixes] = "src/a;";
        PerfConfigurationException exception = Assert.Throws<PerfConfigurationException>(() =>
            PerfEvidenceRunSettings.Load(EvidenceSettingsTestValues.ReaderFor(values))
        );
        exception.Errors.Should().ContainSingle(error => error.Contains("empty entries"));
    }

    [Test]
    public void It_never_enables_allow_any_from_the_environment()
    {
        PerfEvidenceRunSettings
            .Load(EvidenceSettingsTestValues.ReaderFor(EvidenceSettingsTestValues.Valid()))
            .AllowAnyDirtyPath.Should()
            .BeFalse();
    }
}
