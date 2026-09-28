# Trellis.AgentDocs

`Trellis.AgentDocs` is an opt-in local .NET tool for developers using NuGet
packages that publish [AgentDocs guidance](../Trellis.Guidance.Reader/docs/experimental-guidance-v1.md).
It reads the **restored package versions**, verifies each declared Markdown
document against its SHA-256 hash, and installs the guides where coding agents
can find them. It does not require the publisher to use Trellis libraries or
add an application runtime dependency.

## Opt in once

From the Git repository root, with a project or solution already restored:

```powershell
$version = 'YOUR_PUBLISHED_VERSION'
dotnet new tool-manifest --output .config
dotnet tool install Trellis.AgentDocs --version $version --tool-manifest .config\dotnet-tools.json
dotnet tool run agentdocs init <solution-or-project>
dotnet tool run agentdocs check
```

If the repository already has `.config\dotnet-tools.json`, reuse it instead of
creating a second manifest. Replace the placeholder with the published version;
until it is available on NuGet.org, add the local feed
containing its nupkg to `dotnet tool install` with `--add-source`. The local
tool is independently versioned; it does **not** need to match the version of
any library package. No guide is installed merely by adding or restoring a
NuGet dependency: `init` is the explicit consent to manage repository files.

`init` creates Git-root `.agentdocs\README.md` as an index and installs guides
under `.agentdocs\packages\<package-id>\<declared-path>`. It adds small managed
blocks to root and applicable nested `AGENTS.md` files and Git-root
`.github\copilot-instructions.md`, preserving customer-authored instructions.
Visual Studio Copilot must have custom instructions enabled to use that pointer.
Review the generated content and commit the tool manifest, pointers, context,
and restore opt-in files if the team wants them shared.

## After a package upgrade

Change the package reference and run the same `dotnet restore` you already
use. The one-time opt-in adds managed imports to the relevant
`Directory.Build.targets` and `Directory.Solution.targets`. After the selected
project or solution finishes restoring, those hooks run the pinned tool to
refresh the **recorded** package graph. You do not have to run `agentdocs sync`
for normal upgrades, and upgrading a library does not require upgrading the
tool. A package newly added to the selected graph is picked up on restore.

If an upgraded package no longer supplies a guide, its previously owned guide
is removed and restore prints a notice without failing. A malformed manifest,
bad hash, stale selected graph, or modified owned file fails the refresh; it
does not silently install unverified guidance or overwrite your edits.
Normal restores in repositories that have not opted in remain unchanged.
Removing the tool's manifest pin or deleting the owned context by hand does
not disable the import safely; use `agentdocs remove` first.

| Command | Purpose |
|---|---|
| `agentdocs init <solution-or-project>` | Opt in and install verified guides from already-restored packages. |
| `agentdocs check` | Check graph and installed context without writing; useful in CI. |
| `agentdocs refresh` | Refresh from the recorded graph; run automatically after opted-in restores. |
| `agentdocs sync` | Manual refresh for changes made without restore. |
| `agentdocs remove` | Remove owned guides, pointers, restore hooks, and context state without touching customer text. |

`init` accepts `--restore` if the selected project has not yet been restored,
`--source-root DIR` for additional source directories, and `--dry-run` to
preview. The tool refuses unowned destinations and concurrent edits;
`--force` is available only for a reviewed adoption or repair. A project
restore and a solution restore have different MSBuild entry points, so the
opt-in installs both hooks. The current context selects one graph per Git
repository; mixed resolved versions of a package that publishes guidance across
its selected projects are rejected rather than silently mixing guides. Packages
without guidance may resolve to different versions across those projects.

The manifest format is an **experimental convention**, not a NuGet standard.
An agent needs the repository pointers (or explicit direction from its user)
to find the installed index; installing a guide cannot make every agent
discover it automatically.
