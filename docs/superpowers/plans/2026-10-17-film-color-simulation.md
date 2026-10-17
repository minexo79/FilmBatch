# 底片顏色模擬（Hald CLUT ＋ 分色相修正）實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以底片顏色模擬取代現有 Recipe Lab／Sony 創意風格的 `film` 模組：選一卷底片（3D LUT）、調效果比例／暖色／褪色，再加上簡單的分色相修正（內建預設＋使用者自訂），並以目前照片算出的縮圖格狀選單挑選。

**Architecture:** 底片色彩改為一個逐像素 pass：線性光 → sRGB → 33³ 3D LUT（四面體內插）→ 線性 → OKLCh 八色相帶修正 → 暖色 → 褪色 → 與原圖依效果比例混合。LUT 由 RawTherapee Film Simulation Collection（CC BY-SA 4.0）的 Hald CLUT 離線轉成 `.fblut` 二進位檔，嵌入 Core。CPU（`CpuPipeline`）與 GPU（WGSL）兩份實作數學一致。舊的 Recipe Lab 配方、Sony 係數、校正工具、配方匯入匯出與相關 UI 全部移除，舊專案／預設裡的舊 `film.*` 欄位在讀取時靜默丟棄。

**Tech Stack:** .NET 10、Avalonia、CommunityToolkit.Mvvm、WebGPUSharp（WGSL compute）、Magick.NET（僅 LUT 轉檔工具）、xUnit v3。

**Spec:** 本對話中與使用者確認的決策（記錄於 2026-10-17）：
- 只要風格化調色（Hald CLUT ＋ 分色相微調），不做光譜／物理底片模型。
- 接受內附 CC BY-SA 4.0 的 LUT：在 `THIRD-PARTY-NOTICES.txt` 標示出處，LUT 檔沿用 CC BY-SA 4.0。
- 色相修正：簡單的色相修正（內建預設），另留「自訂」讓使用者自己調。
- 預覽：縮圖格狀選單，以**目前照片**的小縮圖顯示每種底片／色相修正的效果，點選即套用。
- 新模組**直接取代**舊的 Recipe Lab 模組。

## Global Constraints

- 新模組沿用模組 key `film`（處理順序、選擇性貼上分類 `PasteCategory.Color`、模組分組 `ModuleGroup.Film` 不變）；中文名稱「底片風格」，英文「Film Look」。
- 保留的舊參數：`film.enabled`、`film.strength`（0–100，預設 100）。
- 移除的舊參數（讀取舊檔時靜默丟棄，不報錯）：`film.recipe`、`film.style`、`film.saturation`、`film.contrast`、`film.sharpness`、`film.white_balance`、`film.amber_blue`、`film.green_magenta`、`film.effect`、`film.effect_option`、`film.exposure`、`film.dro`、`film.wb_reference`。
- 新參數：`film.look`（Choice，預設 `kodak-portra-400`）、`film.warmth`（−100–100，預設 0）、`film.fade`（0–100，預設 0），以及 24 個 `film.hue_{band}_{h|s|l}`（−100–100，預設 0），band 依序為 `red, orange, yellow, green, aqua, blue, purple, magenta`。
- LUT 邊長固定 33；`.fblut` 格式：8 位元組 magic `FBLUT1\0\0`、int32 邊長、邊長³×3 個 little-endian `ushort`（0–65535 對應 0–1），順序 `((b*N + g)*N + r)*3 + channel`（與 Hald CLUT 像素順序相同）。
- 八色相帶中心（OKLCh 色相角，度）：`25, 55, 105, 140, 195, 255, 295, 335`；C#（`HueBands.CentersDegrees`）與 WGSL 必須同值。
- 數學常數（CPU 與 GPU 必須相同）：色相 ±100 → ±30°；飽和 ±100 → 色度 ×(1±1)；明度 ±100 → OKLab L ±0.12；色相／明度依色度閘門 `smoothstep(0.02, 0.08, C)`；暖色 ±100 → R×(1±0.12)、B×(1∓0.12)（線性光）；褪色 0–100 → sRGB `x*(1−0.2f)+0.1f`。
- CPU 與 GPU 版必須同步（`Gpu_MatchesCpu_*` 測試）；C# uniform struct 欄位順序與 WGSL `struct` 完全一致，16 位元組對齊。
- 模組關閉或 `film.strength == 0` 時輸出必須與輸入逐位元相同。
- 文件、註解、UI 字串以繁體中文為主，英文字串放 `Strings.en.resx`；兩份 resx 的 key 必須一致（`LocalizationTests`）。
- LUT 來源固定為 `http://rawtherapee.com/shared/HaldCLUT.zip`（2015-09-20 版，421,602,289 bytes）；README 授權為 CC BY-SA 4.0，作者 Pat David、Pavlov Dmitry、Michael Ezra。

---

## 檔案結構

**新增**

| 檔案 | 職責 |
|---|---|
| `src/FilmBatch.Core/Film/FilmLut.cs` | 3D LUT 資料、四面體取樣、重新取樣、轉成 GPU 紋理排列 |
| `src/FilmBatch.Core/Film/FilmLutFile.cs` | `.fblut` 讀寫 |
| `src/FilmBatch.Core/Film/FilmLookLibrary.cs` | 底片清單（讀嵌入的 `looks.json`）、依 key 取 LUT（快取） |
| `src/FilmBatch.Core/Film/HueBands.cs` | 八色相帶定義、24 個參數 schema、內建色相修正預設與比對 |
| `src/FilmBatch.Core/Film/Looks/looks.json` | 36 卷底片清單（key、名稱、分組、Hald 來源路徑） |
| `src/FilmBatch.Core/Film/Looks/*.fblut` | 36 個 33³ LUT（由工具產生後提交） |
| `src/FilmBatch.Core/Film/Looks/LICENSE.txt` | CC BY-SA 4.0 聲明、原作者、修改說明 |
| `src/FilmBatch.Core/Params/RetiredParams.cs` | 舊 `film.*` 參數清單（讀檔時丟棄） |
| `src/FilmBatch.Engine/Rendering/Oklab.cs` | OKLab 轉換（從 `FilmProcessing.SaturateOklab` 抽出） |
| `src/FilmBatch.Engine/Rendering/FilmLookUniforms.cs` | 與 `film/look.wgsl` 對齊的 uniform |
| `src/FilmBatch.Engine/Rendering/FilmLookProcessing.cs` | CPU 參考實作 |
| `src/FilmBatch.Gpu/Shaders/v1/film/look.wgsl` | GPU 實作 |
| `src/FilmBatch.App/ViewModels/FilmLookViewModel.cs` | 底片／色相修正縮圖格、色相帶編輯器 |
| `src/FilmBatch.App/ViewModels/FilmLookThumbnails.cs` | 以目前照片算縮圖（共用 PreviewService 縮圖佇列） |
| `src/FilmBatch.App/Controls/FilmLookControl.axaml(.cs)` | 縮圖格與色相帶編輯器 UI |
| `tools/film-luts/` | 從 HaldCLUT.zip 以 HTTP Range 抓取指定 PNG 並轉 `.fblut` |
| `docs/film-looks.md` | 新底片風格的來源、演算法、更新流程 |
| 測試：`tests/FilmBatch.Core.Tests/FilmLutTests.cs`、`FilmLookTests.cs`、`RetiredParamsTests.cs`；`tests/FilmBatch.App.Tests/FilmLookViewModelTests.cs` | |

**修改**：`ModuleCatalog.cs`、`SettingsJsonConverter.cs`、`PresetFile.cs`、`SamplePresets.cs`、`FilmBatch.Core.csproj`、`FilmBatch.Engine.csproj`、`ModuleParams.cs`、`CpuPipeline.cs`、`GpuEffectPipeline.cs`、`ModuleSectionViewModel.cs`、`InspectorViewModel.cs`、`MainWindowViewModel.cs`、`InspectorView.axaml`、`Strings.resx`、`Strings.en.resx`、`FilmBatch.Cli/Program.cs`、`AppPaths.cs`、`EffectPipelineTests.cs`、`PhotoOperationsTests.cs`、`FilmBatch.Core.Tests.csproj`、`THIRD-PARTY-NOTICES.txt`、`docs/architecture.md`、`README.md`。

**刪除**：`src/FilmBatch.Core/Film/{CreativeStyleTable,CustomRecipeFile,CustomRecipeModels,FilmRecipe,FilmRecipeLibrary,FilmRecipesData}.cs`、`creative_styles.json`、`recipelab.json`；`src/FilmBatch.Engine/Rendering/{FilmParameters,FilmProcessing}.cs`；`src/FilmBatch.Gpu/Shaders/v1/film/{common,film_pixel,dro_base,film_dro_sharpen,blend}.wgsl`；`src/FilmBatch.App/ViewModels/FilmRecipeViewModel.cs`、`Controls/FilmRecipeControl.axaml(.cs)`；`tools/film-calibration/`；`tests/FilmBatch.Core.Tests/{FilmRecipeTests,FilmCalibrationTests}.cs`、`Fixtures/Film/`；`tests/FilmBatch.App.Tests/FilmRecipeViewModelTests.cs`；`docs/film-calibration.md`、`docs/film-provenance.md`。

> 注意：`film/photo_plate.wgsl` 與 `film/tone.wgsl` 屬於其他模組，**不要刪**。刪 `film/common.wgsl` 前先 `git grep -n 'film/common.wgsl' src` 確認只剩被刪的 shader 在用。

---

### Task 1: 3D LUT 核心（FilmLut、四面體取樣、.fblut）

**Files:**
- Create: `src/FilmBatch.Core/Film/FilmLut.cs`
- Create: `src/FilmBatch.Core/Film/FilmLutFile.cs`
- Test: `tests/FilmBatch.Core.Tests/FilmLutTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class FilmLut { int Size; float[] Data; FilmLut(int size, float[] data); static FilmLut Identity(int size); static FilmLut FromHald(float[] rgb, int level); Vector3 Sample(Vector3 srgb); FilmLut Resample(int size); float[] ToTextureRgba(); }`
  - `public static class FilmLutFile { void Write(Stream, FilmLut); FilmLut Read(Stream); }`

- [ ] **Step 1: 寫失敗的測試**

```csharp
using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public class FilmLutTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(.2f, .7f, .45f)]
    [InlineData(.91f, .13f, .5f)]
    public void Identity_SamplesBackInput(float r, float g, float b)
    {
        var got = FilmLut.Identity(33).Sample(new Vector3(r, g, b));
        Assert.Equal(r, got.X, 5);
        Assert.Equal(g, got.Y, 5);
        Assert.Equal(b, got.Z, 5);
    }

    [Fact]
    public void Sample_ClampsOutOfRangeInput()
    {
        var got = FilmLut.Identity(17).Sample(new Vector3(-.5f, 1.5f, .5f));
        Assert.Equal(new Vector3(0, 1, .5f), got);
    }

    [Fact]
    public void Sample_HitsNodesExactly_OnArbitraryLut()
    {
        var random = new Random(3);
        var data = new float[5 * 5 * 5 * 3];
        for (var i = 0; i < data.Length; i++) data[i] = (float)random.NextDouble();
        var lut = new FilmLut(5, data);
        // 節點 (r=1, g=3, b=2) → index ((2*5+3)*5+1)*3
        var got = lut.Sample(new Vector3(1 / 4f, 3 / 4f, 2 / 4f));
        var i0 = ((2 * 5 + 3) * 5 + 1) * 3;
        Assert.Equal(data[i0], got.X, 5);
        Assert.Equal(data[i0 + 1], got.Y, 5);
        Assert.Equal(data[i0 + 2], got.Z, 5);
    }

    [Fact]
    public void FromHald_UsesHaldPixelOrder()
    {
        // level 2 → 邊長 4，影像 8×8；Hald 像素 k = r + g*4 + b*16
        var level = 2; var n = level * level;
        var rgb = new float[n * n * n * 3];
        for (var k = 0; k < n * n * n; k++)
        {
            rgb[k * 3] = k % n / (n - 1f);
            rgb[k * 3 + 1] = k / n % n / (n - 1f);
            rgb[k * 3 + 2] = k / (n * n) / (n - 1f);
        }
        var lut = FilmLut.FromHald(rgb, level);
        Assert.Equal(4, lut.Size);
        Assert.Equal(new Vector3(1 / 3f, 2 / 3f, 1), lut.Sample(new Vector3(1 / 3f, 2 / 3f, 1)));
    }

    [Fact]
    public void Resample_IdentityStaysIdentity()
    {
        var lut = FilmLut.Identity(144).Resample(33);
        Assert.Equal(33, lut.Size);
        var got = lut.Sample(new Vector3(.3f, .6f, .9f));
        Assert.Equal(.3f, got.X, 4);
        Assert.Equal(.6f, got.Y, 4);
        Assert.Equal(.9f, got.Z, 4);
    }

    [Fact]
    public void ToTextureRgba_LaysOutBlueSlicesHorizontally()
    {
        var lut = FilmLut.Identity(3);
        var texture = lut.ToTextureRgba(); // 寬 9（r + b*3），高 3（g）
        Assert.Equal(9 * 3 * 4, texture.Length);
        // (r=2, g=1, b=2) → x = 2 + 2*3 = 8, y = 1
        var t = (1 * 9 + 8) * 4;
        Assert.Equal(new[] { 1f, .5f, 1f, 1f }, texture[t..(t + 4)]);
    }

    [Fact]
    public void File_RoundTripsWithin16BitPrecision()
    {
        var lut = FilmLut.Identity(9);
        using var stream = new MemoryStream();
        FilmLutFile.Write(stream, lut);
        Assert.Equal(8 + 4 + 9 * 9 * 9 * 3 * 2, stream.Length);
        stream.Position = 0;
        var read = FilmLutFile.Read(stream);
        Assert.Equal(9, read.Size);
        for (var i = 0; i < lut.Data.Length; i++) Assert.Equal(lut.Data[i], read.Data[i], 4);
    }

    [Fact]
    public void File_RejectsWrongMagic()
    {
        using var stream = new MemoryStream(new byte[64]);
        Assert.Throws<InvalidDataException>(() => FilmLutFile.Read(stream));
    }
}
```

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~FilmLutTests"`
Expected: 編譯失敗，`FilmLut` / `FilmLutFile` 不存在。

- [ ] **Step 3: 實作 `FilmLut.cs`**

