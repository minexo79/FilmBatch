using System.Numerics;
using System.Text;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.Core.Tests;

public class CubeLutTests
{
    private const string Identity2 = """
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    [Fact]
    public void IdentityCube_SamplesNodes()
    {
        var lut = CubeLut.Parse(Identity2);
        Assert.Equal(2, lut.Size);
        Assert.Equal(new Vector3(1, 0, 0), lut.Sample(new Vector3(1, 0, 0)));
        Assert.Equal(new Vector3(0, 1, 1), lut.Sample(new Vector3(0, 1, 1)));
    }

    [Fact]
    public void RejectsNonUnitDomain()
    {
        var text = Identity2.Replace("LUT_3D_SIZE 2", "DOMAIN_MIN 0.1 0 0\nLUT_3D_SIZE 2");
        Assert.Throws<InvalidDataException>(() => CubeLut.Parse(text));
    }

    [Fact]
    public void RejectsOneDShaper() =>
        Assert.Throws<InvalidDataException>(() => CubeLut.Parse("LUT_1D_SIZE 2\n0\n1\n"));

    [Fact]
    public void OversizedCube_ResamplesToMax()
    {
        var size = FilmLutFile.MaxSize + 1;
        var text = new StringBuilder($"LUT_3D_SIZE {size}\n");
        for (var i = 0; i < size * size * size; i++) text.Append("0 0 0\n");
        Assert.Equal(FilmLutFile.MaxSize, CubeLut.Parse(text.ToString()).Size);
    }

    [Fact]
    public void Import_RoundTripsThroughFilmSlotKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-luts-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideRoot = root;
        UserLutLibrary.Reset();
        try
        {
            var key = UserLutLibrary.Import(CubeLut.Parse(Identity2), "mine");
            Assert.True(UserLutLibrary.IsUserKey(key));
            UserLutLibrary.Reset();
            var again = FilmLookLibrary.TryLut(key);
            Assert.NotNull(again);
            Assert.Equal(new Vector3(1, 0, 0), again!.Sample(new Vector3(1, 0, 0)));
            Assert.Equal(key, ModuleCatalog.Param("film.look")!.Coerce(ParamValue.Of(key)).AsText);
        }
        finally
        {
            AppPaths.OverrideRoot = null;
            UserLutLibrary.Reset();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
