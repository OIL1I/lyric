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

Bytecode-Format **2.5**. 1629 Tests grün. Das Gate `examples/inventory.lyr` läuft: es belastet
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

- [x] **M8 — S4 — `catch (e)` ohne Typ.** Die letzte Luecke aus P5 ist zu. 1627 Tests gruen.
  - **`Throwable` war laengst da** — als Builtin-Interface mit synthetischem AST, und die Sema
    gab dem ungetypten `e` schon immer diesen Typ. Auch die Handler-Tabelle konnte catch-all seit
    P5 (`CatchType == null`). Gefehlt hat nur der **Slot**: er bekommt jetzt den Interface-Typ,
    den die Sema ohnehin vergibt.
  - **Im Slot liegt ein Fat Pointer, und bauen kann ihn nur die VM.** Welcher konkrete Typ
    geworfen wurde, steht erst zur Laufzeit fest — die VM fuehrt ihn im Frame ohnehin mit, weil
    der typisierte Catch dagegen vergleicht. Ohne ihn waere `e.message()` ein `callvirt` auf
    einen Wert, der seinen eigenen Typ nicht kennt (P3: ein Objekt traegt kein Typ-Tag).
  - **Ein typisierter Catch bekommt weiterhin die nackte Referenz.** Sein Slot hat den konkreten
    Typ, dort gehoert sie hin; ein Test haelt beide Faelle nebeneinander.
  - Der Dispatch-Test fuehrt **zwei** Werfer. Mit nur einem bliebe er auch gruen, wenn der Fat
    Pointer immer denselben Typindex truege — dieselbe Lehre wie bei den Interface-Tests aus P3.

- [x] **Bug: `try { return … } catch (…) { return … }` liess den Compiler abstuerzen.** Gefunden
  beim Bau von S4, aber **unabhaengig davon** — und es traf eine der haeufigsten Formen ueberhaupt.
  - Der Merge-Block wurde **unbedingt** angelegt, blieb ohne Praedecessoren und war vom Einstieg
    unerreichbar. Genau das lehnt der Verifier ab (kein `SimplifyCfg`-Pass in v1). Er entsteht
    jetzt erst, wenn ihn jemand erreicht; faellt niemand durch, meldet `LowerTry` das nach oben.
  - **Derselbe Fehler stand beim Statement-`match`** und wurde im Inventur-Sweep behoben. Hier
    ueberlebte er, weil kein Beispiel und kein Test try/catch mit zwei returnenden Zweigen
    benutzt hat. **Zweimal dieselbe Ursache heisst: der Merge-Block gehoert grundsaetzlich
    bedarfsgesteuert**, nicht an jeder Stelle einzeln nachgezogen.