```csharp
using System.Numerics;

namespace FilmBatch.Core.Film;

/// <summary>
/// 3D 色彩查找表（sRGB 編碼輸入 → sRGB 編碼輸出）。資料順序與 Hald CLUT 像素相同：
/// <c>((b*N + g)*N + r)*3 + 通道</c>。取樣用四面體內插（中性灰沿對角線不會偏色，與 Resolve／darktable 相同）。
/// </summary>
public sealed class FilmLut
{
    public int Size { get; }
    public float[] Data { get; }

    public FilmLut(int size, float[] data)
    {
        if (size < 2) throw new ArgumentOutOfRangeException(nameof(size));
        if (data.Length != size * size * size * 3) throw new ArgumentException("LUT 資料長度必須為 size³×3。", nameof(data));
        Size = size;
        Data = data;
    }

    public static FilmLut Identity(int size)
    {
        var data = new float[size * size * size * 3];
        var max = size - 1f;
        for (var b = 0; b < size; b++)
        for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++)
        {
            var i = ((b * size + g) * size + r) * 3;
            data[i] = r / max; data[i + 1] = g / max; data[i + 2] = b / max;
        }
        return new FilmLut(size, data);
    }

    /// <summary>Hald CLUT（level L：邊長 L²，影像 L³×L³）的 RGB 像素（0–1）直接就是 LUT 資料。</summary>
    public static FilmLut FromHald(float[] rgb, int level) => new(level * level, rgb);

    public Vector3 Sample(Vector3 srgb)
    {
        var max = Size - 1;
        float pr = Math.Clamp(srgb.X, 0, 1) * max, pg = Math.Clamp(srgb.Y, 0, 1) * max, pb = Math.Clamp(srgb.Z, 0, 1) * max;
        int r = Math.Min((int)pr, max - 1), g = Math.Min((int)pg, max - 1), b = Math.Min((int)pb, max - 1);
        float fr = pr - r, fg = pg - g, fb = pb - b;
        var c000 = At(r, g, b);
        var c111 = At(r + 1, g + 1, b + 1);
        if (fr >= fg)
        {
            if (fg >= fb)
            {
                var c100 = At(r + 1, g, b); var c110 = At(r + 1, g + 1, b);
                return c000 + fr * (c100 - c000) + fg * (c110 - c100) + fb * (c111 - c110);
            }
            if (fr >= fb)
            {
                var c100 = At(r + 1, g, b); var c101 = At(r + 1, g, b + 1);
                return c000 + fr * (c100 - c000) + fb * (c101 - c100) + fg * (c111 - c101);
            }
            var c001 = At(r, g, b + 1); var c101b = At(r + 1, g, b + 1);
            return c000 + fb * (c001 - c000) + fr * (c101b - c001) + fg * (c111 - c101b);
        }
        if (fb >= fg)
        {
            var c001 = At(r, g, b + 1); var c011 = At(r, g + 1, b + 1);
            return c000 + fb * (c001 - c000) + fg * (c011 - c001) + fr * (c111 - c011);
        }
        if (fb >= fr)
        {
            var c010 = At(r, g + 1, b); var c011 = At(r, g + 1, b + 1);
            return c000 + fg * (c010 - c000) + fb * (c011 - c010) + fr * (c111 - c011);
        }
        var c010b = At(r, g + 1, b); var c110b = At(r + 1, g + 1, b);
        return c000 + fg * (c010b - c000) + fr * (c110b - c010b) + fb * (c111 - c110b);
    }

    /// <summary>在新邊長的每個節點上取樣本 LUT（工具把 144³ 的 Hald 縮成 33³ 用）。</summary>
    public FilmLut Resample(int size)
    {
        var data = new float[size * size * size * 3];
        var max = size - 1f;
        Parallel.For(0, size, b =>
        {
            for (var g = 0; g < size; g++)
            for (var r = 0; r < size; r++)
            {
                var v = Sample(new Vector3(r / max, g / max, b / max));
                var i = ((b * size + g) * size + r) * 3;
                data[i] = v.X; data[i + 1] = v.Y; data[i + 2] = v.Z;
            }
        });
        return new FilmLut(size, data);
    }

    /// <summary>GPU 紋理排列：寬 N²（x = r + b*N）、高 N（y = g），RGBA32F。</summary>
    public float[] ToTextureRgba()
    {
        var n = Size;
        var result = new float[n * n * n * 4];
        for (var b = 0; b < n; b++)
        for (var g = 0; g < n; g++)
        for (var r = 0; r < n; r++)
        {
            var src = ((b * n + g) * n + r) * 3;
            var dst = (g * n * n + b * n + r) * 4;
            result[dst] = Data[src]; result[dst + 1] = Data[src + 1]; result[dst + 2] = Data[src + 2]; result[dst + 3] = 1;
        }
        return result;
    }

    private Vector3 At(int r, int g, int b)
    {
        var i = ((b * Size + g) * Size + r) * 3;
        return new Vector3(Data[i], Data[i + 1], Data[i + 2]);
    }
}
```

- [ ] **Step 4: 實作 `FilmLutFile.cs`**

```csharp
using System.Text;

namespace FilmBatch.Core.Film;

/// <summary>FilmBatch 的 LUT 二進位格式：magic、int32 邊長、邊長³×3 個 16-bit unorm（little-endian）。</summary>
public static class FilmLutFile
{
    private static ReadOnlySpan<byte> Magic => "FBLUT1\0\0"u8;
    public const int MaxSize = 65;

    public static void Write(Stream stream, FilmLut lut)
    {
        stream.Write(Magic);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(lut.Size);
        foreach (var value in lut.Data) writer.Write((ushort)MathF.Round(Math.Clamp(value, 0, 1) * 65535));
    }

    public static FilmLut Read(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("不是 FilmBatch LUT 檔。");
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var size = reader.ReadInt32();
        if (size is < 2 or > MaxSize) throw new InvalidDataException($"LUT 邊長不合法：{size}");
        var data = new float[size * size * size * 3];
        for (var i = 0; i < data.Length; i++) data[i] = reader.ReadUInt16() / 65535f;
        return new FilmLut(size, data);
    }
}
```

> `File_RejectsWrongMagic` 用 64 個 0 位元組：magic 比對失敗即丟 `InvalidDataException`。

- [ ] **Step 5: 執行測試確認通過**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~FilmLutTests"`
Expected: 全部 PASS。

- [ ] **Step 6: Commit**

```bash
git add src/FilmBatch.Core/Film/FilmLut.cs src/FilmBatch.Core/Film/FilmLutFile.cs tests/FilmBatch.Core.Tests/FilmLutTests.cs
git commit -m "Add 3D LUT core with tetrahedral sampling and .fblut format"
```

---

### Task 2: 底片清單、LUT 轉檔工具與內附 LUT

**Files:**
- Create: `src/FilmBatch.Core/Film/Looks/looks.json`
- Create: `src/FilmBatch.Core/Film/Looks/LICENSE.txt`
- Create: `src/FilmBatch.Core/Film/FilmLookLibrary.cs`
- Create: `tools/film-luts/FilmLuts.csproj`、`tools/film-luts/Program.cs`、`tools/film-luts/README.md`
- Create（工具產生）: `src/FilmBatch.Core/Film/Looks/*.fblut`（36 個）
- Modify: `src/FilmBatch.Core/FilmBatch.Core.csproj`
- Test: `tests/FilmBatch.Core.Tests/FilmLookTests.cs`

**Interfaces:**
- Consumes: `FilmLut`, `FilmLutFile`（Task 1）
- Produces:
  - `public sealed record FilmLook(string Key, string Name, string Group, string Source);`
  - `public static class FilmLookLibrary { const string DefaultKey = "kodak-portra-400"; IReadOnlyList<string> GroupsInOrder; IReadOnlyList<FilmLook> All; bool TryGet(string key, out FilmLook look); FilmLut? TryLut(string key); }`

> ⚠️ 執行 Step 6 需要從 rawtherapee.com 下載約 43 MB（只抓 36 個 PNG，不抓整包 402 MB）。**執行前先向使用者確認可以下載。**

- [ ] **Step 1: 建立 `looks.json`**（順序即 UI 順序；`source` 已逐一核對存在於 2015-09-20 版 zip）

```json
{
  "collection": "RawTherapee Film Simulation Collection 2015-09-20",
  "size": 33,
  "looks": [
    { "key": "kodak-portra-400", "name": "Kodak Portra 400", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Portra 400 2.png" },
    { "key": "kodak-portra-160", "name": "Kodak Portra 160", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Portra 160 2.png" },
    { "key": "kodak-portra-400-vc", "name": "Kodak Portra 400 VC", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Portra 400 VC 2.png" },
    { "key": "kodak-portra-800", "name": "Kodak Portra 800", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Portra 800 2.png" },
    { "key": "kodak-ektar-100", "name": "Kodak Ektar 100", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Ektar 100.png" },
    { "key": "kodak-kodachrome-64", "name": "Kodak Kodachrome 64", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Kodachrome 64.png" },
    { "key": "kodak-ektachrome-100vs", "name": "Kodak Ektachrome 100 VS", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Ektachrome 100 VS.png" },
    { "key": "kodak-elite-chrome-200", "name": "Kodak Elite Chrome 200", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Elite Chrome 200.png" },
    { "key": "kodak-elite-color-400", "name": "Kodak Elite Color 400", "group": "kodak", "source": "HaldCLUT/Color/Kodak/Kodak Elite Color 400.png" },
    { "key": "fuji-velvia-50", "name": "Fuji Velvia 50", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Velvia 50.png" },
    { "key": "fuji-provia-100f", "name": "Fuji Provia 100F", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Provia 100F.png" },
    { "key": "fuji-astia-100f", "name": "Fuji Astia 100F", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Astia 100F.png" },
    { "key": "fuji-superia-400", "name": "Fuji Superia 400", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Superia 400 2.png" },
    { "key": "fuji-pro-400h", "name": "Fuji Pro 400H", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji 400H 2.png" },
    { "key": "fuji-pro-160c", "name": "Fuji Pro 160C", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji 160C 2.png" },
    { "key": "fuji-superia-reala-100", "name": "Fuji Superia Reala 100", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Superia Reala 100.png" },
    { "key": "fuji-superia-xtra-800", "name": "Fuji Superia X-Tra 800", "group": "fuji", "source": "HaldCLUT/Color/Fuji/Fuji Superia X-Tra 800.png" },
    { "key": "agfa-vista-200", "name": "Agfa Vista 200", "group": "agfa-lomo", "source": "HaldCLUT/Color/Agfa/Agfa Vista 200.png" },
    { "key": "agfa-ultra-100", "name": "Agfa Ultra Color 100", "group": "agfa-lomo", "source": "HaldCLUT/Color/Agfa/Agfa Ultra Color 100.png" },
    { "key": "lomo-xpro-200", "name": "Lomography X-Pro Slide 200", "group": "agfa-lomo", "source": "HaldCLUT/Color/Lomography/Lomography X-Pro Slide 200.png" },
    { "key": "lomo-redscale-100", "name": "Lomography Redscale 100", "group": "agfa-lomo", "source": "HaldCLUT/Color/Lomography/Lomography Redscale 100.png" },
    { "key": "polaroid-690", "name": "Polaroid 690", "group": "polaroid", "source": "HaldCLUT/Color/Polaroid/Polaroid 690 3.png" },
    { "key": "polaroid-px70", "name": "Polaroid PX-70", "group": "polaroid", "source": "HaldCLUT/Color/Polaroid/Polaroid PX-70 3.png" },
    { "key": "polaroid-polachrome", "name": "Polaroid Polachrome", "group": "polaroid", "source": "HaldCLUT/Color/Polaroid/Polaroid Polachrome.png" },
    { "key": "kodak-tri-x-400", "name": "Kodak Tri-X 400", "group": "bw", "source": "HaldCLUT/Black-and-White/Kodak/Kodak TRI-X 400 2.png" },
    { "key": "kodak-tmax-100", "name": "Kodak T-Max 100", "group": "bw", "source": "HaldCLUT/Black-and-White/Kodak/Kodak T-Max 100.png" },
    { "key": "ilford-hp5-400", "name": "Ilford HP5 Plus 400", "group": "bw", "source": "HaldCLUT/Black-and-White/Ilford/Ilford HP5 Plus 400.png" },
    { "key": "ilford-delta-100", "name": "Ilford Delta 100", "group": "bw", "source": "HaldCLUT/Black-and-White/Ilford/Ilford Delta 100.png" },
    { "key": "fuji-acros-100", "name": "Fuji Neopan Acros 100", "group": "bw", "source": "HaldCLUT/Black-and-White/Fuji/Fuji Neopan Acros 100.png" },
    { "key": "agfa-apx-100", "name": "Agfa APX 100", "group": "bw", "source": "HaldCLUT/Black-and-White/Agfa/Agfa APX 100.png" },
    { "key": "creative-teal-orange", "name": "Teal & Orange", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/TealOrange.png" },
    { "key": "creative-bleach-bypass", "name": "Bleach Bypass", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/BleachBypass1.png" },
    { "key": "creative-fall-colors", "name": "Fall Colors", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/FallColors.png" },
    { "key": "creative-soft-warming", "name": "Soft Warming", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/SoftWarming.png" },
    { "key": "creative-crisp-winter", "name": "Crisp Winter", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/CrispWinter.png" },
    { "key": "creative-moonlight", "name": "Moonlight", "group": "creative", "source": "HaldCLUT/Color/CreativePack-1/Moonlight.png" }
  ]
}
```

- [ ] **Step 2: 建立 `Looks/LICENSE.txt`**

```text
本資料夾的 *.fblut 衍生自 RawTherapee Film Simulation Collection（2015-09-20 版，
http://rawtherapee.com/shared/HaldCLUT.zip），授權為 Creative Commons Attribution-ShareAlike 4.0
International（CC BY-SA 4.0，https://creativecommons.org/licenses/by-sa/4.0/）。

原作者：Pat David、Pavlov Dmitry、Michael Ezra。

修改：由 level 12 Hald CLUT（144³，8-bit sRGB PNG）以四面體內插重新取樣成 33³，
存成 16-bit 的 FilmBatch .fblut 格式（tools/film-luts）。本資料夾的 LUT 檔依 CC BY-SA 4.0 散布。

商標聲明：檔名中的底片名稱僅用於說明 LUT 意圖模擬的底片，FilmBatch 與上述商標所有者無任何關係，亦未獲其背書。
```

- [ ] **Step 3: 在 `FilmBatch.Core.csproj` 嵌入資源**（舊的 `recipelab.json` / `creative_styles.json` 兩行先保留，Task 6 再刪）

```xml
    <EmbeddedResource Include="Film\Looks\looks.json" LogicalName="FilmBatch.Looks/looks.json" />
    <EmbeddedResource Include="Film\Looks\*.fblut" LogicalName="FilmBatch.Looks/%(Filename)%(Extension)" />
```

- [ ] **Step 4: 寫失敗的測試 `FilmLookTests.cs`**

