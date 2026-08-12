# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M0–M10 sind abgeschlossen und getaggt** (`m0`–`m10-complete`, `v0.1.0`/`v0.5.0`/`v0.9.0`).
**v1.0 ist noch nicht erreicht** — was fehlt, steht unter `## Was v1.0 noch fehlt`.

2675 Tests grün **in Debug und Release**, Bytecode-Format **3.0**, **vier** Binaries plus
`lyrembed.dll`, Version **0.9.0**.

**Was der Stand kann**: die ganze Sprache aus `Sprache.md` übersetzt und läuft; eine
Standardbibliothek, die sich weitgehend selbst trägt (`Map`, `Set`, Merge Sort, sämtliche
Iterator-Adapter und der String-Hash sind in Lyric geschrieben); vier Werkzeuge samt REPL; eine
VS-Code-Extension; und eine Embedding-API, mit der ein C#-Host Skripte lädt, sandboxt, Funktionen
daraus ruft und eigene Funktionen und Typen hineinreicht.

> **Die Datei war bis 2026-08-07 auf 1088 Zeilen gewachsen** und widersprach sich an drei Stellen
> selbst. Sie ist auf ihre eigene Pflegeregel zurückgeschnitten: letzte Slices, offene Punkte,
> Design-Kontext. Alles andere steht in `git log`.

## Zuletzt fertig geworden

- [x] **`Pair<int>.of(3)` geht** (2026-08-12) — eine statische Fabrik auf einem generischen Typ.
  - Der Parser las `Pair` als Bezeichner und `<` als Vergleich; danach stolperte er über den
    Punkt. **Die Erkennung kostet keine Mehrdeutigkeit**: das `<` gilt als Typargument-Liste, wenn
    es balanciert schließt und ein `.` folgt — ein Punkt hinter einer Vergleichskette ist ohnehin
    kein gültiger Ausdruck. Dieselbe Regel wie §6.1 sie seit 2026-08-07 für `f<int>()` zieht;
    Rusts `::<>` wäre ein zweiter Mechanismus für dasselbe Konzept.
  - **Die Sema war die eigentliche Lücke.** `MemberOfType` lieferte den Typ des Members
    unsubstituiert — daher kam „cannot assign 'int' to 'T'", eine Meldung über die Folge. Jetzt
    trägt `NonValueType` die aufgelöste Instanz, und ohne Argumente gibt es `LYR-SEM0063`, das die
    Ursache nennt: §6.2 verlangt sie ausdrücklich, `Pair.of(3)` inferiert nicht.
  - Im Lowering gefunden: `InstanceTable.RequestMethod` hängte auch einer **statischen** Methode
    ein `this` an (ADR-014). Der Verifier sah „passes 1 arg(s), expected 2".
  - **`std.collections` trug den Beleg als Kommentar** — `emptyList` ist eine freie Funktion,
    „weil eine statische Methode auf einer generischen Instanz nicht ausdrückbar ist". Der Satz
    stimmt nicht mehr und steht jetzt richtig da; die Funktion bleibt, weil der Umbau jeden
    Aufrufer kostet und nichts bringt.
  - **`Opt<int>.Some(5)` ist damit NICHT erledigt** und war nie derselbe Posten: das Lowering
    kennt generische Enums überhaupt nicht (`TypeTable.InternEnum` wirft `LYR-IR0001`, schon wenn
    eins nur als Parametertyp vorkommt). Gemessen, nicht vermutet — siehe `## Noch offen`.
  - 24 neue Tests, davon 9 Parser-Gegenproben (`a < b > c.d` bleibt ein Vergleich) und eine, die
    `lyrc ast` absichert: der `AstDumper` wirft bei jedem Knoten, den er nicht kennt.

