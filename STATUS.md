# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M4 — Sema (full) — abgeschlossen** (Tag `m4-complete` ausstehend)

Volle v1-Sprache typgeprüft: Generics, Pattern-Match + Exhaustivität, Exceptions,
Coroutinen, Closures, Interfaces + Extend. Entscheidungen D1–D10 ratifiziert und in
`Sprache.md`/`Doku.md` fixiert. `lyric check` läuft sauber auf allen Beispielen außer
`stack.lyr`/`stats.lyr` (warten auf M8-Array-Methoden). 937 Tests grün.

**Nächster Meilenstein: M5 — IR + Bytecode.** ADR-013 (`.lyrbc` als plattformneutraler,
spezifizierter Vertrag → `docs/Bytecode.md`) und ADR-006 (Coroutine-State-Machine-Lowering)
beachten. Noch nicht geplant.

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`), **M2 — Parser** (`m2-complete`), **M3 — Resolver +
  Sema basic** (`m3-complete`). Details in den Tags / `git log`.
- [x] **M4 — Slices 1+2** (Generics, Pattern-Match voll): `TypeParamType`/`GenericInstance`
  mit Substitution, Call-Inferenz und Constraints (D2); Enum-Payload-/Struct-/Tuple-
  Destructuring, Or-Pattern-Konsistenz, Exhaustivität (D4), Block-Wert-Regel (SEM0033),
  kontextuelle Varianten-Konstruktion (§3.4). Codes `LYR-SEM0026..0033`, `0050`.
- [x] **M4 — Slice 3 — Exceptions + Coroutinen** (3a + 3b):
  - **Builtins**: `Throwable` als Builtin-Interface (abstraktes `message(): string`),
    `panic` → `never` (Bottom-Typ, Divergenz), `Coroutine<T>` → interner `CoroutineOf`.
  - **Exceptions (3a)**: Throwable-Constraint an throw/throws/catch (SEM0030); try ≥1 catch
    (SEM0036), Catch-All zuletzt (SEM0035); throws-Propagation als Post-Pass
    (`ExceptionAnalyzer`, SEM0034); throws-Fn als Wert verboten (SEM0037).
  - **Coroutinen (3b, D6–D8)**: `resume` als Präfix-Ausdruck; yield nur in `Coroutine<T>`
    + wertgeprüft (SEM0038); nur nacktes return (SEM0039); resume liefert Yield-Typ (SEM0040).
  - Codes `LYR-SEM0030, 0034..0040`. `bank.lyr` + `fibonacci.lyr` checken sauber.
- [x] **M4 — Slice 4 — Closures + Interfaces + Extend** (4a + 4b):
  - **Lambdas (4a, D5/D9)**: bidirektionale Inferenz — CheckCall zweiphasig (T aus eager
    Argumenten, U aus dem Lambda-Return); unannotierte Params nehmen den Kontext-FnType
    (SEM0045); Block-Lambdas liefern Werte über return, Typ aus Annotation/Kontext (SEM0046),
    `return` checkt gegen die Lambda. Captures (ADR-011) als Seitentabelle fürs M5-Lifting;
    DAA analysiert Lambda-Bodies (Erstellungsort-Snapshot). Codes `LYR-SEM0045, 0046`.
  - **Interfaces + Extend (4b, D10)**: Extend-Merge über `ExtensionRegistry` (import-gebundene
    Sichtbarkeit); Member-Lookup eigene → Extension (SEM0044) → Interface-Default (SEM0043),
    Builtins via Primitiv→Builtin-Symbol; signatur-genaue Konformanz (SEM0020 + SEM0042,
    generische Interfaces substituiert, throws-Subset); Orphan-Rule (SEM0041); nicht-benannte
    Extend-Ziele (SEM0047); `extend :: [I]` erfüllt Generics-Constraints. Codes
    `LYR-SEM0041..0044, 0047`.
  - `examples/inventory.lyr` als M4-Exit-Artefakt (Interface+Default+Extend+Closure+Generics
    +Match), checkt sauber.

## Woran wir gerade arbeiten

M4 abgeschlossen. Als Nächstes: **M4 taggen** (`m4-complete`), dann **M5 — IR + Bytecode**
planen (erstes Backend-Slice, `docs/Bytecode.md` entsteht dabei laut ADR-013).

## Noch offen (nach M4 vertagt)

- **Generics-Rest**: Constraints mit eigenen Typ-Args (`Comparable<T>` über die Constraint-
  Grenze substituieren); Tuple-Varianten-Konstruktion generischer Enums über Call
  (`Opt.Some(5)`) — typt noch ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath
  nicht ausdrückbar; Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer).
- **Slice-4-Feinheiten**: generische Interface-Default-Substitution beim Member-Lookup nur
  best-effort; `@noCapture`-Enforcement fehlt (Lambda-Params tragen keine Attribute im AST).
- **Extern**: Stdlib-Imports opak → Modul-Universum + Builtin-Konformanz erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M4: sema — interfaces + extend (slice 4b)`

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