```csharp
using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public class FilmLookTests
{
    [Fact]
    public void Library_Has36UniqueLooks_InKnownGroups_DefaultFirst()
    {
        var all = FilmLookLibrary.All;
        Assert.Equal(36, all.Count);
        Assert.Equal(all.Count, all.Select(l => l.Key).Distinct().Count());
        Assert.All(all, l => Assert.Contains(l.Group, FilmLookLibrary.GroupsInOrder));
        Assert.Equal(FilmLookLibrary.DefaultKey, all[0].Key);
    }

    [Fact]
    public void EveryLook_HasEmbedded33Lut()
    {
        foreach (var look in FilmLookLibrary.All)
        {
            var lut = FilmLookLibrary.TryLut(look.Key);
            Assert.NotNull(lut);
            Assert.Equal(33, lut.Size);
        }
    }

    [Fact]
    public void TryLut_UnknownKey_ReturnsNull_AndCachesKnownKey()
    {
        Assert.Null(FilmLookLibrary.TryLut("no-such-film"));
        Assert.Same(FilmLookLibrary.TryLut("kodak-ektar-100"), FilmLookLibrary.TryLut("kodak-ektar-100"));
    }

    [Fact]
    public void BlackAndWhiteLooks_ProduceNeutralOutput()
    {
        var lut = FilmLookLibrary.TryLut("ilford-hp5-400")!;
        var got = lut.Sample(new Vector3(.8f, .2f, .3f));
        Assert.Equal(got.X, got.Y, 2);
        Assert.Equal(got.Y, got.Z, 2);
    }

    [Fact]
    public void ColorLooks_AreNotIdentity()
    {
        var lut = FilmLookLibrary.TryLut("fuji-velvia-50")!;
        var input = new Vector3(.25f, .5f, .3f);
        Assert.True(Vector3.Distance(input, lut.Sample(input)) > .01f);
    }
}
```

- [ ] **Step 5: 實作 `FilmLookLibrary.cs`**

```csharp
using System.Collections.Concurrent;
using System.Text.Json;

namespace FilmBatch.Core.Film;

/// <summary>一卷內附底片：key 存進設定（film.look），Source 是 HaldCLUT.zip 內的來源路徑（工具與文件用）。</summary>
public sealed record FilmLook(string Key, string Name, string Group, string Source);

/// <summary>內附底片清單與 LUT（嵌入資源 FilmBatch.Looks/*；LUT 第一次使用時載入並快取）。</summary>
public static class FilmLookLibrary
{
    public const string DefaultKey = "kodak-portra-400";
    public static IReadOnlyList<string> GroupsInOrder { get; } = ["kodak", "fuji", "agfa-lomo", "polaroid", "bw", "creative"];

    private static readonly ConcurrentDictionary<string, FilmLut?> Luts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FilmLook> ByKey;

    public static IReadOnlyList<FilmLook> All { get; }

    static FilmLookLibrary()
    {
        using var stream = Open("looks.json") ?? throw new InvalidOperationException("缺少內嵌的 looks.json。");
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("looks.json 格式錯誤。");
        All = manifest.Looks;
        ByKey = All.ToDictionary(l => l.Key, StringComparer.Ordinal);
    }

    public static bool TryGet(string key, out FilmLook look) => ByKey.TryGetValue(key, out look!);

    /// <summary>未知 key 回傳 null（算圖端視為「不套用底片」）。</summary>
    public static FilmLut? TryLut(string key) => !ByKey.ContainsKey(key) ? null : Luts.GetOrAdd(key, static k =>
    {
        using var stream = Open(k + ".fblut");
        return stream is null ? null : FilmLutFile.Read(stream);
    });

    private static Stream? Open(string name) =>
        typeof(FilmLookLibrary).Assembly.GetManifestResourceStream("FilmBatch.Looks/" + name);

    private sealed record Manifest(string Collection, int Size, List<FilmLook> Looks);
}
```

- [ ] **Step 6: 建立轉檔工具 `tools/film-luts`**（**先取得使用者同意下載約 43 MB**）

`tools/film-luts/FilmLuts.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>film-luts</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\FilmBatch.Core\FilmBatch.Core.csproj" />
    <PackageReference Include="Magick.NET-Q16-HDRI-AnyCPU" />
  </ItemGroup>
</Project>
```

`tools/film-luts/Program.cs`：

```csharp
// 從 RawTherapee HaldCLUT.zip 以 HTTP Range 只抓 looks.json 列出的 PNG，轉成 33³ .fblut。
// dotnet run --project tools/film-luts -- <looks.json> <輸出資料夾>
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using FilmBatch.Core.Film;
using ImageMagick;

const string Url = "http://rawtherapee.com/shared/HaldCLUT.zip";
const long ExpectedLength = 421_602_289;
if (args.Length != 2) { Console.Error.WriteLine("用法：film-luts <looks.json> <輸出資料夾>"); return 2; }
var manifest = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var size = manifest.GetProperty("size").GetInt32();
Directory.CreateDirectory(args[1]);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
async Task<byte[]> Range(long from, long to)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, Url);
    request.Headers.Range = new RangeHeaderValue(from, to);
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentRange?.Length is { } total && total != ExpectedLength)
        throw new InvalidDataException($"HaldCLUT.zip 大小 {total} 與預期 {ExpectedLength} 不同，來源可能已更新。");
    return await response.Content.ReadAsByteArrayAsync();
}

// 讀 zip 中央目錄（檔尾 64 KB 內的 End of Central Directory）
var tail = await Range(ExpectedLength - 65536, ExpectedLength - 1);
var eocd = tail.AsSpan().LastIndexOf("PK\x05\x06"u8);
var count = BitConverter.ToUInt16(tail, eocd + 10);
var cdSize = BitConverter.ToUInt32(tail, eocd + 12);
var cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
var cd = await Range(cdOffset, cdOffset + cdSize - 1);
var entries = new Dictionary<string, (int Method, long Compressed, long Offset)>(StringComparer.Ordinal);
for (int p = 0, i = 0; i < count; i++)
{
    var method = BitConverter.ToUInt16(cd, p + 10);
    var compressed = BitConverter.ToUInt32(cd, p + 20);
    int nameLength = BitConverter.ToUInt16(cd, p + 28), extra = BitConverter.ToUInt16(cd, p + 30), comment = BitConverter.ToUInt16(cd, p + 32);
    var offset = BitConverter.ToUInt32(cd, p + 42);
    entries[System.Text.Encoding.UTF8.GetString(cd, p + 46, nameLength)] = (method, compressed, offset);
    p += 46 + nameLength + extra + comment;
}

foreach (var look in manifest.GetProperty("looks").EnumerateArray())
{
    var key = look.GetProperty("key").GetString()!;
    var source = look.GetProperty("source").GetString()!;
    if (!entries.TryGetValue(source, out var entry)) throw new InvalidDataException($"zip 內找不到 {source}");
    var header = await Range(entry.Offset, entry.Offset + 29);
    var dataStart = entry.Offset + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
    var raw = await Range(dataStart, dataStart + entry.Compressed - 1);
    byte[] png = entry.Method switch
    {
        0 => raw,
        8 => Inflate(raw),
        _ => throw new InvalidDataException($"不支援的壓縮方式 {entry.Method}：{source}"),
    };
    using var image = new MagickImage(png);
    if (image.Width != 1728 || image.Height != 1728) throw new InvalidDataException($"{source} 不是 level 12 Hald CLUT。");
    var pixels = image.GetPixelsUnsafe().ToShortArray(PixelMapping.RGB)!;
    var rgb = new float[pixels.Length];
    for (var i = 0; i < rgb.Length; i++) rgb[i] = pixels[i] / 65535f;
    var lut = FilmLut.FromHald(rgb, 12).Resample(size);
    await using var output = File.Create(Path.Combine(args[1], key + ".fblut"));
    FilmLutFile.Write(output, lut);
    Console.WriteLine($"{key} ← {source}");
}
return 0;

static byte[] Inflate(byte[] data)
{
    using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
}
```

> `ToShortArray` 回傳 Q16 數值（0–65535）；8-bit PNG 會被擴展到 16-bit，除以 65535 即得 0–1。`FromHald` 的像素順序與 `ImageMagick` 列優先排列一致（k = x + y*1728 = r + g*144 + b*144²）。

`tools/film-luts/README.md`：簡述用途、指令、來源 URL 與檔案大小、授權（CC BY-SA 4.0）、更新底片清單時要同步 `docs/film-looks.md`。

- [ ] **Step 7: 產生 LUT**

Run: `dotnet run --project tools/film-luts -- src/FilmBatch.Core/Film/Looks/looks.json src/FilmBatch.Core/Film/Looks`
Expected: 印出 36 行 `key ← source`；資料夾內 36 個 `.fblut`，每個 215,634 bytes（8 + 4 + 33³×3×2）。

- [ ] **Step 8: 執行測試確認通過**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~FilmLookTests|FullyQualifiedName~FilmLutTests"`
Expected: 全部 PASS。

- [ ] **Step 9: Commit**

```bash
git add src/FilmBatch.Core/Film/Looks src/FilmBatch.Core/Film/FilmLookLibrary.cs src/FilmBatch.Core/FilmBatch.Core.csproj tools/film-luts tests/FilmBatch.Core.Tests/FilmLookTests.cs
git commit -m "Bundle 36 RawTherapee film LUTs (CC BY-SA 4.0) with conversion tool"
```

---

### Task 3: 色相帶定義、新 film schema、舊參數遷移與範例預設

**Files:**
- Create: `src/FilmBatch.Core/Film/HueBands.cs`
- Create: `src/FilmBatch.Core/Params/RetiredParams.cs`
- Modify: `src/FilmBatch.Core/Params/ModuleCatalog.cs`（film 模組整段替換）
- Modify: `src/FilmBatch.Core/Params/SettingsJsonConverter.cs:12-27, 48-51`
- Modify: `src/FilmBatch.Core/Presets/PresetFile.cs:33-38`
- Modify: `src/FilmBatch.Core/Presets/SamplePresets.cs`
- Test: `tests/FilmBatch.Core.Tests/RetiredParamsTests.cs`、`FilmLookTests.cs`（新增色相預設測試）

**Interfaces:**
- Consumes: `FilmLookLibrary.All`, `FilmLookLibrary.DefaultKey`（Task 2）
- Produces:
  - `public static class HueBands { IReadOnlyList<string> Keys; float[] CentersDegrees; IReadOnlyList<string> Labels; string ParamKey(int band, char channel); IReadOnlyList<string> FlatKeys; IEnumerable<ParamDef> Params(); }`
  - `public sealed record HuePreset(string Key, string Name, IReadOnlyDictionary<string, double> Values) { Settings ToSettings(); }`
  - `public static class HuePresets { IReadOnlyList<HuePreset> All; HuePreset? Find(Settings resolved); }`
  - `public static class RetiredParams { bool IsRetired(string flatKey); }`

> 此 Task 完成後舊的 `FilmParameters` 等仍讀舊欄位（預設值），程式可編譯；新算圖在 Task 4 接上。

- [ ] **Step 1: 寫失敗的測試 `RetiredParamsTests.cs`**

```csharp
using System.Text.Json;
using FilmBatch.Core.Params;
using FilmBatch.Core.Presets;

namespace FilmBatch.Core.Tests;

public class RetiredParamsTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new SettingsJsonConverter() } };

    [Fact]
    public void Json_DropsRetiredFilmKeys_KeepsEnabledAndStrength()
    {
        const string json = """{"film.enabled":true,"film.recipe":"kodak-portra-400","film.style":"vivid","film.strength":80,"film.dro":"auto"}""";
        var settings = JsonSerializer.Deserialize<Settings>(json, Options)!;
        Assert.True(settings.Bool("film.enabled"));
        Assert.Equal(80, settings["film.strength"].Number);
        Assert.False(settings.ContainsKey("film.recipe"));
        Assert.False(settings.ContainsKey("film.style"));
        Assert.False(settings.ContainsKey("film.dro"));
    }

    [Fact]
    public void Json_StillRejectsUnknownNonRetiredKey()
    {
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<Settings>("""{"film.bogus":1}""", Options));
    }

    [Fact]
    public void PresetYaml_DropsRetiredFilmKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-retired-{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, """
            id: 3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b
            name: "Old"
            pipeline_version: 1
            modules:
              film:
                enabled: true
                style: "vivid"
                saturation: 2
            """);
        try
        {
            var preset = PresetFile.Read(path);
            Assert.True(preset.Settings.Bool("film.enabled"));
            Assert.False(preset.Settings.ContainsKey("film.style"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Catalog_FilmLookChoices_MatchLibrary()
    {
        var look = ModuleCatalog.Param("film.look")!;
        Assert.Equal(FilmBatch.Core.Film.FilmLookLibrary.All.Select(l => l.Key), look.Choices.Select(c => c.Key));
        Assert.Equal(FilmBatch.Core.Film.FilmLookLibrary.DefaultKey, look.Default.AsText);
        Assert.Equal(24, FilmBatch.Core.Film.HueBands.FlatKeys.Count);
        Assert.All(FilmBatch.Core.Film.HueBands.FlatKeys, key => Assert.NotNull(ModuleCatalog.Param(key)));
    }

    [Fact]
    public void SamplePresets_AreValidAndUseNewFilmKeys()
    {
        foreach (var preset in SamplePresets.Create())
            foreach (var (key, value) in preset.Settings)
            {
                SettingsJsonConverter.Validate(key, value);
                Assert.False(RetiredParams.IsRetired(key), key);
            }
    }
}
```

> 已核對：讀檔方法為 `PresetFile.Read(string path)`，YAML root key 為 `id`、`name`、`pipeline_version`、`modules`；`ParamDef.Default` 型別為 `ParamValue`。

- [ ] **Step 2: 加入 `FilmLookTests` 色相預設測試**

```csharp
    [Fact]
    public void HuePresets_FirstIsNone_AllKeysAreKnownBands()
    {
        Assert.Equal("none", HuePresets.All[0].Key);
        Assert.All(HuePresets.All[0].ToSettings(), kv => Assert.Equal(0, kv.Value.Number));
        foreach (var preset in HuePresets.All)
        {
            var settings = preset.ToSettings();
            Assert.Equal(24, settings.Count());
            Assert.All(preset.Values.Keys, key => Assert.Contains(key, HueBands.FlatKeys));
        }
    }

    [Fact]
    public void HuePresets_Find_MatchesExactValues_ElseNull()
    {
        var warm = HuePresets.All.First(p => p.Key == "warm-skin");
        var resolved = ModuleCatalog.Defaults.WithValues(warm.ToSettings());
        Assert.Equal("warm-skin", HuePresets.Find(resolved)?.Key);
        Assert.Null(HuePresets.Find(resolved.With("film.hue_blue_s", ParamValue.Of(37.0))));
    }
```

> 在檔頭補 `using FilmBatch.Core.Params;`。`Settings.WithValues` 若不存在，用 `warm.ToSettings().Aggregate(ModuleCatalog.Defaults, (s, kv) => s.With(kv.Key, kv.Value))`。

- [ ] **Step 3: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~RetiredParamsTests|FullyQualifiedName~FilmLookTests"`
Expected: 編譯失敗（`HueBands`、`HuePresets`、`RetiredParams` 不存在）。

- [ ] **Step 4: 實作 `RetiredParams.cs`**

```csharp
using System.Collections.Frozen;

namespace FilmBatch.Core.Params;

/// <summary>
/// 已移除的參數（Recipe Lab 底片配方，2026-10 由 底片顏色模擬取代）。
/// 讀取舊專案／預設時靜默丟棄，讓舊檔仍能開啟；寫入時自然不會再出現。
/// </summary>
public static class RetiredParams
{
    private static readonly FrozenSet<string> Keys = new[]
    {
        "film.recipe", "film.style", "film.saturation", "film.contrast", "film.sharpness",
        "film.white_balance", "film.amber_blue", "film.green_magenta", "film.effect",
        "film.effect_option", "film.exposure", "film.dro", "film.wb_reference",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsRetired(string flatKey) => Keys.Contains(flatKey);
}
```

