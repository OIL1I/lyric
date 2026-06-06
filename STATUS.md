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

M0 ist abgeschlossen (`m0-complete`-Tag). Bei M1 noch nicht begonnen —
nächster Schritt: Slice-Planung.

## Was schon erledigt ist

- [x] **M0 — Setup, Architekturrahmen, Test-Infra** (siehe `m0-complete`-Tag)
  - .NET-Solution + GitHub Actions CI (Ubuntu + Windows)
  - `FileId`, `Span` (UTF-16 Code-Unit-Offsets), `LinePosition`
  - `SourceManager`: Disk + Virtual Loading, Locate/Slice/GetLineText,
    line-start cache mit Binary-Search-Lookup
  - `Severity`, `Diagnostic`, `DiagnosticsComparer`, `DiagnosticEngine`:
    sammeln, sortieren (file→start→end→code), Text- und JSON-Rendering
  - 80+ xUnit-Tests grün, CI grün
  - `lyric --version` und `lyric --help` funktionieren

## Woran wir gerade arbeiten

Noch nichts begonnen. Erstes M1-Slice ist zu planen.

Lieferposten von M1 (siehe `docs/ROADMAP.md`):
- Token-Typen für alle Lexeme aus `Sprache.md §1`
- Keywords, Operatoren (Longest-Match), Literals
- f-String-Lexing mit Sub-Token-Modi
- Verschachtelbare Block-Kommentare, Line-Kommentare, Doc-Kommentare
- Diagnostik-Codes `LYR-LEX0001..0020`
- CLI: `lyric tokenize <file>`
- Golden-Tests pro Token-Klasse

## Was als nächstes ansteht

Slice-Aufteilung in der ersten M1-Session:
1. Token-Typen + Lexer-Skelett (Position-Tracking, Trivia-Handling)
2. Einfache Tokens: Whitespace, Kommentare, Identifier, Keywords
3. Numerische Literals (alle Bases + Suffixes)
4. String- und Char-Literals (inkl. Escape-Sequenzen)
5. f-String-Sub-Lexer
6. Operatoren mit Longest-Match-Disambiguation
7. CLI `lyric tokenize`
8. Golden-Test-Infrastruktur

## Offene Fragen / Diskussions-Punkte

- Lexer-Architektur: handgeschrieben mit `int _pos`-Index (rustc-Stil) oder
  Reader/Stream-basiert (Roslyn-Stil)? → Entscheidung im M1-Plan.
- Trivia-Modell: Tokens halten preceding Trivia (Roslyn) oder Trivia ist
  ein eigener Token-Typ im Stream? → Hat Folgen für Parser.
- f-String-Tokenisierung: Sub-Token-Modi inline im Hauptlexer (State-Maschine)
  oder rekursiver Sub-Lexer? → Entscheidung im M1-Plan.

## Letzter relevanter Commit

`M0: implement DiagnosticEngine with text and JSON renderers`

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
