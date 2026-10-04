# AgentDocs for NuGet packages

AgentDocs connects versioned documentation in a NuGet package to the coding
agents working in a repository that uses that package. It has two independent
parts:

| Who | Package | Purpose |
|---|---|---|
| **NuGet author** | `Trellis.AgentDocs.Packaging` | Private build-only helper that packs a guide and verified manifest into any publisher's nupkg. |
| **Developer** | `Trellis.AgentDocs` | Local .NET tool (`agentdocs`) that installs the guides of packages the developer approves and writes an index derived from the restored project graph. |

Neither part adds an application runtime dependency. Both use the
[experimental manifest contract](Trellis.Guidance.Reader/docs/experimental-guidance-v1.md);
this is **not** an official NuGet or cross-agent standard.

## Why this exists

An agent working in a consuming project needs to know the APIs and conventions
of the **installed version** of a dependency. A web page or a copy of the
latest documentation might describe a different version. Putting a Markdown
file in a NuGet package solves versioning, but not discoverability: a reader
also needs to know which file to read, when, and whether its contents match
the package's declaration.

This helper gives publishers a small, repeatable pack-time step instead of
requiring each publisher to hand-maintain a manifest and SHA-256 hash. It puts
one guide and `guidance/reference-manifest.json` in the **publisher's nupkg**.
The manifest declares the guide's package-relative path, its exact-byte hash,
how the publisher intends it to be used, and a description. A compatible reader
can find and verify the guide from the package that the consumer actually
restored.

The helper is a private build dependency of the publisher. It does **not**
install instructions or copy files into a consumer's repository. The developer
chooses whether to install the separate tool and enable repository-local
guidance; installing a NuGet package alone never modifies agent instructions.

## Publish a guide

