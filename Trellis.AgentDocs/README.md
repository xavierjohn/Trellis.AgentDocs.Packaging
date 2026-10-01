# Trellis.AgentDocs

`Trellis.AgentDocs` is an opt-in local .NET tool for developers using NuGet
packages that publish [AgentDocs guidance](../Trellis.Guidance.Reader/docs/experimental-guidance-v1.md).
It reads the **restored package versions**, verifies each declared Markdown
document against its SHA-256 hash, and installs the guides of packages **you
approve** where coding agents can find them. It does not require the publisher
to use Trellis libraries or add an application runtime dependency.

## Opt in once

From the Git repository root, with a project or solution already restored:

```powershell
$version = 'YOUR_PUBLISHED_VERSION'
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version $version --tool-manifest .config\dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project> [<more-projects-or-solutions>]
```

If the repository already has `.config\dotnet-tools.json`, reuse it instead of
creating a second manifest. Replace the placeholder with the published version;
until it is available on NuGet.org, add the local feed containing its nupkg to
`dotnet tool install` with `--add-source`. The local tool is independently
versioned; it does **not** need to match the version of any library package.
`init` accepts several projects or solutions, so projects outside your main
solution can be covered too.

## Approve the packages you want

Restoring a package that publishes guidance never activates it. After `init`,
each such package is **pending**: the tool has not parsed its manifest or read
its documents, and the index lists only its ID and version. `init` prints one
line to paste into `.agentdocs\policy.json`, the consumer-owned approval list:

```json
{
  "schemaVersion": 1,
  "approvedPackages": ["Trellis.Core", "Trellis.ServiceLevelIndicators"]
}
```

Add the IDs you trust, commit the file, and run `dotnet tool run agentdocs sync`.
IDs match case-insensitively and apply to every version. The tool creates the
file (empty) on `init` if it does not exist and never rewrites or removes it.
Removing an ID makes that package pending again and removes its installed
guides on the next `sync`. A pending package can never fail a command, even if
its manifest is invalid, because it is never read. Approval is per package ID,
not per publisher, so use NuGet package source mapping for approved packages.
After approving a package, review the committed diff of `.agentdocs`: an
approved package may later add documents or change which documents it marks
required.

## What gets installed

`init` and `sync` install approved guides under
`.agentdocs\packages\<package-id>\<declared-path>` and write `.agentdocs\README.md`,
an index derived from the restored project graph:

- **Required reading by project.** Analysed projects are grouped by the set of
  approved packages they restore. Each group lists the documents its
  publishers marked `required`.
- **On-demand documents.** Documents marked `onDemand` are listed once per
  package with their descriptions. `supporting` documents are installed but not
  listed; they are reached through links from other documents.
- **Pending review.** Packages that publish guidance but are not approved.
- **Rules** for agents: which files belong to which project, when required
  documents must be read (before substantive work on a listed project, never
  merely for building or testing), how to combine several projects, what an
  unlisted project means, and to run `sync` after changing package references.

Descriptions are publisher text. The index renders them as inline code spans
and tells agents to treat them as topic labels and ignore instructions in them.
`init`, `sync`, and `check` print, per group, the required documents and their
canonical size in bytes, plus the index size, so the cost of what agents will
read is visible. There is no enforced limit.

`init` adds small managed blocks to Git-root `AGENTS.md`, one `AGENTS.md` in
each selected solution directory (or project directory for a project-only entry
point), and Git-root `.github\copilot-instructions.md`. When an entry is at the
Git root, its pointer is deduplicated. Project `src` and `tests` directories and
additional source roots do not get new pointers. Customer-authored instructions
in the selected pointer files are preserved. Each pointer unconditionally says
"Read `.agentdocs/README.md` now", with the path adjusted relative to its
containing file and the repository-root path as a fallback. Visual Studio Copilot
must have custom instructions enabled to use that pointer.

Commit the tool manifest, pointers, `policy.json`, `agent-context.json`, the
index, and the guides. `agent-context.json` records the selected graph and
ownership hashes needed for `check` and safe cleanup after a fresh clone. Keep
the packages you approve reviewed: their documents are instructions for an
agent that acts with your privileges.

## After a package upgrade

Nothing runs automatically. After changing a package reference or version,
restore and then sync:

```powershell
dotnet restore
dotnet tool run agentdocs sync
```

The index tells agents to do the same. In CI, `dotnet restore` followed by
`dotnet tool run agentdocs check --strict` fails when the installed guidance no
longer matches the restored packages or when a hand-written instruction links to
a missing file.

