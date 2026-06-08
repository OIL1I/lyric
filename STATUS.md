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

Slice 1 (Skeleton + Whitespace + Identifier + Brace-Punctuation) und
Slice 2 (Keywords + Doc/Block-Comments) sind durch. Nächster Slice:
Numerische Literals.

## Was schon erledigt ist

- [x] **M0 — Setup, Architekturrahmen, Test-Infra** (siehe `m0-complete`-Tag)
- [x] **M1-Slice 1** — Lexer-Skelett: Cursor mit Sentinel-`\0`, SkipTrivia
  (Whitespace + Line-Comments), Identifier, `()`/`{}`, Bad-Char mit
  `LYR-LEX0001`, EOF.
- [x] **M1-Slice 2** — Alle 37 v1-Keywords als eigene TokenKinds (Dict-
  Lookup in ScanIdentifier), DocComments (`///`) als emittierte Tokens,
  Block-Comments mit Nesting als Trivia, `LYR-LEX0002` für unterminated
  Block-Comments.

## Woran wir gerade arbeiten

**M1-Slice 3** — Numerische Literals. Noch nicht begonnen, Plan steht aus.

Lieferposten (siehe `Sprache.md §1.5`):
- Int-Literals: dec/hex/bin/oct mit `_`-Separator und Suffixes (`i32`, `u64`, …)
- Float-Literals: Dezimal + Exponent + Suffix (`f32`, `f64`)
- Diagnostik-Codes für: invalid digit, multiple decimal points, invalid suffix,
  empty integer literal nach Präfix

## Was als nächstes ansteht

Nach Slice 3:
4. String- und Char-Literals (Slice 4)
5. f-String-Sub-Lexer (Slice 5)
6. Operatoren mit Longest-Match (Slice 6)
7. CLI `lyric tokenize` + Golden-Test-Infrastruktur (Slice 7)

## Offene Fragen / Diskussions-Punkte

Für Slice 3 zu klären:
- Numerische Werte zur Lex-Zeit parsen (`int Value` aufs Token) oder Lex-Zeit nur
  Erkennen, Parsen für Sema verschieben?
- Suffix-Validation (z.B. `100i7` mit unbekannter Größe): Lexer-Fehler oder
  Sema-Fehler?
- Edge-Case `0x_FF`: erlaubt (Separator direkt nach Präfix) oder verboten?

## Letzter relevanter Commit

`M1: add keyword tokens, doc comments, and block comments`

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
