// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
    public class Given_the_generated_name_is_taken_by_an_empty_directory_or_a_file
    {
        private StagerWorkspace _workspace = null!;
        private string _emptyDirectoryPath = null!;
        private string _filePath = null!;
        private Exception? _emptyDirectoryFailure;
        private Exception? _fileFailure;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            DmsOperationId emptyDirectoryOperation = DmsOperationId.New();
            DmsOperationId fileOperation = DmsOperationId.New();
            _emptyDirectoryPath = Path.Combine(
                _workspace.OperationsRoot,
                PrivateOperationDirectory.NamePrefix + emptyDirectoryOperation.Value
            );
            _filePath = Path.Combine(
                _workspace.OperationsRoot,
                PrivateOperationDirectory.NamePrefix + fileOperation.Value
            );
            Directory.CreateDirectory(_emptyDirectoryPath);
            File.WriteAllText(_filePath, "a file, not a directory");

            _emptyDirectoryFailure = Record.Exception(() =>
                PrivateOperationDirectory.Create(_workspace.OperationsRoot, emptyDirectoryOperation).Dispose()
            );
            _fileFailure = Record.Exception(() =>
                PrivateOperationDirectory.Create(_workspace.OperationsRoot, fileOperation).Dispose()
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_refuses_an_empty_directory_rather_than_reusing_it() =>
            _emptyDirectoryFailure.Should().BeOfType<IOException>();

        [Test]
        public void It_leaves_the_empty_directory_in_place() =>
            Directory.Exists(_emptyDirectoryPath).Should().BeTrue();

        [Test]
        public void It_refuses_a_file() => _fileFailure.Should().BeOfType<IOException>();

        [Test]
        public void It_leaves_the_file_in_place() =>
            File.ReadAllText(_filePath).Should().Be("a file, not a directory");
    }

    /// <summary>
    /// The competitor runs at the seam between the root checks and the claim, which is where a separate
    /// existence check would have been raced; the claim itself must refuse without touching what it finds.
    /// </summary>
    [TestFixture]
    public class Given_a_competitor_inserts_the_directory_immediately_before_the_claim
    {
        private StagerWorkspace _workspace = null!;
        private string _sentinelPath = null!;
        private bool _competitorRan;
        private UnixFileMode _competitorMode;
        private UnixFileMode _competitorModeAfterwards;
        private Exception? _failure;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            _sentinelPath = Path.Combine(_workspace.OperationDirectoryPath, "sentinel.txt");
            _competitorMode =
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute;

            _failure = Record.Exception(() =>
                PrivateOperationDirectory
                    .Create(_workspace.OperationsRoot, _workspace.OperationId, beforeClaim: Compete)
                    .Dispose()
            );

            if (!OperatingSystem.IsWindows())
            {
                _competitorModeAfterwards = File.GetUnixFileMode(_workspace.OperationDirectoryPath);
            }
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_ran_the_competitor_at_the_seam() => _competitorRan.Should().BeTrue();

        [Test]
        public void It_refuses() => _failure.Should().BeOfType<IOException>();

        [Test]
        public void It_leaves_the_competitors_contents_untouched() =>
            File.ReadAllText(_sentinelPath).Should().Be("theirs");

        [Test]
        [Platform(Exclude = "Win")]
        public void It_leaves_the_competitors_mode_untouched() =>
            _competitorModeAfterwards.Should().Be(_competitorMode);

        private void Compete()
        {
            Directory.CreateDirectory(_workspace.OperationDirectoryPath);
            File.WriteAllText(_sentinelPath, "theirs");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_workspace.OperationDirectoryPath, _competitorMode);
            }

            _competitorRan = true;
        }
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

    /// <summary>
    /// Windows: the operations root grants Everyone inheritable read, as a shared root may. The claimed directory
    /// is protected from that inheritance and lists only the current user, and the password file written into it
    /// inherits only that entry, so the sentinel credential is readable by no other account.
    /// </summary>
    [TestFixture]
    [Platform(Include = "Win")]
    public class Given_an_operations_root_that_grants_everyone_read_on_windows
    {
        private const string Sentinel = "db:5432:*:dms_admin:SENTINEL-PASSWORD-4be9";

        private StagerWorkspace? _workspace;
        private string _user = string.Empty;
        private string _everyone = string.Empty;
        private string[] _rootIdentities = [];
        private bool _directoryProtected;
        private string[] _directoryIdentities = [];
        private string[] _fileIdentities = [];
        private string _readBack = string.Empty;

        [SupportedOSPlatform("windows")]
        private static string[] IdentitiesOf(FileSystemSecurity security) =>
            [
                .. security
                    .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .Select(rule => rule.IdentityReference.Value)
                    .Distinct(),
            ];

        [SetUp]
        public void Setup()
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Access control lists are read on Windows only.");
                return;
            }

            _workspace = new StagerWorkspace();
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                _user = identity.User!.Value;
            }

            SecurityIdentifier everyone = new(WellKnownSidType.WorldSid, null);
            _everyone = everyone.Value;
            DirectoryInfo root = new(_workspace.OperationsRoot);
            DirectorySecurity rootSecurity = root.GetAccessControl();
            rootSecurity.AddAccessRule(
                new FileSystemAccessRule(
                    everyone,
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
            root.SetAccessControl(rootSecurity);
            _rootIdentities = IdentitiesOf(root.GetAccessControl());

            using PrivateOperationDirectory directory = PrivateOperationDirectory.Create(
                _workspace.OperationsRoot,
                _workspace.OperationId
            );
            string credential = directory.PathOf(PrivateOperationDirectory.CredentialFileName);
            using (FileStream stream = directory.CreateFile(PrivateOperationDirectory.CredentialFileName))
            {
                stream.Write(Encoding.UTF8.GetBytes(Sentinel));
            }

            _readBack = File.ReadAllText(credential);
            DirectorySecurity directorySecurity = new DirectoryInfo(directory.Path).GetAccessControl();
            _directoryProtected = directorySecurity.AreAccessRulesProtected;
            _directoryIdentities = IdentitiesOf(directorySecurity);
            _fileIdentities = IdentitiesOf(new FileInfo(credential).GetAccessControl());
        }

        [TearDown]
        public void TearDown() => _workspace?.Dispose();

        [Test]
        public void It_starts_from_a_root_that_grants_everyone() =>
            _rootIdentities.Should().Contain(_everyone);

        [Test]
        public void It_protects_the_directory_and_lists_only_the_current_user()
        {
            _directoryProtected.Should().BeTrue();
            _directoryIdentities.Should().Equal(_user);
        }

        [Test]
        public void It_gives_the_password_file_to_the_current_user_only()
        {
            _fileIdentities.Should().Equal(_user);
            _readBack.Should().Be(Sentinel);
        }
    }
}
