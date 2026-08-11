# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M9 ist abgeschlossen und getaggt** (`m9-complete`, `v0.9.0`). **M8b — Stdlib-Erweiterung — läuft.**
S1 bis S8 plus die Erreichbarkeitsanalyse.

2562 Tests grün **in Debug und Release**, Bytecode-Format **2.5**, **vier** Binaries, Version **0.9.0**.

**Die Vorgabe für M8b**: *so viel wie möglich in Lyric selbst.* Nativ bleibt nur, was eine echte
Host-Grenze ist — stdin, Datei-I/O, Zeit, `sqrt`/`sin`/`cos`. Alles andere ist Lyric-Code:
`Map`, Merge Sort, der FNV-Hash für Strings, sämtliche Iterator-Adapter. Dass eine Stdlib sich
selbst tragen kann, ist die eigentliche Aussage dieses Meilensteins — und der schärfste Test der
Sprache, den es bisher gab: **zehn Compiler-Lücken** sind dabei aufgefallen, die kein
Meilenstein davor berührt hat.

**Offen für v1.0**: **M10 E3–E6** — `RegisterFunction`,
`RegisterType<T>`, `Reload`, dann Doku und Inventur. E1 und E2 stehen. `std.dotnet` ist gestrichen
(2026-08-11): eine Reflection-Brücke lässt das *Skript* entscheiden, was M10 dem *Host* gibt.

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **M10/E2 — `Call<T>` und die Skalar-Marshalling-Schicht** (2026-08-11). 2562 Tests grün.
  - **`ScriptInstance.Call<T>`/`CallVoid`**, dazu `Defines`. Die Matrix deckt alle vierzehn
    Skalartypen plus `string` hin und zurück ab — als Matrix und nicht als Beispielliste, weil bei
    `AgreementTests` vier Abstürze allesamt *durch Zufall* gefunden wurden.
  - **Verlustfrei oder gar nicht**: `300` als `int8` wird abgelehnt, nicht zu `44` gekürzt. Ein
    Bruch für ein `int` ebenso. Innerhalb von Lyric wickelt Arithmetik definiert um (§6.6) — das
    ist eine Rechnung des Programms und etwas anderes als eine stille Umdeutung an der Grenze.
  - **`Call` sitzt auf einer Instanz, nicht auf der VM** — Abweichung von der ROADMAP-Skizze. Sie
    las sich, als habe eine VM genau ein Skript; bei zwei müsste `Call` raten oder es gäbe ein
    implizites „aktuelles Skript". **Die Instanz ist der Zustand**: der Konstanten-Initialisierer
    läuft beim Erzeugen genau einmal, und zwei Instanzen desselben Moduls teilen nichts. Damit
    fällt ADR-025s Reload-Zusage in E5 von selbst heraus.
  - **`LoadedProgram` in der VM** trennt „laden und initialisieren" von „ausführen". `Run` ist
    jetzt eine Zeile darauf; die alte Fassung ist **gelöscht** und nicht als Zweitkopie
    stehengeblieben.
  - **Ein Fund über E1**: der Modulname des Hosts erreichte den Compiler nie. `ScriptSource`
    setzte nur den Anzeigenamen, die Identität blieb `main` — zwei Mods hätten beide `main`
    geheißen, und ein Aufruf über den Namen fände die Funktion des falschen. Gefunden vom
    **ersten** Test, der eine Funktion beim Namen rief.

