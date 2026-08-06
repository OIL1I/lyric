"""Prueft jedes Sprachkonstrukt aus Sprache.md durch Parser, Sema und Lowering."""
import subprocess, tempfile, os, sys

ROOT = r'C:\Users\Olivier\CLionProjects\lyric'
LYRC = os.path.join(ROOT, r'src\Lyrc\bin\Debug\net10.0\lyrc.exe')

CASES = [
    # (Abschnitt, Konstrukt, Quelle)
    ("§1.5", "f-String mit Format-Spec", 'import std.io.console;\nfn main(): int { let x = 1; console.println(f"{x:N2}"); return 0; }'),
    ("§1.5", "char-Literal", 'fn main(): int { let c = \'a\'; return 0; }'),
    ("§2.2", "Alias-Import", 'import std.string as s;\nfn main(): int { return 0; }'),
    ("§2.3", "Modul-let (Konstante)", 'let pi = 3;\nfn main(): int { return pi; }'),
    ("§2.3", "type-Alias", 'type Id = int;\nfn main(): int { let x: Id = 1; return x; }'),
    ("§3.1", "Default-Parameter", 'fn f(a: int, b: int = 2): int { return a + b; }\nfn main(): int { return f(1); }'),
    ("§3.1", "params-Variadic", 'fn sum(params xs: int[]): int { return 0; }\nfn main(): int { return sum(1, 2); }'),
    ("§3.2", "static let (Typ-Konstante)", 'struct V { x: int, static let ZERO: int = 0; }\nfn main(): int { return V.ZERO; }'),
    ("§3.4", "Enum mit Struct-Variante", 'enum E { T { a: int } }\nfn main(): int { let e = E.T { a = 1 }; return match (e) { T { a } => a, }; }'),
    ("§3.5", "Interface mit Generics", 'interface Box<T> { fn get(): T; }\nfn main(): int { return 0; }'),
    ("§3.6", "extend auf eigenem Typ", 'class P { n: int }\nextend P { fn twice(): int { return this.n * 2; } }\nfn main(): int { let p = P { n = 2 }; return p.twice(); }'),
    ("§3.6", "extend auf builtin", 'extend int { fn twice(): int { return this * 2; } }\nfn main(): int { return (2).twice(); }'),
    ("§4", "Tupel-Typ", 'fn main(): int { let t: (int, int) = (1, 2); return 0; }'),
    ("§4", "Funktionstyp", 'fn main(): int { let f: fn(int) -> int = (x) => x; return f(1); }'),
    ("§5", "for-in ueber Range", 'fn main(): int { var n = 0; for (i in 0..3) { n += i; } return n; }'),
    ("§5", "for-in ueber Array", 'fn main(): int { var n = 0; for (x in [1,2]) { n += x; } return n; }'),
    ("§5", "match-Statement mit Guard", 'fn main(): int { let n = 5; match (n) { x if x > 1 => { return 1; }, _ => { return 0; } } }'),
    ("§5", "Range-Pattern", 'fn main(): int { let n = 5; return match (n) { 0..=9 => 1, _ => 0, }; }'),
    ("§5", "Or-Pattern", 'fn main(): int { let n = 5; return match (n) { 1 | 2 | 5 => 1, _ => 0, }; }'),
    ("§5", "Tupel-Destructuring", 'fn main(): int { let t = (1, 2); return match (t) { (a, b) => a + b, }; }'),
    ("§5", "catch-all catch (_)", 'class B :: [Throwable] { fn message(): string { return ""; } }\nfn r(): int throws B { throw B { }; }\nfn main(): int { try { r(); } catch (_) { return 1; } return 0; }'),
    ("§6.1", "Bitoperationen", 'fn main(): int { return (6 & 3) | (1 << 2) ^ 0; }'),
    ("§6.2", "Lambda mit Block", 'fn main(): int { let f: fn(int) -> int = (x) => { return x + 1; }; return f(1); }'),
    ("§6.2", "Closure faengt ein", 'fn main(): int { let k = 3; let f: fn(int) -> int = (x) => x * k; return f(2); }'),
    ("§6.2", "if als Ausdruck", 'fn main(): int { let n = 1; return if (n > 0) 1 else 0; }'),
    ("§6.5", "String-Konkatenation", 'import std.io.console;\nfn main(): int { console.println("a" + "b"); return 0; }'),
    ("§6.5", "String-Wiederholung", 'import std.io.console;\nfn main(): int { console.println("ab" * 2); return 0; }'),
    ("§6.5", "Array-Wiederholung", 'fn main(): int { let xs = [0] * 3; return xs.length; }'),
    ("§7", "Optional-Chaining ?.", 'class P { n: int }\nfn main(): int { let p: ?P = null; let n: ?int = p?.n; return 0; }'),
    ("§7", "??= Coalescing-Assign", 'fn main(): int { var x: ?int = null; x ??= 5; return x!; }'),
    ("§8", "Coroutine mit yield", 'fn f(): Coroutine<int> { yield 1; }\nfn main(): int { let c = f(); return resume c; }'),
    ("§9", "throws ohne Typ", 'class B :: [Throwable] { fn message(): string { return ""; } }\nfn r(): int throws { throw B { }; }\nfn main(): int { try { r(); } catch (_) { return 1; } return 0; }'),
    ("§9", "panic", 'fn main(): int { panic("x"); }'),
    ("§10", "@test-Attribut", '@test fn t() { }\nfn main(): int { return 0; }'),
    ("§11", "main mit args", 'fn main(args: string[]): int { return args.length; }'),
    ("§16", "generische Funktion", 'fn id<T>(x: T): T { return x; }\nfn main(): int { return id(1); }'),
    ("§16", "generische Klasse", 'class Box<T> { v: T }\nfn main(): int { let b = Box<int> { v = 1 }; return b.v; }'),
    ("§16", "Constraint", 'interface C { fn c(): int; }\nfn f<T :: [C]>(x: T): int { return x.c(); }\nfn main(): int { return 0; }'),
]

def run(cmd, path):
    r = subprocess.run([LYRC, cmd, path], capture_output=True, text=True, cwd=ROOT)
    return r.returncode, (r.stdout or '') + (r.stderr or '')

results = []
for section, name, src in CASES:
    with tempfile.NamedTemporaryFile('w', suffix='.lyr', delete=False, encoding='utf-8') as f:
        f.write(src)
        path = f.name
    try:
        pc, pout = run('parse', path)
        sc, sout = run('check', path)
        lc, lout = run('lower', path)

        if pc != 0:
            stage, detail = 'PARSE', pout
        elif sc != 0:
            stage, detail = 'SEMA', sout
        elif lc != 0:
            stage, detail = 'LOWER', lout
        else:
            stage, detail = 'ok', ''

        code = ''
        for token in detail.split():
            if token.startswith('error[LYR-'):
                code = token[6:].rstrip(']:')
                break
        results.append((section, name, stage, code))
    finally:
        os.unlink(path)

print(f"{'§':<7} {'Konstrukt':<32} {'Stufe':<7} Code")
print('-' * 74)
for section, name, stage, code in results:
    print(f"{section:<7} {name:<32} {stage:<7} {code}")

print()
fails = [r for r in results if r[2] != 'ok']
print(f"{len(results) - len(fails)} von {len(results)} laufen bis zur IR durch.")
