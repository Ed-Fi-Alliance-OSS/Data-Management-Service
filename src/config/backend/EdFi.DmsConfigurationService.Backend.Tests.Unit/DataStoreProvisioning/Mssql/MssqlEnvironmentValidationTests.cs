// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// Step 4.9 without a server: the engine's environmental validation against a server nothing listens on reports
/// <c>ServerUnavailable</c> under <c>TargetServer</c>, skips the visibility and lock probes, and still checks the
/// staging directory: a missing directory is reported, an existing private one is not (and nothing is written into
/// it), and on Unix a group-writable one is refused (D-18). A token cancelled before the call ends it with
/// <see cref="OperationCanceledException"/>.
/// </summary>
public class MssqlEnvironmentValidationTests
{
    private const UnixFileMode Private0750 =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute;

    private const string Unreachable =
        "Server=127.0.0.1,1;User Id=none;Password=none;Connect Timeout=1;TrustServerCertificate=true";

    private static MssqlDmsTargetEngine EngineWithStagingAt(string stagingPath) =>
        new(
            new MssqlAdminConnection(Unreachable, TimeSpan.FromSeconds(10)),
            new MssqlDmsEngineSettings(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                new MssqlStagingSettings(
                    stagingPath,
                    "/var/opt/dms-staging",
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                    3,
                    TimeSpan.FromSeconds(10)
                )
            ),
            TimeProvider.System,
            NullLogger.Instance
        );

