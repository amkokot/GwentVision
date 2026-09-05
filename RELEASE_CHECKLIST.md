# Release checklist

The `release/GwentVision` folder produced by `tools/Export-ReleaseRepository.ps1` is the public repository payload. Do not publish the working tree directly.

1. Review `README.md`, especially the purpose, install steps, and download link.
2. Run `tools/Test-PublicRelease.ps1 -RepositoryRoot release/GwentVision`.
3. Build and run both `--recording-validation-regression` and `--contributor-validation-regression` from the exported folder.
4. Run a current local antivirus scan on the exported folder.
5. In `release/GwentVision`, run `git init -b main`, `git lfs install`, and `git add .`. Inspect `git status` and `git lfs ls-files` before committing; the corpus and large image libraries must appear as LFS objects.
6. Create the GitHub repository, set its default branch to `main`, and push.
7. After the first workflows finish, protect `main` by requiring the `recording-corpus` and `clamav` status checks. If this is an organization-owned public repository, require the merge queue; otherwise enable **Require branches to be up to date before merging**.
8. Enable GitHub private vulnerability reporting and Dependabot alerts.
9. Push the `v0.2.64` version tag. The Release workflow builds, scans, and attaches the Windows ZIP plus its SHA-256 file. The README download link then resolves automatically.

The public export intentionally excludes raw recordings, journals, settings, opponent memory, deck scans, browser state, generated feature caches, and local paths. Only masked corpus media named by the schema-2 manifest is included.
