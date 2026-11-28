namespace WinGlance.App.ViewModels;

public static class Display
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    public static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{number:F1}%" : "—";
    public static string Bytes(double? value)
    {
        if (value is null || !double.IsFinite(value.Value)) return "—";
        var number = Math.Max(0, value.Value);
        int i = 0;
        while (number >= 1024 && i < Units.Length - 1) { number /= 1024; i++; }
        return $"{number:0.#} {Units[i]}";
    }
    public static string Rate(double? value) => value is null ? "—" : Bytes(value) + "/s";
}
