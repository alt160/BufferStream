# Releasing BufferStream

## One-time setup

1. Create the public GitHub repository at `alt160/BufferStream` and enable private vulnerability reporting.
2. Create or claim the `BufferStream` package on NuGet.org.
3. Configure a NuGet.org trusted-publishing policy for this repository and `.github/workflows/release.yml`.
4. Protect `main`: require the CI workflow and disallow force pushes.

## Release procedure

1. Update `CHANGELOG.md` and the release notes in `BufferStream.csproj`.
2. Run the Release build and package-consumer smoke test locally.
3. Create and push an annotated semantic-version tag, for example `v1.0.0`.
4. The release workflow builds from that tag, verifies the package through the smoke project on .NET 8, 9, and 10, creates the GitHub release, attaches the `.nupkg`, `.snupkg`, and SHA-256 manifest, attests the package, and publishes the `.nupkg` to NuGet.org through OIDC trusted publishing.

Do not publish from a developer machine. If the release workflow fails, repair the source or workflow and cut a new version tag; NuGet.org packages are immutable.
