# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M2 — Parser**

Startet gerade. M1 (Lexer) ist abgeschlossen — `m1-complete`-Tag.

## Was schon erledigt ist

- [x] **M0 — Setup, Architekturrahmen, Test-Infra** (`m0-complete`-Tag)
- [x] **M1 — Lexer komplett** (`m1-complete`-Tag): alle Token-Klassen aus
  `Sprache.md §1`, Longest-Match-Operatoren, 4 Zahlbasen + Suffixe,
  String/Char/f-Strings, Kommentare; Codes `LYR-LEX0001–0012`;
  `lyric tokenize` + Golden-Test-Infrastruktur.

## Woran wir gerade arbeiten

**M2-Slice 1** — erster Slice noch zu schneiden (siehe `ROADMAP.md §M2`).

## Was als nächstes ansteht

M2 liefert (laut ROADMAP): AST-Typen für alle Knoten, AST-Dumper,
Recursive-Descent + Pratt-Expressions, Patterns, `lyric parse <file>`,
Golden-Tests je Syntax-Form. Codes `LYR-PAR0001..0050`.

## Offene Fragen / Diskussions-Punkte

- Wie schneiden wir M2? M2 ist groß. Grobe Kandidaten als getrennte Slices:
  AST-Knoten + Dumper → Top-Level-Decls → Statements → Pratt-Expressions
  → Patterns. Reihenfolge/Granularität beim M2-Kickoff festlegen.
- Pratt vs. reiner Recursive-Descent für Expressions (Präzedenz-Tabelle
  `Sprache.md §6.1`) — Entscheidung gehört in den ersten M2-Plan.

## Letzter relevanter Commit

`M1: add lyric tokenize and golden test infrastructure`

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
