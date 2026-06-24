namespace SpeedEmulator.ViewModels;

public sealed class DialogCloseRequestedEventArgs : EventArgs
{
    public DialogCloseRequestedEventArgs(bool dialogResult)
    {
        DialogResult = dialogResult;
    }

    public bool DialogResult { get; }
}
