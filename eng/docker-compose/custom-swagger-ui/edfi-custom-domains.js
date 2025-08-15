// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdFiCustomDomains = function () {

    // Helper function to safely get values from schema
    const safeGet = (schema, key) => {
        if (!schema) return undefined;
        if (typeof schema.get === 'function') {
            return schema.get(key);
        }
        // Fallback for plain objects
        return schema[key];
    };

    // Process spec only once
    const processSpecOnce = (system) => {
        if (window.__edfiSpecProcessed) {
            return;
        }
        if (!system || !system.getSystem) {
            return;
        }

        try {
            const specSelectors = system.getSystem().specSelectors;
            const spec = specSelectors && specSelectors.spec();
            if (!spec) {
                return;
            }

            // Initialize global maps
            window.__edfiDomainsByTag = {};
            window.__edfiTagDescriptions = {};

            // Process paths and extract domains
            const paths = spec.get ? spec.get('paths') : spec.paths;
            if (paths && typeof paths.entrySeq === 'function') {
                paths.entrySeq().forEach(([pathKey, pathValue]) => {
                    const tagMatch = pathKey.match(/\/ed-fi\/([^\/]+)/i);
                    if (tagMatch) {
                        const tagName = tagMatch[1];
                        const pathDomains = safeGet(pathValue, "x-Ed-Fi-domains");
                        if (pathDomains) {
                            window.__edfiDomainsByTag[tagName] = pathDomains.toArray ? pathDomains.toArray() : pathDomains;
                        }
                    }
                });
            }

            // Process tags and map descriptions → tag
            const tags = spec.get ? spec.get('tags').toArray() : (spec.tags || []);
            if (Array.isArray(tags)) {
                tags.forEach(tag => {
                    const plainTag = tag.toJS ? tag.toJS() : tag;
                    if (plainTag.name && plainTag.description) {
                        // Store description as the key and tagName as the value.
                        window.__edfiTagDescriptions[plainTag.description] = plainTag.name;
                    }
                });
            }

            window.__edfiSpecProcessed = true;

        } catch (err) {
            console.warn("Error processing spec:", err);
        }
    };

    return {
        wrapComponents: {

            // OperationTag Now just make sure that spec is processed.
            OperationTag: (Original, system) => {
                return function OperationTagWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) {
                        return React.createElement(Original, props);
                    }

                    processSpecOnce(system); // Process spec just once

                    return React.createElement(Original, props);
                };
            },

            // Markdown adds domains next to the description
            Markdown: (Original, system) => {
                return function MarkdownWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) {
                        return React.createElement(Original, props);
                    }

                    processSpecOnce(system);

                    if (
                        props.source &&
                        typeof props.source === 'string' &&
                        window.__edfiTagDescriptions &&
                        window.__edfiDomainsByTag
                    ) {
                        const tagName = window.__edfiTagDescriptions[props.source];
                        const matchingDomains = tagName ? window.__edfiDomainsByTag[tagName] : null;

                        if (matchingDomains && matchingDomains.length > 0) {
                            const firstDomain = matchingDomains[0];
                            const remainingCount = matchingDomains.length - 1;

                            const tooltip = remainingCount > 0
                                ? React.createElement(
                                    "span",
                                    {
                                        style: {
                                            backgroundColor: "#263238",
                                            color: "white",
                                            padding: "10px 14px",
                                            borderRadius: "8px",
                                            fontSize: "14px",
                                            fontWeight: "500",
                                            whiteSpace: "normal",
                                            boxShadow: "0 4px 12px rgba(0,0,0,0.2)",
                                            position: "absolute",
                                            top: "-80px",
                                            left: "0px",
                                            display: "none",
                                            zIndex: 1000,
                                            maxWidth: "400px",
                                            lineHeight: "1.3",
                                            pointerEvents: "none"
                                        },
                                        className: "large-domain-tooltip"
                                    },
                                    `Domains: ${matchingDomains.join(", ")}`
                                )
                                : null;

                            const descriptionDomain = matchingDomains.length === 1
                                ? `Domain: ${firstDomain}`
                                : `Domains: ${firstDomain}${remainingCount > 0 ? ` +${remainingCount}` : ""}`;

                            const domainElement = React.createElement(
                                "span",
                                {
                                    style: {
                                        color: "#1976d2",
                                        fontWeight: "500",
                                        marginLeft: "6px",
                                        cursor: remainingCount > 0 ? "pointer" : "default",
                                        position: "relative",
                                        display: "inline"
                                    },
                                    onMouseEnter: e => {
                                        if (remainingCount > 0) {
                                            const tt = e.target.querySelector(".large-domain-tooltip");
                                            if (tt) tt.style.display = "block";
                                        }
                                    },
                                    onMouseLeave: e => {
                                        if (remainingCount > 0) {
                                            const tt = e.target.querySelector(".large-domain-tooltip");
                                            if (tt) tt.style.display = "none";
                                        }
                                    }
                                },
                                descriptionDomain,
                                tooltip
                            );

                            return React.createElement(
                                "span",
                                { style: { display: "inline" } },
                                React.createElement("span", { style: { display: "inline" } }, props.source),
                                " ",
                                domainElement
                            );
                        }
                    }

                    return React.createElement(Original, props);
                };
            },
        }
    };
};

