// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class CacheClaimSetsTaskTests
{
    private static AppSettings AppSettingsWith(bool multiTenancy) =>
        new() { AllowIdentityUpdateOverrides = string.Empty, MultiTenancy = multiTenancy };

    private static CacheClaimSetsTask CreateTask(
        IClaimSetProvider claimSetProvider,
        IDataStoreProvider dataStoreProvider,
        bool multiTenancy
    ) =>
        CreateTask(
            claimSetProvider,
            dataStoreProvider,
            multiTenancy,
            NullLogger<CacheClaimSetsTask>.Instance
        );

    private static CacheClaimSetsTask CreateTask(
        IClaimSetProvider claimSetProvider,
        IDataStoreProvider dataStoreProvider,
        bool multiTenancy,
        ILogger<CacheClaimSetsTask> logger
    ) => new(claimSetProvider, dataStoreProvider, Options.Create(AppSettingsWith(multiTenancy)), logger);

    [TestFixture]
    public class Given_Default_Construction : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _task = CreateTask(
                A.Fake<IClaimSetProvider>(),
                A.Fake<IDataStoreProvider>(),
                multiTenancy: false
            );
        }

        [Test]
        public void It_has_order_410()
        {
            _task.Order.Should().Be(410);
        }

        [Test]
        public void It_has_expected_name()
        {
            _task.Name.Should().Be("Cache Claim Sets");
        }
    }

    [TestFixture]
    public class Given_Single_Tenant_Mode : CacheClaimSetsTaskTests
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private IDataStoreProvider _dataStoreProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _dataStoreProvider = A.Fake<IDataStoreProvider>();

            var task = CreateTask(_claimSetProvider, _dataStoreProvider, multiTenancy: false);
            await task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_calls_get_all_claim_sets_once_with_no_tenant()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(null, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_does_not_load_tenants()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Multi_Tenant_Mode_With_Multiple_Tenants : CacheClaimSetsTaskTests
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private IDataStoreProvider _dataStoreProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .Returns<IList<string>>(["tenant-a", "tenant-b"]);

            var task = CreateTask(_claimSetProvider, _dataStoreProvider, multiTenancy: true);
            await task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_loads_tenants_once()
        {
            A.CallTo(() => _dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_calls_get_all_claim_sets_for_each_tenant_in_order()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets("tenant-a", A<CancellationToken>._))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() => _claimSetProvider.GetAllClaimSets("tenant-b", A<CancellationToken>._))
                        .MustHaveHappenedOnceExactly()
                );
        }

        [Test]
        public void It_does_not_call_no_tenant_overload()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(null, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_LoadTenants_Throws : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;
        private IClaimSetProvider _claimSetProvider = null!;
        private RecordingLogger<CacheClaimSetsTask> _logger = null!;

        [SetUp]
        public void Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("tenant load failed"));

            _logger = new RecordingLogger<CacheClaimSetsTask>();
            _task = CreateTask(_claimSetProvider, dataStoreProvider, multiTenancy: true, _logger);
        }

        [Test]
        public async Task It_logs_the_failure_at_critical()
        {
            await _task.ExecuteAsync(CancellationToken.None);

            _logger
                .Records.Should()
                .ContainSingle(record => record.Level == LogLevel.Critical)
                .Which.Exception.Should()
                .BeOfType<InvalidOperationException>();
        }

        [Test]
        public async Task It_does_not_throw()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task It_does_not_call_get_all_claim_sets()
        {
            await _task.ExecuteAsync(CancellationToken.None);

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_GetAllClaimSets_Throws : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;

        [SetUp]
        public void Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException("claim set fetch failed"));

            _task = CreateTask(claimSetProvider, A.Fake<IDataStoreProvider>(), multiTenancy: false);
        }

        [Test]
        public async Task It_does_not_throw()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    public class Given_A_PreCanceled_Token : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public void Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _task = CreateTask(_claimSetProvider, A.Fake<IDataStoreProvider>(), multiTenancy: false);
        }

        [Test]
        public async Task It_propagates_OperationCanceledException()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            Func<Task> act = async () => await _task.ExecuteAsync(cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public async Task It_does_not_call_get_all_claim_sets()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                await _task.ExecuteAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_GetAllClaimSets_Throws_OperationCanceledException_With_Uncanceled_Token
        : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;

        [SetUp]
        public void Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .ThrowsAsync(new TaskCanceledException("dependency canceled (e.g. HttpClient timeout)"));

            _task = CreateTask(claimSetProvider, A.Fake<IDataStoreProvider>(), multiTenancy: false);
        }

        [Test]
        public async Task It_does_not_throw()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    public class Given_LoadTenants_Throws_OperationCanceledException_With_Uncanceled_Token
        : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public void Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.LoadTenants(A<CancellationToken>._))
                .ThrowsAsync(new TaskCanceledException("dependency canceled (e.g. HttpClient timeout)"));

            _task = CreateTask(_claimSetProvider, dataStoreProvider, multiTenancy: true);
        }

        [Test]
        public async Task It_does_not_throw()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task It_does_not_call_get_all_claim_sets()
        {
            await _task.ExecuteAsync(CancellationToken.None);

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }
}
