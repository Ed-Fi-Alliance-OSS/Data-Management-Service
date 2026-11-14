// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;

/// <summary>
/// H-03 with two real identities that share a writable group. A host script, run as <c>root</c>, creates the service
/// user and a second user, both members of one group, and under <see cref="BaseVariable"/> the roots this fixture
/// resolves through, as the service user: <c>group-writable</c> (<c>0770</c>, the service user's, group shared),
/// <c>group-ancestor/root</c> (a private root under a <c>0770</c> shared-group directory), <c>sticky</c>
/// (<c>1770</c>, shared group), and <c>private</c> (<c>0700</c>). The script separately proves the second user can
/// replace a directory the service user claims in the first two and cannot in the last two. Here the catalog refuses
/// the first two as <c>StagingFailed</c> for inspection before anything is staged (so before any replay) and resolves
/// through the last two. Outside CI (category <see cref="Category"/>); skipped when the variable is unset.
/// </summary>
[Category(Category)]
[Platform(Exclude = "Win")]
public class OperationRootSharedGroupTests
{
    public const string Category = "DmsProvisioningSeparateIdentity";

    public const string BaseVariable = "DMS_PROVISIONING_SHARED_GROUP_ROOTS";

    private static readonly string _refusal =
        TemplateArtifactCatalog.StagePackageStep
        + "."
        + TemplateArtifactCatalog.OperationRootUnsafeCheck
        + "."
        + nameof(UnsafeOperationRootReason.ReplaceableEntries);

    [TestFixture]
    public class Given_roots_whose_group_another_user_shares
    {
        private CatalogWorkspace _workspace = null!;
        private readonly Dictionary<string, DmsArtifactResolution> _resolutions = [];
        private readonly Dictionary<string, string[]> _entriesAfter = [];

        private static readonly string[] _roots =
        [
            "group-writable",
            "group-ancestor/root",
            "sticky",
            "private",
        ];

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

            _workspace = new CatalogWorkspace();
            _workspace.BuildBoth();
            foreach (string root in _roots)
            {
                string path = Path.Combine(baseDirectory!, root);
                DmsDataStoreProvisioningOptions options = _workspace.Options();
                options.OperationDirectory = path;
                _resolutions[root] = await _workspace
                    .Catalog(options)
                    .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None);
                _entriesAfter[root] = Directory.GetFileSystemEntries(path);
            }
        }

        [OneTimeTearDown]
        public void Teardown() => _workspace?.Dispose();

        private (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Refusal(string root)
        {
            DmsProvisioningFailure failure = _resolutions[root]
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Subject.Detail;
            return (failure.Code, failure.Category, failure.Diagnostic.Operation);
        }

        [Test]
        public void It_refuses_a_root_the_shared_group_may_write() =>
            Refusal("group-writable")
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        _refusal
                    )
                );

        [Test]
        public void It_refuses_a_private_root_under_an_ancestor_the_shared_group_may_write() =>
            Refusal("group-ancestor/root")
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.StagingFailed,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        _refusal
                    )
                );

        [Test]
        public void It_stages_nothing_under_a_refused_root() =>
            _entriesAfter["group-writable"]
                .Concat(_entriesAfter["group-ancestor/root"])
                .Should()
                .BeEmpty("the refusal precedes the claim");

        [Test]
        public void It_resolves_through_a_sticky_shared_group_root_and_a_private_root() =>
            new[] { _resolutions["sticky"], _resolutions["private"] }
                .Should()
                .AllBeOfType<DmsArtifactResolution.Resolved>();
    }
}
