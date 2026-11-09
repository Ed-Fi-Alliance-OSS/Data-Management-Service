# Operational Concerns

These notes generally apply to any application in the DMS platform.

## Configuration

See [Configuration](./CONFIGURATION.md)

## Deployment

The DMS software is designed for operation on-premises or using cloud-based
managed services. However, the Ed-Fi Alliance will not necessarily provide
detailed deployment orchestration for various environments.

The applications are built in a container-first fashion. A Kubernetes topology,
and potentially Docker Compose topology, is provided for basic testing and
demonstration purposes. These artifacts might be useful for production
deployments into Kubernetes. Anyone using them as such should review carefully,
particularly with respect to security concerns.

Although the application testing process will focus on the container-based
integration, these applications should be able to run on "bare metal" (or
virtual machine) without a container.

### Database versions

On PostgreSQL, the Configuration Service requires PostgreSQL 15 or later. Its
database upgrade creates unique constraints with `NULLS NOT DISTINCT`, which
earlier versions reject, so on PostgreSQL 13 or 14 the upgrade stops and the
Configuration Service does not start. Upgrade PostgreSQL before upgrading the
Configuration Service.

DMS requires PostgreSQL 18 or later with a UTF-8 database encoding. Descriptor
identity will depend on PostgreSQL's builtin `pg_c_utf8` collation, which exists
only in UTF-8 databases. A server shared by DMS and the Configuration Service must
meet the DMS floor. SQL Server deployments are unaffected.

SchemaTools (`ddl provision`) checks both requirements before it runs any DDL:

- With `--create-database`, it first connects to the server's `postgres`
  maintenance database and checks `server_version_num` (at least `180000`) and the
  encoding of the target database if it exists, or of `template1` if it does not.
  Only then does it run `CREATE DATABASE "<name>" ENCODING 'UTF8'`. It does not
  select `template0` or a locale to force UTF-8 onto a cluster whose `template1`
  is not UTF-8; initialize such a cluster with a UTF-8 encoding instead.
- Before any DDL, with or without `--create-database`, it checks the server
  version and the encoding of the target database itself.

A failed check stops provisioning before anything is created and prints this
message, followed by the value that failed:

```text
DMS requires PostgreSQL 18 or later with a UTF-8 database encoding. Upgrade the server to PostgreSQL 18 or later and provision into a UTF-8 database; when SchemaTools creates the database, the server's template1 database must be UTF-8. Detected: server_version_num 170000.
```

The detected value is `server_version_num <n>`, `target database encoding <encoding>`
or `template1 encoding <encoding>`. Managed (CDC) provisioning checks the server
before it records the database-creation intent, so after upgrading the server the
same command can be run again.

PostgreSQL cannot change the encoding of an existing database. A database that
is not UTF-8 must be re-created as UTF-8 and its data moved into it, for example
with `pg_dump` and `pg_restore`, before DMS is upgraded.

