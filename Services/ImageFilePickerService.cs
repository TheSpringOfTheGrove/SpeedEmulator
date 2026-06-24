using Microsoft.Win32;

namespace SpeedEmulator.Services;

public interface IImageFilePickerService
{
    string? PickSealImagePath();
}

public sealed class ImageFilePickerService : IImageFilePickerService
{
    public string? PickSealImagePath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择印章图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
