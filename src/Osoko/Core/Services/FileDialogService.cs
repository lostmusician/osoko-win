using Microsoft.Win32;

namespace Osoko.Core.Services;

/// <summary>Native (Win32 common dialog) implementation of <see cref="IFileDialogService"/>.</summary>
public sealed class FileDialogService : IFileDialogService
{
    public string? PickSavePath(string suggestedFileName, string initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            FileName = suggestedFileName,
            DefaultExt = ".png",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
            ValidateNames = true,
            CheckPathExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