1. Write a Markdown guide for your package, such as `docs/agent-guide.md`.
   Explain the package's purpose, the installed version's public APIs and usage
   patterns, and how it relates to other packages. Follow the
   [author guidance](#write-a-good-guide) below.
2. Reference this helper from the **project that produces the nupkg**, and
   set both properties below in that project file:

   ```xml
   <PropertyGroup>
     <PackageGuidanceDocument>$(MSBuildProjectDirectory)/docs/agent-guide.md</PackageGuidanceDocument>
     <PackageGuidancePath>guides/agent-guide.md</PackageGuidancePath>
     <PackageGuidanceUsage>onDemand</PackageGuidanceUsage>
     <PackageGuidanceDescription>Open when configuring or troubleshooting Example.Library.</PackageGuidanceDescription>
   </PropertyGroup>
   <ItemGroup>
     <PackageReference Include="Trellis.AgentDocs.Packaging"
                       Version="YOUR_PUBLISHED_VERSION" PrivateAssets="all" />
   </ItemGroup>
   ```

3. Run `dotnet pack` on the publishing project. The resulting nupkg contains
   `guides/agent-guide.md` and `guidance/reference-manifest.json`. The guide's
   SHA-256 is generated from the file at pack time; you do not need to list the
   guide separately as a pack item.

`PackageGuidanceDocument` is the **source file** on disk.
`PackageGuidancePath` is its **destination inside the nupkg**. These are not
consumer installation paths.
The destination must be a relative `.md` path using forward slashes and ASCII
letters, digits, dots, hyphens, or underscores. Empty segments, trailing dots,
`.github` segments, Windows device names, backslashes, and path traversal are rejected. Packing
also fails if either property is missing or the source file does not exist.
If neither property is set, the helper does nothing.

`PackageGuidanceUsage` is `onDemand` (the default) or `required`:

- `onDemand`: consumers list the guide with its description and agents open it
  when the task matches.
- `required`: you recommend that an agent read it before writing or changing
  code that uses your package. Each consumer decides whether to honor this, and
  only after approving your package.

`PackageGuidanceDescription` is required: one line of at most 200 characters
with no control characters that says **when to open the guide**. It is
publisher text shown in every consumer's index, so state a topic, not an
instruction.

For Central Package Management, put
`<PackageVersion Include="Trellis.AgentDocs.Packaging" Version="YOUR_PUBLISHED_VERSION" />`
in `Directory.Packages.props` and omit `Version` from the private project
reference. Replace the placeholder with the published version. If a preview is not yet published, add the local feed
containing its nupkg to the publishing project's NuGet restore sources first.

## What readers and consumers receive

For the example above, the generated manifest has this shape (the actual
64-character SHA-256 value is calculated at pack time):

```json
{
  "schemaVersion": 1,
  "documents": [{
    "path": "guides/agent-guide.md",
    "sha256": "<computed-sha256>",
    "usage": "onDemand",
    "description": "Open when configuring or troubleshooting Example.Library."
  }]
}
```

Consumers receive the publisher's guide and manifest **inside the publisher's
NuGet package**, but not this helper or its MSBuild target when the reference
uses `PrivateAssets="all"`. The developer can opt in using the
[`Trellis.AgentDocs` local tool](Trellis.AgentDocs/README.md). It reads the
already-restored NuGet graph, verifies each guide, and writes only the
repository-local context it owns. NuGet itself does not interpret the manifest.

### Several documents

To publish more than one document, declare `PackageGuidanceItem` items instead
of the single-document properties (using both is an error):

```xml
<ItemGroup>
  <PackageGuidanceItem Include="docs/start.md" PackagePath="guide/start.md"
                       Usage="required" Description="Read before using Example.Library." />
  <PackageGuidanceItem Include="docs/http.md" PackagePath="guide/http.md"
                       Description="Open when calling the Example HTTP client." />
  <PackageGuidanceItem Include="docs/internals.md" PackagePath="guide/internals.md"
                       Usage="supporting" />
</ItemGroup>
```

`Include` is the source file and `PackagePath` its destination in the nupkg,
under the same rules as above. `Usage` is `required`, `onDemand` (the default) or
`supporting`; a supporting document may omit `Description`, because consumers do
not list it and an agent reaches it through a link from another document. The
helper checks the same rules the reader applies (path, usage, description,
no duplicates, at least one required or on-demand document), hashes each file,
and packs the documents and the manifest. Documents appear in the manifest in
the order the items are declared.

### Link to guidance from another package

With the item form, declare a virtual Markdown path when one guide links to a
document published by another package:

```xml
<ItemGroup>
  <PackageGuidanceItem Include="docs/start.md" PackagePath="guide/start.md"
                       Usage="required" Description="Read before using Example.Library." />
  <PackageGuidanceReference Include="guide/yarp.md"
                            PackageId="Trellis.Yarp"
                            DocumentPath="guides/yarp.md" />
</ItemGroup>
```

The source guide uses an ordinary relative Markdown link such as
`[YARP guidance](yarp.md#forwarding-actors)`. `Include` is a **virtual path in
the source package's guidance namespace**; no duplicate file is packed there.
`PackageId` and `DocumentPath` identify the exact document declared by the
target package. Reference paths share the local document namespace, so they
cannot collide with a `PackageGuidanceItem` path or directory prefix. Every
package path uses `/` separators and cannot contain a `.github` segment.

The publisher helper validates and writes the declaration, while
`agentdocs validate --strict` verifies that a Markdown link actually uses it.
It cannot validate another package at pack time. In a consumer repository,
AgentDocs rewrites the parsed link destination only when the target package is
both restored and approved, after verifying both packages' declared document
hashes. If the target is absent, unapproved, or publishes no guidance, AgentDocs
installs a generated unavailable notice at the virtual path without reading
target content. A missing exact target document also produces a reason-coded
notice and warning. A missing heading preserves the rewritten link and fragment
and emits a warning. Consumers can promote either warning to failure with
`--strict-references`.

Raw HTML references, query strings, package self-references, and link spellings
that differ from the declared path by case or Unicode normalization are contract
errors. Reference declarations that no Markdown document uses are publisher
warnings and materialize nothing. The target is resolved across the complete
selected graph; references never add a dependency or grant target approval.

Cross-package references currently require the `PackageGuidanceItem` form.
They do not add a NuGet dependency: if the target should always be restored,
declare the appropriate package dependency separately.

## Check your guidance before publishing

The helper checks that the manifest is well formed. Run the
[`agentdocs validate`](Trellis.AgentDocs/README.md) command on the built package
to check the guidance itself: whether the required documents are small enough,
whether relative links resolve, whether every supporting document is linked from
somewhere an agent will read, and whether the front matter of your documents is
closed properly.

```powershell
dotnet pack -c Release
dotnet tool run agentdocs validate artifacts/Example.Library.1.0.0.nupkg --strict
```

See the [contract](Trellis.Guidance.Reader/docs/experimental-guidance-v1.md) for
the manifest format.

## Write a good guide

- **Keep required guides small and self-contained.** A `required` guide is read
  before substantive work in every project that restores your package, so
  its size is a cost on every task. It should not tell the agent to read
  further documents "now"; put the routing it needs inside it.
- **Say when to open each on-demand guide.** The description is what an agent
  decides from. "Open when adding or changing API versioning" is useful;
  "Documentation" is not.
- **Keep required documents stable.** Consumers review what your package asks
  agents to read; adding or changing required documents is visible to them.
- **Use relative links to local documents or declared package references.**
  Declare `PackageGuidanceReference` for a version-correct link to another
  package; never escape the package directory or link to a moving branch.
- **Never hard-code where a consumer installs your guide.**
- **Pin the guide's line endings.** Git checks files out with CRLF on Windows and
  LF elsewhere, so the same commit packs different bytes on each OS. Consumers
  are unaffected (the hash matches the packed bytes, and the tool compares
  canonical text), but your packages are only reproducible across machines if
  the guide has fixed endings, for example `*.md text eol=lf` in `.gitattributes`.
- **Write for the installed version.** The guide must be true of the package
  version it ships in.

## Work on this repository

Run `dotnet build Trellis.AgentDocs.slnx -c Release` and
`dotnet test Trellis.AgentDocs.slnx -c Release` to build and test all projects.
Run `pwsh build/test-packaging.ps1` to pack the helper and a third-party-style
publisher into a local feed. The probe checks the packed document, its SHA-256
manifest, `usage` and `description` (including characters that need escaping),
absence of consumer-side build targets or leaked helper dependencies,
cross-package reference resolution, and rejection of invalid paths, usages,
descriptions, references and missing files.
Run `pwsh build/test-end-to-end.ps1` to exercise discovery, pending-by-default
approval, explicit sync after package upgrades, project and solution graphs,
and isolation of invalid manifests. Both scripts use only local feeds;
nothing is published.

## Versioning and releases

Both published packages share one repository-wide Nerdbank.GitVersioning
`version.json`; the internal reader is not published. Package versions are
computed from Git history at build time, not set in the project files.
`DotNet.ReproducibleBuilds` normalizes CI source paths in the tool's binaries.
The single [build and publish workflow](.github/workflows/build.yml) tests
both components, packs exactly two matching-version nupkgs, and uploads them
as one artifact. Pushes and pull requests verify only. To preview a release,
run the workflow manually with its default `dry_run: true`. To publish both
packages, dispatch it from `main` with `dry_run: false`.

## License

AgentDocs is licensed under the [MIT License](LICENSE).
