> **Mirror.** The canonical home of this document is
> [`lyriclang/lyric-spec` → `spec/13-bytecode.md`](https://github.com/lyriclang/lyric-spec/blob/main/spec/13-bytecode.md).
> This copy exists for the toolchain's tests and doc site; CI diffs everything below the
> marker against the canonical body. Edit it there, mirror it here.

<!-- sync:body -->
# Lyric `.lyrbc` Bytecode Format 3.6

This document is normative. The C# serializer implements it; it does not define it. A disassembler
or a second runtime can be written from this document alone.

Before Lyric v1.0 the format may change incompatibly with a major version bump and without a
migration path. A stability promise begins at v1.0.

Format version **3.6** covers: scalars, locals, module-internal and native calls, structured
control flow, classes, arrays, optionals, enums, interfaces with vtable dispatch, structs with
value semantics, exceptions, global constants, closures, host objects, source positions,
attributes, debug information, the names of opaque field types, and fused compare-and-branch.

**3.6 against 3.5**: four new opcodes. `brcmp` and `brcmpk` (§Calls and control flow) do in one
instruction what a comparison followed by `condbr` does in four; `binll` and `binlk`
(§Arithmetic and bit operations) do the same for an operation whose operands are slots and whose
result goes into one. A module that uses one needs
a runtime of 3.6 or newer; a module that does not is unchanged, which is the compatibility
§Versioning describes. Nothing else moves: the fused forms compute what the instructions they
replace computed, in the same order, with the same overflow and comparison rules — a producer may
emit either.

**3.5 against 3.4**: one new section — OpaqueFields (id 14), the name of the `opaque type` a
field was declared with. It is skippable, and in the plainest way: no other section refers to it,
it says nothing about how the program runs, and a reader that ignores it is a reader that knows
what it knew before. A 3.4 reader therefore loads a 3.5 module unchanged, and a 3.5 reader loads
a 3.4 module — the only difference is whether a host can tell a handle from the number it is
made of.

*(The rule above was written as "a minor may only add skippable sections" and stood that way
through 3.4, which is not one — the note below says so in its own words. The wording was
corrected to what the format has actually done since; no version's contents changed with it.)*

**3.4 against 3.3**: one new `ConstValue` form — an attribute value of enum type, written as the
variant's tag. It is the first change to this format that a 3.3 reader cannot ignore: the
Attributes section is skippable as a whole, but a reader that DOES read it meets a tag it has no
case for and rejects the module. A 3.4 module whose attribute rows use no enum value loads on a
3.3 reader unchanged, which is the same forward path the language's own additions take — the
module that uses the new thing is the module that needs the new runtime.

**3.3 against 3.2**: one new section — DebugInfo (id 13), the names of local and global
slots — and one loosened producer rule: Names (id 12) may carry field names for any type, where
3.2 allowed entries only for types an Attributes row references. Both directions stay compatible:
a 3.2 reader skips the unknown section, and its Names rules never depended on the restriction, so
it loads a 3.3 module unchanged — the only difference is whether a debugger can name a slot or a
field.

**3.2 against 3.1**: two new sections — Attributes (id 11) and Names (id 12). Both are skippable:
no other section refers to either, and a runtime that ignores them runs the program unchanged,
because an attribute describes and does nothing. A 3.1 reader therefore loads a 3.2 module; the
only difference is whether a host can read the attributes.

**3.1 against 3.0**: the SourceMap section (id 6) has a payload. It is skippable, so a 3.0 reader
loads a 3.1 module and a 3.1 reader loads a 3.0 module; the only difference is whether a panic can
name a line.

---

## 1. Encoding

| Item | Rule |
|---|---|
| Fixed-width integers | little-endian, explicitly — not host byte order |
| Variable integers | LEB128, unsigned, at most 10 groups (64 bits) |
| Strings | byte length as LEB128, then UTF-8 without BOM and without terminator |
| Floats | IEEE-754 bit pattern, little-endian (4 or 8 bytes) |
| Bool | one byte, `0x00` is false, anything else is true |

**LEB128 (unsigned)**: seven payload bits per byte, least significant first. Bit 7 (`0x80`) set
means another byte follows. A reader that consumes more than ten bytes must reject the file.

**Determinism**: the same compiler input produces byte-identical output.

- The string pool is in first-use order.
- Sections appear at most once, in ascending id order.
- No timestamps, no absolute paths.

---

## 2. File layout

```
magic            4 bytes   'L' 'Y' 'R' 'B'  (0x4C 0x59 0x52 0x42)
version.major    u16       little-endian
version.minor    u16       little-endian
sections         *         any number, see below
```

A section:

```
id               u8
byteLength       uleb128   length of the payload, excluding id and this field
payload          byteLength bytes
```

A reader must skip a section with an unknown id and must reject a file whose section ids do not
strictly ascend.

**Versioning**: an unknown major version is rejected. An unknown minor version is tolerated.

A new MINOR version may add anything a module can decline to use: a skippable section, a value
form inside one, an opcode. What it may not do is change the meaning or the shape of something a
reader of the previous minor already reads — that is a major. The compatibility this buys is
per MODULE rather than per version: a module that uses nothing new loads on the older reader
unchanged, and a module that uses something new needs a runtime of that minor or newer, the same
forward path the language's own additions take.

Two consequences worth stating, because they are what the rule is for. A reader must not reject a
file merely for carrying a higher minor. And a producer must not silently downgrade: if it emits
a new form, the minor in the header says so, so the failure is "this runtime is too old", named
at load time, instead of a misread instruction.

### Section ids

| Id | Name | Required | Contents |
|---|---|---|---|
| 1 | Capabilities | no | `uleb128` bitset |
| 2 | Strings | no | constant pool, strings only |
| 3 | Types | no | layouts of composite types |
| 4 | Imports | no | host and native functions |
| 5 | Functions | no | defined functions with their code |
| 6 | SourceMap | no | strippable: byte offset to file and line |
| 7 | Start | no | entry point: `uleb128` function index |
| 8 | Impls | no | interface implementations (vtables) |
| 9 | Handlers | no | protected regions per function |
| 10 | Globals | no | global slots and their initializer |
| 11 | Attributes | no | attribute rows: which struct describes which target, with literal values |
| 12 | Names | no | field names: required for types an Attributes row references, permitted for any |
| 13 | DebugInfo | no | strippable: local and global slot names |
| 14 | OpaqueFields | no | the `opaque type` a field was declared with |

A missing section counts as empty.

### Types (Id 3)

`uleb128` count, then per type:

```
nameIndex        uleb128   index into the string pool
kind             u8        0 = layout, 1 = enum, 2 = interface, 3 = struct
```

**kind 0 — layout** (class, struct, and each individual enum variant):

```
fieldCount       uleb128
fieldTypes       fieldCount × type (§3), in declaration order
```

**kind 1 — enum**: names its variants, each of which is its own layout entry.

```
variantCount     uleb128
variantTypes     variantCount × uleb128   index into this table
```

Slot 0 of every variant is its tag: the `i64` index of the variant in its enum's `variantTypes`
list, zero-based in declaration order. Payload fields start at slot 1.

**kind 3 — struct**: the same field layout as kind 0, with value semantics.

```
fieldCount       uleb128
fieldTypes       fieldCount × type (§3), in declaration order
```

A struct value is the same slot sequence as a class object at runtime; `ldfld` and `stfld` operate
on it unchanged. Value semantics live entirely in `structcopy` (§5).

A struct must not contain itself as a field, directly or indirectly. Recursion through a class, an
array or an interface is permitted; those are references.

**kind 2 — interface**: names its method slots instead of fields.

```
slotCount        uleb128   at least 1
slotNames        slotCount × string
```

The index into this list is the slot that `callvirt` addresses. `slotCount` must be at least 1. An
interface must not appear on the left of an Impls row.

The field index is the position in the field list; field names do not appear in the bytecode. The
type name is present for diagnostics and disassembly.

A reader must reject: a field type of `void`, a type index outside the table, and an enum whose
variant is not a layout. A type may name itself as a field type, including forward.

### Capabilities (Id 1)

A `uleb128` bitset stating what the module requires.

| Bit | Value | Capability | Module |
|---|---|---|---|
| 0 | `0x1` | `fileAccess` | `std.io.file` |
| 1 | `0x2` | `networkAccess` | `std.io.net` |
| 2 | `0x4` | `osAccess` | `std.os` |
| 3 | `0x8` | `hostAccess` | reserved |

`0` means the module requires nothing. Submodules inherit: `std.os.env` requires `osAccess`.

Enforcement happens at load time. A module that requires more than the runtime grants is rejected
(`LYR-CAP0001`) before any instruction runs.

### Strings (Id 2)

```
count            uleb128
values           count × string
```

### Imports (Id 4)

```
count            uleb128
entries          count × {
                   name         string
                   paramCount   uleb128
                   paramTypes   paramCount × type tag
                   returnType   type tag
                 }
```

Host functions are referenced symbolically by name and signature. Binding to implementations
happens at load time.

Parameter and return types use the general type grammar of §3. Two composite forms carry a
convention with them:

- **A struct-typed parameter in LAST position** may be a **result buffer**: the compiler wires a
  native declared `fn f(...): S` as `f(..., out: S): void`, the runtime passes a module-owned
  instance of `S`, and the implementation fills one value per field in field order. Which imports
  follow this convention is the host's knowledge, carried by its registration — the format only
  provides the shape.
- Whether a runtime can BIND a given signature is the registry's question, not the reader's: a
  reader accepts any well-formed type here, and a runtime without an implementation for the name
  and signature rejects the module at load time. That is what keeps this section forward-open —
  a module using conventions a runtime predates fails at binding, with the import's name in the
  message, never by misreading.

### Functions (Id 5)

```
count            uleb128
entries          count × {
                   nameIndex    uleb128   index into the string pool
                   paramCount   uleb128
                   returnType   type tag
                   slotCount    uleb128
                   slotTypes    slotCount × type tag
                   maxStack     uleb128
                   blockCount   uleb128
                   blockOffsets blockCount × uleb128   byte offset into 'code'
                   codeLength   uleb128
                   code         codeLength bytes
                 }
```

- The first `paramCount` slots are the parameters, in declaration order. `paramCount ≤ slotCount`.
- `maxStack` is the maximum operand stack depth in this function. A runtime may size its frame
  from it and omit runtime overflow checks.
- `blockOffsets[i]` is the byte offset of block `i` in `code`. Every offset must fall on an
  instruction boundary.
- Block 0 is the entry block. Execution begins at `blockOffsets[0]`.

### SourceMap (Id 6)

Byte offset to source position, one table per function.

```
fileCount        uleb128
files            fileCount × uleb128   index into the string pool
functionCount    uleb128               must equal the count in Functions (Id 5)
functions        functionCount × {
                   rowCount   uleb128
                   rows       rowCount × {
                     offsetDelta  uleb128   bytes since the previous row of this function
                     fileIndex    uleb128   index into the file table above
                     line         uleb128   1-based; 0 means no line is known
                   }
                 }
```

**Strippable.** No other section refers to this one, so removing it leaves a valid module. A module
without it is valid too; the only consequence is that a panic names a function instead of a line.

The offsets are byte offsets into that function's `code`, the same coordinate `blockOffsets` uses.
The first row of a function carries its offset outright; every later row carries the difference to
the one before. The offsets therefore ascend, and only the first row may have a delta of `0`.

A row states the position from its own offset up to the next row's. **To resolve an offset, take the
last row whose offset is less than or equal to it**; before the first row there is no position.

**A row is written only where the position changes.** A loop body is dozens of instructions across a
handful of lines, and a row per instruction would make this the largest section in the file.

A function may carry `rowCount` `0`. Its code then has no position at all, which is what a runtime
reports for it.

**Why byte offsets and not instruction indices.** An instruction index is not a notion of this
format: it presupposes that a runtime decodes the code into an array before running it. A runtime
that walks the bytes directly would have to count instructions to answer a question the format can
answer in its own coordinates.

**The row carries no column.** The shape of an existing row is fixed until a major (§Versioning):
a column cannot be added here later, and adding it beside this section would be a second mechanism
for the same thing.

A reader must reject: a `functionCount` that differs from the Functions section, a file index outside
the file table, a string index outside the pool, an offset that does not lie inside that function's
`code` — a row marks where an instruction begins, so `offset == codeLength` is already outside — and
a delta of `0` on any row but the first.

### Start (Id 7)

```
functionIndex    uleb128   into the shared index space: imports first, then functions
```

The entry point. A runtime calls it without arguments unless its signature takes `string[]` (§8);
its return value is the process exit code, masked with `& 0xFF`.

Without this section the module is a library and has no entry point.

### Impls (Id 8)

The vtables: which function fills which method slot of which interface for which type.

```
implCount        uleb128
implCount × {
  typeIndex      uleb128   the implementing type (class or enum)
  interfaceIndex uleb128   an interface entry
  methodCount    uleb128   must equal the interface's slotCount
  methods        methodCount × uleb128, each an index into the shared call
                 index space (imports first, then functions)
}
```

One row per (type, interface) pair; the same pair twice is an error. A type that is neither class
nor enum must not appear on the left.

All implementations of the same slot share a signature. A runtime may derive the argument count
from any row of the interface, and must, because `callvirt` takes its receiver off the stack
before the target function is known.

### Handlers (Id 9)

The protected regions: which block range of a function is covered by which handler.

```
handlerCount     uleb128
handlerCount × {
  function       uleb128   index into the Functions section
  startBlock     uleb128   first protected block
  endBlock       uleb128   first block no longer protected
  kind           u8        0 = catch, 1 = finally
  catchType      uleb128   0 = catches everything; otherwise type index + 1
  handlerBlock   uleb128   where control transfers
  slot           uleb128   0 = binds nothing; otherwise slot index + 1
}
```

Ranges are block indices, not byte ranges.

**The order is the contract: innermost region first.** While unwinding, a runtime takes the first
entry whose range covers the fault site and whose type matches.

The caught value goes into a slot, not onto the stack. When entering a handler the operand stack
is cleared.

A runtime must reject: a range outside the block count, `startBlock >= endBlock`, a handler block
inside its own range, a type or slot index outside its table, and a `finally` region carrying a
type or a slot.

### Globals (Id 10)

```
globalCount      uleb128
globalTypes      globalCount × type (§3)
initFunction     uleb128   0 = none; otherwise index + 1 into the shared call
                 index space (imports first, then functions)
```

A runtime must call the initializer before the entry point. It takes no arguments, returns
nothing, and leaves the slots filled. The order within its body is the initialization order.

A reader must reject: a global type of `void`, an init index outside the call space, and a
non-empty global list without an init function.

A slot of type `string` starts as the empty string, not as an empty reference.

### Attributes (Id 11)

New in 3.2. Which struct type describes which function, type or the module, with literal values.

```
count            uleb128
rows             count × {
                   targetKind   u8        0 = function, 1 = type, 2 = module
                   target       uleb128   into Functions (kind 0) or Types (kind 1); 0 for kind 2
                   type         uleb128   the attribute's struct type: an index into Types
                   valueCount   uleb128   must equal the struct's fieldCount
                   values       valueCount × ConstValue
                 }

ConstValue       = tag u8, then by tag:
                   integers, char     uleb128    two's complement, widened to 64 bits
                   f32 / f64          4 / 8 bytes IEEE-754 bit pattern, little-endian
                   bool               u8
                   string             uleb128    index into the string pool
                   enum (3.4)         uleb128    the variant's tag: its index in the enum's
                                                 variantTypes list
```

**An enum value names no enum.** The field's own type does, and the enum's entry names its
variants, so the tag is the whole payload — a second copy could disagree with the first. A reader
resolves the variant name through the field type when it wants one; the value itself is the
number slot 0 of that variant carries at runtime. The tag must be within the enum's variant list,
and only a variant WITHOUT a payload may be written: a row holds one value per field, and a
payload is values of its own.

**A row is complete**: one value per field of the attribute type, in field declaration order — a
field the source did not write carries the field's literal default, filled in by the compiler. The
position IS the field index, which is why no index is stored, and each value's tag must equal the
tag of the field at its position.

The rows stand in declaration order: modules in compilation order, targets in source order, the
attributes of one target in the order written.

**Skippable, and inert by design.** No other section refers to this one, and a runtime that
ignores it runs the program unchanged: an attribute describes its target, it does nothing. What a
host makes of a row is the host's business.

A compiler must keep an attributed function alive: the row is a promise that the index is valid,
and the host is a caller the reachability analysis cannot see — the same standing as the entry
point.

A reader must reject: an unknown target kind, a target or type index outside its table, a nonzero
target for a module row, a type that is not a struct, the same (targetKind, target, type) triple
twice, a value count differing from the struct's field count, a value tag differing from the field
tag at its position, a string index outside the pool, and a value of a non-literal type.

### Names (Id 12)

New in 3.2. Field names, for types an Attributes row references — the attribute types
themselves and the attributed type targets. Those entries are REQUIRED whenever such a type has
fields. Since 3.3 the restriction is a floor rather than a ceiling: a compiler may write an entry
for ANY type — a debugger expanding an object needs its field names the same way a host reading
an attribute row does. Stripping debug information (§DebugInfo) removes the entries no Attributes
row demands; everywhere else the rule of the Types section stands: field names are not in the
bytecode.

```
count            uleb128
entries          count × {
                   type         uleb128   index into Types
                   nameCount    uleb128   must equal that type's fieldCount
                   names        nameCount × string
                 }
```

The names stand in field order, so `names[i]` names the field `fieldTypes[i]` describes — and for
an attribute type, the value at position `i` of every row that references it. Entries ascend by
type index; a referenced type with zero fields has no entry.

Without this section a host reading `@Component struct Health { value, max }` would learn a shape
it cannot name: the layout gives field types and the row gives values, but which field means what
exists only in the source.

A reader must reject: a type index outside the table, the same type twice, and a name count
differing from that type's field count.

### DebugInfo (Id 13)

New in 3.3. The name of each local and global slot — what a debugger writes beside a value.

```
functionCount    uleb128               must equal the count in Functions (Id 5)
functions        functionCount × {
                   nameCount  uleb128   0, or that function's slotCount
                   names      nameCount × uleb128   index into the string pool
                 }
globalNameCount  uleb128               0, or the count in Globals (Id 10)
globalNames      globalNameCount × uleb128   index into the string pool
```

`names[i]` names slot `i`, so the first `paramCount` entries name the parameters;
`globalNames[i]` names global slot `i`. The names go through the string pool rather than inline:
`i`, `x` and `this` repeat across functions, and the pool is where repetition pays.

A slot the compiler created — a spilled intermediate, a value crossing a block boundary, a result
buffer — carries the empty string. A consumer does not show such a slot: which slots exist is the
format's business; which of them mean anything to a person is the compiler's statement, made
here.

`nameCount` `0` means the function says nothing about its slots. A count that is neither `0` nor
the function's `slotCount` is malformed: the position IS the slot index, and a partial list would
name the wrong slots.

**Strippable.** No other section refers to this one, so removing it — together with the Names
entries no Attributes row demands — leaves a valid module. A debugger then shows slot indices,
the same way a panic without a SourceMap names a function instead of a line.

A reader must reject: a `functionCount` differing from the Functions section, a `nameCount` that
is neither `0` nor that function's slot count, a `globalNameCount` that is neither `0` nor the
Globals count, and a string index outside the pool.

### OpaqueFields (Id 14)

New in 3.5. The name of the `opaque type` a field was declared with, for types that have at least
one such field.

An opaque alias is a distinct type in the language and its underlying type everywhere below the
checker: `opaque type Entity = int` is an `i64` in every layout, signature and instruction, which
is what lets a handle cross a native boundary for nothing. The consequence is that a consumer
reading the shape of a type — a host deciding what it may write to disk, a debugger labelling a
value — sees a number where the source wrote a handle, and the two demand different treatment.
This section is the name it lost, and nothing more: it does not change what the field IS.

```
count            uleb128
entries          count × {
                   type         uleb128   index into Types
                   nameCount    uleb128   must equal that type's fieldCount
                   names        nameCount × string
                 }
```

The names stand in field order, as in Names (§Names): `names[i]` belongs to the field
`fieldTypes[i]` describes. A field whose type is not opaque carries the **empty string** — the
position is the index, so every field of an entry's type answers or none does. A type no field of
which is opaque has no entry at all.

The name is the alias's own, unqualified, as every name in the tables is. It names the LEAF
through arrays and optionals: a field declared `Entity[]` carries `Entity`, and its entry in the
Types section still says the field is an array. A transparent alias resolves through to what it
names, because a transparent alias is a name for a type rather than a type of its own — only an
opaque one is distinct.

**Skippable and metadata-only.** No other section refers to it, nothing in it affects execution,
and a producer may leave it out entirely. A producer that writes it SHOULD write it for the types
Names covers, which is where a consumer asks.

A reader must reject: a type index outside the table, the same type twice, and a name count
differing from that type's field count.

---

## 3. Type tags

One byte.

| Tag | Type | | Tag | Type |
|---|---|---|---|---|
| `0x01` | `i8` | | `0x08` | `u64` |
| `0x02` | `i16` | | `0x09` | `f32` |
| `0x03` | `i32` | | `0x0A` | `f64` |
| `0x04` | `i64` | | `0x0B` | `bool` |
| `0x05` | `u8` | | `0x0C` | `char` |
| `0x06` | `u16` | | `0x0D` | `string` |
| `0x07` | `u32` | | `0x0E` | `void` |

Composite types from `0x40`:

| Tag | Meaning | Followed by |
|---|---|---|
| `0x40` | reference to a Types entry | `uleb128` type index |
| `0x41` | array | the element type, again as a type (§3) |
| `0x42` | optional (`?T`) | the inner type, again as a type (§3) |
| `0x43` | enum | `uleb128` index of an enum entry |
| `0x44` | interface | `uleb128` index of an interface entry |
| `0x45` | struct (value semantics) | `uleb128` index of a struct entry |
| `0x46` | function value | `uleb128` parameter count, the parameter types, then the return type, all inline |
| `0x47` | host object | the registered type name, inline as a length-prefixed UTF-8 string |

An array's element type is inline rather than a table index: `int[][]` is `0x41 0x41 0x04`. An
array type cannot be recursive, so it needs no table entry.

`void` is valid only as a return type, never as a slot, field or value type.

A value tagged `0x40` is a reference: assignment copies the reference. A value tagged `0x45` is a
value type: the same slot sequence, but every binding copies.

Lyric's `int`, `uint` and `float` are aliases for `i64`, `u64` and `f64` and appear as those.

---

## 4. Execution model

A stack machine with two separate stores per call:

- **Local slots** — indexed, typed, readable and writable in any order.
- **Operand stack** — what the instructions work on.

### The invariant

> The operand stack is empty at every block boundary.

Values that cross blocks travel through local slots. It follows that stack depth at any point is
statically determined without data-flow analysis, that a reader can verify it at load time and
omit every runtime check afterwards, and that jumps need no stack adjustment.

### Jump targets

Jumps name block indices, not byte offsets. The function header carries the offset table. A target
is checked with `index < blockCount`.

---

## 5. Instructions

Every instruction starts with an opcode byte. `T` denotes a type tag byte (§3). The type travels
as a tag next to the opcode, not encoded into it; the tag is in the instruction stream, not in the
runtime value, so dispatch stays static.

### Values and slots

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x01` | `const` | `T`, immediate | +1 | load a constant, see below |
| `0x02` | `ldloc` | `uleb128` slot | +1 | read a slot |
| `0x03` | `stloc` | `uleb128` slot | −1 | store the top value into the slot |
| `0x04` | `pop` | — | −1 | discard the top value |

`const` immediate by tag:

| Tag | Immediate |
|---|---|
| `i8`…`i64`, `u8`…`u64` | `uleb128` of the two's-complement bit pattern, zero-extended to 64 bits |
| `f32` | 4 bytes IEEE-754, little-endian |
| `f64` | 8 bytes IEEE-754, little-endian |
| `bool` | 1 byte |
| `char` | `uleb128` Unicode code point |
| `string` | `uleb128` index into the string pool |

The value must fit the width of the tag. `const void` is invalid.

### Arithmetic and bit operations

Each takes two values of the same type and leaves one of that type (−2 +1). The tag names the
operand type.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x10` | `add T` | | `0x15` | `shl T` |
| `0x11` | `sub T` | | `0x16` | `shr T` |
| `0x12` | `mul T` | | `0x17` | `and T` |
| `0x13` | `div T` | | `0x18` | `or T` |
| `0x14` | `rem T` | | `0x19` | `xor T` |

`add` through `rem` require a numeric type, `shl` through `xor` an integer type. Signed and
unsigned are distinct operations. There is no string concatenation instruction; it lowers to a
call.

**The fused forms** (3.6). Two more opcodes do the same arithmetic over LOCAL SLOTS, in one
instruction instead of four:

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x1A` | `binll` | `op`, `T`, `uleb128` dest, `uleb128` a, `uleb128` b | 0 | `dest = a op b` |
| `0x1B` | `binlk` | `op`, `T`, `uleb128` dest, `uleb128` a, immediate | 0 | `dest = a op k` |

`op` is one byte holding one of the binary opcodes — `0x10`..`0x19` or a comparison
`0x20`..`0x25`; the fused forms carry the operation they perform rather than introducing an
enumeration of their own. `T` is the tag of the OPERANDS, and the rules above apply to it
unchanged; when `op` is a comparison, `dest` receives a `bool`, exactly as the unfused form leaves
one on the stack. `binlk` encodes its constant as `const` encodes a value of `T`.

The destination may be one of the sources: both operands are read before it is written.

Neither touches the operand stack, and both are optional for a producer — see §Versioning.

### Comparisons

Two values of the same type produce a `bool` (−2 +1). The tag names the operand type, not the
result type.

| Opcode | Mnemonic | | Opcode | Mnemonic |
|---|---|---|---|---|
| `0x20` | `lt T` | | `0x23` | `ge T` |
| `0x21` | `le T` | | `0x24` | `eq T` |
| `0x22` | `gt T` | | `0x25` | `ne T` |

`lt`, `le`, `gt` and `ge` require a numeric type. `eq` and `ne` are additionally valid on `bool`,
`char` and `string`.

### Unary operations and conversion

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x30` | `neg` | `T` | −1 +1 | negation, numeric |
| `0x31` | `not` | — | −1 +1 | logical not |
| `0x32` | `bitnot` | `T` | −1 +1 | bitwise not, integer |
| `0x33` | `conv` | `T_from`, `T_to` | −1 +1 | numeric conversion |

`not` carries no type tag; only `bool` is valid. It is the only exception to the tag rule.

`conv` is valid between numeric types only, with `T_from ≠ T_to`.

### Calls and control flow

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x40` | `call` | `uleb128` index | −n [+1] | call, see below |
| `0x41` | `ret` | — | 0 | return without a value |
| `0x42` | `retval` | — | −1 | return the top value |
| `0x43` | `br` | `uleb128` block | 0 | unconditional jump |
| `0x44` | `condbr` | `uleb128` ifTrue, `uleb128` ifFalse | −1 | branch on the top `bool` |
| `0x45` | `unreachable` | — | 0 | must never be reached |
| `0x46` | `brcmp` | `cmp`, `T`, `uleb128` a, `uleb128` b, `uleb128` ifTrue, `uleb128` ifFalse | 0 | compare two slots and branch |
| `0x47` | `brcmpk` | `cmp`, `T`, `uleb128` a, immediate, `uleb128` ifTrue, `uleb128` ifFalse | 0 | compare a slot with a constant and branch |

`call` takes `paramCount` values off the stack, the first parameter lowest, and leaves one value
exactly when the return type is not `void`.

The index addresses a shared index space: all imports first, then all defined functions.

`ret` and `retval` must match the function's return type. Every block ends with exactly one of
`ret`, `retval`, `br`, `condbr`, `brcmp`, `brcmpk`, `unreachable`, `throw` or `endfinally`.

**The fused branches** (3.6). `brcmp` and `brcmpk` are `condbr` with the comparison folded in.
`cmp` is one byte holding one of the comparison opcodes (`0x20`..`0x25`) — the fused forms carry
the operation they perform rather than introducing an enumeration of their own — and `T` is the
tag of the OPERANDS, as on the comparison itself. `brcmpk` encodes its right-hand operand exactly
as `const` encodes a value of `T`.

Both read local slots and branch to block indices; **neither touches the operand stack**, which is
where the saving comes from. The comparison rules of §Comparisons apply unchanged, minus `string`:
these forms compare one machine word.

A producer is free to emit the unfused sequence instead — the two are equivalent by construction,
and a producer that never emits the fused forms writes a module any 3.5 runtime accepts.

### Objects

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x50` | `newobj` | `uleb128` type | +1 | allocate an instance, fields at their zero value |
| `0x51` | `ldfld` | `uleb128` type, `uleb128` field | −1 +1 | replace the reference with the field value |
| `0x52` | `stfld` | `uleb128` type, `uleb128` field | −2 | store the field |

**Stack order for `stfld`**: the reference lies below the value. Push the reference first, then the
value.

The type index accompanies `ldfld` and `stfld` so a reader can check the field index against a
layout at load time without data-flow analysis.

**Zero value of a field**: numbers `0`, `bool` false, `char` U+0000, `string` the empty string,
references the null reference. No field is ever uninitialized.

### Arrays

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x58` | `newarr` | element type (§3), `uleb128` count | −n +1 | take `n` values off the stack and build an array |
| `0x59` | `ldelem` | — | −2 +1 | array, index → element |
| `0x5A` | `stelem` | — | −3 | array, index, value |
| `0x5B` | `arrlen` | — | −1 +1 | length as `i64` |
| `0x5C` | `arrcat` | — | −2 +1 | concatenate two arrays into a new one |
| `0x5D` | `arrrep` | — | −2 +1 | array, count → new array, repeated |

`newarr` takes the element count as an immediate after the element type, then that many values off
the stack, the first element lowest.

An index violation is a panic, not undefined behaviour. This applies to `ldelem` and `stelem`. An
element index is a runtime value and is not checkable at load time.

An array does not grow; its length is fixed at creation. `arrcat` and `arrrep` each produce a new
array and leave their operands unchanged. `arrrep` with count `0` yields an empty array; a negative
count is a panic.

### Optionals

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x60` | `optnone` | inner type (§3) | +1 | push "no value" |
| `0x61` | `optsome` | inner type (§3) | −1 +1 | wrap the top value |
| `0x62` | `optissome` | — | −1 +1 | `bool`: is a value present? |
| `0x63` | `optget` | — | −1 +1 | unwrap; panics when there is no value |

An optional does not nest. A reader must reject an inner type tagged `0x42`.

**Representation.** A value is "no value" exactly when its reference is empty. For `?string`,
`?T[]` and `?class` this coincides with the natural representation. For `?int`, `?bool` and
`?char` there is no free bit pattern, so a runtime must carry a marker distinguishable from the
payload and hold the number beside it. A runtime must not reserve a bit pattern as null: `?int`
must carry all 2^64 `int` values.

### Enums

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x68` | `newvariant` | `uleb128` variantType | −n +1 | allocate a variant; `n` is its payload field count |
| `0x69` | `enumtag` | — | −1 +1 | the variant's tag as `i64` |
| `0x6A` | `enumas` | `uleb128` variantType | −1 +1 | narrow to a variant; panics on a mismatched tag |

`newvariant` takes the payload fields off the stack, the first lowest, and sets slot 0 to the tag
itself.

`match` has no opcode. It reads the tag with `enumtag` and branches on it; after the branch
`enumas` produces a value of the variant's type, and field access is an ordinary `ldfld` against
the variant's layout.

### Interfaces

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x70` | `mkiface` | `uleb128` concreteType, `uleb128` interfaceType | −1 +1 | lift an object reference to its interface type |
| `0x71` | `callvirt` | `uleb128` interfaceType, `uleb128` slot | −n +0/1 | call the slot's implementation for the receiver's concrete type |

`mkiface` carries both indices so a loader can check the implementation relation against the Impls
section without data-flow analysis.

`callvirt` expects the receiver as argument 0, lowest on the stack. `n` is the slot's argument
count including the receiver.

There is no downcast; an interface value cannot be narrowed back to its class.

**Representation.** A value tagged `0x44` carries two things: the object reference and the index of
its concrete type in the Types section. How a runtime stores that is its own choice. Reading the
concrete type out of the object is not permitted — an object carries no type tag in this format,
and `mkiface` is the only point at which the concrete type is known.

### Structs

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x72` | `structcopy` | `uleb128` structType | −1 +1 | produce an independent copy of a struct value |

There is no `newstruct`; a struct value is created with `newobj`.

The copy is recursive across nested structs and shallow across everything else. A field of class,
array or interface type carries a reference, and that reference is shared. A field of struct type
is itself a value and is copied. The recursion terminates without cycle detection because a struct
cannot contain itself.

A compiler must emit `structcopy` wherever a struct value is bound into a new location:
initialization, assignment, argument, return, field and element assignment. A freshly created
value — from `newobj` or as the result of a call — does not need one.

A reader must reject `structcopy` on a type that is not a struct entry.

### Exceptions

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x73` | `throw` | `uleb128` concreteType | −1 | begin unwinding; terminator |
| `0x74` | `endfinally` | — | 0 | end of a `finally` region; terminator |

`throw` carries the concrete type of the thrown value as type index + 1, or `0` when the type is
known only at runtime — the value is then interface-typed and carries its type with it.

The type comparison while unwinding is equality, not a subtype test: the language has no
inheritance, so the type at the throw site is the type a `catch` compares against.

`endfinally` returns control to the point where unwinding was interrupted; the search continues at
the next handler of the same function with the same origin block. A `finally` region is entered
only while unwinding; `endfinally` without an active unwind is an error.

An exception that leaves the entry point uncaught aborts the runtime like a panic.

### Globals

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x75` | `ldglobal` | `uleb128` index | +1 | read a global slot |
| `0x76` | `stglobal` | `uleb128` index | −1 | write a global slot |

As `ldloc` and `stloc`, but module-wide instead of frame-wide. The index is checked at load time
and the access is unchecked at runtime.

### Closures

| Opcode | Mnemonic | Operands | Stack | Effect |
|---|---|---|---|---|
| `0x77` | `mkclosure` | `uleb128` target≪1 \| hasEnv | −(0/1) +1 | build a function value |
| `0x78` | `callind` | `uleb128` argc≪1 \| retval | −(1+argc) +(0/1) | call a function value |

A function value is a pair of environment reference and function index. A closure without captures
carries no reference and costs no allocation.

`mkclosure` takes its environment off the stack; `callind` passes it as argument 0, the position a
receiver occupies for a method.

Both operands carry a flag in the lowest bit, with the value from bit 1 upward, so that the stack
effect of each instruction is known at load time: a closure either has an environment or not, and a
function value does not carry its signature in the instruction stream.

The function index is stored incremented by one, so that a closure over function 0 without an
environment is distinguishable from "no value".

A reader must reject a `mkclosure` target index outside the call space.

---

## 6. Load-time validation

A runtime must validate a module completely before executing it, and may then run it without
safety checks.

| Code | Reason |
|---|---|
| `LYR-BC0001` | magic missing — not a `.lyrbc` file |
| `LYR-BC0002` | unknown major version |
| `LYR-BC0003` | file ends inside a structure; section length does not match its contents |
| `LYR-BC0004` | index out of range: string pool, function, block, slot, type or field |
| `LYR-BC0005` | unknown opcode, unknown type tag, sections not ascending |
| `LYR-BC0006` | stack discipline: underflow, depth ≠ 0 at a block boundary, depth > `maxStack` |

For `newobj`, `ldfld` and `stfld`, load-time checking means: the type index lies within the Types
section, and for `ldfld` and `stfld` the field index lies within the field count of that exact
type. Field access at runtime is then an unchecked array access.

For the fused forms it means: the operation byte is one the form admits — a comparison for
`brcmp` and `brcmpk`, any binary opcode for `binll` and `binlk` — every slot lies within the slot
table, every branch target within the block table, and `T` is one that operation accepts. Their
operands are read and written at runtime without a check, exactly as `ldloc`'s slot is.

The reader stops at the first finding.

---

## 7. Example

Source:

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

A minimal module containing only that function, 46 bytes. The compiler writes a SourceMap and a
DebugInfo section by default; this is the stripped form, which is why sections 6 and 13 are
absent:

```
4C 59 52 42                  magic "LYRB"
03 00                        version.major = 3
03 00                        version.minor = 3

01                           § section 1 — Capabilities
01                             byteLength = 1
00                             bitset = 0

02                           § section 2 — Strings
0A                             byteLength = 10
01                             count = 1
08                             [0] length = 8 bytes
6D 61 69 6E 2E 61 64 64          "main.add"

04                           § section 4 — Imports
01                             byteLength = 1
00                             count = 0

05                           § section 5 — Functions
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

The slot table has two entries: intermediate values stay on the operand stack and need no slot. An
emitter that writes every intermediate into a slot is also conformant.

---

## 8. Runner contract

This section is normative for any runtime used as `lyric --vm <path>`.

### 8.1 Invocation

```
<vm> run <file.lyrbc> [-- <program-args>]
```

The first parameter is literally `run`. Everything after the first `--` belongs to the Lyric
program. A runtime may offer further commands.

### 8.2 Exit codes

| Code | Meaning |
|---|---|
| `0`–`255` | return value of `main`, masked with `& 0xFF` |
| `101` | panic |
| `1` | load, validation or IO error — the program never started |
| `2` | invocation error: missing argument, unknown command, wrong file kind |

`101`, `1` and `2` collide with a program returning those values. Callers that need the
distinction read stderr.

### 8.3 Streams

- **stdout** carries the output of the Lyric program only.
- **stderr** carries diagnostics, panic messages and backtraces only.

No mixing in either direction.

### 8.4 Version output

```
<vm> --version
```

produces free-form text on stdout and exit code `0`. The driver passes it through and does not
interpret it.

### 8.5 Program arguments

Everything after the first `--` belongs to the Lyric program. A runtime delivers it to the entry
point when its signature calls for it:

| Entry point | Behaviour |
|---|---|
| `fn main(): int` | arguments are ignored |
| `fn main(args: string[]): int` | the runtime builds a `string[]` into parameter slot 0 |

Which form is present is read from the signature in the function table.

A reader must reject an entry point with more than one parameter, and one whose single parameter
is not `string[]`.

### 8.6 Modules without an entry point

A module without a Start section is a library: valid bytecode, not a program. `run` on it is an
error (`LYR-VM0001`); `verify` and `info` are not.

### 8.7 Conformance

For a given module,

```
lyrvm verify <file.lyrbc>
```

answers whether this runtime would accept it — format validation (§6) and import binding, without
executing an instruction.
