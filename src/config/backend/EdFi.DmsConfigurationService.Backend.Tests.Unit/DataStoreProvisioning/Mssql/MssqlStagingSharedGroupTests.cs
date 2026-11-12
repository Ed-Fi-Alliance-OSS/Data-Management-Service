// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// V4-01 with two real identities that share a writable group. A host script, run as <c>root</c>, creates the service
/// user and a second user, both members of one group, and under <see cref="BaseVariable"/> four staging directories,
/// each the documented <c>2750</c> directory owned by the service user: <c>group-ancestor/staging</c> (under a
/// <c>0770</c> shared-group parent), <c>link-parent/staging</c> (a symbolic link, in a <c>0770</c> shared-group
/// parent, to a <c>2750</c> directory), <c>sticky/staging</c> (under a <c>1770</c> shared-group parent), and
/// <c>private/staging</c> (under a <c>0755</c> parent of the service user, as <c>/var/opt</c> holds the same-host
/// recipe's directory). The script separately proves the second user can rename the first away or repoint the link,
/// and put its own bytes under the same staged path, and cannot do either to the last two. Here the stager refuses the
/// first two before writing anything and stages into the last two. Outside CI (category <see cref="Category"/>);
/// skipped when the variable is unset.
/// </summary>
[Category(Category)]
[Platform(Exclude = "Win")]
public class MssqlStagingSharedGroupTests
{
    public const string Category = "DmsProvisioningSeparateIdentity";

    public const string BaseVariable = "DMS_PROVISIONING_SHARED_GROUP_STAGING";

    [TestFixture]
    public class Given_staging_directories_whose_path_another_user_shares
    {
        private static readonly string[] _directories =
        [
            "group-ancestor/staging",
            "link-parent/staging",
            "sticky/staging",
            "private/staging",
        ];

        private string _artifactRoot = string.Empty;
        private readonly Dictionary<string, MssqlStagingResult> _results = [];
        private readonly Dictionary<string, string[]> _entriesAfter = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            string? baseDirectory = Environment.GetEnvironmentVariable(BaseVariable);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                Assert.Ignore(
                    $"{BaseVariable} is not set; the shared-group identities are prepared by a host script."
                );
            }

            _artifactRoot = Packaging.PrivateTestDirectory.Create(
                Path.Combine(
                    Path.GetTempPath(),
                    "dms1438-shared-staging-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
                )
            );
            string artifactPath = Path.Combine(_artifactRoot, "a.bak");
            byte[] bytes = RandomNumberGenerator.GetBytes(4096);
            await File.WriteAllBytesAsync(artifactPath, bytes);
            StagedArtifact artifact = new(
                artifactPath,
                MssqlBackupStagerTests.Sha256Of(bytes),
                DmsArtifactKind.MssqlBackup
            );

            int index = 0;
            foreach (string directory in _directories)
            {
                string stagingPath = Path.Combine(baseDirectory!, directory);
                MssqlBackupStager stager = new(
                    new MssqlStagingSettings(
                        stagingPath,
                        "/var/opt/dms-staging",
                        MssqlBackupStagerTests.Mode0640,
                        3,
                        TimeSpan.FromSeconds(10)
                    ),
                    TimeProvider.System,
                    NullLogger.Instance
                );
                MssqlStagingResult result = await stager.StageAsync(
                    artifact,
                    MssqlBackupStagerTests.Operation($"b{index++:D11}"),
                    CancellationToken.None
                );
                _results[directory] = result;
                _entriesAfter[directory] = Directory.GetFileSystemEntries(stagingPath);
                if (result is MssqlStagingResult.Staged staged)
                {
                    (await stager.DeleteAsync(staged.Backup)).Should().BeTrue();
                }
            }
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (_artifactRoot.Length > 0)
            {
                Directory.Delete(_artifactRoot, recursive: true);
            }
        }

        private string Refusal(string directory) =>
            _results[directory].Should().BeOfType<MssqlStagingResult.Failed>().Subject.Diagnostic.Operation;

        [Test]
        public void It_refuses_a_staging_directory_under_a_parent_the_shared_group_may_write() =>
            Refusal("group-ancestor/staging")
                .Should()
                .Be(
                    DmsEngineOperations.StageBackupUnsafeDirectoryPrefix
                        + nameof(UnsafeOperationRootReason.ReplaceableEntries)
                );

        [Test]
        public void It_refuses_a_staging_path_the_other_user_can_repoint() =>
            Refusal("link-parent/staging")
                .Should()
                .Be(
                    DmsEngineOperations.StageBackupUnsafeDirectoryPrefix
                        + nameof(UnsafeOperationRootReason.SymbolicLink)
                );

        [Test]
        public void It_writes_nothing_where_it_refuses() =>
            _entriesAfter["group-ancestor/staging"]
                .Concat(_entriesAfter["link-parent/staging"])
                .Should()
                .BeEmpty("the refusal precedes the create");

        [Test]
        public void It_stages_under_a_sticky_shared_parent_and_a_private_parent() =>
            new[] { _results["sticky/staging"], _results["private/staging"] }
                .Should()
                .AllBeOfType<MssqlStagingResult.Staged>();
    }
}
