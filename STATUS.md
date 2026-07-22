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

M2-Slices 1 (Expressions + TypeExpr) und 2 (Statements) abgeschlossen. M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer komplett** (`m1-complete`-Tag).
- [x] **M2-Slice 1 — Expressions + TypeExpr**: Pratt für alle Operatoren (§6.1);
  TypeExpr (Named/Generic/Array/Tuple/Function/Nullable) inkl. `>>`-Split; f-Strings,
  Lambdas, Array-/Tuple-Literale; `AstDumper`; Recovery. Codes `LYR-PAR0001..0015`.
- [x] **M2-Slice 2 — Statements** (§5): Block, `let`/`var`, `if`/`while`/`do-while`/
  `for-in`, `break`/`continue`/`return`/`yield`/`resume`, `defer`, `throw`,
  `try`/`catch`, `ExprStmt`; Block-Body-Lambdas (`=> { … }`) nachgezogen. `match`
  vertagt (Slice 4, Recovery-Stub). Codes `LYR-PAR0016..0024`. Parser als `partial`
  gesplittet (`Parser.Statements.cs`).

## Woran wir gerade arbeiten

**M2-Slice 3** — Declarations + Generics (§2/§3): Modul-Header, `import`, `pub`,
`fn`, `struct`, `class`, `enum`, `interface`, `extend`, `type`-Alias, globale `let`;
Generic-Params `<T>` / `<T :: [I]>`. Danach steht Module-Parsing → CLI `lyric parse`.

## Was als nächstes ansteht

- Slice 3: Declarations + Generics (s.o.).
- Slice 4: Patterns + `match` (schaltet auch `MatchExpr`/`IfExpr` als Ausdruck frei).
- CLI `lyric parse <file>` (M2-Artefakt), sobald Slice 3 Top-Level-Parsing liefert.

## Entschieden in Slice 1–2 (Kontext für spätere Sessions)

- AST = sealed-record-Hierarchie, Span pro Knoten. Dumper via Pattern-Match, kein Visitor.
- Expression-`<` ist IMMER Vergleich (kein Turbofish). Generics nur im Typkontext.
- Lambda vs. Tuple vs. Grouping: Lookahead auf `=>` hinter der balancierten `)`.
- Tuple: keine Arity-Obergrenze (min 2). `Sprache.md` angepasst.
- Kontroll-Statements halten immer `Block` (kein Dangling-Else); `else if` = verschachteltes `IfStmt`.
- `ExprStmt` „nur Call/Assign" (§5) prüft die Sema, nicht der Parser.
- `IfExpr`/`StructInitExpr`/`MatchExpr` kommen in Slice 3/4 mit den nötigen Bausteinen zurück.
- `{`-Block-vs-Struct-Init wird im Decl-Slice scharf (dann gibt es Struct-Init).

## Letzter relevanter Commit

`M2: statements + block-bodied lambdas (slice 2)`

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
