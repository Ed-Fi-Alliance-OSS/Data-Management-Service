// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using System.Reflection;
using CommandLine;

namespace EdFi.DataManagementService.Downloader
{
    public static class Program
    {
        private static async Task Main(string[] args)
        {

            // Parse command-line arguments
            var result = Parser.Default.ParseArguments<CommandLineOverrides>(args);

            // Handle parsing errors
            result.WithNotParsed(errors =>
            {
                Console.WriteLine("Error parsing command-line arguments. Please provide valid parameters.");
                Environment.Exit(1);
            });

            // Execute program logic if parsing is successful
            _ = await result.WithParsedAsync(async options =>
            {
                // Validate required parameters
                if (string.IsNullOrWhiteSpace(options.PackageId))
                {
                    throw new ArgumentException("Error: packageId is required.");
                }

                string packageId = options.PackageId;
                string? packageVersion = options.PackageVersion;
                string feedUrl = options.FeedUrl;

                // Output directory for the downloaded package and extracted files
                string outputDir = Path.Combine(Path.GetTempPath(), "DownloadedPackages");
                Directory.CreateDirectory(outputDir);

                // Download the package
                var packagePath = await DownloadNuGetPackageAsync(packageId, packageVersion, feedUrl, outputDir);
                Console.WriteLine($"Package downloaded to: {packagePath}");

                Directory.CreateDirectory(options.ApiSchemaFolder);
                ExtractApiSchemaJsonFromAssembly(packageId, packagePath, options.ApiSchemaFolder);
                Console.WriteLine($"ApiSchema.json extracted to folder: {options.ApiSchemaFolder}");
            });


            static void ExtractApiSchemaJsonFromAssembly(string packageId, string packagePath, string outputDir)
            {
                // Open the NuGet package
                using (var packageReader = new PackageArchiveReader(packagePath))
                {
                    // Find the DLL in the package
                    var dllPath = packageReader.GetFiles().FirstOrDefault(f => f.EndsWith("ApiSchema.dll"));
                    if (dllPath == null)
                    {
                        throw new Exception("No DLL found in the package.");
                    }

                    // Extract the DLL to a temporary location
                    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    var dllFilePath = Path.Combine(tempDir, Path.GetFileName(dllPath));
                    using (var dllStream = packageReader.GetStream(dllPath))
                    using (var fileStream = File.Create(dllFilePath))
                    {
                        dllStream.CopyTo(fileStream);
                    }

                    // Load the assembly
                    var assembly = Assembly.LoadFrom(dllFilePath);

                    // Find all embedded JSON files that start with "ApiSchema"
                    var resourceNames = assembly.GetManifestResourceNames()
                        .Where(name => name.StartsWith(packageId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (resourceNames == null)
                    {
                        throw new Exception("ApiSchema.json not found as an embedded resource.");
                    }

                    // Extract the resource

                    foreach (var resourceName in resourceNames)
                    {

                        // Determine the output file path based on the resource name
                        string outputFilePath = Path.Combine(outputDir, resourceName.Replace($"{packageId}.", "")); // Remove package ID prefix

                        // Fix XSD file naming if the resource is an XSD file
                        if (resourceName.EndsWith(".xsd"))
                        {
                            outputFilePath = outputFilePath.Replace("xsd.", "");
                        }

                        // Extract the resource to the output file
                        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                        using var fileStream = File.Create(outputFilePath);
                        resourceStream?.CopyTo(fileStream);
                    }

                }
            }

            static async Task<string> DownloadNuGetPackageAsync(string packageId, string? packageVersion, string feedUrl, string outputDir)
            {
                // Create a NuGet repository
                var providers = Repository.Provider.GetCoreV3();
                var packageSource = new PackageSource(feedUrl);
                var sourceRepository = new SourceRepository(packageSource, providers);

                // Create a SourceCacheContext
                var cacheContext = new SourceCacheContext();

                // Get the package metadata resource
                var packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();

                // Get the latest version if packageVersion is not specified
                if (string.IsNullOrWhiteSpace(packageVersion))
                {
                    var latestPackage = (await packageMetadataResource.GetMetadataAsync(
                        packageId,
                        includePrerelease: true,
                        includeUnlisted: false,
                        cacheContext,
                        NullLogger.Instance,
                        CancellationToken.None))
                        .OrderByDescending(p => p.Identity.Version)
                        .FirstOrDefault();

                    if (latestPackage == null)
                    {
                        throw new Exception($"No versions found for package {packageId} in the feed.");
                    }

                    packageVersion = latestPackage.Identity.Version.ToString();
                }

                var packageIdentity = new PackageIdentity(packageId, new NuGet.Versioning.NuGetVersion(packageVersion));

                // Check if the specified version exists in the feed
                var packageMetadata = (await packageMetadataResource.GetMetadataAsync(
                    packageId,
                    includePrerelease: true,
                    includeUnlisted: false,
                    cacheContext,
                    NullLogger.Instance,
                    CancellationToken.None))
                    .FirstOrDefault(p => p.Identity.Version == packageIdentity.Version);

                if (packageMetadata == null)
                {
                    throw new Exception($"Package {packageId} version {packageVersion} not found in the feed.");
                }

                // Download the package
                var downloadResource = await sourceRepository.GetResourceAsync<DownloadResource>();
                var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                    packageIdentity,
                    new PackageDownloadContext(cacheContext),
                    outputDir,
                    NullLogger.Instance,
                    CancellationToken.None);

                if (downloadResult.Status != DownloadResourceResultStatus.Available)
                {
                    throw new Exception($"Failed to download package {packageId} version {packageVersion}.");
                }

                // Save the package to the output directory
                var packageFilePath = Path.Combine(outputDir, $"{packageId}.{packageVersion}.nupkg");
                using (var fileStream = File.Create(packageFilePath))
                {
                    await downloadResult.PackageStream.CopyToAsync(fileStream);
                }

                Console.WriteLine($"Downloaded package: {packageId} Version: {packageVersion}");
                return packageFilePath;
            }

        }
    }
}
