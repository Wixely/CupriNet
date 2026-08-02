using Avalonia.Controls;

namespace CupriChatLite;

/// <summary>The receiver's decision on an incoming file offer. Default (window closed) is Dismiss.</summary>
public enum FileOfferResult
{
    Dismiss = 0,
    Accept = 1,
    Disable = 2,
}

public partial class FileOfferDialog : Window
{
    public FileOfferDialog()
    {
        InitializeComponent();
    }

    public FileOfferDialog(string info) : this()
    {
        this.FindControl<TextBlock>("InfoText")!.Text = info;
        this.FindControl<Button>("AcceptButton")!.Click += (_, _) => Close(FileOfferResult.Accept);
        this.FindControl<Button>("DismissButton")!.Click += (_, _) => Close(FileOfferResult.Dismiss);
        this.FindControl<Button>("DisableButton")!.Click += (_, _) => Close(FileOfferResult.Disable);
    }
}
