// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The process plumbing's promise on the unconfirmed path: once the run has returned, a descendant that
/// still holds the output pipes reaches no consumer any more, and the reading end it holds is really
/// released, which the descendant observes as a failed write.
/// </summary>
public class PsqlProcessTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    [TestFixture]
    public class Given_a_process_that_exits_while_a_child_keeps_writing_to_its_pipes
    {
        private static readonly TimeSpan _terminationBound = TimeSpan.FromSeconds(2);

        private FakePsql _fake = null!;
        private PsqlProcess.Completion _completion = null!;
        private int _chunks;
        private int _chunksAtReturn;
        private int _chunksAfterTheChildSawThePipeClose;

        [SetUp]
        public async Task Setup()
        {
            string signal = Path.Combine(
                Path.GetTempPath(),
                "dms-1438-process-signal-" + Guid.NewGuid().ToString("N")
            );
            _fake = FakePsql.HoldingPipeAfterExit(signal);
            ProcessStartInfo startInfo = PsqlProcess.CreateStartInfo(_fake.ExecutablePath, _fake.Directory);
            LibpqEnvironment.Apply(
                startInfo,
                connection: null,
                passwordFilePath: null,
                applicationName: null
            );

            _completion = await PsqlProcess
                .RunAsync(
                    startInfo,
                    consumeStandardOutput: null,
                    _ => Interlocked.Increment(ref _chunks),
                    _terminationBound,
                    TimeProvider.System,
                    CancellationToken.None
                )
                .WaitAsync(_bound);
            _chunksAtReturn = Volatile.Read(ref _chunks);

            // The child keeps writing to standard error on every poll until a write fails, which it records
            // as the closed marker: that marker is the evidence that the reading end was released after the
            // run returned, and every write up to it reached the pipe after the run returned.
            await FakePsql.WaitForFileAsync(_fake.ClosedMarkerPath, _bound);
            _chunksAfterTheChildSawThePipeClose = Volatile.Read(ref _chunks);
            File.Delete(signal);
        }

        [TearDown]
        public async Task TearDown() => await _fake.DisposeAsync();

        [Test]
        public void It_reports_the_termination_unconfirmed_with_the_observed_exit_code()
        {
            _completion.Termination.Should().Be(PsqlProcess.Termination.Unconfirmed);
            _completion.ExitCode.Should().Be(0);
        }

        [Test]
        public void It_released_the_reading_end_so_the_child_saw_its_write_fail() =>
            File.Exists(_fake.ClosedMarkerPath).Should().BeTrue();

        [Test]
        public void It_delivers_nothing_to_the_consumer_after_it_returned() =>
            _chunksAfterTheChildSawThePipeClose.Should().Be(_chunksAtReturn);
    }
}
