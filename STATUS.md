# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M7 — Objektmodell + VM (full) — abgeschlossen.** Slices P1 bis P9 stehen.

Bytecode-Format **2.5**. 1586 Tests grün. Das Gate `examples/inventory.lyr` läuft: es belastet
Interface mit Default-Methode, `::`-Konformanz, `extend`, Closure als Parameter, generische
Funktion mit Constraint, `match` auf einem Enum mit Payload und Nullable-Rückgabe **gleichzeitig**
— und hat dabei drei Lücken gefunden, die kein Einzelslice bemerkt hatte (siehe unten).

Damit läuft die Sprache von der Quelle bis zur Ausführung für alle 38 Konstrukte aus
`Sprache.md`. **`extend` war das letzte ohne Slice.**

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst (Meilenstein-Kopf stand auf M6, „woran wir arbeiten" auf P5, die Verifier-Laufzeit einmal
> als widerlegt und zwanzig Zeilen später wieder als Tatsache). Sie ist auf ihre eigene Pflegeregel
> zurückgeschnitten: letzte Slices, offene Punkte, Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **P9 — `extend`** (Sprache.md §3.6), beide Formen. **M7 ist damit abgeschlossen.**
  - **P9a — inhärent.** Eine Extension-Methode ist eine gewöhnliche Funktion mit dem Empfänger als
    Parameter 0 (ADR-014): kein IR-Typ, kein Opcode, kein Format-Bump, ein direkter `call`. Ein
    Skalar als Parameter 0 ist nichts Neues — deshalb braucht `extend int` **kein Boxing**.
  - **Das Mangling war die Arbeit, nicht das Lowering.** Es trägt das *deklarierende* Modul und
    einen `<extend>`-Infix, und beides ist erzwungen: `extend string` darf in jedem Modul stehen,
    und §3.6 lässt eine Extension zu, die einen gleichnamigen Member verdeckt — die Sema **meldet
    das nicht**, sie lässt nur den eigenen Member gewinnen. Ohne den Infix hießen beide
    `main.Player.get`, und der Verifier lehnt doppelte Funktionsnamen ab: ein sauber typgeprüftes
    Programm wäre im Lowering abgestürzt.
  - **P9b — `extend T :: [I]`, und das war zuerst ein Sema-Slice.** Es gab **zwei** Antworten auf
    „erfüllt T das Interface I": `ImplementsInterface` ohne Extensions (für Zuweisungen),
    `ImplementsWithExtensions` mit (für Constraints). Ein Constraint akzeptierte damit eine
    Extension-Konformanz, eine Zuweisung nicht. Der Doc-Kommentar über der ersten warnte wörtlich
    vor genau dieser Spaltung, während die zweite 1200 Zeilen darüber stand.
  - **Kein Skalar-Boxing, und es musste nicht verboten werden**: `extend int :: [Display]` bleibt
    abgelehnt, weil `TypeFacts.SymbolOf` für einen Primitive kein Symbol liefert. Der Fall, der
    Boxing wirklich brauchte — `println<T :: [Display]>` —, braucht es nicht: Monomorphisierung
    mit Constraint wird zu einem direkten Aufruf.
  - **Ein Skalar-Empfänger ging zunächst verloren.** `n.double()` fiel durch alle
    `MemberExpr`-Fälle bis zum Typ-/Modul-Zweig, der keinen Empfänger anhängt — ein `int` ist kein
    `NamedRef`, und die Fallunterscheidung fragte nur danach.

- [x] **Drei Lücken, die erst das M7-Gate gefunden hat** — keine davon aus P9.
  - **Eine Interface-Default-Methode auf einem konkreten Empfänger** (`it.isFree()`) rief direkt
    statt zu heben. Ihr `this` ist der Interface-Typ, dorthin führt kein direkter Aufruf.
    `LowerConstraintCall` machte es seit dem P8-Nachtrag richtig — der Fall **ohne** Constraint
    fehlte, weil bis dahin kein Beispiel eine Default-Methode direkt aufrief.
  - **`match` über ein Optional** war zweifach falsch: `null` als Muster wurde ein
    *Gleichheitsvergleich* (es gibt keinen null-Operanden — es ist `optissome`), und die Bindung
    im anderen Arm speicherte ein `?T` in einen `T`-Slot. Die Sema gibt dem Namen den eingeengten
    Typ; ausgepackt werden muss trotzdem, weil das Narrowing eine Aussage über den Kontrollfluss
    ist und nicht über den Speicher.
  - **`for-in` in einer generischen Funktion** internierte den `ArrayIterator` mit dem
    Typ-*Parameter*; die Typtabelle suchte nach einer Klasse namens `T`.
  - **Das ist der Zweck eines Gates.** Ein Programm, das mehrere Slices gleichzeitig belastet,
    findet die Kanten *zwischen* ihnen — und keine dieser drei hätte ein Slice-Test gefunden, weil
    jeder für sich grün war.

## Messungen

Zahlen statt Meinungen. Erhoben 2026-08-07, Release, 100 000 Iterationen, bereinigt um eine
Skalar-Schleife derselben Länge.

