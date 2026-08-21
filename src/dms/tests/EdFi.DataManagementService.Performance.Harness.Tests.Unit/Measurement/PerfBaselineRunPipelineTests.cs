// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Dirty_Worktree_Guardrail
{
    [Test]
    public void It_accepts_paths_under_an_allowed_prefix()
    {
        FluentActions
            .Invoking(() =>
                PerfBaselineRunPipeline.GuardDirtyPaths(
                    [
                        "src/dms/tests/EdFi.DataManagementService.Performance.Harness/Foo.cs",
                        "src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/Bar.cs",
                    ],
                    [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix]
                )
            )
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_rejects_a_path_outside_the_allowlist()
    {
        FluentActions
            .Invoking(() =>
                PerfBaselineRunPipeline.GuardDirtyPaths(
                    ["src/dms/backend/EdFi.DataManagementService.Backend/Repository.cs"],
                    [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix]
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*dirty outside the approved overlay*Repository.cs*");
    }

    [Test]
    public void It_accepts_a_clean_tree()
    {
        FluentActions
            .Invoking(() =>
                PerfBaselineRunPipeline.GuardDirtyPaths(
                    [],
                    [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix]
                )
            )
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_allows_everything_for_the_empty_prefix()
    {
        FluentActions
            .Invoking(() => PerfBaselineRunPipeline.GuardDirtyPaths(["anything/at/all.cs"], [""]))
            .Should()
            .NotThrow();
    }
}

[TestFixture]
public class Given_The_Ci_Guardrail
{
    [Test]
    public void It_refuses_ci_by_default()
    {
        FluentActions
            .Invoking(() => PerfBaselineRunPipeline.GuardCiEnvironment(allowCi: false, "true"))
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*tmpfs*");
    }

    [Test]
    public void It_allows_ci_only_when_explicitly_permitted()
    {
        FluentActions
            .Invoking(() => PerfBaselineRunPipeline.GuardCiEnvironment(allowCi: true, "true"))
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_passes_off_ci()
    {
        FluentActions
            .Invoking(() => PerfBaselineRunPipeline.GuardCiEnvironment(allowCi: false, null))
            .Should()
            .NotThrow();
    }
}
