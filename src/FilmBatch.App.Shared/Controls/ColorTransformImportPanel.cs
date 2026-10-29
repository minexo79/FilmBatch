using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FilmBatch.App.Localization;
using FilmBatch.Core.Film;

namespace FilmBatch.App.Controls;

/// <summary>Displays CLF descriptors and the user's explicit stage and encoding declaration.</summary>
public sealed class ColorTransformImportPanel : StackPanel
{
    private readonly ComboBox _stage;
    private readonly ComboBox _input;
    private readonly ComboBox _output;

    public ColorTransformImportPanel(ColorTransform transform)
    {
        Spacing = 8;
        Children.Add(Label(Loc.Get("Film.Transform.Declaration")));
        AddDescriptor("Film.Transform.Description", transform.Description);
        AddDescriptor("Film.Transform.InputDescriptor", transform.InputDescriptor);
        AddDescriptor("Film.Transform.OutputDescriptor", transform.OutputDescriptor);
        _stage = Choice("Film.Transform.Stage", [StageLabel(ColorTransformStage.WorkingLinear), StageLabel(ColorTransformStage.DisplayEncoded)], 1);
        _input = Choice("Film.Transform.Input", Encodings(), 1);
        _output = Choice("Film.Transform.Output", Encodings(), 1);
    }

    public ClfImportOptions Options => new((ColorTransformStage)_stage.SelectedIndex,
        (ColorTransformEncoding)_input.SelectedIndex, (ColorTransformEncoding)_output.SelectedIndex);

    public static string StageLabel(ColorTransformStage stage) => Loc.Get(stage == ColorTransformStage.WorkingLinear
        ? "Film.Transform.Working" : "Film.Transform.Display");

    public static string EncodingLabel(ColorTransformEncoding encoding) => Loc.Get(encoding switch
    {
        ColorTransformEncoding.LinearSrgb => "Film.Transform.LinearSrgb",
        ColorTransformEncoding.Srgb => "Film.Transform.Srgb",
        ColorTransformEncoding.ProPhotoGamma18 => "Film.Transform.ProPhoto",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    });

    private static string[] Encodings() =>
        [EncodingLabel(ColorTransformEncoding.LinearSrgb), EncodingLabel(ColorTransformEncoding.Srgb), EncodingLabel(ColorTransformEncoding.ProPhotoGamma18)];

    private static TextBlock Label(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };

    private void AddDescriptor(string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Children.Add(Label(Loc.Get(label) + ": " + value));
    }

    private ComboBox Choice(string label, string[] values, int selected)
    {
        Children.Add(Label(Loc.Get(label)));
        var choice = new ComboBox { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        Children.Add(choice);
        return choice;
    }
}
