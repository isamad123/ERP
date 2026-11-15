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
4. When it finishes, it pushes a branch and writes a handoff note. Review the slice report, then create the pull
   request from the session (or ask it to). Start a **new session** for the next slice.
