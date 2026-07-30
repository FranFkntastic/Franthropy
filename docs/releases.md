# Release Process

Franthropy uses semantic version tags such as `v0.1.0`. A tag on `main` builds,
tests, packs, and attaches the reusable NuGet packages to a GitHub release.

## Published packages

- `Franthropy.AgentBridge`
- `Franthropy.Filtering`
- `Franthropy.FFXIV`
- `Franthropy.Web`

`Franthropy.Dalamud` is intentionally excluded until its patch-sensitive,
locally resolved Dalamud dependencies have a reproducible public build
contract.

## One-time NuGet setup

The release workflow uses nuget.org Trusted Publishing rather than a
long-lived API key.

1. Create a nuget.org trusted publishing policy for owner `FranFkntastic`,
   repository `Franthropy`, workflow file `release.yml`, and environment
   `release`.
2. Create the GitHub `release` environment.
3. Add a repository secret named `NUGET_USER` containing the nuget.org profile
   name, not an email address.

The workflow requests a short-lived API key through GitHub OIDC immediately
before publishing.

## Release

Update package versions only when the public API requires it, merge to `main`,
then create and push the matching tag:

```powershell
git tag -s v0.1.0 -m "Franthropy 0.1.0"
git push origin v0.1.0
```

The workflow verifies that every project packs with the tag-derived version,
publishes `.nupkg` and `.snupkg` files, and creates GitHub release notes.
Package versions are immutable; fixes require a new patch version.
