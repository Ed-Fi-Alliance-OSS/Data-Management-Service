// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace EdFi.DataManagementService.ApiSchemaDownloader.Services;

public class ApiSchemaDownloader(ILogger<ApiSchemaDownloader> logger) : IApiSchemaDownloader
{
    private readonly ILogger<ApiSchemaDownloader> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private const string ApiSchemaContentRoot = "contentFiles/any/any/ApiSchema/";

    public void ExtractApiSchemaFiles(string packageId, string packagePath, string outputDir)
    {
        _logger.LogInformation(
            "Extracting API schema content from package {PackageId} located at {PackagePath}",
            packageId,
            packagePath
        );

        try
        {
            using var packageReader = new PackageArchiveReader(packagePath);
            var apiSchemaContentFiles = packageReader
                .GetFiles()
                .Where(file =>
                    file.StartsWith(ApiSchemaContentRoot, StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith("/", StringComparison.Ordinal)
                )
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (apiSchemaContentFiles.Count == 0)
            {
                _logger.LogError(
                    "No ApiSchema content files found in package {PackageId}. Expected files under {ContentRoot}",
                    packageId,
                    ApiSchemaContentRoot
                );
                throw new Exception("No ApiSchema content files found in the package.");
            }

            string packageOutputDir = Path.Combine(outputDir, "Packages", packageId);
            Directory.CreateDirectory(packageOutputDir);

            _logger.LogInformation(
                "Extracting {FileCount} ApiSchema content files.",
                apiSchemaContentFiles.Count
            );

            foreach (var apiSchemaContentFile in apiSchemaContentFiles)
            {
                string relativePath = apiSchemaContentFile[ApiSchemaContentRoot.Length..];
                string outputFilePath = GetSafeOutputPath(packageOutputDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

                using var resourceStream = packageReader.GetStream(apiSchemaContentFile);
                using var fileStream = File.Create(outputFilePath);
                resourceStream.CopyTo(fileStream);
            }

            _logger.LogInformation(
                "API schema extraction completed successfully to {PackageOutputDir}.",
                packageOutputDir
            );
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An error occurred while extracting ApiSchema files from package.");
            throw new Exception("An error occurred while extracting ApiSchema files from package.", ex);
        }
    }

    private static string GetSafeOutputPath(string outputDir, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"ApiSchema package content path '{relativePath}' must be relative."
            );
        }

        if (Array.Exists(relativePath.Split(['/', '\\'], StringSplitOptions.None), part => part == ".."))
        {
            throw new InvalidOperationException(
                $"ApiSchema package content path '{relativePath}' contains parent-directory traversal."
            );
        }

        string fullOutputDir = Path.GetFullPath(outputDir);
        string fullOutputPath = Path.GetFullPath(Path.Combine(fullOutputDir, relativePath));
        string relativeToOutput = Path.GetRelativePath(fullOutputDir, fullOutputPath);

        if (
            Path.IsPathRooted(relativeToOutput)
            || relativeToOutput == ".."
            || relativeToOutput.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToOutput.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"ApiSchema package content path '{relativePath}' resolves outside the output directory."
            );
        }

        return fullOutputPath;
    }

    public async Task<string> DownloadNuGetPackageAsync(
        string packageId,
        string? packageVersion,
        string feedUrl,
        string outputDir
    )
    {
        _logger.LogInformation("Downloading NuGet package {PackageId} from {FeedUrl}", packageId, feedUrl);

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
