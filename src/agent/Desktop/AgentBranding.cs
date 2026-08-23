namespace CodexControl.Agent.Desktop;

internal static class AgentBranding
{
    private const string LogoResourceName = "CodexControl.Agent.Desktop.Assets.CodexControlLogo.png";
    private const string IconResourceName = "CodexControl.Agent.Desktop.Assets.CodexControl.ico";
    private static readonly Lazy<Image> Logo = new(LoadLogo);
    private static readonly Lazy<Icon> AppIcon = new(LoadApplicationIcon);

    public static Image LogoImage => Logo.Value;

    public static Icon ApplicationIcon => AppIcon.Value;

    private static Image LoadLogo()
    {
        using var stream = typeof(AgentBranding).Assembly.GetManifestResourceStream(LogoResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源：{LogoResourceName}");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private static Icon LoadApplicationIcon()
    {
        using (var stream = typeof(AgentBranding).Assembly.GetManifestResourceStream(IconResourceName))
        {
            if (stream is not null)
            {
                using var source = new Icon(stream);
                return (Icon)source.Clone();
            }
        }

        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }
        catch (ArgumentException)
        {
            // Test hosts and unusual app-host paths can lack an extractable icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
