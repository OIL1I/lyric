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

Slices 1–5 sind durch. Nur noch zwei Slices bis M1-Complete.

## Was schon erledigt ist

- [x] **M0 — Setup, Architekturrahmen, Test-Infra** (siehe `m0-complete`-Tag)
- [x] **M1-Slice 1** — Lexer-Skelett, Whitespace, Identifier, Brace-Punctuation
- [x] **M1-Slice 2** — Keywords, Doc-Comments, Block-Comments mit Nesting
- [x] **M1-Slice 3** — Int/Float-Literals (4 Basen, Suffixes); Codes 0003–0006
- [x] **M1-Slice 4** — String/Char-Literals mit Escapes (`\n`, `\x##`, `\u{...}`);
  Codes 0007–0010
- [x] **M1-Slice 5** — f-Strings via Mode-Stack: Text/Interp/FormatSpec/Normal,
  Brace-Depth-Tracking, nested f-Strings, Disambiguierung `f"` vs
  Identifier `f`; Code 0011

## Woran wir gerade arbeiten

**M1-Slice 6** — Operatoren und Interpunktion mit Longest-Match-Disambiguation.

Lieferposten (siehe `Sprache.md §1.6`):
- Single-char Puncts: `,` `.` `;` `:` `->` `=>` etc.
- Arithmetik: `+` `-` `*` `/` `%`
- Bitwise: `&` `|` `^` `~` `<<` `>>`
- Vergleich: `==` `!=` `<` `<=` `>` `>=`
- Logisch: `&&` `||` `!`
- Assignment: `=` plus alle Compound (`+=`, `-=`, …, `&&=`, `??=`)
- Optional: `?` `?.` `??` `!` (Postfix-Unwrap vs Prefix-Not — Context-frei
  unterscheidbar via Lexer? Oder Parser?)
- Range: `..` `..=`
- Increment/Decrement: `++` `--`
- Spezial: `::` (implements-Op)
- Disambiguierung `<<` vs `<` `<` (Generics-Konflikt — Parser-Job)

## Was als nächstes ansteht

Nach Slice 6:
7. CLI `lyric tokenize` + Golden-Test-Infrastruktur + `m1-complete`-Tag

## Offene Fragen / Diskussions-Punkte

Für Slice 6 zu klären:
- Longest-Match-Strategie: Tabelle aller mehrzeichigen Operatoren mit Trie-Lookup,
  oder direkt Case-Analyse pro Anfangszeichen?
- `!` ist sowohl Postfix-Force-Unwrap (`expr!`) als auch Prefix-Logical-Not (`!expr`).
  Im Lexer ein TokenKind oder zwei? (Parser-Context entscheidet.)
- `<` vs Generics `<T>`-Open: alles Less-Than-Token, Parser disambiguiert?
- Reservierter Bereich: `LYR-LEX0012` aufwärts für Slice 6, falls Operatoren
  Lex-Errors generieren können (vermutlich keine, da alles definiert ist).

## Letzter relevanter Commit

`M1: lex f-strings with mode stack for nested interpolation`

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
