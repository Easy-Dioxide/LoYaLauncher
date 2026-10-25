# LoyaLauncher 0.7.1 — Network reliability patch

## Fixed
- Corrected Modrinth v2 search `facets` format to an array of string arrays.
- Corrected Modrinth project-version filtering to use `game_versions` and `loaders` JSON-array query parameters.
- Added bounded Modrinth API timeout and more actionable HTTP error details.
- Added official manifest-provided version JSON URLs as a fallback after configured mirrors.
- Corrupt version JSON and version-manifest caches are removed before retrying, avoiding repeated “existing file” skips.
- Bumped shared assembly/package version to 0.7.1.
- Added regression tests for Modrinth request shapes.

## Not yet verified
- Build and unit tests require the repository-pinned .NET SDK 10.0.402, which was unavailable in the editing environment.
- Microsoft/Xbox authentication, Java downloads, GitHub release downloads, and actual Windows network behavior still require runtime validation.
