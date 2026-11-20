// H.I.B for Windows — drag files out of the app (Flick cards, received files) and into it (Send to…).
// Made by Haitham Hamid.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace HIB.Services;

public static class FileDrag
{
    /// <summary>True while we are dragging one of our own files out (so our window ignores the drop).</summary>
    public static bool IsDraggingOut { get; private set; }

    /// <summary>Starts an OS drag of <paramref name="path"/> from <paramref name="source"/> (call from PointerPressed/Moved).</summary>
    public static async Task<DragDropEffects> StartAsync(Control source, PointerPressedEventArgs press, string path)
    {
        var top = TopLevel.GetTopLevel(source);
        if (top?.StorageProvider is not { } storage) return DragDropEffects.None;
        var file = await storage.TryGetFileFromPathAsync(path);
        if (file is null) return DragDropEffects.None;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(file));
        IsDraggingOut = true;
        try { return await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy); }
        finally { IsDraggingOut = false; }
    }

    /// <summary>Local paths of files being dropped (empty when the drag holds no files).</summary>
    public static List<string> LocalPaths(DragEventArgs e)
        => e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList()
           ?? new List<string>();
}
