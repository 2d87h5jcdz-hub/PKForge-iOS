using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// The app's own Documents folder shows up in the Files app as "On My iPhone › PKForge".
/// It is linked automatically on first launch, so a save copied there appears on the shelf
/// with no picker at all (Delta: Export Save File › On My iPhone › PKForge).
/// </summary>
internal static class IosStartup
{
    public static string DocumentsPath => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static void EnsureDocumentsRoot(IServiceProvider services)
    {
        try
        {
            var documents = DocumentsPath;
            Directory.CreateDirectory(documents);
            var hint = Path.Combine(documents, "Coloque seus saves aqui.txt");
            if (!File.Exists(hint))
                File.WriteAllText(hint,
                    "Copie para esta pasta os arquivos .sav exportados do Delta (ou de outro emulador).\n" +
                    "O PKForge encontra os saves sozinho ao abrir ou em Settings > Rescan games.\n" +
                    "Depois de editar, importe o .sav de volta no Delta (Import Save File).\n");

            var roots = services.GetService<IWatchedRootStore>();
            if (roots is null) return;
            if (roots.GetRoots().Any(root => root.TreeId == documents)) return;
            roots.AddRoot(new WatchedRoot(EmulatorKind.RetroArch, documents, "PKForge (Arquivos)"));
            Services.AppLog.Info("ios", $"Linked the app's Documents folder: {documents}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Services.AppLog.Warn("ios", $"Could not link the Documents folder: {error.Message}");
        }
    }
}
