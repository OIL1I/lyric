# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M1 — Lexer**

Slices 1–4 sind durch. Nächster Slice: f-Strings mit Mode-Stack.

## Was schon erledigt ist

- [x] **M0 — Setup, Architekturrahmen, Test-Infra** (siehe `m0-complete`-Tag)
- [x] **M1-Slice 1** — Lexer-Skelett, Whitespace, Identifier, Brace-Punctuation
- [x] **M1-Slice 2** — Keywords, Doc-Comments, Block-Comments mit Nesting
- [x] **M1-Slice 3** — Int/Float-Literals (4 Basen, Suffixes, Float-
  Disambiguierung); Codes `LYR-LEX0003`/`0004`/`0006`
- [x] **M1-Slice 4** — String- und Char-Literals mit Escapes (`\n`,
  `\x##`, `\u{...}`-Range-Check); Codes `LYR-LEX0007`/`0008`/`0009`/`0010`

## Woran wir gerade arbeiten

**M1-Slice 5** — f-String-Sub-Lexer. Noch nicht begonnen, Plan steht aus.

Lieferposten (siehe `Sprache.md §1.5`):
- `f"..."` als Start-Token
- StringChunk-Tokens für die Plain-Text-Teile
- `{` / `}` als InterpStart/InterpEnd innerhalb von f-Strings
- Reguläre Token (Identifier, Operator, Literal) innerhalb von `{...}`
- Format-Spec nach `:` bis `}`
- Mode-Stack im Lexer: `Normal` ↔ `FStringText` ↔ `FStringInterp`
- Nested f-Strings: `f"a={f"b={x}"}"` muss tokenisierbar sein

## Was als nächstes ansteht

Nach Slice 5:
6. Operatoren mit Longest-Match (Slice 6)
7. CLI `lyric tokenize` + Golden-Test-Infrastruktur (Slice 7)

## Offene Fragen / Diskussions-Punkte

Für Slice 5 zu klären:
- Mode-Stack als `Stack<LexMode>` Field oder rekursive Sub-Lexer-Instanzen?
- Format-Spec als eigener Token-Typ oder als String-Chunk mit Marker?
- Wieviel Lexer-State (Brace-Depth innerhalb Interp) ist nötig für
  saubere `}`-Disambiguierung (Block-Close vs Interp-Close)?
- Soll der Lexer zwischen `f"…"` und einer Sequenz `Identifier(f) "…"`
  schon im Identifier-Pfad disambiguieren (Lookahead nach `f`),
  oder dispatcht `Next()` zuerst auf `"` und Sub-Macht hat ein
  Prefix-Flag?

## Letzter relevanter Commit

`M1: lex string and character literals with escape sequences`

---

## Wie diese Datei zu pflegen ist

- Nach jedem Slice: `## Was schon erledigt ist` ergänzen, `## Woran wir
  gerade arbeiten` updaten, ggf. `## Was als nächstes ansteht` neu sortieren.
- Bei Meilenstein-Wechsel: oben den neuen Meilenstein eintragen, alte
  abgehakte Items aus `## Was schon erledigt ist` ausdünnen (nur die
  letzten 2–3 Slices behalten, Rest ist in `git log`).
- Bei Block-/Diskussions-Bedarf: `## Offene Fragen` füllen — Claude
  greift das in der nächsten Session auf.
- **Niemals** hier neue Features planen. Das ist `ROADMAP.md`-Territorium.