- [x] **M10/E1 — `LangVm`: laden, ausfuehren, sandboxen** (2026-08-11). 2519 Tests grün.
  - **Neue Assembly `Lyric.Embedding` → `lyrembed.dll`**, das zweite Artefakt mit *beiden*
    Bibliotheken nach `lyrrepl`. Sie **muss** eigenständig sein: läge `LangVm` in `Lyric.Vm` — wie
    die ROADMAP es bis heute behauptete —, müsste `lyrrt` das Frontend referenzieren, und der
    Architektur-Test, der `lyrvm.exe` ohne `lyrfe.dll` und ohne `stdlib/` festhält, fiele.
    **Die ROADMAP-Zeile ist korrigiert.**
  - **Die Voreinstellung ist Sandbox** (`Capability.None`, keine Ausgabe). Ein Host, der
    versehentlich Dateizugriff bekommt, merkt es nie — es funktioniert ja. Der umgekehrte Fehler
    meldet sich sofort. **Die Voreinstellung muss die sein, deren Verletzung laut ist.**
  - **`ScriptSource`**: die Pipeline war pfadgebunden, und das war keine Bequemlichkeit — §2.1
    leitet den Modulnamen aus dem Pfad ab. Ein Skript aus dem Speicher hat keinen, **also nennt
    ihn der Host**. Eine Naht, kein zweiter Weg: der Unterschied ist genau ein Schritt.
  - **Zwei Befunde, beide erst durchs Bauen gefunden:**
  - Die Runtime-Ausnahmen mussten an der Host-Grenze **übersetzt** werden. `LyricPanic` und
    `LyricRuntimeException` leben in `lyrrt`, das ein Host nicht referenziert — ihm bliebe
    `catch (Exception)`. Aufgefallen ist es, weil das Testprojekt bewusst **nur** `lyrembed`
    referenziert; mit einer Referenz mehr wäre der Test grün und die Lücke unsichtbar geblieben.
  - Der Beispiel-Host musste **in die Solution**. Ohne ihn dort baut `dotnet test` ihn kalt nicht
    — vier Tests lokal grün, kalt rot. **Dieselbe Fehlerklasse wie `build/publish.proj`**: es
    funktioniert auf dem Rechner, auf dem es entstanden ist, und der kanonische Pfad kennt es
    nicht.
  - Das Gate ist `examples/embedded-host/`: eine gesandboxte VM läuft, eine abgelehnte Capability
    stoppt **vor der ersten Zeile**, eine zweite VM daneben darf mehr, ein Übersetzungsfehler
    kommt als Diagnose zurück.

- [x] **Die vier Compiler-Lücken aus S9 sind zu** (2026-08-11). 2504 Tests grün.
  - **Eine benannte Funktion ist jetzt ein Funktionswert**: `map(o, verdoppeln)` statt
    `map(o, (n: int) => verdoppeln(n))`. Der Fix brauchte **weder Instruktion noch Opcode** —
    `MakeClosure` nimmt sein Environment seit P6 optional, und die VM entscheidet am
    `HasEnvironment`-Bit, ob Slot 0 belegt wird. Eine Funktion ohne Captures ist eine Closure ohne
    Umgebung; es fehlte nur die eine Stelle im Lowering. Eine **generische** Funktion bleibt
    abgelehnt, mit Begründung in der Meldung: ihre Typargumente hätten keine Aufrufstelle.
  - **Ein Lambda in einer f-String-Interpolation parst.** Der Lexer zählte `{ }`, aber nicht
    `( )` und `[ ]` — deshalb las er das `:` in `(n: int)` als Format-Spec-Trenner. Der Gegentest
    ist der wichtigere: eine Spec auf oberster Ebene muss weiterhin trennen, sonst wäre der Fix
    grün und hätte jede Format-Spec der Sprache stillgelegt.
  - **`panic` divergiert jetzt auch importiert — und meine Diagnose war falsch.** Notiert war
    „`never` ist für die Flussanalyse unsichtbar". Gemessen war es das **nicht**:
    `Flow.AlwaysReturns` behandelt einen divergierenden `ExprStmt` seit jeher. Die Ursache war,
    dass es `panic` **zweimal** gibt — als Builtin im Wurzel-Scope und als native Deklaration in
    `std.core` —, und nur das Builtin trug `never`. Wer `import std.core { panic }` schrieb, bekam
    ein `void`. Zwei Symbole, eine Bedeutung, eine Antwort. Die Umgehung in `std.option.expect`
    (`return o!;` nach dem Panic) ist zurückgebaut.
  - **Ein `null`-Zweig macht den anderen optional** — und der `match` hatte eine **zweite**
    Unifikation, die den Fall ebenfalls nicht kannte. Gemeldet war nur der `if`-Fall; der andere
    fiel beim Nachmessen des Fixes auf. Die Regel liegt jetzt in einer Funktion, die beide rufen.
    Dazu die Lowering-Seite: `LowerIfExpr` lowerte seine Zweige **ohne** erwarteten Typ, also
    scheiterte das `null` dort erneut — derselbe Sema/Backend-Riss wie schon elfmal.

