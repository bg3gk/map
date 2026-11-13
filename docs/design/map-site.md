# The map site: three maps, one web, and its own API

**Status: built, not yet deployed.** `map build` renders it, `map serve` serves it; the engine is
`engine/src/GateKit.Site` and its tests `engine/tests/GateKit.Site.Tests`. `deploy/` puts it on a
server.

The map answers questions about the game - which dialogue says this line, what this stat does,
what a template is made of, where a timeline hides a weapon, what is at offset 4 of
`ecl::ActiveTurnComponent`, what calls `bg3.exe+0x77F580`. Two kinds of asker are served by one
site: a modder who wants to look something up, and an agent with no local copy.

The shape of the pages follows Norbyte's search site, `bg3.norbyte.dev`, and so, since
2026-11-13, does the way it is served: an application on a server of our own. It was first built
for GitHub Pages, and measured out of it - see "Why a server" below.

Like his, it carries `noindex, nofollow` on every answer, and `robots.txt` lets a crawler read
`llms.txt` and nothing else. Keeping the game's text out of the search engines is what makes
"withdrawn the same day it is asked for" true in practice: emptying a server takes a minute, and
getting a page out of someone else's index does not.

## The idea: the site is the API

Every object gets a URL, and the `.json` behind it is the only form a page is kept in.

```
/dialog/DEN_AttackOnDen_Sapper_003           a page a person reads
/dialog/DEN_AttackOnDen_Sapper_003.json      the file an agent fetches - and the page is drawn from it
```

The server answers every page's address with one shell, whose script fetches the `.json` and
draws the page. So the page a person reads and the data an agent fetches are not two renderings
kept in step by tests - they are one file. The scheme is documented in the site's own `llms.txt`
and is stable, so an agent builds the address from an id it already has instead of scraping.

Two questions are asked of the server rather than read from a page:

- `/api/search?q=` - every line of the game's text in every language, every object by its name,
  every page by its id, and every function by an address a decompiler shows.
- `/api/refs?value=` - which objects of the game name a thing in an attribute: the one question
  no page answers ahead of time, because a page lists what an object names, not who names it.

Both are limited per reader per minute; pages never are.

## What is on it: everything

Every object the index read out of the game's files has a page with every attribute the file
gives it (`/object/<key>`), and the richer pages of entities and templates lead to theirs. Every
file the index read lists its objects; every package lists every file inside it; every line of
text has a page with all fifteen languages, its gendered forms, the recordings that voice it and
where it is said; every name of the story scripts lists the scripts that use it. Dialogues show
their nodes, where each leads and their attributes; timelines show every track and every key.
Tables are not cut: the client draws a screenful and the rest on request.

On the engine's side, every function of every binary has a page: its address relative to the
binary (what a debugger takes) and absolute (what Ghidra and IDA show, at each binary's default
base), the label a decompiler gives it, the name it goes by and where the name comes from - the
binary's own export, Script Extender's symbol, or a match to the Toolkit's export - its calls
both ways, its strings, its data and its virtual tables, and its twin in the other build of the
game. Our own two names, a readable title and a note, have their place on it, empty until given.
Every system of the census and every global Script Extender reaches for has a page too.

What is never on it is the engine's code: names, addresses, sizes, layouts and calls are facts
about the code; decompiled output is the code, and is not ours. `map check` refuses a page that
carries any.

## What the work is for, measured

The home page says how far the work has got, the way `docs/design/what-this-is-for.md` in
GateKit measures it and with no single percentage: how much of what we wrote down still carries a
name the machine invented, where each name came from (the game's own, a Toolkit accessor's,
reasoned out of the code, unknown), how many component types were seen alive and verified against
raw memory, and how many of the game's functions go by a name. Each number leads to its page;
"Still owed a name" lists the components where a reader pays off most.

Our names go to a decompiler as one CSV per build of the game (`/export/names-bg3.csv`,
`/export/names-bg3_dx11.csv`), with scripts that apply them in Ghidra and in IDA.

## How it is kept

A page for each of 1.1 million objects, 2 million functions and half a million lines of text is
3.6 million small files: more than the inodes of a small server's disk, and an afternoon to copy.
So the rendered site is one SQLite file, `pages.sqlite`, with every file gzip-compressed under the
path it is served at - the form a browser takes it in, so a page is sent without being unpacked -
and the search is a second one, `search.sqlite`. `map build` writes both under other names and
renames them into place, so a running server goes from the old site to the new one between two
requests.

A line of text has its own page with every language, and search covers all of them, so there is
no language to pick. Every link to a line still carries all of its languages in the JSON, so an
agent resolves a line without a second request.
Lines of languages with spaces are indexed as words; Chinese, Japanese and Korean, which have
none, by every run of three characters, and a shorter search there reads the lines.

## Why a server

The site was built for GitHub Pages first: free, kept up by someone else, and taken down by
deleting a branch. On 2026-11-13 it was measured against the gigabyte a Pages site may be: the
site as it then was came to 582 MB, and everything not on it yet came to about 1,010 MB. Fitting
it would have meant hiding part of the data in packs only our client can open. The owner's rule:
if all of it fits, Pages; if it takes a compromise, a server and nothing hidden. It took a
compromise, so it is a server - see `what-we-publish.md` in GateKit.

The server costs money and attention. What it buys is no limit at all, real status codes, and
questions the pages cannot answer ahead of time. Withdrawal stays as easy as promised: the site is
two files on one machine and a set of release assets.

## Built from releases, on the server

The databases can only be built on a machine that owns the game. They are published as release
assets of this repository (`gatekit data export-release`, `gatekit symbols export-release`), and
`release.json` pins the release a checkout reads. A server fetches them (`map fetch`), every byte
checked, and builds the site itself: it needs no copy of the game. `deploy/install.sh` does all of
it on Debian or Ubuntu, behind Caddy; `deploy/Dockerfile` does it in a container.

## Settled, and what is left

- **How much of the game's text to show.** All of it; it goes the day it is asked for.
- **Patches.** The site says which build it describes, in its footer, in `llms.txt` and beside
  every address; a new patch is a new release and a rebuild.
- **Search engines.** Kept out; the owner's decision.
- **A failure lands on the die.** The d20 turns red and lands on a 1; underneath, the HTTP status
  or one of three codes of our own (unreachable, search, page data), "Try again" and "Close".
- **Not recorded yet:** whether a global is readable from Lua, and where a finding stands
  upstream. The pages say "not recorded yet" rather than guess.
