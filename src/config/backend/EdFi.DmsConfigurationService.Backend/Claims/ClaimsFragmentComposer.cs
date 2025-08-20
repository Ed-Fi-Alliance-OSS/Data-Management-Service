// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Claims;

/// <summary>
/// Service for composing claims from base Claims.json and fragment claimset files
/// This is a port of the transformation logic from the CmsHierarchy tool
/// </summary>
public class ClaimsFragmentComposer(ILogger<ClaimsFragmentComposer> logger) : IClaimsFragmentComposer
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Options for final output that uses camelCase like the base Claims.json
    private static readonly JsonSerializerOptions _outputJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Convert to camelCase for output
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // Omit null values
    };

    /// <summary>
    /// Composes claims from base Claims.json and fragment files in the specified directory
    /// </summary>
    public ClaimsLoadResult ComposeClaimsFromFragments(ClaimsDocument baseClaimsNodes, string fragmentsPath)
    {
        try
        {
            logger.LogInformation(
                "Composing claims from base and fragments in path: {FragmentsPath}",
                fragmentsPath
            );

            // Discover fragment files
            List<string> fragmentFiles = DiscoverFragmentFiles(fragmentsPath);
            if (fragmentFiles.Count == 0)
            {
                logger.LogInformation("No fragment files found - returning base claims");
                return new ClaimsLoadResult(baseClaimsNodes, []);
            }

            logger.LogInformation("Found {FragmentCount} fragment files to compose", fragmentFiles.Count);

            // Convert base claims to our transformation model
            List<TransformationClaim> baseClaims = ConvertToTransformationModel(
                baseClaimsNodes.ClaimsHierarchyNode
            );
            logger.LogDebug(
                "Base claims converted to transformation model: {BaseClaimsCount} claims",
                baseClaims.Count
            );

            // Apply each fragment transformation
            foreach (string fragmentFile in fragmentFiles)
            {
                logger.LogInformation("Applying fragment: {FragmentFile}", fragmentFile);
                baseClaims = ApplyFragmentTransformation(fragmentFile, baseClaims) ?? [];
                logger.LogDebug(
                    "After applying {FragmentFile}: {ClaimsCount} claims",
                    fragmentFile,
                    baseClaims.Count
                );
            }

            // Clean up empty collections before conversion to ensure JSON Schema compliance
            CleanupEmptyCollections(baseClaims);

            // Convert back to ClaimsDocumentNodes format
            JsonNode transformedHierarchy = ConvertFromTransformationModel(baseClaims);

            // Debug logging to check the composed results
            logger.LogInformation(
                "Transformed hierarchy JSON: {HierarchyJson}",
                transformedHierarchy?.ToJsonString()
            );
            logger.LogInformation(
                "Base claim sets JSON: {ClaimSetsJson}",
                baseClaimsNodes.ClaimSetsNode?.ToJsonString()
            );

            // Ensure both nodes are not null
            if (transformedHierarchy == null)
            {
                transformedHierarchy = JsonNode.Parse("[]")!;
            }

            JsonNode claimSetsNode = baseClaimsNodes.ClaimSetsNode ?? JsonNode.Parse("[]")!;
            ClaimsDocument composedNodes = new ClaimsDocument(claimSetsNode, transformedHierarchy);

            logger.LogInformation(
                "Successfully composed claims from {FragmentCount} fragments",
                fragmentFiles.Count
            );
            return new ClaimsLoadResult(composedNodes, []);
        }
        catch (JsonException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "JsonError",
                "Invalid JSON format in fragment files",
                fragmentsPath,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (DirectoryNotFoundException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "DirectoryNotFound",
                "Fragment directory not found",
                fragmentsPath,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (FileNotFoundException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "FileNotFound",
                "Fragment file not found",
                fragmentsPath,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
        catch (IOException ex)
        {
            ClaimsFailure failure = new ClaimsFailure(
                "IOError",
                "Failed to read fragment files",
                fragmentsPath,
                ex
            );
            logger.LogError(ex, failure.Message);
            return new ClaimsLoadResult(null, [failure]);
        }
    }

    /// <summary>
    /// Discovers fragment files matching the pattern *-claimset.json in the specified directory
    /// </summary>
    public List<string> DiscoverFragmentFiles(string fragmentsPath)
    {
        try
        {
            if (!Directory.Exists(fragmentsPath))
            {
                logger.LogWarning("Fragment path does not exist: {FragmentsPath}", fragmentsPath);
                return [];
            }

            List<string> fragmentFiles = Directory
                .GetFiles(fragmentsPath, "*-claimset.json", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("Claims.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            logger.LogDebug(
                "Discovered {Count} fragment files in {Path}",
                fragmentFiles.Count,
                fragmentsPath
            );
            return fragmentFiles;
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.LogError(
                ex,
                "Directory not found when discovering fragment files: {FragmentsPath}",
                fragmentsPath
            );
            return [];
        }
        catch (IOException ex)
        {
            logger.LogError(
                ex,
                "I/O error when discovering fragment files in path: {FragmentsPath}",
                fragmentsPath
            );
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogError(
                ex,
                "Access denied when discovering fragment files in path: {FragmentsPath}",
                fragmentsPath
            );
            return [];
        }
    }

    /// <summary>
    /// Converts ClaimsHierarchy JSON to transformation model (ported from CmsHierarchy.GetBaseClaimHierarchy)
    /// </summary>
    private static List<TransformationClaim> ConvertToTransformationModel(JsonNode claimsHierarchyNode)
    {
        string hierarchyJson = claimsHierarchyNode.ToJsonString();
        List<TransformationClaim>? claims = JsonSerializer.Deserialize<List<TransformationClaim>>(
            hierarchyJson,
            _jsonOptions
        );
        return claims ?? [];
    }

    /// <summary>
    /// Converts transformation model back to JsonNode format
    /// </summary>
    private JsonNode ConvertFromTransformationModel(List<TransformationClaim> claims)
    {
        string json = JsonSerializer.Serialize(claims, _outputJsonOptions);
        return JsonNode.Parse(json) ?? JsonNode.Parse("[]")!;
    }

    /// <summary>
    /// Applies fragment transformation to existing claims (ported from CmsHierarchy.TransformClaims)
    /// </summary>
    private List<TransformationClaim> ApplyFragmentTransformation(
        string fragmentFilePath,
        List<TransformationClaim> existingClaims
    )
    {
        string jsonData = File.ReadAllText(fragmentFilePath);
        ClaimSetResourceClaims? claimSetData = JsonSerializer.Deserialize<ClaimSetResourceClaims>(
            jsonData,
            _jsonOptions
        );
        string claimSetName = claimSetData?.Name ?? Path.GetFileNameWithoutExtension(fragmentFilePath);

        if (claimSetData?.ResourceClaims == null)
        {
            logger.LogWarning("Fragment file {FilePath} has no resource claims", fragmentFilePath);
            return existingClaims;
        }

        foreach (ResourceClaim resourceClaim in claimSetData.ResourceClaims)
        {
            if (resourceClaim.IsParent)
            {
                ApplyParentResourceClaim(resourceClaim, existingClaims);
            }
            else
            {
                ApplyChildResourceClaim(resourceClaim, existingClaims, claimSetName);
            }
        }

        return existingClaims;
    }

    /// <summary>
    /// Applies parent resource claim transformation
    /// </summary>
    private static void ApplyParentResourceClaim(
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims
    )
    {
        TransformationClaim? existingClaim = existingClaims
            .SelectMany(x => SearchRecursive(x, resourceClaim.Name!))
            .FirstOrDefault();

        if (existingClaim != null)
        {
            // Add additional child claims
            List<TransformationClaim> childClaims = resourceClaim
                .Children.Where(x => !string.IsNullOrEmpty(x.Name))
                .Select(x => new TransformationClaim { Name = x.Name! })
                .ToList();
            if (childClaims.Count > 0)
            {
                existingClaim.Claims?.AddRange(childClaims);
            }
        }
        else
        {
            // Add claim at root level
            existingClaims.Add(
                new TransformationClaim
                {
                    Name = resourceClaim.Name,
                    Claims =
                        resourceClaim.Children.Count > 0
                            ?
                            [
                                .. resourceClaim.Children.Select(x => new TransformationClaim
                                {
                                    Name = x.Name,
                                }),
                            ]
                            : null,
                    ClaimSets =
                        resourceClaim.ClaimSets?.Count > 0
                            ?
                            [
                                .. resourceClaim.ClaimSets.Select(cs => new TransformationClaimSet
                                {
                                    Name = cs.Name,
                                    Actions =
                                        cs.Actions?.Count > 0
                                            ? cs
                                                .Actions.Select(a => new TransformationClaimSetAction
                                                {
                                                    Name = a.Name,
                                                    AuthorizationStrategyOverrides =
                                                        a.AuthorizationStrategyOverrides?.Count > 0
                                                            ? a
                                                                .AuthorizationStrategyOverrides.Select(
                                                                    auth => new TransformationAuthorizationStrategy
                                                                    {
                                                                        Name = auth.Name,
                                                                    }
                                                                )
                                                                .ToList()
                                                            : null,
                                                })
                                                .ToList()
                                            : null,
                                }),
                            ]
                            : null,
                    DefaultAuthorization =
                        resourceClaim.DefaultAuthorizationStrategiesForCRUD?.Count > 0
                            ? new TransformationDefaultAuthorization
                            {
                                Actions = resourceClaim
                                    .DefaultAuthorizationStrategiesForCRUD.Where(x => x != null)
                                    .Select(x =>
                                    {
                                        List<TransformationAuthorizationStrategy>? authStrategies = null;
                                        if (
                                            x!.AuthorizationStrategies != null
                                            && x.AuthorizationStrategies.Any()
                                        )
                                        {
                                            authStrategies = x
                                                .AuthorizationStrategies.Select(
                                                    auth => new TransformationAuthorizationStrategy
                                                    {
                                                        Name = auth.Name,
                                                    }
                                                )
                                                .ToList();
                                        }

                                        return new TransformationAction
                                        {
                                            Name = x.ActionName,
                                            AuthorizationStrategies = authStrategies,
                                        };
                                    })
                                    .ToList(),
                            }
                            : null,
                }
            );
        }
    }

    /// <summary>
    /// Applies child resource claim transformation
    /// </summary>
    private void ApplyChildResourceClaim(
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims,
        string claimSetName
    )
    {
        string singularName = PluralToSingular(resourceClaim.Name!);
        TransformationClaim? existingClaim = existingClaims
            .SelectMany(x => SearchRecursive(x, singularName))
            .FirstOrDefault();

        if (existingClaim != null)
        {
            List<TransformationClaimSetAction> actions = new List<TransformationClaimSetAction>();
            List<ClaimSetResourceClaimActionAuthStrategies?> actionsAndAuthStrategies =
                resourceClaim.AuthorizationStrategyOverridesForCRUD;

            if (actionsAndAuthStrategies != null)
            {
                foreach (
                    ClaimSetResourceClaimActionAuthStrategies? actionAuthStrategy in actionsAndAuthStrategies.Where(
                        x => x != null
                    )
                )
                {
                    List<TransformationAuthorizationStrategy>? authOverrides = null;
                    if (
                        actionAuthStrategy!.AuthorizationStrategies != null
                        && actionAuthStrategy.AuthorizationStrategies.Any()
                    )
                    {
                        authOverrides = actionAuthStrategy
                            .AuthorizationStrategies.Select(auth => new TransformationAuthorizationStrategy
                            {
                                Name = auth.Name,
                            })
                            .ToList();
                    }

                    actions.Add(
                        new TransformationClaimSetAction
                        {
                            Name = actionAuthStrategy.ActionName!,
                            AuthorizationStrategyOverrides = authOverrides,
                        }
                    );
                }
            }

            // Only create ClaimSets array and add claimSet if there are actions to include
            if (actions.Count > 0)
            {
                existingClaim.ClaimSets ??= [];
                if (
                    !existingClaim.ClaimSets.Exists(x =>
                        string.Equals(x.Name, claimSetName, StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    existingClaim.ClaimSets.Add(
                        new TransformationClaimSet { Name = claimSetName, Actions = actions }
                    );
                }
            }
        }
    }

    /// <summary>
    /// Recursively searches for a claim with specified name (ported from CmsHierarchy.SearchRecursive)
    /// </summary>
    private static IEnumerable<TransformationClaim> SearchRecursive(
        TransformationClaim claim,
        string searchTerm
    )
    {
        if (string.Equals(ResourceName(claim.Name), searchTerm, StringComparison.InvariantCultureIgnoreCase))
        {
            yield return claim;
        }

        if (claim.Claims != null)
        {
            foreach (TransformationClaim child in claim.Claims)
            {
                foreach (TransformationClaim match in SearchRecursive(child, searchTerm))
                {
                    yield return match;
                }
            }
        }

        static string ResourceName(string? resourceUri)
        {
            if (string.IsNullOrEmpty(resourceUri))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(resourceUri, UriKind.Absolute, out var uri))
            {
                string[] pathSegments = uri.AbsolutePath.Split('/');
                return string.Join("/", pathSegments[^2..]);
            }

            return resourceUri;
        }
    }

    /// <summary>
    /// Converts plural resource names to singular (ported from CmsHierarchy.PluralToSingular)
    /// </summary>
    private static string PluralToSingular(string resourceClaimName)
    {
        if (
            resourceClaimName.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 3
        )
        {
            return resourceClaimName[..^3] + "y";
        }

        if (
            resourceClaimName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && resourceClaimName.Length > 1
        )
        {
            return resourceClaimName[..^1];
        }

        return resourceClaimName;
    }

    /// <summary>
    /// Cleans up empty collections that would violate JSON Schema minItems constraints
    /// Sets them to null so JsonIgnoreCondition.WhenWritingNull will omit them
    /// </summary>
    private static void CleanupEmptyCollections(List<TransformationClaim> claims)
    {
        foreach (TransformationClaim claim in claims)
        {
            // Clean up empty collections that violate minItems: 1
            if (claim.ClaimSets?.Count == 0)
            {
                claim.ClaimSets = null;
            }

            if (claim.DefaultAuthorization?.Actions?.Count == 0)
            {
                claim.DefaultAuthorization.Actions = null;
            }

            // If DefaultAuthorization has no actions, remove it entirely
            if (claim.DefaultAuthorization?.Actions == null)
            {
                claim.DefaultAuthorization = null;
            }

            // Clean nested claims recursively
            if (claim.Claims != null)
            {
                CleanupEmptyCollections(claim.Claims);
                if (claim.Claims.Count == 0)
                {
                    claim.Claims = null;
                }
            }

            // Clean claim set actions
            if (claim.ClaimSets != null)
            {
                foreach (TransformationClaimSet claimSet in claim.ClaimSets)
                {
                    if (claimSet.Actions?.Count == 0)
                    {
                        claimSet.Actions = null;
                    }
                }

                // Remove claim sets that have no actions (violates schema requirements)
                claim.ClaimSets.RemoveAll(cs => cs.Actions == null);

                // If no claim sets remain, set to null
                if (claim.ClaimSets.Count == 0)
                {
                    claim.ClaimSets = null;
                }
            }
        }
    }

    #region Transformation Model Classes (Ported from CmsHierarchy.Model)

    private class TransformationClaim
    {
        public string? Name { get; set; }
        public TransformationDefaultAuthorization? DefaultAuthorization { get; set; }
        public List<TransformationClaimSet>? ClaimSets { get; set; }
        public List<TransformationClaim>? Claims { get; set; }
    }

    private class TransformationDefaultAuthorization
    {
        public List<TransformationAction>? Actions { get; set; }
    }

    private class TransformationAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategies { get; set; }
    }

    private class TransformationClaimSet
    {
        public string? Name { get; set; }
        public List<TransformationClaimSetAction>? Actions { get; set; }
    }

    private class TransformationClaimSetAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategyOverrides { get; set; }
    }

    private class TransformationAuthorizationStrategy
    {
        public string? Name { get; set; }
    }

    // Fragment file structure classes (reused from CmsHierarchy.Model)
    private class ResourceClaim
    {
        public string? Name { get; set; }
        public bool IsParent { get; set; }

        [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
        public List<ClaimSetResourceClaimActionAuthStrategies?> DefaultAuthorizationStrategiesForCRUD { get; set; } =
            [];
        public List<ClaimSetResourceClaimActionAuthStrategies?> AuthorizationStrategyOverridesForCRUD { get; set; } =
            [];
        public List<ResourceClaim> Children { get; set; } = [];
        public List<TransformationClaimSet> ClaimSets { get; set; } = [];
    }

    private class ClaimSetResourceClaimActionAuthStrategies
    {
        public string? ActionName { get; set; }
        public IEnumerable<TransformationAuthorizationStrategy>? AuthorizationStrategies { get; set; }
    }

    private class ClaimSetResourceClaims
    {
        public string? Name { get; set; }
        public List<ResourceClaim> ResourceClaims { get; set; } = [];
    }

    #endregion
}
