# SlimeHelper.Checks

A self-contained check program for Slime's tools. It compiles the real `SlimeToolManager`, `SlimeBrainManager`
and `SlimeMemory` from the app (linked, no copies) and exercises them against temporary folders.

```powershell
dotnet run --project SlimeHelper.Checks
```

Exit code 0 = everything passed, 1 = something failed (the failing checks are printed).

## What it covers
- **Brain**: binding, tag normalisation (`MyRepo`, `#myrepo`), working-directory lookup, nested repos.
- **Stale-build guard and activity trail**: a build is skipped when an edit in the same reply failed, and the CLI shows every step in order.
- **Claims and announcements vs reality**: she is corrected (once) when she says she changed something, or only announces it ("Adding X..."), and the app wrote nothing. Questions, questions about past work and the General thread are exempt (General gets an explanation instead).
- **History**: the app's status lines are never stored or replayed to the model (including old poisoned history).
- **Memory (SAVE_NOTE)**: sub-folders, `@Subjects`, `General`, and the path guard (no `..`, no sibling folders, no absolute paths).
- **EDIT_FILE**: read-before-edit, unique FIND, brace-balance guard, BOM and CRLF preserved, backups.
- **READ_FILE / WRITE_CODE / SEARCH_FILES**: line ranges, truncation, protected files (`.env`, `.git`), overwrite guards.
- **Status-line filter** and search-intent detection.
- **RUN_CHECK**: a real `dotnet build` (first failing, then fixed), approval prompt, trust revoked when a build file
  is edited, declined runs execute nothing, several targets -> asks which, and `npm` scripts (if npm is installed).

## Safe to run
Everything happens in a temporary folder that is deleted afterwards. The brain is redirected to that folder too
(`new SlimeBrainManager(brainRootOverride)`), so your real brain, Obsidian vault and `repo_map.json` are never touched.

## Not covered
The WPF window (`MainWindow`) and the AI calls. Those need the app itself.

## Adding to the solution (optional)
In `SlimeHelper.slnx` add: `<Project Path="../SlimeHelper.Checks/SlimeHelper.Checks.csproj" />`