- [x] **`List<T>.clear()` und `.toArray()`** (2026-08-12, vom Maintainer angelegt).
  - `toArray` ist der interessantere Teil: Rückgabe `T[]`, Backing `(?T)[]`, und **zwischen beiden
    gibt es keine Umdeutung**. `!` packt einen einzelnen Wert aus, nicht ein Array elementweise —
    `?T[]` ist ein *Array von Optionals* und kein optionales Array. Die erste Fassung versuchte
    `return result!;` und war `LYR-SEM0005`.
  - Gebaut wird jetzt von Anfang an als `T[]`. Die leere Liste hat kein erstes Element, aus dem
    sich eins bauen ließe, und wird vorher abgefangen (`return [];`, seit M8b/S8).
  - Sieben Tests, darunter: die Länge ist `count` und nicht `capacity` (derselbe Fehler, den `get`
    schon einmal gemacht hat), die Kopie ist eine Kopie, und `clear` gibt das Backing wirklich
    frei statt es hinter `count` stehen zu lassen.

- [x] **`b?.get()` geht** (2026-08-12). 2661 Tests grün, Debug und Release.
  - Die Sema machte aus `?.get` ein `?fn() -> int` und meldete dann `LYR-SEM0013: not callable` —
    eine Auskunft über einen Zwischentyp, den niemand hingeschrieben hat. Jetzt packt `CheckCall`
    den Empfänger aus, wenn der Callee ein `?.`-Glied ist, und legt das Optional um das
    *Ergebnis*. Der Ausweg (`if (b != null) { b.get() }`) war dreimal so lang.
  - **Der Aufruf läuft durch dieselbe Auflösung wie jeder andere.** Alle fünf Dispatch-Wege
    tragen ihn ohne eine Zeile Zusatzcode: konkrete Klasse, generische Instanz, Interface
    (dynamisch), Constraint-Typparameter und primitiver Empfänger mit Extension. Ein zweiter Pfad
    hätte jeden davon ein weiteres Mal beantworten müssen — die Sorte Zweitkopie, die in diesem
    Projekt neunmal auseinandergelaufen ist.
  - **Der erste Anlauf war genau diese Zweitkopie**, nur getarnt: ein Sonderfall im
    Callee-`switch`, der den ausgepackten Empfänger anhängte. Er stand **vor** der Generics- und
    der Interface-Erkennung und verdeckte sie — `b?.get()` auf einem `Box<int>` wurde zu
    *„external or bodiless"*, eine Diagnose auf die falsche Ursache. Aufgefallen nur, weil ich
    danach gefragt habe statt es anzunehmen.
  - Jetzt hängen die zwei Abweichungen **am AST-Knoten**: der ausgepackte Empfänger am Ziel, der
    Rückgabetyp am Aufruf. Die Fallunterscheidung fragt den Empfängertyp über eine Stelle, die
    in der Kette auspackt. Als Parameter hätte es vier weitere Signaturen gekostet, die keine
    davon interessiert.
  - **Dabei fiel eine ältere Unstimmigkeit auf**: `b?.w` auf ein Feld `w: ?int` ergab `??int`,
    und der Fehler kam eine Ebene zu spät als „cannot assign '?int' to 'int'". Optionals
    verschachteln nicht (§4) — beide Stellen kollabieren jetzt, Sema *und* Lowering. Wieder
    **eine Frage, zwei Stellen**; diesmal beide beim ersten Anlauf gefunden, weil der
    Verifier-Befund (`call dest t61 is i64 but Box.leer returns ?i64`) direkt darauf zeigte.
  - Ein leerer Empfänger wertet **die Argumente nicht aus**. Der Test misst das mit einem
    Seiteneffekt; ohne ihn bliebe er grün, wenn sie vor der Prüfung berechnet würden.
  - **Wo es aufhört, sagt es das** (`LYR-SEM0062`): hält das Glied einen Funktions-*Wert*
    (`f: fn() -> int`), gibt es zwei Fragen und ein `?` — ob der Empfänger da ist und ob das Feld
    belegt ist. Wer dort auspackte, beantwortete die zweite stillschweigend mit ja; bei
    `f: ?fn() -> int` ist das ein Aufruf auf null. Die Meldung nennt den Ausweg, und ein Test
    prüft, dass der Ausweg compiliert — sonst wäre sie ein Hinweis ins Leere.
  - §7 in `Sprache.md` und das Nullable-Kapitel der `Doku.md` sagen die Aufruf-Form jetzt an.

