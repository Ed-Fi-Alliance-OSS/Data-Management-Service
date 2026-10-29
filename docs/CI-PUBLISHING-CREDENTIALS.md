# CI Publishing Credentials

This page covers how the GitHub workflows in this repository hold the credentials that publish
packages and images, and what has to be set up in GitHub, Azure DevOps and Docker Hub for that to
work. It follows security review findings 1 to 3 (DMS-1647, DMS-1648 and DMS-1649).

## The credentials

| Name | Kind | Where it lives | Used by |
| --- | --- | --- | --- |
| `AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN` | secret | `publishing` environment only | push and promote steps for the EdFi Azure Artifacts feed |
| `AZURE_ARTIFACTS_USER_NAME` | secret | `publishing` environment only | promote steps |
| `DOCKER_HUB_TOKEN` | secret | `publishing` environment only | Docker Hub login in the jobs that push `edfialliance` images |
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
- Reusable workflows still name the Azure secrets under `workflow_call.secrets`, and their callers
  pass them by name. GitHub requires this even for a secret that exists only in an environment.
  The caller job has no environment, so what it passes resolves to nothing outside the reusable
  workflow's `publishing` jobs. See
  [Reuse workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows).

`eng/ci/tests/ReleasePublishingCredentials.Tests.ps1` enforces these rules for every workflow in
`.github/workflows`, and runs in the DMS pull request lane.

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
complete only once setup step 4 below is done.

## Setup

These steps change GitHub, Azure DevOps and Docker Hub settings. None of them have been done yet.
Do them in this order. Merging this change is the last item of step 4.

### 1. Create the `publishing` environment and protect it

In **Settings → Environments → New environment**, create `publishing` with:

1. **Required reviewers.** Add the release maintainers' team and turn on **Prevent self-review**,
   so the person who started a run cannot approve it. Before approving, a reviewer checks that the
   run's commit is on `main`, or that its tag points at a commit that was reviewed and merged.
2. **Deployment branches and tags: Selected branches and tags.** Add a branch rule for `main` and
   tag rules for `v*`, `dms-pre-*` and `cs-pre-*`.
3. Turn off **Allow administrators to bypass configured protection rules**.

A tag rule alone does not prove a commit was reviewed, because anyone who can push can create a
tag. Add a tag ruleset (**Settings → Rules → Rulesets**) that lets only the release automation and
release maintainers create, update or delete `v*`, `dms-pre-*` and `cs-pre-*` tags. The
`dms-pre-*` and `cs-pre-*` tags are created by the merge-or-tag workflows, so the ruleset's bypass
list must include the identity those workflows use.

Create the environment **before** merging this change. GitHub creates an environment automatically
the first time a job names it, and an automatically created environment has no protection rules.

### 2. Put the publishing tokens in the environment

GitHub never returns a secret's value, so the existing organization secrets cannot be read and
moved. Whoever holds the Azure DevOps and Docker Hub publishing credentials has to supply them
again, or issue new ones. To add each to the environment without leaving it in shell history:

```shell
gh-work secret set AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh-work secret set AZURE_ARTIFACTS_USER_NAME --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh-work secret set DOCKER_HUB_TOKEN --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
```

Each command prompts for the value. Never paste a token into an issue, a log or this repository.

### 3. Provision the Docker Scout identity

1. Create a Docker Hub account used only for this. It must not be a member of the `edfialliance`
   organization and must never be given write access to any `edfialliance` repository.