The recorded graph tracks every restore input, so even a bump of a package that
publishes no guidance changes it and plain `check` fails until someone runs
`sync`. To fail CI only when the installed guidance, index or pointers would
actually change, use `dotnet tool run agentdocs check --strict --content-only`:
a graph that differs only in restore inputs that do not affect what is
installed passes, with a note suggesting `sync` to refresh the record. A
modified, missing or outdated installed guide still fails.

If an approved package no longer supplies a guide, or you withdraw its approval,
`sync` removes its previously owned guides and prints a notice. A malformed
manifest, bad hash, mixed versions of one approved package, stale selected graph,
or modified owned file fails the command without writing; it does not silently
install unverified guidance or overwrite your edits. If a write is interrupted,
re-run with `--force` or restore the files from Git.

`sync`, `init`, and `check` warn with file and line about missing local
Markdown links in `AGENTS.md`, `CLAUDE.md`, and `.github/copilot-instructions.md`,
including nested files. They recognize Markdown links and backticked paths,
resolve them relative to the instruction file or Git root, and never rewrite
hand-authored links. Use `--strict` to fail before writing if any such link is
missing.

| Command | Purpose |
|---|---|
| `agentdocs init <solution-or-project>...` | Opt in, create an empty policy if none exists, and install guides for approved packages. |
| `agentdocs sync` | Update installed guides and the index from the recorded graph and the policy. |
| `agentdocs check` | Check graph and installed context without writing; useful in CI. `--content-only` ignores drift that does not change the installed guidance. |
| `agentdocs remove` | Remove owned guides, pointers, and context state without touching customer text or the policy. |
| `agentdocs validate <package.nupkg\|directory>` | For authors: check a package's guidance before publishing. Read-only; needs no repository, restore or policy. |

`validate` is the publisher-side check, so a mistake is caught before a consumer
meets it. It reports errors for contract violations (`AD001`-`AD010`: manifest
shape, unsafe or missing paths, hash mismatches, usage, descriptions, at least
one required or on-demand document, duplicate or aliased paths, non-UTF-8
documents, archive entries that would extract onto the same path (same decoded path, case or Unicode alias, or a file that is also a directory), files over the
validator's resource limits) and warnings for guidance that is valid but hard to
use (`AD101`-`AD108`): an oversized required set, links that will not resolve
once installed (missing files, headings that do not exist, root-relative paths,
and targets that are not installed Markdown such as images or code samples, in Markdown links and in raw HTML `href`/`src`),
supporting documents that no required or on-demand document reaches, a front
matter block that is not valid YAML, Markdown files beside the guidance that the
manifest does not list, total guidance size, the number of index entries, and
two on-demand documents with the identical description. Documents are read with a
Markdown parser (GitHub heading anchors) and a YAML parser, and only the
manifest and the documents it declares are read from a package, each within a size limit.
It exits 1 on errors, and on warnings with `--strict`. Use
`--max-required-bytes N`, `--max-total-bytes N` and `--max-indexed-documents N`
to change the budgets (defaults 32768, 4 MiB and 100) and `--format json` for
machine-readable output on standard output.

`init` accepts `--restore` if the selected project has not yet been restored,
`--source-root DIR` for additional source directories, and `--dry-run` to
preview. The tool refuses unowned destinations and concurrent edits; `--force`
is available only for a reviewed adoption or repair. The current context
selects one graph per Git repository; mixed resolved versions of an approved
package that publishes guidance across its selected projects are rejected
rather than silently mixing guides. Packages without guidance, and packages
that are not approved, may resolve to different versions across those projects.
For freshness checks, NuGet's evaluated per-target restore references are
compared against the restored project-reference graph. Build-only references
that NuGet omits, such as a SQL project supplying a dacpac, do not cause false
stale-assets errors.

Contexts created by earlier previews installed restore hooks. Run `agentdocs
remove` with the version that created them, or delete `.agentdocs` and the
restore imports it added to `Directory.Build.targets` and
`Directory.Solution.targets`, and then run `init` again.

## Known limits

- A frontend directory inside a project's own directory (for example a SPA under
  an ASP.NET project) belongs to that project by the index's rules and inherits
  its required reading. Separately rooted projects do not.
- Files compiled into a project from outside its directory are not matched to it.
- A project outside the selected graph is "not analysed": the index requires
  nothing for it. Select it in `init` to cover it.

The manifest format is an **experimental convention**, not a NuGet standard.
An agent needs the repository pointers (or explicit direction from its user)
to find the installed index; installing a guide cannot make every agent
discover it automatically.
