# Cloud environment set-up (one time)

1. Go to https://claude.ai/code and sign in.
2. Under the message box, open the **environment selector** and choose **Add environment** (or edit **Default**).
3. Name it `ERP`. Keep network access at **Trusted** (it already allows NuGet, npm, Docker Hub and Microsoft package sites).
4. In **Setup script**, paste the contents of `scripts/cloud-setup.sh` from this repository. Save.
5. The first session in this environment takes a few extra minutes while .NET is installed; later sessions start
   with it already in place.

# Starting each piece of work

1. At https://claude.ai/code choose repository **isamad123/ERP**, environment **ERP**, permission mode **Accept edits**.
2. Pick the model named in the build plan for that slice (`/model` or the model menu): Opus 5.5 or Sonnet 5.5.
3. Type: `Do slice S1.01. Follow CLAUDE.md.` (change the slice number each time).
4. When it finishes, it pushes a branch, writes a handoff note, opens one pull request to `main` (slice file and
   build together) and turns on auto-merge, so GitHub merges it by itself once CI is green (CLAUDE.md, "Branches
   and merging"). Review the slice report. Start a **new session** for the next slice once the pull request shows
   **Merged**; it starts from the latest `main`. If it shows a red cross instead, ask a session to fix it.

# One-time GitHub settings for auto-merge (product owner)

Auto-merge only waits for CI when `main` has required checks; without them GitHub would merge before CI finishes.

1. On GitHub open **isamad123/ERP → Settings → General**, scroll to **Pull Requests**, tick **Allow auto-merge**
   (and, optionally, **Automatically delete head branches**).
2. Open **Settings → Branches** (or **Rules → Rulesets**) and add a rule for `main`: **Require a pull request before
   merging** (set required approvals to 0) and **Require status checks to pass**, adding the checks
   `changes`, `build-and-test` and `tenant-isolation`. Leave **Require branches to be up to date** off, so a pull
   request does not need a fresh CI run each time another slice merges first.