    private static string NewDirectoryPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "dms1438-readiness-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
        );

    /// <summary>Creates the directory with <paramref name="mode"/> on Unix, set again after creation because the umask applies.</summary>
    private static string CreateDirectory(UnixFileMode mode)
    {
        string path = NewDirectoryPath();
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }

        return path;
    }

    private static IReadOnlyList<DmsReadinessReason> ReasonsOf(DmsEngineEnvironment environment) =>
        environment.Should().BeOfType<DmsEngineEnvironment.NotReady>().Subject.Reasons;

    [TestFixture]
    public class Given_an_unreachable_server_and_a_missing_staging_directory
    {
        private DmsEngineEnvironment _environment = null!;
        private IReadOnlyList<DmsReadinessReason> _reasons = [];

        [SetUp]
        public async Task Setup()
        {
            _environment = await EngineWithStagingAt(NewDirectoryPath()).ValidateEnvironmentAsync(default);
            _reasons = ReasonsOf(_environment);
        }

        [Test]
        public void It_reports_no_server_major_version() => _environment.ServerMajorVersion.Should().BeNull();

        [Test]
        public void It_reports_the_server_as_unavailable_and_the_directory_as_missing_only() =>
            _reasons
                .Select(reason => (reason.Code, reason.Probe, reason.Diagnostic!.Operation))
                .Should()
                .Equal(
                    (
                        DmsProvisioningFailureCode.ServerUnavailable,
                        MssqlEnvironment.TargetServerProbe,
                        MssqlEnvironment.ConnectOperation
                    ),
                    (
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        MssqlEnvironment.StagingDirectoryMissingOperation
                    )
                );

        [Test]
        public void It_keeps_the_connection_failure_type_chain() =>
            _reasons[0].Diagnostic!.ExceptionTypeChain.Should().Contain("SqlException");
    }

    [TestFixture]
    public class Given_an_unreachable_server_and_a_private_staging_directory
    {
        private string _path = string.Empty;
        private IReadOnlyList<DmsReadinessReason> _reasons = [];

        [SetUp]
        public async Task Setup()
        {
            _path = CreateDirectory(Private0750);
            _reasons = ReasonsOf(await EngineWithStagingAt(_path).ValidateEnvironmentAsync(default));
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_path, recursive: true);

        [Test]
        public void It_reports_only_the_server() =>
            _reasons.Select(reason => reason.Probe).Should().Equal(MssqlEnvironment.TargetServerProbe);

        [Test]
        public void It_writes_no_probe_without_a_server() =>
            Directory.GetFileSystemEntries(_path).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_group_writable_staging_directory
    {
        private string _path = string.Empty;
        private IReadOnlyList<DmsReadinessReason> _reasons = [];

        [SetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The staging directory mode is read on Unix only.");
            }

            _path = CreateDirectory(Private0750 | UnixFileMode.GroupWrite);
            _reasons = ReasonsOf(await EngineWithStagingAt(_path).ValidateEnvironmentAsync(default));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }

        [Test]
        public void It_refuses_the_directory() =>
            _reasons
                .Should()
                .ContainSingle(reason => reason.Probe == MssqlEnvironment.StagingDirectoryProbe)
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            MssqlEnvironment.StagingDirectoryWritableOperation
                        )
                    )
                );
    }

    /// <summary>
    /// V4-01 at startup: a correctly owned <c>0750</c> staging directory under a parent its group may write without the
    /// sticky bit, or a staging path that is a symbolic link to such a directory, is refused under the
    /// <c>StagingDirectory</c> probe with the reason, once and only after the directory's own rules pass; under a sticky
    /// shared parent it is accepted. The checks read the file system only, so the unreachable server stays the only
    /// other reason.
    /// </summary>
    [TestFixture]
    public class Given_a_private_staging_directory_on_a_path_another_identity_could_replace
    {
        private string _root = string.Empty;
        private IReadOnlyList<DmsReadinessReason> _underGroupWritable = [];
        private IReadOnlyList<DmsReadinessReason> _throughLink = [];
        private IReadOnlyList<DmsReadinessReason> _underSticky = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The staging directory ancestry is read on Unix only.");
                return;
            }

            _root = PrivateTestDirectory.Create(NewDirectoryPath());
            string groupWritable = Leaf("group-ancestor", Private0750 | UnixFileMode.GroupWrite);
            string sticky = Leaf("sticky", Private0750 | UnixFileMode.GroupWrite | UnixFileMode.StickyBit);
            string target = Leaf("private", Private0750);
            string link = Path.Combine(_root, "link");
            Directory.CreateSymbolicLink(link, target);

            _underGroupWritable = ReasonsOf(
                await EngineWithStagingAt(groupWritable).ValidateEnvironmentAsync(default)
            );
            _throughLink = ReasonsOf(await EngineWithStagingAt(link).ValidateEnvironmentAsync(default));
            _underSticky = ReasonsOf(await EngineWithStagingAt(sticky).ValidateEnvironmentAsync(default));
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (_root.Length > 0)
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private string Leaf(string parent, UnixFileMode parentMode)
        {
            string staging = PrivateTestDirectory.Create(Path.Combine(_root, parent, "staging"));
            File.SetUnixFileMode(staging, Private0750);
            File.SetUnixFileMode(Path.Combine(_root, parent), parentMode);
            return staging;
        }

        private static (DmsProvisioningFailureCode, string)[] StagingReasons(
            IReadOnlyList<DmsReadinessReason> reasons
        ) =>
            [
                .. reasons
                    .Where(reason => reason.Probe == MssqlEnvironment.StagingDirectoryProbe)
                    .Select(reason => (reason.Code, reason.Diagnostic!.Operation)),
            ];

        [Test]
        public void It_refuses_a_staging_directory_under_a_group_writable_parent() =>
            StagingReasons(_underGroupWritable)
                .Should()
                .Equal(
                    (
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryUnsafeAncestryPrefix
                            + nameof(UnsafeOperationRootReason.ReplaceableEntries)
                    )
                );

        [Test]
        public void It_refuses_a_staging_path_that_is_a_symbolic_link() =>
            StagingReasons(_throughLink)
                .Should()
                .Equal(
                    (
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryUnsafeAncestryPrefix
                            + nameof(UnsafeOperationRootReason.SymbolicLink)
                    )
                );

        [Test]
        public void It_accepts_a_staging_directory_under_a_sticky_shared_parent() =>
            _underSticky.Select(reason => reason.Probe).Should().Equal(MssqlEnvironment.TargetServerProbe);
    }

    /// <summary>
    /// D-18 ownership: the file system root, owned by root with no group or other write, stands for a staging
    /// directory another user owns (the SQL Server identity in the finding). Run as a non-root user, as CI does;
    /// a root process owns every candidate, so the case is ignored there and left to the rule tests.
    /// </summary>
    [TestFixture]
    public class Given_a_staging_directory_another_user_owns
    {
        private IReadOnlyList<DmsReadinessReason> _reasons = [];

        [SetUp]
        public async Task Setup()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Ignore("The staging directory owner is read on Unix only.");
                return;
            }

            if (Environment.UserName == "root")
            {
                Assert.Ignore("A root process owns the file system root.");
            }

            ((File.GetUnixFileMode("/") & MssqlStagingSettings.ForbiddenFileModeBits) == 0)
                .Should()
                .BeTrue("the case isolates the owner rule from the mode rule");
            _reasons = ReasonsOf(await EngineWithStagingAt("/").ValidateEnvironmentAsync(default));
        }

        [Test]
        public void It_refuses_the_directory_as_not_owned_by_the_process() =>
            _reasons
                .Should()
                .ContainSingle(reason => reason.Probe == MssqlEnvironment.StagingDirectoryProbe)
                .Which.Should()
                .Be(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.StartupValidationFailed,
                        MssqlEnvironment.StagingDirectoryProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            MssqlEnvironment.StagingDirectoryNotOwnedOperation
                        )
                    )
                );
    }

    [TestFixture]
    public class Given_a_token_cancelled_before_the_call
    {
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            try
            {
                await EngineWithStagingAt(NewDirectoryPath()).ValidateEnvironmentAsync(cancelled.Token);
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();
    }
}
