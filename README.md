# AgentDocs for NuGet packages

AgentDocs connects versioned documentation in a NuGet package to the coding
agents working in a repository that uses that package. It has two independent
parts:

| Who | Package | Purpose |
|---|---|---|
| **NuGet author** | `Trellis.AgentDocs.Packaging` | Private build-only helper that packs a guide and verified manifest into any publisher's nupkg. |
| **Developer** | `Trellis.AgentDocs` | Local .NET tool (`agentdocs`) that installs guides from restored packages and opts the repository into automatic refresh on future restores. |

Neither part adds an application runtime dependency. Both use the
[experimental manifest contract](Trellis.Guidance.Reader/docs/experimental-guidance-v1.md);
this is **not** an official NuGet or cross-agent standard.

## Why this exists

An agent working in a consuming project needs to know the APIs and conventions
of the **installed version** of a dependency. A web page or a copy of the
latest documentation might describe a different version. Putting a Markdown
file in a NuGet package solves versioning, but not discoverability: a reader
also needs to know which file is the entry point and whether its contents
match the package's declaration.

This helper gives publishers a small, repeatable pack-time step instead of
requiring each publisher to hand-maintain a manifest and SHA-256 hash. It puts
one guide and `guidance/reference-manifest.json` in the **publisher's nupkg**.
The manifest declares the guide's package-relative path, its exact-byte hash,
and the entry point. A compatible reader can find and verify the guide from
the package that the consumer actually restored.

The helper is a private build dependency of the publisher. It does **not**
install instructions or copy files into a consumer's repository. The developer
chooses whether to install the separate tool and enable repository-local
guidance; installing a NuGet package alone never modifies agent instructions.

## Publish a guide

1. Write a Markdown guide for your package, such as `docs/agent-guide.md`.
   Make it a useful entry point: explain the package's purpose, the installed
   version's public APIs and usage patterns, and links to any related material.
2. Reference this helper from the **project that produces the nupkg**, and
   set both properties below in that project file:

   ```xml
   <PropertyGroup>
     <PackageGuidanceDocument>$(MSBuildProjectDirectory)/docs/agent-guide.md</PackageGuidanceDocument>
     <PackageGuidancePath>guides/agent-guide.md</PackageGuidancePath>
   </PropertyGroup>
   <ItemGroup>
     <PackageReference Include="Trellis.AgentDocs.Packaging"
                       Version="0.1.0-preview.1" PrivateAssets="all" />
   </ItemGroup>
   ```

3. Run `dotnet pack` on the publishing project. The resulting nupkg contains
   `guides/agent-guide.md` and `guidance/reference-manifest.json`. The guide's
   SHA-256 and entry point are generated from the file at pack time; you do
   not need to list the guide separately as a pack item.

`PackageGuidanceDocument` is the **source file** on disk.
`PackageGuidancePath` is its **destination inside the nupkg** and the
manifest's sole entry point. These are not consumer installation paths.
The destination must be a relative `.md` path using forward slashes and ASCII
letters, digits, dots, hyphens, or underscores. Empty segments, trailing dots,
Windows device names, backslashes, and path traversal are rejected. Packing
also fails if either property is missing or the source file does not exist.
If neither property is set, the helper does nothing.

For Central Package Management, put
`<PackageVersion Include="Trellis.AgentDocs.Packaging" Version="0.1.0-preview.1" />`
in `Directory.Packages.props` and omit `Version` from the private project
reference. If this preview package is not yet published, add the local feed
containing its nupkg to the publishing project's NuGet restore sources first.

## What readers and consumers receive

For the example above, the generated manifest has this shape (the actual
64-character SHA-256 value is calculated at pack time):

```json
{
  "schemaVersion": 1,
  "documents": [{ "path": "guides/agent-guide.md", "sha256": "<computed-sha256>" }],
  "entryPoints": ["guides/agent-guide.md"]
}
```

Consumers receive the publisher's guide and manifest **inside the publisher's
NuGet package**, but not this helper or its MSBuild target when the reference
uses `PrivateAssets="all"`. The developer can opt in using the
[`Trellis.AgentDocs` local tool](Trellis.AgentDocs/README.md). It reads the
already-restored NuGet graph, verifies each guide, and writes only the
repository-local context it owns. NuGet itself does not interpret the manifest.

The preview helper intentionally supports one Markdown guide and
one entry point; the manifest format can represent multiple documents, but
publishers needing that today must produce the manifest themselves.

## Work on this repository

Run `pwsh build/test-packaging.ps1` to pack the helper and a third-party-style
publisher into a local feed. The probe checks the packed document, its SHA-256
manifest, absence of consumer-side build targets or leaked helper dependencies,
and rejection of invalid paths and missing files.
Run `dotnet test Trellis.Guidance.Reader/tests/Trellis.Guidance.Reader.Tests.csproj`,
`dotnet test Trellis.AgentDocs/tests/Trellis.AgentDocs.Tests.csproj`, and
`pwsh build/test-end-to-end.ps1` to exercise discovery, installation, and
automatic refresh through both project and solution restores.
