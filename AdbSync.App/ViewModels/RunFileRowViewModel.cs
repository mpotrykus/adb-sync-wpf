using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Models.Orchestration.RunHistory;
using System.Windows;
using System.Windows.Media;

namespace AdbSync.App.ViewModels;

public sealed class RunFileRowViewModel(RunFileChange change)
{
    public string DeviceName { get; } = change.DeviceName;
    public SyncPhase Phase { get; } = change.Phase;
    public RunFileAction Action { get; } = change.Action;
    public string StepText { get; } = change.Phase == SyncPhase.PreConnect ? "Connect" : change.Phase.ToString();
    public string ActionText { get; } = change.Action.ToString();
    public string Path { get; } = change.Path;
    public string SizeText { get; } = change.Size is { } size ? RunHistoryRowViewModel.FormatSize(size) : "";
    public string? Detail { get; } = change.Detail;

    /// <summary>The first <see cref="FolderDepth"/> folders of the path, so a deep tree groups into a manageable number of folders.</summary>
    public string FolderKey { get; } = ToFolderKey(change.Path);

    private const int FolderDepth = 2;

    private static string ToFolderKey(string path)
    {
        if (path.Length == 0)
            return "(no file)";

        var folders = path.Replace('\\', '/').Split('/')[..^1];
        return folders.Length == 0 ? "(root)" : string.Join("/", folders.Take(FolderDepth));
    }

    public Brush ActionBrush { get; } = (Brush)Application.Current.Resources[change.Action switch
    {
        RunFileAction.Deleted or RunFileAction.Error => "Brush.Danger",
        RunFileAction.Conflict or RunFileAction.Held => "Brush.Warning",
        _ => "Brush.Success",
    }];
}
