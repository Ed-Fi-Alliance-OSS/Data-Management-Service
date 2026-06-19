---
jira: DMS-1194
jira_url: https://edfi.atlassian.net/browse/DMS-1194
---

# Story: Document DMS v1.0 Change Queries Deferred Features in Release Notes

## Description

Document the DMS v1.0 Change Queries deferred-feature limitations in the DMS v1.0 release notes.

The Change Queries implementation for DMS v1.0 intentionally does not include snapshot support, a way to disable the Change Queries feature, or custom view-based authorization strategies for Change Query endpoints. 

The release notes must call out these limitations explicitly so operators, API Publisher users, and implementers do not assume ODS parity in these areas.

Optional: The release notes links to the follow-up tickets:
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/29-snapshot-support.md`
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/30-disable-change-queries-feature.md`
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/31-custom-view-based-readchanges-authorization.md`

## Proposed v8.0 release-note text

The text below is the deliverable for this story: the wording to publish on the Ed-Fi API
v8.0 "What's New" page (see *Destination & handoff notes*). It is normative-faithful to
`reference/design/backend-redesign/design-docs/change-queries.md` and is intended to be
copied verbatim into the public release notes.

> ### Change Queries — deferred features in DMS v1.0
>
> Change Queries are available in DMS v1.0 (Ed-Fi API v8.0), including the `/deletes`,
> `/keyChanges`, and `/availableChangeVersions` endpoints and the
> `minChangeVersion`/`maxChangeVersion` filters on live resource and descriptor GET-many
> endpoints. Several ODS capabilities are deferred in this release; do not assume full ODS
> parity in the following areas:
>
> - **Snapshot / read-replica isolation is not supported.** The `Use-Snapshot` request
>   header is not part of the DMS v1.0 Change Queries contract. DMS v1.0 **silently ignores**
>   `Use-Snapshot` on `/deletes`, `/keyChanges`, `/availableChangeVersions`, and live
>   resource/descriptor GET-many requests: the request is processed against current data
>   without snapshot isolation, no `Warning` header is returned, and no snapshot-specific
>   error is emitted.
> - **Ed-Fi API Publisher guidance.** The Ed-Fi API Publisher sends `Use-Snapshot: true` by
>   default when its source API major version is 7 or higher. Because DMS v1.0 silently
>   ignores the header, reads from a DMS v1.0 source are **not** snapshot-isolated —
>   concurrent writes against the source may be visible mid-publish and can produce
>   inconsistent published data. When publishing from a DMS v1.0 source, run the Publisher
>   with `--ignoreIsolation=true` to explicitly acknowledge that source isolation is
>   unavailable (or accept the risk of inconsistent reads).
> - **No option to disable Change Queries.** Change Queries are always on in DMS v1.0. DMS
>   v1.0 does not provide the ODS-style `ApiSettings:Features:ChangeQueries` disable setting,
>   and the corresponding `Feature Disabled` response is not part of the v1.0 contract.
> - **Custom view-based `ReadChanges` authorization is not supported.** Custom view-based
>   authorization strategies are not supported for the `/deletes` and `/keyChanges` Change
>   Query endpoints in DMS v1.0. Other Change Query authorization strategies are supported,
>   but Change Query authorization should not be assumed to be at full ODS parity.
>
> Snapshot/read-replica support, a runtime option to disable Change Queries, and custom
> view-based `ReadChanges` authorization are planned for a later release (targeted for Ed-Fi
> API v8.1).

## Destination & handoff notes

This DMS repository does not contain a release-notes or changelog file, and it has no
docs-site generator configuration. The canonical Ed-Fi release notes ("What's New In This
Release") are published from a separate repository:

- Published page: <https://docs.ed-fi.org/reference/ed-fi-api/whats-new/whats-new-in-this-release/>
- Source repository (Docusaurus): `ed-fi-alliance-oss/ed-fi-alliance-oss.github.io`
- Versioned source path pattern:
  `odsApi_versioned_docs/version-<X>/whats-new/whats-new-in-this-release.md`
  (the v8.0 / "Upcoming" version is the target for the text above)

The remaining action — applying the *Proposed v8.0 release-note text* to the v8.0 "What's
New" page in `ed-fi-alliance-oss.github.io` — is performed in that repository by the docs
team and is out of scope for this branch.

**Links policy for the public text:** the published release note intentionally contains no
links to internal design docs (`reference/design/...`) or to Jira tickets, because the
"What's New" page is public-facing and those links are neither audience-appropriate nor an
existing convention there. The follow-up work is recorded here for reviewers and the docs
team instead of in the public text:

- Snapshot / read-replica support — DMS-1190
  (`reference/design/backend-redesign/epics/10-update-tracking-change-queries/29-snapshot-support.md`),
  targeted for Ed-Fi API v8.1.
- Runtime flag to disable Change Queries (and the deferred `Feature Disabled` ProblemDetails)
  — DMS-1191
  (`reference/design/backend-redesign/epics/10-update-tracking-change-queries/30-disable-change-queries-feature.md`),
  targeted for Ed-Fi API v8.1.
- Custom view-based `ReadChanges` authorization — DMS-1193
  (`reference/design/backend-redesign/epics/10-update-tracking-change-queries/31-custom-view-based-readchanges-authorization.md`),
  targeted for Ed-Fi API v8.1.
