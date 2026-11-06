// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

public class PrivateOperationDirectoryTests
{
    [TestFixture]
    public class Given_a_directory_created_for_an_operation
    {
        private StagerWorkspace _workspace = null!;
        private PrivateOperationDirectory _directory = null!;
        private string _createdFilePath = null!;
        private Exception? _secondCreateFailure;
        private bool _deleted;
        private bool _deletedAgain;
        private Exception? _createAfterDeleteFailure;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            _directory = PrivateOperationDirectory.Create(_workspace.OperationsRoot, _workspace.OperationId);

            using (FileStream file = _directory.CreateFile("first.bin"))
            {
                file.Write([1, 2, 3]);
                _createdFilePath = file.Name;
            }

            _secondCreateFailure = Record.Exception(() => _directory.CreateFile("first.bin").Dispose());

            _deleted = _directory.TryDelete();
            _deletedAgain = _directory.TryDelete();
            _createAfterDeleteFailure = Record.Exception(() => _directory.CreateFile("late.bin").Dispose());
        }

        [TearDown]
        public void TearDown()
        {
            _directory.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_is_named_from_the_operation_id() =>
            _directory.Path.Should().Be(_workspace.OperationDirectoryPath);

        [Test]
        public void It_creates_files_inside_itself() =>
            _createdFilePath.Should().Be(Path.Combine(_directory.Path, "first.bin"));

        [Test]
        public void It_refuses_to_replace_an_existing_file() =>
            _secondCreateFailure.Should().BeOfType<IOException>();

        [Test]
        public void It_deletes_itself_with_its_contents()
        {
            _deleted.Should().BeTrue();
            Directory.Exists(_directory.Path).Should().BeFalse();
        }

        [Test]
        public void It_reports_a_repeated_deletion_as_done() => _deletedAgain.Should().BeTrue();

        [Test]
        public void It_refuses_to_create_files_after_deletion() =>
            _createAfterDeleteFailure.Should().BeOfType<ObjectDisposedException>();
    }

    [TestFixture]
    public class Given_the_generated_name_is_already_taken
    {
        private StagerWorkspace _workspace = null!;
        private string _sentinelPath = null!;
        private Exception? _failure;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            Directory.CreateDirectory(_workspace.OperationDirectoryPath);
            _sentinelPath = Path.Combine(_workspace.OperationDirectoryPath, "sentinel.txt");
            File.WriteAllText(_sentinelPath, "not ours");

            _failure = Record.Exception(() =>
                PrivateOperationDirectory.Create(_workspace.OperationsRoot, _workspace.OperationId).Dispose()
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses() => _failure.Should().BeOfType<IOException>();

        [Test]
        public void It_leaves_the_existing_directory_untouched() =>
            File.ReadAllText(_sentinelPath).Should().Be("not ours");
    }

    [TestFixture]
    public class Given_an_unusable_root
    {
        private Exception? _relativeRootFailure;
        private Exception? _missingRootFailure;

        [SetUp]
        public void Setup()
        {
            DmsOperationId operationId = DmsOperationId.New();
            _relativeRootFailure = Record.Exception(() =>
                PrivateOperationDirectory.Create("relative/root", operationId).Dispose()
            );
            _missingRootFailure = Record.Exception(() =>
                PrivateOperationDirectory
                    .Create(
                        Path.Combine(Path.GetTempPath(), "dms1438-missing-" + Guid.NewGuid()),
                        operationId
                    )
                    .Dispose()
            );
        }

        [Test]
        public void It_refuses_a_relative_root() =>
            _relativeRootFailure.Should().BeOfType<ArgumentException>();

        [Test]
        public void It_refuses_a_missing_root() =>
            _missingRootFailure.Should().BeOfType<DirectoryNotFoundException>();
    }

    [TestFixture]
    [Platform(Exclude = "Win")]
    public class Given_a_directory_on_a_unix_file_system
    {
        private StagerWorkspace _workspace = null!;
        private UnixFileMode _directoryMode;
        private UnixFileMode _fileMode;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            using PrivateOperationDirectory directory = PrivateOperationDirectory.Create(
                _workspace.OperationsRoot,
                _workspace.OperationId
            );
            directory.CreateFile("secret.bin").Dispose();

            if (!OperatingSystem.IsWindows())
            {
                _directoryMode = File.GetUnixFileMode(directory.Path);
                _fileMode = File.GetUnixFileMode(directory.PathOf("secret.bin"));
            }
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_is_mode_0700() =>
            _directoryMode.Should().Be(PrivateOperationDirectory.PrivateDirectoryMode);

        [Test]
        public void It_creates_files_with_mode_0600() =>
            _fileMode.Should().Be(PrivateOperationDirectory.PrivateFileMode);
    }
}