- [ ] **Step 5: 在兩個讀取入口丟棄舊參數**

`SettingsJsonConverter.Read`，在解析出 `value` 之後、`values.TryAdd` 之前：

```csharp
            if (RetiredParams.IsRetired(key)) continue;
```

並把 `Validate` 中的例外改為只剩材質：

```csharp
        // Asset choices can be supplied by the user's library, not only the static catalog.
        if (definition.Kind == ParamKind.Choice && key is not "light_leak.texture"
```

`PresetFile.cs`，在 `var flatKey = module + "." + key;` 之後：

```csharp
                    if (RetiredParams.IsRetired(flatKey)) continue;
```

- [ ] **Step 6: 實作 `HueBands.cs`**

```csharp
using FilmBatch.Core.Params;
using static FilmBatch.Core.Params.ParamDef;

namespace FilmBatch.Core.Film;

/// <summary>
/// 分色相修正：OKLCh 色相環上的八個色相帶，各有色相／飽和／明度三個 −100–100 的參數。
/// 中心角度與 WGSL film/look.wgsl 的 CENTERS 必須相同。
/// </summary>
public static class HueBands
{
    public static IReadOnlyList<string> Keys { get; } = ["red", "orange", "yellow", "green", "aqua", "blue", "purple", "magenta"];
    public static IReadOnlyList<string> Labels { get; } = ["紅", "橙", "黃", "綠", "青", "藍", "紫", "洋紅"];
    public static readonly float[] CentersDegrees = [25, 55, 105, 140, 195, 255, 295, 335];
    public const string Channels = "hsl";

    public static string ParamKey(int band, char channel) => $"hue_{Keys[band]}_{channel}";

    /// <summary>24 個扁平 key（film.hue_red_h …），順序：色相帶 × (h, s, l)。</summary>
    public static IReadOnlyList<string> FlatKeys { get; } =
        [.. Enumerable.Range(0, 8).SelectMany(b => Channels.Select(c => "film." + ParamKey(b, c)))];

    public static IEnumerable<ParamDef> Params() =>
        Enumerable.Range(0, 8).SelectMany(b => new[]
        {
            Float(ParamKey(b, 'h'), Labels[b] + "色相", 0, -100, 100),
            Float(ParamKey(b, 's'), Labels[b] + "飽和", 0, -100, 100),
            Float(ParamKey(b, 'l'), Labels[b] + "明度", 0, -100, 100),
        });
}

/// <summary>內建色相修正（一鍵套用）；Values 只列非零欄位，其餘為 0。</summary>
public sealed record HuePreset(string Key, string Name, IReadOnlyDictionary<string, double> Values)
{
    public Settings ToSettings() => new(HueBands.FlatKeys.Select(key =>
        new KeyValuePair<string, ParamValue>(key, ParamValue.Of(Values.TryGetValue(key, out var v) ? v : 0.0))));
}

public static class HuePresets
{
    private static HuePreset P(string key, string name, params (string Key, double Value)[] values) =>
        new(key, name, values.ToDictionary(v => "film.hue_" + v.Key, v => v.Value, StringComparer.Ordinal));

    public static IReadOnlyList<HuePreset> All { get; } =
    [
        P("none", "無"),
        P("warm-skin", "暖膚色", ("red_h", 10), ("orange_s", 10), ("orange_l", 8), ("yellow_s", -10)),
        P("clear-sky", "清澈藍天", ("blue_s", 20), ("blue_l", -10), ("aqua_h", 10), ("aqua_s", 10)),
        P("lush-green", "青翠綠意", ("green_h", 15), ("green_s", 15), ("yellow_h", 15)),
        P("golden", "金黃秋色", ("orange_s", 20), ("yellow_h", -10), ("yellow_s", 20), ("green_h", -20), ("green_s", -15)),
        P("teal-orange", "冷暖對比", ("orange_s", 20), ("aqua_h", 5), ("aqua_s", 20), ("blue_h", -15), ("green_s", -20)),
        P("muted", "低彩度", ("red_s", -25), ("orange_s", -25), ("yellow_s", -25), ("green_s", -25),
            ("aqua_s", -25), ("blue_s", -25), ("purple_s", -25), ("magenta_s", -25)),
    ];

    /// <summary>目前 24 個值完全符合哪個內建預設；都不符合（使用者自訂）時回傳 null。</summary>
    public static HuePreset? Find(Settings resolved) => All.FirstOrDefault(preset =>
        preset.ToSettings().All(kv => resolved.TryGetValue(kv.Key, out var v) && v == kv.Value));
}
```

> `Settings` 若沒有 `IEnumerable<KeyValuePair<string, ParamValue>>` 建構子以外的工廠，沿用現有 `new Settings(IEnumerable<KeyValuePair<…>>)`。

- [ ] **Step 7: 替換 `ModuleCatalog.cs` 的 film 模組**（整段 `new("film", "底片色彩", …)` 換成）

```csharp
        // 底片風格：底片顏色模擬（RawTherapee Film Simulation Hald CLUT）＋ 八色相帶修正（docs/film-looks.md）。
        new("film", "底片風格", ModuleGroup.Film, PasteCategory.Color,
        [
            Bool(ModuleSchema.EnabledKey, "啟用"),
            Choice("look", "底片", [.. Film.FilmLookLibrary.All.Select(l => O(l.Key, l.Name))]),
            Float("strength", "效果比例", 100, 0, 100, 1, "%"),
            Float("warmth", "暖色", 0, -100, 100),
            Float("fade", "褪色", 0, 0, 100),
            .. Film.HueBands.Params(),
        ]),
```

> `[.., .. Film.HueBands.Params()]` 是 collection expression 的 spread，若 `ModuleSchema` 建構子參數型別不支援，改成 `new ParamDef[] { … }.Concat(Film.HueBands.Params()).ToArray()`。

- [ ] **Step 8: 改寫 `SamplePresets.cs`**

刪除 `RecipeFields` 與 `WithRecipe`，改成：

```csharp
    private static Settings WithLook(IEnumerable<(string Key, ParamValue Value)> extra, string lookKey, double strength = 100) =>
        S([("film.enabled", true), ("film.look", lookKey), ("film.strength", strength), .. extra]);
```

每個原本 `WithRecipe([...], "kodak-portra-400", strength: 85.0)` 改為 `WithLook([...], "kodak-portra-400", 85.0)`，並從 `extra` 移除重複的 `("film.enabled", true)`。原配方 key 對應：`kodak-portra-400 → kodak-portra-400`、`kodak-ektar-100 → kodak-ektar-100`；其他 Recipe Lab key 依名稱挑 `looks.json` 中最接近的底片（例如 Fuji 類 → `fuji-pro-400h`、黑白類 → `kodak-tri-x-400`），並在 commit 訊息列出對應。

- [ ] **Step 9: 執行測試確認通過**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~RetiredParamsTests|FullyQualifiedName~FilmLookTests"`
Expected: PASS。再跑 `dotnet build`：舊 `ModuleParams` 讀的是已不存在於 schema 的 key，但 `N/T` 只是取不到就回預設，仍可編譯。若 `FilmRecipeTests` 等舊測試失敗屬預期，Task 6 會刪除。

- [ ] **Step 10: Commit**

```bash
git add src/FilmBatch.Core tests/FilmBatch.Core.Tests/RetiredParamsTests.cs tests/FilmBatch.Core.Tests/FilmLookTests.cs
git commit -m "Replace film schema with film color look, warmth, fade and hue bands; drop retired keys on load"
```

---

### Task 4: CPU 算圖（FilmLookProcessing）

**Files:**
- Create: `src/FilmBatch.Engine/Rendering/Oklab.cs`
- Create: `src/FilmBatch.Engine/Rendering/FilmLookUniforms.cs`
- Create: `src/FilmBatch.Engine/Rendering/FilmLookProcessing.cs`
- Modify: `src/FilmBatch.Engine/Rendering/ModuleParams.cs:50-54, 76, 190-205, 250, 304`
- Modify: `src/FilmBatch.Engine/Rendering/CpuPipeline.cs:64-65`
- Modify: `tests/FilmBatch.Core.Tests/EffectPipelineTests.cs`（`FilmOnly` 與 film 相關測試）

**Interfaces:**
- Consumes: `FilmLut`, `FilmLookLibrary.TryLut`, `HueBands`（Task 1–3）
- Produces:
  - `internal static class Oklab { Vector3 FromLinear(Vector3); Vector3 ToLinear(Vector3); }`
  - `internal struct FilmLookUniforms { Vector4 Mix, Flags, Band0..Band7; Vector4 Band(int); static FilmLookUniforms From(ModuleParams); }`
  - `internal static class FilmLookProcessing { void Apply(float[] pixels, int count, FilmLookUniforms u, FilmLut lut); Vector3 Pixel(Vector3 linear, in FilmLookUniforms u, FilmLut lut); float BandWeight(float hueDegrees, int band); }`
  - `ModuleParams`: `bool FilmOn; string FilmLookKey; float FilmStrength, FilmWarmth, FilmFade; float[] FilmHue (24); FilmLut? FilmLut; internal FilmLookUniforms FilmLook;`

- [ ] **Step 1: 改寫 `EffectPipelineTests` 的 film 測試**

把 `FilmOnly` 換成：

```csharp
    private static Settings FilmOnly(double strength = 100, string look = "kodak-ektar-100",
        double warmth = 0, double fade = 0, params (string Key, double Value)[] hue)
    {
        var s = ModuleCatalog.Defaults
            .With("film.enabled", true)
            .With("film.look", look)
            .With("film.strength", strength)
            .With("film.warmth", warmth)
            .With("film.fade", fade);
        foreach (var (key, value) in hue) s = s.With("film." + key, value);
        return s;
    }
```

刪除 `FilmExposure_IsEvGain_WithOtherStagesDisabled`；把 `FilmStrength_ZeroPreservesInput_HalfMixesFullStage` 的呼叫改成 `FilmOnly()`、`FilmOnly(strength: 50)`、`FilmOnly(strength: 0)`（線性域中點語義不變）；其他呼叫 `FilmOnly(1.0)` / `FilmOnly(0.5)` 的地方改成 `FilmOnly()`。新增：

```csharp
    [Fact]
    public void FilmLook_HueBand_ShiftsRedButNotBlueOrGray()
    {
        var ct = TestContext.Current.CancellationToken;
        var cpu = new CpuFakeRenderer();
        // 三個像素：飽和紅、飽和藍、中性灰（16-bit sRGB）
        var src = new SourceImage(3, 1, [
            52000, 8000, 8000, 65535,
            8000, 12000, 52000, 65535,
            30000, 30000, 30000, 65535]);
        var plain = cpu.Render16(src, FilmOnly(), ct).Rgba16;
        var shifted = cpu.Render16(src, FilmOnly(hue: [("hue_red_h", 100), ("hue_red_s", 50)]), ct).Rgba16;
        Assert.NotEqual(plain[..3], shifted[..3]);
        Assert.Equal(plain[4..7], shifted[4..7]);
        Assert.Equal(plain[8..11], shifted[8..11]);
    }

    [Theory]
    [InlineData(10f)] [InlineData(25f)] [InlineData(80f)] [InlineData(170f)] [InlineData(330f)] [InlineData(359f)]
    public void HueBandWeights_ArePartitionOfUnity(float hue)
    {
        var sum = 0f;
        for (var band = 0; band < 8; band++) sum += FilmLookProcessing.BandWeight(hue, band);
        Assert.Equal(1f, sum, 5);
    }

    [Fact]
    public void FilmLook_WarmthRaisesRedLowersBlue_FadeLiftsBlack()
    {
        var ct = TestContext.Current.CancellationToken;
        var cpu = new CpuFakeRenderer();
        var src = new SourceImage(2, 1, [30000, 30000, 30000, 65535, 0, 0, 0, 65535]);
        var plain = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100"), ct).Rgba16;
        var warm = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100", warmth: 100), ct).Rgba16;
        Assert.True(warm[0] > plain[0] && warm[2] < plain[2]);
        var faded = cpu.Render16(src, FilmOnly(look: "kodak-tmax-100", fade: 100), ct).Rgba16;
        Assert.True(faded[4] > plain[4] + 3000);
    }
```

> `CpuFakeRenderer` 對 `SourceImage` 的建構方式依現有測試（如 `Render16_IsTrue16Bit_NotExpandedBgra8`）調整；`FilmBatch.Engine` 需要對測試專案 `InternalsVisibleTo`（已有，`FilmLookProcessing` 為 internal）。

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~EffectPipelineTests"`
Expected: 編譯失敗（`FilmLookProcessing` 不存在）。

- [ ] **Step 3: 實作 `Oklab.cs`**（數值取自 `FilmProcessing.SaturateOklab`）

```csharp
using System.Numerics;

namespace FilmBatch.Engine.Rendering;

/// <summary>線性 sRGB ↔ OKLab（與 film/look.wgsl 的 to_oklab / from_oklab 同係數）。</summary>
internal static class Oklab
{
    public static Vector3 FromLinear(Vector3 rgb)
    {
        var l = MathF.Cbrt(.41222147f * rgb.X + .53633254f * rgb.Y + .05144599f * rgb.Z);
        var m = MathF.Cbrt(.21190350f * rgb.X + .68069955f * rgb.Y + .10739696f * rgb.Z);
        var s = MathF.Cbrt(.08830246f * rgb.X + .28171884f * rgb.Y + .62997870f * rgb.Z);
        return new(.21045426f * l + .79361779f * m - .00407205f * s,
            1.97799850f * l - 2.42859221f * m + .45059371f * s,
            .02590404f * l + .78277177f * m - .80867577f * s);
    }

    public static Vector3 ToLinear(Vector3 lab)
    {
        var l = lab.X + .39633778f * lab.Y + .21580376f * lab.Z;
        var m = lab.X - .10556135f * lab.Y - .06385417f * lab.Z;
        var s = lab.X - .08948418f * lab.Y - 1.29148555f * lab.Z;
        l *= l * l; m *= m * m; s *= s * s;
        return new(4.07674166f * l - 3.30771159f * m + .23096993f * s,
            -1.26843800f * l + 2.60975740f * m - .34131940f * s,
            -.00419609f * l - .70341861f * m + 1.70761470f * s);
    }
}
```

- [ ] **Step 4: 實作 `FilmLookUniforms.cs`**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

namespace FilmBatch.Engine.Rendering;

