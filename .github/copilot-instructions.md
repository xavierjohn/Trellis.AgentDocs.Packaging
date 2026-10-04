# Agent instructions for Trellis.AgentDocs.Packaging

## Running tests: Microsoft.Testing.Platform

This repository uses **Microsoft.Testing.Platform (MTP), not VSTest**.
`global.json` selects the MTP runner, and both test projects are executable
xUnit v3 applications with `UseMicrosoftTestingPlatformRunner` enabled.

Run commands from the repository root. These PowerShell examples use Windows
paths; use native path separators on other operating systems.

```powershell
dotnet build Trellis.AgentDocs.slnx -c Release
dotnet test Trellis.AgentDocs.slnx -c Release --no-build -- --no-progress
```

For a focused test run, invoke the test application directly. Put runner
arguments **after `--`**:

```powershell
dotnet run --project Trellis.AgentDocs\tests\Trellis.AgentDocs.Tests.csproj -c Release --no-build -- --filter-method '*Context_schema_one*' --no-progress
dotnet run --project Trellis.Guidance.Reader\tests\Trellis.Guidance.Reader.Tests.csproj -c Release --no-build -- --filter-method '*Discover*' --no-progress
```

- Use xUnit's `--filter-method`, `--filter-class`, or `--filter-query`, not
  VSTest's `--filter "FullyQualifiedName~..."`. Repeat `--filter-method` for
  multiple OR selections; do not join expressions with `|`.
- Do not copy VSTest flags such as `--logger trx`, `--nologo`, or
  `--verbosity quiet` into MTP test commands. An unrecognized option can exit
  with code 5 before any tests run; that is a command-line failure, not a test
  failure or evidence that dependencies are missing.
- `--no-progress` currently works in both test applications. The Reader runner
  deprecates it in favor of `--progress off`, but the AgentDocs runner does not
  support that replacement yet. Use the common option for solution-wide runs.
- Options depend on the platform and registered extensions in each test
  application. Check the actual runner rather than assuming the latest online
  documentation applies to both:

  ```powershell
  dotnet run --project Trellis.AgentDocs\tests\Trellis.AgentDocs.Tests.csproj -c Release --no-build -- --help
  dotnet run --project Trellis.Guidance.Reader\tests\Trellis.Guidance.Reader.Tests.csproj -c Release --no-build -- --help
  ```

- `--no-build` requires a successful build of the same configuration after the
  latest code changes. Do not rebuild or pack the same configuration while its
  test executable is running: Windows can lock the apphost. Wait for your run,
  or build and test consistently with `-c Debug` in a separate configuration;
  do not terminate an unrelated process to release the lock.
- Lifecycle tests invoke MSBuild and restore, so the full suite can take several
  minutes without output when progress is disabled. Wait for the final summary
  and exit code; do not report success if zero tests ran or suppress failures
  with `--ignore-exit-code`.

After relevant packaging or lifecycle changes, also run:

```powershell
pwsh -NoProfile -File build\test-packaging.ps1
pwsh -NoProfile -File build\test-end-to-end.ps1
```

Reference: [Microsoft.Testing.Platform CLI options](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-cli-options).
