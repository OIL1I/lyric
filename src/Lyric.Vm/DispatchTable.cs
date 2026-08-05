using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// Die vtables, einmal beim Laden aus der Impls-Sektion aufgebaut.
///
/// <para>Ein <c>callvirt</c> kennt zwei Dinge statisch — Interface und Slot — und eins dynamisch:
/// den konkreten Typ, den der Empfaenger als Fat Pointer mitfuehrt. Gesucht ist also
/// (konkreter Typ, Interface) → Funktionsliste. Diese Klasse macht daraus einen Array-Zugriff.</para>
///
/// <para><b>Warum flach und nicht als Dictionary im heissen Pfad</b>: der Interpreter darf beim
/// Aufruf nichts pruefen und nichts hashen — der Loader hat bereits validiert, dass jedes
/// <c>mkiface</c> eine Impl-Zeile hat und jeder Slot in Reichweite liegt (ADR-013). Die Tabelle
/// ist eine dichte Matrix ueber (Typindex × Interfaceindex); bei den Groessenordnungen, die eine
/// Skriptsprache erreicht, ist das billiger als jede Indirektion. Wird sie einmal zu duenn
/// besetzt, ist der Umbau lokal — sie hat genau einen Aufrufer.</para>
/// </summary>
internal sealed class DispatchTable
{
    /// <summary>Zeile = konkreter Typ, Spalte = Interface, Zelle = Funktionsindex je Slot.
    /// <c>null</c> heisst „implementiert nicht" — der Loader schliesst aus, dass ein
    /// <c>callvirt</c> je dort landet.</summary>
    private readonly int[]?[,] _rows;

    /// <summary>Zeile = Interface, Spalte = Slot, Zelle = Argumentzahl <b>inklusive</b> Empfaenger.
    ///
    /// <para>Die Arity gehoert hierher und nicht an die Instruktion: der Interpreter muss den
    /// Empfaenger vom Stack lesen, <i>bevor</i> er die Zielfunktion kennt — er braucht ja dessen
    /// konkreten Typ, um sie ueberhaupt zu finden. Ohne vorab bekannte Tiefe waere das ein
    /// Henne-Ei-Problem. Loesbar ist es, weil alle Implementierungen eines Slots dieselbe Signatur
    /// haben; das hat die Sema als Konformanz geprueft.</para></summary>
    private readonly int[]?[] _arity;

    private DispatchTable(int[]?[,] rows, int[]?[] arity)
    {
        _rows = rows;
        _arity = arity;
    }

    public static DispatchTable Build(BytecodeModule module)
    {
        var size = module.Types.Count;
        var rows = new int[]?[size, size];
        var arity = new int[]?[size];

        foreach (var impl in module.Impls)
        {
            var methods = new int[impl.Methods.Count];
            for (var i = 0; i < methods.Length; i++) methods[i] = impl.Methods[i];
            rows[impl.Type, impl.Interface] = methods;

            // Die erste Zeile eines Interfaces legt die Signaturen fest; jede weitere muss
            // dieselben haben, sonst waere die Konformanz verletzt.
            if (arity[impl.Interface] is not null) continue;

            var counts = new int[impl.Methods.Count];
            for (var i = 0; i < counts.Length; i++)
            {
                var target = impl.Methods[i];
                counts[i] = target < module.Imports.Count
                    ? module.Imports[target].ParamTypes.Count
                    : module.Functions[target - module.Imports.Count].ParamCount;
            }

            arity[impl.Interface] = counts;
        }

        return new DispatchTable(rows, arity);
    }

    /// <summary>Wie viele Werte der Aufruf vom Stack nimmt, Empfaenger eingerechnet.</summary>
    public int ArityOf(int interfaceType, int slot)
    {
        if (interfaceType >= 0 && interfaceType < _arity.Length
            && _arity[interfaceType] is { } counts && slot >= 0 && slot < counts.Length)
            return counts[slot];

        throw new LyricRuntimeException(VmDiagnostics.NoImplementation,
            $"interface {interfaceType} has no implementation at all, so slot {slot} has no "
            + "signature — the module was not validated at load time");
    }

    /// <summary>
    /// Der Funktionsindex im gemeinsamen Raum (erst Imports, dann Funktionen).
    ///
    /// <para>Wirft nur, wenn die Load-Zeit-Validierung umgangen wurde — etwa weil ein Host das
    /// Modul selbst zusammengesetzt hat. Im regulaeren Weg ist der Fall unerreichbar, und die
    /// Meldung sagt das auch, damit niemand sie fuer eine erwartbare Laufzeitbedingung haelt.
    /// </para>
    /// </summary>
    public int Resolve(int concreteType, int interfaceType, int slot)
    {
        if (concreteType >= 0 && concreteType < _rows.GetLength(0)
            && interfaceType >= 0 && interfaceType < _rows.GetLength(1)
            && _rows[concreteType, interfaceType] is { } methods
            && slot >= 0 && slot < methods.Length)
            return methods[slot];

        throw new LyricRuntimeException(VmDiagnostics.NoImplementation,
            $"no implementation for slot {slot} of interface {interfaceType} on type "
            + $"{concreteType} — the module was not validated at load time");
    }
}
