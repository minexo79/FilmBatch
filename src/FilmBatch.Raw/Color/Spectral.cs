// 移植自 LightCraft crates/raw/src/spectral.rs（實測光譜相機矩陣）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Color;

public static partial class Spectral
{
    public sealed class Camera(string make, string model, Mat3[] colorMatrix, Mat3[] forwardMatrix)
    {
        public string Make { get; } = make;
        public string Model { get; } = model;
        public Mat3[] ColorMatrix { get; } = colorMatrix;
        public Mat3[] ForwardMatrix { get; } = forwardMatrix;

        public bool Fill(ColorData color)
        {
            if (CameraColor.HasMatrix(color)) return false;
            color.Illuminant = [17, 21];
            color.ColorMatrix = [ColorMatrix[0], ColorMatrix[1]];
            color.ForwardMatrix = [ForwardMatrix[0], ForwardMatrix[1]];
            return true;
        }
    }

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string Brand(string make)
    {
        var words = Words(make);
        return words.Length == 0 ? "" : words[0].ToUpperInvariant();
    }

    private static string Key(string brand, string model)
    {
        var words = Words(model);
        for (var i = 0; i < words.Length; i++) words[i] = words[i].ToUpperInvariant();
        var start = 0;
        var end = words.Length;
        if (start < end && words[start] == brand) start++;
        if (start < end && words[start] == "EOS") start++;
        if (end - start > 1 && words[end - 1] == "DIGITAL") end--;
        return string.Join(" ", words, start, end - start);
    }

    public static Camera? Find(string? make, string model)
    {
        var makeBrand = make is null ? null : Brand(make);
        if (makeBrand == "") makeBrand = null;
        bool Same(string rowMake, string rowModel)
        {
            var rowBrand = Brand(rowMake);
            var wanted = Key(rowBrand, model);
            return (makeBrand is null || makeBrand == rowBrand) && wanted.Length > 0 && Key(rowBrand, rowModel) == wanted;
        }
        foreach (var alias in TableAliases)
        {
            if (!Same(alias.Make, alias.Model)) continue;
            foreach (var camera in TableCameras)
                if (Brand(camera.Make) == Brand(alias.Make) && camera.Model == alias.Target) return camera;
            return null;
        }
        foreach (var camera in TableCameras) if (Same(camera.Make, camera.Model)) return camera;
        return null;
    }

    public static Camera? FindCamera(string camera)
    {
        var words = Words(camera);
        if (words.Length == 0) return null;
        for (var i = 1; i < words.Length; i++)
        {
            var row = Find(string.Join(" ", words, 0, i), string.Join(" ", words, i, words.Length - i));
            if (row is not null) return row;
        }
        return Find(null, camera);
    }

    public static Xy? AsShotWhiteThrough(Camera camera, ColorData color, float[]? wbMultipliers)
    {
        if (color.AsShotWhiteXy is null && color.AsShotNeutral is null && !CameraColor.UsableWb(wbMultipliers)) return null;
        var c = color.Clone();
        c.Profile = new ProfileLook();
        camera.Fill(c);
        return CameraColor.AsShotWhiteOf(c, wbMultipliers);
    }

    public static IReadOnlyList<Camera> Cameras() => TableCameras;
    internal static IReadOnlyList<(string Make, string Model, string Target)> Aliases => TableAliases;
}
