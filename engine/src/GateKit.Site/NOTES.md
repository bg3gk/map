# What the code does not say for itself

The design and the numbers are in [docs/design/map-site.md](../../docs/design/map-site.md); the
command is in [docs/contracts/cli.md](../../docs/contracts/cli.md), section 14.5. This page is the
part a person changing the code needs and would otherwise find out by breaking it. It began as
the hand-over of the first half of the work and was rewritten when the site was finished.

## A page is its JSON

Nothing writes an object's HTML. `JsonWriter` writes `<kind>/<slug>.json`; the address
`/<kind>/<slug>` has no file, so Pages serves `404.html` — the shell — and `Assets/app.js` fetches
the `.json` and draws it (`page()` and `value()`). Anything a page shows must therefore be in the
JSON, and anything the frame of a page needs comes into the shell from the builder:
`SITE_KINDS` (which first segments are pages) and `SITE_NOTICES` (the two closing sentences,
picked by the page's `index.origin`). The JSON leaves a link's `text` out when the last segment of
its route reads back as it; `Routes.Unslug` and `unslug()` in the client are the same rule and must
stay so.

A slug escapes with `!xx`, not `%xx`. Every server decodes `%27` in a request path before it looks
for the file, so a file named with `%27` cannot be fetched at all — which is how fifteen stats
with an apostrophe or a space in their names were unreachable until the pages started being
fetched by address.

## Languages, names and the drawing order

- **Packs.** `LanguagePacks` writes `loca/handles.txt.gz` and one `loca/<language>.json.gz` per
  language, aligned by position. The client keeps them in a `Map` from handle to text once a
  reader picks a language. Only English goes into `search.sqlite`.
- **A handle is a link only if some language has text for it** (`SiteIndex._spoken`). The game
  names handles with no line anywhere, its own placeholder
  `ls::TranslatedStringRepository::s_HandleUnknown` among them.
- **A dialogue's line is the link** (`SiteIndex.Line`): its text is shown, its handle is the
  address, and the chosen language is drawn under it. The client draws such a link as prose
  (`a.ln`), not as an identifier.
- **Short names.** A column or field is written `"name|explanation"`; `Hints` splits it, the JSON
  carries the explanations in `hints`, and the client puts them in `title`. `Hints.MaxName` (14)
  is held by a test over every kind.
- **Only the latest drawing writes the page.** Every call of `go()` takes a number (`turn`), and an
  answer that arrives after a newer drawing began is dropped. Without it, the English-only search
  that starts at load could finish after the one that runs once a language pack has arrived, and
  overwrite it.

## Following a link, and waiting

- Internal links are followed in the page (`pushState` + `go()`); `popstate` draws the address the
  reader went back to. Files - `.json`, the packs, `llms.txt` - and clicks meant for a new tab are
  left to the browser.
- **Nothing writes `main` but `paint()`**, which first checks the drawing is still the latest
  (`turn`), then hides the veil and restores the site's own title and footer unless the page
  names its own. There are no "loading…" placeholders: the old page stays until the new one is
  ready, and `wait(true)` shows the d20 only if that takes 300 ms.
- `site serve --latency MS` delays every answer; requests are handled side by side, or the
  database's range requests would queue behind each other's delay.

## Checking the look

`gatekit site demo --out DIR --serve` (`Demo/DemoSite.cs`) renders the data of `Demo/` - the same
data the tests render - in about a second, and prints one address of every kind. Use it before a
full render; the full one is for numbers and the link check.

## When something fails

`fail(e, mine, again)` in `app.js` lands the die red on a 1 and writes the reason under it: the
HTTP status where a server answered with one, otherwise one of the codes in `FAILED`, which a
test holds equal to the table in `llms.txt`. `again` is what "Try again" repeats (a language
pack, say); without it the page is drawn again. Close puts the address of the page on screen
back in the bar. The cached database worker and the handles list drop a failed promise, or a
retry would retry nothing; a pending reset of the die is cancelled by a new failure or load.

## Releasing

`SitePublisher` makes the commit with the git command line, in `<site>.publish`, with the site as
its work tree - nothing is copied - and from nothing every time, which is what keeps it an orphan.
It runs `SiteCheck` first and refuses a site rendered for another base than the remote serves
(`BaseOf`: `/map/` for `bg3gk/map`). `PublishTests` pushes to a bare repository in a temporary
folder and checks there is one commit, with every file, and still one after publishing again.

The search database is cut into parts by `SearchDatabase.Split` after it is built; the reader is
sql.js-httpvfs in its `chunked` mode, which never reads across two parts, so a part must be a whole
number of pages. The shell is written last, because it carries the length of the whole.

## Three sources, one book of addresses

`SiteSources` holds the three maps — `SiteIndex` (the game's content), `SymbolIndex` (the
engine's binaries) and `ComponentMap` (`research/coverage/`). Each registers the ids of its own
kinds in the shared `SlugBook` when it is opened, **before any page is rendered**, so a page of
one map can ask whether an object of another has an address. A link into a map that is not open
degrades to a plain identifier, never to a broken address; that is what lets a reader with only
the index still build a site.

Adding a kind means a class implementing `IPageKind`, an entry in `PageKinds.All` and one in
`Routes.Kinds` (a test asserts the two agree), and a `Register` call in whichever source owns it.

## `Grouper` is load-bearing

A page's child rows are not fetched per page — the index has 3.2 M track rows and no index on
`dialog_lines(dialog_id)`, so a query per page would take hours. Each child table is read once,
ordered by its parent key, and handed out group by group as the main cursor walks the same order.
That is why every main query of the game's kinds ends in `ORDER BY id` or `ORDER BY uuid` and
every child query is ordered by the same column. **Break that ordering and pages silently lose
their tables** — no error, just empty sections. SQLite's BINARY collation is what
`StringComparer.Ordinal` does, which is why the text groupers are correct; every uuid in the index
is lowercase, so the join never misses on case.

The symbol and enum kinds do not use it: every table they read has an index on the column they
ask by, and 14,000 indexed queries take seconds.

## The search database

`search.sqlite` is read by the browser over HTTP range requests through
[sql.js-httpvfs](https://github.com/phiresky/sql.js-httpvfs) (vendored in `Assets/vendor/`,
Apache-2.0, declared in `NOTICE`). None of what follows is obvious, and each of it was learned by
something going wrong:

- **The page size must match the client's chunk size.** `SearchDatabase.PageSize` is written into
  the file with `PRAGMA page_size` *before any table exists*, and into the client as
  `window.SITE_CHUNK`. Change one and you must change the other, or every query fetches torn
  pages. `VACUUM` at the end is not tidiness: it makes the pages contiguous so one logical read is
  one range request.
- **The reader asks for the size with HEAD first.** A server that answers HEAD with a body is in
  error, and `site serve` did exactly that and died on the first search anyone tried. GitHub
  Pages gets it right; `SiteServer` now does too, and a test holds it.
- **FTS5 is external-content.** `obj_fts`, `loca_fts` and `gname_fts` have
  `content='<table>', content_rowid='id'` and are filled by `INSERT INTO x_fts(x_fts)
  VALUES('rebuild')` after the bulk insert. That halves the file and is what makes `snippet()`
  work. It also means **a content table needs a rowid**: `loca` cannot be made `WITHOUT ROWID`
  to save its handle index, however tempting the twenty megabytes look. `loca_use` is not a
  content table, and is.
- **`gname_fts` is built with `detail = none`**, which drops positions and refuses phrase
  queries. The client therefore joins words with `AND`, never a space, and splits on `_` as the
  tokenizer does: a quoted `"S_HAG_WoodWoad_001"` is a phrase of four tokens and fails outright.
  If a search ever comes back with *phrase queries are not supported*, that is where to look.
- **The default tokenizer is deliberate.** The game index's own `texts_fts` uses
  `tokenchars '_'`, which makes `DEN_AttackOnDen_Sapper_003` a single token, and then searching
  for `sapper` finds nothing. Here `_` splits, and the `words` column of `obj` also splits at
  every change of case, so "turn" finds `ecl::ActiveTurnComponent`. Exact ids are handled
  separately by `obj.ident`, which is indexed and holds the dotted form of a C++ name.

## The thread between the maps

`ComponentMap.Parse` is the one place that reads a sighting label of
`research/coverage/seen/index.json`. The shape is `<situation>:<what was being looked at>`:

- `dialog:` and `tree1:`/`tree2:`/`tree3:` carry a dialogue's short name with **the uuid of the
  instance that played it** appended — not the dialogue's own uuid. Strip it and match the leaf.
- every other prefix carries the name a level gives an object, which matches `entities.name`.
  9,090 names are shared by more than one placed object; the link goes to the first and says how
  many there are, and the reverse thread is kept on all of them.
- a label with no colon is a situation that named nothing (`idle`, `fight`).

If the capture ever changes its labels, this method and the design change with it.

`coverage.json` lists **every component twice**, once from each side's point of view, so it
cannot say which side a component belongs to; the engine's own namespace does (`ecl` client,
`esv` server, the rest shared). `ComponentKind.Side` is that rule.

## What the data turned out to be like

- Dialogue names are filed in folders — `Camp/CAMP_SleepCutscene` — but the leaf alone is unique
  across all 9,376 non-shadowed dialogues, even ignoring case, which is why the URL is the leaf.
  Re-check after a patch; `Routes.SlugsFor` already disambiguates with `~2` if it ever stops.
- Stat names are not unique: 454 are defined more than once and 146 collide only by case, which
  on Windows and macOS is one folder. Fifteen contain characters outside `[A-Za-z0-9._-]`.
- 567 timelines have no uuid and so no address.
- 17,336 of the 31,649 type names in the symbol database are template instantiations, the
  longest 18,108 characters; `Routes.IsPlainTypeName` keeps them off the site. `type_names` writes
  its names as a declaration does — `struct ecs::TypeIdContext` — and `SymbolIndex.Plain` strips
  the keyword. `se_types.namespace` is often empty where `engine_class` is qualified; the engine's
  name wins (`SymbolIndex.SeTypeName`).
- The index's `meta.game` is the folder it was read from and names a machine; it is never shown.
- **Localization handles have no `.json` twin of their own.** 469 k exist; a file each would cost
  more than the rest of the site. `/loca/<handle>` is rendered in the browser from the search
  database, English is published in bulk as `/loca/English/<xx>.json`, and a handle on any page
  already carries its English text in the value's `note`.
