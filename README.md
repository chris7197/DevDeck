# DevDeck
DevDeck - SOOMFON dev integration

## PerfMon Data Collector Set toggle

The `Toggle PerfMon Logging` Stream Dock action controls **an existing** Windows
Performance Monitor Data Collector Set. It never creates or changes counters.

1. In a normal Windows terminal run `logman query` to find your Data Collector Set
   (e.g. `Developer Diagnostics`). It must already exist under Performance Monitor
   > Data Collector Sets > User Defined (or another accessible collector set).
2. Add **Toggle PerfMon Logging** from the DevDeck action category to your controller.
   In its property inspector, enter the exact **PerfMon Data Collector Set name**.
   Alternatively set `"PerfMonCollectorName": "Developer Diagnostics"` in the
   installed DevDeck `settings.json` for a default shared by all PerfMon keys.
3. The key reads Windows status on each normal DevDeck status refresh: `START`
   means stopped, red `RECORDING` means running. Press to toggle.
4. If you use profile slots, set the slot's action ID in `Profiles` to
   `com.chris7197.devdeck.perfmon.toggle`; optionally use
   `ButtonPresets` (e.g. `"VisualStudio/15": {"PerfMonCollectorName":"Developer Diagnostics"}`).

DevDeck uses `logman query`, `logman start <name>` and `logman stop <name>`.
The collector must be accessible by the Windows user running the Stream Dock
plugin; some collectors require administrator rights. DevDeck will report the
error rather than silently running elevated. Unknown or missing collector
states are never toggled. Existing log output and retention are controlled by
PerfMon's own Data Collector Set configuration, not DevDeck.

With the existing installer, use `scripts/Publish-Plugin.ps1 -Install` to install
new plugin files without resetting your existing settings. Configure the key
in Stream Dock's property inspector after installation.
