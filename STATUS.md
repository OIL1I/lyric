# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M2 — Parser (abgeschlossen)**

Alle Slices 1–4 plus Struct-Init fertig. `examples/hello.lyr` parst clean, `lyric parse`
liefert vollständige Modul-ASTs. Offen: Tag `m2-complete` setzen, dann M3 planen.

## Was schon erledigt ist

- [x] **M1 — Lexer komplett** (`m1-complete`-Tag).
- [x] **M2 — Parser komplett**: Expressions (Pratt, §6.1), TypeExpr (inkl. `>>`-Split),
  Statements (§5), Declarations + Generics (§2/§3), Patterns + `match` (§6.3), `IfExpr`,
  Struct-Init. Einstiege `ParseModule`/`ParseStatement`/`ParsePattern`/`ParseExpression`,
  `AstDumper`, CLI `lyric parse`. Recovery überall (Parser wirft nie). Codes
  `LYR-PAR0001..0037`. 179 Parser-Tests (Golden + Unit).

## Woran wir gerade arbeiten

Nichts offen in M2. Nächster Meilenstein: **M3 — Resolver + Sema (basic)** (ROADMAP):
Modul-Auflösung, Symboltabellen, Typsystem ohne Generics, DAA, Cast-Regeln,
`main`-Entry-Contract. Noch nicht geplant/geschnitten.

## Was als nächstes ansteht

- Tag `m2-complete` setzen.
- M3 planen (Slice-Schnitt festlegen).

## Entschieden in M2 (Kontext für spätere Sessions)

- AST = sealed-record-Hierarchie, Span pro Knoten. Dumper via Pattern-Match, kein Visitor.
- Expression-`<` ist IMMER Vergleich (kein Turbofish). Generics nur im Typkontext.
- Lambda vs. Tuple vs. Grouping: Lookahead auf `=>` hinter balancierter `)`.
- Tuple: keine Arity-Obergrenze (min 2).
- `throws`/`type` sind kontextuelle Keywords (Lexer liefert Identifier).
- Struct/Class-Member + Match-Arme: Feld/Expr-Body braucht `,`, Block-Body nicht (Option 1).
- Pattern-Bind-vs-Unit-Variante: nackter Einzel-Ident → BindingPattern (Sema entscheidet); qualifiziert/mit Payload → VariantPattern.
- `if`: Statement → `IfStmt` (Blocks, else optional); Ausdruck → `IfExpr` (Ausdruck-Branches, else Pflicht → garantierter Wert).
- Struct-Init: nur in Wert-Position (nicht am ExprStmt-Anfang; `{`-Block-Ambiguität via `_allowStructInit`-Flag).
- **Für die Sema offen**: Block-Wert-Frage bei Match-Block-Armen; permissiv Geparstes prüfen (`mut` an freien fn, `pub` an Membern, `ExprStmt` nur Call/Assign, `params`/Default-Param-Regeln, `main`-Contract, Exhaustivität).

## Letzter relevanter Commit

`M2: struct-init expressions + '{' disambiguation`

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
