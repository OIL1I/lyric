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

M2-Slices 1–4 abgeschlossen. Für komplettes M2 fehlt nur noch der Struct-Init-Ausdruck.
M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer komplett** (`m1-complete`-Tag).
- [x] **M2-Slice 1 — Expressions + TypeExpr**: Pratt (§6.1), TypeExpr inkl. `>>`-Split,
  f-Strings, Lambdas, `AstDumper`. Codes `LYR-PAR0001..0015`.
- [x] **M2-Slice 2 — Statements** (§5): Block, Bindings, if/while/do-while/for-in,
  Jumps, defer, throw, try/catch, ExprStmt; Block-Body-Lambdas. Codes `LYR-PAR0016..0024`.
- [x] **M2-Slice 3 — Declarations + Generics** (§2/§3): Modul-Header, Imports,
  fn/struct/class/enum/interface/extend/type-Alias/globale let, Generics, `throws`,
  `params`. `ParseModule()` + CLI `lyric parse`. Codes `LYR-PAR0025..0032`.
- [x] **M2-Slice 4 — Patterns + `match`** (§6.3): volle Pattern-Grammar (Wildcard,
  Literale/Ranges, Bindings, Varianten, Tuple, Or, Field-Patterns), `MatchStmt` +
  `MatchExpr`, `IfExpr`. Codes `LYR-PAR0033..0036`.

## Woran wir gerade arbeiten

**Struct-Init-Ausdruck** (`TypePath '{' field = expr … '}'`, §6.2) — die letzte
`{`-Ambiguität. Braucht ein `_allowStructInit`-Flag (verboten am ExprStmt-Anfang,
freigeschaltet in Klammern/Args), das mehrere committete Expression-Stellen berührt.
Schließt M2 ab → Tag `m2-complete`.

## Was als nächstes ansteht

- Struct-Init → M2 komplett (Tag `m2-complete`).
- Danach M3: Resolver + Sema (basic).

## Entschieden in Slice 1–4 (Kontext für spätere Sessions)

- AST = sealed-record-Hierarchie, Span pro Knoten. Dumper via Pattern-Match, kein Visitor.
- Expression-`<` ist IMMER Vergleich (kein Turbofish). Generics nur im Typkontext.
- Lambda vs. Tuple vs. Grouping: Lookahead auf `=>` hinter balancierter `)`.
- Tuple: keine Arity-Obergrenze (min 2).
- `throws`/`type` sind kontextuelle Keywords (Lexer liefert Identifier).
- Struct/Class-Member + Match-Arme: Feld/Expr-Body braucht `,`, Block-Body nicht (Option 1, Spec angepasst).
- Pattern-Bind-vs-Unit-Variante: nackter Einzel-Ident → BindingPattern (Sema entscheidet); qualifiziert/mit Payload → VariantPattern.
- `if`: Statement → `IfStmt` (Blocks, else optional); Ausdruck → `IfExpr` (Ausdruck-Branches, else Pflicht → garantierter Wert).
- Block-Wert-Frage: für if-Ausdrücke durch Ausdruck-Branches gelöst; für Match-Block-Arme noch offen (Sema, M3/M4).
- Parser permissiv, Sema prüft: `mut` an freien fn, `pub` an Membern, `ExprStmt` nur Call/Assign.

## Letzter relevanter Commit

`M2: patterns + match + if-expression (slice 4)`

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