/// <summary>與 film/look.wgsl 的 LookParams 相同排列（10 × vec4f = 160 bytes）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FilmLookUniforms
{
    public Vector4 Mix;   // 效果比例 0–1、暖色 −1–1、褪色 0–1、LUT 邊長
    public Vector4 Flags; // x：分色相是否有非零值（0/1）
    public Vector4 Band0, Band1, Band2, Band3, Band4, Band5, Band6, Band7; // 色相、飽和、明度（−1–1）、0

    public readonly Vector4 Band(int band) => band switch
    {
        0 => Band0, 1 => Band1, 2 => Band2, 3 => Band3, 4 => Band4, 5 => Band5, 6 => Band6, _ => Band7,
    };

    public static FilmLookUniforms From(ModuleParams p)
    {
        var bands = new Vector4[8];
        var active = false;
        for (var b = 0; b < 8; b++)
        {
            bands[b] = new(p.FilmHue[b * 3] / 100, p.FilmHue[b * 3 + 1] / 100, p.FilmHue[b * 3 + 2] / 100, 0);
            active |= bands[b] != Vector4.Zero;
        }
        return new FilmLookUniforms
        {
            Mix = new(p.FilmStrength / 100, p.FilmWarmth / 100, p.FilmFade / 100, p.FilmLut?.Size ?? 0),
            Flags = new(active ? 1 : 0, 0, 0, 0),
            Band0 = bands[0], Band1 = bands[1], Band2 = bands[2], Band3 = bands[3],
            Band4 = bands[4], Band5 = bands[5], Band6 = bands[6], Band7 = bands[7],
        };
    }
}
```

- [ ] **Step 5: 實作 `FilmLookProcessing.cs`**

```csharp
using System.Numerics;
using FilmBatch.Core.Film;

namespace FilmBatch.Engine.Rendering;

/// <summary>
/// 底片風格的 CPU 參考實作（與 film/look.wgsl 同演算法）：
/// 線性 → sRGB → LUT → 線性 → 八色相帶（OKLCh）→ 暖色 → 褪色（sRGB）→ 與原圖依效果比例混合（線性）。
/// </summary>
internal static class FilmLookProcessing
{
    public static void Apply(float[] pixels, int count, FilmLookUniforms u, FilmLut lut) =>
        Parallel.For(0, count, pixel =>
        {
            var i = pixel * 4;
            var result = Pixel(new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]), u, lut);
            pixels[i] = result.X; pixels[i + 1] = result.Y; pixels[i + 2] = result.Z;
        });

    public static Vector3 Pixel(Vector3 linear, in FilmLookUniforms u, FilmLut lut)
    {
        var display = Srgb(Vector3.Clamp(linear, Vector3.Zero, Vector3.One));
        var look = Linear(lut.Sample(display));
        if (u.Flags.X != 0) look = ApplyHueBands(look, u);
        var warmth = u.Mix.Y;
        if (warmth != 0) look *= new Vector3(1 + .12f * warmth, 1, 1 - .12f * warmth);
        var fade = u.Mix.Z;
        if (fade != 0) look = Linear(Srgb(Vector3.Max(look, Vector3.Zero)) * (1 - .2f * fade) + new Vector3(.1f * fade));
        return Vector3.Lerp(linear, look, u.Mix.X);
    }

    public static float BandWeight(float hueDegrees, int band)
    {
        var centers = HueBands.CentersDegrees;
        var center = centers[band];
        var delta = Wrap180(hueDegrees - center);
        if (delta >= 0) return MathF.Max(0, 1 - delta / Wrap360(centers[(band + 1) % 8] - center));
        return MathF.Max(0, 1 + delta / Wrap360(center - centers[(band + 7) % 8]));
    }

    private static Vector3 ApplyHueBands(Vector3 rgb, in FilmLookUniforms u)
    {
        var lab = Oklab.FromLinear(Vector3.Max(rgb, Vector3.Zero));
        var chroma = MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);
        if (chroma < 1e-5f) return rgb;
        var hue = Wrap360(MathF.Atan2(lab.Z, lab.Y) * (180 / MathF.PI));
        float dh = 0, ds = 0, dl = 0;
        for (var band = 0; band < 8; band++)
        {
            var w = BandWeight(hue, band);
            if (w == 0) continue;
            var values = u.Band(band);
            dh += w * values.X; ds += w * values.Y; dl += w * values.Z;
        }
        var gate = SmoothStep(.02f, .08f, chroma);
        var newHue = (hue + dh * 30 * gate) * (MathF.PI / 180);
        var newChroma = chroma * MathF.Max(0, 1 + ds);
        var lightness = lab.X + dl * .12f * gate;
        return Vector3.Max(Oklab.ToLinear(new Vector3(lightness, newChroma * MathF.Cos(newHue), newChroma * MathF.Sin(newHue))), Vector3.Zero);
    }

    private static float Wrap360(float x) => (x % 360 + 360) % 360;
    private static float Wrap180(float x) { x = Wrap360(x); return x > 180 ? x - 360 : x; }
    private static float SmoothStep(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
    private static Vector3 Srgb(Vector3 c) => new(CpuPipeline.LinearToSrgb(c.X), CpuPipeline.LinearToSrgb(c.Y), CpuPipeline.LinearToSrgb(c.Z));
    private static Vector3 Linear(Vector3 c) => new(CpuPipeline.SrgbToLinear(c.X), CpuPipeline.SrgbToLinear(c.Y), CpuPipeline.SrgbToLinear(c.Z));
}
```

> 若 `common/color.wgsl` 的 `linear_to_srgb` 與 `CpuPipeline.LinearToSrgb` 對 >1 的處理不同，這裡輸入已先 clamp，不影響一致性。

- [ ] **Step 6: 修改 `ModuleParams.cs`**

欄位（取代原 50–54 行與 76 行）：

```csharp
    // film（底片風格：LUT ＋ 色相帶；FilmLut 為 null 時不套用）
    public bool FilmOn;
    public string FilmLookKey = "";
    public float FilmStrength, FilmWarmth, FilmFade;
    public float[] FilmHue = new float[24];
    public FilmLut? FilmLut;
    internal FilmLookUniforms FilmLook;
```

初始化（取代 190–205 行）：

```csharp
            // film：底片風格（docs/film-looks.md）
            FilmOn = B(s, "film.enabled", false),
            FilmLookKey = T(s, "film.look", FilmLookLibrary.DefaultKey),
            FilmStrength = N(s, "film.strength", 100),
            FilmWarmth = N(s, "film.warmth", 0),
            FilmFade = N(s, "film.fade", 0),
            FilmHue = [.. HueBands.FlatKeys.Select(key => N(s, key, 0))],
```

`m.Film = FilmParameters.From(m, source.ShortSide);` 換成：

```csharp
        m.FilmLut = m.FilmOn ? FilmLookLibrary.TryLut(m.FilmLookKey) : null;
        m.FilmLook = FilmLookUniforms.From(m);
```

`HasSpatialPasses` 移除 `|| (FilmOn && FilmSharpness != 0)` 一行；刪除 `FilmStyleIndex`、`FilmEffectIndex`、`WhiteBalanceKelvin`（先 `git grep` 確認無其他呼叫者）。檔頭加 `using FilmBatch.Core.Film;`。

- [ ] **Step 7: 修改 `CpuPipeline.cs`**

```csharp
        // ③ 底片（線性）：底片風格 → 古典版材
        if (p.FilmOn && p.FilmLut is not null && p.FilmStrength > 0) FilmLookProcessing.Apply(cur, n, p.FilmLook, p.FilmLut);
```

- [ ] **Step 8: 執行測試確認通過**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~EffectPipelineTests&FullyQualifiedName!~Gpu_"`
Expected: PASS（GPU 測試 Task 5 處理）。

- [ ] **Step 9: Commit**

```bash
git add src/FilmBatch.Engine tests/FilmBatch.Core.Tests/EffectPipelineTests.cs
git commit -m "Render film looks on CPU: LUT, OKLCh hue bands, warmth and fade"
```

---

### Task 5: GPU 算圖（film/look.wgsl）

**Files:**
- Create: `src/FilmBatch.Gpu/Shaders/v1/film/look.wgsl`
- Modify: `src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs`（kernel 宣告、建構子、`Process` film 區段、LUT 紋理快取、`Dispose`）
- Modify: `tests/FilmBatch.Core.Tests/EffectPipelineTests.cs`（`Gpu_MatchesCpu_OnFilmAndGrain` 改用新設定，新增色相帶比對）

**Interfaces:**
- Consumes: `FilmLookUniforms`、`FilmLut.ToTextureRgba()`、`ModuleParams.FilmLut/FilmLook`（Task 1、4）

- [ ] **Step 1: 新增 GPU 對 CPU 測試**（沿用 `Gpu_MatchesCpu_OnFilmAndGrain` 的 GPU 建立與容差寫法）

```csharp
    [Theory]
    [InlineData("kodak-portra-400", 0, 0)]
    [InlineData("fuji-velvia-50", 40, 30)]
    [InlineData("creative-teal-orange", -60, 0)]
    public void Gpu_MatchesCpu_OnFilmLook(string look, double warmth, double fade)
    {
        var settings = FilmOnly(strength: 80, look: look, warmth: warmth, fade: fade,
            hue: [("hue_orange_s", 30), ("hue_blue_h", -50), ("hue_green_l", 40)]);
        // 以下照 Gpu_MatchesCpu_OnFilmAndGrain：同一張測試圖分別用 CPU 與 GPU Render16，
        // 每通道差異 ≤ 該測試現有的容差（GPU 不可用時以 Assert.Skip 略過）。
    }
```

> 實作者：把 `Gpu_MatchesCpu_OnFilmAndGrain` 的本體複製進來，只換 `settings`。這是「照抄同檔案既有本體」，不是新邏輯。

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~Gpu_MatchesCpu_OnFilmLook"`
Expected: FAIL（GPU 仍走舊 film shader 或跳過 film）。若機器沒有 GPU 會 Skip——此時在有 GPU 的機器或 CI（Dawn 軟體介面卡）驗證。

- [ ] **Step 3: 實作 `film/look.wgsl`**

```wgsl
// 底片風格（docs/film-looks.md）：與 FilmLookProcessing.cs 同演算法。
#include "common/color.wgsl"

struct LookParams {
    mix: vec4f,   // 效果比例、暖色、褪色、LUT 邊長
    flags: vec4f, // x：分色相啟用
    bands: array<vec4f, 8>, // 色相、飽和、明度（-1..1）
}

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var lut: texture_2d<f32>;
@group(0) @binding(2) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(3) var<uniform> p: LookParams;

fn lut_at(r: i32, g: i32, b: i32, n: i32) -> vec3f {
    return textureLoad(lut, vec2i(r + b * n, g), 0).rgb;
}

fn lut_sample(c: vec3f) -> vec3f {
    let n = i32(p.mix.w);
    let maxi = f32(n - 1);
    let pos = clamp(c, vec3f(0.0), vec3f(1.0)) * maxi;
    let i0 = min(vec3i(pos), vec3i(n - 2));
    let f = pos - vec3f(i0);
    let r = i0.x; let g = i0.y; let b = i0.z;
    let c000 = lut_at(r, g, b, n);
    let c111 = lut_at(r + 1, g + 1, b + 1, n);
    if (f.x >= f.y) {
        if (f.y >= f.z) {
            let c100 = lut_at(r + 1, g, b, n); let c110 = lut_at(r + 1, g + 1, b, n);
            return c000 + f.x * (c100 - c000) + f.y * (c110 - c100) + f.z * (c111 - c110);
        }
        if (f.x >= f.z) {
            let c100 = lut_at(r + 1, g, b, n); let c101 = lut_at(r + 1, g, b + 1, n);
            return c000 + f.x * (c100 - c000) + f.z * (c101 - c100) + f.y * (c111 - c101);
        }
        let c001 = lut_at(r, g, b + 1, n); let c101 = lut_at(r + 1, g, b + 1, n);
        return c000 + f.z * (c001 - c000) + f.x * (c101 - c001) + f.y * (c111 - c101);
    }
    if (f.z >= f.y) {
        let c001 = lut_at(r, g, b + 1, n); let c011 = lut_at(r, g + 1, b + 1, n);
        return c000 + f.z * (c001 - c000) + f.y * (c011 - c001) + f.x * (c111 - c011);
    }
    if (f.z >= f.x) {
        let c010 = lut_at(r, g + 1, b, n); let c011 = lut_at(r, g + 1, b + 1, n);
        return c000 + f.y * (c010 - c000) + f.z * (c011 - c010) + f.x * (c111 - c011);
    }
    let c010 = lut_at(r, g + 1, b, n); let c110 = lut_at(r + 1, g + 1, b, n);
    return c000 + f.y * (c010 - c000) + f.x * (c110 - c010) + f.z * (c111 - c110);
}

fn wrap360(x: f32) -> f32 { return ((x % 360.0) + 360.0) % 360.0; }
fn wrap180(x: f32) -> f32 { let w = wrap360(x); return select(w, w - 360.0, w > 180.0); }

fn band_weight(h: f32, k: u32) -> f32 {
    var centers = array<f32, 8>(25.0, 55.0, 105.0, 140.0, 195.0, 255.0, 295.0, 335.0);
    let c = centers[k];
    let d = wrap180(h - c);
    if (d >= 0.0) { return max(0.0, 1.0 - d / wrap360(centers[(k + 1u) % 8u] - c)); }
    return max(0.0, 1.0 + d / wrap360(c - centers[(k + 7u) % 8u]));
}

fn to_oklab(c: vec3f) -> vec3f {
    let l = pow(0.41222147 * c.r + 0.53633254 * c.g + 0.05144599 * c.b, 1.0 / 3.0);
    let m = pow(0.21190350 * c.r + 0.68069955 * c.g + 0.10739696 * c.b, 1.0 / 3.0);
    let s = pow(0.08830246 * c.r + 0.28171884 * c.g + 0.62997870 * c.b, 1.0 / 3.0);
    return vec3f(0.21045426 * l + 0.79361779 * m - 0.00407205 * s,
        1.97799850 * l - 2.42859221 * m + 0.45059371 * s,
        0.02590404 * l + 0.78277177 * m - 0.80867577 * s);
}

fn from_oklab(c: vec3f) -> vec3f {
    var l = c.x + 0.39633778 * c.y + 0.21580376 * c.z;
    var m = c.x - 0.10556135 * c.y - 0.06385417 * c.z;
    var s = c.x - 0.08948418 * c.y - 1.29148555 * c.z;
    l = l * l * l; m = m * m * m; s = s * s * s;
    return vec3f(4.07674166 * l - 3.30771159 * m + 0.23096993 * s,
        -1.26843800 * l + 2.60975740 * m - 0.34131940 * s,
        -0.00419609 * l - 0.70341861 * m + 1.70761470 * s);
}

fn hue_bands(rgb: vec3f) -> vec3f {
    let lab = to_oklab(max(rgb, vec3f(0.0)));
    let chroma = length(lab.yz);
    if (chroma < 1e-5) { return rgb; }
    let hue = wrap360(degrees(atan2(lab.z, lab.y)));
    var delta = vec3f(0.0);
    for (var k = 0u; k < 8u; k++) {
        let w = band_weight(hue, k);
        if (w > 0.0) { delta += w * p.bands[k].xyz; }
    }
    let gate = smoothstep(0.02, 0.08, chroma);
    let h = radians(hue + delta.x * 30.0 * gate);
    let c = chroma * max(0.0, 1.0 + delta.y);
    let lightness = lab.x + delta.z * 0.12 * gate;
    return max(from_oklab(vec3f(lightness, c * cos(h), c * sin(h))), vec3f(0.0));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    if (any(id.xy >= textureDimensions(src))) { return; }
    let px = textureLoad(src, id.xy, 0);
    let original = px.rgb;
    var look = srgb_to_linear(lut_sample(linear_to_srgb(clamp(original, vec3f(0.0), vec3f(1.0)))));
    if (p.flags.x != 0.0) { look = hue_bands(look); }
    let warmth = p.mix.y;
    if (warmth != 0.0) { look *= vec3f(1.0 + 0.12 * warmth, 1.0, 1.0 - 0.12 * warmth); }
    let fade = p.mix.z;
    if (fade != 0.0) { look = srgb_to_linear(linear_to_srgb(max(look, vec3f(0.0))) * (1.0 - 0.2 * fade) + vec3f(0.1 * fade)); }
    textureStore(dst, id.xy, vec4f(mix(original, look, p.mix.x), px.a));
}
```

