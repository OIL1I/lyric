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

M2-Slices 1–3 abgeschlossen. Nur noch Slice 4 (Patterns + `match`). M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer komplett** (`m1-complete`-Tag).
- [x] **M2-Slice 1 — Expressions + TypeExpr**: Pratt (§6.1), TypeExpr inkl. `>>`-Split,
  f-Strings, Lambdas, `AstDumper`. Codes `LYR-PAR0001..0015`.
- [x] **M2-Slice 2 — Statements** (§5): Block, Bindings, if/while/do-while/for-in,
  Jumps, defer, throw, try/catch, ExprStmt; Block-Body-Lambdas. `match` vertagt.
  Codes `LYR-PAR0016..0024`.
- [x] **M2-Slice 3 — Declarations + Generics** (§2/§3): Modul-Header, Imports,
  `fn`/`struct`/`class`/`enum`/`interface`/`extend`/`type`-Alias/globale `let`,
  Generics `<T :: [I]>`, `throws`, `params`, Default-Params. `ParseModule()` +
  CLI `lyric parse`. Codes `LYR-PAR0025..0032`. `examples/hello.lyr` parst clean
  (M2-Exit erreicht).

## Woran wir gerade arbeiten

**M2-Slice 4** — Patterns + `match` (§6.3): volle Pattern-Grammar (Literale,
Wildcard, Bindings, Tuple-/Struct-/Enum-Destructuring, Or-Patterns, Range, Guards),
`MatchStmt` + `MatchExpr`. Schaltet zugleich `IfExpr` als Ausdruck und den
Struct-Init-Ausdruck (`{`-Ambiguität) frei.

## Was als nächstes ansteht

- Slice 4: Patterns + `match` (s.o.) → schließt M2 ab.
- Danach M3: Resolver + Sema (basic).

## Entschieden in Slice 1–3 (Kontext für spätere Sessions)

- AST = sealed-record-Hierarchie, Span pro Knoten. Dumper via Pattern-Match, kein Visitor.
- Expression-`<` ist IMMER Vergleich (kein Turbofish). Generics nur im Typkontext.
- Lambda vs. Tuple vs. Grouping: Lookahead auf `=>` hinter der balancierten `)`.
- Tuple: keine Arity-Obergrenze (min 2).
- Kontroll-Statements halten immer `Block`; `else if` = verschachteltes `IfStmt`.
- `throws` und `type` sind **kontextuelle** Keywords (Lexer liefert Identifier).
- Struct/Class-Member: Feld braucht `,`, Block-Methode nicht (Option 1, `Sprache.md` §3.2/§3.3).
- Permissiv parsen, Sema prüft: `mut` an freien fn, `pub` an Membern, `ExprStmt` nur Call/Assign.
- Noch vertagt: Struct-Init-Ausdruck + `IfExpr`/`MatchExpr` → Slice 4.

## Letzter relevanter Commit

`M2: declarations + generics + module parsing (slice 3)`

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
