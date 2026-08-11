using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Was ein <b>Host-Typ</b> ist (M10/E4, ADR-026) — an <b>einer</b> Stelle.
///
/// <para><b>Die Regel</b>: eine <c>class</c> in einem <b>nativen</b> Modul, die <b>kein Feld</b>
/// und <b>keinen Methodenrumpf</b> hat. Beides zusammen, und beides ist noetig — eine feldlose
/// Klasse allein waere in User-Code eine gewoehnliche (wenn auch nutzlose) Klasse, und ein natives
/// Modul enthaelt auch gewoehnliche Klassen mit Feldern.</para>
///
/// <para><b>„Kein Feld" ist die eigentliche Aussage</b>, nicht „leer": ein Host-Typ hat kein
/// Layout, das dieses Modul kennt. Methoden aendern daran nichts — sie sind Natives und liegen
/// beim Host, genau wie die freien Funktionen, ueber die E4a lief. Bis E4b hiess die Regel
/// „leerer Rumpf", was dasselbe meinte, solange es keine Methoden gab.</para>
///
/// <para><b>Warum kein Marker.</b> <c>@host</c> waere deutlicher, aber Attribute sind post-v1
/// (Sprache.md §10) — sie einzufuehren hiesse, fuer ein Werkzeug-Thema eine Grammatik-Entscheidung
/// zu treffen. „Leere Klasse in einem nativen Modul" sagt dasselbe: ein Typ ohne Inhalt, ueber den
/// das Modul nichts weiss.</para>
///
/// <para><b>Warum diese Datei existiert.</b> Die Frage wird an <b>zwei</b> Stellen gestellt — beim
/// Lowern einer nativen Signatur (<see cref="DeclaredTypes"/>, ueber den syntaktischen Knoten) und
/// beim Lowern eines Sema-Typs an der Aufrufstelle (<see cref="TypeTable"/>, ueber das Symbol).
/// Beim ersten Anlauf stand sie nur an der ersten; das Ergebnis war
/// <c>cannot compare IrRefType with IrHostType</c> im Verifier, weil dieselbe Klasse einmal als
/// Host-Typ und einmal als gewoehnliche Referenz gelowert wurde. <b>Eine Frage, zwei Stellen, eine
/// Antwort</b> — dasselbe Muster, das in diesem Projekt achtmal Zeit gekostet hat.</para>
/// </summary>
internal static class HostTypes
{
    /// <summary>Der Name, wenn <paramref name="symbol"/> ein Host-Typ ist; sonst <c>null</c>.
    /// </summary>
    public static string? NameOf(TypeSymbol? symbol, Compilation? compilation)
    {
        if (compilation is null) return null;
        if (symbol is not { Kind: TypeSymbolKind.Class, Declaration: ClassDecl declaration })
            return null;

        // Ein Feld oder ein Methodenrumpf macht daraus eine gewoehnliche Klasse: dann kennt das
        // Modul ein Layout beziehungsweise Code, und beides gehoert nicht dem Host.
        foreach (var member in declaration.Members)
            if (member is not FunctionDecl { Body: null }) return null;

        // In welchem Modul steht die Deklaration? Der Symboltabelle sieht man das nicht an, also
        // wird gesucht — die Modulliste ist kurz, und diese Frage stellt sich nur fuer eine
        // leere Klasse.
        foreach (var module in compilation.Modules)
        {
            if (!ReferenceEquals(module.Members.LookupLocal(symbol.Name), symbol)) continue;
            return compilation.IsNative(module) ? symbol.Name : null;
        }

        return null;
    }
}