> - `pow(x, 1/3)` 對負值未定義；輸入已 `max(rgb, 0)`，與 C# `MathF.Cbrt` 在非負值相同。
> - `smoothstep` 是 WGSL 內建，與 C# 版公式相同。
> - `vec3i(pos)` 對非負值等同 C# 的 `(int)` 截斷；`min(…, n-2)` 對應 C# 的 `Math.Min(…, max - 1)`。

- [ ] **Step 4: 修改 `GpuEffectPipeline.cs`**

1. kernel 欄位：把 `_film, _droBase, _droApply, _filmBlend` 換成 `_look`。
2. 建構子：刪除 `film/film_pixel`、`film/dro_base`、`film/film_dro_sharpen`、`film/blend` 四行，加入：

```csharp
        _look = K("film/look", tex, tex, storage, uniform);
```

3. 新增欄位與方法：

```csharp
    // 內附 LUT 的紋理（FilmLookLibrary 對同一 key 回傳同一個 FilmLut，以參考相等快取）
    private readonly Dictionary<FilmLut, GpuTexture> _luts = new(ReferenceEqualityComparer.Instance);

    private GpuTexture LutTexture(FilmLut lut)
    {
        if (_luts.TryGetValue(lut, out var texture)) return texture;
        var n = (uint)lut.Size;
        texture = _gpu.CreateTexture(n * n, n, GpuTextureFormat.Rgba32Float, GpuTextureUsage.CopyDst | GpuTextureUsage.Sampled, "film lut");
        _gpu.WriteTexture<float>(texture, lut.ToTextureRgba());
        _luts.Add(lut, texture);
        return texture;
    }
```

4. `Process` 中整段 `if (p.FilmOn && p.Film.Mix.X > 0) { … }` 換成：

```csharp
        if (p.FilmOn && p.FilmLut is not null && p.FilmStrength > 0)
            current = Apply(_look, p.FilmLook, LutTexture(p.FilmLut));
```

5. `Dispose()` 加入 `foreach (var texture in _luts.Values) texture.Dispose();`。
6. 檔頭加 `using FilmBatch.Core.Film;`。`EffectRendererOptions.RecipesFolder` 屬性刪除（先 `git grep -n RecipesFolder` 確認無其他使用者）。

> `_luts` 的 key 型別是 `FilmLut`，`ReferenceEqualityComparer.Instance` 實作 `IEqualityComparer<object?>`，需要 `new Dictionary<FilmLut, GpuTexture>((IEqualityComparer<FilmLut>)ReferenceEqualityComparer.Instance)`；`FilmLut` 沒有覆寫 Equals，直接用預設比較子也等同參考相等，二選一即可。

- [ ] **Step 5: 更新 `Gpu_MatchesCpu_OnFilmAndGrain`**：`FilmOnly(1.0)` → `FilmOnly()`（若 Task 4 已改則略過）。

- [ ] **Step 6: 執行測試與 shader 驗證**

Run: `dotnet test tests/FilmBatch.Core.Tests --filter "FullyQualifiedName~EffectPipelineTests"`
Expected: PASS（含 `Gpu_MatchesCpu_OnFilmLook`）。

Run: `dotnet run build/CheckShaders.cs -- --naga naga`（若本機有 naga；CI 會跑）
Expected: 所有 shader 通過，包含 `v1/film/look.wgsl`。

- [ ] **Step 7: Commit**

```bash
git add src/FilmBatch.Gpu/Shaders/v1/film/look.wgsl src/FilmBatch.Engine/Rendering/GpuEffectPipeline.cs tests/FilmBatch.Core.Tests/EffectPipelineTests.cs
git commit -m "Render film looks on GPU with tetrahedral LUT sampling"
```

---

### Task 6: 移除 Recipe Lab 底片模組的所有殘留

**Files:**
- Delete: 見上方「刪除」清單
- Modify: `src/FilmBatch.Core/FilmBatch.Core.csproj`（刪兩個舊 JSON 資源與 `InternalsVisibleTo film-calibrate`）、`src/FilmBatch.Engine/FilmBatch.Engine.csproj`（刪 `InternalsVisibleTo film-calibrate`）
- Modify: `src/FilmBatch.Cli/Program.cs`（刪 `recipes` 指令，`RootCommand` 移除 `recipes`）
- Modify: `src/FilmBatch.Core/AppPaths.cs`（刪 `Recipes`，確認無使用者）
- Modify: `tests/FilmBatch.Core.Tests/FilmBatch.Core.Tests.csproj`（刪 `Compile Include="..\..\tools\film-calibration\Color.cs"` 與 `Fixtures\Film` 兩行）
- Modify: `tests/FilmBatch.Core.Tests/PhotoOperationsTests.cs`（依 `git grep -n "film\." tests/FilmBatch.Core.Tests/PhotoOperationsTests.cs` 把舊 key 換成新 key，例如 `film.style` → `film.look`、值改用 `looks.json` 的 key）
- Modify: `tests/FilmBatch.App.Tests/LocalizationTests.cs`（若有列舉 `Film.*` 字串的斷言，改成新 key）

- [ ] **Step 1: 刪檔**

```bash
git rm src/FilmBatch.Core/Film/CreativeStyleTable.cs src/FilmBatch.Core/Film/CustomRecipeFile.cs src/FilmBatch.Core/Film/CustomRecipeModels.cs src/FilmBatch.Core/Film/FilmRecipe.cs src/FilmBatch.Core/Film/FilmRecipeLibrary.cs src/FilmBatch.Core/Film/FilmRecipesData.cs src/FilmBatch.Core/Film/creative_styles.json src/FilmBatch.Core/Film/recipelab.json
git rm src/FilmBatch.Engine/Rendering/FilmParameters.cs src/FilmBatch.Engine/Rendering/FilmProcessing.cs
git rm src/FilmBatch.Gpu/Shaders/v1/film/film_pixel.wgsl src/FilmBatch.Gpu/Shaders/v1/film/dro_base.wgsl src/FilmBatch.Gpu/Shaders/v1/film/film_dro_sharpen.wgsl src/FilmBatch.Gpu/Shaders/v1/film/blend.wgsl
git rm -r tools/film-calibration tests/FilmBatch.Core.Tests/Fixtures/Film
git rm tests/FilmBatch.Core.Tests/FilmRecipeTests.cs tests/FilmBatch.Core.Tests/FilmCalibrationTests.cs
git rm docs/film-calibration.md docs/film-provenance.md
```

再執行 `git grep -n 'film/common.wgsl' src`；若結果為空，`git rm src/FilmBatch.Gpu/Shaders/v1/film/common.wgsl`。App 端（`FilmRecipeViewModel`、`FilmRecipeControl`、`FilmRecipeViewModelTests`）留到 Task 7 與新 UI 一起換，避免 App 中間狀態無法編譯；本 Task 只確保 Core／Engine／Cli／Core.Tests 可編譯。

- [ ] **Step 2: 修改 csproj、Cli、AppPaths、測試**（照上方 Files 清單逐一處理）

- [ ] **Step 3: 確認沒有殘留**

Run: `git grep -nE "FilmRecipe|CreativeStyle|CustomRecipe|recipelab|creative_styles|film-calibrat|FilmParameters|FilmProcessing\b|AppPaths.Recipes" -- src tests tools build ':!src/FilmBatch.App' ':!tests/FilmBatch.App.Tests'`
Expected: 無輸出。

- [ ] **Step 4: 建置與測試**

Run: `dotnet build src/FilmBatch.Cli && dotnet test tests/FilmBatch.Core.Tests`
Expected: 建置成功、Core 測試全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add -A src/FilmBatch.Core src/FilmBatch.Engine src/FilmBatch.Gpu src/FilmBatch.Cli tests/FilmBatch.Core.Tests tools docs
git commit -m "Remove Recipe Lab film recipes, Sony coefficients and calibration tool"
```

---

### Task 7: 縮圖格狀 UI（底片／色相修正）與色相帶編輯器

**Files:**
- Create: `src/FilmBatch.App/ViewModels/FilmLookThumbnails.cs`
- Create: `src/FilmBatch.App/ViewModels/FilmLookViewModel.cs`
- Create: `src/FilmBatch.App/Controls/FilmLookControl.axaml`、`FilmLookControl.axaml.cs`
- Modify: `src/FilmBatch.App/ViewModels/ModuleSectionViewModel.cs`、`InspectorViewModel.cs`、`MainWindowViewModel.cs`
- Modify: `src/FilmBatch.App/Views/InspectorView.axaml:77-85`
- Modify: `src/FilmBatch.App/Localization/Strings.resx`、`Strings.en.resx`
- Delete: `src/FilmBatch.App/ViewModels/FilmRecipeViewModel.cs`、`Controls/FilmRecipeControl.axaml(.cs)`、`tests/FilmBatch.App.Tests/FilmRecipeViewModelTests.cs`
- Test: `tests/FilmBatch.App.Tests/FilmLookViewModelTests.cs`

**Interfaces:**
- Consumes: `FilmLookLibrary`, `HueBands`, `HuePresets`（Core）；`MainWindowViewModel.Service/Selection/FocusFile/Preview.Source/Renderer`、`ProjectService.SetValues(IReadOnlyCollection<string>, Settings, string)`、`ProjectService.SettingsFor(string)`、`PreviewService.RequestThumbnail(string, SourceImage, Settings)`、`ThumbnailReady` / `Failed` 事件、`PresetHoverCoordinator.ToBitmap(RenderResult)`、`SourceImage.Downscale(int)`、`ParamViewModel`
- Produces:
  - `public sealed partial class FilmTileViewModel : ObservableObject { string Key; string Name; bool IsSelected; Bitmap? Thumbnail; bool IsLoading; }`
  - `public sealed partial class HueBandViewModel(int index, string label, ParamViewModel hue, ParamViewModel saturation, ParamViewModel lightness)`
  - `public sealed partial class FilmLookViewModel : ObservableObject { IReadOnlyList<string> Groups; string SelectedGroup; ObservableCollection<FilmTileViewModel> Looks; ObservableCollection<FilmTileViewModel> HuePresets; IReadOnlyList<HueBandViewModel> Bands; HueBandViewModel SelectedBand; bool IsCustomHue; IRelayCommand<FilmTileViewModel> ApplyLookCommand, ApplyHuePresetCommand; void Attach(IReadOnlyList<ParamViewModel> hueParams); void RefreshSelection(); void RequestThumbnails(); }`
  - `public sealed class FilmLookThumbnails { const string KeyPrefix = "film-look/"; const int LongEdge = 128; void Request(...); void OnThumbnailReady(string, RenderResult); void OnThumbnailFailed(string, string); void Invalidate(); }`

**UI 規格**

- film 模組展開後頂部依序：
  1. 「底片」：分組下拉（柯達／富士／愛克發與 Lomo／拍立得／黑白／創意），下面是 3 欄縮圖格（每格 96×64 縮圖＋名稱），目前底片加框。點縮圖 → `film.enabled = true`、`film.look = key`，寫入選取中的所有照片（一筆復原紀錄）。
  2. 一般參數列：效果比例、暖色、褪色（沿用 `ParamRow`）。
  3. 「色相修正」：內建預設縮圖格（無／暖膚色／清澈藍天／青翠綠意／金黃秋色／冷暖對比／低彩度），縮圖套用目前底片＋該修正。目前值不符合任何預設時顯示「自訂」標記。
  4. 「自訂色相」：八個色塊單選（紅…洋紅），下方三條滑桿（色相／飽和／明度）綁到所選色相帶的 `ParamViewModel`；任一滑桿改動即成為「自訂」。
- 縮圖：以焦點照片的預覽代理圖 `Downscale(128)` 算圖，設定 = 焦點照片目前最終參數 ＋ 覆寫 look 或 24 個色相值；只在 film 模組展開時算，焦點或設定變更後 400 ms 防抖重算；走共用的低優先縮圖佇列，鍵 `film-look/{look|hue}/{key}/{焦點檔名}/{序號}`，與縮圖列、懸停預覽的鍵不相交。
- 縮圖最多快取「目前焦點 × 目前設定」一組（36 + 7 張），設定一變就全部丟棄。

- [ ] **Step 1: 寫失敗的測試 `FilmLookViewModelTests.cs`**（沿用 `FilmRecipeViewModelTests` 的 `TestSupport` 建立方式：先讀被刪檔案的 setup 寫法，再照抄成以下測試）

```csharp
public class FilmLookViewModelTests
{
    [AvaloniaFact]
    public void ApplyLook_WritesLookAndEnablesModule_ForSelection()
    {
        using var ctx = TestSupport.CreateMainWithPhotos(2); // 依 TestSupport 實際 API
        var film = ctx.Main.Inspector.FilmLook;
        film.SelectedGroup = "fuji";
        var velvia = film.Looks.Single(t => t.Key == "fuji-velvia-50");
        film.ApplyLookCommand.Execute(velvia);
        foreach (var file in ctx.Main.Selection)
        {
            var s = ctx.Main.Service!.SettingsFor(file);
            Assert.True(s.Bool("film.enabled"));
            Assert.Equal("fuji-velvia-50", s["film.look"].AsText);
        }
        Assert.True(velvia.IsSelected);
    }

    [AvaloniaFact]
    public void ApplyHuePreset_WritesAll24Values_AndCustomFlagFollowsEdits()
    {
        using var ctx = TestSupport.CreateMainWithPhotos(1);
        var film = ctx.Main.Inspector.FilmLook;
        film.ApplyHuePresetCommand.Execute(film.HuePresets.Single(t => t.Key == "clear-sky"));
        var s = ctx.Main.Service!.SettingsFor(ctx.Main.FocusFile!);
        Assert.Equal(20, s["film.hue_blue_s"].Number);
        Assert.Equal(0, s["film.hue_red_h"].Number);
        Assert.False(film.IsCustomHue);
        film.SelectedBand = film.Bands[0];
        film.SelectedBand.Hue.SetNumber(12); // 依 ParamViewModel 實際 API（SliderParamViewModel 的 Value setter）
        film.RefreshSelection();
        Assert.True(film.IsCustomHue);
    }