- [x] **`s = Small { n = 5 };` geht** (2026-08-11). 2675 Tests grün.
  - §6.2 erlaubt den Ausdruck „in jeder Wert-Position", und die rechte Seite einer Zuweisung ist
    eine. `ParseExprStmt` schaltete die Mehrdeutigkeits-Sperre aber für die **ganze** Anweisung
    ab — sie gilt dem *Anfang*, weil dort ein Block stehen könnte. Hinter einem `=` kann keiner
    stehen.
  - **Die Meldung war das eigentliche Ärgernis**: `'Small' is a type, not a value — did you mean
    'Small { . }'?` schlug genau das vor, was dort schon stand. Bekannt seit P3, und am
    2026-08-07 ist der Maintainer beim Schreiben einer Messprobe erneut hineingelaufen, ohne ihn
    wiederzuerkennen.
  - **Die Gegenprobe ist die wichtigere Hälfte**: am Statement-Anfang bleibt es gesperrt, ein
    Block bleibt ein Block, und `c = a < b` bleibt ein Vergleich. Ein Fix, der die Sperre ganz
    entfernte, kostete keine Diagnose, sondern eine falsche Deutung.
  - **`Opt<int>.Some(5)` ist *nicht* dieselbe Ursache** — gemessen, nicht vermutet. Ich hatte
    beide als einen Posten geschätzt; der Fix hier hat dort nichts bewegt. Der Aufwand steht
    korrigiert unter `## Noch offen`.

- [x] **Zwei Diagnosen, die auf die falsche Ursache zeigten** (2026-08-11). 2668 Tests grün.
  - **Ein Attribut an einem Parameter** wurde als Parametername gelesen; danach fehlte der Rumpf,
    und der Compiler sprach von *nativen Deklarationen* — zu jemandem, der `@noCapture` schreiben
    wollte. Jetzt dieselbe Meldung wie an einer Deklaration (`LYR-PAR0038`, §10), und der Rumpf
    bleibt erhalten: ein Test prüft, dass es bei **einer** Meldung bleibt.
  - **`interface B :: [A]`** lief in eine Meldung über Parameter-Klammern. Jetzt `LYR-PAR0039`,
    und sie nennt den Ausweg, weil es einen gibt: `std.core` löst dasselbe mit zwei Constraints
    nebeneinander (ADR-024). Die Konformanzliste wird gelesen und verworfen — **eine Diagnose je
    Ursache**, sonst stolperte der Parser gleich noch einmal über `[A]`.
  - Beides kostet keine Ausdrucksstärke: beide Formen bleiben abgelehnt. Es kostete Zeit — eine
    Diagnose, die auf die falsche Stelle zeigt, ist teurer als gar keine, weil man dort sucht.
  - Die Gegenprobe steht daneben: `class K :: [A]` bleibt gültig. Ohne sie wäre die halbe Stdlib
    ein Syntaxfehler, und der Test wäre trotzdem grün.

- [x] **Die Konformanz prüft ihre Typargumente** (2026-08-11). 2656 Tests grün.
  - **Der ernsteste Befund dieser Arbeit, und er stand als Lässlichkeit in dieser Datei.** Bis
    heute verglich die Konformanz nur das Interface-*Symbol*: `class Ones :: [Src<int>]` erfüllte
    ein `<T :: [Src<string>]>`, und der Rumpf legte einen `i64` in einen `string`-Slot.
  - **In Debug fing es der Verifier. In Release — also in dem, was ausgeliefert wird — lief es
    durch** und lieferte eine stille falsche Antwort; der Bytecode-Loader fing es ebenfalls nicht.
    Kein fehlendes Feature, sondern ein Typprüfer, der ein Programm annimmt, dessen Typen nicht
    halten. Dass .NET den Schaden eindämmt (leerer String statt Speicherfehler), ist Glück der
    Wertdarstellung.
  - **Dieselbe Lücke saß an zwei Stellen**: beim Constraint *und* bei der Zuweisung an einen
    Interface-Typ — beide liefen über denselben Vergleich. Zum neunten Mal in diesem Projekt
    dasselbe Muster.
  - Die volle Substitutionsabbildung wird durchgereicht statt eines Parameters nach dem anderen:
    ein Constraint darf die übrigen Typ-Parameter nennen (`<K, V :: [Map<K, V>]>`), und
    `Eq<T>` ist erst mit `T := int` die Frage, die wirklich gestellt wird.
  - **Zehn Tests, beide Richtungen.** `Map<K :: [Hashable<K>, Equatable<K>]>` und `Iterator<T>`
    sind die schwersten Nutzer generischer Constraints in der Stdlib und blieben unberührt — ohne
    die Gegenproben wäre ein Fix, der zu viel ablehnt, nicht von einem richtigen zu unterscheiden.