| Was | Bytes/Operation |
|---|---|
| Struct-Bau **+** Methodenaufruf (`Vec2.add`) | **352 B** |
| nur Aufruf (`fn step(a: float): float`) | **176 B** |
| nur Struct-Bau | **112 B** |
| Skalar-Basislinie | 9 064 B *insgesamt* |

**Die VM ist im Kern allokationsfrei** — eine Schleife mit Fließkomma-Arithmetik allokiert über
100 000 Durchläufe nichts Nennenswertes. Alles darüber sind Aufrufe und Objekte.

**Die Hälfte der Bytes hat mit Structs nichts zu tun**: `Frame.For` allokiert pro Aufruf drei
Objekte (Frame, Slots, Stack). Damit ist die Reihenfolge für eine spätere Optimierung festgelegt —
**Frame-Pooling, dann Inlining, dann Scalar Replacement**, nicht umgekehrt: der in `add` gebaute
Wert **escaped** (er wird zurückgegeben), also findet Escape-Analyse ohne vorheriges Inlining
nichts. Die Ideen stehen in `docs/IDEAS.md`; **gebaut wird davon in v1 nichts.**

Im Frame-Budget: 1000 Entities × 10 Vec-Operationen × 60 fps ≈ 211 MB/s, grob eine
Gen0-Sammlung pro Frame. Gen0 ist kurz — **kein Grund, Vektor-Mathematik hinter Natives zu
verlegen.** Das war die offene Frage aus P4; sie ist beantwortet.

Weiter gemessen: `for-in` über einen Range kostet **1,28×** gegenüber einer `while`-Schleife (nicht
mehr, wie der P8-Eintrag befürchten ließ). Der Verifier ist **~50 %** der Lowering-Zeit in Debug,
nicht ~90 % — die alte Behauptung stammte aus M5 und hatte nie eine Quelle. Ein Release-Profil
steht weiter aus.

## Woran wir gerade arbeiten

**M7 ist abgeschlossen — als nächstes M8.** Der Meilenstein-Zuschnitt steht in der ROADMAP;
`std.fmt` mit Format-Specs und die Builtin-Konformanz (`Throwable`, `Display`) sind die beiden
Posten, an denen die meisten offenen Punkte unten hängen.

## Noch offen

**Sprachlücken, vor v1 zu schließen:**

- **`b?.get()` geht nicht** — Optional-Chaining mit *Methodenaufruf*. Die Sema macht `?.get` zu
  einem `?fn() -> int` und stolpert dann über das `()`. Feldzugriff (`b?.v`) funktioniert.
- **Die Konformanz prüft die Definition statt der Typargumente**: `Ones :: [Src<int>]` würde auch
  für `Src<string>` akzeptiert.
- **`string` lässt sich nicht iterieren** — `std.string` hat kein `length`, mit dem ein Adapter
  laufen könnte.
- **`catch (e)` ohne Typ** ist `LYR-IR0001`: der Slot bräuchte `Throwable` als Interface, das hängt
  an der Builtin-Konformanz (M8). `catch (_)` und `catch (e: T)` gehen.
- **Parser: `s = Small { n = 5 };`** scheitert mit `LYR-PAR0016`, obwohl §6.2 den Ausdruck „in jeder
  Wert-Position" erlaubt — die Mehrdeutigkeits-Sperre gilt dem *Anfang* eines `ExprStmt`, greift
  aber auf die ganze Zuweisung durch. *(Bekannt seit P3 — und am 2026-08-07 beim Schreiben einer
  Messprobe erneut hineingelaufen, ohne ihn wiederzuerkennen. Er kostet real Zeit.)*
- **Generics-Rest aus M4**: Constraints mit eigenen Typ-Args über die Grenze substituieren;
  `Opt.Some(5)` typt ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath nicht ausdrückbar.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.

**Zwei Ungleichbehandlungen, die eine Entscheidung brauchen (keine Bugs):**

- `let p = P { hp = 1 }; p.hp = 9;` ist **erlaubt**, `let xs = [1,2]; xs[0] = 9;` ist
  **`LYR-SEM0019`**. §6.4 unterscheidet sie ausdrücklich, aber seit ADR-016 ist `T[]` genauso ein
  Referenztyp wie eine Klasse. **Zu klären, bevor `Indexable<T>` kommt** — dort hängt dieselbe
  Frage am `mut fn`-Setter nochmal.
- `fn f(p: P) { p.x = 9; }` ist `LYR-SEM0019`, aber `p.shift(9)` mit `mut fn` geht durch — obwohl
  beides denselben Effekt auf dieselbe Kopie hat.

**Werkzeug und Format:**

- **Source-Map-Sektion** (Id 6) ist reserviert und beschrieben, wird aber nicht geschrieben —
  Panics zeigen deshalb die Funktion, nicht die Zeile.
- **Sektions-Byte-Größen fehlen in `lyrvm info`**: der Reader verwirft sie nach dem Parsen. Sie
  nachzurüsten hieße, das Modell um Herkunftsdaten zu erweitern — eigene Entscheidung.