    [AvaloniaFact]
    public void FilmModule_GenericParams_ExcludeLookAndHueBands()
    {
        using var ctx = TestSupport.CreateMainWithPhotos(1);
        var section = ctx.Main.Inspector.Modules.Single(m => m.Key == "film");
        Assert.Equal(["strength", "warmth", "fade"], section.Params.Select(p => p.Def.Key));
        Assert.Equal(8, section.FilmLook!.Bands.Count);
    }

    [AvaloniaFact]
    public void Thumbnails_RenderOnlyWhileFilmExpanded_WithNamespacedKeys()
    {
        using var ctx = TestSupport.CreateMainWithPhotos(1);
        var section = ctx.Main.Inspector.Modules.Single(m => m.Key == "film");
        var keys = ctx.CapturedThumbnailKeys; // 依 TestSupport 的假 renderer／PreviewService 攔截方式
        section.IsExpanded = false;
        ctx.PumpUntilIdle();
        Assert.DoesNotContain(keys, k => k.StartsWith(FilmLookThumbnails.KeyPrefix));
        section.IsExpanded = true;
        ctx.PumpUntil(() => section.FilmLook!.Looks.All(t => t.Thumbnail is not null));
        Assert.Contains(keys, k => k.StartsWith("film-look/look/"));
        Assert.Contains(keys, k => k.StartsWith("film-look/hue/"));
    }
}
```

> 這四個測試的「行為」是必須的；`TestSupport` 的建立／攔截方法名稱以現有 `FilmRecipeViewModelTests`、`PresetHoverPreviewTests` 為準（先讀這兩個檔案）。若 `TestSupport` 沒有攔截縮圖鍵的機制，參考 `PresetHoverPreviewTests` 如何驗證懸停算圖，用同樣方式。

- [ ] **Step 2: 執行測試確認失敗**

Run: `dotnet test tests/FilmBatch.App.Tests --filter "FullyQualifiedName~FilmLookViewModelTests"`
Expected: 編譯失敗。

- [ ] **Step 3: 實作 `FilmLookThumbnails.cs`**

```csharp
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FilmBatch.App.Services;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.ViewModels;

/// <summary>
/// 底片／色相修正縮圖：以焦點照片的縮小代理圖、目前最終參數加上各選項的覆寫值算圖。
/// 共用 PreviewService 的低優先縮圖佇列；鍵 <c>film-look/{kind}/{key}/{焦點}/{序號}</c> 與其他縮圖鍵不相交。
/// 父層接線：<c>Renderer.ThumbnailReady += OnThumbnailReady; Renderer.Failed += OnThumbnailFailed;</c>
/// </summary>
public sealed class FilmLookThumbnails(MainWindowViewModel main)
{
    public const string KeyPrefix = "film-look/";
    public const int LongEdge = 128;
    public static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<string, FilmTileViewModel> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<SourceImage> _sources = []; // GPU 執行緒完成前保留來源參考
    private DispatcherTimer? _timer;
    private Func<IEnumerable<(string Kind, FilmTileViewModel Tile, Settings Settings)>>? _jobs;
    private long _sequence;
    private long _generation;

    /// <summary>排程一次重算（防抖）；jobs 在到期時才求值，讀當下的焦點與設定。</summary>
    public void Request(Func<IEnumerable<(string Kind, FilmTileViewModel Tile, Settings Settings)>> jobs)
    {
        _jobs = jobs;
        _timer ??= new DispatcherTimer(Debounce, DispatcherPriority.Background, (_, _) => Flush());
        _timer.Interval = Debounce;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>焦點或設定改變：舊縮圖全部作廢（結果回來時以 generation 比對丟棄）。</summary>
    public void Invalidate()
    {
        _generation++;
        _pending.Clear();
        _sources.Clear();
        _timer?.Stop();
    }

    private void Flush()
    {
        _timer?.Stop();
        var focus = main.FocusFile;
        var preview = main.Preview.Source;
        if (_jobs is null || focus is null || preview is null || main.Service is null) return;
        var source = preview.Downscale(LongEdge);
        _sources.Add(source);
        foreach (var (kind, tile, settings) in _jobs())
        {
            var key = $"{KeyPrefix}{kind}/{tile.Key}/{Uri.EscapeDataString(focus)}/{_generation}.{++_sequence}";
            _pending[key] = tile;
            tile.IsLoading = true;
            main.Renderer.RequestThumbnail(key, source, settings);
        }
    }

    public void OnThumbnailReady(string key, RenderResult result)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || !_pending.Remove(key, out var tile)) return;
        var old = tile.Thumbnail;
        tile.Thumbnail = PresetHoverCoordinator.ToBitmap(result);
        tile.IsLoading = false;
        old?.Dispose();
        if (_pending.Count == 0) _sources.Clear();
    }

    public void OnThumbnailFailed(string key, string message)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || !_pending.Remove(key, out var tile)) return;
        tile.IsLoading = false;
        AppLog.Warn($"底片縮圖算圖失敗：{key}：{message}");
    }
}
```

> `Invalidate` 遞增 `_generation` 並清 `_pending`：已送出但尚未回來的結果因為不在 `_pending` 中而被丟棄，這就是過期判定，不需另外比對 generation。

- [ ] **Step 4: 實作 `FilmLookViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>縮圖格中的一格（底片或色相修正預設）。</summary>
public sealed partial class FilmTileViewModel(string key, string name) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isLoading;
}

/// <summary>自訂色相的一個色相帶：三個滑桿直接是 film 模組的 ParamViewModel（覆寫標記、復原合併照舊）。</summary>
public sealed class HueBandViewModel(int index, string label, string swatch, ParamViewModel hue, ParamViewModel saturation, ParamViewModel lightness)
{
    public int Index { get; } = index;
    public string Label { get; } = label;
    public string Swatch { get; } = swatch;
    public ParamViewModel Hue { get; } = hue;
    public ParamViewModel Saturation { get; } = saturation;
    public ParamViewModel Lightness { get; } = lightness;
}

/// <summary>film 模組頂部：底片縮圖格、色相修正縮圖格與自訂色相帶編輯器。</summary>
public sealed partial class FilmLookViewModel : ObservableObject
{
    private static readonly string[] Swatches = ["#E04848", "#F08A3C", "#E8CF3A", "#4CB050", "#3EC1C9", "#3F6FE0", "#8A55D6", "#D64FA8"];
    private readonly MainWindowViewModel _main;
    private bool _expanded;

    public FilmLookViewModel(MainWindowViewModel main)
    {
        _main = main;
        Thumbnails = new FilmLookThumbnails(main);
        HuePresets = new(Core.Film.HuePresets.All.Select(p => new FilmTileViewModel(p.Key, Loc.TryGet($"Film.Hue.{p.Key}") ?? p.Name)));
        _selectedGroup = FilmLookLibrary.GroupsInOrder[0];
        Looks = [];
        LoadGroup();
    }

    public FilmLookThumbnails Thumbnails { get; }
    public IReadOnlyList<string> Groups => FilmLookLibrary.GroupsInOrder;
    public ObservableCollection<FilmTileViewModel> Looks { get; }
    public ObservableCollection<FilmTileViewModel> HuePresets { get; }
    public IReadOnlyList<HueBandViewModel> Bands { get; private set; } = [];

    [ObservableProperty] private string _selectedGroup;
    [ObservableProperty] private HueBandViewModel? _selectedBand;
    [ObservableProperty] private bool _isCustomHue;

    public static string GroupLabel(string group) => Loc.TryGet($"Film.Group.{group}") ?? group;

    /// <summary>ModuleSectionViewModel 建立 film 參數後呼叫：24 個色相參數交給編輯器顯示。</summary>
    public void Attach(IReadOnlyList<ParamViewModel> hueParams)
    {
        Bands = [.. Enumerable.Range(0, 8).Select(b => new HueBandViewModel(b,
            Loc.TryGet($"Film.Band.{HueBands.Keys[b]}") ?? HueBands.Labels[b], Swatches[b],
            hueParams[b * 3], hueParams[b * 3 + 1], hueParams[b * 3 + 2]))];
        SelectedBand = Bands[0];
        OnPropertyChanged(nameof(Bands));
    }

    partial void OnSelectedGroupChanged(string value)
    {
        LoadGroup();
        RefreshSelection();
        RequestThumbnails();
    }

    /// <summary>模組展開／收合：只在展開時算縮圖。</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (expanded) RequestThumbnails();
    }

    /// <summary>焦點或設定改變後由 Inspector 呼叫：更新選取框與自訂標記，並重算縮圖。</summary>
    public void RefreshSelection()
    {
        var resolved = Resolved();
        var look = resolved?.TryGetValue("film.look", out var v) == true ? v.AsText : FilmLookLibrary.DefaultKey;
        foreach (var tile in Looks) tile.IsSelected = tile.Key == look;
        var preset = resolved is null ? null : Core.Film.HuePresets.Find(resolved);
        foreach (var tile in HuePresets) tile.IsSelected = tile.Key == preset?.Key;
        IsCustomHue = resolved is not null && preset is null;
    }

    public void OnSettingsChanged()
    {
        RefreshSelection();
        Thumbnails.Invalidate();
        RequestThumbnails();
    }

    public void RequestThumbnails()
    {
        if (!_expanded) return;
        Thumbnails.Request(() =>
        {
            var resolved = Resolved();
            if (resolved is null) return [];
            var on = resolved.With("film.enabled", ParamValue.Of(true));
            var looks = Looks.Select(t => ("look", t, on.With("film.look", ParamValue.Of(t.Key))));
            var hues = Core.Film.HuePresets.All.Zip(HuePresets).Select(pair =>
                ("hue", pair.Second, pair.First.ToSettings().Aggregate(on, (s, kv) => s.With(kv.Key, kv.Value))));
            return looks.Concat(hues).ToList();
        });
    }

    [RelayCommand]
    private void ApplyLook(FilmTileViewModel? tile)
    {
        if (tile is null || _main.Service is null || _main.Selection.Count == 0) return;
        var values = new Settings([
            new("film.enabled", ParamValue.Of(true)),
            new("film.look", ParamValue.Of(tile.Key)),
        ]);
        if (_main.Service.SetValues(_main.Selection, values, Loc.Format("Film.LookApplied", tile.Name)))
            OnSettingsChanged();
    }

    [RelayCommand]
    private void ApplyHuePreset(FilmTileViewModel? tile)
    {
        var preset = Core.Film.HuePresets.All.FirstOrDefault(p => p.Key == tile?.Key);
        if (preset is null || _main.Service is null || _main.Selection.Count == 0) return;
        if (_main.Service.SetValues(_main.Selection, preset.ToSettings(), Loc.Format("Film.HueApplied", tile!.Name)))
            OnSettingsChanged();
    }

    private void LoadGroup()
    {
        foreach (var tile in Looks) tile.Thumbnail?.Dispose();
        Looks.Clear();
        foreach (var look in FilmLookLibrary.All.Where(l => l.Group == SelectedGroup))
            Looks.Add(new FilmTileViewModel(look.Key, look.Name));
    }

    private Settings? Resolved() =>
        _main.Service is { } service && _main.FocusFile is { } focus ? service.SettingsFor(focus) : null;
}
```

> - 已核對：`Loc.TryGet(string)` 與 `Loc.Format(string, params object[])` 皆為 public static。
> - `SetValues` 成功後，`MainWindowViewModel` 原本就會觸發 Inspector 重新 Load；若 Inspector 重新 Load 時已呼叫 `FilmLook.OnSettingsChanged()`（Step 6），這裡的 `OnSettingsChanged()` 可省略，避免重算兩次。

- [ ] **Step 5: 修改 `ModuleSectionViewModel.cs`**

```csharp
    public ModuleSectionViewModel(ModuleSchema schema, IParamHost host,
        Action<ModuleSectionViewModel> onReset, Action<ModuleSectionViewModel> onCopyOnly,
        FilmLookViewModel? filmLook = null)
    {
        Schema = schema;
        _host = host;
        _onReset = onReset;
        _onCopyOnly = onCopyOnly;
        _allParams = [.. schema.Params
            .Where(p => p.Key != ModuleSchema.EnabledKey)
            .Select(p => ParamViewModel.Create(schema.Key, p, host))];
        FilmLook = schema.Key == "film" ? filmLook : null;
        // film 模組：底片（look）由縮圖格選、24 個色相參數由色相帶編輯器顯示，一般參數列只留其餘欄位。
        Params = new(FilmLook is null ? _allParams : _allParams.Where(p => p.Def.Key != "look" && !p.Def.Key.StartsWith("hue_", StringComparison.Ordinal)));
        FilmLook?.Attach([.. _allParams.Where(p => p.Def.Key.StartsWith("hue_", StringComparison.Ordinal))]);
    }

    private readonly List<ParamViewModel> _allParams;

    /// <summary>film 模組頂部的底片風格選擇；其他模組為 null。</summary>
    public FilmLookViewModel? FilmLook { get; }
```

`RefreshLabels` 與 `Load` 改為迭代 `_allParams`（否則色相滑桿不會載入值）；`OnIsExpandedChanged` 加 `FilmLook?.SetExpanded(value);`。刪除舊的 `Film` 屬性。

> `HueBands.Params()` 的順序是 band × (h, s, l)，所以 `Where(StartsWith("hue_"))` 後的第 `b*3+c` 個就是對應參數，與 `Attach` 的索引一致。

- [ ] **Step 6: 修改 `InspectorViewModel.cs` 與 `MainWindowViewModel.cs`**

- `InspectorViewModel`：`public FilmRecipeViewModel Film { get; }` → `public FilmLookViewModel FilmLook { get; }`；`Film = new FilmRecipeViewModel(main);` → `FilmLook = new FilmLookViewModel(main);`；建立模組時傳 `FilmLook`；原本兩處 `Film.RefreshSelection();`（第 95、112 行附近，焦點與設定變更時）改為 `FilmLook.OnSettingsChanged();`。
- `MainWindowViewModel`：在 `Renderer.ThumbnailReady += Presets.Hover.OnThumbnailReady;` 旁加入：

```csharp
        Renderer.ThumbnailReady += Inspector.FilmLook.Thumbnails.OnThumbnailReady;
        Renderer.Failed += Inspector.FilmLook.Thumbnails.OnThumbnailFailed;
