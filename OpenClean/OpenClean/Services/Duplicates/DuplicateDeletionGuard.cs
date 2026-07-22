namespace OpenClean.Services.Duplicates;

/// <summary>
/// Sicherheits-Guard für das Löschen von Duplikaten: stellt sicher, dass pro Gruppe IMMER
/// mindestens eine Kopie erhalten bleibt. Zentralisiert die Regel, die zuvor im normalen und im
/// sicheren Löschpfad getrennt (und minimal unterschiedlich) implementiert war.
/// </summary>
public static class DuplicateDeletionGuard
{
    /// <summary>
    /// Bestimmt aus den Dateien einer Gruppe die tatsächlich zu löschenden markierten Kopien.
    /// Sind ALLE Dateien der Gruppe markiert, wird die neueste (spätestes <paramref name="modified"/>)
    /// verschont – so bleibt garantiert eine Kopie übrig. <paramref name="sparedOne"/> meldet, ob
    /// eine Kopie geschont wurde (für die „übersprungen“-Zählung des Aufrufers).
    /// </summary>
    public static List<T> SelectDeletable<T>(
        IReadOnlyCollection<T> groupFiles,
        Func<T, bool> isSelected,
        Func<T, DateTime> modified,
        out bool sparedOne)
    {
        var selected = groupFiles.Where(isSelected).ToList();
        sparedOne = false;

        // Alle Kopien markiert -> die neueste behalten, damit nie eine ganze Gruppe restlos
        // verschwindet (auch wenn der Nutzer manuell alles markiert hat).
        if (selected.Count > 0 && selected.Count == groupFiles.Count)
        {
            var spared = selected.OrderByDescending(modified).First();
            selected.Remove(spared);
            sparedOne = true;
        }

        return selected;
    }
}