- **`std.fmt.format` nach M8** (Format-Specs `{x:N2}` in `shapes.lyr`/`stats.lyr`).
- **Verifier-Anteil im Release-Profil messen** — die Debug-Zahlen sind von JIT-Aufwärmen
  durchsetzt und taugen nur als Größenordnung.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; **`ErrorType` heißt ausschließlich „hier wurde bereits
  gemeldet"** — nicht „unbekannt". Maschinell geprüft.
- Generics: Monomorphisierung. Die einzige Option, die zu dieser VM passt — C# reifiziert und
  braucht einen JIT, Java erased und bezahlt mit Boxing; beides setzt voraus, dass die Runtime
  Typen kennt, und ein Lyric-Wert trägt kein Typ-Tag (ADR-013).
- **Ein Wert trägt kein Typ-Tag.** Jeder Opcode trägt sein Tag im Instruktionsstrom, der Dispatch
  bleibt statisch. Daraus folgt das Fat-Pointer-Muster, das Interfaces (P3), Closures (P6) und
  Coroutinen (P7) teilen: Referenz plus Wort in `LyrValue`.
- **IR**: Type-Felder auf den Instruktionen sind Kopien für den Printer, die Temp-Tabelle ist die
  Autorität — dass beide übereinstimmen, ist der Kern-Job des Verifiers.
- **Totale Funktionen über das heutige Typ-Universum werfen im `default`**, statt einen Ersatzwert
  zu liefern (`IrType.Equal`, `IrNames.*`, `TypeLowering.Lower`, `IrPrinter.TypeStr`,
  `IrBinKind.FromAst`). Der Wurf nennt die Stelle, die beim Erweitern nachzuziehen ist. Ausnahme
  ist `IrVerifier.Show` — dort würde ein Wurf den Befund verdecken. *(Ein `default`, der still
  nichts tut, hat schon einmal den Instruktionsstrom desynchronisiert: `CodeDecoder.SkipType`.)*
- **`IrShape` ist die einzige Quelle für Operanden/Dest/Successors**, **`IrNames` die einzige für
  Skalar-Namen und Mnemonics.** Zwei Kopien dieser switch-Blöcke wären still falscher Code.
- **Lowering**: Statements liefern „fällt der Kontrollfluss durch?"; Werte über Blockgrenzen laufen
  durch (ggf. synthetische) Locals, nie durch Temps — **genau deshalb braucht diese IR kein Phi**.
  Blockdichte und `Entry == bb0` sind im `BlockBuilder` strukturell garantiert statt geprüft.
- **Zwei Fehlerklassen im Lowering**: gültiges Lyric, das der Backend-Stand nicht kann → `LYR-IR0001`
  mit Position; interne Inkonsistenz → `InternalCompilationException`. **Bewusst genau ein IR-Code**
  — Codes sind stabile Bezeichner, die Lücken vorübergehend. `LYR-IR0002..0010` bleiben frei.
  Ebenso gilt: eine entfallene Nummer (`LYR-CLI0007`) wird **nie** neu vergeben.
- **Zeilenenden sind Test-Vertrag, nicht Geschmack**: `.gitattributes` erzwingt `eol=lf` auch im
  Arbeitsbaum, weil die Goldens Span-Offsets vergleichen. **Nicht entfernen** — ohne sie fallen 14
  Golden-Tests in jedem frischen Clone und der `windows-latest`-Job bricht.
- **Arbeitsmodus** (Scope-Check 2026-08-02, gilt weiter): Claude plant *und* implementiert, der
  Maintainer reviewt — bewusste Abweichung von `CLAUDE.md` §Collaboration, wo
  Plan-von-Claude/Code-vom-User steht. Zu beobachten ist, ob das Verständnis des Codes mit seinem
  Umfang mithält. **Kein `CHANGELOG.md` vor `v1.0.0`**; die annotierte Tag-Message ist die
  Release-Notiz.
- **Am Ende jedes Meilensteins ist die Lieferposten-Liste Punkt für Punkt abzuhaken, nicht das
  Exit-Kriterium allein.** M5 und M6 haben je einen Teil ihrer Posten stillschweigend nicht
  geliefert; die Lücke tarnte sich als saubere Diagnose. Aus demselben Grund wurden in M7 **sechs**
  Gates neu zugeschnitten, weil sie Sprachmittel späterer Slices verlangten.

## Letzter relevanter Commit

`M7: extend - P9c, Gate inventory.lyr, M7 abgeschlossen`

---

## Wie diese Datei zu pflegen ist

- Nach jedem Slice: `## Zuletzt fertig geworden` ergänzen, `## Woran wir gerade
  arbeiten` updaten.
- **Höchstens vier Einträge unter `## Zuletzt fertig geworden`.** Der fünfte
  fliegt raus — er steht in `git log`. Diese Regel gab es schon, sie wurde
  1088 Zeilen lang ignoriert.
- Bei Meilenstein-Wechsel: oben den neuen Meilenstein eintragen.
- Erledigte Punkte aus `## Noch offen` **löschen**, nicht durchstreichen.
- **Niemals** hier neue Features planen. Das ist `ROADMAP.md`-Territorium.
