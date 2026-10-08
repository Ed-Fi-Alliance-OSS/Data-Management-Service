// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcOverlapAssertions
{
    private CdcProjectionWork _work = null!;
    private CdcCacheDocument _cache = null!;

    [SetUp]
    public void Setup()
    {
        var now = DateTimeOffset.UtcNow;
        _work = new(12, now, now);
        _cache = new(Guid.NewGuid(), 11, "etag", now, "Ed-Fi", "Student", "5.2.0", new());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_accepts_retained_newer_work_with_absent_or_older_cache(bool olderCache) =>
        CdcOverlapAssertions.AssertPendingNewerWork(11, 12, [_work], olderCache ? [_cache] : []);

    [Test]
    public void It_rejects_the_older_attempt_acknowledging_newer_work()
    {
        Action act = () => CdcOverlapAssertions.AssertPendingNewerWork(11, 12, [], []);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_work_that_did_not_advance_to_the_committed_source_version()
    {
        Action act = () =>
            CdcOverlapAssertions.AssertPendingNewerWork(
                11,
                12,
                [_work with { RequiredContentVersion = 11 }],
                []
            );
        act.Should().Throw<AssertionException>();
    }

    [TestCase(12)]
    [TestCase(13)]
    public void It_rejects_cache_that_advanced_before_the_next_attempt_was_released(long cacheVersion)
    {
        Action act = () =>
            CdcOverlapAssertions.AssertPendingNewerWork(
                11,
                12,
                [_work],
                [_cache with { ContentVersion = cacheVersion }]
            );
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_a_candidate_materialized_after_the_newer_commit()
    {
        Action act = () => CdcOverlapAssertions.AssertPendingNewerWork(12, 12, [_work], []);
        act.Should().Throw<AssertionException>();
    }
}
