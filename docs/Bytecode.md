# Lyric — `.lyrbc` Bytecode-Format v1.0

> Dieses Dokument ist **normativ** (ADR-013). Der C#-Serializer in `src/Lyric.Bytecode/` ist eine
> Implementierung dieser Spec, nicht ihre Definition. Ziel-Test: jemand kann allein aus diesem
> Dokument einen Disassembler oder eine zweite Runtime schreiben, ohne den C#-Code zu lesen.
>
> **Stabilität**: Bis Lyric v1.0 darf sich das Format inkompatibel ändern — Major-Version-Bump ohne
> Migrationspfad. Ein Stabilitätsversprechen gibt es erst ab v1.0.
>
> **Stand**: Format-Version **1.0**. Deckt den Sprachumfang ab, den das IR-Lowering heute erzeugt:
> Skalare, Locals, modulinterne Calls, strukturierter Kontrollfluss.

---

## 1. Grundregeln der Kodierung

| Sache | Regel |
|---|---|
| Fixbreiten-Ganzzahlen | **Little-Endian**, explizit — nicht die Byte-Reihenfolge der Host-Maschine |
| Variable Ganzzahlen | **LEB128**, unsigned, höchstens 10 Gruppen (64 Bit) |
| Strings | Länge in **Bytes** als LEB128, dann UTF-8 ohne BOM und ohne Nullterminator |
| Floats | **IEEE-754-Bitmuster**, little-endian (4 bzw. 8 Byte) — nicht der Dezimalwert |
| Bool | ein Byte, `0x00` = false, alles andere = true |

**LEB128 (unsigned)**: sieben Nutzbits je Byte, beginnend beim niedrigstwertigen. Bit 7 (`0x80`)
gesetzt heißt „ein weiteres Byte folgt". Ein Leser, der mehr als zehn Bytes liest, muss die Datei
ablehnen.

**Determinismus**: Derselbe Compiler-Input erzeugt **byte-identischen** Output. Dafür gilt:

- Der String-Pool steht in **Erst-Verwendungs-Reihenfolge** (nicht Hash-Reihenfolge).
- Sektionen erscheinen **höchstens einmal** und in **aufsteigender Id-Reihenfolge**.
- Keine Zeitstempel, keine absoluten Pfade.

---

## 2. Datei-Aufbau

```
magic            4 Byte    'L' 'Y' 'R' 'B'  (0x4C 0x59 0x52 0x42)
version.major    u16       little-endian
version.minor    u16       little-endian
sections         *         beliebig viele, siehe unten
```

Eine Sektion:

```
id               u8
byteLength       uleb128   Länge des Inhalts, ohne id und ohne dieses Feld
payload          byteLength Bytes
```

Ein Leser **muss** eine Sektion mit unbekannter Id überspringen (die Länge macht das möglich) und
**muss** eine Datei ablehnen, deren Sektions-Ids nicht streng aufsteigen.

**Versionierung**: Eine unbekannte **Major**-Version wird abgelehnt. Eine unbekannte **Minor** ist
zu tolerieren — neue Minor-Versionen dürfen nur überspringbare Sektionen hinzufügen.

### Sektions-Ids

| Id | Name | Pflicht | Inhalt |
|---|---|---|---|
| 1 | Capabilities | nein | `uleb128` Bitset |
| 2 | Strings | nein | Konstantenpool, **nur Strings** |
| 3 | Types | — | **reserviert**, wird in 1.0 nicht geschrieben |
| 4 | Imports | nein | Host-/Native-Funktionen |
| 5 | Functions | nein | definierte Funktionen samt Code |
| 6 | SourceMap | nein | optional und **strippbar**: PC → Datei/Zeile |

Fehlt eine Sektion, gilt sie als leer.

**Warum nur Strings im Konstantenpool**: Zahlen sind als LEB128-Immediate nicht größer als ein
Pool-Index und sparen die Indirektion. Der Pool hat damit genau eine Aufgabe.

**Warum Id 3 reserviert ist**: Skalare Typen sind ein Byte (§3) und brauchen keine Tabelle.
Zusammengesetzte Typen (struct/class/enum) brauchen später eine — die Id jetzt freizuhalten
verhindert, dass ihre Einführung die bestehenden verschiebt.

### Capabilities (Id 1)

