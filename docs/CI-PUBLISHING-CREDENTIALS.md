# CI Publishing Credentials

This page covers how the GitHub workflows in this repository hold the credentials that publish
packages and images, and what has to be set up in GitHub, Azure DevOps and Docker Hub for that to
work. It follows security review findings 1 to 3 (DMS-1647, DMS-1648 and DMS-1649).

The workflow changes are merged first. The GitHub, Azure DevOps and Docker Hub setup below follows
the merge, and the findings stay open until it is done.

## The credentials

| Name | Kind | Where it lives | Used by |
| --- | --- | --- | --- |
| `AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN` | secret | `publishing` environment only | push and promote steps for the EdFi Azure Artifacts feed |
| `AZURE_ARTIFACTS_USER_NAME` | secret | `publishing` environment only | promote steps |
| `DOCKER_HUB_TOKEN` | secret | `publishing` environment only | Docker Hub login in the jobs that push `edfialliance` images |
| `EDFI_BUILD_AGENT_PAT` | secret | `release-automation` environment only | creating `dms-pre-*` and `cs-pre-*` prereleases and their tags, and the scheduled merge-queue enqueue |
| `DOCKER_SCOUT_USERNAME` | variable | repository | Docker Hub login for Docker Scout and pulls |
| `DOCKER_SCOUT_TOKEN` | secret | repository | Docker Hub login for Docker Scout and pulls |

`DOCKER_USERNAME`, `AZURE_ARTIFACTS_FEED_URL` and `ARTIFACTS_PACKAGES_URL` are plain variables and
are unchanged.

Nothing that only reads the EdFi feed needs a credential. The feed is public: builds, restores, the
contract checks and the manual prerelease dry run read it anonymously. Do not add a read-only Azure
token unless a read actually fails without one.

## What the workflows do

- Pull request runs never receive a publishing token. The template package workflows split into a
  build job, which passes no Azure secret, and a publish job that runs only for `main`, `v*` tags
  and releases. The smoke test passes no Azure secret.
- The Dockerfile pull request workflow and the scheduled image analysis log in with the Scout
  identity, never with `DOCKER_HUB_TOKEN`.
- Every job that pushes or promotes a package, or pushes an image, runs in the `publishing`
  environment. Its `if:` also limits it to the refs that may publish: `main` or a `v*` tag for the
  template, SDK and SecurityMetadataTools packages; only a `v*` tag for stable-release promotion and
  image tagging; and the release's own `dms-pre-*` or `cs-pre-*` tag for prerelease publishing.
- Inside those jobs, only the step that pushes, promotes or logs in reads the token. No workflow
  puts a publishing token in workflow-wide or job-wide `env`.
- `EDFI_BUILD_AGENT_PAT` creates the prerelease tags, and the tag ruleset will let its identity
  past the tag restrictions, so it is protected the same way. The two merge-or-tag jobs and the
  scheduled merge-queue enqueue run in the `release-automation` environment, which has no
  reviewers but releases the token only to runs on `main` and `v*` tags. Only the step that calls
  the GitHub API reads it.
- Reusable workflows still name the Azure secrets under `workflow_call.secrets`, and their callers
  pass them by name. GitHub requires this even for a secret that exists only in an environment.
  The caller job has no environment, so what it passes resolves to nothing outside the reusable
  workflow's `publishing` jobs. See
  [Reuse workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows).

`eng/ci/tests/ReleasePublishingCredentials.Tests.ps1` enforces these rules for every workflow in
`.github/workflows`, for both environments, and runs in the DMS pull request lane.

## Why moving `env` alone does not fix this

A pull request from a branch in this repository runs the workflow files from that branch, and a
manual run uses the files from whichever ref it names. Anyone who can push a branch can edit the
workflow, add a step that prints a secret, and run it. If a secret is a repository or organization
secret, any job can read it, wherever the YAML says to use it.

