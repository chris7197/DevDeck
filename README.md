# DevDeck
DevDeck - SOOMFON dev integration


## Project Developer Command Prompt

DevDeck includes a **Project Developer Command Prompt** action (`com.chris7197.devdeck.projectdevcmd`),
available as a standalone action. Assign it to a free slot in your existing `VisualStudio` profile.

With a project file active in Visual Studio, press **Dev Cmd**. DevDeck queries the
**selected Visual Studio instance** using DTE, resolves the active file's
`ProjectItem.ContainingProject` (also works for linked project items), and opens
an **external** Developer Command Prompt in the folder containing the owning
`.csproj`/other project file. It loads that specific Visual Studio installation's
`VsDevCmd.bat`, then explicitly changes to the project folder. It does **not**
open a shell in the solution root, nor launch Visual Studio's embedded Terminal panel.

Files opened outside a loaded project, Solution Items, and unsaved/new files with
no physical path cannot supply an owning project; the action reports this
instead of guessing. If several Visual Studio solutions are open, DevDeck's
usual solution picker selects the instance whose active editor will be used.

Install with `./scripts/Publish-Plugin.ps1 -Install` on Windows. Existing
installed `settings.json` is preserved by default: assign
`com.chris7197.devdeck.projectdevcmd` to a free VisualStudio slot, or add
the standalone action in Stream Dock. Avoid
`-ResetSettings` unless you intentionally want to replace all saved settings.