Ein `uleb128`-Bitset. In Format 1.0 immer `0` = „verlangt nichts". Die Zuordnung einzelner Bits zu
den Capability-Stufen aus ADR-007 (`fileAccess`, `networkAccess`, `osAccess`, `hostAccess`) entsteht
mit der Stdlib; bis dahin darf ein Leser ein Bitset ungleich 0 ablehnen.

### Strings (Id 2)

```
count            uleb128
values           count × String
```

### Imports (Id 4)

```
count            uleb128
entries          count × {
                   name         String
                   paramCount   uleb128
                   paramTypes   paramCount × TypeTag
                   returnType   TypeTag
                 }
```

Host-Funktionen werden **symbolisch** referenziert (Name + Signatur), nicht über Adressen; die
Bindung an konkrete Implementierungen macht der Host beim Laden. In Format 1.0 ist die Tabelle
immer leer — das Lowering kennt noch keine externen Calls.

### Functions (Id 5)

```
count            uleb128
entries          count × {
                   nameIndex    uleb128   Index in den String-Pool
                   paramCount   uleb128
                   returnType   TypeTag
                   slotCount    uleb128
                   slotTypes    slotCount × TypeTag
                   maxStack     uleb128
                   blockCount   uleb128
                   blockOffsets blockCount × uleb128   Byte-Offset in 'code'
                   codeLength   uleb128
                   code         codeLength Bytes
                 }
```

- Die **ersten `paramCount` Slots sind die Parameter**, in Deklarations-Reihenfolge. Es gilt
  `paramCount ≤ slotCount`.
- `maxStack` ist die maximale Tiefe des Operanden-Stacks in dieser Funktion. Eine Runtime darf
  ihren Frame danach dimensionieren und zur Laufzeit auf Überlauf-Prüfungen verzichten.
- `blockOffsets[i]` ist der Byte-Offset von Block `i` in `code`. Jeder Offset **muss** auf einer
  Instruktionsgrenze liegen.

---

## 3. Typ-Tags

Ein Byte.

| Tag | Typ | | Tag | Typ |
|---|---|---|---|---|
| `0x01` | `i8` | | `0x08` | `u64` |
| `0x02` | `i16` | | `0x09` | `f32` |
| `0x03` | `i32` | | `0x0A` | `f64` |
| `0x04` | `i64` | | `0x0B` | `bool` |
| `0x05` | `u8` | | `0x0C` | `char` |
| `0x06` | `u16` | | `0x0D` | `string` |
| `0x07` | `u32` | | `0x0E` | `void` |

Werte ab `0x40` sind für zusammengesetzte Typen reserviert. `void` ist ausschließlich als
Rückgabetyp gültig, nie als Slot- oder Wert-Typ.

Lyrics `int`/`uint`/`float` sind Aliasse für `i64`/`u64`/`f64` und erscheinen im Bytecode als solche.

---

## 4. Ausführungsmodell

Eine **Stack-Maschine** mit zwei getrennten Speichern je Aufruf:

- **Local-Slots**: indiziert, typisiert, beliebig les- und schreibbar.
- **Operanden-Stack**: die Instruktionen arbeiten darauf.

### Die tragende Invariante

> **Der Operanden-Stack ist an jeder Blockgrenze leer.**

Werte, die Blöcke überqueren, laufen durch Local-Slots. Daraus folgt:

- Die Stack-Tiefe an jedem Punkt ist **statisch** bestimmbar, ohne Datenflussanalyse.
- Ein Leser kann sie beim Laden prüfen und danach jede Laufzeitprüfung weglassen — das ist ADR-013s
  „Validierung beim Load statt beim Call".
- Sprünge brauchen keine Stack-Angleichung, weil an beiden Enden die Tiefe 0 ist.

### Sprungziele

Sprünge nennen **Block-Indizes**, keine Byte-Offsets. Der Funktionskopf trägt die Offset-Tabelle.
Ein Ziel prüft man mit `index < blockCount` — kein Abgleich gegen Instruktionsgrenzen nötig.

---

## 5. Instruktionen

Jede Instruktion beginnt mit einem Opcode-Byte. `T` steht für ein Typ-Tag-Byte (§3).