- [x] **M8b/S9 — `std.option`, drei Abbruch-Funktionen, `Exception`. `std.error` und
  `std.coroutine` sind gestrichen** (2026-08-10). 2486 Tests grün.
  - **`std.option`**: `map`, `andThen`, `filter`, `zip`, `contains`, `toArray`, `iter`, `expect` —
    alles in Lyric, freie Funktionen über `?T`. **Kein Typ `Option<T>`**: `?T` *ist* er (§4).
  - **Vier Namen aus `Doku.md` §22 fallen weg, weil die Sprache sie schon hat**: `unwrap` ist `!`,
    `unwrapOr` ist `??` (und `??` lowert zu einer Verzweigung, ist also bereits faul),
    `isSome`/`isNone` sind `!= null`/`== null`. Der letzte Fall ist nicht bloß Redundanz, sondern
    **schädlich**: am Vergleich hängt das Flow-Narrowing, eine Funktion schnitte es ab.
  - **`flatten` ist nicht formulierbar.** `??int` ist ein Parser-Fehler, `?(?int)` ist
    `LYR-IR0001: optionals do not nest`. Die Funktion hat in Lyric keinen Eingabetyp — die Zeile
    in §22 beschrieb eine andere Sprache. `andThen` ist der Ersatz: weil `f` schon `?U` liefert,
    entsteht die Verschachtelung gar nicht erst.
  - **`std.core` bekommt `assert`, `todo`, `unreachable`** — von §22 seit M8 versprochen, nie
    gebaut — und **`Exception`**.
  - **`std.error` gibt es nicht.** Übrig blieb genau eine Klasse; ein Modul dafür trägt seinen
    Namen nicht. `NullDereferenceError` und `CoroutineEndedError` entstehen nicht: beide Fälle
    bleiben `panic`. Gemessen, was die Alternative kostet — die Stdlib hat **null**
    `throws`-Deklarationen, **ein** Force-Unwrap und **124** Stellen mit Division oder Index.
    Geprüftes `throws` propagiert transitiv, also müssten `Ordered`, `Hashable` und `Display` als
    Interfaces werfen. Der Preis trifft die ganze Bibliothek, der Nutzen eine Zeile.
  - **`std.coroutine` gibt es nicht**, und der Grund ist verwertbar: die Brücke
    `Coroutine<T>` → `Iterator<T>` ist nicht schreibbar, solange das Ende ein Panic ist. Der
    `resume`-Aufruf, der den Rumpf durchlaufen lässt, ist **derselbe**, der daran stirbt — ein
    „frag vorher"-Prädikat käme immer genau ein `resume` zu spät. Der Umbau (Sprungverteiler
    liefert intern `?T`, `resume` packt aus und behält seinen Vertrag) ist Lowering-Arbeit in der
    Größenordnung eines P-Slices. Nicht abgelehnt, nur nicht hier.
  - **`Sprache.md` §7 und §8 haben gelogen** und sind korrigiert: `expr!` „wirft
    `NullDereferenceError`" und `resume` „wirft `CoroutineEndedError`" — beides panict, und keiner
    der Typen existierte je.

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

**M8b ist inhaltlich durch.** `Set<T>`, `std.string`, `std.math`, `std.fmt`, `std.io.file`,
`std.os` und zuletzt `std.option` stehen (S1–S9); `std.error` und `std.coroutine` sind mit
Begründung gestrichen statt geliefert.

**M10 läuft.** E1 und E2 stehen (`LangVm`, Capabilities, `Call<T>`, Marshalling); als Nächstes
**E3** — `RegisterFunction`. Der Seam dafür ist seit M6 da: Natives werden **beim Laden über den
Namen** gebunden. Die offene Frage ist die Signatur — der Compiler kennt nur, was deklariert ist,
also muss `RegisterFunction` die Deklaration in ein synthetisches Host-Modul **erzeugen**. `Doku.md`
§21 zeigt ein Skript, das `playSound("hit")` ohne Import ruft; das ist so nicht baubar.

**Die offene Frage, die vor E4 zu beantworten ist**: Lebenszeit und Identität eines Host-Objekts
über die Grenze — hält der Host es am Leben oder die VM? Das ist die einzige Stelle in M10, an der
ich noch keine Antwort habe, und sie gehört gestellt, bevor E4 anfängt.

**Die Erreichbarkeitsanalyse ist da** — vorgezogen, weil `std.string` den Effekt zum ersten Mal
schmerzhaft sichtbar gemacht hat: zwei Tests, die „ein Hello-World trägt keine String-Maschinerie"
festhielten, wurden falsch. Sie grün zu schreiben hätte geheißen, eine Zusage aufzugeben statt sie
einzulösen.

**Danach M10**, die Embedding-API: `Lyric.Embedding.LangVm` mit
`RegisterFunction`/`RegisterType`, die bidirektionale Marshalling-Schicht zwischen Lyric-Werten
und .NET-Objekten, `Reload` fuer Hot-Reload, und ein Beispiel-Host in C#.

