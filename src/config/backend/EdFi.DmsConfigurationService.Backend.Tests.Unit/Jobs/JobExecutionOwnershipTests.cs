// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobExecutionOwnershipTests
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(5);

    [TestFixture]
    public class Given_a_new_execution
    {
        private JobExecutionOwnership _ownership = null!;
        private bool _admittedImmediately;

        [SetUp]
        public async Task Setup()
        {
            _ownership = new JobExecutionOwnership();
            Task<IDisposable> entry = _ownership.EnterAsync(CancellationToken.None);
            _admittedImmediately = entry.IsCompletedSuccessfully;
            (await entry).Dispose();
        }

        [Test]
        public void It_starts_owned_without_a_reason()
        {
            _ownership.State.Should().Be(JobOwnershipState.Owned);
            _ownership.Reason.Should().BeNull();
        }

        [Test]
        public void It_admits_the_first_entrant_immediately() => _admittedImmediately.Should().BeTrue();
    }

    [TestFixture]
    public class Given_ownership_lost_before_finalization
    {
        private JobExecutionOwnership _ownership = null!;
        private bool _markedUncertain;
        private bool _laterUncertainty;
        private bool _beganFinalizing;

        [SetUp]
        public void Setup()
        {
            _ownership = new JobExecutionOwnership();
            _markedUncertain = _ownership.TryMarkUncertain("RenewalOwnershipLost");
            _laterUncertainty = _ownership.TryMarkUncertain("RenewalTimeout");
            _beganFinalizing = _ownership.TryBeginFinalizing();
        }

        [Test]
        public void It_refuses_to_begin_finalizing()
        {
            _markedUncertain.Should().BeTrue();
            _beganFinalizing.Should().BeFalse();
            _ownership.State.Should().Be(JobOwnershipState.Uncertain);
        }

        [Test]
        public void It_keeps_the_first_reason()
        {
            _laterUncertainty.Should().BeFalse();
            _ownership.Reason.Should().Be("RenewalOwnershipLost");
        }
    }

    [TestFixture]
    public class Given_an_outcome_write_whose_result_is_unknown
    {
        private JobExecutionOwnership _ownership = null!;
        private bool _beganFinalizing;
        private bool _markedUncertain;
        private bool _markedFinalized;

        [SetUp]
        public void Setup()
        {
            _ownership = new JobExecutionOwnership();
            _beganFinalizing = _ownership.TryBeginFinalizing();
            _markedUncertain = _ownership.TryMarkUncertain("WriteOutcomeUnknown");
            _markedFinalized = _ownership.TryMarkFinalized();
        }

        [Test]
        public void It_moves_from_finalizing_to_uncertain_and_never_to_finalized()
        {
            _beganFinalizing.Should().BeTrue();
            _markedUncertain.Should().BeTrue();
            _markedFinalized.Should().BeFalse();
            _ownership.State.Should().Be(JobOwnershipState.Uncertain);
            _ownership.Reason.Should().Be("WriteOutcomeUnknown");
        }
    }

    [TestFixture]
    public class Given_a_committed_outcome_write
    {
        private JobExecutionOwnership _ownership = null!;
        private bool _markedFinalized;
        private bool _laterUncertainty;
        private bool _secondFinalization;

        [SetUp]
        public void Setup()
        {
            _ownership = new JobExecutionOwnership();
            _ownership.TryBeginFinalizing();
            _markedFinalized = _ownership.TryMarkFinalized();
            _laterUncertainty = _ownership.TryMarkUncertain("RenewalTimeout");
            _secondFinalization = _ownership.TryBeginFinalizing();
        }

        [Test]
        public void It_stays_finalized()
        {
            _markedFinalized.Should().BeTrue();
            _laterUncertainty.Should().BeFalse();
            _secondFinalization.Should().BeFalse();
            _ownership.State.Should().Be(JobOwnershipState.Finalized);
            _ownership.Reason.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_a_fence_waiting_when_a_renewal_arrives
    {
        private readonly List<string> _admissions = [];
        private bool _fenceWaitedForRenewal;

        [SetUp]
        public async Task Setup()
        {
            JobExecutionOwnership ownership = new();
            IDisposable firstFence = await ownership.EnterAsync(CancellationToken.None);

            Task<IDisposable> waitingFence = ownership.EnterAsync(CancellationToken.None);
            Task<IDisposable> renewal = ownership.EnterForRenewalAsync(CancellationToken.None);

            firstFence.Dispose();
            IDisposable renewalEntry = await renewal.WaitAsync(_patience);
            _admissions.Add("renewal");
            _fenceWaitedForRenewal = !waitingFence.IsCompleted;

            renewalEntry.Dispose();
            (await waitingFence.WaitAsync(_patience)).Dispose();
            _admissions.Add("fence");
        }

        [Test]
        public void It_admits_the_renewal_before_the_earlier_fence() =>
            _admissions.Should().Equal("renewal", "fence");

        [Test]
        public void It_keeps_the_fence_waiting_while_the_renewal_holds_the_gate() =>
            _fenceWaitedForRenewal.Should().BeTrue();
    }

    [TestFixture]
    public class Given_successive_fences_and_a_renewal
    {
        private readonly List<string> _admissions = [];

        [SetUp]
        public async Task Setup()
        {
            JobExecutionOwnership ownership = new();
            IDisposable holder = await ownership.EnterAsync(CancellationToken.None);

            Task<IDisposable>[] fences =
            [
                .. Enumerable.Range(0, 3).Select(_ => ownership.EnterAsync(CancellationToken.None)),
            ];
            Task<IDisposable> renewal = ownership.EnterForRenewalAsync(CancellationToken.None);

            holder.Dispose();
            List<Task<IDisposable>> pending = [.. fences, renewal];
            while (pending.Count > 0)
            {
                Task<IDisposable> admitted = await Task.WhenAny(pending).WaitAsync(_patience);
                pending.Remove(admitted);
                _admissions.Add(admitted == renewal ? "renewal" : $"fence{Array.IndexOf(fences, admitted)}");
                (await admitted).Dispose();
            }
        }

        [Test]
        public void It_admits_the_renewal_first_and_the_fences_in_arrival_order() =>
            _admissions.Should().Equal("renewal", "fence0", "fence1", "fence2");
    }

    [TestFixture]
    public class Given_a_waiter_cancelled_before_it_is_admitted
    {
        private bool _renewalCancelled;
        private bool _fenceAdmitted;

        [SetUp]
        public async Task Setup()
        {
            JobExecutionOwnership ownership = new();
            IDisposable holder = await ownership.EnterAsync(CancellationToken.None);

            using CancellationTokenSource cancellation = new();
            Task<IDisposable> renewal = ownership.EnterForRenewalAsync(cancellation.Token);
            Task<IDisposable> fence = ownership.EnterAsync(CancellationToken.None);

            await cancellation.CancelAsync();
            _renewalCancelled = renewal.IsCanceled;
            holder.Dispose();

            IDisposable fenceEntry = await fence.WaitAsync(_patience);
            _fenceAdmitted = true;
            fenceEntry.Dispose();
        }

        [Test]
        public void It_cancels_the_waiter() => _renewalCancelled.Should().BeTrue();

        [Test]
        public void It_hands_the_gate_to_the_next_waiter() => _fenceAdmitted.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_entry_released_twice
    {
        private bool _thirdEntrantWaited;

        [SetUp]
        public async Task Setup()
        {
            JobExecutionOwnership ownership = new();
            IDisposable first = await ownership.EnterAsync(CancellationToken.None);
            Task<IDisposable> second = ownership.EnterAsync(CancellationToken.None);

            first.Dispose();
            IDisposable secondEntry = await second.WaitAsync(_patience);
#pragma warning disable S3966 // The second Dispose is the behavior under test: it must not release the gate again.
            first.Dispose();
#pragma warning restore S3966

            Task<IDisposable> third = ownership.EnterAsync(CancellationToken.None);
            _thirdEntrantWaited = !third.IsCompleted;

            secondEntry.Dispose();
            (await third.WaitAsync(_patience)).Dispose();
        }

        [Test]
        public void It_releases_the_gate_only_once() => _thirdEntrantWaited.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_already_cancelled_token
    {
        private bool _entryCancelled;

        [SetUp]
        public void Setup() =>
            _entryCancelled = new JobExecutionOwnership().EnterAsync(new CancellationToken(true)).IsCanceled;

        [Test]
        public void It_does_not_enter_the_gate() => _entryCancelled.Should().BeTrue();
    }
}