**Der Typ steht als Tag am Opcode, nicht im Opcode.** Bei zehn numerischen Typen ergäbe eine
Spezialisierung wie in der JVM (`iadd`/`ladd`/`fadd`/…) rund hundert Arithmetik-Opcodes; die
Tabelle bliebe nicht mehr lesbar. Der Tag steht im **Instruktionsstrom**, nicht im Laufzeitwert —
der Dispatch bleibt statisch, es gibt keinen polymorphen Opcode.

### Werte und Slots

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x01` | `const` | `T`, Immediate | → +1 | Konstante laden, siehe unten |
| `0x02` | `ldloc` | `uleb128` slot | → +1 | Slot lesen |
| `0x03` | `stloc` | `uleb128` slot | −1 | oberster Wert in den Slot |
| `0x04` | `pop` | — | −1 | obersten Wert verwerfen |

**`const`-Immediate** je nach Tag:

| Tag | Immediate |
|---|---|
| `i8`…`i64`, `u8`…`u64` | `uleb128` des **Zweierkomplement-Bitmusters**, nullerweitert auf 64 Bit |
| `f32` | 4 Byte IEEE-754, little-endian |
| `f64` | 8 Byte IEEE-754, little-endian |
| `bool` | 1 Byte |
| `char` | `uleb128` Unicode-Codepoint |
| `string` | `uleb128` Index in den String-Pool |

Der Wert **muss** in die Breite des Tags passen: `const i8` mit einem Immediate > `0xFF` ist
ungültig. `const void` ist ungültig.

### Arithmetik und Bitoperationen

Alle nehmen zwei Werte desselben Typs und legen einen Wert dieses Typs zurück (−2 +1).
Das Tag nennt den Operandentyp.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x10` | `add T` | | `0x15` | `shl T` |
| `0x11` | `sub T` | | `0x16` | `shr T` |
| `0x12` | `mul T` | | `0x17` | `and T` |
| `0x13` | `div T` | | `0x18` | `or T` |
| `0x14` | `rem T` | | `0x19` | `xor T` |

`add`…`rem` verlangen einen numerischen Typ, `shl`…`xor` einen ganzzahligen. Signed und unsigned
sind **verschiedene** Operationen — `div i64` und `div u64` sind nicht austauschbar. Es gibt keine
String-Konkatenation als Instruktion: `string + string` ist in Lyric eingebaute Semantik, lowert
aber zu einem Call.

### Vergleiche

Zwei Werte desselben Typs → ein `bool` (−2 +1). **Das Tag nennt den Operandentyp**, nicht den
Ergebnistyp.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x20` | `lt T` | | `0x23` | `ge T` |
| `0x21` | `le T` | | `0x24` | `eq T` |
| `0x22` | `gt T` | | `0x25` | `ne T` |

`lt`/`le`/`gt`/`ge` verlangen einen numerischen Typ. `eq`/`ne` sind zusätzlich auf `bool`, `char`
und `string` gültig.

### Unäre Operationen und Konvertierung

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x30` | `neg` | `T` | −1 +1 | Vorzeichen, numerisch |
| `0x31` | `not` | — | −1 +1 | logisches Nicht |
| `0x32` | `bitnot` | `T` | −1 +1 | Bitweises Nicht, ganzzahlig |
| `0x33` | `conv` | `T_from`, `T_to` | −1 +1 | numerische Konvertierung |

**`not` trägt als Einziger kein Typ-Tag** — nur `bool` ist gültig, ein Tag wäre reine Redundanz.
Das ist die einzige Ausnahme von der Tag-Regel.

`conv` ist nur zwischen numerischen Typen gültig, und `T_from ≠ T_to`: eine
Identitäts-Konvertierung erzeugt der Compiler nicht.

### Aufrufe und Kontrollfluss

| Opcode | Mnemonic | Operanden | Stack | Wirkung |
|---|---|---|---|---|
| `0x40` | `call` | `uleb128` index | −n [+1] | Aufruf, siehe unten |
| `0x41` | `ret` | — | 0 | Rückkehr ohne Wert |
| `0x42` | `retval` | — | −1 | Rückkehr mit dem obersten Wert |
| `0x43` | `br` | `uleb128` block | 0 | unbedingter Sprung |
| `0x44` | `condbr` | `uleb128` ifTrue, `uleb128` ifFalse | −1 | verzweigt nach dem obersten `bool` |
| `0x45` | `unreachable` | — | 0 | darf nie erreicht werden |

