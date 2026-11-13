# Working in this repository

This is the engine of the GateKit map site, and the home of its releases. Read `README.md` first,
then `docs/design/map-site.md`.

1. **Nothing of the game is ever committed.** No database, no rendered site, no text, no file of
   Larian's. They travel as release assets and are built on a server. `.gitignore` refuses them.
2. **No code of the engine on any page.** Names, addresses, sizes, layouts and calls are facts
   about it; decompiled output is not ours. `map check` enforces it; do not work around it.
3. **Everything in English** - code, comments, docs, commit messages.
4. **Warnings are errors**, and `dotnet test` stays green. A change to what a page shows comes with
   a test on the dummy site (`engine/src/GateKit.Site/Demo`), never on a machine's database.
5. **The query code is GateKit's.** The `gatekit` submodule is pinned; change GateKit there, then
   move the pin.
6. Commits end with
   `Co-Authored-By: GateKit Thrall <356219887+gatekit-thrall@users.noreply.github.com>` and never
   name an AI assistant as an author.

| To work on | Look in |
|---|---|
| what a kind of page shows | `engine/src/GateKit.Site/*Kind*.cs`, `MapKinds.cs`, `PageKinds.cs` |
| the browser client | `engine/src/GateKit.Site/Assets/` |
| search and what-names-this | `SearchDatabase.cs`, `SiteSearch.cs`, `SiteRefs.cs` |
| the server | `SiteServer.cs`; `deploy/` for the machine around it |
| the measures of the work | `AimKinds.cs` |