Die Capabilities aus M8/S6 sind dafuer die halbe Miete: der Host konfiguriert beim Erzeugen der
VM, was ein Skript anfassen darf, und die Durchsetzung liegt bereits beim Laden. `std.dotnet`
gehoert in denselben Slice — es ist Interop und teilt sich die Marshalling-Schicht.

**Der `v0.9.0`-Tag ist gesetzt** (annotiert, die Message ist die Release-Notiz — CONTRIBUTING
§Releases, kein `CHANGELOG.md` vor v1.0), dazu `m9-complete`. Beide zeigen auf den **ersten Stand,
auf dem alle drei CI-Jobs grün sind** — nicht auf „M9: Politur" vom 2026-08-07, wo `dotnet test`
in Release rot war und die Auslieferung nicht baute. Einen Tag dorthin zu setzen wäre das „done by
intent alone", das Rule 3 verbietet.

Sie standen kurzzeitig einen Commit früher und sind verschoben worden: dort waren die Tests grün,
der Publish-Job aber rot. **Ein Tag ist das Einzige, was sich nicht stillschweigend nachbessern
lässt** — deshalb war das Verschieben richtig und wäre es eine Woche später nicht mehr gewesen.

**Weiterhin ungetaggt: `m5-complete`, `m7-complete`, `m8-complete` und `v0.5.0`.** Rule 3 verlangt
sie, die Meilensteine sind fertig, die Tags fehlen — bewusst offen gelassen, weil sie auf die
damaligen Commits gehören und das eine eigene Entscheidung ist.

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
- **`Opt<int>.Some(5)` ist nicht ausdrückbar.** Scheitert im **Parser** (`LYR-PAR0002: expected
  an expression, got Dot`) — `Opt<int>` wird in Wert-Position nicht als Typpfad gelesen. *Stand
  bis 2026-08-07 zusammen mit dem M4-Constraint-Rest in einem Punkt; sie hängen nicht zusammen,
  der Constraint-Teil ist erledigt und dieser nicht.* Auch eine *statische Methode* auf einer
  generischen Instanz bleibt `LYR-SEM0052`; explizite Typargumente gibt es nur an
  Funktions-Aufrufen.
- **`@noCapture` wird nicht durchgesetzt** — Lambda-Parameter tragen keine Attribute im AST.
- **`do { return … } while (…)` laesst den Compiler abstuerzen.** Rumpf, Bedingung und Ausgang
  werden alle drei vorab angelegt; terminiert der Rumpf, sind Bedingung und Ausgang unerreichbar,
  und der Verifier lehnt ab. Der Fix braucht einen Umbau, weil `_loops.Push` die Sprungziele
  vorab braucht — ein bedarfsgesteuerter Ausgang muesste wissen, ob ein `break` ihn benutzt.
  *Bewusst offen gelassen*: eine Schleife, deren Rumpf immer terminiert, schleift nie; die Form
  ist toter Code. Gefunden im Merge-Block-Sweep 2026-08-07.
- **`DeclaredTypes.Lower` wirft ungefangen** aus `ModuleLowerer.Lower` heraus: eine native
  Signatur mit einem unbekannten Typ gibt einen Compiler-*Absturz* statt einer Diagnose. Gefunden
  beim Bau von S2, als `split` noch nicht lowerbar war.

- **Ein Block-Lambda liefert seinen Rückgabetyp nicht an die Inferenz**: `(n: int) => n` bindet
  `U`, `(n: int) => { return n; }` nicht. *Keine Lücke, sondern eine dokumentierte Grenze* —
  `LYR-SEM0046` sagt es und schlägt die Annotation vor, und die funktioniert. Steht hier, weil ich
  sie am 2026-08-08 fälschlich als Bug gemeldet habe.

- **`?T[] ?? []`** und **`size`** sind erledigt (M8b/S8).

- **Interface-Vererbung gibt es nicht** (`interface A :: [B]` ist ein Parser-Fehler; die
  Grammatik sieht für `InterfaceDecl` keine Konformanzliste vor). Aufgefallen beim Bau von
  ADR-024, das sie voraussetzte. Ob v1 sie braucht, ist offen — `Hashable` bräuchte sie nur, um
  `Equatable` zu implizieren.
- **`string < string` und `==` auf Nutzertypen sind abgelehnt** (`LYR-SEM0003` / `LYR-SEM0055`).
  Bewusst und vorübergehend: Operator-Overloading ist das erste Thema nach v1.0 (v1.4), und die
  Diagnose zeigt darauf. Bis dahin eine gewöhnliche Methode.

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

`M10: Call<T> und die Skalar-Marshalling-Schicht (E2)`

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