**`call`** nimmt `paramCount` Werte vom Stack (der erste Parameter zuunterst) und legt genau dann
einen Wert zurück, wenn der Rückgabetyp nicht `void` ist.

Der Index adressiert einen **gemeinsamen Indexraum**: zuerst alle Imports, dann alle definierten
Funktionen. Bei `importCount = 0` ist der Index also die Position in der Function-Tabelle.

`ret`/`retval` müssen zum Rückgabetyp der Funktion passen. Jeder Block endet mit genau einer
Instruktion aus `ret`, `retval`, `br`, `condbr`, `unreachable`.

---

## 6. Validierung beim Laden

Eine Runtime **muss** ein Modul vollständig prüfen, bevor sie es ausführt, und darf es danach ohne
Sicherheitsprüfungen laufen lassen. Ablehnungsgründe und ihre Diagnostik-Codes:

| Code | Grund |
|---|---|
| `LYR-BC0001` | Magic fehlt — keine `.lyrbc`-Datei |
| `LYR-BC0002` | unbekannte Major-Version |
| `LYR-BC0003` | Datei endet mitten in einer Struktur; Sektions-Länge passt nicht zum Inhalt |
| `LYR-BC0004` | Index zeigt ins Leere: String-Pool, Funktion, Block oder Slot |
| `LYR-BC0005` | unbekannter Opcode, unbekanntes Typ-Tag, Sektionen nicht aufsteigend |
| `LYR-BC0006` | Stack-Disziplin: Unterlauf, Tiefe ≠ 0 an einer Blockgrenze, Tiefe > `maxStack` |

Der Leser bricht beim ersten Befund ab. Anders als der IR-Verifier sammelt er nicht: bei einer
kaputten Datei ist der zweite Befund meist Folge des ersten.

---

## 7. Beispiel

Quelle:

```lyr
fn add(a: int, b: int): int {
    return a + b;
}
```

Disassembly:

```
fn main.add -> i64 {
  params: 2
  maxstack: 2
  slots:
    l0: i64
    l1: i64
  bb0:
    ldloc 0
    ldloc 1
    add i64
    retval
}
```

Die vollständige Datei, 46 Bytes:

```
4C 59 52 42                  magic "LYRB"
01 00                        version.major = 1
00 00                        version.minor = 0

01                           § Sektion 1 — Capabilities
01                             byteLength = 1
00                             bitset = 0

02                           § Sektion 2 — Strings
0A                             byteLength = 10
01                             count = 1
08                             [0] Länge = 8 Byte
6D 61 69 6E 2E 61 64 64          "main.add"

04                           § Sektion 4 — Imports
01                             byteLength = 1
00                             count = 0

05                           § Sektion 5 — Functions
12                             byteLength = 18
01                             count = 1
00                             nameIndex = 0            -> "main.add"
02                             paramCount = 2
04                             returnType = i64
02                             slotCount = 2
04 04                          slotTypes = i64, i64
02                             maxStack = 2
01                             blockCount = 1
00                             blockOffsets[0] = 0
07                             codeLength = 7
02 00                            ldloc 0
02 01                            ldloc 1
10 04                            add i64
42                               retval
```

Die Slot-Tabelle hat zwei Einträge, nicht fünf: die Zwischenwerte bleiben auf dem Operanden-Stack
und brauchen keinen Slot. Ein Emitter, der jedes Zwischenergebnis in einen Slot schreibt, ist
ebenfalls konform — er erzeugt nur größeren und langsameren Code.

---

## 8. Was in Format 1.0 fehlt

Absichtlich, weil das Lowering es noch nicht erzeugt: zusammengesetzte Typen (struct/class/enum,
Arrays, Tupel), Nullable, Exceptions, Coroutinen-State-Machines, Closures, generische Instanzen und
externe Calls. Jedes davon braucht neue Opcodes oder Sektionen und damit eine neue Format-Version.

Ebenfalls offen, aber ohne Format-Änderung nachrüstbar: die Source-Map-Sektion (Id 6 ist reserviert
und beschrieben, wird aber noch nicht geschrieben) und Copy-Propagation im Emitter — heute erzeugt
ein Temp mit mehreren Lesern ein `ldloc`/`stloc`-Paar, das ein Optimierer einsparen könnte.
