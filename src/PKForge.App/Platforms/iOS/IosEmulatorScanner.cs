using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App;

/// <summary>
/// iOS emulators (Delta, RetroArch…) keep their saves inside their own sandbox, so PKForge
/// scans a folder the player links in the Files app instead: every save-looking file in it
/// (and in its subfolders) that the engine can read becomes a detected save.
/// </summary>
public sealed class IosEmulatorScanner(ISaveEngine engine) : IEmulatorDetectionService
{
    private const long MaxSaveBytes = 64L * 1024 * 1024;

    public ValueTask<EmulatorScanResult> ScanAsync(string treeId, EmulatorKind kind, CancellationToken cancellationToken = default) =>
        new(Task.Run(() =>
        {
            var trace = new List<string>();
            try
            {
                return IosFiles.Access(treeId, root => Scan(root, treeId, kind, trace, cancellationToken), trace);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                trace.Add($"IOS SCAN FAILED {error.GetType().Name}: {error.Message}");
                Services.AppLog.Error("scan", "iOS folder scan failed", error);
                return new EmulatorScanResult([], 0, [], trace);
            }
        }, cancellationToken));

    private EmulatorScanResult Scan(string root, string treeId, EmulatorKind kind, List<string> trace, CancellationToken cancellationToken)
    {
        var found = new List<DetectedSave>();
        var rejected = new List<string>();
        var seen = 0;
        trace.Add($"IOS ROOT path={root} exists={Directory.Exists(root)}");
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = false };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen++;
            var name = Path.GetFileName(file);
            if (seen <= 40) trace.Add($"IOS FILE {Path.GetRelativePath(root, file)}");
            if (!EmulatorSaveHeuristics.IsCandidateFileName(name, kind) && !EmulatorSaveHeuristics.IsCandidateFileName(name))
                continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > MaxSaveBytes) continue;
                var bytes = File.ReadAllBytes(file);
                var description = engine.TryDescribe(bytes, name);
                trace.Add($"IOS CANDIDATE {name} bytes={bytes.Length} parsed={(description is null ? "no" : description.GameName)}");
                if (description is null)
                {
                    rejected.Add(name);
                    continue;
                }
                var folder = Path.GetDirectoryName(file) ?? root;
                var siblings = Directory.EnumerateFiles(folder).Select(sibling => Path.GetFileName(sibling)).ToList();
                var romName = EmulatorSaveHeuristics.FindSiblingRom(name, siblings);
                var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, name, romName, name);
                var relative = Path.GetRelativePath(root, file);
                var id = IosFiles.ChildId(treeId, relative);
                found.Add(new DetectedSave(
                    id, name, guess.Label, kind,
                    EmulatorSaveHeuristics.RequiresExtraCare(kind),
                    new DateTimeOffset(info.LastWriteTimeUtc),
                    description.Generation, description.TrainerName, description.PlayTime,
                    guess, romName, Path.GetDirectoryName(relative),
                    Language: description.Language));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                rejected.Add($"{name}: {error.Message}");
                trace.Add($"IOS READ FAILED {name}: {error.GetType().Name} {error.Message}");
            }
        }
        trace.Add($"IOS DONE files={seen} saves={found.Count}");
        return new EmulatorScanResult(EmulatorSaveHeuristics.Normalize(found), seen, rejected, trace);
    }
}
