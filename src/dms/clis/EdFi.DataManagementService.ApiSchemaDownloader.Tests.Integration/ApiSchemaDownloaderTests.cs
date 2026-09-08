// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.ApiSchemaDownloader.Services;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Tests.Unit;

[TestFixture]
public class ApiSchemaDownloaderTests
{
    private IApiSchemaDownloader _downloader = null!;
    private ILogger<Services.ApiSchemaDownloader> _fakeLogger = null!;
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeLogger = A.Fake<ILogger<Services.ApiSchemaDownloader>>();
        _downloader = new Services.ApiSchemaDownloader(_fakeLogger);

        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [TestFixture]
    public class Given_An_Invalid_Download : ApiSchemaDownloaderTests
    {
        [Test]
        public void ShouldThrowExceptionWhenPackageNotFound()
        {
            var ex = Assert.ThrowsAsync<Exception>(async () =>
                await _downloader.DownloadNuGetPackageAsync(
                    "NonExistentPackage",
                    "1.0.0",
                    "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
                    _tempDirectory
                )
            );

            Assert.That(
                ex?.Message,
                Does.Contain("Package NonExistentPackage version 1.0.0 not found in the feed.")
            );
        }

        [Test]
        public async Task It_rejects_invalid_packageId_before_creating_package_file()
        {
            Func<Task> act = async () =>
                await _downloader.DownloadNuGetPackageAsync(
                    "../EscapingPackage",
                    "1.0.0",
                    "https://not-used.invalid/v3/index.json",
                    _tempDirectory
                );

            await act.Should()
                .ThrowAsync<ArgumentException>()
                .WithParameterName("packageId")
                .WithMessage("*packageId is invalid*");
            Directory.GetFiles(_tempDirectory, "*.nupkg", SearchOption.AllDirectories).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_An_Invalid_PackageId_For_Extraction : ApiSchemaDownloaderTests
    {
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("Package With Space")]
        [TestCase("Package/Child")]
        [TestCase(@"Package\Child")]
        [TestCase(".")]
        [TestCase("..")]
        [TestCase("/RootedPackage")]
        [TestCase(@"C:\RootedPackage")]
        public void It_rejects_packageId_before_creating_package_directory(string packageId)
        {
            Action act = () =>
                _downloader.ExtractApiSchemaFiles(
                    packageId,
                    Path.Combine(_tempDirectory, "missing.nupkg"),
                    _tempDirectory
                );

            act.Should()
                .Throw<ArgumentException>()
                .WithParameterName("packageId")
                .WithMessage("*packageId is invalid*");
            Directory.Exists(Path.Combine(_tempDirectory, "Packages")).Should().BeFalse();
        }

        [Test]
        public void It_rejects_null_packageId_before_creating_package_directory()
        {
            Action act = () =>
                _downloader.ExtractApiSchemaFiles(
                    null!,
                    Path.Combine(_tempDirectory, "missing.nupkg"),
                    _tempDirectory
                );

            act.Should()
                .Throw<ArgumentException>()
                .WithParameterName("packageId")
                .WithMessage("*packageId is invalid*");
            Directory.Exists(Path.Combine(_tempDirectory, "Packages")).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_An_Valid_Download : ApiSchemaDownloaderTests
    {
        [Test]
        public async Task ShouldReturnWhenPackageFound()
        {
            string packageId = "EdFi.DataStandard52.ApiSchema";
            string feedUrl =
                "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

            string packagePath = await _downloader.DownloadNuGetPackageAsync(
                packageId,
                string.Empty,
                feedUrl,
                _tempDirectory
            );

            Assert.That(File.Exists(packagePath), Is.True, "Nuget package has been downloaded successfully.");
        }

        [Test]
        public async Task ShouldReturnWhenPackageVersionFound()
        {
            string packageId = "EdFi.DataStandard52.ApiSchema";
            string packageVersion = "1.0.333";
            string feedUrl =
                "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

            string packagePath = await _downloader.DownloadNuGetPackageAsync(
                packageId,
                packageVersion,
                feedUrl,
                _tempDirectory
            );

            Assert.That(File.Exists(packagePath), Is.True, "Nuget package has been downloaded successfully.");
        }
    }

    [TestFixture]
    public class Given_An_Invalid_Package : ApiSchemaDownloaderTests
    {
        [Test]
        public void Should_Throw_Exception_When_Package_Does_Not_Contain_ApiSchema_Content()
        {
            var packagePath = Path.Combine(_tempDirectory, "invalidPackage.nupkg");
            File.WriteAllText(packagePath, "This is not a valid NuGet package.");

            var ex = Assert.Throws<Exception>(() =>
                _downloader.ExtractApiSchemaFiles("TestPackage", packagePath, _tempDirectory)
            );

            Assert.That(
                ex?.Message,
                Is.EqualTo("An error occurred while extracting ApiSchema files from package.")
            );
        }
    }

    [TestFixture]
    public class Given_A_Valid_Package : ApiSchemaDownloaderTests
    {
        [Test]
        public async Task Should_Extract_ApiSchema_Content_Files_Correctly()
        {
            string packageId = "EdFi.DataStandard52.ApiSchema";
            string packageVersion = "1.0.333";
            string feedUrl =
                "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

            string packagePath = await _downloader.DownloadNuGetPackageAsync(
                packageId,
                packageVersion,
                feedUrl,
                _tempDirectory
            );

            _downloader.ExtractApiSchemaFiles(packageId, packagePath, _tempDirectory);

            string packageOutputDir = Path.Combine(_tempDirectory, "Packages", packageId);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(packageOutputDir, "ApiSchema.json")), Is.True);
                Assert.That(File.Exists(Path.Combine(packageOutputDir, "package-manifest.json")), Is.True);
                Assert.That(File.Exists(Path.Combine(packageOutputDir, "xsd", "Ed-Fi-Core.xsd")), Is.True);
            });
        }
    }
}
