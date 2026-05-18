// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
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
        IDmsInstanceProvider dmsInstanceProvider,
        bool multiTenancy
    ) =>
        new(
            claimSetProvider,
            dmsInstanceProvider,
            Options.Create(AppSettingsWith(multiTenancy)),
            NullLogger<CacheClaimSetsTask>.Instance
        );

    [TestFixture]
    public class Given_Default_Construction : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;

        [SetUp]
        public void Setup()
        {
            _task = CreateTask(
                A.Fake<IClaimSetProvider>(),
                A.Fake<IDmsInstanceProvider>(),
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
        private IDmsInstanceProvider _dmsInstanceProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();

            var task = CreateTask(_claimSetProvider, _dmsInstanceProvider, multiTenancy: false);
            await task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_calls_get_all_claim_sets_once_with_no_tenant()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(null)).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_does_not_load_tenants()
        {
            A.CallTo(() => _dmsInstanceProvider.LoadTenants()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_Multi_Tenant_Mode_With_Multiple_Tenants : CacheClaimSetsTaskTests
    {
        private IClaimSetProvider _claimSetProvider = null!;
        private IDmsInstanceProvider _dmsInstanceProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            A.CallTo(() => _dmsInstanceProvider.LoadTenants())
                .Returns<IList<string>>(["tenant-a", "tenant-b"]);

            var task = CreateTask(_claimSetProvider, _dmsInstanceProvider, multiTenancy: true);
            await task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_loads_tenants_once()
        {
            A.CallTo(() => _dmsInstanceProvider.LoadTenants()).MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_calls_get_all_claim_sets_for_each_tenant_in_order()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets("tenant-a"))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() => _claimSetProvider.GetAllClaimSets("tenant-b"))
                        .MustHaveHappenedOnceExactly()
                );
        }

        [Test]
        public void It_does_not_call_no_tenant_overload()
        {
            A.CallTo(() => _claimSetProvider.GetAllClaimSets(null)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_LoadTenants_Throws : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;
        private IClaimSetProvider _claimSetProvider = null!;

        [SetUp]
        public void Setup()
        {
            _claimSetProvider = A.Fake<IClaimSetProvider>();
            var dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            A.CallTo(() => dmsInstanceProvider.LoadTenants())
                .ThrowsAsync(new InvalidOperationException("tenant load failed"));

            _task = CreateTask(_claimSetProvider, dmsInstanceProvider, multiTenancy: true);
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

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._)).MustNotHaveHappened();
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
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._))
                .ThrowsAsync(new InvalidOperationException("claim set fetch failed"));

            _task = CreateTask(claimSetProvider, A.Fake<IDmsInstanceProvider>(), multiTenancy: false);
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
            _task = CreateTask(_claimSetProvider, A.Fake<IDmsInstanceProvider>(), multiTenancy: false);
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

            A.CallTo(() => _claimSetProvider.GetAllClaimSets(A<string?>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_A_Dependency_Throws_OperationCanceledException : CacheClaimSetsTaskTests
    {
        private CacheClaimSetsTask _task = null!;

        [SetUp]
        public void Setup()
        {
            var claimSetProvider = A.Fake<IClaimSetProvider>();
            A.CallTo(() => claimSetProvider.GetAllClaimSets(A<string?>._))
                .ThrowsAsync(new OperationCanceledException("dependency canceled"));

            _task = CreateTask(claimSetProvider, A.Fake<IDmsInstanceProvider>(), multiTenancy: false);
        }

        [Test]
        public async Task It_does_not_swallow_the_cancellation()
        {
            Func<Task> act = async () => await _task.ExecuteAsync(CancellationToken.None);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
