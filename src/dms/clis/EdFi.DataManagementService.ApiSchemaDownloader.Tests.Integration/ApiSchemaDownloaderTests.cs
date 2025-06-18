// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.ApiSchemaDownloader.Services;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Tests.Unit
{
    [TestFixture]
    public class ApiSchemaDownloaderTests
    {
        private IApiSchemaDownloader _downloader = null!;
        private ILogger<Services.ApiSchemaDownloader> _fakeLogger = null!;
        private string _tempDirectory = null!;

        [SetUp]
        public void SetUp()
        {
            // Create a fake logger using FakeItEasy
            _fakeLogger = A.Fake<ILogger<Services.ApiSchemaDownloader>>();

            // Initialize the downloader with the fake logger
            _downloader = new Services.ApiSchemaDownloader(_fakeLogger);

            // Create a temporary directory for test output
            _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
        }

        [TestFixture]
        public class Given_An_Invalid_Download : ApiSchemaDownloaderTests
        {
            [Test]
            public void ShouldThrowExceptionWhenPackageNotFound()
            {
                // Act
                var ex = Assert.ThrowsAsync<Exception>(async () =>
                    await _downloader.DownloadNuGetPackageAsync(
                        "NonExistentPackage",
                        "1.0.0",
                        "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
                        _tempDirectory
                    )
                );

                // Assert
                Assert.That(
                    ex?.Message,
                    Does.Contain("Package NonExistentPackage version 1.0.0 not found in the feed.")
                );
            }
        }

        [TestFixture]
        public class Given_An_Valid_Download : ApiSchemaDownloaderTests
        {
            [Test]
            public async Task ShouldReturnWhenPackageFound()
            {
                // Arrange
                string packageId = "EdFi.DataStandard52.ApiSchema";
                string feedUrl =
                    "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

                // Act
                string packagePath = await _downloader.DownloadNuGetPackageAsync(
                    packageId,
                    string.Empty,
                    feedUrl,
                    _tempDirectory
                );

                // Assert
                Assert.That(
                    File.Exists(packagePath),
                    Is.True,
                    "Nuget package has been downloaded successfully."
                );
            }

            [Test]
            public async Task ShouldReturnWhenPackageVersionFound()
            {
                // Arrange
                string packageId = "EdFi.DataStandard52.ApiSchema";
                string packageVersion = "1.0.216";
                string feedUrl =
                    "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

                // Act
                string packagePath = await _downloader.DownloadNuGetPackageAsync(
                    packageId,
                    packageVersion,
                    feedUrl,
                    _tempDirectory
                );

                // Assert
                Assert.That(
                    File.Exists(packagePath),
                    Is.True,
                    "Nuget package has been downloaded successfully."
                );
            }
        }

        [TestFixture]
        public class Given_An_Invalid_Package : ApiSchemaDownloaderTests
        {
            [Test]
            public void Should_Throw_Exception_When_Package_Does_Not_Contain_DLL()
            {
                // Arrange
                var packagePath = Path.Combine(_tempDirectory, "invalidPackage.nupkg");
                File.WriteAllText(packagePath, "This is not a valid NuGet package.");

                // Act & Assert
                var ex = Assert.Throws<Exception>(() =>
                    _downloader.ExtractApiSchemaJsonFromAssembly("TestPackage", packagePath, _tempDirectory)
                );

                Assert.That(
                    ex?.Message,
                    Is.EqualTo("An error occurred while extracting ApiSchema Json From Assembly.")
                );
            }
        }

        [TestFixture]
        public class Given_A_Valid_Package : ApiSchemaDownloaderTests
        {
            [Test]
            public async Task Should_Extract_Embedded_Resources_Correctly()
            {
                // Arrange
                string packageId = "EdFi.DataStandard52.ApiSchema";
                string feedUrl =
                    "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json";

                // Act
                string packagePath = await _downloader.DownloadNuGetPackageAsync(
                    packageId,
                    string.Empty,
                    feedUrl,
                    _tempDirectory
                );

                // Act
                _downloader.ExtractApiSchemaJsonFromAssembly(
                    "EdFi.DataStandard52.ApiSchema",
                    packagePath,
                    _tempDirectory
                );
            }
        }
    }
}
