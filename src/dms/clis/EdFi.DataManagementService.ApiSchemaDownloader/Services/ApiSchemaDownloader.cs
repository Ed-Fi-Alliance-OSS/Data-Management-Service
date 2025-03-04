// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Services
{
    public class ApiSchemaDownloader(ILogger<ApiSchemaDownloader> logger) : IApiSchemaDownloader
    {
        private readonly ILogger<ApiSchemaDownloader> _logger =
            logger ?? throw new ArgumentNullException(nameof(logger));

        public const string packageName = "EdFi.DataStandard52.ApiSchema";

        public void ExtractApiSchemaJsonFromAssembly(string packageId, string packagePath, string outputDir)
        {
            _logger.LogInformation(
                "Extracting API schema from package {PackageId} located at {PackagePath}",
                packageId,
                packagePath
            );

            try
            {
                using var packageReader = new PackageArchiveReader(packagePath);
                var dllPath = packageReader
                    .GetFiles()
                    .FirstOrDefault(f => f.Contains(packageId) && f.EndsWith(".dll"));
                if (dllPath == null)
                {
                    _logger.LogError("No DLL found in the package {PackageId}", packageId);
                    throw new Exception("No DLL found in the package.");
                }

                var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);
                var dllFilePath = Path.Combine(tempDir, Path.GetFileName(dllPath));

                _logger.LogDebug("Extracting DLL {DllPath} to {TempDir}", dllPath, tempDir);

                using (var dllStream = packageReader.GetStream(dllPath))
                using (var fileStream = File.Create(dllFilePath))
                {
                    dllStream.CopyTo(fileStream);
                }

                _logger.LogDebug("Loading assembly from {DllFilePath}", dllFilePath);
                byte[] assemblyBytes = File.ReadAllBytes(dllFilePath);
                var assembly = Assembly.Load(assemblyBytes);

                var resourceNames = assembly
                    .GetManifestResourceNames()
                    .Where(name => name.StartsWith(packageName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!resourceNames.Any())
                {
                    _logger.LogError(
                        "ApiSchema.json not found as an embedded resource in {PackageId}",
                        packageId
                    );
                    throw new Exception("ApiSchema.json not found as an embedded resource.");
                }

                _logger.LogInformation(
                    "Extracting {ResourceCount} resources from assembly.",
                    resourceNames.Count
                );

                foreach (var resourceName in resourceNames)
                {
                    string outputFilePath = Path.Combine(
                        outputDir,
                        resourceName.Replace(packageId + ".", "")
                    );

                    if (resourceName.EndsWith(".xsd"))
                    {
                        outputFilePath = outputFilePath.Replace("xsd.", "");
                    }

                    using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                    using var fileStream = File.Create(outputFilePath);
                    resourceStream?.CopyTo(fileStream);
                }

                _logger.LogInformation("API schema extraction completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "An error occurred while extracting ApiSchema Json From Assembly.");
                throw new Exception("An error occurred while extracting ApiSchema Json From Assembly.");
            }
        }

        public async Task<string> DownloadNuGetPackageAsync(
            string packageId,
            string? packageVersion,
            string feedUrl,
            string outputDir
        )
        {
            _logger.LogInformation(
                "Downloading NuGet package {PackageId} from {FeedUrl}",
                packageId,
                feedUrl
            );

            var providers = Repository.Provider.GetCoreV3();
            var packageSource = new PackageSource(feedUrl);
            var sourceRepository = new SourceRepository(packageSource, providers);
            var cacheContext = new SourceCacheContext();
            var packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                var latestPackage = (
                    await packageMetadataResource.GetMetadataAsync(
                        packageId,
                        true,
                        false,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None
                    )
                )
                    .OrderByDescending(p => p.Identity.Version)
                    .FirstOrDefault();

                if (latestPackage == null)
                {
                    _logger.LogError("No versions found for package {PackageId} in the feed.", packageId);
                    throw new Exception($"No versions found for package {packageId} in the feed.");
                }

                packageVersion = latestPackage.Identity.Version.ToString();
            }

            _logger.LogDebug("Selected package version {PackageVersion}", packageVersion);

            var packageIdentity = new PackageIdentity(packageId, new NuGetVersion(packageVersion));
            var packageMetadata = (
                await packageMetadataResource.GetMetadataAsync(
                    packageId,
                    true,
                    false,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None
                )
            ).FirstOrDefault(p => p.Identity.Version == packageIdentity.Version);

            if (packageMetadata == null)
            {
                _logger.LogError(
                    "Package {PackageId} version {PackageVersion} not found in the feed.",
                    packageId,
                    packageVersion
                );
                throw new Exception($"Package {packageId} version {packageVersion} not found in the feed.");
            }

            var downloadResource = await sourceRepository.GetResourceAsync<DownloadResource>();
            var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                packageIdentity,
                new PackageDownloadContext(cacheContext),
                outputDir,
                NullLogger.Instance,
                CancellationToken.None
            );

            if (downloadResult.Status != DownloadResourceResultStatus.Available)
            {
                _logger.LogError(
                    "Failed to download package {PackageId} version {PackageVersion}.",
                    packageId,
                    packageVersion
                );
                throw new Exception($"Failed to download package {packageId} version {packageVersion}.");
            }

            var packageFilePath = Path.Combine(outputDir, $"{packageId}.{packageVersion}.nupkg");
            using (var fileStream = File.Create(packageFilePath))
            {
                await downloadResult.PackageStream.CopyToAsync(fileStream);
            }

            _logger.LogInformation(
                "Successfully downloaded package {PackageId} Version {PackageVersion} to {PackageFilePath}",
                packageId,
                packageVersion,
                packageFilePath
            );
            return packageFilePath;
        }
    }
}
