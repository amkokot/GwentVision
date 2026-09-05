# Contributing to Gwent Vision

Thanks for taking an interest in Gwent Vision. You do not need to be an experienced programmer to help. Testing the app, describing a confusing screen, reporting a missed card, improving the documentation, or suggesting an idea can all move the project forward.

If this is your first open-source project, that is completely fine. Start with one small problem, ask questions when something is unclear, and keep your change focused.

## Choose how you would like to help

- **Found a bug?** Open a [bug report](../../issues/new?template=bug_report.yml). Explain what happened and what you expected instead.
- **Have an idea?** Open a [feature request](../../issues/new?template=feature_request.yml). You do not need to know how to build it.
- **Want to test?** Try a release on your display setup or battlefield and report anything that looks wrong.
- **Want to change the project?** Follow the workflow below and open a pull request.

A **pull request**, often shortened to **PR**, is a proposed change that other people can review before it becomes part of the project.

## Set up the project

You will need:

- Windows 10 or later;
- [Git](https://git-scm.com/downloads) or GitHub Desktop;
- [Git LFS](https://git-lfs.com/), which downloads the larger validation images; and
- the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

Use the green **Code** button on GitHub to clone the repository. If you use a terminal, run `git lfs install` once before working with the validation images.

From the repository folder, check that the project builds:

```powershell
dotnet build GwentCompanion.sln -c Release
```

If the build succeeds, your setup is ready.

## A simple contribution workflow

1. **Check the issue tracker.** Search for the bug or idea before starting. If it is already listed, leave a comment saying you would like to work on it. Otherwise, open a new issue.
2. **Start from the latest `main`.** `main` is the current shared version of the project.
3. **Create a branch.** A branch is your own workspace for one change. Give it a short name such as `fix-tooltip-detection` or `improve-install-guide`.
4. **Make one focused change.** Small PRs are easier to understand, test, and combine with other people's work.
5. **Add or update a test.** The test records what should keep working after your change.
6. **Run the required checks, then open a PR.** Explain the problem, what you changed, and how you tested it.

You can do these steps with GitHub Desktop or with Git commands. A typical command-line start looks like this:

```powershell
git switch main
git pull
git switch -c fix-short-description
```

Using a coding assistant is welcome, but please read the changes it makes and run the same checks you would run for code written by hand. Do not give an assistant private recordings, account information, or unmasked screenshots.

## How the validation system works

A **regression test** is a small check that proves a fixed bug does not quietly return later.

The validation suite does not decide the correct behavior for you. When you change something, you still need to describe the expected result and write an assertion—a line of test code that checks that result.

After you add the test, discovery is automatic. You do not need to edit a central test list, change a validation version number, add a command-line option, or modify the GitHub workflow. This design also reduces merge conflicts when several people add tests at the same time.

There are two main paths:

- Use a **recording validation case** when a detector bug depends on pixels from a screenshot.
- Use a **media-free validation test** for features, layouts, data rules, accessibility, performance safeguards, or integration behavior.

If you are unsure which path fits, open an issue or a draft PR and ask.

## Path A: fix a detector bug

Use this path when Gwent Vision misreads—or fails to read—something visible in the game.

Try to provide:

- a **positive example**, showing the situation the detector should recognize; and
- a nearby **negative example**, showing a similar situation it should reject.

Give each bug its own case. From the repository root, run:

```powershell
.\tools\New-ValidationCase.ps1 `
  -CaseId tooltip-cleaver-false-positive `
  -Summary "Reject the beige battlefield as a card tooltip" `
  -MediaPath C:\captures\before.png,C:\captures\nearby-negative.png
```

Change the example ID, summary, and image paths to match your bug. An ID uses lowercase words separated by hyphens, such as `tooltip-cleaver-false-positive`. Add `-ResultScreen` if the evidence comes from the result or MMR screen.

The script will:

1. cover the standard player-identity areas;
2. create a small `case.json` description;
3. number the evidence as `evidence-01.jpg`, `evidence-02.jpg`, and so on; and
4. create a matching C# test file.

Open the generated images and inspect every part of them. Automatic masking is a starting point, not proof that an image is safe to publish. Remove any names or other identifying information the script missed.

The generated test contains a deliberate `NotImplementedException`. Replace that line with the assertion for your expected behavior, then commit the case folder and its test together.

## Path B: change a feature, layout, or rule

This path does not use gameplay screenshots. Create a small test with:

```powershell
.\tools\New-ValidationTest.ps1 `
  -TestId candidate-panel-empty-state `
  -Kind feature `
  -Summary "Keep the candidate panel useful when no card is recognized"
```

The ID and kind use lowercase words separated by hyphens. Common kinds include:

- `feature`
- `layout`
- `data`
- `accessibility`
- `performance`
- `integration`

These are examples, not a closed list. Choose another clear kind if it describes the change better.

Open the generated C# file and replace its `NotImplementedException` with a deterministic check. **Deterministic** means the same code should produce the same result every time. A good validation test must not require GWENT to be running, internet access, a user account, or private files from your computer.

Small, non-personal sample files may go in `tests/GwentCompanion.Tests/Fixtures`. A performance test should check a stable limit—such as a maximum amount of work—not whether one computer finished within an exact number of milliseconds.

If you intentionally redesign the interface, update the test that describes the old layout and add or revise a test that describes the new layout. Explain the decision in your PR. Do not simply delete a failing test to make the checks green.

## Run the required checks

Run all three commands before opening or updating a PR:

```powershell
dotnet build GwentCompanion.sln -c Release
dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --recording-validation-regression
dotnet run --project tests/GwentCompanion.Tests/GwentCompanion.Tests.csproj -c Release -- --contributor-validation-regression
```

These commands check that:

- the project compiles;
- every public validation file still has the expected name, size, and SHA-256 fingerprint;
- required privacy declarations and corpus limits are intact;
- known difficult detector sequences still behave correctly; and
- all automatically discovered feature and layout tests pass.

GitHub runs the same checks again after you open the PR. It also runs a malware scan. A PR cannot merge while a required check is failing.

## Keep your branch up to date

Another contribution may be merged while you are working. Before your PR is merged, update your branch from the latest `main` and run the three checks again. In GitHub, you may see an **Update branch** button. GitHub Desktop also has an **Update from main** option.

If both branches changed the same lines, Git may report a **merge conflict**. This does not mean either person did something wrong. It only means a person must choose how the two edits fit together. Ask for help in the PR if the correct combination is not obvious.

There is no shared validation version number to update. Once your branch includes the latest `main`, the automatic runner tests your change together with every validation case that has already been merged.

## Protect people's privacy

Never commit a raw capture or recording. Public evidence must not contain:

- player names or avatars tied to an account;
- profile or account identifiers;
- notifications containing a person's name;
- private deck information;
- local computer paths;
- session journals;
- personal deck-library data; or
- opponent-memory data.

Use `tools/Export-AnonymizedValidationCorpus.ps1` when exporting a larger public corpus. Review a contact sheet of every new sequence before committing it. If identifying information appears outside an existing mask, improve the sanitizer and regenerate the evidence.

Validation JPG and PNG files are tracked with Git LFS. Do not bypass Git LFS or replace a corpus image without regenerating its manifest through the export tool.

## Keep the repository safe

Do not commit:

- executable files or ZIP archives;
- dependency or package caches;
- passwords, access tokens, browser profiles, or other credentials;
- generated `bin` or `obj` folders; or
- unrelated recordings and temporary files.

Keep new dependencies to a minimum and explain why they are needed in the PR. GitHub scans the repository and the packaged Windows release with ClamAV, but automated scanning does not replace careful review.

If you find a security problem, do not post the details in a public issue. Follow the private reporting instructions in [SECURITY.md](SECURITY.md).

## What to include in your pull request

A useful PR description answers four questions:

1. What problem does this solve?
2. What did you change?
3. What test or evidence did you add?
4. Could this increase CPU, memory, disk, or network use while a game is running?

It is fine to open a **draft PR** before the work is complete. A draft lets other contributors offer advice without treating the change as ready to merge.

## Maintainer merge settings

Maintainers should require the Validation and Malware scan checks before merging. If the repository belongs to an organization, GitHub's merge queue is the easiest way to test each queued PR against the newest `main`. Otherwise, enable **Require branches to be up to date before merging**.

## Need help?

Open an issue or a draft PR and describe where you got stuck. Include the command you ran and the error message, but remove usernames, local paths, credentials, and private game information first. Asking a clear question is a contribution too.
