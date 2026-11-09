// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The fake's signal read on Windows, where the watcher can report the rename of a signal file while the
/// writer (<c>move</c>) still holds it: a read that meets a sharing violation waits for the handle to go
/// and then returns the content, and any other failure is raised at once instead of being retried. The
/// first open attempt runs synchronously within the call, so whether the returned task is already complete
/// tells which of the two happened without depending on any timing.
/// </summary>
[Platform(Include = "Win")]
public class FakePsqlTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    [TestFixture]
    public class Given_a_signal_file_its_writer_still_holds_exclusively
    {
        private const string Content = "PGAPPNAME=dms-provision:signal\r\n";

        private string _directory = null!;
        private bool _completedWhileHeld;
        private string _read = null!;

        [SetUp]
        public async Task Setup()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "dms-1438-signal-read-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(_directory);
            string signal = Path.Combine(_directory, "started");
            await File.WriteAllTextAsync(signal, Content);

            Task<string> read;
            using (new FileStream(signal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                read = FakePsql.ReadSignalAsync(signal, _bound);
                _completedWhileHeld = read.IsCompleted;
            }

            _read = await read.WaitAsync(_bound);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_does_not_complete_while_the_writer_holds_the_file() =>
            _completedWhileHeld.Should().BeFalse();

        [Test]
        public void It_returns_the_whole_content_once_the_writer_lets_go() => _read.Should().Be(Content);
    }

    [TestFixture]
    public class Given_a_signal_file_that_does_not_exist
    {
        private bool _completedWithinTheCall;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            string missing = Path.Combine(
                Path.GetTempPath(),
                "dms-1438-signal-read-" + Guid.NewGuid().ToString("N"),
                "started"
            );
            Task<string> read = FakePsql.ReadSignalAsync(missing, _bound);
            _completedWithinTheCall = read.IsCompleted;
            _exception = await Record.ExceptionAsync(() => read.WaitAsync(_bound));
        }

        [Test]
        public void It_fails_within_the_call_instead_of_retrying() =>
            _completedWithinTheCall.Should().BeTrue();

        [Test]
        public void It_raises_the_failure_as_the_runtime_reported_it() =>
            _exception.Should().BeOfType<DirectoryNotFoundException>();
    }
}
