namespace DriveRedirect.Desktop.ViewModels;

public sealed record NavigationItem(
    string Key,
    string Title,
    string Glyph,
    PageViewModelBase Page)
{
    public override string ToString() => Title;
}

public sealed record OptionItem(string Label, string Value)
{
    public override string ToString() => Label;
}