- [x] **Die zwei Abstürze aus der v1.0-Liste** (2026-08-11). 2646 Tests grün.
  - **`do { return … } while (…)`** war ein Compiler-Absturz: Rumpf, Bedingung und Ausgang wurden
    alle drei vorab angelegt, und terminierte der Rumpf, blieben zwei Blöcke ohne Prädecessoren —
    was der Verifier ablehnt, weil es keinen `SimplifyCfg`-Pass gibt. Sie entstehen jetzt
    **bedarfsgesteuert**.
  - **Die Falle war die Bedingung, unter der man das entscheidet.** STATUS beschrieb den Fall
    lange als „der Rumpf terminiert" — daran lässt er sich nicht festmachen:
    `do { if (c) { break; } return 2; }` fällt **nicht** durch und erreicht den Ausgang trotzdem.
    *Ist der Block erreichbar* und *fällt der Rumpf durch* sind zwei Fragen; nur die erste zählt.
    Ein Test steht genau dafür da, und ein zu einfacher Fix wäre mit dem ersten grün und mit ihm
    rot.
  - **Dieselbe Lösung zum dritten Mal**: der Merge-Block von `match` (Inventur-Sweep) und der von
    `try` (M8/S4) waren derselbe Fehler. Die Lehre stand schon 2026-08-07 in dieser Datei — *ein
    Merge-Block gehört grundsätzlich bedarfsgesteuert*. `do-while` war der dritte Fall, und
    niemand hatte ihn daraufhin angesehen.
  - **Der zweite „Absturz" war keiner mehr.** `DeclaredTypes.Lower` liefert längst eine Diagnose
    mit Position — auf dem Import-Pfad wie auf dem Host-Methoden-Pfad, beides nachgemessen. Der
    STATUS-Eintrag war veraltet; ich hatte ihn im letzten Bericht ungeprüft als blockierend geführt.

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

**M10 läuft.** E1–E3 stehen: `LangVm`, Capabilities, `Call<T>`, Marshalling, `RegisterFunction`.
Ein Host kann heute ein Skript laden, sandboxen, Funktionen daraus rufen und eigene hineinreichen.

**M10 ist abgeschlossen**, Inventur inklusive. **v1.0 ist es nicht** — was fehlt, steht unten
unter `## Was v1.0 noch fehlt`: vier Meilenstein-Tags, ein `CHANGELOG.md`, plattformspezifische
Binaries, eine Doku-Site, und die Entscheidung, welche der offenen Sprachlücken v1 blockieren.

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

## Was v1.0 noch fehlt

**M0–M10 sind inhaltlich fertig.** Das Release ist es nicht, und die Liste ist kurz genug, um sie
Punkt für Punkt abzuarbeiten — nach der Regel, an der M9 gescheitert ist.

**Prozess (CONTRIBUTING):**

