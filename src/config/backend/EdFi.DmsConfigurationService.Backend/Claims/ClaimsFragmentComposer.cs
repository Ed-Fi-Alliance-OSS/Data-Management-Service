// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
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

            // Apply each fragment transformation, collecting the claim sets the fragments define
            List<string> definedClaimSetNames = [];
            foreach (string fragmentFile in fragmentFiles)
            {
                logger.LogInformation("Applying fragment: {FragmentFile}", fragmentFile);
                if (ApplyFragmentTransformation(fragmentFile, baseClaims) is string definedClaimSetName)
                {
                    // An invalid name is not registered, but the composition continues: failing it
                    // would make Hybrid mode fall back to the base claims and drop every fragment
                    if (IsValidClaimSetName(definedClaimSetName))
                    {
                        definedClaimSetNames.Add(definedClaimSetName);
                    }
                    else
                    {
                        logger.LogError(
                            "Fragment {FragmentFile} defines a claim set name that is empty, longer than {MaxLength} characters, or contains white space; the claim set is not registered",
                            fragmentFile,
                            ValidationConstants.ClaimSetNameMaxLength
                        );
                    }
                }
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
            if (transformedHierarchy is null)
            {
                transformedHierarchy = JsonNode.Parse("[]")!;
            }

            JsonNode claimSetsNode = WithDefinedClaimSets(
                baseClaimsNodes.ClaimSetsNode,
                definedClaimSetNames
            );
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
        return JsonSerializer.Deserialize<List<TransformationClaim>>(hierarchyJson, _jsonOptions) ?? [];
    }

    /// <summary>
    /// Converts transformation model back to JsonNode format
    /// </summary>
    private static JsonNode ConvertFromTransformationModel(List<TransformationClaim> claims)
    {
        string json = JsonSerializer.Serialize(claims, _outputJsonOptions);
        return JsonNode.Parse(json) ?? JsonNode.Parse("[]")!;
    }

    /// <summary>
    /// Applies the Management API claim set name rules, because a registered name becomes a claim set row
    /// </summary>
    private static bool IsValidClaimSetName(string claimSetName) =>
        claimSetName.Length is > 0 and <= ValidationConstants.ClaimSetNameMaxLength
        && Regex.IsMatch(claimSetName, ValidationConstants.ClaimSetNameNoWhiteSpaceRegex);

    /// <summary>
    /// Copies the base claim sets and appends, as system-reserved, each fragment-defined claim set
    /// that the base does not already declare
    /// </summary>
    private JsonNode WithDefinedClaimSets(JsonNode? baseClaimSetsNode, List<string> definedClaimSetNames)
    {
        // Re-parse so the composed document never shares nodes with the base document
        JsonArray claimSets = JsonNode.Parse(baseClaimSetsNode?.ToJsonString() ?? "[]")!.AsArray();

        HashSet<string> declaredClaimSetNames = new(
            claimSets.Select(claimSet => claimSet?["claimSetName"]?.GetValue<string>()).OfType<string>(),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (string claimSetName in definedClaimSetNames.Where(declaredClaimSetNames.Add))
        {
            claimSets.Add(new JsonObject { ["claimSetName"] = claimSetName, ["isSystemReserved"] = true });
            logger.LogInformation(
                "Registered claim set {ClaimSetName} defined by a claims fragment",
                claimSetName
            );
        }

        return claimSets;
    }

    /// <summary>
    /// Applies fragment transformation to existing claims (ported from CmsHierarchy.TransformClaims).
    /// Returns the claim set name the fragment defines, or null when it defines none. A fragment
    /// defines its top-level name (or file name) as a claim set when it has a non-parent resource
    /// claim, because those grants are attached under that name.
    /// </summary>
    private string? ApplyFragmentTransformation(
        string fragmentFilePath,
        List<TransformationClaim> existingClaims
    )
    {
        string jsonData = File.ReadAllText(fragmentFilePath);
        if (
            JsonSerializer.Deserialize<ClaimSetResourceClaims>(jsonData, _jsonOptions)
            is not { ResourceClaims: not null } claimSetData
        )
        {
            logger.LogWarning("Fragment file {FilePath} has no resource claims", fragmentFilePath);
            return null;
        }

        string claimSetName = claimSetData.Name ?? Path.GetFileNameWithoutExtension(fragmentFilePath);
        bool definesClaimSet = false;
        foreach (ResourceClaim resourceClaim in claimSetData.ResourceClaims)
        {
            if (resourceClaim.IsParent)
            {
                ApplyParentResourceClaim(resourceClaim, existingClaims);
            }
            else
            {
                ApplyChildResourceClaim(fragmentFilePath, resourceClaim, existingClaims, claimSetName);
                definesClaimSet = true;
            }
        }

        return definesClaimSet ? claimSetName : null;
    }

    /// <summary>
    /// Applies parent resource claim transformation
    /// </summary>
    private static void ApplyParentResourceClaim(
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims
    )
    {
        if (FindClaim(existingClaims, resourceClaim.Name!) is TransformationClaim existingClaim)
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
                                    .DefaultAuthorizationStrategiesForCRUD.OfType<ClaimSetResourceClaimActionAuthStrategies>()
                                    .Select(x => new TransformationAction
                                    {
                                        Name = x.ActionName,
                                        AuthorizationStrategies =
                                            x.AuthorizationStrategies is not null
                                            && x.AuthorizationStrategies.Any()
                                                ? x
                                                    .AuthorizationStrategies.Select(
                                                        auth => new TransformationAuthorizationStrategy
                                                        {
                                                            Name = auth.Name,
                                                        }
                                                    )
                                                    .ToList()
                                                : null,
                                    })
                                    .ToList(),
                            }
                            : null,
                }
            );
        }
    }

    /// <summary>
    /// Applies child resource claim transformation. The grant name is resolved exactly as written
    /// first, and only when no claim has that name is its singular form tried. When neither matches,
    /// a warning is logged and the entry is skipped.
    /// </summary>
    private void ApplyChildResourceClaim(
        string fragmentFilePath,
        ResourceClaim resourceClaim,
        List<TransformationClaim> existingClaims,
        string claimSetName
    )
    {
        string name = resourceClaim.Name!;
        if (FindGrantClaim(existingClaims, name) is not TransformationClaim existingClaim)
        {
            logger.LogWarning(
                "Fragment {FragmentFile} grants claim set {ClaimSetName} access to {ResourceClaimName}, which matches no resource claim by that name or its singular form; the entry is skipped",
                fragmentFilePath,
                claimSetName,
                name
            );
        }
        else
        {
            List<TransformationClaimSetAction> actions = new List<TransformationClaimSetAction>();
            foreach (
                ClaimSetResourceClaimActionAuthStrategies actionAuthStrategy in resourceClaim.AuthorizationStrategyOverridesForCRUD?.OfType<ClaimSetResourceClaimActionAuthStrategies>()
                    ?? []
            )
            {
                actions.Add(
                    new TransformationClaimSetAction
                    {
                        Name = actionAuthStrategy.ActionName!,
                        AuthorizationStrategyOverrides =
                            actionAuthStrategy.AuthorizationStrategies is not null
                            && actionAuthStrategy.AuthorizationStrategies.Any()
                                ? actionAuthStrategy
                                    .AuthorizationStrategies.Select(
                                        auth => new TransformationAuthorizationStrategy { Name = auth.Name }
                                    )
                                    .ToList()
                                : null,
                    }
                );
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

    // Exact name first; the singular form only when no claim has the exact name
    private static TransformationClaim? FindGrantClaim(List<TransformationClaim> existingClaims, string name)
    {
        string singularName = PluralToSingular(name);
        return FindClaim(existingClaims, name)
            ?? (singularName == name ? null : FindClaim(existingClaims, singularName));
    }

    private static TransformationClaim? FindClaim(List<TransformationClaim> existingClaims, string name) =>
        existingClaims.SelectMany(x => SearchRecursive(x, name)).FirstOrDefault();

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

        if (claim.Claims is not null)
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
            if (claim.DefaultAuthorization?.Actions is null)
            {
                claim.DefaultAuthorization = null;
            }

            // Clean nested claims recursively
            if (claim.Claims is not null)
            {
                CleanupEmptyCollections(claim.Claims);
                if (claim.Claims.Count == 0)
                {
                    claim.Claims = null;
                }
            }

            // Clean claim set actions
            if (claim.ClaimSets is not null)
            {
                foreach (TransformationClaimSet claimSet in claim.ClaimSets)
                {
                    if (claimSet.Actions?.Count == 0)
                    {
                        claimSet.Actions = null;
                    }
                }

                // Remove claim sets that have no actions (violates schema requirements)
                claim.ClaimSets.RemoveAll(cs => cs.Actions is null);

                // If no claim sets remain, set to null
                if (claim.ClaimSets.Count == 0)
                {
                    claim.ClaimSets = null;
                }
            }
        }
    }

    #region Transformation Model Classes (Ported from CmsHierarchy.Model)

    private sealed class TransformationClaim
    {
        public string? Name { get; set; }
        public TransformationDefaultAuthorization? DefaultAuthorization { get; set; }
        public List<TransformationClaimSet>? ClaimSets { get; set; }
        public List<TransformationClaim>? Claims { get; set; }
    }

    private sealed class TransformationDefaultAuthorization
    {
        public List<TransformationAction>? Actions { get; set; }
    }

    private sealed class TransformationAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategies { get; set; }
    }

    private sealed class TransformationClaimSet
    {
        public string? Name { get; set; }
        public List<TransformationClaimSetAction>? Actions { get; set; }
    }

    private sealed class TransformationClaimSetAction
    {
        public string? Name { get; set; }
        public List<TransformationAuthorizationStrategy>? AuthorizationStrategyOverrides { get; set; }
    }

    private sealed class TransformationAuthorizationStrategy
    {
        public string? Name { get; set; }
    }

    // Fragment file structure records (reused from CmsHierarchy.Model).
    // Positional records — the primary constructor is what System.Text.Json calls during
    // deserialization, satisfying analyzers that don't track reflection-based property writes.
    private sealed record ResourceClaim(string? Name = null, bool IsParent = false)
    {
        [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
        public List<ClaimSetResourceClaimActionAuthStrategies?> DefaultAuthorizationStrategiesForCRUD { get; init; } =
        [];
        public List<ClaimSetResourceClaimActionAuthStrategies?> AuthorizationStrategyOverridesForCRUD { get; init; } =
        [];
        public List<ResourceClaim> Children { get; init; } = [];
        public List<TransformationClaimSet> ClaimSets { get; init; } = [];
    }

    private sealed record ClaimSetResourceClaimActionAuthStrategies(
        string? ActionName = null,
        IEnumerable<TransformationAuthorizationStrategy>? AuthorizationStrategies = null
    );

    private sealed record ClaimSetResourceClaims(string? Name = null)
    {
        public List<ResourceClaim> ResourceClaims { get; init; } = [];
    }

    #endregion
}
