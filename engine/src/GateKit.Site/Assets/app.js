// The whole client: a search box, and then links. No framework, no build step.
//
// Every page of the site is drawn here. The server answers every page's address with this shell,
// and this script fetches the page's .json and draws it - which is why a person and an agent
// cannot be shown two different things. Searching is one question to /api/search.
(function () {
  "use strict";

  var base = window.SITE_BASE || "/";
  var main = document.getElementById("main");
  var box = document.getElementById("q");
  var foot = document.getElementById("foot");
  var notices = window.SITE_NOTICES || {};
  var kinds = window.SITE_KINDS || [];
  // Every drawing of the page takes a number; an answer that arrives after a newer drawing has
  // begun is dropped, so a slow search can never overwrite a later one.
  var turn = 0;
  var veil = document.getElementById("wait");
  var waiting = null;
  var scrollUp = false;
  var plainTitle = document.title;
  // The address of what is on screen, so that closing a failure can put it back in the bar.
  var shown = location.pathname + location.search;
  var whose = document.getElementById("whose");
  var plainWhose = whose.innerHTML;

  // Nothing changes for the first 300 ms of a load - most loads end sooner, and a flash of a
  // spinner is worse than nothing - and then the page dims under a rolling d20. The old page stays
  // where it is underneath until the new one replaces it.
  function wait(on) {
    clearTimeout(waiting);
    if (on) {
      clearTimeout(settling);
      settle();
      waiting = setTimeout(function () { veil.classList.add("on"); document.body.setAttribute("aria-busy", "true"); }, 300);
    } else {
      veil.classList.remove("on");
      document.body.removeAttribute("aria-busy");
    }
  }

  // A failure is a number on a red die: the HTTP status where a server answered with one, and
  // one of these where none did. llms.txt carries the same list, so a number means the same thing
  // to whoever reads it, a person or an agent.
  var FAILED = {
    1: "the site could not be reached: the connection or the server is down",
    2: "the search could not be answered",
    4: "the data of this page could not be read"
  };
  var num = document.getElementById("d20n");
  var why = document.getElementById("why");
  var whyText = document.getElementById("whyText");

  function failure(code, detail) {
    var e = new Error(detail || FAILED[code] || "");
    e.code = code;
    return e;
  }

  // An error that does not yet say what kind it is gets the kind of the step it came from.
  function classify(e, fallback) {
    return e && e.code ? e : failure(fallback, e && e.message ? e.message : String(e));
  }

  // The die back to a yellow 20, for the next wait.
  function settle() {
    veil.classList.remove("bad");
    veil.setAttribute("aria-label", "loading");
    why.hidden = true;
    num.textContent = "20";
  }

  // Writes the page, if this drawing is still the latest one; the frame (title, footer) goes back
  // to the site's own unless the page names its own.
  function paint(mine, html, footer, title) {
    if (mine !== turn) return false;
    wait(false);
    main.innerHTML = html;
    whose.innerHTML = footer || plainWhose;
    document.title = title || plainTitle;
    if (scrollUp) { window.scrollTo(0, 0); scrollUp = false; }
    shown = location.pathname + location.search;
    return true;
  }

  function esc(s) {
    return String(s === null || s === undefined ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }

  // The site's own routes are two segments; anything else is an address we do not serve. The
  // path is used as the browser has it, not decoded: a slug is the name of a file, written with
  // `!` and two hex digits where an id has a character no file name could hold.
  function route() {
    var path = location.pathname;
    return path.indexOf(base) === 0 ? path.slice(base.length) : path.replace(/^\//, "");
  }

  // The id a slug was made from: each run of `!xx` read back as UTF-8.
  function unslug(slug) {
    return slug.replace(/(?:![0-9A-Fa-f]{2})+/g, function (run) {
      try { return decodeURIComponent(run.replace(/!/g, "%")); } catch (e) { return run; }
    });
  }

  // One question to the server; a status it answers with is the number on the die.
  function ask(text) {
    var url = base + "api/search?q=" + encodeURIComponent(text);
    return fetch(url).catch(function () { throw failure(1); }).then(function (r) {
      if (r.status === 429) throw failure(429, "too many searches from here in one minute; wait a moment");
      if (!r.ok) throw failure(r.status, "the search answered HTTP " + r.status);
      return r.json().catch(function () { throw failure(2); });
    });
  }

  var UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
  var HANDLE = /^h[0-9a-f]{8}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{4}g[0-9a-f]{12}$/i;

  // One rule for what is a link: only the chip - the identifier in its box - is one. A row of
  // results, a line of text, a note beside a chip are read, not clicked.
  function chip(href, text) {
    return '<a class="id" href="' + esc(base + href) + '">' + esc(text) + "</a>";
  }

  // One row of search results: a chip, and what it is.
  function hit(chipHtml, rest) {
    return '<div class="hit">' + chipHtml + (rest || "") + "</div>";
  }

  function group(title, html, count) {
    if (!html) return "";
    return '<div class="group"><h2>' + esc(title) + (count ? " (" + count + ")" : "") + "</h2>" + html + "</div>";
  }

  function jump(route) {
    history.replaceState(null, "", base + route);
    scrollUp = true;
    go();
  }

  // `lead` is drawn above the results: a missing address keeps saying it is missing while the
  // search for what it might have meant runs below it.
  function search(text, lead) {
    var mine = turn;
    lead = lead || "";
    if (!text.trim()) { home(); return; }
    ask(text).then(function (r) {
      // An id that names exactly one page goes there. It takes the search's place in the history
      // rather than adding to it: otherwise Back returns to the search, which jumps forward again,
      // and the reader can never leave.
      if (mine !== turn) return;
      if (r.exact) { jump(r.exact); return; }

      // A hit is grouped by the map it belongs to, because the three answer different questions.
      var maps = [
        { title: "The game's own content", kinds: ["entity", "dialog", "timeline", "template", "stat", "level", "object", "source", "package", "osiris"], html: "" },
        { title: "Our map of the engine's components", kinds: ["component"], html: "" },
        { title: "The engine's own symbols", kinds: ["symbol", "enum", "function"], html: "" }
      ];
      r.pages.forEach(function (o) {
        var row = hit(chip(o.route, o.name),
          '<span class="nt">' + esc(o.kind) + (o.note ? " · " + esc(o.note) : "") + "</span>");
        for (var i = 0; i < maps.length; i++) {
          if (maps[i].kinds.indexOf(o.kind) >= 0) { maps[i].html += row; return; }
        }
        maps[0].html += row;
      });
      var pages = maps.map(function (m) { return group(m.title, m.html); }).join("");
      var lines = r.lines.map(function (o) {
        return hit(chip("loca/" + o.handle, o.lang), '<span class="tx">' + esc_snippet(o.text) + "</span>");
      }).join("");
      var names = r.names.map(function (o) {
        var where = '<span class="nt">' + esc(o.node) + (o.key ? " · " + esc(o.key) : "") + "</span>";
        return hit(o.route ? chip(o.route, o.name) : '<code class="id">' + esc(o.name) + "</code>", where);
      }).join("");
      paint(mine, lead + (pages || lines || names
        ? pages
          + group("Lines of the game's text", lines)
          + group("Other objects of the game, most of which have no page here", names)
        : '<p class="state">nothing matches ' + esc(text) + ".</p>"));
    }).catch(function (e) { fail(classify(e, 2), mine); });
  }

  // The snippet already carries <em> around what matched; everything else is escaped.
  function esc_snippet(s) {
    return game(esc(s).replace(/&lt;em&gt;/g, "<em>").replace(/&lt;\/em&gt;/g, "</em>"));
  }

  // The game's own text carries a little markup of its own: line breaks, italics and bold, which
  // are drawn as they are meant, and the tags that tell the game which tooltip to show
  // (<LSTag ...>), which are dropped around the words they wrap. It works on text already
  // escaped, so nothing else in a line can become markup. The JSON keeps the text as the game has it.
  function game(html) {
    return html
      .replace(/&lt;br\s*\/?&gt;/gi, "<br>")
      .replace(/&lt;(\/?)(i|b)&gt;/gi, function (_, close, tag) { return "<" + close + tag.toLowerCase() + ">"; })
      .replace(/&lt;\/?(?:LSTag|hl)\b.*?&gt;/g, "");
  }

  // One value of a page, drawn the way the JSON says it: a bare string is prose, a number is a
  // number, null is nothing, and an object names something - a link when it has a route.
  function value(v) {
    if (v === null || v === undefined) return '<i class="z">none</i>';
    if (typeof v === "number") return String(v);
    // Prose is wrapped so that, inside a table that takes the width it needs, a long sentence
    // still breaks at a readable width instead of stretching the table.
    if (typeof v === "string") return v.length > 40 ? '<span class="tx">' + game(esc(v)) + "</span>" : game(esc(v));
    var html;
    if (v.route !== undefined) {
      var shown = v.text !== undefined ? v.text : unslug(v.route.slice(v.route.indexOf("/") + 1));
      // A line of the game's text that is what the table is about reads as prose, and the chip
      // beside it is the way to its page - the prose itself is not a link.
      html = v.route.indexOf("loca/") === 0 && v.text !== undefined
        ? '<span class="tx">' + game(esc(shown)) + "</span> " + chip(v.route, "line")
        : chip(v.route, shown);
    } else if (v.id !== undefined) {
      html = '<code class="id">' + esc(v.id) + "</code>";
    } else if (v.path !== undefined) {
      html = '<code class="pt">' + esc(v.path) + "</code>";
    } else {
      html = esc(v.text);
    }
    if (v.note) html += '<span class="nt">' + esc(v.note) + "</span>";
    return html;
  }

  function page(path) {
    var mine = turn;
    fetch(base + path + ".json").catch(function () { throw failure(1); }).then(function (r) {
      // No such page is not a failure: the reader is told so, and shown what the search finds.
      if (r.status === 404) { missing(path); return null; }
      if (!r.ok) throw failure(r.status, "the page answered HTTP " + r.status);
      return r.json().catch(function () { throw failure(4); });
    }).then(function (doc) {
      if (!doc || mine !== turn) return;
      var title = doc.title + " · " + doc.kind + " · GateKit Map";
      var html = "<h1>" + esc(doc.title) + '</h1><p class="sub">' + esc(doc.subtitle || doc.kind)
        + ' · <a href="' + esc(base + doc.route + ".json") + '">as JSON</a></p><dl>';
      var hints = doc.hints || {};
      Object.keys(doc.fields).forEach(function (name) {
        html += "<dt" + tip(hints[name]) + ">" + esc(name) + "<dd>" + value(doc.fields[name]);
      });
      html += "</dl>";
      tables = [];
      doc.sections.forEach(function (section) {
        // A table scrolls inside its own box: a wide one must never widen the page. A long one
        // shows a screenful and the rest on request: every row is in the JSON already, so asking
        // for them costs no request, only the drawing.
        var first = section.rows.slice(0, SCREENFUL);
        html += "<section><h2>" + esc(section.title) + '</h2><div class="tw"><table><thead><tr>'
          + section.columns.map(function (c) { return "<th" + tip(hints[c]) + ">" + esc(c); }).join("")
          + "</tr></thead><tbody>" + rows(first) + "</tbody></table></div>";
        if (section.rows.length > first.length) {
          html += '<p class="cut"><button type="button" class="more" data-table="' + tables.length + '">Show all '
            + section.rows.length.toLocaleString("en") + " rows</button></p>";
          tables.push(section.rows);
        }
        if (section.total > section.shown) {
          html += '<p class="cut">the first ' + section.shown + " of " + section.total
            + " rows; the rest are in the databases <code class=\"pt\">map fetch</code> downloads</p>";
        }
        html += "</section>";
      });
      // Whose material this is decides the last two sentences: Larian's text under the promise,
      // or what we measured, which is ours. The builder wrote both into the shell.
      // Who names this object is the one question no page answers ahead of time; the server
      // asks the full index when a reader wants to know.
      if (doc.index && doc.index.origin === "game") {
        html += '<section class="refs"><p class="cut"><button type="button" class="more" data-refs="'
          + esc(doc.id) + '">What else names this</button></p></section>';
      }
      var notice = notices[doc.index && doc.index.origin] || notices.game || {};
      html += '<p class="raw">' + (notice.missing || "") + "</p>";
      paint(mine, html, notice.whose, title);
    }).catch(function (e) { fail(classify(e, 4), mine); });
  }

  // How many rows of a table are drawn before the rest wait for a click.
  var SCREENFUL = 200;
  // The rows of the long tables of the page on screen, by the number their button carries.
  var tables = [];

  function rows(list) {
    return list.map(function (row) {
      return "<tr>" + row.map(function (cell) { return "<td>" + value(cell); }).join("");
    }).join("");
  }

  document.addEventListener("click", function (e) {
    var asked = e.target.closest ? e.target.closest("button[data-refs]") : null;
    if (asked) { refs(asked); return; }
    var button = e.target.closest ? e.target.closest("button.more") : null;
    if (!button) return;
    var section = button.closest("section");
    section.querySelector("tbody").innerHTML = rows(tables[Number(button.getAttribute("data-table"))] || []);
    button.parentNode.remove();
  });

  // Every object of the game that names this one in an attribute, from the server.
  function refs(button) {
    var mine = turn;
    var box = button.closest("section");
    button.disabled = true;
    fetch(base + "api/refs?value=" + encodeURIComponent(button.getAttribute("data-refs")))
      .catch(function () { throw failure(1); })
      .then(function (r) {
        if (r.status === 429) throw failure(429, "too many questions from here in one minute; wait a moment");
        if (!r.ok) throw failure(r.status, "the question answered HTTP " + r.status);
        return r.json();
      })
      .then(function (answer) {
        if (mine !== turn) return;
        var list = answer.objects;
        box.innerHTML = "<h2>Named by" + (list.length ? " (" + list.length + (list.length >= answer.most ? "+" : "") + ")" : "") + "</h2>"
          + (list.length
            ? '<div class="tw"><table><thead><tr><th>object<th>node<th>attribute</tr></thead><tbody>'
              + list.map(function (o) {
                return "<tr><td>" + chip(o.route, o.name || o.route.slice(7)) + "<td>" + esc(o.node) + "<td>" + esc(o.attribute);
              }).join("") + "</tbody></table></div>"
            : '<p class="state">No other object names it in an attribute.</p>');
      })
      .catch(function (e) { button.disabled = false; fail(classify(e, 2), mine, function () { refs(button); }); });
  }

  function tip(text) {
    return text ? ' title="' + esc(text) + '"' : "";
  }

  // Something failed. The page underneath stays as it was; the die turns red and shows the code,
  // with what it means and a way to try again. A failure of a drawing that is no longer the
  // latest is not the reader's concern and is dropped.
  // What "Try again" does: draw the page again, unless the failure says what else to repeat.
  var retry = null;
  // The die goes back to a yellow 20 only once it has faded out; a new failure or a new load in
  // the meantime cancels that, or it would paint a fresh failure yellow and leave it spinning.
  var settling = null;

  function fail(e, mine, again) {
    if (mine !== undefined && mine !== turn) return;
    retry = again || null;
    clearTimeout(settling);
    var code = e && e.code ? e.code : 1;
    clearTimeout(waiting);
    // Whatever failed, the die lands on a 1 - a critical failure, as the game itself would roll
    // it. Which failure it was is said underneath, with its number.
    num.textContent = "1";
    // One of our own codes says what it means, what exactly it was where that is known, and its
    // number; an HTTP status says who answered it, which already carries the number.
    var detail = e && e.message ? e.message : "";
    var said = FAILED[code]
      ? FAILED[code] + (detail && detail !== FAILED[code] ? " (" + detail + ")" : "") + ". Error " + code + "."
      : (detail || "the site answered HTTP " + code) + ".";
    whyText.textContent = said.charAt(0).toUpperCase() + said.slice(1);
    why.hidden = false;
    veil.classList.add("bad");
    veil.classList.add("on");
    veil.setAttribute("aria-label", "failed, code " + code);
    document.body.removeAttribute("aria-busy");
    if (!main.innerHTML.trim()) {
      main.innerHTML = '<p class="state">This page could not be loaded (code ' + esc(code) + ").</p>";
    }
  }

  // Closing a failure leaves the page that is on screen, so the address goes back to that page;
  // otherwise the bar would name the page that never arrived.
  function dismiss() {
    if (location.pathname + location.search !== shown) history.replaceState(null, "", shown);
    veil.classList.remove("on");
    settling = setTimeout(settle, 220);
  }

  function home() {
    paint(turn, document.getElementById("home").innerHTML);
  }

  function missing(path) {
    var id = path.indexOf("/") > 0 ? path.slice(path.indexOf("/") + 1) : path;
    var lead = "<h1>No page at " + esc("/" + path) + "</h1>"
      + '<p class="sub">The addresses this site serves are listed on <a href="' + esc(base) + 'llms.txt">llms.txt</a>.'
      + " A C++ name is written with a dot where it has <code class=\"id\">::</code>."
      + " Below is what the search finds for it.</p>";
    if (id) { box.value = unslug(id); search(unslug(id), lead); } else { paint(turn, lead); }
  }

  function go() {
    turn++;
    wait(true);
    var path = route().replace(/\/$/, "");
    var q = new URLSearchParams(location.search).get("q");
    var cut = path.indexOf("/");
    if (cut > 0 && cut < path.length - 1 && kinds.indexOf(path.slice(0, cut)) >= 0) {
      page(path);
      return;
    }
    if (path === "" || path === "index.html" || path === "404.html") {
      if (q) { box.value = q; search(q); } else { home(); }
      return;
    }
    missing(path);
  }

  document.getElementById("form").addEventListener("submit", function (e) {
    e.preventDefault();
    var q = box.value.trim();
    if (!q) return;
    history.pushState(null, "", base + "?q=" + encodeURIComponent(q));
    scrollUp = true;
    go();
  });


  // A link inside the site is followed without leaving the page, so the page a reader is on stays
  // until the next one is ready, instead of the browser blanking it the moment the link is
  // clicked. Anything that is a file rather than a page - the JSON, llms.txt, the API - and any
  // click meant for a new tab is left to the browser.
  document.addEventListener("click", function (e) {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    var a = e.target.closest ? e.target.closest("a[href]") : null;
    if (!a || a.target || a.hasAttribute("download")) return;
    var url = new URL(a.href, location.href);
    if (url.origin !== location.origin || url.pathname.indexOf(base) !== 0) return;
    if (/\.(json|txt|csv|py|css|js|png|jpg)$/i.test(url.pathname) || url.pathname.indexOf(base + "api/") === 0) return;
    e.preventDefault();
    if (url.pathname + url.search === location.pathname + location.search) return;
    history.pushState(null, "", url.pathname + url.search + url.hash);
    scrollUp = true;
    go();
  });
  window.addEventListener("popstate", go);
  document.getElementById("again").addEventListener("click", function () {
    var repeat = retry || go;
    retry = null;
    veil.classList.remove("on");
    repeat();
  });
  document.getElementById("dismiss").addEventListener("click", dismiss);
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && veil.classList.contains("bad")) dismiss();
  });

  go();
})();