- [x] **Sweep ueber die Merge-Bloecke** — 15 Konstrukte durchgemessen, bei denen alle Zweige
  terminieren. **13 waren sauber**, zwei nicht, und der schwerere Fund hatte mit Merge-Bloecken
  gar nichts zu tun.
  - **`defer` neben einem `return` in einem Zweig liess den Compiler abstuerzen.** Das Lowern
    eines defer-Rumpfes betritt einen Scope und pusht dabei auf genau den Stack, ueber den
    `EmitAllPendingDefers` gerade iteriert — .NET wirft „Collection was modified" mitten im
    Compiler. Behoben (Iteration ueber eine Kopie), mit einem zweiten Test, der die
    LIFO-Reihenfolge festhaelt: eine Kopie in der falschen Richtung waere sonst unbemerkt
    geblieben. **Die alltaeglichste Form ueberhaupt, und kein Test hatte beides zusammen** —
    obwohl P5 „defer an jedem Ausgang" ausdruecklich liefert.
  - **`do { return … } while (…)` bleibt offen** (siehe „Noch offen"). Der Fix braucht einen
    Umbau, und die Form ist tot: eine Schleife, deren Rumpf immer terminiert, schleift nie.
    Verglichen mit `try/catch` — sehr haeufig, deshalb in S4 sofort behoben — ist das eine
    andere Groessenordnung.
  - **`if/else` machte es laengst richtig** und war die Vorlage fuer den try/catch-Fix: erst
    pruefen, ob ueberhaupt jemand durchfaellt, dann den Block anlegen. `while`, `for-in`,
    `match`, if-Ausdruck, `&&`/`||`, `??`, `?.`, verschachteltes `try`, Coroutinen und
    `panic` in beiden Zweigen sind alle sauber.

- [x] **M8 — S3 — `std.fmt` und Format-Specs.** `f"{avg:N2}"` laeuft; **`examples/stats.lyr`
  ist damit gruen**, nachdem es seit M6 auf genau diese Zeile gewartet hat. 1621 Tests gruen.
  - **Die Spec-Sprache ist die von .NET und wird unveraendert durchgereicht** (`N2`, `F3`, `D5`,
    `X`, `E2`, `P1`), wie `Sprache.md` §2.2 es verlangt. Eine eigene Notation daneben waere ein
    zweiter Mechanismus fuer dieselbe Sache.
  - **Ohne Spec bleibt es bei den `fromXxx`-Wandlern.** Ein Format-Aufruf, der nur den Standard
    nachbaut, waere ein zweiter Weg zu demselben Ergebnis — ein Test haelt das fest.
  - **Immer invariant.** Eine Zahl, die unter deutscher Locale `1.234,57` und unter englischer
    `1,234.57` wird, ist kein Formatierungsdetail, sondern ein Programm, das sich je nach Rechner
    anders verhaelt. Dieselbe Entscheidung wie bei `toUpper`/`toLower` in S2.
  - **Eine ungueltige Spec ist ein `panic`**, kein stilles Ausweichen auf die
    Standarddarstellung: sie steht als Literal im Quelltext und haengt nicht von der Eingabe ab.
    `{x:Q9}` ist falsch geschrieben, nicht ungluecklich gelaufen — ein Fallback truege den
    Tippfehler bis in die Ausgabe.
  - **`Sprache.md` §2.2 ist korrigiert**: dort stand `{value:0>5}` als Beispiel fuer eine
    „.NET-analoge" Spec. Das ist Rust- bzw. Python-Notation und war nie .NET — die Zeile
    widersprach ihrer eigenen Ansage im selben Satz. Das Auffuellen uebernimmt jetzt die
    Breiten-Form (`{name:10}`, `{name:-10}`), die es fuer `string`, `bool` und `char` ohnehin
    braucht, weil .NET fuer die keine Standardformate kennt.
  - **`shapes.lyr` laeuft weiterhin nicht** — es braucht `std.math` (`sqrt`, `pi`), also S7. Das
    ist keine Format-Luecke, und der Slice endet hier.

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

**M8 — Stdlib.** S1 (Builtin-Konformanz), S2 (`string`), S3 (`std.fmt`) und S4 (`Throwable`)
stehen. Als naechstes **S5 — `std.collections`** mit `List<T>`, `Map<K,V>`, `Set<T>`.

**Vor S5 gehoert die Mutabilitaets-Entscheidung getroffen** (siehe „Noch offen"): `Indexable<T>`
hat einen `mut fn`-Setter, und dann stellt sich die Frage aus P2 zum zweiten Mal — diesmal an
einem Interface, das jeder Nutzertyp implementieren kann.

Danach **S6** (Capabilities, ADR-007), **S7** (`std.io.file`, `std.os`, `std.math` — daran haengt
`shapes.lyr`), **S8** (Gate: `wc`-Klon).

**`std.io.net` ist aus M8 gestrichen** und steht in der v1.X-Tabelle (Begruendung in der
ROADMAP): was ein blockierender Socket in einer Single-Thread-VM bedeutet, ist eine Entscheidung
ueber das Nebenlaeufigkeitsmodell und kein Nebenprodukt eines Stdlib-Meilensteins.

## Noch offen

**Sprachlücken, vor v1 zu schließen:**

- **`b?.get()` geht nicht** — Optional-Chaining mit *Methodenaufruf*. Die Sema macht `?.get` zu
  einem `?fn() -> int` und stolpert dann über das `()`. Feldzugriff (`b?.v`) funktioniert.
- **Die Konformanz prüft die Definition statt der Typargumente**: `Ones :: [Src<int>]` würde auch
  für `Src<string>` akzeptiert.
- **Parser: `s = Small { n = 5 };`** scheitert mit `LYR-PAR0016`, obwohl §6.2 den Ausdruck „in jeder
  Wert-Position" erlaubt — die Mehrdeutigkeits-Sperre gilt dem *Anfang* eines `ExprStmt`, greift
  aber auf die ganze Zuweisung durch. *(Bekannt seit P3 — und am 2026-08-07 beim Schreiben einer
  Messprobe erneut hineingelaufen, ohne ihn wiederzuerkennen. Er kostet real Zeit.)*
- **Generics-Rest aus M4**: Constraints mit eigenen Typ-Args über die Grenze substituieren;
  `Opt.Some(5)` typt ohne Instanz-Inferenz, `Opt<int>.Some(…)` ist per TypePath nicht ausdrückbar.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.
- **`char as int` ist kein erlaubter Cast** (`LYR-SEM0006`). Beim Schreiben der S2-Tests
  aufgefallen. Ob das gewollt ist, sagt §6.5 nicht eindeutig — ungeprüft gelassen, weil eine
  Cast-Regel eine Sprachentscheidung ist und kein Nebenprodukt.
- **`do { return … } while (…)` laesst den Compiler abstuerzen.** Rumpf, Bedingung und Ausgang
  werden alle drei vorab angelegt; terminiert der Rumpf, sind Bedingung und Ausgang unerreichbar,
  und der Verifier lehnt ab. Der Fix braucht einen Umbau, weil `_loops.Push` die Sprungziele
  vorab braucht — ein bedarfsgesteuerter Ausgang muesste wissen, ob ein `break` ihn benutzt.
  *Bewusst offen gelassen*: eine Schleife, deren Rumpf immer terminiert, schleift nie; die Form
  ist toter Code. Gefunden im Merge-Block-Sweep 2026-08-07.
- **`DeclaredTypes.Lower` wirft ungefangen** aus `ModuleLowerer.Lower` heraus: eine native
  Signatur mit einem unbekannten Typ gibt einen Compiler-*Absturz* statt einer Diagnose. Gefunden
  beim Bau von S2, als `split` noch nicht lowerbar war.

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

`sweep: defer neben return, Merge-Bloecke durchgemessen`

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
