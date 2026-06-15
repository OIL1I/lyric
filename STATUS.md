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

Slices 1–6 sind durch. Nur noch ein Slice bis M1-Complete.

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
- [x] **M1-Slice 4** — String/Char-Literals mit Escapes; Codes 0007–0010
- [x] **M1-Slice 5** — f-Strings via Mode-Stack; Code 0011
- [x] **M1-Slice 6** — Operatoren/Interpunktion mit Longest-Match
  (Case-Analyse pro Anfangszeichen); `.`/`:` jetzt echte Tokens

## Woran wir gerade arbeiten

**M1-Slice 7** — CLI `lyric tokenize` + Golden-Test-Infrastruktur +
`m1-complete`-Tag. Letzter Slice von M1.

## Was als nächstes ansteht

Nach M1-Complete:
M2

## Offene Fragen / Diskussions-Punkte



## Letzter relevanter Commit

`M1: lex operators and punctuation with longest-match`

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
