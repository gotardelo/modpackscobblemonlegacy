using System.Text.RegularExpressions;
using System.Windows;

namespace CobblemonLegacy;

public partial class LoginChoiceWindow : Window
{
    private static readonly Regex NicknameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    public LoginChoiceWindow(string currentNickname)
    {
        InitializeComponent();
        NicknameTextBox.Text = string.IsNullOrWhiteSpace(currentNickname) ? "Player" : currentNickname;
    }

    public string SelectedAuthMode { get; private set; } = "";
    public string OfflineNickname { get; private set; } = "";

    private void MicrosoftButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedAuthMode = AuthModes.Microsoft;
        DialogResult = true;
    }

    private void OfflineButton_Click(object sender, RoutedEventArgs e)
    {
        ChoicePanel.Visibility = Visibility.Collapsed;
        NicknamePanel.Visibility = Visibility.Visible;
        NicknameTextBox.Focus();
        NicknameTextBox.SelectAll();
    }

    private void SaveNicknameButton_Click(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameTextBox.Text.Trim();
        if (!NicknameRegex.IsMatch(nickname))
        {
            NicknameErrorText.Text = "Use 3 a 16 caracteres: letras, numeros ou underline.";
            return;
        }

        OfflineNickname = nickname;
        SelectedAuthMode = AuthModes.Offline;
        DialogResult = true;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        NicknameErrorText.Text = "";
        NicknamePanel.Visibility = Visibility.Collapsed;
        ChoicePanel.Visibility = Visibility.Visible;
    }
}
