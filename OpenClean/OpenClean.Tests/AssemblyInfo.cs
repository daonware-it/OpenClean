using Xunit;

// IntegrityState ist ein prozessweiter Zustand. Würden Testklassen parallel laufen, könnte ein
// Test, der ihn auf "gesperrt" setzt, einem anderen die Datei-Löschung wegnehmen. Die Suite ist
// klein und schnell – Parallelität bringt hier nichts, kostet aber Verlässlichkeit.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