**Docker volumes created before PostgreSQL 18.** From PostgreSQL 18 the official
`postgres` image keeps its cluster in a versioned directory below a volume mounted
at `/var/lib/postgresql`, where earlier images mounted the volume at
`/var/lib/postgresql/data`. A PostgreSQL 16 volume mounted at the new location is
not read: PostgreSQL 18 initializes a new, empty cluster beside the old files.
Before the first start on 18, either remove the old volume (its data is lost) or
upgrade it with `pg_upgrade`. This applies to developer volumes from
`eng/docker-compose/postgresql.compose.yml`. The security-review VM's `update.sh` refuses
to update such a volume; see
[its update runbook](../eng/azure-vm/provision/UPDATE.md#part-b-update-to-a-newer-build--bash).

A later PostgreSQL major upgrade can change how `pg_c_utf8` folds case. Follow
[PostgreSQL major upgrades](POSTGRESQL-MAJOR-UPGRADE.md) before reopening DMS to
writes after such an upgrade.

## Plugins

A plugin is a directory of already-published assemblies that the Ed-Fi API loads at
startup and lets contribute to its configuration and its service composition. DMS
ships no fetcher: getting a plugin directory under the plugin root is a
**deployment** step that happens before the process starts, and it comes in two
recipes, below.

Acquiring the bytes does not enable them. A plugin runs if and only if its directory
name appears in `Plugins:Allowed`, which is a separate, deployment-owned setting
neither recipe writes. See [Plugins](./CONFIGURATION.md#plugins) for that setting and
for configuration precedence, and
[PLUGINS.md](../src/plugins/EdFi.Api.Plugins/PLUGINS.md) for what an implementer has
to do on the other side.

### Trust

**A loaded plugin runs with full process trust.** It can read every connection
string, every decrypted secret, every request body, and every token. .NET has no
code-access security, does not verify strong names when loading an assembly from a
path, and does not check Authenticode signatures at load on any platform. The
isolated load context each plugin gets isolates **assembly identity**; it is **not**
a security boundary. Every control below is about the integrity of what gets loaded,
not about containing it once loaded.

**Write access to the plugin root is equivalent to code execution as the host
process.** That sentence is the threat model. Because DMS ships no fetcher, the
posture is the same in every deployment: **the runtime identity never writes the
plugin root.** The plugin root must be writable by the deployment identity and not
by the runtime identity, and the `:ro` mount both recipes use is how a container
deployment states that. **DMS cannot verify it and does not claim to.**

The package digest in Recipe 2 is the second control, and it sits where the download
happens rather than inside DMS, so the bytes that reach the plugin root are bytes the
operator approved whether or not DMS was ever involved. A feed that serves its own
package hashes does not substitute for it: the point of pinning is that the
**operator** approved these bytes, not that the feed vouches for them.

What this does **not** protect against, stated so it is not rediscovered:

- A supply-chain compromise of a package the operator deliberately pinned. The digest
  proves the bytes did not change; it says nothing about what they do.
- An operator with write access to both the plugin root and the configuration, who
  updates a directory and its digest together.
- Anything a plugin does after it loads. There is no capability restriction, no
  resource limit, and no audit beyond the load inventory in the log.
- A plugin that hangs. The contribution hooks are synchronous and unbounded, so a
  plugin that blocks blocks startup. That is deliberate — a timeout would mean
  continuing without a plugin the operator required, which the failure rules below
  refuse. What you get instead is attribution: the loader announces each plugin
  before entering its hook, so the last line written names the plugin that hung.

### Plugin names are case-sensitive, on every filesystem

Every comparison the loader makes between an allowlist entry, a directory name, an
assembly name, and the plugin's own declared name is **ordinal**. The released image
is Linux and its filesystem is case-sensitive, so `Acme.Dms.Identity` and
`acme.dms.identity` are two different directories there.

**A case-insensitive filesystem does not soften that.** The loader never asks whether
a path exists, because a Windows or default macOS volume answers yes to that question
for a name that is not the name it was asked about. It lists the parent directory and
compares the spelling it reads back, ordinally, and it does so for the plugin
directory, the entry assembly, and the dependency manifest alike. A mis-cased name is
the same named startup failure on a developer machine as in the image, so it is
caught where the mistake is made rather than at deployment. Write the name exactly as
the directory is named.

The one deliberate exception is the duplicate check over `Plugins:Allowed`, which
folds case: two entries differing only in case are one directory on a
case-insensitive filesystem and two on the image's, so the allowlist is treated as
ambiguous and startup fails.

### The two recipes are alternatives, not additions

Both recipes end at the single `/app/plugins` mount target, and two Compose overlays
cannot both claim it. **Pick one per DMS service.** Each is a Compose overlay added
with its own `-f`; the base Compose files are unchanged by either, which is why a
deployment that uses no plugin materialises no plugin directory at all.

Everything a deployment has to supply is an environment variable declared with `:?`,
so the committed files run as they stand and a second deployment does not have to
edit them.

### Recipe 1: a pre-populated plugin root

The operator publishes plugin directories on the host and bind-mounts the tree
read-only. `DMS_PLUGINS_MOUNT_SOURCE` is the one required value: the host path that
holds the plugin directories.

<!-- embed: eng/docker-compose/plugins-dms.yml -->
```yaml
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Plugin acquisition, recipe 1: a pre-populated plugin root, bind-mounted read-only.
#
# Add with its own -f. It is an alternative to plugins-fetch-dms.compose.yml rather than an addition
# to it: /app/plugins is one mount target and two overlays cannot both claim it.
#
# DMS_PLUGINS_MOUNT_SOURCE is declared with :? rather than a default. A deployment that
# composed this file in meant to supply a host path, and a silent default would make Docker
# materialize an empty root-owned directory beside it, which is exactly what keeping the
# mount out of the base files avoids.
#
# Acquiring the bytes does not enable them. Nothing here writes Plugins:Allowed; the
# allowlist is a separate deployment-owned setting. See eng/docker-compose/README.md,
# "Loading plugins".
services:
  dms:
    volumes:
      - ${DMS_PLUGINS_MOUNT_SOURCE:?set the host path holding the plugin directories}:/app/plugins:ro
```

Nothing in this recipe writes into the mounted directory. Replacing a plugin's
contents is the operator's own step, and the stop-fetch-start rule below applies to
it for the same reason it applies to Recipe 2.

### Recipe 2: a pinned package fetched by the deployment

A deployment that would rather pull a published package than provision a host volume
does so in a one-shot service that runs to completion before DMS starts. It downloads
the `.nupkg`, verifies it against a digest the operator pinned, clears the target
directory, extracts the plugin directory into a named volume, and exits. DMS then
mounts that volume read-only exactly as in Recipe 1.

The three required values are `PLUGIN_PACKAGE_URL`, `PLUGIN_PACKAGE_SHA256`, and
`PLUGIN_NAME`.

<!-- embed: eng/docker-compose/plugins-fetch-dms.compose.yml -->
```yaml
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Plugin acquisition, recipe 2: a pinned package fetched, digest-verified and extracted by a
# one-shot service that runs to completion before DMS starts.
#
# Add with its own -f. It is an alternative to plugins-dms.yml rather than an addition to it:
# /app/plugins is one mount target and two overlays cannot both claim it.
#
# All three deployment-supplied values are declared with :? rather than defaults. The package
# address, its SHA-256 and the plugin directory name inside it are facts about one operator's
# plugin, and a committed file carrying any of them as a literal could not be run by a second
# deployment without being edited.
#
# Acquiring the bytes does not enable them. Nothing here writes Plugins:Allowed; the allowlist
# is a separate deployment-owned setting. See eng/docker-compose/README.md, "Loading plugins".
#
# Repeat deployments are stop, fetch, start. service_completed_successfully orders container
# startup and nothing more: it does not keep a completed one-shot service away from the files of a
# container already running. Composing this file again over a stack that is up starts the fetcher
# while DMS keeps running on the same volume, and the fetcher clears the plugin directory before it
# extracts. Because assemblies load lazily, a request arriving afterwards can find a declared file
# missing or load a dependency from a version other than the one the startup inventory recorded.
# Take the stack down before fetching again. See the same README section.
services:
  fetch-plugins:
    # Pinned by digest, as every third-party image in this directory already is. An operator may
    # re-pin to an image they have approved; a mutable tag would leave the step that verifies the
    # package digest running on bytes nobody pinned. This one is alpine:3.24.3, carrying BusyBox
    # 1.37.0, whose wget, sha256sum and unzip are the four commands below.
    image: alpine@sha256:be2656061b536c1883c4034790e21d0a09c5aaaf51968b89368a3c0371ea2c9a
    environment:
      PLUGIN_PACKAGE_URL: ${PLUGIN_PACKAGE_URL:?set the .nupkg download address}
      PLUGIN_PACKAGE_SHA256: ${PLUGIN_PACKAGE_SHA256:?set the SHA-256 of the pinned .nupkg}
      PLUGIN_NAME: ${PLUGIN_NAME:?set the plugin directory name inside the package}
    volumes:
      - plugins:/out
    # PLUGIN_NAME is checked before it reaches rm or mv, and the check is the same single-path-
    # segment rule the loader applies to an allowlist entry. Without it a name of ".." would make
    # this recipe delete the volume root. Both destructive commands stay inside /out, which is the
    # named volume below.
    #
    # Clearing the directory before extracting is part of the recipe rather than tidiness: it is
    # what makes the digest a statement about what is on disk rather than only about what was
    # downloaded. Without it, changing a pinned version leaves files from the previous version
    # beside the new ones, covered by no digest and named by no .deps.json.
    #
    # A PLUGIN_NAME matching nothing leaves unzip exiting zero over an empty directory, and the
    # chained mv is what fails the service loudly under sh -ec.
    command: >
      sh -ec '
      case "$$PLUGIN_NAME" in "" | "." | ".." | [!A-Za-z0-9]* | *[!A-Za-z0-9._-]*) echo "PLUGIN_NAME must be a single path segment matching [A-Za-z0-9][A-Za-z0-9._-]*" >&2; exit 2;; esac;
      wget -qO /tmp/plugin.nupkg "$$PLUGIN_PACKAGE_URL" &&
      echo "$$PLUGIN_PACKAGE_SHA256  /tmp/plugin.nupkg" | sha256sum -c - &&
      rm -rf "/out/$$PLUGIN_NAME" &&
      unzip -q /tmp/plugin.nupkg "contentFiles/any/any/$$PLUGIN_NAME/*" -d /tmp/x &&
      mv "/tmp/x/contentFiles/any/any/$$PLUGIN_NAME" /out/'
    logging:
      driver: json-file
      options:
        max-size: "${DOCKER_LOG_MAX_SIZE:-50m}"
        max-file: "${DOCKER_LOG_MAX_FILE:-5}"
  dms:
    depends_on:
      fetch-plugins:
        condition: service_completed_successfully
    volumes:
      - plugins:/app/plugins:ro
volumes:
  plugins:
```

**`PLUGIN_PACKAGE_URL`** is the package's download address in the feed's
`PackageBaseAddress` form, `<base>/<id>/<version>/<id>.<version>.nupkg` in lower
case, where `<base>` is what the feed's `index.json` publishes for that resource. It
differs between feed hosts, which is one more reason the address belongs to the
deployment rather than to DMS. Feed credentials, proxies, and mirrors are the
deployment's business, so a private feed works with the same secret-mounting
facilities the deployment already uses.

**`PLUGIN_PACKAGE_SHA256`** is a SHA-256 over the `.nupkg` **bytes**, which is what
`sha256sum -c` verifies before anything is extracted:

```shell
shasum -a 256 acme.dms.identity.1.2.0.nupkg
```

The package is digested rather than the extracted directory because a `.nupkg` is a
single file with a natural digest that an operator computes in one command and a
build system emits for free. Digesting a directory instead would mean specifying a
content-manifest algorithm — path ordering, separator normalisation, symbolic links,
case-insensitive filesystems — that every operator and every build tool would have to
reimplement identically.

**Clearing `<PluginRoot>/<Name>/` before extracting is part of the recipe rather than
tidiness.** It is what makes the digest a statement about what is **on disk** rather
than only about what was downloaded. Without it, changing a pinned version leaves
files from the previous version beside the new ones, covered by no digest and named
by no `.deps.json`, and the next person to read that directory cannot tell which
version they are looking at.

**The `fetch-plugins` image is pinned by digest**, following the convention every
third-party image in `eng/docker-compose/` already uses — `postgresql.compose.yml` pins its
Postgres image the same way. The committed digest is a real, pullable `alpine` 3.22
image, so the recipe is runnable as it stands rather than a placeholder that must be
replaced before it works. An operator may re-pin it to an image they have approved; a
mutable tag would leave the step that verifies the package digest running on bytes
nobody pinned.

#### Re-fetching is stop, fetch, start

**Take the stack down before fetching again.** `depends_on` with
`condition: service_completed_successfully` orders container **startup**: it holds
DMS back until the one-shot service has exited zero, and it says nothing about that
service running again beside a container that is already up. Composing the deployment
again over a live stack does exactly that, because Compose recreates a service whose
definition changed and leaves an unchanged one running — and the fetcher clears the
plugin directory before it extracts into it.

The volume is mounted read-only into DMS, which stops DMS writing it and does nothing
about a writer outside the container. Assemblies load lazily, so the process does not
hold every declared file open: a request arriving during or after the swap can meet a
missing file, or load a dependency from a version other than the one the startup
inventory recorded — and that inventory is the one record tying a running process to
the bytes it is serving. Nothing inside a Compose file can prevent this, so the
requirement lives in the recipe instead.

Recipe 1 has no fetch step, but the same rule applies to replacing the contents of
its mounted directory, for the same reason.

### Recipe 2 on Kubernetes

The same four commands run in an `initContainers` entry that writes to an `emptyDir`
the DMS container mounts `readOnly: true`. The `PLUGIN_NAME` check is the same
single-path-segment rule the loader applies to an allowlist entry, and it runs before
`rm` and `mv` so that a name of `..` cannot make the recipe delete the volume root.

```yaml
spec:
  volumes:
    - name: plugins
      emptyDir: {}
  initContainers:
    - name: fetch-plugins
      # Pinned by digest for the same reason the Compose recipe pins it.
      image: alpine@sha256:be2656061b536c1883c4034790e21d0a09c5aaaf51968b89368a3c0371ea2c9a
      env:
        # Supply these from a ConfigMap, or from a Secret when the feed address
        # carries credentials. They are the deployment's own values, exactly as the
        # three :?-required variables are in the Compose recipe.
        - name: PLUGIN_PACKAGE_URL
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginPackageUrl }
        - name: PLUGIN_PACKAGE_SHA256
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginPackageSha256 }
        - name: PLUGIN_NAME
          valueFrom:
            configMapKeyRef: { name: dms-plugins, key: pluginName }
      volumeMounts:
        - name: plugins
          mountPath: /out
      command: ["sh", "-ec"]
      args:
        - |
          case "$PLUGIN_NAME" in "" | "." | ".." | [!A-Za-z0-9]* | *[!A-Za-z0-9._-]*)
            echo "PLUGIN_NAME must be a single path segment matching [A-Za-z0-9][A-Za-z0-9._-]*" >&2
            exit 2
            ;;
          esac
          wget -qO /tmp/plugin.nupkg "$PLUGIN_PACKAGE_URL"
          echo "$PLUGIN_PACKAGE_SHA256  /tmp/plugin.nupkg" | sha256sum -c -
          rm -rf "/out/$PLUGIN_NAME"
          unzip -q /tmp/plugin.nupkg "contentFiles/any/any/$PLUGIN_NAME/*" -d /tmp/x
          mv "/tmp/x/contentFiles/any/any/$PLUGIN_NAME" /out/
  containers:
    - name: dms
      image: edfialliance/ed-fi-api@sha256:<the digest you pinned>
      volumeMounts:
        - name: plugins
          mountPath: /app/plugins
          # The runtime identity never writes the plugin root.
          readOnly: true
```

`sh -ec` is what makes this fail loudly: `-e` stops at the first command that fails,
so a digest mismatch never reaches the extraction. A `PLUGIN_NAME` that matches
nothing inside the package leaves `unzip` exiting zero over an empty directory, and
the `mv` is the command that then fails.

This form is not asserted against a committed file, because no committed file is its
artifact. The Compose blocks above are; see the note at the end of this chapter.

### When a plugin does not load, DMS does not start

**An allowlisted plugin that does not load is fatal.** Skip-and-continue is right
when a missing plugin degrades a capability whose absence the operator can see, and
for these plugins the absence is invisible: a validation plugin that silently did not
load means business rules stop being enforced while writes keep succeeding, and
nothing surfaces until the data is already wrong.

Two outcomes are warnings rather than failures, and a boot produces **at most one**
of them. Both come from the same step, which runs only once every allowlisted plugin
has already loaded, and neither can happen when `Plugins:Allowed` is empty: nothing
was asked for, so the root is never listed at all.

- **Directories under the plugin root that the allowlist does not name.** One
  aggregate warning, not one per directory: a single line listing every ignored
  name, ordinally sorted and comma-separated. Such a directory is never opened,
  never probed, and never has its metadata read. Replayed through the application
  logger as `Plugin loader warning UnallowlistedDirectories`, carrying the names.
- **The plugin root could not be listed.** When enumerating the root fails with an
  I/O or permission error, whether it holds unallowlisted directories is unknown.
  That is not worth failing a boot whose plugins all loaded, so it warns instead.
  Replayed as `Plugin loader warning PluginRootNotListed`, carrying the error
  message and no directory names.

The second is why the absence of the first proves nothing. An operator who greps for
ignored directories and finds none has to rule out `PluginRootNotListed` before
concluding there were none to ignore.

The tables below group every failure by **what you do about it**.

#### Fix the allowlist

| Condition | What the failure says |
| --- | --- |
| A name repeated in `Plugins:Allowed`, before or after trimming, or differing only in case | The allowlist is ambiguous about what was approved. Names the entry. |
| A name that is not a single path segment: rooted, containing `/` or `\`, equal to `.` or `..`, or otherwise outside `[A-Za-z0-9][A-Za-z0-9._-]*` | Names the entry and the rule. Checked before the path is composed, because a rooted name would discard the plugin root entirely. |
| A composed plugin path that does not sit under the plugin root once symbolic links are resolved | Names the entry and both resolved paths. |
| An allowlisted directory, or its entry assembly, is missing | Names the expected path. Check the spelling first; the other cause is a directory the deployment did not deliver. |

#### Fix the deployment

| Condition | What the failure says |
| --- | --- |
| The plugin root is missing and `Plugins:Allowed` is non-empty | Plugins were asked for and cannot be delivered. Mount the root, or empty the allowlist. |
| The entry assembly is present but fails to load | Reports the load exception. The bytes that reached the root are not the bytes that were meant to. |

A plugin root that is missing while `Plugins:Allowed` is **empty or absent** is not
an error at all: nothing was asked for, and the root is not inspected.

#### Contact the implementer

| Condition | What the failure says |
| --- | --- |
| The entry assembly references a newer version of a contract assembly than the host carries | Names the plugin, the contract, the version required, and the version the host carries. Read before any type is loaded, so it is never a raw type-load error. |
| The plugin's `.deps.json` declares a higher assembly version than the host carries for an assembly the host also has | Names the plugin, the assembly, the version declared, and the version the host carries. |
| A managed assembly the plugin requests at run time resolves to a host copy older than its reference declares | Names the same four things. This is the backstop for a reference the `.deps.json` check could not see; see the known gaps below. |
| The plugin directory has no `.deps.json` | Names the plugin. Its private dependencies would not resolve, failing on a request rather than at startup. |
| The `.deps.json` declares a `runtimepack` library, which a self-contained publish produces | Names the plugin. A plugin is published framework-dependent. |
| The entry assembly exposes zero, or more than one, plugin class | Names what was found. |
| The plugin's declared name does not match its directory name | Names both. Compared ordinally. |
| The entry assembly's own assembly name does not match the directory name | Names both. |
| A contribution hook throws | Names the plugin and the hook, `ContributeConfiguration` or `ContributeServices`, and keeps the plugin's own exception as the cause. |
| The plugin's configuration hook removes or reorders a configuration source that was present before it ran | Names the plugin and the position and type of the source. Adding sources is the only permitted operation, and that includes leaving alone a source an earlier plugin in `Plugins:Allowed` added. |
| The plugin registers a service type the host owns, or removes, replaces, or overwrites a host-owned registration that existed before its hook ran | Names the plugin and the service type, before the call reaches the host's own collection. Plugins contribute registrations; they do not edit the host's. Removing a registration the plugin itself added is fine, and removing a pre-existing framework or third-party registration is permitted and recorded in the inventory. |
| The plugin removes, replaces, or overwrites one of the host's four logging registrations | Names the plugin and the service type. `ClearProviders()` lands here: it would silence DMS and the inventory record of the plugin doing it. Adding a logging **provider** is untouched. |
| The plugin **adds** its own unkeyed logger factory or logger registration | Names the plugin and the service type. Those are resolved singly, so the last registration wins and a plugin would displace the host's logging by adding rather than removing. Keyed logging registrations and added providers stay permitted. |
| The plugin registers a declared contract under a wildcard service key | Names the contract. Enumerable resolution cannot reach a wildcard registration, so the startup probe cannot cover it. |
| The plugin leaves no surviving declared contract registration and added no configuration source | Names the plugin and lists what it did register. It ran and contributed nothing the host will call — most often a plugin allowlisted on the wrong host, or a replace-contract claim made with a `TryAdd`, which declines invisibly. A plugin that added a configuration source and declared no plugin contract is not reported here, because the host reads what it contributed. One that declared contracts and had every one removed by a later plugin is reported whether or not it also added a source. |
| A registration of a declared contract cannot be constructed | Reported by the startup probe, which resolves each surviving declared-contract registration once before the process serves traffic. |

Every row in this group is about the plugin's own content rather than about how the
deployment is assembled, which is why its author is the party who can change it. Some
of them have a second remedy: the two version-skew rows and the runtime backstop name
a version **the host** carries, so running a host version the plugin already supports
resolves them as surely as a new plugin build would. Either way the allowlist is the
operator's immediate lever, and removing the plugin's name is what gets DMS started
again.

#### Resolve a conflict between two plugins

| Condition | What the failure says |
| --- | --- |
| Two plugins register the same single-claimant contract | Names both plugins and the contract. Only an operator can resolve it, by removing one of the two names from `Plugins:Allowed`. |
| One plugin registers the same single-claimant contract twice | Names that plugin and its registration count. No allowlist change can choose between two registrations made inside one plugin, so this one goes to its author. |

#### Known gaps, stated rather than implied

- **An unmanaged library a plugin declares but cannot resolve is not detected at
  load.** Native resolution is lazy by construction, so it surfaces as a
  `DllNotFoundException` on first use.
- **A managed assembly whose host copy is older than the plugin's reference declares
  refuses on the request thread when that first use falls after startup.** The
  refusal itself is the backstop row above; what changes is how it reaches you. The
  readable message is produced by a frame inside the loader, and once startup has
  completed there is no such frame: the refusal travels up the request pipeline as
  repeated HTTP 500s, with no process abort and no unwrapped message.

#### Two rules that apply only to configuration contribution

A plugin can contribute configuration sources from its `ContributeConfiguration`
hook, which DMS runs for every allowlisted plugin, in `Plugins:Allowed` order, as soon
as the plugins have loaded. Two rows above are specific to it:

- **A configuration hook that removes or reorders a source it did not add is
  fatal**, naming the plugin. Adding is the only permitted operation. The loader then
  places what the plugin added below the operator's unprefixed environment and
  command-line sources, and above the JSON files and the `ASPNETCORE_` and `DOTNET_`
  prefixed environment sources; see
  [Configuration precedence](./CONFIGURATION.md#configuration-precedence).
- **The "contributed nothing" row has a second half.** A plugin satisfies that row by
  registering a declared contract, or by contributing at least one configuration
  source, so a plugin whose only job is to supply configuration starts normally.

### Where to read the reason

The two questions have two answers, and they live in different places.

**Why startup failed: the startup status file.** Its path is
`AppSettings:StartupStatusFilePath`, defaulting to `dms-startup-status.json` in the
system temporary directory. It is a JSON document carrying the status, the bootstrap
phase, a one-line summary, and — on a failure — the exception type and message. A
plugin failure names its phase there. `LoadPlugins` covers a failure to load and a
failure inside a plugin's `ContributeConfiguration` hook, which runs in that same
phase as soon as loading returns. `ConfigureServices` covers a failure inside a
`ContributeServices` hook, because the host invokes that hook while it is registering
services.
`ValidatePluginRegistrations` covers a registration the host refuses after the container
is built: an invalid custom validator registration, or a plugin contribution the audit
rejects, such as two plugins replacing one contract.

**What actually loaded: the log.** Each loaded plugin produces one
`Plugin inventory for {PluginName} version {AssemblyVersion}` event listing the files
the plugin declared, the service types it registered, the registrations it removed,
and the host-first substitutions recorded up to that point. Because the design cannot
contain a plugin, that event is an **audit record**: it is how an incident responder
says which third-party code was available to the process, at what version, from which
bytes.

**What the substitution list does and does not prove.** It is narrower than the other
three, and an investigation into a dependency mismatch has to read it as what it is: a
startup snapshot of the version-changing substitutions observed so far. The event is
emitted once, before any startup task runs, and a resolution is recorded only where
the version the host served differs from the version the plugin declared. A
same-version substitution leaves no row, and neither does a resolution first triggered
after the event was written. An assembly's absence from the list is therefore not
evidence that the plugin's own copy ran.

**One ordering caveat.** Plugin loading runs before the logging pipeline exists, so
the loader writes its own lines to standard error. Its warnings are replayed through
the application logger once one exists, as `Plugin loader warning` events, so a
deployment that collects application logs rather than container output still sees
them. A **fatal** during loading is not replayed, because the process does not reach
the point where a logger exists: for that, read standard error and the startup status
file.

### The Configuration Service

The Configuration Service loads plugins with the same loader, from its own plugin
root and its own `Plugins:Allowed`. A plugin allowlisted for DMS is not thereby
allowlisted for the Configuration Service, and the reverse, so a plugin both hosts
use is delivered to and named in each. A plugin that registers a contract only one
host declares fails startup in the other, so only a plugin that adds configuration
and registers no contract can be used by both; see
[Secrets](./SECRETS.md#what-a-secrets-plugin-is). Everything above about trust,
case, and the stop-fetch-start rule applies unchanged. The plugin a Configuration
Service operator is most likely to install is a secrets plugin; see
[Secrets](./SECRETS.md) for what one serves and what adopting one does and does not
protect.

**The mount target is `/app/plugins` on the `config` service**, which is the
`Plugins:Directory` the Configuration Service's `appsettings.json` ships. As for
DMS, the two recipes below are alternatives that both claim it, so pick one.

The Configuration Service writes no startup status file. A plugin that does not load
stops it before it serves a request, and the reason is on standard error; see
[Configuration Service plugins](./CONFIGURATION.md#configuration-service-plugins).

#### Recipe 1 for the Configuration Service: a pre-populated plugin root

`plugins-config.yml` is the Configuration Service counterpart of `plugins-dms.yml`.
`CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE` is the one required value: the host path that holds the
Configuration Service's plugin directories. Add the file with its own `-f` after
`local-config.compose.yml` or `published-config.compose.yml`, followed by a deployment-owned
override that sets `Plugins__Allowed` on the `config` service, because this overlay
allowlists nothing. When the plugin serves secrets from a vault, that override also
removes them from the environment; see
[Secrets on the shipped Compose files](#secrets-on-the-shipped-compose-files).

<!-- embed: eng/docker-compose/plugins-config.yml -->
```yaml
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Plugin acquisition for the Configuration Service: a pre-populated plugin root, bind-mounted
# read-only. The Configuration Service counterpart of plugins-dms.yml, which it mirrors.
#
# Add with its own -f, after local-config.compose.yml or published-config.compose.yml, both of which it leaves
# unchanged. start-local-config.ps1 has no setting that adds it: DMS_PLUGINS_COMPOSE_FILES is the
# Data Management Service launchers' setting for DMS overlays, not a Configuration Service one.
#
# CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE is declared with :? rather than a default. A deployment that composed
# this file in meant to supply a host path, and a silent default would make Docker materialize an
# empty root-owned directory beside it, which is exactly what keeping the mount out of the base
# files avoids.
#
# Acquiring the bytes does not enable them. Nothing here writes Plugins:Allowed; the allowlist is
# a separate deployment-owned setting on the Configuration Service. See eng/docker-compose/README.md,
# "Loading plugins into the Configuration Service".
services:
  config:
    volumes:
      - ${CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE:?set the host path holding the plugin directories}:/app/plugins:ro
```

`DMS_PLUGINS_COMPOSE_FILES` is the DMS launchers' setting and does not add this
file, and `start-local-config.ps1` has no equivalent; compose it directly, as
[eng/docker-compose/README.md](../eng/docker-compose/README.md) shows.

#### Recipe 2 for the Configuration Service: a pinned package fetched by the deployment

There is no committed fetch overlay for the Configuration Service. The fetch itself
is the same: the same three required values, the same digest-pinned image, and the
same four commands, which check the plugin name, verify the package digest, clear
the target directory, and extract the plugin directory. Only where the result is
mounted differs. Either of two forms does it:

- **Into the host path Recipe 1 mounts.** Run the four commands as a deployment step
  that extracts into the directory `CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE` names, then compose
  `plugins-config.yml` exactly as in Recipe 1. Nothing about the overlay changes.
- **Into a volume, as `plugins-fetch-dms.compose.yml` does for DMS.** Copy that file into the
  deployment and point its last service block at `config` instead of `dms`. When DMS
  also uses Recipe 2 in the same Compose project, rename the `fetch-plugins` service
  and the `plugins` volume as well, because the two files would otherwise define one
  service and one volume for both hosts, and each host's plugin root would then hold
  the other's plugins. Rename every reference along with them: the service's key and
  the `depends_on` entry under `config` that names it, and the volume's top-level
  declaration together with both mounts that use it, the fetcher's `/out` and the
  `config` service's `/app/plugins:ro`.

On Kubernetes, the init-container form in
[Recipe 2 on Kubernetes](#recipe-2-on-kubernetes) applies unchanged, with the
Configuration Service container mounting the `emptyDir` at `/app/plugins` with
`readOnly: true`. As there, these forms are not asserted against a committed file,
because no committed file is their artifact.

### The Compose blocks above are asserted equal to the committed files

`eng/docker-compose/plugins-dms.yml`, `eng/docker-compose/plugins-fetch-dms.compose.yml`, and
`eng/docker-compose/plugins-config.yml` are the artifact. The end-to-end tiers run
those files as committed, and a check in this repository compares each block above
against its file, so a chapter edited without the file — or a file edited without the
chapter — fails. Nothing anywhere drives a recipe parsed out of this document, which
would prove the document rather than the file an operator actually composes with
`-f`.

For running the recipes against a local development stack through
`bootstrap-local-dms.ps1`, see
[eng/docker-compose/README.md](../eng/docker-compose/README.md).

### Secrets on the shipped Compose files

A secret a plugin serves from a vault must not also be supplied by the environment, because an environment variable outranks every plugin source; see [Secrets](./SECRETS.md#the-process-global-secrets-phase-a-serves).
The shipped Compose files map six of those secrets from `.env` variables, and deleting a variable does not remove the mapping.
For five of them, `local-dms.compose.yml` and `local-config.compose.yml` set the key to an empty string, which outranks the vault.
An empty `DatabaseSettings__EncryptionKey` stops the Configuration Service at startup.
The others do not: the host starts and then fails requests, and an empty `IdentitySettings__EncryptionKey` fails only once the self-contained identity provider issues its first token.
`published-dms.compose.yml` and `published-config.compose.yml` require both encryption-key variables instead, which feed three mappings: `CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY` feeds `ConfigurationServiceSettings__EncryptionKey` on `dms` and `DatabaseSettings__EncryptionKey` on `config`, and `CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY` feeds `IdentitySettings__EncryptionKey` on `config`.
Deleting either variable stops `docker compose config`, `up` and `down` with an error that names it; only teardown through `start-published-dms.ps1 -d` supplies an inert placeholder for a missing value.
In those files only the two client secrets still become empty strings.
The sixth, `DatabaseSettings__DatabaseConnection`, is mapped only on `config` and has a default in `local-config.compose.yml` and `published-config.compose.yml`, so deleting its variable sends the Configuration Service to the bundled `dms-postgresql` database instead of the one the vault names.

Remove each key the vault serves in the same deployment-owned override that sets
`Plugins__Allowed`, with `!reset null`. A service block goes only in the override for
a Compose project that defines that service: a project that runs only one host, such
as one started with `start-local-config.ps1`, keeps only that host's block, because
Compose rejects a block for a service the project does not define.

```yaml
services:
  dms:
    environment:
      ConfigurationServiceSettings__ClientSecret: !reset null
      ConfigurationServiceSettings__EncryptionKey: !reset null
  config:
    environment:
      DatabaseSettings__DatabaseConnection: !reset null
      DatabaseSettings__EncryptionKey: !reset null
      IdentitySettings__ClientSecret: !reset null
      IdentitySettings__EncryptionKey: !reset null
```

List only the keys the vault actually serves. Keep the matching `.env` variables
set: the reset already keeps their values out of the containers, and the local
launch scripts read them directly to register identity clients and provision
databases. For the two encryption-key variables the published files enforce this: Compose
interpolates every file before it applies `!reset null`, so a deleted variable stops
Compose even when the override resets its key.
`CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY` feeds both
`DatabaseSettings__EncryptionKey` on `config` and
`ConfigurationServiceSettings__EncryptionKey` on `dms`, so clearing it for one host
would blank the key the other still reads from the environment. With the connection
string removed, the stock Configuration Service entry point skips its database wait
and .NET reports a connection error itself if the database is not ready yet, so keep
a restart policy on `config`, as [Secrets](./SECRETS.md) describes.

Where the override sits decides whether its `config` block applies.
`DMS_PLUGINS_COMPOSE_FILES` is the DMS launchers' hook for DMS overlays: both launchers
insert its files right after the DMS service file and before `local-config.compose.yml` or
`published-config.compose.yml`. The `dms` block works there. A `config` reset in a file named
there does not: the Configuration Service file comes later and sets those keys again,
whichever Compose version you use. The launchers have no hook that adds a file after the
Configuration Service file, so a `config` reset needs a file composed after it, and in the
combined stack the separate limitation below applies even then.

> **Limitation in the combined DMS and Configuration Service stack, observed in affected
> Docker Compose versions.** This is independent of where the launchers insert overlays. The DMS
> launchers add `dms-cms-proxy.yml` after both service files, and
> that file sets `environment` on `config`. In an affected Compose version, a later `!reset null`
> on a `config` environment key is then not applied: the Configuration Service keys in this
> override can stay in the container, and their `.env` values outrank the vault values. The
> `dms` resets are not affected. This was verified locally with Compose v5.1.3 and has also
> been reported with v5.5.1; do not assume another version behaves differently. Before relying
> on the override, render the complete Compose
> file sequence you deploy with, override included, and confirm that every key you reset is
> absent. Check key names only, and do not publish or log the rendered configuration,
> because it contains the secret values. For example, this PowerShell check keeps the
> rendered configuration in a variable and prints only key names. It stops with an error
> unless Compose renders successfully and the result has a `config` service with an
> environment, so an empty result never passes for a render that did not include `config`:
>
> ```powershell
> $composeFiles = '<first file>', '<next file>', '<your override>'  # every -f file, in the order your deployment passes them
> $envFile = '<your env file>'
> $resetKeys = 'DatabaseSettings__DatabaseConnection', 'DatabaseSettings__EncryptionKey',
>     'IdentitySettings__ClientSecret', 'IdentitySettings__EncryptionKey'  # the config keys your override resets
>
> $fileArguments = foreach ($file in $composeFiles) { '-f'; $file }
> $rendered = docker compose @fileArguments --env-file $envFile config --format json 2>$null
> if ($LASTEXITCODE -ne 0) {
>     throw "docker compose config failed (exit code $LASTEXITCODE). Run the same command with 'config --quiet' to see the error without printing the configuration."
> }
> $config = (($rendered -join "`n") | ConvertFrom-Json).services.config
> if ($null -eq $config) { throw 'The rendered configuration has no config service; check the file list.' }
> if ($null -eq $config.environment) { throw 'The rendered config service has no environment; check the file list.' }
> $present = @($config.environment.PSObject.Properties.Name | Where-Object { $_ -in $resetKeys })
> if ($present.Count -gt 0) { throw "Still set on config: $($present -join ', ')" }
> 'Every reset key is absent from config.'
> ```
>
> Reordering the files or leaving out `dms-cms-proxy.yml` is not a validated workaround:
> that file carries the Configuration Service's forwarded-header trust and `/connect/*`
> rate-limit settings for the combined stack.

`DATABASE_CONNECTION_STRING_ADMIN` on `dms` is not a vault key, because only the DMS entry point reads it, but it carries database credentials.
`local-dms.compose.yml` gives it a default, a superuser connection string for the bundled `dms-postgresql` service; `published-dms.compose.yml` has no default and requires a value.
To keep it out of the container, keep the variable set and reset it in the same override with `DATABASE_CONNECTION_STRING_ADMIN: !reset null` under `dms`.
DMS then starts without waiting for PostgreSQL.

## Logging

See [Logging Policy](./LOGGING.md)

## Observability

Observability is closely related to logging, but goes beyond it. Open Telemetry
is an emerging standard for observability.

The following article provides additional information about Open Telemetry and
how it might be useful in the DMS platform. The article references the Project
Meadowlark application stack, but is equally applicable here: [What Is Open
Telemetry?](https://github.com/Ed-Fi-Exchange-OSS/Meadowlark/blob/main/docs/design/open-telemetry/README.md)

For the metrics the Ed-Fi API publishes for collection reads — traditional
paging, cursor paging, and partition planning — including the instrument names,
units, dimensions, and what collecting them requires of the host, see
[Collection Paging Telemetry](./PAGING-TELEMETRY.md).

## Security

### Transport Encryption

Those who are hosting the application are strongly encouraged to use TLS binding
at least at the gateway level. When running a container network, mutual TLS will
provide greater security in case someone is able to elevate privileges on one of
the services.

### Rate Limiting

The application gateway remains the best place to apply rate limiting and DoS
controls. Only the Ed-Fi API has a built-in rate limiter as a fallback; the
Configuration Service has none today. The API's limiter partitions on the raw
Host header value: clients sharing a hostname share one bucket, while varied
Host values create separate buckets, so it neither isolates clients nor
strictly bounds total backend load. It is a coarse backstop only and must not
be relied on for DoS or brute-force protection.

### Authentication and Authorization

See [Roles and Scopes](./ROLES-SCOPES.md)

### Deployment preflight for claim-set names and scope shapes

The Ed-Fi API resolves the one claim set a request uses from the token's scope
values, the Configuration Service rejects commas in claim-set names, and the
self-contained provider issues scopes separated by spaces. See
[Roles and Scopes](./ROLES-SCOPES.md#claim-set-selection-from-token-scopes) for the
rules. Complete the steps below **before** rolling out the release that contains
them. This is a deployment prerequisite for operators, not a code migration, and
nothing in the product performs it for you.

1. **Inventory comma-containing claim-set names.** List every claim set in every
   tenant whose name contains a comma. The new validation rejects such a name
   wherever a name is introduced or replaced through the API, import, and application
   paths (claim-set create, update, copy and import, application create and update),
   so an existing one can no longer be re-saved unchanged. Fragment registration skips
   registering an invalid name, logs it, and continues with the other claim sets, and
   the XML conversion tool throws its established `InvalidOperationException`.

   In **self-contained mode**, this inventory also prevents an unintended change in
   permissions. A client assigned `Alpha,Beta` stores separate `Alpha` and `Beta`
   permissions. Fresh tokens carry `Alpha Beta`, so DMS looks up those two names
   instead of the literal `Alpha,Beta` claim set. If neither name exists in the
   tenant, resource requests return `500`, identity-service requests return `403`,
   and `/oauth/token_info` returns `401`. If exactly one exists, requests use that
   claim set's permissions, which may differ from the application's assigned claim
   set. If both exist, requests return `403` for ambiguity. Skipping preflight can
   therefore change effective permissions as well as cause an outage. Keycloak
   stores the literal client scope name and does not perform this comma split.
2. **Inventory the references to those names.** List every application, and every
   identity-provider client or client scope (the Keycloak realm, or the
   `OpenIddictScope` and `OpenIddictApplicationScope` rows of the self-contained
   provider), that refers to a comma-containing claim-set name. A token carries its
   claim set as a scope, so a client's scope and the claim set's name must change
   together.
3. **Correct each name through the established management workflows.** Use the
   Configuration Service claim-set and application endpoints, and the identity
   provider's own administration, as for any other claim-set change. Do not
   auto-rename names with scripts against stored data, and do not edit stored client
   credentials: the key and secret of an existing application stay as they are.
4. **Obtain fresh tokens where the old scope shape is incompatible.** An issued
   token keeps the `scope` it was issued with until it expires. A token whose scope
   joined several values with commas is read as one literal value, because DMS and
   the Configuration Service do not split issued JWT claims on commas. Neither
   product rewrites or splits such a token. Having clients request a new token is
   what replaces it. The self-contained provider's change to space-separated output
   applies only to tokens issued after the release and does not repair old ones.
5. **Account for propagation.** Corrected claim sets reach the Ed-Fi API through its
   cached claim sets, per tenant and per replica, and are eventually consistent
   rather than an immediate revocation. Follow
   [How long a change takes to apply](./IDENTITY-MANAGEMENT.md#how-long-a-change-takes-to-apply)
   and [Reloading the claim-set cache](./IDENTITY-MANAGEMENT.md#reloading-the-claim-set-cache),
   remembering that a reload clears one replica and one tenant spelling at a time.

While a token's scopes recognize more than one claim set in a tenant, the Ed-Fi API
answers `403` with `The token identifies multiple claim sets.`, and a tenant whose
metadata holds two claim sets with names that differ only by case answers `500` for
a token that names them. Correct such tenants by renaming or removing the duplicate
through the same management workflows.

## Configuration Service database upgrades

The Configuration Service upgrades its own database with DbUp. The embedded
scripts run in order at startup when `DeployDatabaseOnStartup` is set, or
through the Installer, and each script that completes is journaled in
`dmscs_SchemaVersions`. A script that fails is not journaled: the service logs
`Database Deploy Failure` with the script's error and exits, and the whole
script runs again on the next start. Some scripts refuse to run against data
that the schema change would make invalid. That is deliberate: they change
nothing and describe what blocks them, and this section is the operator
procedure for resolving it.

Run an upgrade from one Configuration Service instance, or from the Installer,
with no other instance serving writes. Migration `0036` holds an exclusive
lock on `dmscs."ApiClient"` from its preflight through constraint creation, so
a concurrent writer waits rather than slipping past its check; other blocking
scripts, such as `0035`, take no such lock. Two instances running the upgrade
at the same time are not serialized by DbUp either. Stop every instance first,
upgrade from one, then start the rest.

### Migration 0036: API client identifiers must be unique

Migration `0036` makes `ClientId` (the key a caller authenticates with) and
`ClientUuid` (the identity provider's identifier for that client) unique
across the whole `dmscs."ApiClient"` table, in every application and every
tenant. The Configuration Service generates both values itself, so duplicates
come only from rows seeded outside the API or from a setup that ran twice. If
any exist, startup stops with a message of this shape:

```text
API client identifier uniqueness upgrade blocked: 2 ApiClient row(s) share a ClientId with another row, ids: 17, 42. 3 ApiClient row(s) share a ClientUuid with another row, ids: 42, 43, 44. Resolve the duplicates with the operator procedure in docs/OPERATIONS.md ("Configuration Service database upgrades"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.
```

The counts are complete. The ids are a sample: at most 20 `ApiClient."Id"`
values per column, so a message ending in `... and 5 more` names only part of
the set, and even a short list does not say which rows share a value with
which. The enumeration query in step 2 is the complete list; do not work from
the message. The ids are internal row ids, not keys.

**Do not delete through the API while resolving duplicates.**
`DELETE /v3/apiClients/{id}` also deletes the identity-provider client the row
points to, by its `ClientUuid`. For rows that share a `ClientUuid`, that is
the survivor's provider client, and deleting the loser through the API
destroys the survivor's credentials. The procedure below deletes rows directly
in the database and leaves every provider client in place. The service is
stopped for the whole procedure, so nothing else writes to the table; do not
start the previous build to clean up through the API either.

1. **Stop the Configuration Service.** Every instance. The upgrade runs at
   startup or through the Installer, so nothing below happens with the
   service up.

2. **Enumerate every affected row.** The query lists every `ApiClient` row
   whose `ClientId` or `ClientUuid` is also held by another row, with a flag
   for each, its ownership chain (application, vendor, tenant), its audit
   columns, and its data-store and ownership-token associations. Save the
   output: it is the review record for steps 4 to 6 and the starting point
   for step 8.

   PostgreSQL:

   ```sql
   SELECT c."Id", c."ClientId", c."ClientUuid",
          EXISTS (SELECT 1 FROM dmscs."ApiClient" o
                  WHERE o."ClientId" = c."ClientId" AND o."Id" <> c."Id") AS "SharesClientId",
          EXISTS (SELECT 1 FROM dmscs."ApiClient" o
                  WHERE o."ClientUuid" = c."ClientUuid" AND o."Id" <> c."Id") AS "SharesClientUuid",
          c."Name", c."IsApproved",
          c."ApplicationId", a."ApplicationName", a."ClaimSetName",
          v."Id" AS "VendorId", v."Company", t."Id" AS "TenantId", t."Name" AS "TenantName",
          c."CreatedAt", c."CreatedBy", c."LastModifiedAt", c."ModifiedBy",
          c."CreatorOwnershipTokenId",
          (SELECT string_agg(ds."Id"::text || ':' || ds."Name", ', ' ORDER BY ds."Id")
           FROM dmscs."ApiClientDataStore" acd
           JOIN dmscs."DataStore" ds ON ds."Id" = acd."DataStoreId"
           WHERE acd."ApiClientId" = c."Id") AS "DataStores",
          (SELECT string_agg(acot."OwnershipTokenId"::text, ', ' ORDER BY acot."OwnershipTokenId")
           FROM dmscs."ApiClientOwnershipToken" acot
           WHERE acot."ApiClientId" = c."Id") AS "OwnershipTokens"
   FROM dmscs."ApiClient" c
   JOIN dmscs."Application" a ON a."Id" = c."ApplicationId"
   JOIN dmscs."Vendor" v ON v."Id" = a."VendorId"
   LEFT JOIN dmscs."Tenant" t ON t."Id" = v."TenantId"
   WHERE EXISTS (SELECT 1 FROM dmscs."ApiClient" o
                 WHERE o."ClientId" = c."ClientId" AND o."Id" <> c."Id")
      OR EXISTS (SELECT 1 FROM dmscs."ApiClient" o
                 WHERE o."ClientUuid" = c."ClientUuid" AND o."Id" <> c."Id")
   ORDER BY c."ClientId", c."ClientUuid", c."Id";
   ```

   SQL Server:

   ```sql
   SELECT c.Id, c.ClientId, c.ClientUuid,
          CASE WHEN EXISTS (SELECT 1 FROM dmscs.ApiClient o
                            WHERE o.ClientId = c.ClientId AND o.Id <> c.Id) THEN 1 ELSE 0 END AS SharesClientId,
          CASE WHEN EXISTS (SELECT 1 FROM dmscs.ApiClient o
                            WHERE o.ClientUuid = c.ClientUuid AND o.Id <> c.Id) THEN 1 ELSE 0 END AS SharesClientUuid,
          c.Name, c.IsApproved,
          c.ApplicationId, a.ApplicationName, a.ClaimSetName,
          v.Id AS VendorId, v.Company, t.Id AS TenantId, t.Name AS TenantName,
          c.CreatedAt, c.CreatedBy, c.LastModifiedAt, c.ModifiedBy,
          c.CreatorOwnershipTokenId,
          (SELECT STRING_AGG(CAST(CONCAT(ds.Id, ':', ds.Name) AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY ds.Id)
           FROM dmscs.ApiClientDataStore acd
           JOIN dmscs.DataStore ds ON ds.Id = acd.DataStoreId
           WHERE acd.ApiClientId = c.Id) AS DataStores,
          (SELECT STRING_AGG(CAST(acot.OwnershipTokenId AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY acot.OwnershipTokenId)
           FROM dmscs.ApiClientOwnershipToken acot
           WHERE acot.ApiClientId = c.Id) AS OwnershipTokens
   FROM dmscs.ApiClient c
   JOIN dmscs.Application a ON a.Id = c.ApplicationId
   JOIN dmscs.Vendor v ON v.Id = a.VendorId
   LEFT JOIN dmscs.Tenant t ON t.Id = v.TenantId
   WHERE EXISTS (SELECT 1 FROM dmscs.ApiClient o
                 WHERE o.ClientId = c.ClientId AND o.Id <> c.Id)
      OR EXISTS (SELECT 1 FROM dmscs.ApiClient o
                 WHERE o.ClientUuid = c.ClientUuid AND o.Id <> c.Id)
   ORDER BY c.ClientId, c.ClientUuid, c.Id;
   ```

   Both queries use the equality the constraints apply, which is also what
   the migration's preflight uses. On SQL Server, `ClientId` compares under
   the column's collation: a case-insensitive collation makes `abc` and `ABC`
   duplicates, and trailing spaces never distinguish two keys. On PostgreSQL,
   with the deterministic default collation Configuration Service databases
   use, comparison is byte-wise, so those pairs are distinct keys.

3. **Back up** before changing anything. Either the whole database:

   ```bash
   pg_dump -U postgres -Fc <database> > <database>-before-0036.dump
   ```

   ```sql
   BACKUP DATABASE [<database>] TO DISK = N'<path>\<database>-before-0036.bak' WITH COPY_ONLY, INIT;
   ```

   or only the three tables the procedure touches:

   ```bash
   pg_dump -U postgres -Fc -t 'dmscs."ApiClient"' -t 'dmscs."ApiClientDataStore"' \
     -t 'dmscs."ApiClientOwnershipToken"' <database> > apiclient-before-0036.dump
   ```

   ```sql
   SELECT * INTO dmscs.ApiClient_Before0036 FROM dmscs.ApiClient;
   SELECT * INTO dmscs.ApiClientDataStore_Before0036 FROM dmscs.ApiClientDataStore;
   SELECT * INTO dmscs.ApiClientOwnershipToken_Before0036 FROM dmscs.ApiClientOwnershipToken;
   ```

   The SQL Server copies are plain tables in the `dmscs` schema; drop them
   once the upgrade has been verified, because they are not part of the
   schema and nothing else removes them.

4. **Form the duplicate groups and inspect each one.** The output is ordered
   by `ClientId`, then `ClientUuid`, so rows that share a key are adjacent but
   rows that share a UUID under different keys are not. Group the rows twice:
   once by `ClientId` over the rows flagged `SharesClientId`, and once by
   `ClientUuid` over the rows flagged `SharesClientUuid`. A row flagged for
   both belongs to one group of each kind; review such overlapping groups
   together, because a decision in one changes what the other contains. For
   each row, read both identifiers and both flags, the application, vendor
   and tenant it belongs to, the audit columns (who created it and when), and
   the associations the row would lose if deleted. Then look at the identity
   provider for every key and UUID in the group:

   * Keycloak: `GET {keycloak}/admin/realms/{realm}/clients?clientId={ClientId}`
     finds a client by key; `GET {keycloak}/admin/realms/{realm}/clients/{ClientUuid}`
     finds it by internal id; the admin console shows the same objects.
     Compare the client's `id` and `clientId` with each row, and note its
     name, its client scopes (the claim set), and the attributes or protocol
     mappers that carry the education-organization and data-store claims.
   * Self-contained provider (OpenIddict): the client is a row in the same
     database.

     ```sql
     -- PostgreSQL
     SELECT app."Id", app."ClientId", app."DisplayName", app."Type", app."CreatedAt", app."ProtocolMappers",
            (SELECT string_agg(s."Name", ', ' ORDER BY s."Name")
             FROM dmscs."OpenIddictApplicationScope" aps
             JOIN dmscs."OpenIddictScope" s ON s."Id" = aps."ScopeId"
             WHERE aps."ApplicationId" = app."Id") AS "Scopes"
     FROM dmscs."OpenIddictApplication" app
     WHERE app."ClientId" = '<ClientId>' OR app."Id" = '<ClientUuid>';
     ```

     ```sql
     -- SQL Server
     SELECT app.Id, app.ClientId, app.DisplayName, app.Type, app.CreatedAt, app.ProtocolMappers,
            (SELECT STRING_AGG(CAST(s.Name AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY s.Name)
             FROM dmscs.OpenIddictApplicationScope aps
             JOIN dmscs.OpenIddictScope s ON s.Id = aps.ScopeId
             WHERE aps.ApplicationId = app.Id) AS Scopes
     FROM dmscs.OpenIddictApplication app
     WHERE app.ClientId = '<ClientId>' OR app.Id = '<ClientUuid>';
     ```

   What the shapes mean: rows that share a `ClientId` but hold different
   `ClientUuid` values point at two provider clients, or at one client and a
   dangling reference; the provider answers that key for at most one of them,
   so the other row's credentials never worked or stopped working. Rows that
   share a `ClientUuid` point at one provider client, whose `clientId`
   matches at most one of them; the rest disagree with the provider about
   which key they hold. Audit columns and the provider's creation time show
   which attempt created what.

5. **Establish the intended owner of each group** from step 4, and record how
   you established it. No rule such as "keep the lowest id" is offered, and
   nothing in the service reconciles the provider automatically. **Stop if
   ownership remains ambiguous** and leave the database as it is: an upgrade
   that stays blocked is recoverable, a deleted credential is not.

6. **Delete only the reviewed row ids**, in one transaction, child rows
   first. Replace `<reviewed ids>` with the ids written down in step 5 and
   nothing else; the same list goes in all three statements. Deleting a row
   discards its data-store assignments and ownership-token links. The
   provider client is not touched.

   Run the deletion in an interactive session that stays open: `psql`, or
   `sqlcmd` without `-Q` or `-i`, or a client with autocommit off. The block
   opens the transaction and deletes; it does not end the transaction.

   PostgreSQL:

   ```sql
   BEGIN;
   DELETE FROM dmscs."ApiClientOwnershipToken" WHERE "ApiClientId" IN (<reviewed ids>)
   RETURNING "ApiClientId", "OwnershipTokenId";
   DELETE FROM dmscs."ApiClientDataStore" WHERE "ApiClientId" IN (<reviewed ids>)
   RETURNING "ApiClientId", "DataStoreId";
   DELETE FROM dmscs."ApiClient" WHERE "Id" IN (<reviewed ids>)
   RETURNING "Id", "ClientId", "ClientUuid";
   ```

   SQL Server:

   ```sql
   SET XACT_ABORT ON;
   BEGIN TRANSACTION;
   DELETE FROM dmscs.ApiClientOwnershipToken
   OUTPUT deleted.ApiClientId, deleted.OwnershipTokenId
   WHERE ApiClientId IN (<reviewed ids>);
   DELETE FROM dmscs.ApiClientDataStore
   OUTPUT deleted.ApiClientId, deleted.DataStoreId
   WHERE ApiClientId IN (<reviewed ids>);
   DELETE FROM dmscs.ApiClient
   OUTPUT deleted.Id, deleted.ClientId, deleted.ClientUuid
   WHERE Id IN (<reviewed ids>);
   ```

   The transaction is still open. In the same session, compare the returned
   rows with the review record: every id from step 5 and no other, and for
   each one the associations step 2 listed. Then end the transaction with
   exactly one of the statements below. If the session closes before either
   runs, both engines roll the deletion back and nothing was deleted.

   PostgreSQL, when the rows match:

   ```sql
   COMMIT;
   ```

   PostgreSQL, otherwise:

   ```sql
   ROLLBACK;
   ```

   SQL Server, when the rows match:

   ```sql
   COMMIT TRANSACTION;
   ```

   SQL Server, otherwise:

   ```sql
   ROLLBACK TRANSACTION;
   ```

   The child tables cascade from `ApiClient` on both engines, but the
   procedure deletes them explicitly so that the returned rows show exactly
   what each deletion discards.

7. **Re-check both identifiers**, then retry the upgrade. Each query must
   return zero rows; if either still returns rows, go back to step 2 with
   that output.

   PostgreSQL:

   ```sql
   SELECT c."Id" FROM dmscs."ApiClient" c
   WHERE EXISTS (SELECT 1 FROM dmscs."ApiClient" o WHERE o."ClientId" = c."ClientId" AND o."Id" <> c."Id");
   SELECT c."Id" FROM dmscs."ApiClient" c
   WHERE EXISTS (SELECT 1 FROM dmscs."ApiClient" o WHERE o."ClientUuid" = c."ClientUuid" AND o."Id" <> c."Id");
   ```

   SQL Server:

   ```sql
   SELECT c.Id FROM dmscs.ApiClient c
   WHERE EXISTS (SELECT 1 FROM dmscs.ApiClient o WHERE o.ClientId = c.ClientId AND o.Id <> c.Id);
   SELECT c.Id FROM dmscs.ApiClient c
   WHERE EXISTS (SELECT 1 FROM dmscs.ApiClient o WHERE o.ClientUuid = c.ClientUuid AND o.Id <> c.Id);
   ```

   Then start one Configuration Service instance or run the Installer. The
   script runs again in full, adds both constraints, and journals `0036`;
   `SELECT scriptname FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE '%0036%';`
   (PostgreSQL) or
   `SELECT ScriptName FROM dbo.dmscs_SchemaVersions WHERE ScriptName LIKE '%0036%';`
   (SQL Server) returns one row once it has. Start the remaining instances
   after that.

8. **Provider cleanup is a separate activity.** Deleting a row removes the
   Configuration Service's reference to a provider client and establishes
   nothing about that client. A row seeded outside the API, or corrupted, can
   point at a provider client that is legitimately in use, including a client
   registered through `POST /connect/register`, which never has an
   `ApiClient` row. Leave every provider client in place until the upgrade is
   done. Afterwards, handle a provider client you suspect is orphaned under
   [the one rule for deleting a client during recovery](#the-one-rule-for-deleting-a-client-during-recovery):
   deleting it requires independent evidence of ownership, such as provider
   audit events tying the client's creation to the attempt that produced the
   deleted row, or an operator's recorded confirmation documenting how
   ownership was established, together with recorded operator approval of
   the deletion. The `ClientUuid` in the step 2 review record says where to
   look; it is not authorization. A provider client whose UUID a surviving
   row still holds is in use and must not be deleted. If ownership remains
   uncertain, stop and leave the client in place. Nothing in the migration or
   in this procedure deletes provider objects.

## Configuration Service: identity-provider clients left behind by failed provisioning

Creating an API client is two systems agreeing: the Configuration Service asks
the identity provider for a client, then records it in its own database. When
the second half fails, or when the provider accepts the client and then refuses
the role assignment the client needs, the service removes the client it created
and answers the caller with an error carrying no credentials.

That removal is usually confirmed. When it is not, a client can be left in the
provider that no database row references and no caller holds credentials for.
It cannot be reached through the API, so it has to be removed by hand. The log
says which situation occurred and carries the identifiers needed to act.

### The one rule for deleting a client during recovery

A client may be deleted as recovery for a failed attempt **only** when its
internal id equals the provider UUID recorded in that attempt's log line,
verified in the same realm. When the log carries no UUID, or the UUID does not
match, every other match — client id, display name, creation time, role
mappings, correlation id — is investigative context and **not** authorization to
delete.

Deletion then requires one of:

* provider audit evidence tying the client's creation to the failed attempt, for
  example Keycloak admin events for `CLIENT` `CREATE` in that realm, correlated
  by time and representation with the failed request; or
* an operator's recorded confirmation that documents **how** ownership by the
  failed attempt was established.

If ownership remains uncertain, stop and leave the client in place.

The rule is strict because two requests can collide. `POST /connect/register`
checks whether a client id is already taken before creating it, and that check
is not atomic with the creation: two concurrent registrations of the same client
id can both pass it, after which the provider creates one client and refuses the
other. Recovering the refused request by client id alone would delete the
successful registration's client. A client deleted by its owner and registered
again under the same id before recovery runs would be destroyed the same way.
Only the UUID identifies the object a particular attempt created.

Enable admin-event auditing for the realm in advance if you want the first kind
of evidence available. Turning it on after an incident cannot establish who
created a client that already exists.

### Situation 1: the service could not confirm its own cleanup

Log pair, from the Configuration Service:

```text
Client provisioning failed during {phase} for client {clientId} (provider client {clientUuid}); deleting the created client
Could not confirm deletion of provider client {clientUuid} (client {clientId}) after failed provisioning; cleanup outcome {outcome}; the client may remain and must be reviewed manually
```

The caller received `502` or `500` and no credentials. The client is expected to
remain when the outcome names a provider refusal or an unknown result, and may
or may not remain when the provider was unreachable or the deletion threw: in
those cases the deletion is unconfirmed rather than known to have failed.

The client's role assignment was reported unsuccessful when the phase is
`role-assignment`, is unconfirmed when that phase raised an exception, and was
never attempted in the other phases. For `POST /v3/applications` and
`POST /v3/apiClients` the secret was generated server-side and never returned,
so nobody holds usable credentials. For `POST /connect/register` the secret was
**chosen by the caller**, so if the role assignment did take effect the client
is a usable credential the caller still has. Treat that as the urgent case.

**First check the phase.** When it is `client-identifier-parse`, the provider
returned an identifier the service could not parse, so the value in the log is
**not a UUID**. Do not use it to look a client up and do not use it to delete
one: a `404` from that invalid address says nothing about whether the client
this request created still exists. Follow situation 3 below instead, which
searches by client id as investigation only and requires audit evidence or a
recorded operator confirmation before anything is deleted. The numbered
procedure that follows applies to every other phase, where the logged provider
identifier is a well-formed UUID.

1. Resolve the client by UUID: `GET {keycloak}/admin/realms/{realm}/clients/{clientUuid}`,
   or open it by internal id in the admin console. A `404` means that client is
   already absent, whichever operation removed it; stop. If it is found, confirm
   its `clientId` equals the logged `{clientId}`. A mismatch means the
   identifier is not what the log expects: stop and investigate.
2. For the two insert endpoints, confirm no database row references it:

   ```sql
   SELECT "Id", "ApplicationId", "ClientUuid" FROM dmscs."ApiClient" WHERE "ClientId" = '<clientId>';
   ```

   On SQL Server the table is `dmscs.ApiClient`. Add
   `OR "ClientUuid" = '<clientUuid>'` **only** when the logged value is a
   well-formed UUID; PostgreSQL rejects a malformed literal compared against a
   `uuid` column, and the `client-identifier-parse` phase logs exactly such a
   value. If a row exists, **stop and investigate** rather than assuming it
   belongs to another request.
3. For `POST /connect/register`, absence from `dmscs."ApiClient"` is expected and
   authorizes nothing by itself. The UUID match from step 1 is the
   authorization; record it.
4. Delete the client by internal id:
   `DELETE {keycloak}/admin/realms/{realm}/clients/{clientUuid}`. A `404` means
   it was already removed.
5. Re-issue the original request. It provisions a **new** client: the insert
   endpoints mint a fresh key and secret, and registration may reuse the
   caller's chosen id once the old client is gone. **There is no automatic
   retry convergence.** Each attempt is independent.

### Situation 2: cleanup after a failed database insert could not be confirmed

Log line, from the Configuration Service:

```text
Could not confirm deletion of provider client {clientUuid} (client {clientId}) after the database insert failed; cleanup outcome {outcome}; the client may remain and must be reviewed manually
```

The caller received `409`, `400` or `500` and no credentials. A **fully
provisioned** client is expected to remain, or may remain for an unconfirmed
outcome: it has its role, its claim-set scope and its claims, and is enabled
according to the request. Its secret was never returned, so it is unusable
clutter rather than a live credential.

This situation always carries a well-formed UUID, because the workflow reached
the database only after the provider reported a usable identifier. Follow steps
1, 2 and 4 of situation 1. In step 2, a row that **does** exist after a `500`
means the insert may have committed after all: do not delete the provider
client. Treat the row as a live API client and let the caller reset its
credentials or delete it through the API.

### Situation 3: the creation outcome itself is unconfirmed

Log line carrying a client id, no provider UUID, and the phrase
`creation outcome unconfirmed`. A line saying `creation not attempted` needs no
recovery: that request created nothing.

Whether a client exists is unknown. A non-success answer from the provider makes
creation unlikely but does not disprove it; a success with no location to read
the identifier from makes it likely; a transport failure leaves it open. **This
attempt did not perform role assignment**, so a client found under that client
id may belong to a different attempt and may carry roles.

Search by client id as investigation only:
`GET {keycloak}/admin/realms/{realm}/clients?clientId={clientId}`. If nothing is
found, stop. If a client is found, the deletion rule above applies in full: with
no UUID from this attempt, delete only on audit evidence or recorded operator
confirmation, and otherwise leave it and record the finding. For the two insert
endpoints, also run step 2 of situation 1; a row means the client is in use and
must not be deleted.

Recovery never deletes realm roles or client scopes. Those are shared by every
client in the realm, so a failed attempt leaves nothing about them to repair.

### Finding provider clients that no API client references

A provider client can also lose its `ApiClient` row without any log line. A
`DELETE /v3/vendors/{id}` made before DMS-1493 removed the vendor's rows without
deleting their provider clients, and those clients keep their credentials (see
[OWASP-AUTH-COVERAGE.md](OWASP-AUTH-COVERAGE.md)). In `self-contained` mode, since
DMS-1494, a client without an API client row is refused at `/connect/token`,
`/connect/revoke` and `/connect/introspect` unless its roles and scopes qualify it
as an administrative client
([CS-AUTH](../reference/design/configuration-service/CS-AUTH.md#approval-states-and-clients-without-an-api-client-row)),
so a vendor candidate obtains no new token. In `keycloak` mode its credentials keep
working. The comparison below lists
**candidates** for review. It changes nothing, and it cannot say why a client has
no row: current rows do not record an earlier vendor deletion, and many provider
clients legitimately have none. Expect these among the results:

* clients registered through `POST /connect/register`, which carry
  `IdentitySettings:ConfigServiceRole` (`cms-client` by default) instead of the
  `IdentitySettings:ClientRole` (`dms-client`) given to API clients;
* bootstrap and administrative clients created by deployment scripts or by hand;
* for Keycloak, the realm's built-in clients (`account`, `account-console`,
  `admin-cli`, `broker`, `realm-management`, `security-admin-console`) and the
  clients of any other application that uses the realm;
* a client created while the comparison runs, whose row is not yet committed.

**Recognizing a refused candidate (`self-contained`).** When such a client presents
its correct secret and is refused, the Configuration Service logs at Warning:

```text
Client {ClientId} (application {ApplicationId}) authenticated but is not admitted ({Admission}); refused
```

The application id is the `OpenIddictApplication.Id` the query below prints.
`NoApiClientRowHoldsClientRoleOnly` is the shape of an orphaned vendor client: it
holds `IdentitySettings:ClientRole` but not `IdentitySettings:ConfigServiceRole`.
`NoApiClientRowNoAdministrativeRole`, `NoApiClientRowNoScopes` and
`NoApiClientRowNonAdministrativeScope` usually mean an administrative client made
by hand without the administrative role or with other scopes. A legitimate one is
fixed by giving it `ConfigServiceRole` and only Management API scopes, or by
re-creating it through `POST /connect/register` or `setup-openiddict.ps1`.
`NoApiClientRowRoleConfiguration` names the configuration, not the client: the two
role names resolve to one role row or collide, and every administrative client
without a row is refused until non-colliding names are configured. A role-name
change requires the
[role-membership audit](#auditing-role-memberships-before-a-role-name-change).
A wrong secret never produces this line, and an unapproved client stays silent.

Compare against every Configuration Service database whose clients live in the
same provider. All tenants of one database share its `dmscs.ApiClient` table, so
the queries need no tenant filter. A Keycloak realm can also be shared by several
Configuration Service deployments, each with its own database.

**`self-contained`.** The provider clients are in the same database, and
`ApiClient.ClientUuid` is their `Id`. The query lists each provider client with
no API client row, with its roles. PostgreSQL:

```sql
SELECT oa."Id", oa."ClientId", oa."DisplayName", oa."CreatedAt",
       string_agg(r."Name", ', ' ORDER BY r."Name") AS "Roles"
FROM dmscs."OpenIddictApplication" oa
LEFT JOIN dmscs."OpenIddictClientRole" cr ON cr."ClientId" = oa."Id"
LEFT JOIN dmscs."OpenIddictRole" r ON r."Id" = cr."RoleId"
WHERE NOT EXISTS (SELECT 1 FROM dmscs."ApiClient" ac WHERE ac."ClientUuid" = oa."Id")
GROUP BY oa."Id", oa."ClientId", oa."DisplayName", oa."CreatedAt"
ORDER BY oa."CreatedAt";
```

SQL Server:

```sql
SELECT oa.Id, oa.ClientId, oa.DisplayName, oa.CreatedAt,
       STRING_AGG(r.Name, ', ') WITHIN GROUP (ORDER BY r.Name) AS Roles
FROM dmscs.OpenIddictApplication oa
LEFT JOIN dmscs.OpenIddictClientRole cr ON cr.ClientId = oa.Id
LEFT JOIN dmscs.OpenIddictRole r ON r.Id = cr.RoleId
WHERE NOT EXISTS (SELECT 1 FROM dmscs.ApiClient ac WHERE ac.ClientUuid = oa.Id)
GROUP BY oa.Id, oa.ClientId, oa.DisplayName, oa.CreatedAt
ORDER BY oa.CreatedAt;
```

**`keycloak`.** `ApiClient.ClientUuid` holds the client's internal `id`, not its
OAuth `clientId`, so compare on `id`. Collect the UUIDs from every Configuration
Service database that uses the realm into one file:

```shell
psql "<connection string>" -At -c 'SELECT "ClientUuid" FROM dmscs."ApiClient"' >> cms-client-uuids.txt
sqlcmd -S <server> -d <database> -h -1 -W -Q "SET NOCOUNT ON; SELECT ClientUuid FROM dmscs.ApiClient" >> cms-client-uuids.txt
```

List the realm's clients with the admin CLI, which returns all of them. The
Admin REST equivalent is `GET {keycloak}/admin/realms/{realm}/clients?briefRepresentation=true`;
when paging with `first` and `max`, continue until a page is short.

```shell
kcadm.sh config credentials --server <keycloak url> --realm master --user <admin user>
kcadm.sh get clients -r <realm> --fields id,clientId --format csv --noquotes > realm-clients.csv
```

Then list the realm clients whose `id` no database holds, leaving out the
built-ins. SQL Server prints UUIDs in upper case, so the comparison ignores case:

```powershell
$builtIn = 'account', 'account-console', 'admin-cli', 'broker', 'realm-management', 'security-admin-console'
$cms = Get-Content cms-client-uuids.txt | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }
Import-Csv realm-clients.csv -Header Id, ClientId |
    Where-Object { $_.Id.ToLowerInvariant() -notin $cms -and $_.ClientId -notin $builtIn }
```

To see what a candidate was created for, read the realm roles of its service
account. `dms-client` points to an API client, `cms-client` to a registration, and
a client without a service account was not created by the Configuration Service:

```shell
kcadm.sh get clients/<id>/service-account-user -r <realm> --fields id,username
kcadm.sh get users/<service account user id>/role-mappings/realm -r <realm> --fields name
```

Treat every result as a candidate until an operator has established who owns it.
A role, a name or a missing row is not proof. Deleting a client that another
deployment, tenant database or application still uses cuts it off. Deciding to
delete one needs the same independent evidence of ownership and recorded approval
as [the one rule for deleting a client during recovery](#the-one-rule-for-deleting-a-client-during-recovery);
nothing in this procedure deletes or changes a client.

### Auditing role memberships before a role-name change

In `self-contained` mode a client without an API client row is admitted as
administrative by its role membership
([CS-AUTH](../reference/design/configuration-service/CS-AUTH.md#changing-or-reusing-a-role-name)).
Memberships are written when a client is created and read under the current
`IdentitySettings:ConfigServiceRole` and `IdentitySettings:ClientRole`. Changing
either name, or reusing a name either role used before, therefore gives existing
memberships a new meaning, on both database engines and whether or not the names
ever collided. For example, after changing `ConfigServiceRole = A, ClientRole = B`
to `ConfigServiceRole = B, ClientRole = C`, every vendor client created earlier
holds `B`, now the administrative role. This affects clients with an API client
row as well: an approved vendor client that holds `B` now meets the Management API
role requirement and, with an administrative scope, reaches protected Configuration
Service endpoints. Nothing in the product detects this.

**Whenever either role name is changed, or a previously used name is reused,
audit role memberships before the new configuration takes effect.** The names are
read at startup, so finish the audit before any instance starts with them.

1. Record the current names, the new names, and every name either role has used
   before in this deployment.
2. On every Configuration Service database the deployment uses, run the
   `self-contained` query in
   [Finding provider clients that no API client references](#finding-provider-clients-that-no-api-client-references)
   **with its `WHERE NOT EXISTS` line removed**, so that it lists **every**
   client with its roles, whether or not it has an API client row. The audit must
   cover all clients: a role-name change affects clients with a row as much as
   clients without one.
3. Establish what each listed client is from ownership records, not from its role:
   current data does not say whether a client was created as a vendor client or as
   an administrative one. Under the new names, a client without an API client row
   is admitted only if it holds the new `ConfigServiceRole` and only Management
   API scopes, and any client that holds the new `ConfigServiceRole` meets the
   Management API role requirement.
4. Re-create or re-role every client whose membership no longer matches its kind:
   - a vendor client, with or without an API client row, that holds the new
     `ConfigServiceRole` name, including one created on SQL Server while
     `ClientRole` collided with `ConfigServiceRole`, which holds the
     administrative role row;
   - an administrative client that holds only a retired name.

   Re-roling changes `dmscs.OpenIddictClientRole` rows; re-creating an
   administrative client goes through `POST /connect/register` or
   `setup-openiddict.ps1`. Deleting a client follows
   [the one rule for deleting a client during recovery](#the-one-rule-for-deleting-a-client-during-recovery).
5. Start the Configuration Service with the new names only when every listed
   client's membership matches its kind.

The two names must also not collide. When they resolve to one role row in the
database, or are equal after trailing white space is removed and case, width, kana
type and non-spacing marks are ignored (for example `cms-client` and `CMS-Client`,
or `cms-client` with a trailing space), every client without an API client row is
refused with the `NoApiClientRowRoleConfiguration` Warning. Clients with an API
client row are unaffected.

## Configuration Service: DMS data store provisioning

The Configuration Service can create and delete DMS data store databases from signed template packages
(`Minimal` and `Sample`) on a PostgreSQL or SQL Server server. It is off by default and configured in the
`DmsDataStoreProvisioning` section; see [DmsDataStoreProvisioning](./CONFIGURATION.md#dmsdatastoreprovisioning)
for every setting. This release adds the provisioner as a service for the Configuration Service's own
background jobs (see
[Configuration Service Background Jobs](./CONFIGURATION-SERVICE-BACKGROUND-JOBS.md#dms-data-store-provisioning)).
It adds no endpoint and does not change `/health`.

How a create works, in short: the allowlisted package is copied into a private operation directory, its
attestation is checked against the trust policy, and its manifest is checked against the `Compatibility`
settings. The database is then restored under a generated staging name
(`edfi_dms_provision_stage_<operation id>`, an id of 12 hex characters) and validated there: content
profile, schema and inventory, and security state. Only then is it renamed to the requested name and marked
with the data store's identity (`dms.DataStoreIdentity.SourceIdentity`), which is the last change. A
database that already holds the requested name is never restored over: it is accepted only when it carries
the expected identity and passes the same validation. A delete drops a database only when it carries the
expected identity, after closing every other session on it.

### Readiness

At startup, an enabled provisioner checks its environment once. A failure does not stop the service: it
marks provisioning `NotReady`, logs each failed check at Critical, and refuses every provisioning call until
the service restarts. Fix the cause, then restart. Each Critical line has this form:

```text
DMSPROV-<Code>: DMS data store provisioning is not ready for target provider <provider>: probe <Probe> failed at <Operation> (exception types <types>, provider error <code>)
```

| Probe | What it checks | Usual fix |
| --- | --- | --- |
| `SecretResolution` | `${secret:...}` references in `AdminConnectionString` resolve. | Fix the secret provider or the reference. |
| `TrustPolicy` | The trust policy file is readable, valid, and lists at least one `detached-attestation` producer with a valid P-256 key. | See [Trust](#template-package-trust). |
| `Catalog.Minimal`, `Catalog.Sample` | Each allowlisted package and its attestation are present, the attestation verifies, and the manifest matches `Compatibility`. | Place the exact package and attestation in `ArtifactDirectory`, or correct the allowlist or `Compatibility`. |
| `TargetServer` | The server can be reached and its facts read. | Network, credentials, TLS settings. |
| `ServerVersion`, `EngineCompatibility.<template>` | PostgreSQL 18 or later; SQL Server 2022 (16) or later; and the server's major version is at least the one each package was built on. | Upgrade the server, or use packages built for it. |
| `Superuser`, `CreateDatabasePrivilege`, `SignalBackendPrivilege`, `ReadAllStatsPrivilege`, `EnqueueOwnerRole` | PostgreSQL privileges; see [PostgreSQL](#postgresql). | Grant them to the administrative login. |
| `CreateDatabasePrivilege`, `AlterAnyDatabasePrivilege`, `AlterAnyConnectionPrivilege`, `ViewServerStatePrivilege`, `FileCatalogVisibility`, `DefaultDataPath`, `DefaultLogPath` | SQL Server privileges and default file locations; see [SQL Server](#sql-server). | Grant them, or set the instance default paths. |
| `PsqlVersion` | `Postgresql:PsqlPath` runs and reports major version 18 or later. | Install `psql` 18, or correct the path. |
| `ClientCertificate` | The Configuration Service's own PostgreSQL connections would send no client certificate: `PGSSLCERT` is unset and the home directory holds no `.postgresql/postgresql.crt` (`%APPDATA%\postgresql\postgresql.crt` on Windows), unless `SSL Mode` is `Disable`. `psql` never sends one. | Remove the variable or the file; client-certificate authentication of the administrative login is not supported. |
| `AmbientPassword` | With no `Password` in `AdminConnectionString`, the Configuration Service's own PostgreSQL connections would find no password elsewhere: `PGPASSWORD` is unset or empty, and no password file exists at `PGPASSFILE` or, when that is unset, at `.pgpass` in the home directory (`%APPDATA%\postgresql\pgpass.conf` on Windows). `psql` receives only the configured password. | Put the password in `AdminConnectionString` (a `${secret:...}` reference), or remove the variable or the file. |
| `GssAuthentication` | The server did not authenticate the administrative login to `postgres` through GSSAPI (Kerberos). That login comes from the Configuration Service's Kerberos credentials, which `psql` does not receive. `pg_hba.conf` is matched per database, so this probe cannot see a GSSAPI rule for other databases: each create checks its staging database before `psql` starts and fails with a permanent `RestoreFailed`, operation `RestoreIntoStaging.GssAuthentication`, when that database is authenticated through GSSAPI. | Authenticate the administrative login with a password (or `trust`/`peer`) in `pg_hba.conf` for every database; GSSAPI authentication of it is not supported. |
| `StagingDirectory`, `StagingVisibility` | The SQL Server staging directory exists, is owned by the Configuration Service user (on Linux), is not group- or other-writable, and SQL Server can read a probe file written into it. | See [SQL Server](#sql-server). |
| `TargetLock` | A lock can be taken and released on the server. | Usually a server or privilege problem an earlier probe also reports. |

### Template package trust

Every package is authenticated before anything is extracted. `TrustPolicyPath` names one trust policy file
in the template trust-policy format (`eng/docker-compose/template-trust-policy.json` is the tracked, empty
example). The Configuration Service reads that one file; it does not merge the git-ignored
`template-trust-policy.local.json` overlay the bootstrap scripts use. A policy with no
`detached-attestation` producer makes provisioning `NotReady`. There is no unsigned mode.

Until a CI-signed release publishes its producer key, sign the packages you deploy with your own key:

1. Create a P-256 signer with `eng/DatabaseTemplates/new-template-dev-trust.ps1 -Purpose Dev`, or keep an
   equivalent key under your own key management.
2. Attest each package's exact bytes with `Write-TemplatePackageAttestation` from
   `eng/DatabaseTemplates/Template-Management.psm1`, and place the attestation next to the package as
   `<PackageId>.<Version>.nupkg.attestation.json` in `ArtifactDirectory`.
3. List that producer and its public key in the file `TrustPolicyPath` names.

Any change to a package's bytes after signing makes it `ArtifactUntrusted`.

### PostgreSQL

| Topology | Supported |
| --- | --- |
| The Configuration Service (container or host) with `psql` 18 or later, reaching the server over TCP. The published Configuration Service image includes `psql` 18 at `/usr/bin/psql`. | Yes |
| No `psql` in the Configuration Service runtime | No (`NotReady`, `PsqlVersion`) |
| A non-superuser administrative login | No (`NotReady`, `Superuser`) |

The administrative login must be a **superuser**. The PostgreSQL template ends by making
`edfi_dms_enqueue_owner` the owner of its enqueue functions. PostgreSQL allows that for a non-superuser only
when the new owner may create objects in the `dms` schema, which the template's own security rules forbid.
The narrower privileges are still checked and named individually, so one startup shows all of them:

- `CREATEDB` (`CreateDatabasePrivilege`) to create, rename, and drop databases;
- the privileges of `pg_signal_backend` (`SignalBackendPrivilege`) to end other sessions on a target;
- the privileges of `pg_read_all_stats` (`ReadAllStatsPrivilege`) to identify those sessions;
- `CREATEROLE` while `edfi_dms_enqueue_owner` does not exist yet (`EnqueueOwnerRole`). Once it exists it
  must be `NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`, with no
  membership that carries the admin, inherit, or set option; otherwise operations fail with
  `RoleStateUnsafe`.

`template1` must use `UTF8` encoding.

### SQL Server

`RESTORE` reads its backup from a file the server opens, so the Configuration Service copies each backup into
a staging directory the server also sees. The directory separates the two identities: the Configuration
Service owns it and writes into it, and SQL Server can only read it. The service refuses a group- or
other-writable directory. It re-hashes the staged file after the restore and fails with
`StagingIntegrityViolation` if the file changed.

| Topology | Setup | Supported |
| --- | --- | --- |
| Same host | `groupadd dms-staging`; `usermod -aG dms-staging mssql` (the SQL Server service account); `mkdir -p /var/opt/dms-staging`; `chown <cms user>:dms-staging /var/opt/dms-staging`; `chmod 2750 /var/opt/dms-staging`. `Mssql:StagingPath` and `Mssql:StagingServerPath` are the same path. | Yes |
| Configuration Service and SQL Server containers sharing a volume | On the volume: `chown <cms uid>:<gid>` and `chmod 2750`. Run SQL Server with `--group-add <gid>` and the Configuration Service as the owning uid (the published image runs as root). `StagingPath` and `StagingServerPath` are each container's mount point. On Docker Desktop, use a named volume: a bind mount from Windows shows every file as `0777`. | Yes |
| An SMB or NFS share mapped into both | The Configuration Service identity has Modify; the SQL Server service SID has Read & Execute only. `StagingServerPath` is the server's view of the share. | Yes |
| No staging path SQL Server can see | — | No (`NotReady`, `StagingVisibility`) |
| Platforms without `RESTORE ... FROM DISK` | — | No |

A local, runnable version of the container topology with separate Linux identities is in
[RUNNING-LOCALLY.md](./RUNNING-LOCALLY.md#cms-dms-template-provisioning-sql-server-staging-with-separate-identities).

The administrative login needs:

- membership in `dbcreator` or `sysadmin` (`CreateDatabasePrivilege`);
- `ALTER ANY DATABASE` (`AlterAnyDatabasePrivilege`);
- `ALTER ANY CONNECTION` (`AlterAnyConnectionPrivilege`), to end other sessions on a target;
- `VIEW SERVER STATE`, or `VIEW SERVER PERFORMANCE STATE` where the server has it
  (`ViewServerStatePrivilege`);
- visibility of `sys.master_files` (`FileCatalogVisibility`).

The instance default data and log paths must be set (`DefaultDataPath`, `DefaultLogPath`). Restored files
go there under generated names, never to the paths recorded in the backup.

### Operational rule

While provisioning is enabled, do not run DDL by hand (create, rename, drop, or `ALTER DATABASE` state or
permissions) against a managed data store's database or any `edfi_dms_provision_*` database, except as
described under [Recovery](#recovery), while the data store's record is in a failed state and no session
holds its lock (see [A lock that is never released](#a-lock-that-is-never-released)). The provisioner
detects a database it cannot verify and refuses it, but a change made by hand at the moment of a step
cannot always be detected. On SQL Server, for example, a delete checks the database's identity and drops it
in one batch, and a rename made between the two by another session can have the wrong database dropped.

### Failure codes

Every failure carries a code, a category, and a diagnostic with only the step that failed, the exception
type names, and the provider's error number or SQLSTATE. It never carries a connection string, secret,
server message, or `psql` output. The category tells the caller whether to retry:

| Category | Meaning | Retry |
| --- | --- | --- |
| `InvalidRequest` | The request itself was rejected: unknown template, invalid name, empty identity, missing pin. | No |
| `NotReady` | Provisioning is disabled or failed its startup checks. | Not until a restart |
| `Transient` | The target was left as it was, or the outcome was settled; the same request may succeed later. The cleanup report may still list leftovers. | Yes |
| `Permanent` | The same inputs fail again. No target was left behind, but the cleanup report may still list leftovers. | No |
| `OperatorInspectionRequired` | A database, package, or server state needs a person to look at it. | No |

| Code | Category |
| --- | --- |
| `FeatureDisabled`, `StartupValidationFailed`, `AdapterUnavailable` | `NotReady` |
| `TemplateNotAllowlisted`, `InvalidTargetName`, `EmptySourceIdentity`, `ArtifactPinMissing` | `InvalidRequest` |
| `ProtectedTarget`, `AdminPrivilegeMissing`, `RoleStateUnsafe`, `EngineIncompatible`, `StagingNotVisible`, `ServerRejected` | `Permanent` |
| `SecretResolutionFailed`, `ServerUnavailable`, `LockTimeout`, `LockUnavailable`, `TimedOut`, `StagingFailed`, `StagingNameCollision`, `Unknown` | `Transient` |
| `RestoreFailed` | `Permanent`; `Transient` when a lost connection ended the restore and it is known to have rolled back |
| `ExclusiveAccessNotEstablished`, `LockLost`, `DropFailed` | `Transient` before anything on the target changed; `OperatorInspectionRequired` after |
| `ArtifactMissing`, `ArtifactUntrusted`, `ArtifactTampered`, `ManifestInvalid`, `ManifestMismatch`, `BackupInvalid`, `PackageIdentityInvalid`, `ArtifactChangedSinceCreate` | `OperatorInspectionRequired` |
| `ContentProfileViolation`, `TargetIncompatible`, `SecurityStateViolation`, `StagingIntegrityViolation`, `IdentityAssignmentFailed`, `TargetUnowned`, `TargetUnverifiable`, `TargetFenced`, `InstanceChanged`, `OutcomeUnknown`, `CompensationFailed` | `OperatorInspectionRequired` |

### Recovery

Never drop a database by hand unless you have shown which data store record it belongs to. A database that
no `dmscs.DataStore` row points at is not proven to be an orphan, and an identity that cannot be read may
only mean the login lacks a privilege. **If ownership remains uncertain, stop and do not delete.**

- **`TargetUnowned`**: a database holds the requested name but carries another identity. Find out which
  record or deployment owns it. Rename or drop it only once it is shown to belong to no one, then retry.
- **`TargetUnverifiable`**, or an operation interrupted between the rename and the identity marker: the
  database records which operation created it. On PostgreSQL, `shobj_description(oid, 'pg_database')` reads
  `edfi-dms-provision:v1:<operation id>:<expected identity>`. On SQL Server, the primary file's
  `physical_name` ends in `__<operation id>.mdf`. Drop it by hand, then retry, only when all of these hold:
  - the database holds this record's target name;
  - its operation id is the one in this record's failed create (the `1438_10` `OperationFailed` log line
    names the operation id and the code);
  - on PostgreSQL, the expected identity in the comment equals the identity this record persisted;
  - no `dmscs.DataStore` row points at the database.
- **`TargetFenced`**: an interrupted delete left the database closed to connections. After confirming that
  no session holds its lock, reopen it: PostgreSQL `ALTER DATABASE "<name>" ALLOW_CONNECTIONS true`;
  SQL Server `ALTER DATABASE [<name>] SET MULTI_USER`.
- **`ArtifactChangedSinceCreate`**: the package now in `ArtifactDirectory` is not the one the data store was
  first created from. Put the original package back, or create a new data store.
- **`CompensationFailed`, `OutcomeUnknown`, `InstanceChanged`, `ExclusiveAccessNotEstablished`,
  `StagingIntegrityViolation`, `LockLost`**: inspect the database the failure names, confirm that no session
  holds its lock, then apply the matching step above.

### A lock that is never released

Each operation holds a per-name lock on the target server, in a session named `dmsprov:<operation id>`. If
`LockTimeout` persists after every Configuration Service instance that could hold the lock is stopped or
idle, find the holder by the lock, never by session name or age alone. Confirm that no running instance
logs that operation id, and only then end the session. The operation that owned it, if still running, then
fails safely with `LockLost`.

PostgreSQL, connected to the `postgres` database (replace `<target name>`). Target names are ASCII, and
`COLLATE "C"` makes `lower()` change only `A`–`Z`, as the service does; under the database's own locale
(Turkish, for example) `lower()` can derive a different key and miss the holder:

```sql
WITH k AS (
    SELECT encode(sha256(convert_to('dms-provision-target:v1:' || lower('<target name>' COLLATE "C"), 'UTF8')), 'hex') AS h
)
SELECT a.pid, a.application_name, a.backend_start
FROM k, pg_locks l
JOIN pg_stat_activity a ON a.pid = l.pid
WHERE l.locktype = 'advisory'
  AND l.database = (SELECT oid FROM pg_database WHERE datname = 'postgres')
  AND l.classid::bigint = ('x' || lpad(substr(k.h, 1, 8), 16, '0'))::bit(64)::bigint
  AND l.objid::bigint = ('x' || lpad(substr(k.h, 9, 8), 16, '0'))::bit(64)::bigint
  AND l.objsubid = 1
  AND l.granted;
```

After confirming that `application_name` is `dmsprov:<operation id>` and its instance is stopped, run
`SELECT pg_terminate_backend(<pid>, 10000);`.

SQL Server shows an application lock only by a truncated resource name, so request the same lock and see
which session blocks it. In one session on `master`:

```sql
EXEC sp_getapplock @Resource = N'dms-provision-target:v1:<lowercase target name>',
    @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 30000;
```

While it waits, from a second session (replace `<first session id>`):

```sql
SELECT g.request_session_id, s.program_name, s.login_time
FROM sys.dm_tran_locks w
JOIN sys.dm_tran_locks g
    ON g.resource_type = w.resource_type
    AND g.resource_database_id = w.resource_database_id
    AND g.resource_description = w.resource_description
    AND g.request_status = 'GRANT'
JOIN sys.dm_exec_sessions s ON s.session_id = g.request_session_id
WHERE w.request_session_id = <first session id>
  AND w.resource_type = 'APPLICATION'
  AND w.request_status = 'WAIT';
```

End the first session's request: cancel it, or run `sp_releaseapplock` if it was granted because nothing
held the lock. After confirming that `program_name` is `dmsprov:<operation id>` and its instance is
stopped, run `KILL <session id>`. Then wait until no row in `sys.dm_exec_sessions` has both that
`session_id` and that `login_time`; SQL Server reuses a freed `session_id` immediately.

### Leftovers after a crash

A failed or cancelled operation tries to remove what it created, with `CleanupAttempts` bounded attempts;
removal is not guaranteed. What a call could not remove is listed in its result's cleanup report, whatever
the outcome or failure category, and logged by generated name. A cancelled call returns no result, so its
leftovers are reported only in the log. A process that crashed reports nothing.

| Event | Level | Leftover |
| --- | --- | --- |
| `1438_12` `StagingResidue` | Error | A staging database |
| `1438_14` `TargetResidue` | Error | A target left closed to connections by a delete, or renamed but not marked by a create, for inspection |
| `1438_21` `ResidueLeft` | Error | An operation directory or a file in it |
| `1438_22` `CredentialResidue` | Critical | A credential file in an operation directory |
| `1438_34` `PasswordFileRetained` | Warning | The password file of a `psql` replay (the operation directory's cleanup then reports it) |
| `1438_40` `StagedFileResidue` | Error | A staged SQL Server backup |
| `1438_41` `ProbeFileResidue` | Error | A readiness probe file |

Nothing is swept automatically on a later start: a process that crashed leaves its leftovers until an
operator removes them. To find them:

- operation directories `dms-provision-*` under `OperationDirectory` (the process temp directory when it is
  not set) and staged backups `dms-provision-*.bak` under `Mssql:StagingPath`. Remove credential files
  first;
- PostgreSQL staging databases:
  `SELECT datname, shobj_description(oid, 'pg_database') FROM pg_database WHERE datname LIKE 'edfi\_dms\_provision\_stage\_%';`
- SQL Server staging databases:
  `SELECT d.name, d.state_desc, f.physical_name FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id AND f.file_id = 1 WHERE d.name LIKE 'edfi[_]dms[_]provision[_]stage[_]%';`

The 12 hex characters after `edfi_dms_provision_stage_`, and in each file name, are the operation id.
Confirm that no session belongs to that operation (`application_name` or `program_name`
`dmsprov:<operation id>` or `dms-provision:<operation id>`), then remove the leftover.
