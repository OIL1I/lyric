# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M5 — IR + Bytecode — in Arbeit**

Slices P1–P3 stehen: IR-Datentypen, Printer + Goldens, Verifier. Das **Lowering AST → IR fehlt
noch** — IR entsteht bisher nur aus handgebauten Fixtures. ADR-006 (Coroutine-State-Machine-
Lowering) und ADR-013 (`.lyrbc` als plattformneutraler, spezifizierter Vertrag →
`docs/Bytecode.md`) sind ratifiziert und in `ROADMAP.md` fixiert. 1025 Tests grün, davon 84 in
`Lyric.Tests.Ir`.

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`), **M2 — Parser** (`m2-complete`), **M3 — Resolver + Sema
  basic** (`m3-complete`), **M4 — Sema full** (`m4-complete`). Volle v1-Sprache
  typgeprüft; Entscheidungen D1–D11 in `Sprache.md`/`Doku.md` fixiert. Details in den Tags / `git log`.
- [x] **M5 — P1 — IR-Datentypen**: `IrModule`/`IrFunction`/`IrBlock`, Ids als
  `BlockId`/`TempId`/`LocalId`/`FunctionId` (die Id **ist** der Slot-/Sprung-Index im späteren
  Bytecode, daher dichte Tabellen). Instruktionen als Records, Ops und Terminatoren getrennt
  (`IrOp` vs. `IrTerminator`) — „Terminator mitten im Block" ist damit unrepräsentierbar statt
  geprüft. `IrScalarType` + `TypeLowering` von `LyrType`.
- [x] **M5 — P2 — Printer + Golden-Tests**: `IrPrinter` als deterministischer Text-Dump (Typ steht
  am Dest, nie `AppendLine`, `switch` mit default-Wurf erzwingt Vollständigkeit). 7 Golden-Fixtures
  inkl. `loop` (Back-Edge).
- [x] **M5 — P3 — Verifier**: `IrVerifier.Verify` sammelt Befunde als Klartext-Strings, `VerifyOrThrow`
  wirft. Bewusst **keine** `LYR-IR####`-Codes: jeder Befund ist ein Compiler-Bug, keine
  User-Diagnose — der Code-Bereich bleibt echten Lowering-Fehlern vorbehalten.
  - **Vier Phasen mit Bail-out** (Tabellen → CFG-Form → Reachability + Availability → Def/Use +
    Typen). Prüfungen setzen einander voraus; bei Fundamentalfehlern bricht die Funktion ab, damit
    ein Fehler keine Kaskade auslöst (dasselbe Prinzip wie `ErrorType` als Poison in der Sema).
  - **Def/Use per Availability-Dataflow** (vorwärts, Schnittmenge als Meet, optimistisches TOP für
    Loop-Header). Bei genau einer Definition pro Temp ist „auf jedem Pfad verfügbar" äquivalent zu
    „die Definition dominiert den Use" — kein Dominator-Baum nötig, bis Phi-Knoten dazukommen.
  - 74 Testfälle: jede gültige Fixture befundfrei, eine Invariante pro Negativ-Test, plus
    Bail-out-ohne-Kaskade, Isolation zwischen Funktionen, Determinismus, und dass der Verifier auf
    malformed IR nie selbst crasht.

## Woran wir gerade arbeiten

Als Nächstes **M5 — P4: Lowering AST → IR**. Es ist der erste echte Abnehmer des Verifiers — ab
dann läuft er nach jedem Lowering-Durchlauf statt nur gegen Fixtures.

## Noch offen

**Aus M5 P1–P3:**

- Der Verifier läuft noch an keiner Produktions-Aufrufstelle (kommt mit P4: immer in Debug/Tests,
  im Release hinter Flag).
- `docs/Bytecode.md` (ADR-013) noch nicht begonnen.

**Git-Stand (nicht vergessen):**

- `origin/main` steht auf `0114908`, also 6 Commits hinter lokal. Und auf origin liegen nur
  `m0-complete`, `m1-complete`, `setup-complete` — **`m2-complete`, `m3-complete` und `m4-complete`
  sind lokal, nie gepusht**. Solange das so bleibt, existiert alles ab M2 nur auf diesem Rechner.

**Aus M4 vertagt:**

- **Generics-Rest**: Constraints mit eigenen Typ-Args (`Comparable<T>` über die Constraint-Grenze
  substituieren); Tuple-Varianten-Konstruktion generischer Enums über Call (`Opt.Some(5)`) — typt
  noch ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath nicht ausdrückbar;
  Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer).
- **Slice-4-Feinheiten**: generische Interface-Default-Substitution beim Member-Lookup nur
  best-effort; `@noCapture`-Enforcement fehlt (Lambda-Params tragen keine Attribute im AST).
- **Extern**: Stdlib-Imports opak → Modul-Universum + Builtin-Konformanz erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- **IR**: Type-Felder auf den Instruktionen sind Kopien für den Printer, die Temp-Tabelle ist die
  Autorität — dass beide übereinstimmen, ist der Kern-Job des Verifiers. Die tragenden
  IR-Invarianten (Parameter-Konvention, dichte Id-Tabellen, Entry-Regel, Single-Definition) stehen
  als Doku an `IrFunction`, durchgesetzt werden sie vom Verifier.
- **Totale Funktionen über das heutige Typ-Universum werfen im `default`**, statt einen Ersatzwert
  zu liefern: `IrType.Equal`, `IrNames.Scalar/Bin/Un`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`. Der Wurf nennt die Stelle, die beim Erweitern nachzuziehen ist. Ausnahme ist
  `IrVerifier.Show`: es baut Befund-Texte, ein Wurf würde dort den Befund verdecken.
- **`IrNames` ist die einzige Quelle für Skalar-Namen und Op-Mnemonics** (Printer + Verifier). Man
  liest Dump und Befunde nebeneinander, wenn man einen Lowering-Bug sucht — sie dürfen nicht driften.
- „Ist der Typ genau dieser Skalar?" → Pattern-Match (`IsVoid`/`IsBool`, total). „Stimmen zwei Typen
  überein?" → `IrType.Equal`. Zwei verschiedene Fragen, zwei Mechanismen.
- **IR-Invarianten, die Arbeit ins Lowering verschieben** (alle im Verifier durchgesetzt und
  getestet): unerreichbare Blöcke sind ein Fehler (kein `SimplifyCfg`-Pass in v1); Block-Ids dicht
  und `Entry == Blocks[0]`; `string + string` lowert zu einem Call, **nicht** zu `BinOp Add` (sonst
  wäre der `add`-Opcode polymorph — gegen ADR-013); `IntConst` ist zweierkomplement-kodiert und auf
  64 Bit nullerweitert; Identitäts-`Convert` elidiert das Lowering; Ordnungsvergleiche nur auf
  Numerik, `eq`/`ne` auch auf bool/char/string.
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.
- **Zeilenenden sind Test-Vertrag, nicht Geschmack**: `.gitattributes` erzwingt `eol=lf` auch im
  Arbeitsbaum, weil die Lexer-/Parser-Goldens Span-Offsets vergleichen und CRLF jeden Offset um ein
  Byte pro Zeile verschiebt. Nicht entfernen — ohne sie fallen 14 Golden-Tests in jedem frischen
  Clone.

## Letzter relevanter Commit

`docs: STATUS.md - m4-complete-Stand korrigiert`

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
