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

M2-Slice 1 (Expressions + TypeExpr) abgeschlossen. M1 (Lexer) = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer komplett** (`m1-complete`-Tag).
- [x] **M2-Slice 1 — Expressions + TypeExpr**: Pratt-Parser für alle Operatoren
  (§6.1) mit korrekter Präzedenz/Assoziativität; TypeExpr (Named/Generic/Array/
  Tuple/Function/Nullable) inkl. `>>`-Split für verschachtelte Generics; f-Strings,
  Lambdas (Expression-Body), Array-/Tuple-Literale, Grouping; `AstDumper`; Recovery
  (Parser wirft nie → ErrorExpr/ErrorType). Codes `LYR-PAR0001..0015`. Golden- +
  Unit-Tests in `Lyric.Tests.Parsing`.

## Woran wir gerade arbeiten

**M2-Slice 2** — Statements (§5): Block, `let`/`var`, `if`/`while`/`do-while`/
`for-in`, `break`/`continue`/`return`/`yield`/`resume`, `defer`, `throw`,
`try`/`catch`, `ExprStmt`. (`match` erst mit Patterns in Slice 4.)

## Was als nächstes ansteht

- Slice 2: Statements (s.o.). `Block` schaltet Block-Body-Lambdas (`=> { … }`,
  Parser-TODO) und später `IfExpr` frei.
- Slice 3: Declarations + Generics. Slice 4: Patterns + `match`.
- CLI `lyric parse <file>` (M2-Artefakt) sobald Top-Level-Parsing steht.

## Entschieden in Slice 1 (Kontext für spätere Sessions)

- AST = sealed-record-Hierarchie, Span pro Knoten. Dumper via Pattern-Match, kein Visitor.
- Expression-`<` ist IMMER Vergleich — Lyric hat keinen Turbofish. Die „`<`-Generics
  -Ambiguität" existiert nur im Typkontext und ist dort durch RD gelöst.
- Lambda vs. Tuple vs. Grouping: Lookahead auf `=>` hinter der balancierten `)`.
- Tuple: keine Arity-Obergrenze (min 2). `Sprache.md` entsprechend angepasst.
- `IfExpr`/`StructInitExpr` entfernt (verfrüht) — kommen in ihrem Slice mit
  `Block`/`StructInitField` korrekt zurück.
- `{`-Block-vs-Struct-Init: in Slice 1 kein Thema (keine Blocks); im Decl-Slice klären.

## Letzter relevanter Commit

`M2: complete parser expression + type layer (slice 1)`

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
