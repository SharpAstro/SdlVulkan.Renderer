# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. `CLAUDE.md` holds the architecture and conventions; this page holds only what a flight
needs to plan, build, check and land.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR or merge; the controller lands the flight. Leave nothing uncommitted, and put scratch output
  outside the plane.
- Sibling clones are not yours. `DIR.Lib` source sits beside this repo, not inside a plane, so a plane restores the
  `DIR.Lib` package (a floating pin, `11.57.*`, in `src/Directory.Packages.props`), as CI does. A change that needs `DIR.Lib` to change is its own flight in that repo: name it
  and leave it.
- No display or window is reachable from a plane. The tests render through a headless offscreen Vulkan context and
  `Assert.Skip` when no Vulkan ICD is present, so a skipped test proves nothing about rendering. The tools under
  `tools/` that need a panel (`HdrProbe`, `TransparencyProbe`) are the controller's to run, not a step.
- Never wait with a job that sleeps or polls: use `pilot leg wait --for-file <path>` and end the turn. Long builds
  and test runs go through `pilot run -- <command>`. The machine is shared, so a job may wait its turn first.

## Build and check

```bash
dotnet build -c Release
dotnet test src/SdlVulkan.Renderer.Tests -c Release
```

- `dotnet build -c Release` also builds `net10.0-android` (the library's target list when no `RuntimeIdentifier` is set),
  so it needs the android workload. CI installs it first (`dotnet workload install android`); a machine without it fails
  the build there, not in this change.
- Tests are xunit.v3 with Shouldly; code targets `net10.0`. No linter is configured.
- Package versions for `src/` live in `src/Directory.Packages.props`, never in a `.csproj` there. The tools under
  `tools/` sit outside it and carry their own versions in their `.csproj` files.
- A release bump is one line, `VersionMajorMinor` in `src/Directory.Build.props`, with its note in `CHANGELOG.md`.
  Do not hand-edit anything derived from it.
- CI also runs a Linux WebView smoke and a Windows job (non-gating); a change to the WebView packages is checked by
  `tools/WebViewSmoke` there, not from a plane.

## How a flight merges

- main has no merge queue, but its ruleset requires **linear history**, so a PR lands by squash or rebase, never as a
  merge commit. Take main by a merge (`git fetch origin`, `git merge origin/main`), never a rebase, and never push with
  `--force`. A merge that resolved a conflict cannot be rebased onto main; say so in the leg's report, so the PR
  lands squashed.
- Keep a flight's commits few and each one whole.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages.
