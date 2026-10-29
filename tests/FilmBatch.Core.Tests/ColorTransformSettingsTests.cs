using System.Text.Json;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using FbSettings = FilmBatch.Core.Params.Settings;

namespace FilmBatch.Core.Tests;

/// <summary>Only film.look may contain imported film keys; resolving enabled transforms cannot silently bypass failures.</summary>
public sealed class ColorTransformSettingsTests
{
    private static readonly string LookKey = "look-" + new string('a', 64);
    private static readonly string UserKey = "user-" + new string('a', 16);

    [Fact]
    public void FilmChoice_AcceptsBothAssetKeyFormatsWithoutWideningOtherChoices()
    {
        var film = ModuleCatalog.Param("film.look");
        Assert.NotNull(film);
        Assert.Equal(LookKey, film!.Coerce(ParamValue.Of(LookKey)).AsText);
        Assert.Equal(UserKey, film.Coerce(ParamValue.Of(UserKey)).AsText);
        var unrelated = ParamDef.Choice("look", "Other", new ChoiceOption("default", "Default"));
        Assert.Equal("default", unrelated.Coerce(ParamValue.Of(LookKey)).AsText);
        Assert.Equal("default", unrelated.Coerce(ParamValue.Of(UserKey)).AsText);
        Assert.Equal(film.Default.AsText, film.Coerce(ParamValue.Of("look-INVALID")).AsText);
    }

    [Fact]
    public void SettingsRoundTrip_AcceptsOnlyFilmLookTransformKey()
    {
        var settings = FbSettings.Empty.With("film.look", LookKey);
        var text = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<FbSettings>(text);
        Assert.NotNull(restored);
        Assert.Equal(LookKey, restored!.Text("film.look"));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<FbSettings>("{\"raw.wb_mode\":\"" + LookKey + "\"}"));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<FbSettings>("{\"film.look\":\"look-INVALID\"}"));
    }

    [Fact]
    public void Resolve_UnknownLegacyBypassesButMissingNewAssetFailsVisibly()
    {
        Assert.Null(FilmLookLibrary.Resolve("not-a-film", TestContext.Current.CancellationToken));
        var key = "look-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var ex = Assert.Throws<ColorTransformAssetException>(() => FilmLookLibrary.Resolve(key, TestContext.Current.CancellationToken));
        Assert.NotNull(ex.InnerException);
        var builtIn = FilmLookLibrary.Resolve(FilmLookLibrary.DefaultKey, TestContext.Current.CancellationToken);
        Assert.NotNull(builtIn?.Lut);
        Assert.Null(builtIn!.Transform);
    }
}
