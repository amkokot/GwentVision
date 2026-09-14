# Release checklist

The `release/GwentVision` folder is the checked-out public repository. Do not publish the private working tree directly.

For a later rollover, keep the existing tracked `release/GwentVision` checkout intact. Export to a fresh sibling such as `release/GwentVision-next`, run the release checks there, then review and apply that tree's diff to the tracked checkout while preserving its `.git` directory. The exporter copies every private `tests/recording-validation/cases` add-on and rebuilds the anonymized schema-2 corpus manifest, so validated detector failures move forward without exposing the private raw corpus. The exporter refuses to replace a tracked checkout even when `-Force` is supplied.

1. Review `README.md`, especially the purpose, install steps, and download link.
2. Run `tools/Test-PublicRelease.ps1 -RepositoryRoot release/GwentVision`.
3. Build and run both `--recording-validation-regression` and `--contributor-validation-regression` from the exported folder.
4. Run a current local antivirus scan on the exported folder.
5. In `release/GwentVision`, inspect `git status`, `git diff --check`, and `git lfs status` before committing. The corpus and large image libraries must remain LFS objects.
6. Push the release branch and open a pull request into `main`. Wait for the `recording-corpus`, public-release, Pages, and malware checks to pass before merging.
7. Confirm that `main` protection requires the release checks. If this is an organization-owned public repository, require the merge queue; otherwise enable **Require branches to be up to date before merging**.
8. Confirm GitHub private vulnerability reporting and Dependabot alerts remain enabled.
9. After merging, push the version tag matching `Directory.Build.props`. The Release workflow builds, scans, and attaches the Windows ZIP plus its SHA-256 file. The README download link then resolves automatically.

The public export intentionally excludes raw recordings, journals, settings, opponent memory, deck scans, browser state, generated feature caches, saved `.gvm` match files, and local paths. Only masked corpus media named by the schema-2 manifest is included.