The controls that branch code cannot change are GitHub environment protection rules. An
environment secret is released only to a job that names the environment, and only after the
environment's deployment-ref rules and required reviewers allow that run. That is why the
publishing tokens must exist only as `publishing` environment secrets. The `if:` gates in the
workflows are defense in depth, not the boundary. See
[Managing environments for deployment](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
and
[Deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments).

The workflow changes alone do not close the findings. While the repository or organization copies
of the tokens are still reachable from this repository, any branch can read them. The fix is
complete only once setup step 3 below is done.

## Setup

These steps change GitHub, Azure DevOps and Docker Hub settings, and follow the merge of the
workflow changes. Do them in this order.

Until then the workflows keep working: a job that names an environment that does not exist makes
GitHub create it with no protection rules, and a secret the environment does not hold resolves to
the repository or organization copy. So publishing and prerelease tagging run as before, and a
publishing run needs no approval yet. Protecting an environment GitHub created is the same as
protecting a new one.

### 1. Protect the `publishing` and `release-automation` environments

In **Settings → Environments**, open or create each environment.

`publishing`:

1. **Required reviewers.** Add the release maintainers' team and turn on **Prevent self-review**,
   so the person who started a run cannot approve it. Before approving, a reviewer checks that the
   run's commit is on `main`, or that its tag points at a commit that was reviewed and merged.
   Once this is on, a merge to `main` that publishes a template package waits for this approval.
2. **Deployment branches and tags: Selected branches and tags.** Add a branch rule for `main` and
   tag rules for `v*`, `dms-pre-*` and `cs-pre-*`.
3. Turn off **Allow administrators to bypass configured protection rules**.

`release-automation`:

1. **No required reviewers.** Every merge to `main` cuts its prerelease unattended.
2. **Deployment branches and tags: Selected branches and tags.** Add a branch rule for `main` and
   a tag rule for `v*`. The merge-or-tag workflows also run on `v*.*.*` tag pushes.
3. Turn off **Allow administrators to bypass configured protection rules**.

A manual merge-queue run from any branch other than `main` then fails instead of reading the token.

### 2. Put the tokens in the environments

GitHub never returns a secret's value, so the existing organization secrets cannot be read and
moved. Whoever holds each credential has to supply it again, or issue a new one. To add each
without leaving it in shell history:

```shell
gh secret set AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh secret set AZURE_ARTIFACTS_USER_NAME --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh secret set DOCKER_HUB_TOKEN --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh secret set EDFI_BUILD_AGENT_PAT --env release-automation -R Ed-Fi-Alliance-OSS/Data-Management-Service
```

Each command prompts for the value. Never paste a token into an issue, a log or this repository.

Do this before step 3. An environment's copy takes precedence inside its jobs, so adding it changes
nothing that works today; removing the old copies first would stop publishing and prerelease
tagging until this step is done.

### 3. Remove the old access and check it

1. Delete any repository secret named `AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN`,
   `AZURE_ARTIFACTS_USER_NAME`, `DOCKER_HUB_TOKEN` or `EDFI_BUILD_AGENT_PAT`.
2. These are organization secrets today, and they are shared with this repository. An organization
   owner removes this repository from each one's selected repositories. If a secret is visible to
   all repositories, the owner changes it to selected repositories and keeps every other repository
   that still needs it. Do not delete an organization secret other repositories depend on just for
   this repository.
3. Check with the commands in [Read-only inspection](#read-only-inspection). The repository and
   organization-secret listings must not show the four names, the `publishing` environment
   listing must show the three publishing tokens, and the `release-automation` listing must show
   `EDFI_BUILD_AGENT_PAT`.

No publishing pause is needed: from step 2 on, every job that reads a token reads its environment's
copy.

### 4. Restrict release tags

A tag rule on an environment does not prove a commit was reviewed, because anyone who can push can
create a tag. In **Settings → Rules → Rulesets**, add a tag ruleset:

- **Targets:** `v*`, `dms-pre-*` and `cs-pre-*`.
- **Restrict creations** and **Restrict updates** for all three, so only the bypass list can create
  or move a release tag.
- **Restrict deletions** for `v*` only. `On Release` deletes superseded `dms-pre-*` and `cs-pre-*`
  prereleases and their tags with the workflow's own `GITHUB_TOKEN`, which is not on the bypass
  list; restricting their deletion would leave the release deleted and the tag in place, and fail
  the run. Deleting a prerelease tag publishes nothing.
- **Bypass list:** the release maintainers, and the identity that owns `EDFI_BUILD_AGENT_PAT`,
  which creates the prerelease tags.

Add the build-agent identity to the bypass list only after step 3. Until then any branch can read
its token, and with it create, move or delete any release tag.

### Docker Scout identity

Done: `DOCKER_SCOUT_USERNAME` and `DOCKER_SCOUT_TOKEN` are set, and the pull request Docker checks
log in and scan with them (see Verification).

1. The Docker Hub account is used only for this. It must not be a member of the `edfialliance`
   organization and must never be given write access to any `edfialliance` repository.
2. Its token should have the narrowest scope Scout accepts: try **Public Repo Read-only**, then
   **Repo Read-only**. Whatever the scope, the account itself has no `edfialliance` write access,
   so the token cannot push, overwrite or delete any `edfialliance` image. See
   [Access token permissions](https://docs.docker.com/security/access-tokens/reference/).
3. It is stored as repository settings, because pull request jobs have no environment:

   ```shell
   gh variable set DOCKER_SCOUT_USERNAME --body "<scout-account>" -R Ed-Fi-Alliance-OSS/Data-Management-Service
   gh secret set DOCKER_SCOUT_TOKEN -R Ed-Fi-Alliance-OSS/Data-Management-Service
   ```

## When a publishing run fails

Rerunning a failed run does not pick up a fix. A rerun, and any new run a tag starts, uses the
workflow files of that run's commit. A commit from before this change has no `environment:` on its
jobs, so after step 3 it cannot read any token.

- **A `main` build or prerelease:** start a new run from a commit that includes this change. The
  next merge to `main` cuts a new prerelease; to publish sooner, start the workflow by hand on
  `main`.
- **A release tag on an older commit:** do not rerun it, and do not move the tag. A release
  maintainer creates the next version's tag on a commit that includes this change; the ruleset
  from step 4 allows that for the bypass list only.
- **A run waiting for approval:** a reviewer approves it under **Deployments**, or rejects it and
  starts a new one.

## Read-only inspection

These commands only read settings, and none of them print a secret value:

```shell
# Environments and their protection rules
gh api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/environments
gh api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/environments/publishing/deployment-branch-policies
gh api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/environments/release-automation/deployment-branch-policies

# Secret names, by scope
gh secret list -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh secret list --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh secret list --env release-automation -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/organization-secrets --jq '.secrets[].name'

# Tag rulesets
gh api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/rulesets

# Variables
gh variable list -R Ed-Fi-Alliance-OSS/Data-Management-Service
```

After setup step 3, neither the repository nor the organization-secrets listing may include
`AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN`, `AZURE_ARTIFACTS_USER_NAME`, `DOCKER_HUB_TOKEN` or
`EDFI_BUILD_AGENT_PAT`.

## Verification

Each check starts a workflow, so run it only once you are authorized to.

| Check | Status |
| --- | --- |
| 1. A scratch branch cannot publish | Not run. Needs setup steps 1 to 3 |
| 2. Docker checks | Pull request run passed (see below). The next push to `main` and a manual run are not run yet |
| 3. Template and smoke pull request runs | Passed (see below) |
| 4. Prerelease dry run | Passed (see below) |
| 5. A real publish waits for approval | Not run. Needs setup steps 1 to 3 |
| 6. Prerelease tagging still works | Not run. Needs setup steps 1 to 4 |
| 7. Release cleanup still works | Not run. Needs setup step 4 and the next stable release |

1. **A scratch branch cannot publish or read the build-agent token.** Push a throwaway branch and
   start these on it:

   ```shell
   gh workflow run on-release.yml --ref <scratch-branch>
   gh workflow run on-prerelease.yml --ref <scratch-branch>
   gh workflow run "Pkg EdFi.Api.SecurityMetadataTools.yml" --ref <scratch-branch> -f publish_package=true
   gh workflow run "Pkg EdFi.Api.Sdk.yml" --ref <scratch-branch> -f publish_package=true
   gh workflow run EdFi.Api.Minimal.Template.PostgreSQL.yml --ref <scratch-branch> -f publish_package=true
   gh workflow run scheduled-merge-queue-enqueue.yml --ref <scratch-branch>
   ```

   In each run, every job that names `publishing` must be skipped, no deployment to `publishing` may
   appear under **Deployments**, and no package or image may change. The template run builds with
   its credential-free job only. The merge-queue run must fail at its `release-automation`
   deployment, because the branch is not allowed.
2. **Docker checks.** A pull request that changes `src/dms/Dockerfile`, the next push to `main`,
   and `gh workflow run on-pullrequest-dockerfile.yml --ref main` must each pass Hadolint, the
   image build and the Docker Scout scan, using `DOCKER_SCOUT_USERNAME`.
3. **Template and smoke pull request runs.** A pull request that changes `eng/DatabaseTemplates/**`
   runs all four template workflows and the smoke test. Each must pass, with the publish job
   skipped.
4. **Prerelease dry run.** `gh workflow run on-prerelease.yml --ref main` must succeed without
   the `publishing` environment. Then `gh run download <run-id>` must contain the
   `EdFi.Api.Plugins`, `EdFi.Api.CustomValidation`, `EdFi.Api.Identity` and `EdFi.Api.Secrets`
   `-NuGet` and `-SBOM` artifacts, and their `edfiApi*.intoto.jsonl` provenance files.
5. **A real publish waits for approval.** The next `dms-pre-*` or `cs-pre-*` prerelease, `main`
   template build and `v*` release must each stop at the `publishing` approval and publish only
   after a reviewer other than the person who started it approves.
6. **Prerelease tagging still works.** The next merge to `main` that changes `src/dms/**` or
   `src/config/**` must deploy to `release-automation` without approval and create its
   `dms-pre-*` or `cs-pre-*` prerelease and tag under the ruleset. Creating a `dms-pre-*` tag as a
   collaborator who is not on the bypass list must be refused.
7. **Release cleanup still works.** The next stable release's `On Release` run must delete each
   superseded prerelease and its tag, and pass.

Recorded on pull request #1419 at commit `3073af017`, before the `publishing` environment existed
and while the organization secrets were still shared with this repository:

- **Docker checks:** [On Pull Request - Dockerfile](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476473627)
  passed Hadolint, the image build and the Docker Scout scan for both Dockerfiles, after logging in
  with `DOCKER_SCOUT_USERNAME`.
- **Template and smoke runs:** all passed, each with every publish job skipped:
  [Minimal MsSql](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476474838),
  [Minimal PostgreSQL](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476474974),
  [Populated MsSql](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476474651),
  [Populated PostgreSQL](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476474532)
  and [Scheduled Smoke Test](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41476474646).
- **Prerelease dry run:** [a manual run on the pull request branch](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41485910918)
  passed. It produced the `-NuGet` and `-SBOM` artifacts and the `edfiApi*.intoto.jsonl`
  provenance for `EdFi.Api.Plugins`, `EdFi.Api.CustomValidation`, `EdFi.Api.Identity` and
  `EdFi.Api.Secrets`, and checked all four contracts against the feed without a token. Every
  publish, Docker Hub and release-attachment job was skipped.

These runs show the workflows build, scan and dry-run without publishing credentials. They do not
show that the credentials are protected: checks 1, 5, 6 and 7, and the inspection above, remain
outstanding until the setup is done.

1. **A scratch branch cannot publish.** Push a throwaway branch and start these on it:

   ```shell
   gh workflow run on-release.yml --ref <scratch-branch>
   gh workflow run on-prerelease.yml --ref <scratch-branch>
   gh workflow run "Pkg EdFi.Api.SecurityMetadataTools.yml" --ref <scratch-branch> -f publish_package=true
   gh workflow run "Pkg EdFi.Api.Sdk.yml" --ref <scratch-branch> -f publish_package=true
   gh workflow run EdFi.Api.Minimal.Template.PostgreSQL.yml --ref <scratch-branch> -f publish_package=true
   ```

   In each run, every job that names `publishing` must be skipped, no deployment to `publishing` may
   appear under **Deployments**, and no package or image may change. The template run builds with
   its credential-free job only.
2. **Docker checks.** A pull request that changes `src/dms/Dockerfile`, the next push to `main`,
   and `gh workflow run on-pullrequest-dockerfile.yml --ref main` must each pass Hadolint, the
   image build and the Docker Scout scan, using `DOCKER_SCOUT_USERNAME`.
3. **Template and smoke pull request runs.** A pull request that changes `eng/DatabaseTemplates/**`
   runs all four template workflows and the smoke test. Each must pass, with the publish job
   skipped.
4. **Prerelease dry run.** `gh workflow run on-prerelease.yml --ref main` must succeed without
   the `publishing` environment. Then `gh run download <run-id>` must contain the
   `EdFi.Api.Plugins`, `EdFi.Api.CustomValidation`, `EdFi.Api.Identity` and `EdFi.Api.Secrets`
   `-NuGet` and `-SBOM` artifacts, and their `edfiApi*.intoto.jsonl` provenance files.
5. **A real publish waits for approval.** The next `dms-pre-*` or `cs-pre-*` prerelease, `main`
   template build and `v*` release must each stop at the `publishing` approval and publish only
   after a reviewer other than the person who started it approves.
