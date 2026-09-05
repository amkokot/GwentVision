# Regression cases

Each fixed visual bug belongs in its own folder. The folder contains anonymized evidence, `case.json`, and has one matching C# test in `tests/GwentCompanion.Tests/RecordingCases`. Nothing is registered in a shared list; after the contributor writes and commits the assertion, the validation runner discovers the case and test automatically. The suite does not infer the expected result itself.

Create a case from the repository root:

```powershell
.\tools\New-ValidationCase.ps1 -CaseId tooltip-cleaver-false-positive -Summary "Reject the beige battlefield as a card tooltip" -MediaPath C:\captures\before.png
```

Then replace the deliberate `NotImplementedException` in the generated test with the assertion for the bug. Visually inspect every generated image before committing it. The standard player-name regions are always masked; pass `-ResultScreen` for end-of-match screens, which need additional identity masks.

Names use lowercase kebab-case for case folders and PascalCase for the generated test class. Never edit another contributor's case to add an unrelated regression.