- ~~Meilenstein-Tags~~ **erledigt** (2026-08-11): `m5-complete`, `m7-complete`, `m8-complete`,
  `m10-complete` und `v0.5.0` sind nachgezogen. Sie zeigen auf die **historischen**
  Abschluss-Commits, nicht auf HEAD — ein Tag markiert, wann ein Meilenstein fertig war, und ihn
  ans Ende zu hängen machte die Historie unbrauchbar. (`m9-complete` ist die begründete Ausnahme:
  er wurde bewusst verschoben, weil M9 zu dem Zeitpunkt inhaltlich *nicht* fertig war.) Rule 3 ist
  damit für M0–M10 erfüllt.
- **`CHANGELOG.md`** — §Releases: *„From `v1.0.0` on: tag, GitHub release page, and a
  `CHANGELOG.md` entry."* Vor v1.0 gab es bewusst keinen; ab v1.0 gibt es ihn.
- **GitHub-Release-Seite** zum `v1.0.0`-Tag.

**Artefakt (ROADMAP §v1.0):**

- **Binaries für Windows/Linux/macOS** via `dotnet publish -r …`. `publish.proj` liefert heute
  **framework-abhängig** und ohne RID-Matrix — es braucht eine .NET-10-Laufzeit auf der Zielmaschine.
- **Doku-Site** (statisches HTML aus den Docs). Es gibt keine.

**Die zwei Abstürze sind behoben** (2026-08-11). Was unter `## Noch offen` bleibt, sind Grenzen
**mit Diagnose** (`Opt<int>.Some(5)`, `@noCapture`, Interface-Vererbung) — sie kosten Ausdrucksstärke, keinen Absturz. **Ob sie v1 blockieren, ist
eine Entscheidung und keine Messung.**

## Noch offen

**Aus dem M10-Plan, beim Messen gefunden:**

- **Der Member-Trenner aus §3.2 ist für Block-Rümpfe geschrieben.** Eine bodylose Methode in einer
  Klasse braucht `int;,` — Semikolon *und* Komma hintereinander. Es geht, aber es ist eine
  Schreibweise, die niemand errät.

**Sprachlücken, vor v1 zu schließen:**

- **Generische Enums gibt es im Lowering nicht** — `TypeTable.InternEnum` wirft `LYR-IR0001`,
  sobald ein `enum Opt<T>` auch nur als Parametertyp auftaucht; keine Variante muss dafür
  konstruiert werden. Damit ist `Opt<int>.Some(5)` **kein Syntax-Posten**: Parser und Sema tragen
  die Form seit dem 2026-08-12, das Lowering nicht. Was fehlt: Varianten-Layouts pro
  Instanziierung, Tags und das `match`/Pattern-Lowering unter Substitution. **Zwei bis drei
  Sessions, nicht ein Tag** — die Schätzung in dieser Datei war falsch, und zwar um den Faktor,
  der zwischen „Syntax" und „fehlendes Feature" liegt.
- **`static fn` in einem Enum-Rumpf parst nicht** — `LYR-PAR0008` („expected ')' after
  parameters") plus zwei Folgemeldungen, alle drei über etwas anderes als die Ursache. Am
  2026-08-12 beim Messen der Enum-Lücke aufgefallen. ~1 h.
- **Ein Block-Lambda liefert seinen Rückgabetyp nicht an die Inferenz**: `(n: int) => n` bindet
  `U`, `(n: int) => { return n; }` nicht. *Keine Lücke, sondern eine dokumentierte Grenze* —
  `LYR-SEM0046` sagt es und schlägt die Annotation vor, und die funktioniert. Steht hier, weil ich
  sie am 2026-08-08 fälschlich als Bug gemeldet habe.

- **`?T[] ?? []`** und **`size`** sind erledigt (M8b/S8).

- **Interface-Vererbung gibt es nicht** (`interface A :: [B]` ist `LYR-PAR0039` mit einer
  Meldung, die den Ausweg nennt). Aufgefallen beim Bau von ADR-024, das sie voraussetzte. Ob v1
  sie braucht, ist offen — `Hashable` bräuchte sie nur, um `Equatable` zu implizieren. Kein
  Programm ist ohne sie unschreibbar: `std.core` verlangt beides nebeneinander.
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

`parser: Struct-Init rechts vom '=' — die Sperre gilt dem Anfang`

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
