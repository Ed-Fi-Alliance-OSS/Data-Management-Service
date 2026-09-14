# Secrets Manager Companion (DMS-1503)

## Overview

Spike [DMS-1503](https://edfi.atlassian.net/browse/DMS-1503) designs the Secrets Manager plugin type for the Ed-Fi API platform, under epic [DMS-1504](https://edfi.atlassian.net/browse/DMS-1504) Shared Plugin Extension Infrastructure.

It answers two questions the plugin spine deliberately deferred, and it answers them together because an operator experiences them as one: how a plugin supplies the values DMS and CMS read as configuration, and how a data store connection string can name a secret instead of carrying one.

The argument, the decisions, and the evidence are in [design.md](./design.md).
This file is the manifest.

## Provenance

The type is not speculative.
[plugins-DMS-1462](../plugins-DMS-1462/design.md) names Secrets Manager as one of three plugin types, designs the delivery mechanism for all three, and then defers this one to its own spike with a section, "The Secrets Spike", recording exactly what that spike inherits so it does not rediscover it.
This document is the answer to that section.

It is also why the spine kept Phase A.
Nothing on the spine's own table consumes configuration contribution: custom validation and identity are both Phase B.
Phase A exists in that design because secrets needs it, and it was designed rather than built so that this spike could build on a mechanism that was decided rather than running.
Draft 01 below is that build, and it is the only draft here that touches the Data Management Service.

| Deferred by the spine | Taken up here |
| --- | --- |
| The Secrets Manager plugin type: contracts, lifetimes, caching, the multi-tenant dimension | `ISecretResolver`, singleton, host-owned cache, tenant as an argument. design.md, "The Contract Package" |
| The DMS equivalent of external configuration of ODS connection strings | A `${secret:<name>}` token in the stored connection string, resolved in CMS at read time. design.md, "The Secret Reference" |
| `IClientSecretHasher` relocation out of `Backend.OpenIddict` | `EdFi.Api.Secrets`, with the hashing-iterations key repaired in the same pass. design.md, "The `IClientSecretHasher` Relocation" |
| Phase A, decided in the spine and owned by the secrets foundations | Draft 01, with the whole deferred list the spine assigned to it |
| CMS host integration, marked deferred for want of a consuming epic | Draft 03. This spike is that epic |

## Documents

| Document | Status | Covers |
| --- | --- | --- |
| [design.md](./design.md) | **Drafted 2026-09-14**, not yet approved | The type: the two kinds of secret and why they need two mechanisms, the secret reference and its grammar, where resolution happens, freshness and caching, failure semantics, the contract package, the hasher relocation, cardinality, CMS host integration, the configuration surface, and the trust notes |
| [plugins-DMS-1462/design.md](../plugins-DMS-1462/design.md) | Approved 2026-08-27 | The mechanism this builds on. Every Phase A statement in it is inherited here rather than revisited |
| [identity-DMS-1413/design.md](../identity-DMS-1413/design.md) | **Approved**, filing gate closed 2026-09-07, six drafts filed as DMS-1512 through DMS-1517 | The sibling companion under the same spine, and the precedent for a replace-cardinality contract with a host default |

## Ticket Drafts

Five drafts.
The default posture was the fewest that cover the scope, and the shape that fell out is one per thing that can be reviewed and released on its own: the spine's missing phase, the contract, the host, the capability, and the publication.

| Draft | Title | Depends on | Status |
| --- | --- | --- | --- |
| [01](./01-add-phase-a-configuration-contribution.md) | Add Phase A Configuration Contribution to the Plugin Contract | the plugin foundations, DMS-1496 through DMS-1501 | not filed |
| [02](./02-add-secrets-contract-package.md) | Add the `EdFi.Api.Secrets` Contract Package and Relocate `IClientSecretHasher` | the plugin foundations | not filed |
| [03](./03-integrate-plugin-loading-into-cms-startup.md) | Integrate Plugin Loading into Configuration Service Startup | 01, 02 | not filed |
| [04](./04-resolve-secret-references-in-connection-strings.md) | Resolve Secret References in Stored Connection Strings | 03 | not filed |
| [05](./05-document-and-publish-secrets-contract.md) | Document and Publish `EdFi.Api.Secrets` | 04, DMS-1500, DMS-1501 | not filed, release-gated |

### Blockers

| Draft | Blocked by | Why |
| --- | --- | --- |
| 01 | the plugin foundations, [DMS-1501](https://edfi.atlassian.net/browse/DMS-1501) included | The Phase A invocation goes inside the `LoadPlugins` bootstrap phase [DMS-1499](https://edfi.atlassian.net/browse/DMS-1499) creates, and the integration proof needs a host that loads plugins at all. DMS-1501 is load-bearing for ordering rather than for code: the spine specifies `EdFi.Api.Plugins` at "`1.0.0` for the first published contract and `1.1.0` when `ContributeConfiguration` lands" (`plugins-DMS-1462/design.md:697`), and this story is what lands `ContributeConfiguration`. Landing it first would make the first published contract `1.1.0`, leaving no `1.0.0` for an older plugin to have been built against, which is the compatibility direction the contract shape exists for |
| 02 | the plugin foundations | Nothing in this story loads a plugin, so on its own merits it would be unblocked. It is blocked anyway, because the spine says "every ticket it produces depends on the full plugin foundations being in place, because a secrets plugin is a plugin" (`plugins-DMS-1462/design.md:1402`), and that is an approved decision this spike inherits rather than one it is free to narrow. The dependency costs ordering and nothing else |
| 03 | 01, 02 | Needs `ContributeConfiguration` to exist before it can invoke it, and needs both contracts to exist before `CmsPluginContracts` can declare them |
| 04 | 03 | Needs a registered `ISecretResolver` to resolve anything, and needs CMS to be loading plugins at all. Transitively needs 02 for the contract |
| 05 | 04, [DMS-1500](https://edfi.atlassian.net/browse/DMS-1500), [DMS-1501](https://edfi.atlassian.net/browse/DMS-1501) | The implementer guide links outward to `PLUGINS.md` for packaging, delivery, and the trust model, and DMS-1500 is the story that turns that file from a placeholder into the delivery guide. An implementer also cannot build a plugin without `EdFi.Api.Plugins` on the feed, which is DMS-1501 |

Nothing here blocks a spine story.
Draft 01 changes the plugin contract and the loader, and every spine story that reads either is a dependency of it rather than a dependent.

**Why draft 01 is here and not in the spine.**
The spine assigned it explicitly: the member is withheld from the first published contract because a virtual the host never calls is a hook a plugin can override to no effect, so it lands with its first consumer.
Its acceptance criteria are the spine's decisions implemented rather than re-decided, with one addition of this spike's own, which is that the integration proof supplies a real process-global secret rather than an invented key, because the empty string `appsettings.json` ships at those keys is exactly the shadowing case the placement rule exists to prevent.

**Why draft 02 carries the hashing-iterations repair.**
The spine found that `Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26` is CMS's only `Configure<IdentityOptions>` and never assigns the iteration count, so the hasher always reads the model default and both keys an operator would reach for are dead, and it handed the question here: the contract "has to name a key that actually binds rather than inheriting either of these".
Filing it separately would leave a plugin contract for the hasher whose host default has a dead knob, which makes replacing the hasher the only way to reach a setting that was supposed to be configuration.
Whether it is *also* worth its own Jira ticket is a filing question rather than a design one; see the gate below.

**Why there is no Data Management Service story after draft 01.**
The capability lands in CMS, which is the spine's finding and which this design's research confirms: DMS's own configuration secrets are all served by Phase A, and it obtains every connection string from CMS as cipher text it decrypts with a shared key.
Nothing in drafts 02 through 05 changes a DMS file.

**Why no sample vault plugin is shipped.**
An Ed-Fi-authored Azure Key Vault or AWS Parameter Store plugin would commit the Alliance to a cloud SDK's release cadence, authentication surface, and CVEs for a component every deployment configures differently.
The ODS documentation's own answer to the same problem is worked examples an implementer copies, and draft 05 writes the DMS equivalents.
The repository ships fixture plugins for its own tests, backed by a file rather than by a vault, and nothing an operator installs.

## Filing Gate

**Closed.**
No draft is filed until this design is approved and the pull request carrying it is approved, which is the same gate DMS-1462 used and for the same reason: the drafts commit five tickets' worth of scope to decisions this document is still being reviewed for.

When it opens, all five are filed as stories under epic [DMS-1504](https://edfi.atlassian.net/browse/DMS-1504), with Jira `Blocks` links recording the dependency column above, including the plugin-foundation prerequisites DMS-1499, DMS-1500, and DMS-1501.
Draft 05 is filed with the others and is release-gated in the same way DMS-1501 is.

One item is deliberately left for that moment rather than decided here, because it is a filing question and not a design one: **whether the `IdentitySettings` hashing-iterations defect is also filed as its own ticket** beside the story set.
It was on the needs-go-ahead list DMS-1462 produced and never taken up.
The design's position is that the repair belongs in draft 02 either way, because a contract whose host default has a dead configuration knob is not a finished contract; a separate ticket would be a record of the defect rather than a separate piece of work.

Two smaller observations are recorded in design.md, "Out of Scope and Deferred", and are not filed by this spike: the two other dead `IdentitySettings` keys found in the same file while establishing that the iteration pair is dead, and the unreachable four-argument `AddPostgresOpenIddictStores` overload.
Neither is a secret and neither blocks anything here.
They are written down so the next reader of those files does not have to rediscover them.
