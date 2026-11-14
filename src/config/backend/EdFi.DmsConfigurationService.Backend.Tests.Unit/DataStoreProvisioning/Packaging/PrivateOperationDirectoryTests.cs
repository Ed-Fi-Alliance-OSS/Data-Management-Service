// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;
using FluentAssertions;
using FluentAssertions.Execution;

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

    /// <summary>
    /// CAR-001: the claim succeeded, so the directory is this operation's own when securing it fails. A file-system
    /// failure deletes it and carries the owner out in <see cref="OperationDirectorySetupException"/>; any other
    /// failure deletes it and propagates unchanged.
    /// </summary>
    [TestFixture]
    public class Given_securing_the_claimed_directory_fails
    {
        private StagerWorkspace _workspace = null!;
        private IOException _injected = null!;
        private Exception? _failure;
        private bool _directoryExistedAtTheSeam;
        private Exception? _otherFailure;
        private string _otherPath = null!;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            _injected = new IOException("injected");
            _failure = Record.Exception(() =>
                PrivateOperationDirectory
                    .Create(
                        _workspace.OperationsRoot,
                        _workspace.OperationId,
                        beforeClaim: null,
                        afterClaim: path =>
                        {
                            _directoryExistedAtTheSeam = Directory.Exists(path);
                            throw _injected;
                        }
                    )
                    .Dispose()
            );

            DmsOperationId other = DmsOperationId.New();
            _otherPath = Path.Combine(
                _workspace.OperationsRoot,
                PrivateOperationDirectory.NamePrefix + other.Value
            );
            _otherFailure = Record.Exception(() =>
                PrivateOperationDirectory
                    .Create(
                        _workspace.OperationsRoot,
                        other,
                        beforeClaim: null,
                        afterClaim: _ => throw new InvalidOperationException("injected")
                    )
                    .Dispose()
            );
        }

        [TearDown]
        public void TearDown() => _workspace.Dispose();

        [Test]
        public void It_ran_the_seam_after_the_claim() => _directoryExistedAtTheSeam.Should().BeTrue();

        [Test]
        public void It_carries_the_owner_and_the_original_failure()
        {
            OperationDirectorySetupException setup = _failure
                .Should()
                .BeOfType<OperationDirectorySetupException>()
                .Subject;
            using AssertionScope scope = new();
            setup.InnerException.Should().BeSameAs(_injected);
            setup.Directory.Path.Should().Be(_workspace.OperationDirectoryPath);
            setup.Directory.OperationId.Should().Be(_workspace.OperationId);
        }

        [Test]
        public void It_deletes_the_directory_it_created() =>
            Directory.Exists(_workspace.OperationDirectoryPath).Should().BeFalse();

        [Test]
        public void It_propagates_any_other_failure_unchanged() =>
            _otherFailure.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_deletes_the_directory_after_any_other_failure() =>
            Directory.Exists(_otherPath).Should().BeFalse();
    }

    /// <summary>
    /// CAR-001: securing the claimed directory fails and its deletion is blocked. The owner still leaves in the
    /// exception, its path is the directory that remains, and the catalog's residue accounting reports it.
    /// A sentinel is written before the trap so the recursive deletion must unlink an entry: removing an empty
    /// directory needs write permission on its parent only, which the Unix trap leaves in place.
    /// </summary>
    [TestFixture]
    public class Given_securing_the_claimed_directory_fails_and_its_deletion_is_blocked
    {
        private StagerWorkspace _workspace = null!;
        private DeletionTrap? _trap;
        private OperationDirectorySetupException? _failure;
        private bool _deletedWhileBlocked;
        private IReadOnlyList<DmsCleanupResidue> _residue = [];

        [SetUp]
        public void Setup()
        {
            if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            {
                Assert.Ignore("A privileged process may unlink in a read-only directory.");
            }

            _workspace = new StagerWorkspace();
            _failure =
                Record.Exception(() =>
                    PrivateOperationDirectory.Create(
                        _workspace.OperationsRoot,
                        _workspace.OperationId,
                        beforeClaim: null,
                        afterClaim: path =>
                        {
                            File.WriteAllText(Path.Combine(path, "sentinel"), "");
                            _trap = new DeletionTrap(path);
                            throw new IOException("injected");
                        }
                    )
                ) as OperationDirectorySetupException;
            if (_failure is not null)
            {
                _deletedWhileBlocked = _failure.Directory.TryDelete();
                _residue = TemplateArtifactCatalog.ResidueUnder(_failure.Directory.Path);
            }
        }

        [TearDown]
        public void TearDown()
        {
            _trap?.Dispose();
            _failure?.Directory.Dispose();
            _workspace?.Dispose();
        }

        [Test]
        public void It_carries_the_owner_out() => _failure.Should().NotBeNull();

        [Test]
        public void It_leaves_the_directory_while_blocked()
        {
            _deletedWhileBlocked.Should().BeFalse();
            Directory.Exists(_workspace.OperationDirectoryPath).Should().BeTrue();
        }

        [Test]
        public void It_accounts_for_the_remaining_directory_as_residue() =>
            _residue
                .Should()
                .Equal(
                    new DmsCleanupResidue(
                        DmsResidueKind.OperationDirectory,
                        PrivateOperationDirectory.NamePrefix + _workspace.OperationId.Value
                    )
                );
    }

    /// <summary>
    /// CAR-001: <c>CreateNew</c> succeeded, so the file is this directory's own when securing it fails. Its handle is
    /// disposed before the exception leaves, without relying on finalization; the file is deleted, and the name can
    /// be created again.
    /// </summary>
    [TestFixture]
    public class Given_securing_a_created_file_fails
    {
        private StagerWorkspace _workspace = null!;
        private PrivateOperationDirectory _directory = null!;
        private IOException _injected = null!;
        private FileStream? _seen;
        private Exception? _failure;
        private Exception? _recreateFailure;
        private bool _existedAfterFailure;

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            _directory = PrivateOperationDirectory.Create(_workspace.OperationsRoot, _workspace.OperationId);
            _injected = new IOException("injected");
            _failure = Record.Exception(() =>
                _directory
                    .CreateFile(
                        PrivateOperationDirectory.CredentialFileName,
                        afterCreate: stream =>
                        {
                            _seen = stream;
                            throw _injected;
                        }
                    )
                    .Dispose()
            );
            _existedAfterFailure = File.Exists(
                _directory.PathOf(PrivateOperationDirectory.CredentialFileName)
            );
            _recreateFailure = Record.Exception(() =>
                _directory.CreateFile(PrivateOperationDirectory.CredentialFileName).Dispose()
            );
        }

        [TearDown]
        public void TearDown()
        {
            _directory.Dispose();
            _workspace.Dispose();
        }

        [Test]
        public void It_propagates_the_original_failure() => _failure.Should().BeSameAs(_injected);

        [Test]
        public void It_closed_the_handle_before_the_exception_left()
        {
            _seen.Should().NotBeNull();
            _seen!.SafeFileHandle.IsClosed.Should().BeTrue();
        }

        [Test]
        public void It_deletes_the_file_it_created() => _existedAfterFailure.Should().BeFalse();

        [Test]
        public void It_can_create_the_name_again() => _recreateFailure.Should().BeNull();
    }

    /// <summary>
    /// CAR-001: securing a created credential file fails and its deletion is blocked (read-only on Windows, the
    /// directory's write permission removed on Unix). The handle is still disposed, nothing was written, and the
    /// file that remains is accounted for by the directory's owner as credential residue.
    /// </summary>
    [TestFixture]
    public class Given_securing_a_created_file_fails_and_its_deletion_is_blocked
    {
        private StagerWorkspace _workspace = null!;
        private PrivateOperationDirectory _directory = null!;
        private DeletionTrap? _trap;
        private string _filePath = null!;
        private FileStream? _seen;
        private Exception? _failure;
        private long _remainingLength = -1;
        private bool _directoryReleased;
        private IReadOnlyList<DmsCleanupResidue> _residue = [];

        [SetUp]
        public void Setup()
        {
            if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            {
                Assert.Ignore("A privileged process may unlink in a read-only directory.");
            }

            _workspace = new StagerWorkspace();
            _directory = PrivateOperationDirectory.Create(_workspace.OperationsRoot, _workspace.OperationId);
            _filePath = _directory.PathOf(PrivateOperationDirectory.CredentialFileName);
            _failure = Record.Exception(() =>
                _directory
                    .CreateFile(
                        PrivateOperationDirectory.CredentialFileName,
                        afterCreate: stream =>
                        {
                            _seen = stream;
                            BlockDeletion();
                            throw new IOException("injected");
                        }
                    )
                    .Dispose()
            );
            if (File.Exists(_filePath))
            {
                _remainingLength = new FileInfo(_filePath).Length;
            }

            _directoryReleased = _directory.TryDelete();
            _residue = TemplateArtifactCatalog.ResidueUnder(_directory.Path);
        }

        private void BlockDeletion()
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(_filePath, FileAttributes.ReadOnly);
            }
            else
            {
                _trap = new DeletionTrap(_directory.Path);
            }
        }

        [TearDown]
        public void TearDown()
        {
            _trap?.Dispose();
            if (OperatingSystem.IsWindows() && File.Exists(_filePath))
            {
                File.SetAttributes(_filePath, FileAttributes.Normal);
            }

            _directory?.Dispose();
            _workspace?.Dispose();
        }

        [Test]
        public void It_propagates_the_failure() => _failure.Should().BeOfType<IOException>();

        [Test]
        public void It_closed_the_handle_before_the_exception_left()
        {
            _seen.Should().NotBeNull();
            _seen!.SafeFileHandle.IsClosed.Should().BeTrue();
        }

        [Test]
        public void It_leaves_the_file_empty() => _remainingLength.Should().Be(0);

        [Test]
        public void It_reports_the_remaining_credential_file_through_the_directory_owner()
        {
            _directoryReleased.Should().BeFalse();
            _residue
                .Should()
                .Equal(
                    new DmsCleanupResidue(
                        DmsResidueKind.CredentialFile,
                        PrivateOperationDirectory.NamePrefix
                            + _workspace.OperationId.Value
                            + "/"
                            + PrivateOperationDirectory.CredentialFileName
                    ),
                    new DmsCleanupResidue(
                        DmsResidueKind.OperationDirectory,
                        PrivateOperationDirectory.NamePrefix + _workspace.OperationId.Value
                    )
                );
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

    /// <summary>
    /// H-03: the rule over one path entry's raw <c>st_mode</c> and owner. A directory owned by <c>root</c> or this
    /// process is safe when neither its group nor others may write to it, or when the sticky bit keeps them from renaming
    /// the entries they do not own. A group is not assumed trusted: a group-writable directory without the sticky bit (what
    /// a <c>umask</c> of <c>0002</c> creates) is refused, as are a symbolic link, a non-directory, and another owner.
    /// </summary>
    [TestFixture]
    public class Given_path_entries_judged_by_mode_and_owner
    {
        private const uint Self = 1000;
        private const uint Other = 1001;
        private const int DirectoryType = 0x4000;

        [Test]
        public void It_accepts_private_and_sticky_directories_owned_by_root_or_this_process() =>
            new[]
            {
                OperationRootSafety.Judge(DirectoryType | 0b111_101_101, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_000_000, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_101_101, 0, Self),
                OperationRootSafety.Judge(DirectoryType | 0x200 | 0b111_111_111, 0, Self),
                OperationRootSafety.Judge(DirectoryType | 0x200 | 0b111_111_111, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0x200 | 0b111_111_000, 0, Self),
            }
                .Should()
                .AllSatisfy(reason => reason.Should().BeNull());

        [Test]
        public void It_refuses_entries_another_identity_could_replace() =>
            new[]
            {
                OperationRootSafety.Judge(0xA000 | 0b111_111_111, Self, Self),
                OperationRootSafety.Judge(0x8000 | 0b110_100_100, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_101_101, Other, Self),
                OperationRootSafety.Judge(DirectoryType | 0x200 | 0b111_111_111, Other, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_111_111, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_111_101, Self, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_111_000, 0, Self),
                OperationRootSafety.Judge(DirectoryType | 0b111_101_111, Self, Self),
            }
                .Should()
                .Equal(
                    UnsafeOperationRootReason.SymbolicLink,
                    UnsafeOperationRootReason.NotDirectory,
                    UnsafeOperationRootReason.UntrustedOwner,
                    UnsafeOperationRootReason.UntrustedOwner,
                    UnsafeOperationRootReason.ReplaceableEntries,
                    UnsafeOperationRootReason.ReplaceableEntries,
                    UnsafeOperationRootReason.ReplaceableEntries,
                    UnsafeOperationRootReason.ReplaceableEntries
                );
    }

    /// <summary>
    /// H-03 on a real Unix file system: a root, or an ancestor of it, whose entries another identity could rename away is
    /// refused before anything is created under it, whether its group or others may write it; a sticky world-writable
    /// root and a private root are used as before.
    /// A symbolic link to an otherwise safe root is refused, since the link's target is not what was checked.
    /// </summary>
    [TestFixture]
    [Platform(Exclude = "Win")]
    public class Given_an_operations_root_another_identity_could_replace
    {
        private StagerWorkspace _workspace = null!;
        private string _link = null!;
        private Exception? _worldWritable;
        private Exception? _groupWritable;
        private Exception? _worldWritableAncestor;
        private Exception? _symbolicLink;
        private Exception? _sticky;
        private Exception? _private;
        private string[] _entriesAfterRefusals = [];

        private static Exception? Claim(string root) =>
            Record.Exception(() => PrivateOperationDirectory.Create(root, DmsOperationId.New()).Dispose());

        [SetUp]
        public void Setup()
        {
            _workspace = new StagerWorkspace();
            string root = _workspace.OperationsRoot;
            string ancestor = Path.GetDirectoryName(root)!;
            _link = Path.Combine(Path.GetTempPath(), "dms1438-root-link-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            const UnixFileMode Private =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode Everyone =
                Private
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            UnixFileMode ancestorMode = File.GetUnixFileMode(ancestor);

            File.SetUnixFileMode(root, Everyone);
            _worldWritable = Claim(root);
            File.SetUnixFileMode(
                root,
                Private | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            );
            _groupWritable = Claim(root);

            File.SetUnixFileMode(root, Private);
            File.SetUnixFileMode(ancestor, Everyone);
            _worldWritableAncestor = Claim(root);
            File.SetUnixFileMode(ancestor, ancestorMode);

            File.CreateSymbolicLink(_link, root);
            _symbolicLink = Claim(_link);
            _entriesAfterRefusals = Directory.GetFileSystemEntries(root);

            File.SetUnixFileMode(root, Everyone | UnixFileMode.StickyBit);
            _sticky = Claim(root);
            File.SetUnixFileMode(root, Private);
            _private = Claim(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_link) || Directory.Exists(_link))
            {
                File.Delete(_link);
            }

            _workspace.Dispose();
        }

        [Test]
        public void It_refuses_a_root_others_or_its_group_may_write_without_the_sticky_bit() =>
            new[] { _worldWritable, _groupWritable }
                .Should()
                .AllSatisfy(failure =>
                    failure
                        .Should()
                        .BeOfType<UnsafeOperationRootException>()
                        .Which.Reason.Should()
                        .Be(UnsafeOperationRootReason.ReplaceableEntries)
                );

        [Test]
        public void It_refuses_a_private_root_under_an_ancestor_others_may_write() =>
            _worldWritableAncestor
                .Should()
                .BeOfType<UnsafeOperationRootException>()
                .Which.Reason.Should()
                .Be(UnsafeOperationRootReason.ReplaceableEntries);

        [Test]
        public void It_refuses_a_root_reached_through_a_symbolic_link() =>
            _symbolicLink
                .Should()
                .BeOfType<UnsafeOperationRootException>()
                .Which.Reason.Should()
                .Be(UnsafeOperationRootReason.SymbolicLink);

        [Test]
        public void It_creates_nothing_under_a_refused_root() => _entriesAfterRefusals.Should().BeEmpty();

        [Test]
        public void It_names_no_path_in_the_refusal() =>
            _worldWritable!.Message.Should().NotContain(_workspace.OperationsRoot).And.NotContain("/");

        [Test]
        public void It_uses_a_sticky_world_writable_root_and_a_private_root() =>
            (_sticky, _private).Should().Be(((Exception?)null, (Exception?)null));
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
