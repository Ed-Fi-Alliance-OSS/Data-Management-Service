// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcRestartObservations
{
    private CdcRestartObservations _observations = null!;
    private ServiceProvider _provider = null!;
    private RecordingPager _inner = null!;
    private IDocumentProjectionWorkPager _pager = null!;
    private IDocumentCacheAdministrativePrimitives _primitives = null!;
    private IDocumentCacheAdministrativePrimitives _innerPrimitives = null!;
    private DocumentProjectionWorkPageRequest _request = null!;
    private Task<DocumentProjectionWorkPage> _held = null!;
    private CancellationTokenSource _cancellation = null!;

    [SetUp]
    public async Task Setup()
    {
        _observations = new(TimeSpan.FromSeconds(2));
        _innerPrimitives = A.Fake<IDocumentCacheAdministrativePrimitives>();
        ServiceCollection services = new();
        services.AddOptions<DocumentCacheOptions>();
        services.AddSingleton<IDocumentProjectionWorkPager, RecordingPager>();
        services.AddSingleton<IDocumentCacheAdministrativePrimitives>(_ => _innerPrimitives);
        _observations.ConfigureServices(services);
        _provider = services.BuildServiceProvider();
        _pager = _provider.GetRequiredService<IDocumentProjectionWorkPager>();
        _inner = _provider.GetRequiredService<RecordingPager>();
        _primitives = _provider.GetRequiredService<IDocumentCacheAdministrativePrimitives>();
        _request = Request();
        _cancellation = new();
        await _pager.ReadPageAsync(_request);
        _held = _pager.ReadPageAsync(_request, _cancellation.Token);
        await _observations.WaitUntilHeldAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task Cleanup()
    {
        await _observations.DisposeAsync();
        try
        {
            await _held;
        }
        catch (OperationCanceledException)
        { /* held deadline or disposal */
        }
        await _provider.DisposeAsync();
        _held.Dispose();
        _cancellation.Dispose();
    }

    [Test]
    public async Task It_holds_a_real_second_page_and_returns_the_same_provider_page_on_release()
    {
        _provider
            .GetRequiredService<IOptions<DocumentCacheOptions>>()
            .Value.Projector.PageSize.Should()
            .Be(2);
        _pager.ProviderToken.Should().Be(_inner.ProviderToken);
        _inner.Requests.Should().Equal(_request, _request);
        _held.IsCompleted.Should().BeFalse();
        var snapshot = _observations.Snapshot();
        snapshot
            .Pages.Select(p => p.Items.Select(i => i.DocumentId))
            .Should()
            .BeEquivalentTo(new[] { new long[] { 1, 2 }, new long[] { 3, 4 } });
        snapshot
            .Pages.SelectMany(p => p.Items)
            .Select(i => i.RequiredContentVersion)
            .Should()
            .Equal(10, 20, 30, 40);
        _observations.Release();
        (await _held).Should().BeSameAs(_inner.Pages[1]);
        (await _pager.ReadPageAsync(_request)).Should().BeSameAs(_inner.Pages[2]);
    }

    [TestCase("cancel")]
    [TestCase("dispose")]
    [TestCase("timeout")]
    public async Task It_cancels_held_operations_and_awaits_them_on_disposal(string mode)
    {
        if (mode == "cancel")
        {
            await _cancellation.CancelAsync();
        }
        if (mode == "dispose")
        {
            await _observations.DisposeAsync();
        }
        Func<Task> wait = async () => await _held.WaitAsync(TimeSpan.FromSeconds(3));
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_counts_only_completed_page_reads_and_propagates_provider_failure()
    {
        _observations.Release();
        await _held;
        _inner.Fail = true;
        Func<Task> read = () => _pager.ReadPageAsync(_request);
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider failure");
        _observations.Snapshot().Pages.Should().HaveCount(2);
    }

    [Test]
    public async Task It_bounds_payload_free_page_observations_and_reports_truncation()
    {
        _observations.Release();
        await _held;
        for (int index = 0; index < 70; index++)
        {
            await _pager.ReadPageAsync(_request);
        }
        var snapshot = _observations.Snapshot();
        snapshot.Pages.Should().HaveCount(64);
        snapshot.DroppedPages.Should().Be(8);
    }

    [Test]
    public async Task It_does_not_treat_empty_cursor_wrap_reads_as_recovery_pages()
    {
        _observations.Release();
        await _held;
        _inner.Empty = true;
        (await _pager.ReadPageAsync(_request)).IsEmpty.Should().BeTrue();
        _observations.Snapshot().Pages.Should().HaveCount(2);
    }

    [Test]
    public async Task It_disposes_while_the_real_provider_operation_is_in_flight()
    {
        _observations.Release();
        await _held;
        _inner.Block = true;
        var reading = _pager.ReadPageAsync(_request);
        await _inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await _observations.DisposeAsync();
        _inner.Exited.Should().BeTrue();
        Func<Task> wait = async () => await reading;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        _observations.Snapshot().Pages.Should().HaveCount(2);
    }

    [Test]
    public async Task It_records_actual_baseline_and_inventory_entry_calls_even_when_the_delegate_fails()
    {
        var session = A.Fake<IRelationalWriteSession>();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        A.CallTo(() => _innerPrimitives.CaptureBaselineBoundaryAsync(session, token))
            .Throws(new InvalidOperationException());
        A.CallTo(() =>
                _innerPrimitives.SeedBaselinePageAsync(
                    session,
                    A<DocumentCacheAdministrativeBaselineSeedPageRequest>._,
                    token
                )
            )
            .Throws(new InvalidOperationException());
        A.CallTo(() =>
                _innerPrimitives.ScrubPageAsync(
                    session,
                    A<DocumentCacheAdministrativeScrubPageRequest>._,
                    token
                )
            )
            .Throws(new InvalidOperationException());
        Func<Task> boundary = () => _primitives.CaptureBaselineBoundaryAsync(session, token);
        Func<Task> seed = () => _primitives.SeedBaselinePageAsync(session, null!, token);
        Func<Task> scrub = () => _primitives.ScrubPageAsync(session, null!, token);
        await boundary.Should().ThrowAsync<InvalidOperationException>();
        await seed.Should().ThrowAsync<InvalidOperationException>();
        await scrub.Should().ThrowAsync<InvalidOperationException>();
        var snapshot = _observations.Snapshot();
        snapshot.BaselineBoundaries.Should().Be(1);
        snapshot.BaselinePages.Should().Be(1);
        snapshot.InventoryPages.Should().Be(1);
        await using var otherLifetime = new CdcRestartObservations(TimeSpan.FromSeconds(2));
        otherLifetime.Snapshot().Should().BeEquivalentTo(new CdcRestartSnapshot([], 0, 0, 0, 0));
    }

    [Test]
    public async Task It_delegates_lifecycle_reads_without_classifying_bounded_status_probes_as_inventory()
    {
        var session = A.Fake<IRelationalWriteSession>();
        var result = new DocumentCacheGuardedNewEmptyActivationState(false, false, false, "test");
        A.CallTo(() =>
                _innerPrimitives.ReadGuardedNewEmptyActivationStateAsync(session, CancellationToken.None)
            )
            .Returns(result);
        (await _primitives.ReadGuardedNewEmptyActivationStateAsync(session)).Should().BeSameAs(result);
        var snapshot = _observations.Snapshot();
        snapshot.InventoryPages.Should().Be(0);
        snapshot.BaselineBoundaries.Should().Be(0);
        snapshot.BaselinePages.Should().Be(0);
    }

    private sealed class RecordingPager : IDocumentProjectionWorkPager
    {
        public RecordingPager() { }

        public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;
        public List<DocumentProjectionWorkPageRequest> Requests { get; } = [];
        public List<DocumentProjectionWorkPage> Pages { get; } = [];
        public bool Fail { get; set; }
        public bool Empty { get; set; }
        public bool Block { get; set; }
        public bool Exited { get; private set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (Fail)
            {
                throw new InvalidOperationException("provider failure");
            }
            if (Block)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Exited = true;
                }
            }
            int start = Pages.Count * 2 + 1;
            var now = DateTimeOffset.UtcNow;
            DocumentProjectionWorkPage page = new(
                Empty ? [] : [new(start, start * 10, now, now), new(start + 1, (start + 1) * 10, now, now)],
                request.PageSize
            );
            Pages.Add(page);
            return page;
        }
    }

    private static DocumentProjectionWorkPageRequest Request()
    {
        var target = DocumentCacheTargetKey.Create("Tenant", 1);
        return new(
            new DocumentCacheTargetExecutionContext(
                target,
                new(1),
                new(
                    false,
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromSeconds(1),
                    2,
                    1,
                    TimeSpan.FromSeconds(5),
                    1000,
                    TimeSpan.FromHours(1)
                ),
                new(target.DataStoreId, "postgresql"),
                new(RelationalProviderToken.Postgresql, "connection"),
                new("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
                new(DocumentCacheLifecycleState.Tracking, false),
                new(DocumentCacheInventoryStatus.Satisfied, "test"),
                new(DocumentCacheEnqueueTriggerStatus.Satisfied, "test"),
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            ),
            new()
        );
    }
}

[TestFixture]
public class Given_CdcRestartRecoveryAssertions
{
    private CdcRestartSnapshot _snapshot = null!;
    private readonly long[] _ids = [1, 2, 3, 4, 5, 6, 7];

    [SetUp]
    public void Setup() =>
        _snapshot = new(
            [
                new(2, [new(1, 10), new(2, 20)]),
                new(2, [new(3, 30), new(4, 40)]),
                new(2, [new(5, 50), new(6, 60)]),
                new(2, [new(7, 70)]),
            ],
            0,
            0,
            0,
            0
        );

    [Test]
    public void It_accepts_multiple_real_pages_covering_every_retained_key() =>
        CdcRestartAssertions.AssertRecovery(_snapshot, _ids);

    [TestCase("baseline-boundary")]
    [TestCase("baseline-page")]
    [TestCase("inventory")]
    [TestCase("truncated")]
    [TestCase("missing-key")]
    [TestCase("large-page")]
    public void It_rejects_scans_truncation_missing_work_and_wrong_page_size(string defect)
    {
        _snapshot = defect switch
        {
            "baseline-boundary" => _snapshot with { BaselineBoundaries = 1 },
            "baseline-page" => _snapshot with { BaselinePages = 1 },
            "inventory" => _snapshot with { InventoryPages = 1 },
            "truncated" => _snapshot with { DroppedPages = 1 },
            "missing-key" => _snapshot with { Pages = _snapshot.Pages.Take(3).ToArray() },
            _ => _snapshot with { Pages = _snapshot.Pages.Select(p => p with { PageSize = 100 }).ToArray() },
        };
        Action assert = () => CdcRestartAssertions.AssertRecovery(_snapshot, _ids);
        assert.Should().Throw<AssertionException>();
    }
}