```

（若 `Inspector` 在這兩行之後才建立，把接線移到 `Inspector` 建立之後、`Renderer.Start()` 之前。）並在原本 `Presets.Hover.InvalidateCache()` 的兩處（重建裝置、開新資料夾）加 `Inspector.FilmLook.Thumbnails.Invalidate();`。

- [ ] **Step 7: 實作 `FilmLookControl.axaml`**

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:FilmBatch.App.ViewModels"
             xmlns:c="using:FilmBatch.App.Controls"
             xmlns:l="using:FilmBatch.App.Localization"
             x:Class="FilmBatch.App.Controls.FilmLookControl"
             x:DataType="vm:FilmLookViewModel">
  <!-- 底片顏色模擬：底片縮圖格 → 色相修正縮圖格 → 自訂色相帶（docs/film-looks.md） -->
  <StackPanel Spacing="8">
    <Grid ColumnDefinitions="*,Auto" Classes="param-line">
      <TextBlock Classes="param-name" Text="{l:Tr Film.Look}" VerticalAlignment="Center" />
      <ComboBox Grid.Column="1" Classes="flat" MinWidth="150"
                ItemsSource="{Binding Groups}" SelectedItem="{Binding SelectedGroup}"
                AutomationProperties.Name="{l:Tr Film.Look}">
        <ComboBox.ItemTemplate>
          <DataTemplate x:DataType="x:String">
            <TextBlock Text="{Binding Converter={x:Static c:FilmLookControl.GroupLabelConverter}}" />
          </DataTemplate>
        </ComboBox.ItemTemplate>
      </ComboBox>
    </Grid>
    <ItemsControl ItemsSource="{Binding Looks}">
      <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate><UniformGrid Columns="3" /></ItemsPanelTemplate>
      </ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:FilmTileViewModel">
          <Button Classes="film-tile" Classes.selected="{Binding IsSelected}" Margin="2" Padding="2"
                  Command="{Binding $parent[ItemsControl].((vm:FilmLookViewModel)DataContext).ApplyLookCommand}"
                  CommandParameter="{Binding}" ToolTip.Tip="{Binding Name}"
                  AutomationProperties.Name="{Binding Name}">
            <StackPanel Spacing="2">
              <Border Classes="film-thumb" Height="64">
                <Panel>
                  <Image Source="{Binding Thumbnail}" Stretch="UniformToFill" />
                  <ProgressBar IsIndeterminate="True" IsVisible="{Binding IsLoading}" VerticalAlignment="Bottom" />
                </Panel>
              </Border>
              <TextBlock Classes="small" Text="{Binding Name}" TextTrimming="CharacterEllipsis" HorizontalAlignment="Center" />
            </StackPanel>
          </Button>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>

    <Grid ColumnDefinitions="*,Auto" Classes="param-line">
      <TextBlock Classes="param-name" Text="{l:Tr Film.HueCorrection}" VerticalAlignment="Center" />
      <Border Grid.Column="1" Classes="edited-badge" IsVisible="{Binding IsCustomHue}">
        <TextBlock Classes="small" Text="{l:Tr Film.HueCustom}" />
      </Border>
    </Grid>
    <ItemsControl ItemsSource="{Binding HuePresets}">
      <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate><UniformGrid Columns="3" /></ItemsPanelTemplate>
      </ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:FilmTileViewModel">
          <Button Classes="film-tile" Classes.selected="{Binding IsSelected}" Margin="2" Padding="2"
                  Command="{Binding $parent[ItemsControl].((vm:FilmLookViewModel)DataContext).ApplyHuePresetCommand}"
                  CommandParameter="{Binding}" AutomationProperties.Name="{Binding Name}">
            <StackPanel Spacing="2">
              <Border Classes="film-thumb" Height="64">
                <Panel>
                  <Image Source="{Binding Thumbnail}" Stretch="UniformToFill" />
                  <ProgressBar IsIndeterminate="True" IsVisible="{Binding IsLoading}" VerticalAlignment="Bottom" />
                </Panel>
              </Border>
              <TextBlock Classes="small" Text="{Binding Name}" TextTrimming="CharacterEllipsis" HorizontalAlignment="Center" />
            </StackPanel>
          </Button>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>

    <TextBlock Classes="param-name" Text="{l:Tr Film.HueBands}" />
    <ListBox Classes="hue-bands" ItemsSource="{Binding Bands}" SelectedItem="{Binding SelectedBand}">
      <ListBox.ItemsPanel>
        <ItemsPanelTemplate><UniformGrid Columns="8" /></ItemsPanelTemplate>
      </ListBox.ItemsPanel>
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:HueBandViewModel">
          <Ellipse Width="18" Height="18" Fill="{Binding Swatch}" ToolTip.Tip="{Binding Label}"
                   AutomationProperties.Name="{Binding Label}" />
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
    <ContentControl Content="{Binding SelectedBand}">
      <ContentControl.ContentTemplate>
        <DataTemplate x:DataType="vm:HueBandViewModel">
          <StackPanel>
            <ContentControl Content="{Binding Hue}"><ContentControl.ContentTemplate><DataTemplate><c:ParamRow /></DataTemplate></ContentControl.ContentTemplate></ContentControl>
            <ContentControl Content="{Binding Saturation}"><ContentControl.ContentTemplate><DataTemplate><c:ParamRow /></DataTemplate></ContentControl.ContentTemplate></ContentControl>
            <ContentControl Content="{Binding Lightness}"><ContentControl.ContentTemplate><DataTemplate><c:ParamRow /></DataTemplate></ContentControl.ContentTemplate></ContentControl>
          </StackPanel>
        </DataTemplate>
      </ContentControl.ContentTemplate>
    </ContentControl>
  </StackPanel>
</UserControl>
```

`FilmLookControl.axaml.cs`：

```csharp
using Avalonia.Controls;
using Avalonia.Data.Converters;
using FilmBatch.App.ViewModels;

namespace FilmBatch.App.Controls;

/// <summary>底片風格選擇控制（僅 XAML，邏輯在 <see cref="FilmLookViewModel"/>）。</summary>
public partial class FilmLookControl : UserControl
{
    public static readonly IValueConverter GroupLabelConverter =
        new FuncValueConverter<string?, string>(group => group is null ? "" : FilmLookViewModel.GroupLabel(group));

    public FilmLookControl() => InitializeComponent();
}
```

在 `Themes/Controls.axaml` 加 `film-tile` / `film-thumb` 樣式：`Button.film-tile` 透明背景、`:pointerover` 用既有 hover 筆刷；`Button.film-tile.selected` 2px 強調色邊框（沿用 `edited-badge` 用的強調色 resource）；`Border.film-thumb` 背景 `FB.ThumbnailBackBrush`、`CornerRadius="4"`、`ClipToBounds="True"`。顏色一律用 `Themes/Colors.axaml` 既有 DynamicResource（亮／暗主題都要正常）。

- [ ] **Step 8: 修改 `InspectorView.axaml`**（77–85 行）

```xml
                      <!-- film 模組頂部的底片風格由 ModuleSectionViewModel.FilmLook 提供；其他模組為 null 不顯示 -->
                      <ContentControl Content="{Binding FilmLook}">
                        <ContentControl.DataTemplates>
                          <DataTemplate x:DataType="vm:FilmLookViewModel">
                            <c:FilmLookControl />
                          </DataTemplate>
                        </ContentControl.DataTemplates>
                      </ContentControl>
```

- [ ] **Step 9: 本地化**

兩份 resx 刪除所有 `Film.*`、`Param.film.*`、`Choice.film.*` 舊 key（`git grep -n 'name="Film\.\|name="Param.film\.\|name="Choice.film\.' src/FilmBatch.App/Localization`），加入：

| key | 中文 | English |
|---|---|---|
| `Module.film` | 底片風格 | Film Look |
| `Param.film.look` | 底片 | Film |
| `Param.film.strength` | 效果比例 | Effect Mix |
| `Param.film.warmth` | 暖色 | Warmth |
| `Param.film.fade` | 褪色 | Fade |
| `Param.film.hue_{band}_h`／`_s`／`_l`（24 個） | 「紅色相」「紅飽和」「紅明度」… | "Red Hue" "Red Saturation" "Red Luminance" … |
| `Film.Look` | 底片 | Film |
| `Film.Group.kodak`／`fuji`／`agfa-lomo`／`polaroid`／`bw`／`creative` | 柯達／富士／愛克發與 Lomo／拍立得／黑白／創意 | Kodak / Fuji / Agfa & Lomo / Polaroid / Black & White / Creative |
| `Film.HueCorrection` | 色相修正 | Hue Correction |
| `Film.HueCustom` | 自訂 | CUSTOM |
| `Film.HueBands` | 自訂色相 | Custom Hue |
| `Film.Band.red`…`magenta` | 紅、橙、黃、綠、青、藍、紫、洋紅 | Red, Orange, Yellow, Green, Aqua, Blue, Purple, Magenta |
| `Film.Hue.none`…`muted`（7 個） | 無、暖膚色、清澈藍天、青翠綠意、金黃秋色、冷暖對比、低彩度 | None, Warm Skin, Clear Sky, Lush Green, Golden, Teal & Orange, Muted |
| `Film.LookApplied` | 已套用底片「{0}」 | Applied film "{0}" |
| `Film.HueApplied` | 已套用色相修正「{0}」 | Applied hue correction "{0}" |

> 色相帶參數 label 也可以讓 `ParamRow` 在色相帶編輯器內顯示「色相／飽和／明度」：若 `ParamViewModel.Label` 來自 `Loc.ParamLabel`，就在 resx 放完整名稱（「紅色相」），編輯器內的顯示接受完整名稱即可，不另做簡稱。

- [ ] **Step 10: 刪除舊 UI 與測試**

```bash
git rm src/FilmBatch.App/ViewModels/FilmRecipeViewModel.cs src/FilmBatch.App/Controls/FilmRecipeControl.axaml src/FilmBatch.App/Controls/FilmRecipeControl.axaml.cs tests/FilmBatch.App.Tests/FilmRecipeViewModelTests.cs
```

- [ ] **Step 11: 測試**

Run: `dotnet test tests/FilmBatch.App.Tests`
Expected: 全部 PASS（含 `FilmLookViewModelTests`、`LocalizationTests`、`ThemeSnapshotTests`）。若 `ThemeSnapshotTests` 因 film 區塊外觀改變而失敗，依該測試的既有更新流程重建快照，並在 commit 訊息說明。

- [ ] **Step 12: 實機確認**（用 `run` skill 啟動 App）
  - 開一個有 JPG 與 RAW 的資料夾，展開「底片風格」：三欄縮圖以目前照片顯示、切換分組會重算。
  - 點 Velvia 50：主預覽變色、縮圖加框；Ctrl+Z 一次即復原。
  - 點「清澈藍天」：天空變藍、「自訂」標記消失；拖「紅色相」滑桿：出現「自訂」標記，色相修正縮圖全部取消選取。
  - 開舊專案（含舊 `film.style` 等欄位）：能正常開啟，不報錯。
  - 切換亮／暗主題、英文介面各看一次。

- [ ] **Step 13: Commit**

```bash
git add -A src/FilmBatch.App tests/FilmBatch.App.Tests
git commit -m "Add film color simulation thumbnail grid and hue band editor"
```

---

### Task 8: 文件與授權聲明

**Files:**
- Create: `docs/film-looks.md`
- Modify: `THIRD-PARTY-NOTICES.txt`、`docs/architecture.md`（4.5 節、名詞速查、處理順序表、修改食譜、測試對照、資料檔位置）、`README.md`（若提到 Recipe Lab）

- [ ] **Step 1: 撰寫 `docs/film-looks.md`**，章節：
  1. 概念：對應參數（底片 = LUT、效果比例 = Strength、暖色 = Warmth、褪色 = Fade、色相修正）。
  2. 來源與授權：HaldCLUT.zip 版本、大小、CC BY-SA 4.0、作者、商標聲明；`src/FilmBatch.Core/Film/Looks/LICENSE.txt`。
  3. 演算法：完整處理順序（與 Global Constraints 的常數相同）、`.fblut` 格式、紋理排列、四面體內插。
  4. 色相帶：八個中心角、權重（相鄰中心間線性、總和為 1）、色度閘門、內建預設表。
  5. 新增或更換底片：改 `looks.json` → 跑 `tools/film-luts` → 跑 `FilmLookTests` → 更新本文件的底片表。
  6. RAW：目前 RAW 經 LibRaw 轉 sRGB 後同樣套 LUT；LUT 輸入為 sRGB 顯示編碼，線性亮部 >1 會在 LUT 前截斷（效果比例混合時保留原值）。
  7. 舊專案相容：`RetiredParams` 清單；舊 `film.enabled = true` 的照片會改套用預設底片 Kodak Portra 400。

- [ ] **Step 2: 更新 `THIRD-PARTY-NOTICES.txt`**：移除 Recipe Lab（recipe-lab-sony-pmca）段落（資料已不再散布；先 `git grep -n -i "recipe" THIRD-PARTY-NOTICES.txt` 確認段落範圍），新增 RawTherapee Film Simulation Collection 段落（名稱、URL、版本、CC BY-SA 4.0 全名與連結、作者、修改說明、商標聲明）。

- [ ] **Step 3: 更新 `docs/architecture.md`**：4.5 節改為「Core．底片風格（Film）」，表格列 `FilmLut`、`FilmLutFile`、`FilmLookLibrary`、`HueBands`／`HuePresets`、`Looks/`、`RetiredParams`；4.6 節的型別表把 `FilmProcessing`、`FilmParameters` 換成 `FilmLookProcessing`、`FilmLookUniforms`、`Oklab`；處理順序表 ③ 改為「底片風格 → 古典版材」；名詞速查的「配方」改為「底片（look）」；修改食譜加入「新增一卷底片」；資料檔位置刪除 recipes 資料夾；測試對照加入新測試檔。

- [ ] **Step 4: 全面檢查**

Run: `git grep -n -i "recipe lab\|recipelab\|creative style\|創意風格\|配方" -- docs README.md src`
Expected: 只剩與本次變更無關、或刻意保留的歷史說明（例如 `docs/plan.md` 的歷史章節；若保留，在該處加一行「已由 film-looks.md 取代」）。

- [ ] **Step 5: 完整驗證**

Run: `dotnet build FilmBatch.sln && dotnet test`
Expected: 建置成功、全部測試 PASS（GPU 測試在無 GPU 環境為 Skip）。

- [ ] **Step 6: Commit**

```bash
git add docs THIRD-PARTY-NOTICES.txt README.md
git commit -m "Document film color simulation and RawTherapee LUT attribution"
```

---

## 自我檢查紀錄

- **規格覆蓋**：風格化（Task 1、4、5）；CC BY-SA 內附與聲明（Task 2、8）；色相修正＋自訂（Task 3 預設、Task 4/5 算圖、Task 7 編輯器）；縮圖格、以目前照片算（Task 7）；取代舊模組（Task 3 schema、Task 6 刪除、Task 7 UI）；JPG 與 RAW 都可用（同一 sRGB 管線，Task 8 文件說明限制）。
- **型別一致**：`FilmLut.Sample/Resample/ToTextureRgba/FromHald`、`FilmLookLibrary.TryLut/DefaultKey/GroupsInOrder`、`HueBands.FlatKeys/CentersDegrees/ParamKey`、`HuePresets.All/Find`、`FilmLookUniforms.From/Band`、`FilmLookProcessing.Apply/Pixel/BandWeight`、`FilmLookViewModel.Attach/SetExpanded/OnSettingsChanged/RefreshSelection/RequestThumbnails`、`FilmLookThumbnails.Request/Invalidate/OnThumbnailReady/OnThumbnailFailed` 在各 Task 名稱一致。
- **已知需實作者對照現有程式的點**（皆已在步驟中標出檢查指令）：`TestSupport` 測試工具 API、`Loc` 公開方法、`ParamViewModel` 設值 API、`ThemeSnapshotTests` 快照更新流程。