2. Create a personal access token for it. Start with **Public Repo Read-only**, then try
   **Repo Read-only** if Scout rejects that. If neither works, give the token only the narrowest
   scope Scout accepts. That is safe because the account itself has no `edfialliance` write access,
   so whatever its token's scope, it cannot push, overwrite or delete any `edfialliance` image. See
   [Access token permissions](https://docs.docker.com/security/access-tokens/reference/).
3. Store it as repository-scoped settings. Pull request jobs, which have no environment, need it.
   That is acceptable only because the account has no `edfialliance` write access, so the token
   cannot push, overwrite or delete any `edfialliance` image:

   ```shell
   gh-work variable set DOCKER_SCOUT_USERNAME --body "<scout-account>" -R Ed-Fi-Alliance-OSS/Data-Management-Service
   gh-work secret set DOCKER_SCOUT_TOKEN -R Ed-Fi-Alliance-OSS/Data-Management-Service
   ```

Docker's documentation says Scout needs a Docker Hub login, but it does not say a read-only token is
enough. See
[Docker Scout GitHub Actions](https://docs.docker.com/scout/integrations/ci/gha/) and the
[scout-action README](https://github.com/docker/scout-action/blob/v1.24.0/README.md). This has not
been verified yet. The first manual run of **On Pull Request - Dockerfile** confirms it (see
Verification step 2). If it does not, follow item 2 above: widen the token only as far as Scout
requires, and keep the account without `edfialliance` write access.

### 4. Remove the old access, check it, then merge

Do this as one short, coordinated cutover once steps 1 to 3 are done. An environment secret takes
precedence only inside jobs that name the environment. Every other job, including one a branch
adds, still reads a repository or organization copy. So the old access has to be gone before the
new workflows are relied on.

1. Pause publishing. Do not merge to `main`, create a release or start a publishing run until item 5.
   The workflows already on `main` read the old copies, so any publish in this window fails. That
   is expected, and better than publishing while any branch can still read the tokens.
2. Delete any repository secret named `AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN`,
   `AZURE_ARTIFACTS_USER_NAME` or `DOCKER_HUB_TOKEN`.
3. These are organization secrets today, and they are shared with this repository. An organization
   owner removes this repository from each one's selected repositories. If a secret is visible to
   all repositories, the owner changes it to selected repositories and keeps every other repository
   that still needs it. Do not delete an organization secret other repositories depend on just for
   this repository.
4. Check with the commands in [Read-only inspection](#read-only-inspection). The repository and
   organization-secret listings must not show the three names, and the `publishing` environment
   listing must show all three.
5. Merge this change, then resume publishing. Rerun any publish that failed during the cutover.

Until items 2 and 3 are done, pull requests and manual runs on any branch can still read the
tokens, and the security findings remain open.

## Read-only inspection

These commands only read settings, and none of them print a secret value:

```shell
# Environments and their protection rules
gh-work api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/environments
gh-work api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/environments/publishing/deployment-branch-policies

# Secret names, by scope
gh-work secret list -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh-work secret list --env publishing -R Ed-Fi-Alliance-OSS/Data-Management-Service
gh-work api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/organization-secrets --jq '.secrets[].name'

# Variables
gh-work variable list -R Ed-Fi-Alliance-OSS/Data-Management-Service
```

After setup step 4, neither the repository nor the organization-secrets listing may include
`AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN`, `AZURE_ARTIFACTS_USER_NAME` or `DOCKER_HUB_TOKEN`.

## Verification

None of these runs has happened yet. Each starts a workflow, so run them only once you are
authorized to.

1. **A scratch branch cannot publish.** Push a throwaway branch and start these on it:

   ```shell
   gh-work workflow run on-release.yml --ref <scratch-branch>
   gh-work workflow run on-prerelease.yml --ref <scratch-branch>
   gh-work workflow run "Pkg EdFi.Api.SecurityMetadataTools.yml" --ref <scratch-branch> -f publish_package=true
   gh-work workflow run "Pkg EdFi.Api.Sdk.yml" --ref <scratch-branch> -f publish_package=true
   gh-work workflow run EdFi.Api.Minimal.Template.PostgreSQL.yml --ref <scratch-branch> -f publish_package=true
   ```

   In each run, every job that names `publishing` must be skipped, no deployment to `publishing` may
   appear under **Deployments**, and no package or image may change. The template run builds with
   its credential-free job only.
2. **Docker checks.** A pull request that changes `src/dms/Dockerfile`, the next push to `main`,
   and `gh-work workflow run on-pullrequest-dockerfile.yml --ref main` must each pass Hadolint, the
   image build and the Docker Scout scan, using `DOCKER_SCOUT_USERNAME`.
3. **Template and smoke pull request runs.** A pull request that changes `eng/DatabaseTemplates/**`
   runs all four template workflows and the smoke test. Each must pass, with the publish job
   skipped.
4. **Prerelease dry run.** `gh-work workflow run on-prerelease.yml --ref main` must succeed without
   the `publishing` environment. Then `gh-work run download <run-id>` must contain the
   `EdFi.Api.Plugins`, `EdFi.Api.CustomValidation`, `EdFi.Api.Identity` and `EdFi.Api.Secrets`
   `-NuGet` and `-SBOM` artifacts, and their `edfiApi*.intoto.jsonl` provenance files.
5. **A real publish waits for approval.** The next `dms-pre-*` or `cs-pre-*` prerelease, `main`
   template build and `v*` release must each stop at the `publishing` approval and publish only
   after a reviewer other than the person who started it approves.
