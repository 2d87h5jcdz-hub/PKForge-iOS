using Foundation;
using PKForge.Domain;
using UIKit;
using UniformTypeIdentifiers;

namespace PKForge.App;

/// <summary>Presents the Files app's picker and hands back the urls the player chose.</summary>
internal static class IosPicker
{
    // UIKit holds the picker's delegate weakly. Without these strong references the managed
    // delegate can be collected while the picker is open, and the pick never reaches the app.
    private static UIDocumentPickerViewController? _picker;
    private static PickerDelegate? _delegate;

    private sealed class PickerDelegate(TaskCompletionSource<NSUrl[]> done) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) =>
            Finish(done, urls ?? []);

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url) =>
            Finish(done, url is null ? [] : [url]);

        public override void WasCancelled(UIDocumentPickerViewController controller) => Finish(done, []);
    }

    /// <summary>Swiping the sheet down closes the picker without "Cancel": still a cancel for the app.</summary>
    private sealed class DismissDelegate(TaskCompletionSource<NSUrl[]> done) : UIAdaptivePresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => Finish(done, []);
    }

    private static DismissDelegate? _dismiss;

    private static void Finish(TaskCompletionSource<NSUrl[]> done, NSUrl[] urls)
    {
        Services.AppLog.Info("ios", $"Picker returned {urls.Length} item(s)");
        done.TrySetResult(urls);
        _picker = null;
        _delegate = null;
        _dismiss = null;
    }

    public static Task<NSUrl[]> PickAsync(UTType[] types, bool multiple, CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            var done = new TaskCompletionSource<NSUrl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var picker = new UIDocumentPickerViewController(types, false)
            {
                AllowsMultipleSelection = multiple,
                ShouldShowFileExtensions = true,
            };
            var pickerDelegate = new PickerDelegate(done);
            picker.Delegate = pickerDelegate;
            _picker = picker;
            _delegate = pickerDelegate;
            _dismiss = new DismissDelegate(done);
            if (picker.PresentationController is { } presentation) presentation.Delegate = _dismiss;
            cancellationToken.Register(() => MainThread.BeginInvokeOnMainThread(() =>
            {
                picker.DismissViewController(true, null);
                Finish(done, []);
            }));
            var top = Platform.GetCurrentUIViewController()
                ?? throw new InvalidOperationException("No screen is available to show the file picker.");
            Services.AppLog.Info("ios", $"Presenting picker for {string.Join(",", types.Select(t => t.Identifier))} (multiple: {multiple})");
            top.PresentViewController(picker, true, null);
            return done.Task;
        });
}

public sealed class IosDocumentPicker : IDocumentPicker
{
    public async ValueTask<PickedDocument?> PickSaveAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Item], false, cancellationToken).ConfigureAwait(false);
        return urls.Length == 0 ? null : new PickedDocument(IosFiles.Remember(urls[0]), IosFiles.DisplayName(urls[0]));
    }

    public async ValueTask<IReadOnlyList<PickedDocument>> PickManyAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Item], true, cancellationToken).ConfigureAwait(false);
        return urls.Select(url => new PickedDocument(IosFiles.Remember(url), IosFiles.DisplayName(url))).ToList();
    }
}

public sealed class IosFolderPicker : IFolderPicker
{
    public async ValueTask<PickedFolder?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var urls = await IosPicker.PickAsync([UTTypes.Folder], false, cancellationToken).ConfigureAwait(false);
        return urls.Length == 0 ? null : new PickedFolder(IosFiles.Remember(urls[0]), IosFiles.DisplayName(urls[0]));
    }
}

public sealed class IosSaveFileAccess : ISaveFileAccess
{
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => (ReadOnlyMemory<byte>)IosFiles.Access(documentId, File.ReadAllBytes), cancellationToken));

    public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var copy = bytes.ToArray();
        return new ValueTask(Task.Run(() => IosFiles.Access(documentId, path =>
        {
            IosFiles.WriteBytes(path, copy);
            var written = new FileInfo(path).Length;
            if (written != copy.Length)
                throw new IOException($"The save was written as {written} bytes instead of {copy.Length}.");
        }), cancellationToken));
    }
}

public sealed class IosFolderFileAccess : IFolderFileAccess
{
    public ValueTask<IReadOnlyList<PickedDocument>> ListFilesAsync(string treeId, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => (IReadOnlyList<PickedDocument>)IosFiles.Access(treeId, root =>
            Directory.EnumerateFiles(root)
                .Select(file => Path.GetFileName(file))
                .Where(name => !name.EndsWith(".pkforge-tmp", StringComparison.Ordinal))
                .Select(name => new PickedDocument(IosFiles.ChildId(treeId, name), name))
                .ToList()), cancellationToken));

    public ValueTask<ReadOnlyMemory<byte>> ReadFileAsync(string documentId, CancellationToken cancellationToken = default) =>
        new(Task.Run(() => (ReadOnlyMemory<byte>)IosFiles.Access(documentId, File.ReadAllBytes), cancellationToken));

    public ValueTask WriteFileAsync(string treeId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var copy = bytes.ToArray();
        return new ValueTask(Task.Run(() => IosFiles.Access(treeId, root => IosFiles.WriteBytes(Path.Combine(root, fileName), copy)),
            cancellationToken));
    }
}

/// <summary>An iPhone has one screen: the lower-screen mirror stays inside the main UI.</summary>
public sealed class IosSecondaryDisplayHost : ISecondaryDisplayHost
{
    public bool IsAvailable => false;
    public ValueTask ShowAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DismissAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>iOS has no home-screen widget for the Poképark (yet): publishing is a no-op.</summary>
public static class PokeparkWidgetPublisher
{
    public const int FrameWidth = 640;
    public const int FrameHeight = 360;
    public const int FrameCount = 8;
    public const int FrameIntervalMilliseconds = 400;
    public static bool HasActiveWidgets() => false;
    public static void Publish(IReadOnlyList<byte[]> pngFrames) { }
}
