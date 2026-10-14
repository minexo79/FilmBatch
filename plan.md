# FilmBatch 計畫書：批次底片效果處理工具

> 版本：v0.7（規劃稿）
> 日期：2026-10-14
> 定位：獨立的批次類比效果工具。接收任何 RAW 編輯器（例如 RapidRAW）匯出的照片，為每張照片套上底片色彩與類比效果後批次輸出
> 技術：C# / **.NET 10（LTS）** ＋ **Avalonia UI 12**（Fluent 深色主題、MVVM）＋ **WebGPU（wgpu-native）與 WGSL compute shader 影像引擎**，以 `dotnet publish` 發佈為 Windows / macOS / Linux 原生執行檔

### 版本變更

| 版本 | 變更 |
|---|---|
| v0.1 | 初版：三層設定模型、預設庫、複製貼上、底片模型、批次匯出 |
| v0.2 | 新增光學、合成、表面類效果模組；改為純 Python 跨平台 |
| v0.3 | 打包改用 Nuitka；UI 採 PySide6 Fusion + 深色調色盤；所有流程與架構圖改為 Mermaid；UI 規範獨立為第 2 章，並列為開發第一階段（M0） |
| v0.4 | 移除所有與第三方濾鏡軟體的對照與依賴，成為獨立產品：效果模組改用自有命名與分類；模組面板順序改為依處理階段排列；第 6 章改為「效果模組總覽」；LUT 匯入改為通用流程 |
| v0.5 | UI 主題改用 PyQtDarkTheme（`pyqtdarktheme-fork`） |
| v0.6 | 技術棧改為 C# / .NET 10 + Avalonia UI；影像運算採 OpenCvSharp4 + SIMD；影像 IO 採 Magick.NET。Python 版計畫保存於 `plan_python_v0.5.md` |
| v0.7 | **影像引擎改為 GPU 架構（參考 RapidRAW 的 wgpu + WGSL 做法）**：移除 OpenCV，全部效果以 WGSL compute shader 實作，透過 C# 的 WebGPU 綁定呼叫 wgpu-native（Windows：DX12 / Vulkan、macOS：Metal、Linux：Vulkan）；預覽與匯出共用同一套 shader；CPU 端只負責檔案讀寫（Magick.NET）與排程。UI 升級為 **Avalonia 12** 與對應的 Dock.Avalonia 12。改寫 5、7、9–16 章 |
| v0.8 | M0 結果：C# WebGPU 綁定定案為 **WebGPUSharp（Dawn）**，取代 9.1 的 Silk.NET 首選（理由見 `docs/adr/0001-webgpu-binding.md`）；標題列與選單列合一（2.2.1）；M0 進度與發現見 `docs/M0-status.md` |

### 目錄

1. 背景與目標
2. **UI 規範（第一優先）**
3. 核心概念
4. 資料模型
5. 影像處理管線
6. 效果模組總覽
7. 底片色彩來源
8. 材質庫
9. 跨平台與 .NET 發佈
10. 批次匯出
11. 系統架構
12. 效能策略
13. 開發里程碑
14. 測試策略
15. 風險與對策
16. 驗收標準

---

## 1. 背景與目標

### 1.1 要解決的問題

```mermaid
flowchart LR
    A["RAW 檔"] --> B["RAW 編輯器<br/>解碼、基本調色"]
    B --> C["單張濾鏡工具<br/>開啟 → 選效果 → 微調 → 存檔"]
    C -->|"每一張都要重複"| C
    C --> D["輸出 JPEG"]
```

為照片加上底片風格時，常見的做法是逐張開啟濾鏡工具處理。這種做法有幾個痛點：

- 每張照片都要各自開啟、選效果、微調、存檔，非常耗時。
- 一般的批次功能只能「整批套同一個效果」，沒辦法每張用不同底片、不同效果。
- 批次存檔缺乏匯出控制（格式、品質、目的地），也無法事後回頭修改單張設定。

FilmBatch 的目標是把「每張不同、但可以批次管理」這件事做好。

### 1.2 目標流程

```mermaid
flowchart LR
    A["RAW 檔"] --> B["任一 RAW 編輯器<br/>例如 RapidRAW"]
    B -->|"匯出 16-bit TIFF / JPEG"| C
    subgraph C["FilmBatch"]
        direction TB
        C1["指定預設<br/>底片 + 效果組合"] --> C2["單張微調<br/>只記錄差異"]
        C2 --> C3["複製設定到其他照片"]
        C3 --> C4["批次匯出"]
    end
    C --> D["最終 JPEG / TIFF"]
```

### 1.3 核心需求

| # | 需求 | 優先度 |
|---|---|---|
| R1 | 每張照片可以有各自的底片色彩與效果 | 必要 |
| R2 | 可儲存**多個**預設（配置檔），組成預設庫 | 必要 |
| R3 | 每張照片可指定要套用哪個預設 | 必要 |
| R4 | 在預設之上可針對單張微調，且只記錄差異 | 必要 |
| R5 | 可將某張照片的設定複製到其他照片（可選擇複製哪些部分） | 必要 |
| R6 | 即時預覽（拖曳滑桿時即時更新），可對比原圖 | 必要 |
| R7 | 批次匯出：可選格式、品質、尺寸、目的地 | 必要 |
| R8 | 非破壞性編輯，原始檔永不修改 | 必要 |
| R9 | 提供完整的類比效果模組：底片色彩、顆粒、光暈、漏光、光學模糊、鏡頭變形、雙重曝光、多格分鏡、灰塵刮痕、古典版材等（見第 6 章） | 必要 |
| R10 | UI 使用 Avalonia Fluent 深色主題，配色依 2.2 規格 | 必要 |
| R11 | 以 C# / .NET 10 + Avalonia 開發，影像運算在 GPU 上以 WGSL compute shader 執行，發佈為三平台執行檔 | 必要 |
| R12 | 可匯入 `.cube` 與 HaldCLUT 格式的 LUT 當作底片 | 重要 |
| R13 | 多選後可同時調整同一參數 | 重要 |

### 1.4 不在範圍內（第一版不做）

- RAW 解碼、鏡頭校正、白平衡挑色器（交給上游的 RAW 編輯器）
- 相框與邊框
- 局部調整、筆刷遮罩（第一版以各模組內建的對焦區 / 保護區遮罩處理常見需求）
- 照片管理（評分、標籤、關鍵字）
- 沒有可用 GPU 的環境下的高效能運作（以軟體 GPU 介面卡維持可用，但速度不列入效能目標，見 12）

---

## 2. UI 規範（第一優先）

> UI 是使用者每天面對的部分，也決定了資料模型與互動流程該長什麼樣子。
> 因此在開發順序上，**先完成 UI 規範與可操作的原型（M0）**，凍結後再實作影像引擎。
> 後續各章的功能都必須能對應到本章定義的介面元件。
> 既有的 Python / PySide6 M0 原型可作為互動行為的參考，Avalonia 版本以本章為準重新實作。

### 2.1 設計原則

| 原則 | 說明 |
|---|---|
| **中性灰** | 所有介面灰階的 R = G = B，不帶色偏，避免影響使用者對照片色彩的判斷 |
| **照片優先** | 預覽區佔最大面積；面板可收合、可停靠、可隱藏（`Tab` 切換全部面板） |
| **單一強調色** | 只用一個強調色（琥珀橘，帶有底片與暗房的溫暖意象，在中性灰上辨識度高）標示「選取」與「被覆寫」，其餘元件保持低調 |
| **所見即所算** | 模組面板由上到下依處理階段排列（色彩 → 光學 → 底片 → 表面），面板順序就是運算順序，讓使用者直覺理解效果如何疊加 |
| **不打斷操作** | 算圖、縮圖產生、匯出都在背景執行，UI 執行緒永不阻塞；錯誤以狀態列或非阻斷提示呈現 |
| **鍵盤友善** | 常用操作都有快捷鍵；macOS 上 `Ctrl` 自動對應 `⌘` |
| **MVVM** | 所有畫面以 ViewModel 驅動，View 只負責呈現與輸入；ViewModel 不引用任何 Avalonia 控制項，可單獨測試 |

### 2.2 主題：Avalonia Fluent（深色）＋ 自訂資源字典

#### 2.2.1 實作方式

- 使用 Avalonia 內建的 **`FluentTheme`**，`Application.RequestedThemeVariant = ThemeVariant.Dark`，**不跟隨作業系統**的淺色 / 深色設定，三個平台外觀一致。
- **配色依 2.2.2 規格**：在 `Themes/Colors.axaml` 的 `ThemeDictionaries` → `Dark` 中覆寫 Fluent 的系統資源（`SystemAccentColor` 及其 `Light1–3` / `Dark1–3` 變體、`SystemRegionColor` 等），並定義本專案的色彩 token（`FB.*Brush`）。
  - Fluent 深色版的灰階本身已接近中性灰；M0 以元件展示視窗逐一檢查，凡帶色偏的 Fluent 資源一律覆寫為 2.2.2 的色碼。
  - Avalonia 12 的 Fluent 資源鍵若與 11 有差異，以 12 的主題原始碼為準，於 M0 定稿對照表。
- 自訂元件的樣式集中在 `Themes/Controls.axaml`（Avalonia `Styles`，以 class 選擇器套用，例如 `Border.module-header`、`TextBlock.overridden`），不散落在各 View。
- 所有 XAML 使用**編譯繫結**，繫結錯誤在建置時即可發現，也為日後的 Native AOT / Trimming 鋪路。
- 已知注意事項：
  - Fluent 的控制項尺寸偏大（觸控友善），需在 `Controls.axaml` 統一調整 `Slider`、`NumericUpDown`、`ComboBox` 的高度與內距，以符合 28px 參數列高度。
  - 「模組停用時內容淡化但仍可調整」不能用 `IsEnabled=false`（會無法操作），改以內容區 `Opacity` + 停用色樣式 class 實作。
  - `NumericUpDown` 設 `ShowButtonSpinner="False"` 作為參數數值框。
- 標題列：**標題列與選單列合一**（M0 定案）。主視窗以 `ExtendClientAreaToDecorationsHint` 把內容延伸進標題列，同一列依序為「圖示｜選單｜視窗標題（置中）｜系統按鈕」；空白處以 `WindowDecorationProperties.ElementRole="TitleBar"` 標為原生拖曳區（拖曳移動、雙擊最大化、Windows 貼齊版面皆由系統處理），系統按鈕仍由平台繪製，只隱藏平台繪製的標題文字。macOS 的紅綠燈在左側，圖示與選單需右移讓出位置（跨平台建置時驗證）。對話框維持原生標題列。

```xml
<!-- FilmBatch.App/Themes/Colors.axaml（節錄） -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Dark">
      <!-- 覆寫 Fluent 系統資源 -->
      <Color x:Key="SystemAccentColor">#D98E2B</Color>
      <Color x:Key="SystemAccentColorLight1">#E6A44A</Color>
      <Color x:Key="SystemRegionColor">#2D2D2D</Color>
      <!-- 本專案色彩 token -->
      <SolidColorBrush x:Key="FB.WindowBrush"     Color="#2D2D2D"/>
      <SolidColorBrush x:Key="FB.BaseBrush"       Color="#1E1E1E"/>
      <SolidColorBrush x:Key="FB.ButtonBrush"     Color="#353535"/>
      <SolidColorBrush x:Key="FB.TextBrush"       Color="#DCDCDC"/>
      <SolidColorBrush x:Key="FB.AccentBrush"     Color="#D98E2B"/>
      <SolidColorBrush x:Key="FB.OverrideBrush"   Color="#D98E2B"/>
      <SolidColorBrush x:Key="FB.PresetUpdatedBrush" Color="#5AA9E6"/>
      <!-- … 其餘見 Colors.axaml -->
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

#### 2.2.2 配色規格

| 色彩 token（資源鍵） | 色碼 | 用途 |
|---|---|---|
| `FB.WindowBrush` | `#2D2D2D` | 視窗與面板背景 |
| `FB.TextBrush` | `#DCDCDC` | 一般文字、按鈕文字、輸入框文字 |
| `FB.BaseBrush` | `#1E1E1E` | 輸入框、清單、縮圖列背景 |
| `FB.AlternateBaseBrush` | `#262626` | 清單交錯列、狀態列 |
| `FB.ButtonBrush` | `#353535` | 按鈕、模組標題列 |
| `FB.PlaceholderBrush` | `#7A7A7A` | 提示文字 |
| `FB.ToolTipBackgroundBrush` / `FB.ToolTipTextBrush` | `#3A3A3A` / `#EAEAEA` | 工具提示 |
| `FB.BorderLightBrush` / `FB.BorderBrush` | `#4A4A4A` / `#3E3E3E` | 邊框亮部、一般邊框 |
| `FB.ShadowBrush` | `#1A1A1A` / `#0F0F0F` | 分隔線、陰影 |
| `FB.AccentBrush`（`SystemAccentColor`） | `#D98E2B` | **強調色**：選取、目前項目、滑桿已填充段 |
| `FB.AccentTextBrush` | `#111111` | 強調色上的文字 |
| `FB.WarningTextBrush` | `#FF6B6B` | 警示文字 |
| `FB.LinkBrush` | `#E6A44A` | 連結 |
| `FB.DisabledTextBrush` | `#6E6E6E` | 停用狀態文字 |
| `FB.DisabledAccentBrush` | `#505050` | 停用狀態選取 |

語意色（只用於小面積標記，不作為大面積背景）：

| 用途 | 色碼 |
|---|---|
| 參數被覆寫的標記點 ● | `#D98E2B`（同強調色） |
| 預設已更新 ⟳ | `#5AA9E6` |
| 錯誤 | `#E05555` |
| 成功 | `#6BBF59` |

預覽畫布背景**不屬於主題資源**，由偏好設定切換：深灰 `#1A1A1A`（預設）、中灰 `#808080`（判斷亮度用）、純黑 `#000000`。

#### 2.2.3 字型與尺寸

| 項目 | 規格 |
|---|---|
| 字型 | 以 `FontManagerOptions` 設定預設字型與後備字型：Windows「Microsoft JhengHei UI」、macOS「PingFang TC」、Linux「Noto Sans CJK TC」 |
| 基本字級 | 13px（約 10pt；數值欄位使用等寬數字 `FontFeatures="tnum"`） |
| 間距 | 以 4px 為基本單位（4 / 8 / 12 / 16） |
| 參數列高度 | 28px |
| 視窗最小尺寸 | 1280 × 800 |
| HiDPI | Avalonia 以 DIP 排版；預覽影像依 `TopLevel.RenderScaling` 決定 GPU 輸出的像素尺寸，避免模糊 |

### 2.3 主視窗版面

```mermaid
flowchart TB
    subgraph MW["主視窗 MainWindow（最小 1280×800）"]
        direction TB
        TB["標題列＋選單列（合一）<br/>圖示｜檔案 編輯 檢視 預設｜視窗標題｜系統按鈕<br/>工具按鈕列<br/>開啟資料夾｜復原｜重做｜複製｜貼上｜選擇性貼上｜原圖對比｜縮放｜匯出"]
        subgraph MID["DockControl（Dock.Avalonia）"]
            direction LR
            L["左 ToolDock<br/>預設庫<br/>寬 220px，可收合"]
            C["中央 DocumentDock<br/>預覽畫布 + 控制點<br/>佔剩餘空間"]
            R["右 ToolDock<br/>照片資訊 + 模組面板<br/>寬 ≥ 320px"]
        end
        F["下 ToolDock<br/>縮圖列 Filmstrip，高 120px"]
        S["狀態列<br/>算圖狀態｜GPU 介面卡｜縮放比例｜選取數量｜背景工作進度"]
        TB ~~~ MID ~~~ F ~~~ S
    end
```

- 停靠系統使用 **Dock.Avalonia**（MIT，版本需與 Avalonia 12 對應）：三個工具面板都可拖曳到其他邊、浮動、釘選為自動隱藏或關閉。
- 版面以 Dock 的序列化功能存成 `layout.json`，視窗位置與大小存入 `settings.json`（見 9.2），下次開啟還原。
- 「檢視」選單提供「重設版面」（載入內建預設版面）。
- `Tab` 隱藏或顯示所有面板，`F` 切換全螢幕預覽（`WindowState.FullScreen`）。
- 狀態列顯示目前使用的 GPU 介面卡與後端（例如「Apple M2 · Metal」），使用軟體介面卡時以警示色提示。

### 2.4 區域規格

#### 2.4.1 工具列

| 按鈕 | 快捷鍵 | 說明 |
|---|---|---|
| 開啟資料夾 | `Ctrl/⌘+O` | 以 `StorageProvider.OpenFolderPickerAsync` 選擇照片資料夾，自動載入或建立專案 |
| 復原 / 重做 | `Ctrl/⌘+Z` / `Ctrl/⌘+Shift+Z` | 專案層級的復原堆疊 |
| 複製設定 | `Ctrl/⌘+C` | 複製目前照片的設定 |
| 貼上 | `Ctrl/⌘+V` | 完整貼上到選取照片 |
| 選擇性貼上 | `Ctrl/⌘+Shift+V` | 開啟選擇性貼上對話框 |
| 原圖對比 | 按住 `空白鍵`；`Y` 切換左右分割 | 檢視原圖 |
| 縮放 | `Ctrl/⌘+0` 符合視窗、`Ctrl/⌘+1` 100% | |
| 匯出 | `Ctrl/⌘+E` | 開啟匯出對話框 |

- 快捷鍵以 `KeyBinding` + `KeyGesture` 定義，修飾鍵使用平台熱鍵設定（`PlatformHotkeyConfiguration`），在 macOS 自動對應 `⌘`。macOS 另提供原生選單列（`NativeMenu`）。
- 圖示使用單色線條圖示（SVG 轉 `StreamGeometry` / `PathIcon`），顏色跟隨 `FB.TextBrush`，停用時跟隨 `FB.DisabledTextBrush`。

#### 2.4.2 預設庫（左面板）

- 以 `TreeView` 呈現：分類 → 預設。每個預設顯示**色彩標籤**、名稱，以及滑鼠停留時的效果縮圖（`ToolTip` 內放縮圖）。
- 操作：
  - 拖曳預設到縮圖列或預覽區，指定給該照片（或所有選取照片）。拖放使用 Avalonia `DragDrop`，資料格式為自訂的 `filmbatch/preset-id`。
  - 雙擊：指定給目前選取的照片。
  - 右鍵選單（`ContextMenu`）：套用到選取照片、重新命名、更改色彩標籤、複製、刪除、在檔案總管中顯示（`ILauncher` 開啟所在資料夾）。
- 底部按鈕：「＋ 以目前照片建立預設」、「管理…」。
- 前 9 個「釘選」預設可用數字鍵 `1`–`9` 快速套用。

#### 2.4.3 預覽畫布（中央）

- 自訂控制項 `PreviewCanvas : Control`，覆寫 `Render(DrawingContext)` 繪製預覽影像，不使用巢狀的 `Image` / `Canvas` 版面，以降低每次更新的負擔。
- **影像來源**：GPU 引擎在 GPU 上完成算圖並轉成 8-bit BGRA 後讀回，寫入 `WriteableBitmap`（第一版）。日後可改為 Avalonia 的 GPU 互通（`ICompositionGpuInterop`）直接共享紋理，省去讀回（見 12）。
- 縮放：符合視窗 / 100% / 滾輪縮放；放大時以滑鼠拖曳平移（`PointerPressed` / `PointerMoved` + `Pointer.Capture`）。100% 檢視時只算可見區域（見 12）。
- 原圖對比：按住空白鍵顯示原圖；`Y` 切換左右分割（分割線可拖曳）。
- 算圖中在右上角顯示細小的旋轉指示，**不遮擋畫面、不鎖住操作**；舊結果保留到新結果出來。
- 多選時顯示目前焦點照片，左上角疊字「已選取 N 張，調整將套用到全部」。

**畫布控制點**

| 模組 | 控制點 |
|---|---|
| 焦外成像 | 對焦中心（拖曳）、清晰區大小（外圈把手）、過渡區（虛線外圈） |
| 縮放和旋轉模糊 | 模糊中心、保護區半徑 |
| 動態模糊 | 保護區中心與半徑、方向指示線（拖曳端點改角度） |
| 鏡頭暈影 | 暈影中心、大小把手 |
| 漏光 | 進光方向（沿畫面邊緣拖曳） |
| 雙重曝光 | 第二張影像的位移、縮放、旋轉把手 |

控制點外觀：直徑 12px 白色圓圈加 1px 深色外框（任何畫面上都看得清楚），作用中的控制點以強調色填充。控制點在 `PreviewCanvas.Render` 中與影像一起繪製（不建立子控制項），命中測試自行計算。只顯示**目前展開模組**的控制點，避免畫面雜亂。拖曳時即時更新預覽（GPU 足以在拖曳中直接算 1600px 代理圖），放開後寫入參數，並記為一筆復原紀錄。

#### 2.4.4 縮圖列（下面板）

使用 `ListBox` + 水平 `VirtualizingStackPanel` + `DataTemplate`（`ThumbnailView`），支援數千張照片的虛擬化捲動；縮圖點陣圖由 `ThumbnailViewModel` 非同步載入，捲出畫面時可釋放。

| 位置 | 標記 | 意義 |
|---|---|---|
| 左下 | 色彩標籤 + 預設名稱縮寫 | 這張照片使用的預設 |
| 右上 | ● 強調色 | 有單張覆寫 |
| 右上 | ⟳ 藍色 | 所用預設已在預設庫中更新，可同步 |
| 外框 | 強調色 2px | 選取中 |
| 縮圖本身 | — | 套用效果後的結果（背景以 GPU 批次更新） |

- 自行處理 `Ctrl/⌘` 與 `Shift` 多選、`Ctrl/⌘+A` 全選，行為與檔案總管一致。
- 上方篩選列：依預設篩選、只顯示有覆寫的照片、只顯示未指定預設的照片（以篩選集合實作，避免整份清單重建）。

#### 2.4.5 照片資訊與模組面板（右面板）

```mermaid
flowchart TB
    RP["右 ToolDock"] --> INFO["照片資訊區（固定在頂部）"]
    INFO --> I1["檔名、尺寸"]
    INFO --> I2["指定預設：ComboBox"]
    INFO --> I3["已覆寫 N 項｜重設此張｜以此張建立預設｜更新回預設"]
    RP --> SC["模組捲動區 ScrollViewer"]
    SC --> GH["分組標題 × 5<br/>基本｜幾何｜光學｜底片｜表面"]
    GH --> MS["ModuleSection × 16<br/>（依下方順序排列）"]
    MS --> H["標題列<br/>▸ 折疊｜☑ 啟用｜名稱｜● 覆寫數｜⋯ 選單"]
    MS --> ROWS["ParamRow × N<br/>由 schema 經 DataTemplate 產生"]
```

模組面板依**處理階段**分組，由上到下的順序與第 5 章的運算順序一致：

| 分組 | 模組 |
|---|---|
| 基本 | 基本調整 |
| 幾何 | 鏡頭變形、多格分鏡 |
| 光學 | 雙重曝光、散景與選擇性對焦、放射與旋轉模糊、動態模糊、暗角、光暈、漏光 |
| 底片 | 底片色彩、古典版材、色調分離、清晰與銳化 |
| 表面 | 灰塵與刮痕、顆粒 |

- 面板內容由參數 schema 產生：每個 `ParamDef` 對應一個 ViewModel（`SliderParamVM`、`ChoiceParamVM`、`ToggleParamVM`…），由 `DataTemplates` 依型別選擇 View，新增參數不需要寫 XAML。
- 分組標題可整組折疊；未啟用的模組可選擇「隱藏未啟用模組」，讓面板只顯示正在使用的效果。
- 「基本調整」橫跨線性與顯示兩段運算（見 5.2），在面板上仍放在最上方，因為它是最常調整的模組。

### 2.5 自訂元件規格

| 元件 | Avalonia 實作 | 行為規格 |
|---|---|---|
| **ParamRow** | `UserControl`：覆寫點 ● ＋ 名稱 ＋ `ParamSlider` ＋ `NumericUpDown` | 被覆寫時名稱粗體、顯示 ●（`Classes.overridden` 繫結）；雙擊名稱重設回預設值；數值框可直接輸入；滑桿與數值框同步 |
| **滑桿滾輪行為** | `ParamSlider : Slider` | **只有在滑桿取得焦點時才處理 `PointerWheelChanged`**，否則不標記 `Handled`，讓事件冒泡給 `ScrollViewer`，避免捲動面板時誤改參數 |
| **滑桿拖曳** | `ParamSlider` | 拖曳中持續送出預覽請求；放開時合併為一筆復原紀錄；按住 `Shift` 微調（步進 ÷ 10） |
| **ChoiceRow** | 名稱 ＋ `ComboBox` | 用於底片種類、光圈形狀、版面配置等 |
| **ModuleSection** | `HeaderedContentControl`（自訂 `ControlTemplate`）：標題列 ＋ 內容區 | 點標題折疊或展開；啟用勾選框關閉時，內容區變為停用色但仍可預先調整；`⋯` 選單提供「重設此模組」「只複製此模組」 |
| **PresetItem** | `TreeViewItem` 的 `DataTemplate`：色彩標籤 ＋ 名稱 | 可拖曳；停留 500ms（`ToolTip.ShowDelay`）顯示效果縮圖 |
| **ThumbnailView** | `ListBoxItem` 的 `DataTemplate`：縮圖 ＋ 標記 | 見 2.4.4 |
| **ControlPoint** | `PreviewCanvas` 內繪製的圓形把手 | 見 2.4.3 |
| **ToastBar** | 狀態列上方的非阻斷提示（覆蓋層 `Panel`） | 3 秒後自動消失（`DispatcherTimer`），可附「復原」按鈕（例如「已貼上到 12 張照片｜復原」） |

### 2.6 互動狀態

```mermaid
stateDiagram-v2
    state "尚未開啟專案" as NoProject
    state "單選編輯" as Single
    state "多選編輯" as Multi
    state "拖曳控制點 / 滑桿" as Dragging
    state "原圖對比" as Before
    state "匯出中（背景）" as Exporting

    [*] --> NoProject
    NoProject --> Single : 開啟資料夾
    Single --> Multi : Ctrl/⌘ 或 Shift 點選
    Multi --> Single : 單獨點選一張
    Single --> Dragging : 按下滑桿或控制點
    Multi --> Dragging : 按下滑桿
    Dragging --> Single : 放開（寫入參數、一筆復原紀錄）
    Single --> Before : 按住空白鍵
    Before --> Single : 放開空白鍵
    Single --> Exporting : 開始匯出
    Multi --> Exporting : 開始匯出
    Exporting --> Single : 完成或取消
```

匯出在背景進行，**匯出期間仍可繼續編輯其他照片**。匯出開始時，會先為每張照片建立參數快照（不可變的 `record`），所以匯出期間的修改不會影響已排入佇列的照片。匯出與預覽共用同一個 GPU，排程器讓預覽請求優先（見 11.3）。

### 2.7 主要互動流程

#### 2.7.1 指定預設

```mermaid
flowchart TD
    A["從預設庫拖曳預設"] --> B{"放開位置"}
    B -->|"某張縮圖"| C{"該縮圖是否在選取範圍內？"}
    C -->|"是"| D["套用到所有選取照片"]
    C -->|"否"| E["只套用到該張"]
    B -->|"預覽區"| D2["套用到目前照片<br/>（多選時為所有選取照片）"]
    D --> F{"目標照片有覆寫？"}
    E --> F
    D2 --> F
    F -->|"否"| G["直接指定"]
    F -->|"是"| H["詢問：保留覆寫 / 清除覆寫<br/>可勾選「記住我的選擇」"]
    H --> G
    G --> I["寫入復原紀錄 → 更新縮圖與預覽"]
```

#### 2.7.2 複製與貼上

```mermaid
flowchart TD
    A["Ctrl/⌘+C<br/>複製目前照片的設定"] --> B{"貼上方式"}
    B -->|"Ctrl/⌘+V"| C["完整貼上<br/>presetId + 所有覆寫"]
    B -->|"右鍵選單"| D["只貼預設<br/>只改 presetId，保留目標原本的覆寫"]
    B -->|"Ctrl/⌘+Shift+V"| E["選擇性貼上對話框<br/>以模組為單位勾選"]
    E --> F{"勾選「包含隨機種子」？"}
    F -->|"否（預設）"| G["種子沿用目標照片自己的"]
    F -->|"是"| G2["種子也一併複製"]
    C --> H["清理：與預設相同的覆寫自動移除"]
    D --> H
    G --> H
    G2 --> H
    H --> I["寫入一筆復原紀錄 → 背景重算縮圖"]
    I --> J["ToastBar：已貼上到 N 張照片｜復原"]
```

設定剪貼簿為程式內部狀態；同時以 JSON 寫入系統剪貼簿，方便在兩個 FilmBatch 視窗之間貼上。

### 2.8 對話框

所有對話框皆為 `Window` + `ShowDialog<TResult>(owner)`，以 ViewModel 回傳結果；確認類詢問以自訂的小型對話框實作。

| 對話框 | 內容 | 規格 |
|---|---|---|
| **選擇性貼上** | 16 個模組的勾選清單（分組：色彩類 / 光學類 / 表面類）、「包含隨機種子」、「貼上時鏡像位置」 | 提供「全選」「全不選」「只選色彩類」快捷按鈕；記住上次的勾選 |
| **匯出** | 範圍、格式、品質、尺寸、目的地、檔名範本 | 下方即時顯示「將匯出 N 張到 …」；可儲存為匯出預設 |
| **預設管理** | 預設清單、分類、色彩標籤、匯入 / 匯出 YAML | 刪除預設時顯示「有 N 張照片使用中，將改用專案內快照」 |
| **偏好設定** | 預覽背景色、預覽代理圖尺寸、GPU 介面卡選擇（多顯卡時）與後端偏好、GPU 記憶體上限、材質庫與 LUT 資料夾、語言 | 修改後立即生效（切換介面卡需重新建立 GPU 裝置） |
| **同步預設** | 列出所用預設已更新的照片，可逐張或全部同步 | 見 4.4 |

### 2.9 回饋與錯誤處理

- 背景工作（縮圖、預覽、匯出）的進度統一顯示在狀態列右側，點擊可展開工作清單（`Flyout`）。
- 讀檔失敗的照片在縮圖列顯示錯誤圖示，滑鼠停留可看原因，不影響其他照片。
- **GPU 裝置遺失**（驅動程式重置、外接顯示卡拔除）時，引擎自動重建裝置並重新上傳目前照片，狀態列提示「GPU 已重新初始化」，不中斷操作；匯出佇列中的工作自動重試。
- 啟動時找不到硬體 GPU 介面卡，改用軟體介面卡並以 ToastBar 說明效能會受影響。
- 只有「會影響多張照片且無法復原」的操作才跳出確認對話框，例如刪除預設、把調整更新回預設。其餘操作一律可復原，不額外確認。
- 未處理的例外由 `Dispatcher.UIThread.UnhandledException` 與 `TaskScheduler.UnobservedTaskException` 統一攔截，寫入 log 並以 ToastBar 提示，不直接讓程式結束。

### 2.10 多國語系

- 所有介面字串放在 `.resx` 資源檔（`Strings.resx`、`Strings.en.resx`），以強型別資源類別在 ViewModel 中使用，XAML 中以自訂 `{l:Tr Key}` 標記延伸存取，切換語言時即時更新。
- 預設語言為繁體中文，英文列為後續擴充。

### 2.11 M0 的 UI 交付物

| # | 交付物 | 說明 |
|---|---|---|
| 1 | `Themes/Colors.axaml` + `Themes/Controls.axaml` | Fluent 深色主題 + 自訂資源，依 2.2 的規格實作 |
| 2 | **元件展示視窗**（Widget Gallery） | 列出所有自訂元件的各種狀態：一般、滑鼠停留、按下、停用、被覆寫、多選，用來檢查主題一致性；以 `--gallery` 參數啟動 |
| 3 | **可操作原型** | 主視窗、停靠面板、縮圖列、預設庫、模組面板皆可操作；使用範例照片與「假算圖器」，用來驗證互動與流暢度 |
| 4 | 對話框原型 | 選擇性貼上、匯出、預設管理、偏好設定（尚無實際功能） |
| 5 | 資料流驗證 | 原型已串接第 4 章的三層設定模型與復原堆疊 |
| 6 | **GPU 技術驗證（spike）** | 「假算圖器」直接以 WGSL compute shader 實作（亮度、飽和、一個可分離高斯模糊），驗證：C# WebGPU 綁定的選型（見 9.1）、上傳 16-bit 影像 → 運算 → 讀回 → `WriteableBitmap` 的完整路徑與延遲、三平台後端（DX12 / Metal / Vulkan）與軟體介面卡 |
| 7 | **三平台發佈冒煙建置** | 原型以 `dotnet publish` 在三個平台各發佈一次並能啟動，確認 wgpu-native、SkiaSharp、Magick.NET 原生函式庫都能載入（見 9.4） |
| 8 | UI 規範定稿 | 依原型實際操作回饋修訂本章，凍結為 v1 UI 規範 |

---

## 3. 核心概念

### 3.1 三層設定模型

```mermaid
flowchart BT
    D["系統預設值 Defaults<br/>程式內建，所有參數的初始值"]
    P["指定的預設 Preset<br/>從預設庫選一個，例如「Portra 暖調 + 漏光」"]
    O["單張覆寫 Overrides<br/>只存和預設不一樣的欄位"]
    F["最終參數<br/>Defaults ⊕ Preset ⊕ Overrides"]
    D --> P --> O --> F
```

這樣設計的好處：

- **修改預設會自動同步**：把預設的顆粒調大，所有使用這個預設的照片都會跟著變，但每張手動調過的欄位不受影響。
- **單張覆寫一目了然**：UI 標示哪些參數被這張照片改過，並提供「重設回預設」。
- **檔案很小**：每張照片只存幾個差異欄位。

### 3.2 效果模組與開關

- 每個效果都是一個**模組**，有一個 `enabled` 開關。開關與其他參數一樣，可存進預設、可被單張覆寫、可被複製。
- 關閉的模組完全不參與運算（不派送對應的 shader）。
- 模組的**執行順序固定**，由處理管線決定（第 5 章），不開放拖曳排序。
  - 理由：順序會大幅影響物理正確性（例如顆粒一定要在最後），固定順序讓結果可預期，預設也更容易共用。

### 3.3 預設庫

- 預設是一份 YAML 檔，內容是一組完整參數，包含各模組的開關。
- 預設庫存在使用者設定資料夾，可跨專案重複使用（路徑見 9.2）。
- 每個預設有唯一 ID（`Guid`），改名不會影響已指定的照片。
- 可分類、可設定色彩標籤、可附帶效果縮圖。

### 3.4 照片設定的操作

| 操作 | 行為 |
|---|---|
| **指定預設** | 設定照片的 `presetId`；可選擇保留或清除既有覆寫 |
| **微調** | 修改參數；與預設相同的值會自動從覆寫中移除 |
| **重設單一參數** | 移除該欄位的覆寫 |
| **重設單一模組** | 移除該模組所有欄位的覆寫 |
| **重設此張** | 清除所有覆寫，回到純預設 |
| **以此張建立新預設** | 把目前最終參數存成新預設，並讓這張改指定為新預設（覆寫清空） |
| **把此張的調整更新回預設** | 將覆寫合併回預設（會影響所有使用該預設的照片，需確認） |

### 3.5 複製設定的規則

流程見 2.7.2。補充規則：

- 選擇性貼上時，被勾選的模組（含開關）會寫成目標照片的覆寫，再依「與預設相同則移除」的規則清理。
- **隨機種子預設不複製**：漏光、顆粒、灰塵與刮痕、古典版材、多格分鏡等隨機效果，每張照片依檔名產生自己的種子，否則貼上後每張的形狀會一模一樣。
- **位置類參數以正規化座標儲存**（0–1），所以在不同尺寸、不同長寬比的照片間貼上仍然合理；可選擇「貼上時鏡像位置」，方便橫直幅混用。
- **雙重曝光的來源**若是「本張自己」，貼上後使用目標照片自己；若是指定檔案，則沿用該檔案。

---

## 4. 資料模型

### 4.1 類別關係

```mermaid
classDiagram
    class ParamDef {
        +string Key
        +string Label
        +ParamKind Kind
        +ParamValue Default
        +double Min
        +double Max
        +double Step
        +bool IsSeed
        +bool IsPosition
    }
    class IEffectModule {
        <<interface>>
        +string Key
        +Stage Stage
        +IReadOnlyList~ParamDef~ Params
        +Encode(GpuPassContext ctx, ModuleParams p)
    }
    class Preset {
        +Guid Id
        +string Name
        +string Category
        +string ColorLabel
        +int PipelineVersion
        +Settings Settings
    }
    class PhotoEntry {
        +string FileName
        +Guid? PresetId
        +Dictionary~string,ParamValue~ Overrides
    }
    class Project {
        +string Folder
        +Guid DefaultPresetId
        +Dictionary~Guid,PresetSnapshot~ PresetSnapshots
        +Dictionary~string,PhotoEntry~ Photos
        +SettingsFor(string name) Settings
        +SeedFor(string name) uint
    }
    class PresetLibrary {
        +string Folder
        +IReadOnlyList~Preset~ Presets
        +Get(Guid id) Preset
        +Save(Preset preset)
    }
    IEffectModule "1" *-- "many" ParamDef
    Project "1" o-- "many" PhotoEntry
    Project "1" o-- "many" Preset : 快照
    PhotoEntry "many" --> "1" Preset : PresetId
    PresetLibrary "1" o-- "many" Preset
```

- `ParamValue` 為小型的不可變值型別（`readonly record struct`），可容納數值、布林、字串（選項 key、相對路徑）三種內容，避免以 `object` 裝箱。
- `Settings` 為扁平 key → `ParamValue` 的不可變字典（`ImmutableDictionary` 或 `FrozenDictionary`），可安全地在背景執行緒間傳遞。
- 模組的數值參數在算圖時打包成對應 WGSL `struct` 的 uniform 緩衝區（欄位佈局由 schema 產生，見 11.2）。

### 4.2 預設檔（YAML）

以 YamlDotNet 讀寫；檔案中的 key 使用 snake_case，C# 端以命名慣例轉換為 PascalCase 屬性。

```yaml
id: 3f2a9c1e-8b4d-4e2a-9f1c-0a7b6d5e4c3b
name: Portra 暖調 + 漏光
category: 人像
color_label: "#D98E2B"
pipeline_version: 1
modules:
  basic:      { enabled: true, exposure: 0.0, brightness: 5, contrast: 0, highlights: -10,
                shadows: 10, temperature: 0, tint: 0, saturation: 0, structure: 0 }
  lens_distortion: { enabled: false, amount: 0, chromatic: 0, edge_soften: 0, auto_crop: true }
  multi_lens: { enabled: false }
  double_exposure: { enabled: false }
  bokeh:      { enabled: false }
  zoom_rotate_blur: { enabled: false }
  motion_blur:{ enabled: false }
  vignette:   { enabled: true, amount: 15, size: 60, roundness: 0, feather: 60,
                center_x: 0.5, center_y: 0.5 }
  halation:   { enabled: true, amount: 20, threshold: 70, radius: 30, hue: 10 }
  light_leak: { enabled: true, mode: procedural, amount: 35, angle: 30, size: 40, hue: 25,
                irregularity: 50 }
  film:       { enabled: true, stock: portra400, strength: 85, contrast: -10, saturation: 0,
                warmth: 15, fade: 8 }
  photo_plate:{ enabled: false }
  tone:       { enabled: true, shadow_hue: 200, shadow_amount: 10, highlight_hue: 40,
                highlight_amount: 15, balance: 0 }
  detail:     { enabled: true, sharpen: 0, clarity: 0 }
  dirt_scratches: { enabled: false }
  grain:      { enabled: true, amount: 25, size: 30, roughness: 50, color: 30 }
```

### 4.3 專案檔（JSON）

存在 `<照片資料夾>/.filmbatch/project.json`，以 `System.Text.Json` 的原始碼產生器（`JsonSerializerContext`）讀寫，屬性命名使用 snake_case：

```json
{
  "pipeline_version": 1,
  "default_preset_id": "3f2a9c1e-...",
  "preset_snapshots": {
    "3f2a9c1e-...": { "name": "Portra 暖調 + 漏光", "updated_at": "2026-10-13T15:00:00", "settings": { } }
  },
  "photos": {
    "DSC_0012.tif": {
      "preset_id": "3f2a9c1e-...",
      "overrides": { "basic.exposure": 0.3, "light_leak.amount": 50, "bokeh.enabled": true }
    },
    "DSC_0013.tif": { "preset_id": "a81b...", "overrides": {} }
  }
}
```

- 寫檔採「寫入暫存檔 → `File.Move(overwrite: true)`」的原子替換，避免當機時專案檔損毀。

### 4.4 預設快照與同步

預設庫是全域的，日後修改或刪除預設時，舊專案的結果不應該在不知情的情況下改變。因此專案會保存所用預設的快照：

```mermaid
stateDiagram-v2
    state "已同步" as Synced
    state "預設庫已更新" as Updated
    state "沿用舊快照" as Kept
    state "預設已刪除（只剩快照）" as Orphan

    [*] --> Synced : 指定預設時建立快照
    Synced --> Updated : 預設庫中的預設被修改
    Updated --> Synced : 使用者選擇同步（更新快照）
    Updated --> Kept : 使用者選擇保留
    Kept --> Updated : 預設庫再次被修改
    Synced --> Orphan : 預設庫中的預設被刪除
    Orphan --> Synced : 另存為新預設
```

預設庫資料夾以 `FileSystemWatcher` 監看，外部修改 YAML 時自動重新載入並更新同步狀態。

### 4.5 設計決策

- **參數 key 一律扁平化**：格式為 `模組.參數`，例如 `bokeh.blur`。合併、比對、複製都以扁平 key 處理；YAML 只是為了好讀才寫成巢狀。
- **沒有指定預設的照片**：使用 `default_preset_id`，新匯入的照片也自動指定為它。
- **`pipeline_version`**：日後改進演算法時，舊專案仍以舊版演算法算圖，避免結果突然改變（概念同主流 RAW 編輯器的「處理版本」）。shader 依版本分資料夾保存（`Shaders/v1/`、`Shaders/v2/`），舊版 shader 不刪除。
- **檔案路徑**：專案內引用的材質或雙重曝光影像一律存相對路徑，並使用 `/` 分隔，確保專案在三個作業系統之間搬移仍可開啟。
- **核心資料不依賴 Avalonia 與 GPU**：`FilmBatch.Core` 只引用 BCL 與 YamlDotNet，可在沒有 GPU 的環境下完整測試。

---

## 5. 影像處理管線

### 5.1 輸入假設與 GPU 資料格式

- 輸入是 RAW 編輯器已完成基礎調色的 **sRGB** 影像，建議使用 16-bit TIFF，避免色階斷層。
- 這是「顯示參考」（display-referred）影像，不是場景線性資料。所以底片曲線必須保持 0→0、1→1，不能再套一次完整的 S 曲線，否則對比會被重複壓縮。
- **CPU 端**：Magick.NET 解碼、EXIF 轉正，轉成 16-bit RGBA 後上傳 GPU。CPU 不做任何效果運算。
- **GPU 端**：
  - 上傳紋理 `rgba16uint` 或 `rgba16float`，第一個 shader 轉成**線性光 `rgba32float`** 的工作紋理。
  - 管線在兩張工作紋理之間來回寫入（ping-pong），每個模組是一到數個 compute pass；需要多次模糊的模組另外配置暫存紋理，由紋理池重複使用。
  - 區段快取（見 12）以 `rgba16float` 保存以節省顯示記憶體。
  - 輸出 pass 轉回 sRGB：預覽輸出 `rgba8unorm`（BGRA 排列）讀回顯示；匯出輸出 16-bit 讀回再交給 Magick.NET 編碼。
- **尺寸上限**：一般 GPU 支援的 2D 紋理邊長上限為 8192–16384。啟動時查詢介面卡的 `maxTextureDimension2D` 與 `maxBufferSize`；超過上限或顯示記憶體不足的匯出改為**分塊算圖**（見 12）。

### 5.2 處理順序

光學與曝光類效果在**線性光**中進行，模糊時高光才會形成自然的光斑，疊加時亮度才會正確相加。

```mermaid
flowchart TB
    IN["輸入 sRGB 影像<br/>（CPU 解碼後上傳）"] --> D0["sRGB → 線性<br/>（shader）"]
    D0 --> B1["基本調整（線性部分）<br/>曝光、白平衡"]

    subgraph G1["① 幾何（線性）"]
        direction LR
        LD["鏡頭變形"] --> ML["多格分鏡"]
    end

    subgraph G2["② 光學（線性光）"]
        direction LR
        DE["雙重曝光"] --> BK["焦外成像"] --> ZR["縮放旋轉模糊"] --> MB["動態模糊"]
        MB --> VG["鏡頭暈影"] --> HL["光暈"] --> LL["漏光"]
    end

    subgraph G3["③ 底片（log → 顯示）"]
        direction LR
        FM["底片色彩<br/>串擾 → 特性曲線 → 色偏<br/>或 LUT"] --> MIX["膠片效果比例<br/>中性結果與底片結果混合"] --> PP["古典版材"]
    end

    subgraph G4["④ 顯示空間"]
        direction LR
        B2["基本調整（顯示部分）<br/>亮度、對比、亮暗部、飽和、細節提取"] --> TN["色調分離"] --> DT["清晰度、銳化"]
    end

    subgraph G5["⑤ 表面"]
        direction LR
        DS["灰塵與刮痕"] --> GR["顆粒"]
    end

    B1 --> G1 --> G2 --> G3 --> G4 --> G5 --> OUT["輸出 sRGB<br/>（讀回 CPU）"]
```

排序上的考量：

- **鏡頭暈影在光學區段**：暗角是鏡頭的光學現象，發生在底片曝光之前，所以在線性光中以乘法處理，高光區域的暗角會被底片曲線自然壓縮。
- **光暈與漏光放在模糊之後**：它們發生在底片乳劑上，不應該被鏡頭模糊。
- **灰塵與刮痕在底片之後、顆粒之前**：灰塵與刮痕位於底片表面，不會被底片的色彩曲線改變顏色，但仍會被顆粒覆蓋。
- **顆粒最後加入**：才不會被其他調整放大。
- **逐像素模組可合併**：相鄰且只做逐像素運算的模組（例如基本調整線性部分、暗角、底片色彩、色調分離），日後可由 shader 組合器合併成單一 pass，減少紋理讀寫（見 12）。

### 5.3 模組與參數清單

所有參數集中定義在一份 schema 中（key、顯示名稱、型別、範圍、預設值、步進、是否為隨機種子、是否為位置座標）。schema 以 C# 程式碼宣告於各模組類別，啟動時由 `ModuleRegistry` 收集。UI 面板、YAML 讀寫、合併、驗證、選擇性貼上、**WGSL uniform struct 的佈局**都由這份 schema 自動產生，新增參數時只需要改一個地方。

座標類參數（`center_x`、`center_y`…）以 0–1 正規化表示；尺寸類參數以短邊比例換算。

下列「實作」皆指 WGSL compute shader。共用的工具函式（色彩空間轉換、雜湊亂數、雜訊、可分離模糊、取樣）放在 `Shaders/common/`，由 shader 前處理器組合（見 11.4）。

#### 5.3.1 基本調整 `basic`

| 參數 | 範圍 | 說明 |
|---|---|---|
| exposure | −3 ~ +3 EV | 曝光（線性） |
| temperature / tint | −100 ~ 100 | 色溫、綠↔洋紅（線性） |
| brightness | −100 ~ 100 | 中間調亮度 |
| contrast | −100 ~ 100 | 對比 |
| highlights / shadows | −100 ~ 100 | 亮部、暗部 |
| saturation | −100 ~ 100 | 飽和度 |
| structure | −100 ~ 100 | 細節提取：中頻局部對比 |

實作：線性與顯示兩部分各為一個逐像素 pass；`structure` 需要一次中半徑模糊（共用可分離高斯）。

#### 5.3.2 鏡頭變形 `lens_distortion`

| 參數 | 範圍 | 說明 |
|---|---|---|
| amount | −100 ~ 100 | 負值為桶狀（魚眼感），正值為枕狀 |
| chromatic | 0 ~ 100 | 橫向色散：紅、藍通道以不同半徑縮放，邊緣出現彩色鑲邊 |
| edge_soften | 0 ~ 100 | 邊緣柔化，模擬廉價鏡頭邊緣解像力下降 |
| auto_crop | 開 / 關 | 自動放大，裁掉變形後的黑邊 |

實作：每個輸出像素以徑向多項式 `r' = r·(1 + k1·r² + k2·r⁴)` 反算來源座標，以雙線性取樣（或 bicubic，品質選項）讀取；R、B 通道使用略為不同的 k 值形成色散；邊緣柔化依半徑混合原圖與模糊版本。

#### 5.3.3 多格分鏡 `multi_lens`

| 參數 | 範圍 | 說明 |
|---|---|---|
| layout | 2 橫格 / 3 橫格 / 4 橫格 / 2 直格 / 2×2 / 4 格交錯 | 版面配置 |
| offset | 0 ~ 100 | 各格之間的畫面錯位量（模擬鏡頭視差） |
| zoom_variation | 0 ~ 100 | 各格的縮放差異 |
| exposure_variation | 0 ~ 100 | 各格的曝光差異（模擬快門不一致） |
| color_variation | 0 ~ 100 | 各格的色偏差異 |
| divider | 0 ~ 100 | 分隔線柔和度（0 = 硬邊，100 = 完全漸層融合） |
| seed | 整數 | 各格隨機變化的種子 |

實作：各格的位移、縮放、曝光與色偏在 CPU 端由種子算出，以 uniform 陣列傳入；單一 pass 中每個像素判斷所屬格子與羽化權重，從原圖取樣後合成。

#### 5.3.4 雙重曝光 `double_exposure`

| 參數 | 範圍 | 說明 |
|---|---|---|
| source | 本張自己 / 專案內另一張 / 外部檔案 | 第二次曝光的影像來源 |
| source_path | 相對路徑 | 來源為檔案或其他照片時使用 |
| blend | 相加 / 濾色 / 變亮 / 色彩增值 | 合成模式（在線性光中計算） |
| opacity | 0 ~ 100 | 第二次曝光的強度 |
| offset_x / offset_y | −0.5 ~ 0.5 | 位移（正規化） |
| scale | 50 ~ 200 % | 縮放 |
| rotation | −180 ~ 180° | 旋轉 |
| mirror | 無 / 水平 / 垂直 | 鏡像（可用同一張圖做重影效果） |
| exposure_balance | −100 ~ 100 | 兩次曝光的亮度分配，避免疊加後過曝 |

實作：第二張影像上傳為另一張紋理，仿射轉換矩陣在 CPU 端算好以 uniform 傳入，shader 以反向映射取樣後在線性光中合成。「相加」最接近底片實際的兩次曝光，搭配 `exposure_balance` 讓總曝光量維持合理。

#### 5.3.5 焦外成像 `bokeh`

| 參數 | 範圍 | 說明 |
|---|---|---|
| center_x / center_y | 0 ~ 1 | 對焦點（可在預覽上拖曳） |
| focus_size | 0 ~ 100 | 清晰區大小 |
| focus_shape | 圓形 / 橢圓 / 水平帶 / 垂直帶 | 清晰區形狀（水平帶可做移軸效果） |
| aspect / angle | — | 橢圓長寬比、帶狀區角度 |
| transition | 0 ~ 100 | 清晰區到模糊區的過渡寬度 |
| blur | 0 ~ 100 | 最大模糊量 |
| shape | 圓形 / 六角形 / 八角形 / 貓眼 | 光圈形狀，決定光斑外觀 |
| highlight_boost | 0 ~ 100 | 光斑亮度增強 |

實作：

- 先以 shader 產生「模糊半徑圖」（依清晰區形狀與過渡計算每個像素的模糊程度）。
- 模糊前先提高高光、模糊後還原，才會出現明顯的光斑。
- **主要做法：散射 / 聚集式（gather）可變半徑模糊**。在半解析度上以光圈形狀的取樣點陣（黃金角螺旋或多邊形取樣）聚集鄰近像素，取樣權重依鄰近像素自身的模糊半徑決定，避免清晰前景被背景「吃掉」；最後與全解析度清晰影像依半徑圖混合。
- 六角形 / 八角形光圈可改用多次方向性一維模糊組合（類似遊戲引擎的六角形景深），成本與半徑呈線性關係。
- 貓眼形狀：依離畫面中心的方向，裁切取樣點陣（模擬機械暈影）。
- 備案：若聚集式在大半徑下品質不足，再實作 GPU FFT（Stockham）卷積處理固定光圈形狀，並以多級模糊內插近似可變模糊。

#### 5.3.6 縮放和旋轉模糊 `zoom_rotate_blur`

| 參數 | 範圍 | 說明 |
|---|---|---|
| center_x / center_y | 0 ~ 1 | 模糊中心（可在預覽上拖曳） |
| zoom | 0 ~ 100 | 放射狀模糊（爆炸變焦） |
| rotate | −100 ~ 100 | 旋轉模糊，正負代表方向 |
| protect_radius | 0 ~ 100 | 中心保護區半徑 |
| transition | 0 ~ 100 | 保護區過渡寬度 |

實作：每個像素沿「指向中心的方向」（放射）或「以中心為圓心的圓弧」（旋轉）取 N 個樣本平均，樣本數依模糊長度自動決定；大量模糊時以多次較短的 pass 疊代（每次長度倍增），以少量樣本達到長距離平滑。最後依保護區遮罩與原圖混合。GPU 上不需要極座標轉換。

#### 5.3.7 動態模糊 `motion_blur`

| 參數 | 範圍 | 說明 |
|---|---|---|
| angle | 0 ~ 180° | 模糊方向 |
| length | 0 ~ 100 | 模糊長度（短邊比例） |
| protect_enabled | 開 / 關 | 是否啟用保護區 |
| protect_x / protect_y / protect_size | — | 保護區，例如保持主體清晰（可在預覽上拖曳） |

實作：沿指定方向的一維線段取樣（同樣採用疊代倍增），在線性光中模糊，依保護區遮罩混合。

#### 5.3.8 鏡頭暈影 `vignette`

| 參數 | 範圍 | 說明 |
|---|---|---|
| amount | −100 ~ 100 | 正值變暗（暗角），負值變亮（亮角） |
| size | 0 ~ 100 | 暈影起始位置 |
| roundness | −100 ~ 100 | −100 貼合畫面矩形，100 為正圓 |
| feather | 0 ~ 100 | 漸層柔和度 |
| center_x / center_y | 0 ~ 1 | 暈影中心（可在預覽上拖曳） |

實作：逐像素 pass，以超橢圓距離函式計算衰減並乘上線性值。

#### 5.3.9 光暈 `halation`

| 參數 | 範圍 | 說明 |
|---|---|---|
| amount | 0 ~ 100 | 強度 |
| threshold | 0 ~ 100 | 高光閾值 |
| radius | 1 ~ 100 | 擴散半徑 |
| hue | 0 ~ 60 | 色相（紅 → 橘） |

實作：取亮度超過閾值的部分（軟閾值），建立降採樣金字塔做兩種半徑的模糊（類似 bloom 的 downsample / upsample 鏈），染色後加回線性影像。

#### 5.3.10 漏光 `light_leak`

| 參數 | 範圍 | 說明 |
|---|---|---|
| mode | 程序化 / 材質 | 程序化自動產生；材質則從漏光材質庫挑選 |
| texture | 材質名稱或「隨機」 | 材質模式使用 |
| amount | 0 ~ 100 | 強度 |
| angle | 0 ~ 360° | 漏光從畫面哪個邊緣進入 |
| size | 5 ~ 100 | 範圍 |
| hue | 0 ~ 360 | 色相（材質模式時為色相偏移） |
| irregularity | 0 ~ 100 | 不規則度（程序化模式） |
| count | 1 ~ 3 | 漏光數量 |
| seed | 整數 | 樣式種子 |

實作：程序化模式在 shader 中以正規化座標計算從畫面邊緣某點向內衰減的光斑，以低頻雜訊（以種子決定的值雜訊 / simplex）扭曲形狀，中心偏白、邊緣飽和；材質模式則把黑底的漏光掃描圖上傳為紋理，轉線性、縮放旋轉後相加。兩者都是在線性光中「增加曝光」，再由底片曲線壓縮，看起來比 Screen 混合更像真的漏光。

#### 5.3.11 底片色彩 `film`

| 參數 | 範圍 | 說明 |
|---|---|---|
| stock | 內建底片 key 或 `lut:檔名` | 底片種類（見第 7 章） |
| strength | 0 ~ 100 % | 膠片效果比例 |
| contrast | −100 ~ 100 | 底片對比 |
| saturation | −100 ~ 100 | 底片飽和 |
| warmth | −100 ~ 100 | 底片冷暖 |
| fade | 0 ~ 100 | 褪色（黑位提升） |

底片的色彩與顆粒拆成「底片色彩」與「顆粒」兩個模組，讓兩者可以獨立微調與複製。實作見第 7 章。

#### 5.3.12 古典版材 `photo_plate`

模擬 19 世紀的濕版、錫版等古典攝影質感。

| 參數 | 範圍 | 說明 |
|---|---|---|
| plate_type | 錫版 / 玻璃濕版 / 藍曬 / 蛋白相紙 | 決定基本色調與感光特性 |
| strength | 0 ~ 100 % | 與底片色彩結果的混合比例 |
| ortho | 0 ~ 100 | 正色性感光：紅色偏暗、藍色偏亮 |
| tint_hue / tint_amount | 0 ~ 360 / 0 ~ 100 | 自訂染色 |
| edge_erosion | 0 ~ 100 | 版材邊緣的不規則塗佈與剝落 |
| stains | 0 ~ 100 | 化學污漬與流痕 |
| uneven | 0 ~ 100 | 塗佈不均造成的明暗起伏 |
| seed | 整數 | 隨機紋理種子 |

實作：依版材的感光權重轉成單色（正色性時藍色權重高），再依版材類型染色；邊緣剝落以「到畫面邊緣的距離」（矩形距離可直接解析計算）加上多層低頻雜訊做閾值處理；污漬以多層雜訊的閾值與模糊組合產生；也可疊加材質庫中的版材掃描圖。

#### 5.3.13 色調分離 `tone`

| 參數 | 範圍 | 說明 |
|---|---|---|
| shadow_hue / shadow_amount | 0 ~ 360 / 0 ~ 100 | 暗部色調 |
| highlight_hue / highlight_amount | 0 ~ 360 / 0 ~ 100 | 亮部色調 |
| balance | −100 ~ 100 | 暗部與亮部的分界 |

實作：逐像素 pass。

#### 5.3.14 清晰與銳化 `detail`

| 參數 | 範圍 | 說明 |
|---|---|---|
| clarity | −100 ~ 100 | 清晰度（大半徑局部對比，限中間調） |
| sharpen | 0 ~ 100 | 銳化（小半徑反銳化遮罩，只作用於亮度） |

實作：清晰度使用降採樣金字塔上的大半徑模糊（或邊緣保留的導向濾波）；銳化為全解析度小半徑可分離高斯。

#### 5.3.15 灰塵與刮痕 `dirt_scratches`

| 參數 | 範圍 | 說明 |
|---|---|---|
| mode | 程序化 / 材質 | 程序化自動產生，或從材質庫挑選 |
| dust | 0 ~ 100 | 灰塵點數量 |
| scratches | 0 ~ 100 | 刮痕數量 |
| hairs | 0 ~ 100 | 毛髮 / 纖維數量 |
| polarity | 亮 / 暗 / 混合 | 正片上的灰塵為暗，負片放大後為亮 |
| strength | 0 ~ 100 | 整體不透明度 |
| scale | 50 ~ 200 % | 材質縮放 |
| seed | 整數 | 分布種子 |

實作（程序化）：灰塵、刮痕、毛髮的形狀在 **CPU 端依種子以正規化座標產生**（點的位置與大小、貝茲曲線控制點），以 storage buffer 傳入 GPU；shader 以「到最近圖形的有號距離」（分格加速，每格只檢查附近的圖形）算出抗鋸齒遮罩，再依極性提亮或壓暗。所有尺寸以短邊比例計算，因此與解析度無關。

#### 5.3.16 顆粒 `grain`

| 參數 | 範圍 | 說明 |
|---|---|---|
| amount | 0 ~ 100 | 強度 |
| size | 1 ~ 100 | 顆粒大小（短邊比例） |
| roughness | 0 ~ 100 | 粗糙度（粗細兩層雜訊的比例） |
| color | 0 ~ 100 | 彩色顆粒比例 |

實作：雜訊以「種子 + 正規化座標」為輸入的整數雜湊函式產生（見 5.4），粗細兩層以不同頻率疊加；強度隨亮度變化：中間調最明顯，暗部與亮部較弱。

### 5.4 一致性原則

- **空間參數以短邊比例計算**：半徑、長度、顆粒與灰塵大小都依短邊換算，低解析度預覽與全尺寸匯出看起來一致。
- **座標以正規化表示**：對焦點、模糊中心、暈影中心等皆為 0–1。
- **隨機效果使用固定種子**：種子由「檔名 CRC32（`System.IO.Hashing.Crc32`）+ 模組種子參數」產生。同一張照片每次算圖結果相同；不同照片自動得到不同的漏光、灰塵、刮痕。
- **GPU 亂數用整數雜湊**：shader 內以 PCG / xxHash 類的**整數**雜湊函式（輸入種子與座標）產生亂數，不依賴浮點運算順序，各家 GPU 結果一致；CPU 端需要亂數時（例如灰塵位置）使用相同演算法的 C# 實作，不使用 `System.Random`（其演算法可能隨 .NET 版本改變）。
- **形狀與解析度無關**：隨機形狀以固定大小的低頻網格產生（例如 5×5）再以平滑內插放大；顆粒雜湊的座標先量化到「以短邊比例決定的顆粒格」，所以預覽與匯出的顆粒分布相同（預覽只是較小的取樣）。
- **分塊不影響結果**：分塊算圖時，每塊取足夠的邊界延伸（apron，依該次管線中最大的模糊半徑計算），雜湊亂數以全圖座標計算，所以分塊與否結果相同。
- **浮點精度**：WGSL 的 `f32` 在不同 GPU 間不保證逐位元相同（超越函式如 `exp`、`log`、`pow` 的精度由實作決定）。因此跨裝置以 ΔE 門檻驗證；同一裝置、同一驅動程式要求逐位元相同（見 14）。

---

## 6. 效果模組總覽

### 6.1 模組分類

```mermaid
mindmap
  root(("FilmBatch<br/>效果模組"))
    基本
      基本調整
    幾何
      鏡頭變形
      多格分鏡
    光學
      雙重曝光
      散景與選擇性對焦
      放射與旋轉模糊
      動態模糊
      暗角
      光暈
      漏光
    底片
      底片色彩
      古典版材
      色調分離
      清晰與銳化
    表面
      灰塵與刮痕
      顆粒
```

### 6.2 模組特性一覽

| 模組 | key | 階段 | 隨機種子 | 畫布控制點 | 材質 | GPU 成本 | 里程碑 |
|---|---|---|---|---|---|---|---|
| 基本調整 | `basic` | 線性 + 顯示 | — | — | — | 低（含一次中半徑模糊） | M1 |
| 鏡頭變形 | `lens_distortion` | 幾何 | — | — | — | 低 | M5 |
| 多格分鏡 | `multi_lens` | 幾何 | ✓ | — | — | 低 | M6 |
| 雙重曝光 | `double_exposure` | 光學 | — | ✓ | ✓ | 低 | M6 |
| 散景與選擇性對焦 | `bokeh` | 光學 | — | ✓ | — | **高** | M5 |
| 放射與旋轉模糊 | `zoom_rotate_blur` | 光學 | — | ✓ | — | 中 | M5 |
| 動態模糊 | `motion_blur` | 光學 | — | ✓ | — | 中 | M5 |
| 暗角 | `vignette` | 光學 | — | ✓ | — | 極低 | M1 |
| 光暈 | `halation` | 光學 | — | — | — | 中 | M1 |
| 漏光 | `light_leak` | 光學 | ✓ | ✓ | ✓ | 低 | M1（程序化）/ M6（材質） |
| 底片色彩 | `film` | 底片 | — | — | LUT | 低 | M1 |
| 古典版材 | `photo_plate` | 底片 | ✓ | — | ✓ | 低 | M6 |
| 色調分離 | `tone` | 顯示 | — | — | — | 極低 | M1 |
| 清晰與銳化 | `detail` | 顯示 | — | — | — | 中 | M1 |
| 灰塵與刮痕 | `dirt_scratches` | 表面 | ✓ | — | ✓ | 低 | M6 |
| 顆粒 | `grain` | 表面 | ✓ | — | — | 低 | M1 |

說明：

- **隨機種子**：該模組的隨機形狀由「檔名 + 種子參數」決定，複製設定時預設不複製種子（見 3.5）。
- **畫布控制點**：可在預覽上直接拖曳位置類參數（見 2.4.3）。
- **材質**：除了內建程序化效果，也可使用材質庫中的外部素材（見第 8 章）。

### 6.3 模組設計原則

- **單一職責**：每個模組只做一件事，例如底片色彩與顆粒分開，才能各自微調、各自複製。
- **零值即恆等**：參數全為 0 或模組關閉時，輸出必須與輸入完全相同（關閉時不派送 pass；參數為 0 時 shader 的數學也必須是恆等）。
- **物理優先**：光學與曝光類效果在線性光中計算，形狀與強度盡量符合真實光學與底片的行為，而不是單純的圖層混合。
- **程序化優先、材質為輔**：所有效果在沒有任何外部素材時都能運作；材質用於追求特定質感。
- **自有實作**：shader 全部自行撰寫。可參考公開的演算法與論文，以及 RapidRAW 等專案的架構思路，但 **RapidRAW 為 AGPL-3.0 授權，不複製其任何程式碼或 shader**。

---

## 7. 底片色彩來源

### 7.1 內建參數化底片模型

| 參數 | 意義 |
|---|---|
| `crosstalk` 3×3 | 染料層色彩串擾。每列總和為 1，中性灰不受影響；對角線 > 1 時飽和度提高 |
| `k`（RGB） | 各通道特性曲線斜率（對比） |
| `shift`（RGB） | 各通道曲線位移，形成色彩交叉（crossover） |
| `black` / `white`（RGB） | 黑位與白位的色偏 |
| `shadow_tint` / `highlight_tint` | 暗部與亮部的額外色偏 |
| `sat` | 整體飽和 |
| `mono` | 黑白底片的各通道感光權重 |

曲線在 sRGB 值的 logit 空間計算：`y = sigmoid(k · logit(x) − shift)`，可保持端點 0→0、1→1，適合顯示參考的輸入。整條底片模型在 shader 中逐像素計算，參數以 uniform 傳入，調整「底片對比 / 飽和 / 冷暖」時只需要更新 uniform，不需要重建任何資料。

```mermaid
flowchart LR
    A["線性 RGB"] --> B["log2"] --> C["× 串擾矩陣"] --> D["exp2 → sRGB 編碼"]
    D --> E["logit"] --> F["各通道 k、shift"] --> G["sigmoid"]
    G --> H["黑位 / 白位映射"] --> I["暗部 / 亮部色偏"] --> J["飽和調整"] --> K["底片結果"]
```

第一版內建的風格（皆為「風格近似」，非實測校正）：

- 彩色：Portra 400、Ektar 100、Gold 200、Superia 400、Pro 400H、CineStill 800T、Kodachrome、Velvia 50
- 黑白：Tri-X 400、HP5 Plus

內建底片資料以 JSON 嵌入組件資源（`EmbeddedResource`）。「底片對比 / 飽和 / 冷暖」分別對應 `k` 的倍率、飽和倍率與 `shift` 的紅藍偏移。

### 7.2 LUT 底片

除了內建模型，底片也可以是一個 3D LUT。LUT 與內建底片一樣，可以搭配膠片效果比例、底片對比、飽和、冷暖、褪色等微調。

支援格式：

- `.cube`（業界通用的 3D LUT 格式）。
- HaldCLUT 影像（PNG / TIFF）。

GPU 實作：LUT 在 CPU 端解析後上傳為 **3D 紋理**（`rgba16float`，可使用硬體三線性過濾；`rgba32float` 的過濾需要額外的裝置功能，不列為必要），shader 以 `textureSampleLevel` 取樣，並依 LUT 尺寸修正半格偏移。LUT 紋理以檔案路徑 + 修改時間為鍵快取在 GPU 上。

**用 HaldCLUT 製作自己的 LUT**：把任何一套調色轉成 FilmBatch 可用的底片。

```mermaid
flowchart LR
    A["FilmBatch<br/>產生 identity HaldCLUT<br/>512×512、16-bit PNG"] --> B["任一調色軟體<br/>對這張對照圖套用調色<br/>只用色彩類調整"]
    B --> C["存成 16-bit PNG / TIFF"] --> D["放進 LUT 資料夾"]
    D --> E["底片選單出現此 LUT"]
```

- 調色時**只使用全域的色彩調整**（曲線、色彩平衡、HSL 等），不要加顆粒、暗角、模糊等空間效果，因為 LUT 只能記錄「每個顏色對應到哪個顏色」。
- LUT 檔只放使用者自製或授權明確的檔案。

### 7.3 未來擴充

- 研究 agx-emulsion / Spectral Film LUT 的光譜模型，產生更準確的底片參數或 LUT（GPU 上可直接執行較重的光譜模型）。
- 底片參數編輯器：讓使用者自己調整 crosstalk 與各通道曲線，存成自訂底片（調整時即時預覽）。

---

## 8. 材質庫

漏光、灰塵與刮痕、古典版材、雙重曝光都可以使用外部材質。

### 8.1 資料夾結構

```
<使用者資料夾>/FilmBatch/
├── settings.json     偏好設定、視窗位置、GPU 介面卡選擇
├── layout.json       停靠面板版面
├── presets/          預設檔（YAML）
├── luts/             .cube 與 HaldCLUT
├── logs/             執行記錄
└── textures/
    ├── leaks/        漏光掃描（黑底）
    ├── dust/         灰塵、刮痕、毛髮（黑底白痕或透明 PNG）
    ├── plates/       版材紋理
    └── overlays/     雙重曝光用素材
```

### 8.2 規則

- 支援 JPEG、PNG（含透明通道）、16-bit TIFF；以 Magick.NET 解碼後上傳為 GPU 紋理，並建立 mipmap 以便縮小取樣時不產生鋸齒。
- 材質依目標照片長寬比自動裁切與縮放（在 shader 中以取樣座標完成），可旋轉 90° 配合直幅照片。
- 選擇「隨機材質」時，以照片種子決定挑選哪一張，所以每張照片的結果固定（材質清單先依檔名以 `StringComparer.Ordinal` 排序，避免不同 OS 的列舉順序造成差異）。
- 程式**內建程序化產生器**，沒有任何材質檔時所有效果仍可使用。
- 材質庫只放使用者自製（例如自己掃描的漏光、灰塵）或授權明確可自由使用的素材，**不擷取其他軟體內附的材質**，以免違反授權。
- 未來可提供「材質製作工具」：把自己拍攝的底片掃描圖轉成黑底材質（去背、正規化亮度）。

---

## 9. 跨平台與 .NET 發佈

### 9.1 技術選型

| 用途 | 套件 | 說明 |
|---|---|---|
| 語言 / 執行環境 | **C# 14 / .NET 10（LTS）** | .NET 8 的支援於 2026-11 結束，不採用 |
| GUI | **Avalonia UI 12.x** | 三平台一致外觀（自繪，Skia 算繪）、HiDPI、MIT 授權 |
| UI 主題 | Avalonia `FluentTheme`（深色）+ 自訂資源字典 | 依 2.2 配色 |
| 停靠面板 | **Dock.Avalonia 12.x** | 對應 Avalonia 12 的版本線；可停靠、浮動、序列化版面；MIT 授權 |
| MVVM | **CommunityToolkit.Mvvm** | 原始碼產生器，無反射、對 Trimming 友善；MIT 授權 |
| 依賴注入 / 記錄 | Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.Logging | 組裝服務、檔案記錄 |
| **GPU API** | **WebGPU（wgpu-native）** | 跨平台 GPU 抽象：Windows → DX12 / Vulkan、macOS → Metal、Linux → Vulkan；MIT / Apache-2.0 授權 |
| **C# WebGPU 綁定** | 首選 **Silk.NET.WebGPU**（+ `Silk.NET.WebGPU.Native.WGPU`、`Extensions.WGPU`）；候選 Evergine `WebGPU.NET`（wgpu-native）、`WebGPUSharp`（Dawn，.NET 10、支援 AOT） | 於 M0 spike 比較後定案；引擎只透過自有的 `FilmBatch.Gpu` 薄封裝存取，日後可替換 |
| Shader 語言 | **WGSL** | 以 compute shader 實作全部效果；CI 以 naga 驗證語法 |
| 影像 IO / EXIF / ICC | **Magick.NET**（Q16-HDRI） | 讀寫 16-bit TIFF / PNG、JPEG，保留 EXIF、嵌入 ICC，日後的色彩管理（內含 LittleCMS）；Apache 2.0 授權 |
| 預設檔 | YamlDotNet | YAML 讀寫 |
| 專案檔 | System.Text.Json（原始碼產生器） | JSON 讀寫，Trimming 安全 |
| 命令列 | System.CommandLine | `filmbatch` CLI 子命令 |
| 測試 | xUnit、**Avalonia.Headless.XUnit** | UI 測試不需要實體顯示器；GPU 測試見 14 |
| 發佈 | `dotnet publish`（self-contained、ReadyToRun）；選用 Velopack 產生安裝檔與自動更新 | 見 9.3 |

選型注意：

- **為什麼用 GPU 而不是 OpenCV**：本專案的效果幾乎都是逐像素運算或大半徑卷積，正是 GPU 擅長的工作；以 WGSL 撰寫可讓預覽與匯出在三個平台共用同一份程式碼，拖曳滑桿時即時更新。OpenCV 的 GPU 路徑不符合需求（CUDA 只支援 NVIDIA、OpenCL 在 macOS 已被棄用）。
- **Silk.NET.WebGPU 的維護狀況**：Silk.NET 團隊目前把主要精力放在 3.0，2.x 版本更新較少。因此綁定一律包在 `FilmBatch.Gpu` 內，M0 同時試做候選綁定，選定後鎖定 wgpu-native 版本。
- **wgpu-native 版本**：綁定套件內附的 wgpu-native 版本決定可用的 WebGPU 功能；以 CPM 鎖定版本，升級前在三平台 CI 驗證。
- **版本集中管理**：使用 `Directory.Packages.props`（Central Package Management）鎖定所有套件版本，Avalonia 與 Dock.Avalonia 必須同一主版本。
- **不使用 ImageSharp / Emgu CV / OpenCV**：ImageSharp 採 Six Labors Split License、Emgu CV 為 GPL / 商業雙授權；OpenCV 已由 GPU 引擎取代。
- **GPU 不採用 OpenGL compute shader / OpenCL**：macOS 的 OpenGL 停在 4.1、OpenCL 已被 Apple 棄用。

### 9.2 跨平台注意事項

| 項目 | 做法 |
|---|---|
| 路徑 | 一律使用 `Path.Combine` / `Path.GetRelativePath`；專案檔內存相對路徑並用 `/` 分隔 |
| 中文路徑 | .NET 與 Magick.NET 皆支援 Unicode 路徑；仍納入路徑測試（見 14） |
| 使用者資料夾 | 自行實作 `AppPaths`，以 `OperatingSystem.IsWindows() / IsMacOS() / IsLinux()` 決定：Windows `%APPDATA%\FilmBatch`、macOS `~/Library/Application Support/FilmBatch`、Linux `$XDG_CONFIG_HOME/FilmBatch`（預設 `~/.config/FilmBatch`） |
| 程式內建資源 | 圖示、XAML 以 `AvaloniaResource`（`avares://`）嵌入；內建底片資料與 **WGSL shader** 以 `EmbeddedResource` 嵌入；開發模式可改從原始碼資料夾讀取 shader 以支援熱重載 |
| 偏好設定 | 統一存為 `settings.json`（System.Text.Json） |
| 檔名大小寫 | 比對副檔名一律 `OrdinalIgnoreCase`；專案內照片 key 保留原始大小寫，並以 `StringComparer.Ordinal` 比對 |
| GPU 後端 | Windows 預設 DX12（Vulkan 為備援）、macOS 為 Metal、Linux 為 Vulkan；偏好設定可強制指定後端，方便排除驅動程式問題 |
| 無 GPU 環境 | Windows 可用 WARP（DX12 軟體介面卡）、Linux 可用 lavapipe（Mesa 的 Vulkan 軟體實作）；macOS 一律有 Metal。啟動時列舉介面卡，硬體優先 |
| GPU 執行緒 | 所有 GPU 指令由單一「GPU 執行緒」提交（`Channel` 佇列），避免多執行緒同時操作佇列；CPU 解碼 / 編碼在執行緒池並行 |
| UI 執行緒 | 背景結果一律經 `Dispatcher.UIThread.Post` / `InvokeAsync` 回到 UI 執行緒；不在背景執行緒存取任何控制項 |
| 快捷鍵 | 使用 `KeyGesture` + 平台熱鍵設定，`Ctrl` 在 macOS 自動對應 `⌘` |
| 編碼 | 文字檔讀寫一律 UTF-8（.NET 預設，不寫 BOM） |
| Linux 相依 | 執行需 `libfontconfig1`、X11 相關函式庫，以及 **Vulkan 驅動程式**（`mesa-vulkan-drivers` 或廠商驅動）；在發佈說明列出 |

### 9.3 發佈設定

採用 **self-contained + 資料夾發佈**（輸出一個資料夾或 macOS `.app`），不採用 single-file 壓縮：

- self-contained 不需要使用者另外安裝 .NET 執行環境。
- 原生函式庫（SkiaSharp、HarfBuzzSharp、wgpu-native、Magick.Native）保持獨立檔案，便於除錯與授權聲明。
- 啟用 **ReadyToRun** 縮短冷啟動時間；**Native AOT 列為 M7 之後的評估項目**（需確認 Dock.Avalonia、YamlDotNet、Magick.NET 與所選 WebGPU 綁定的 AOT 相容性）。
- Trimming 先使用 `TrimMode=partial`；以元件展示視窗與冒煙測試確認沒有遺漏。

共用設定集中在 `Directory.Build.props` 與 `FilmBatch.App.csproj`，各平台只差 RID：

```xml
<!-- src/FilmBatch.App/FilmBatch.App.csproj（節錄） -->
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <AssemblyName>FilmBatch</AssemblyName>
  <ApplicationIcon>Assets/icon.ico</ApplicationIcon>
  <SelfContained>true</SelfContained>
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>partial</TrimMode>
  <Version>1.0.0</Version>
  <Company>FilmBatch</Company>
  <Product>FilmBatch</Product>
</PropertyGroup>
```

```bash
# Windows
dotnet publish src/FilmBatch.App -c Release -r win-x64 -o dist/win-x64

# macOS（Apple Silicon / Intel），之後由 build 腳本組成 FilmBatch.app
dotnet publish src/FilmBatch.App -c Release -r osx-arm64 -o dist/osx-arm64
dotnet publish src/FilmBatch.App -c Release -r osx-x64   -o dist/osx-x64

# Linux
dotnet publish src/FilmBatch.App -c Release -r linux-x64 -o dist/linux-x64
```

打包腳本 `build/Build.cs`（.NET 10 的檔案型程式，`dotnet run build/Build.cs -- --rid osx-arm64 --smoke`）負責：以 naga 驗證所有 WGSL、發佈、組成 macOS `.app`（`Info.plist`、`icon.icns`、`Contents/MacOS/`）、複製授權聲明、執行冒煙測試、壓縮產物。

各平台的建置需求：

| 平台 | 需求 | 其他 |
|---|---|---|
| Windows | .NET 10 SDK | 選用：Velopack / Inno Setup 安裝檔、程式碼簽章 |
| macOS | .NET 10 SDK、Xcode Command Line Tools（簽章、`hdiutil`） | 公開發佈需 Apple Developer 簽章與公證；`.app` 內所有 `.dylib`（含 `libwgpu_native.dylib`）都要簽章 |
| Linux | .NET 10 SDK | 在較舊的發行版（例如 Ubuntu 22.04）建置與測試，以提高 glibc 相容性；選用 AppImage |

注意事項：

- **ReadyToRun 跨平台**：ReadyToRun 可以在單一 OS 上為其他 RID 編譯，但 macOS 的簽章、公證與 `.dmg` 必須在 macOS 上進行，因此仍以三平台矩陣建置。
- **授權檔**：發佈包中附上 Avalonia、Dock.Avalonia、SkiaSharp、wgpu-native、WebGPU 綁定、ImageMagick / Magick.NET、YamlDotNet 等第三方授權聲明（`THIRD-PARTY-NOTICES.txt`）。

### 9.4 建置與發佈流程

以 GitHub Actions 矩陣自動化：

```mermaid
flowchart TD
    A["推送版本 tag，例如 v1.0.0"] --> B["GitHub Actions 矩陣建置"]
    B --> W["windows-latest<br/>win-x64"]
    B --> M1["macos-14<br/>osx-arm64"]
    B --> M2["macos-13<br/>osx-x64（可選）"]
    B --> L["ubuntu-22.04<br/>linux-x64"]

    W --> S1["actions/setup-dotnet（10.0）<br/>還原 NuGet（含快取）"]
    M1 --> S1
    M2 --> S1
    L --> S1

    S1 --> V["naga 驗證所有 WGSL"]
    V --> T["dotnet test<br/>Core / UI 測試 + GPU 測試<br/>（軟體介面卡：WARP / lavapipe）"]
    T -->|"失敗"| X["中止建置"]
    T -->|"通過"| N["dotnet publish<br/>self-contained + ReadyToRun"]
    N --> SM["冒煙測試：啟動執行檔（--smoke），<br/>載入範例資料夾，匯出一張"]
    SM --> P{"平台"}
    P -->|"Windows"| PW["壓縮為 .zip<br/>（可選：Velopack 安裝檔）"]
    P -->|"macOS"| PM["組成 .app → 打包 .dmg<br/>（可選：簽章 + 公證）"]
    P -->|"Linux"| PL["打包 .tar.gz<br/>（可選：AppImage）"]
    PW --> R["上傳到 GitHub Release"]
    PM --> R
    PL --> R
```

- 開發與自用階段直接 `dotnet run --project src/FilmBatch.App`，不必發佈。
- CI 的 runner 沒有實體 GPU：Windows 使用 WARP、Linux 安裝 `mesa-vulkan-drivers` 使用 lavapipe；macOS runner 的 Metal 可用性於 M0 驗證，若不可用則 macOS 的 GPU 測試改在本機執行。
- **M0 階段就要完成一次冒煙建置**：把 UI 原型（含 GPU spike）在三個平台各發佈一次並確認能啟動，及早發現原生函式庫、Trimming、資源嵌入等問題。

---

## 10. 批次匯出

| 選項 | 內容 |
|---|---|
| 範圍 | 選取的照片 / 全部 / 依預設篩選 |
| 格式 | JPEG（品質 1–100）、16-bit TIFF、16-bit PNG |
| 尺寸 | 原尺寸 / 長邊指定像素（縮放在 GPU 上以高品質濾波完成） |
| 目的地 | 預設為 `<資料夾>/export/`，可自訂 |
| 檔名 | 範本，例如 `{name}_{preset}`，或 `{name}` 加後綴 |
| 中繼資料 | 保留 EXIF（方向重設為 1）；嵌入 sRGB ICC（Magick.NET） |
| 進度 | 可取消（`CancellationToken`）、顯示進度（`IProgress<T>`） |

**匯出管線**：GPU 一次只算一張，CPU 端的解碼與編碼與之重疊，形成三段式流水線：

```mermaid
flowchart LR
    subgraph CPU1["CPU 執行緒池"]
        R["解碼 N+1<br/>（Magick.NET）"]
    end
    subgraph GPU["GPU 執行緒"]
        G["上傳 → 算圖 → 讀回 N"]
    end
    subgraph CPU2["CPU 執行緒池"]
        E["編碼並寫檔 N−1<br/>（保留 EXIF、嵌入 ICC）"]
    end
    R --> G --> E
```

- 預讀張數依記憶體自動決定（預設前後各 1–2 張），取代原本的「並行數」設定。
- 匯出期間預覽請求可插隊：GPU 執行緒在兩張匯出之間（或分塊之間）優先處理預覽請求。

匯出時，在 `.filmbatch/export_log.json` 記錄每張照片實際使用的完整參數、演算法版本與 GPU 介面卡資訊，方便日後重現。

另提供命令列介面（獨立的主控台程式 `filmbatch`，與 GUI 共用 Core / Engine），方便寫腳本或排程：

```bash
filmbatch export <照片資料夾> --out <輸出資料夾> --format jpg --quality 95
filmbatch export <照片資料夾> --preset "Portra 暖調" --only-unassigned
filmbatch export <照片資料夾> --adapter software   # 強制使用軟體介面卡
filmbatch haldclut identity.png                     # 產生 HaldCLUT 對照圖
filmbatch gpu-info                                  # 列出可用的 GPU 介面卡與限制
```

GUI 在 Windows 為 `WinExe`（不帶主控台視窗），因此 CLI 另建 `FilmBatch.Cli` 專案，兩者一起發佈在同一個資料夾。

---

## 11. 系統架構

### 11.1 分層架構

```mermaid
flowchart TB
    subgraph UI["UI 層（FilmBatch.App · Avalonia 12）"]
        direction LR
        U1["主視窗與停靠面板<br/>Dock.Avalonia"]
        U2["預覽畫布 + 控制點"]
        U3["模組面板<br/>由 schema 經 DataTemplate 產生"]
        U4["預設庫面板"]
        U5["縮圖列"]
        U6["對話框"]
        U7["ViewModels<br/>CommunityToolkit.Mvvm"]
    end

    subgraph APP["應用層（FilmBatch.Core）"]
        direction LR
        A1["命令與復原堆疊<br/>CommandStack"]
        A2["專案服務<br/>ProjectService"]
        A3["預設庫<br/>PresetLibrary"]
        A4["材質庫<br/>TextureLibrary"]
    end

    subgraph SCH["排程層（FilmBatch.Engine）"]
        direction LR
        S1["算圖排程<br/>RenderScheduler"]
        S2["快取<br/>來源紋理 / 區段結果 / 縮圖"]
        S3["匯出佇列<br/>ExportQueue"]
    end

    subgraph ENG["引擎層（FilmBatch.Engine，不依賴 UI）"]
        direction LR
        E1["管線 Pipeline<br/>組合 compute pass"]
        E2["效果模組 Modules/"]
        E3["底片模型與 LUT Film/"]
        E4["影像 IO<br/>Magick.NET"]
    end

    subgraph GPU["GPU 層（FilmBatch.Gpu）"]
        direction LR
        G1["裝置與介面卡管理<br/>裝置遺失重建"]
        G2["紋理 / 緩衝區池"]
        G3["Shader 載入、前處理、<br/>管線快取"]
        G4["WebGPU 綁定 → wgpu-native"]
        G5[("Shaders/*.wgsl")]
    end

    subgraph STO["儲存"]
        direction LR
        D1[("project.json")]
        D2[("presets/*.yaml")]
        D3[("textures/、luts/")]
        D4[("settings.json<br/>layout.json")]
    end

    UI -->|"使用者操作 → 命令"| A1
    A1 --> A2
    U4 --> A3
    A2 --> S1
    A2 --> S3
    S1 --> S2
    S1 --> E1
    S3 --> E1
    S3 --> E4
    E1 --> E2
    E1 --> E3
    E2 --> A4
    E1 --> G1
    E2 --> G3
    G1 --> G4
    G2 --> G4
    G3 --> G5
    A2 --> D1
    A3 --> D2
    A4 --> D3
    UI --> D4
    S1 -.->|"結果（事件 → Dispatcher.UIThread）"| U2
    S2 -.->|"縮圖（事件 → Dispatcher.UIThread）"| U5
```

關鍵原則：

- **引擎層不依賴 UI**：`RenderAsync(來源, 參數, 種子, 輸出規格, CancellationToken) → 影像` 可以在 CLI、測試、背景中直接使用。
- **GPU 細節集中在 `FilmBatch.Gpu`**：模組只描述「要派送哪些 pass、綁定哪些資源」，不直接呼叫 WebGPU 綁定的 API，日後可更換綁定或後端。
- **Core 不引用 Avalonia 與 GPU**：專案相依以 `.csproj` 參考強制分層（App → Engine → Gpu；App / Engine → Core），由建置保證不會反向依賴。
- **UI 不直接改資料**：所有修改都透過命令物件進入 `CommandStack`，因此每個操作都能復原，也能合併連續的滑桿拖曳。
- **單一 GPU 執行緒**：所有 GPU 提交經由一個佇列序列化；結果以事件 / `IProgress<T>` 傳出，由 ViewModel 經 `Dispatcher.UIThread` 回到 UI 執行緒。
- **參數 schema 單一來源**：UI 面板、序列化、驗證、選擇性貼上、uniform 佈局都從 schema 產生。
- **預覽與匯出共用同一套 shader**，只有輸出解析度與分塊方式不同，避免結果不一致。

### 11.2 效果模組介面

```csharp
public enum Stage { Linear, Geometry, Optical, Film, Display, Surface }

public interface IEffectModule
{
    string Key { get; }                          // "vignette"
    Stage Stage { get; }
    IReadOnlyList<ParamDef> Params { get; }      // schema（同時決定 uniform 佈局）

    /// 準備本模組需要的 GPU 資源（材質、LUT、灰塵圖形緩衝區…），可快取
    ValueTask PrepareAsync(ModuleParams p, RenderContext ctx);

    /// 把本模組的 compute pass 錄進指令編碼器：讀 ctx.Input、寫 ctx.Output
    void Encode(GpuPassContext ctx, ModuleParams p);
}

public sealed class VignetteModule : IEffectModule
{
    public string Key => "vignette";
    public Stage Stage => Stage.Optical;
    public IReadOnlyList<ParamDef> Params { get; } =
    [
        ParamDef.Bool("enabled", "啟用", false),
        ParamDef.Float("amount", "強度", 0, -100, 100),
        ParamDef.Float("size", "大小", 50, 0, 100),
        ParamDef.Position("center_x", "中心 X", 0.5),
        ParamDef.Position("center_y", "中心 Y", 0.5),
        // …
    ];

    public ValueTask PrepareAsync(ModuleParams p, RenderContext ctx) => ValueTask.CompletedTask;

    public void Encode(GpuPassContext ctx, ModuleParams p) =>
        ctx.Dispatch("optical/vignette.wgsl", uniforms: p.ToUniforms(Params));
}
```

```wgsl
// Shaders/v1/optical/vignette.wgsl（節錄）
#include "common/color.wgsl"

struct Params { amount: f32, size: f32, roundness: f32, feather: f32, center: vec2f, aspect: f32 }
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var dst: texture_storage_2d<rgba32float, write>;
@group(0) @binding(2) var<uniform> p: Params;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) id: vec3u) {
    let size = textureDimensions(src);
    if (id.x >= size.x || id.y >= size.y) { return; }
    let uv = (vec2f(id.xy) + 0.5) / vec2f(size);
    let c = textureLoad(src, id.xy, 0);
    textureStore(dst, id.xy, vec4f(c.rgb * vignette_falloff(uv, p), c.a));
}
```

- 管線依 `Stage` 與註冊順序依序呼叫已啟用的模組。模組在 `ModuleRegistry` 中明確註冊（不以反射掃描組件，保持 Trimming / AOT 相容）。
- 新增效果時只需要新增一個模組類別、一或多個 `.wgsl` 檔並註冊，UI 面板會依 schema 自動長出對應的參數列。
- uniform 緩衝區的位元組佈局由 schema 依 WGSL 對齊規則產生；單元測試比對 C# 端佈局與 WGSL `struct` 宣告（以 naga 反射取得）是否一致。

### 11.3 預覽算圖時序

```mermaid
sequenceDiagram
    actor U as 使用者
    participant P as 模組面板（ViewModel）
    participant C as CommandStack
    participant S as ProjectService
    participant R as RenderScheduler
    participant G as GPU 執行緒
    participant V as 預覽畫布

    U->>P: 拖動滑桿
    P->>C: SetParamCommand（拖曳中，可合併）
    C->>S: SetValue(photo, key, value)
    S-->>P: 更新覆寫標記
    S->>R: RequestPreview(photo, 1600px)
    Note over R: 每個顯示幀最多一次請求<br/>只保留最新請求（Channel 容量 1、DropOldest）
    R->>G: 從受影響的區段開始算圖（其餘用區段快取）
    G->>G: 派送 compute pass → 轉 BGRA8 → 複製到讀回緩衝區
    G-->>R: MapAsync 完成（generation = n）
    R-->>V: 經 Dispatcher.UIThread：若 n 為最新則更新 WriteableBitmap，否則丟棄
    U->>P: 放開滑桿
    P->>C: 結束合併，記為一筆復原紀錄
    R->>G: 背景更新該照片縮圖（低優先）
```

- 來源影像在切換照片時上傳一次，之後只有 uniform 改變，不重新上傳。
- 讀回使用雙緩衝的讀回緩衝區（注意 WebGPU 要求每列位元組數為 256 的倍數），GPU 寫下一幀時 CPU 讀上一幀。
- 若 GPU 跟不上（例如散景大半徑），排程器自動把拖曳中的預覽降為 800px，放開後補算 1600px。

### 11.4 Shader 管理

- WGSL 檔案放在 `src/FilmBatch.Gpu/Shaders/v{版本}/`，以 `EmbeddedResource` 打包。
- WGSL 本身沒有 include 機制，由自有的簡易前處理器處理 `#include` 與常數替換（例如 workgroup 大小），前處理結果以內容雜湊為鍵快取已編譯的 compute pipeline。
- 開發模式（`--shader-dev`）從原始碼資料夾讀取並監看 `.wgsl`，存檔後自動重新編譯並重算預覽，方便調整效果。
- CI 以 naga CLI 對前處理後的每個 shader 做語法與型別驗證。

### 11.5 批次匯出時序

```mermaid
sequenceDiagram
    actor U as 使用者
    participant D as 匯出對話框
    participant S as ProjectService
    participant Q as ExportQueue
    participant L as 解碼工作者
    participant G as GPU 執行緒
    participant W as 編碼工作者
    participant B as 狀態列

    U->>D: 設定並按「匯出」
    D->>S: 取得每張照片的參數快照
    S-->>D: [(照片, 最終參數, 種子), …]
    D->>Q: Enqueue(工作清單)
    loop 每張照片（三段流水線重疊執行）
        Q->>L: 解碼全尺寸（Magick.NET）
        L->>G: 上傳 → 算圖（必要時分塊）→ 讀回 16-bit
        G->>W: 編碼並寫檔（保留 EXIF、嵌入 ICC）
        W-->>Q: 完成 / 失敗
        Q-->>B: IProgress 更新進度
    end
    U-->>Q: （可隨時取消，CancellationToken）
    Q-->>B: 完成：成功 N 張、失敗 M 張
    Q->>S: 寫入 export_log.json
```

### 11.6 專案目錄結構

```
FilmBatch/
├── FilmBatch.sln
├── Directory.Build.props         # 共用編譯設定（Nullable、LangVersion、TreatWarningsAsErrors）
├── Directory.Packages.props      # 集中鎖定套件版本
├── global.json                   # 鎖定 .NET SDK 版本
├── build/
│   └── Build.cs                  # naga 驗證、發佈、.app 組裝、冒煙測試（dotnet run）
├── src/
│   ├── FilmBatch.Gpu/            # WebGPU 薄封裝
│   │   ├── Device/               # 介面卡列舉、裝置建立、裝置遺失處理
│   │   ├── Resources/            # 紋理 / 緩衝區池、讀回緩衝區
│   │   ├── ShaderSystem/         # 前處理器、pipeline 快取、熱重載
│   │   └── Shaders/
│   │       └── v1/
│   │           ├── common/       # 色彩轉換、雜湊亂數、雜訊、可分離模糊、取樣
│   │           ├── linear/  geometry/  optical/  film/  display/  surface/
│   │           └── io/           # sRGB↔線性、輸出轉換、縮放
│   ├── FilmBatch.Engine/         # 不依賴 UI
│   │   ├── Imaging/              # Magick.NET 讀寫、EXIF、上傳格式轉換
│   │   ├── Pipeline/             # Pipeline、RenderContext、區段快取、分塊算圖
│   │   ├── Film/                 # 底片模型、內建底片（EmbeddedResource）、LUT、HaldCLUT
│   │   ├── Modules/              # 每個效果一個類別
│   │   ├── Textures/             # 材質載入、灰塵 / 刮痕圖形產生
│   │   └── Scheduling/           # RenderScheduler、快取、ExportQueue
│   ├── FilmBatch.Core/           # 不依賴 UI 與 GPU
│   │   ├── Params/               # ParamDef、ParamValue、合併、diff、uniform 佈局
│   │   ├── Projects/             # Project、project.json
│   │   ├── Presets/              # PresetLibrary、YAML、快照同步
│   │   ├── Commands/             # 命令與復原堆疊
│   │   ├── Random/               # 與 shader 相同演算法的雜湊亂數
│   │   └── AppPaths.cs           # 使用者資料夾、相對路徑
│   ├── FilmBatch.App/            # Avalonia GUI
│   │   ├── App.axaml(.cs)
│   │   ├── Program.cs            # 進入點（--gallery、--smoke、--shader-dev）
│   │   ├── Themes/               # Colors.axaml、Controls.axaml
│   │   ├── ViewModels/
│   │   ├── Views/                # MainWindow、Dock 版面、面板
│   │   ├── Controls/             # ParamSlider、ParamRow、ModuleSection、PreviewCanvas…
│   │   ├── Dialogs/
│   │   ├── Gallery/              # 元件展示視窗（M0）
│   │   ├── Localization/         # Strings.resx、Strings.en.resx、Tr 標記延伸
│   │   └── Assets/               # 圖示
│   └── FilmBatch.Cli/            # filmbatch 命令列（System.CommandLine）
├── tests/
│   ├── FilmBatch.Core.Tests/     # 合併、貼上、快照同步、亂數、uniform 佈局
│   ├── FilmBatch.Engine.Tests/   # GPU：identity、回歸、一致性、決定性、分塊
│   ├── FilmBatch.App.Tests/      # Avalonia.Headless UI 測試
│   └── ReferenceImages/
└── .github/workflows/            # 三平台 CI 與發佈
```

---

## 12. 效能策略

GPU 讓逐像素運算幾乎不成問題，效能的重點轉為：**減少 CPU↔GPU 傳輸、減少紋理讀寫次數、控制顯示記憶體用量**。

- **影像常駐 GPU**：切換到一張照片時上傳一次原圖（全尺寸與 1600px 代理圖），之後調整參數只更新 uniform；最近使用的幾張照片的來源紋理以 LRU 保留在 GPU 上（上限依顯示記憶體設定）。
- **區段快取**：快取每個區段（幾何 / 光學 / 底片 / 顯示）的輸出紋理（`rgba16float`）。例如只調顆粒時，從快取的「表面區段之前」結果開始算，不必重做模糊與底片。
- **Pass 合併**：相鄰的逐像素模組由 shader 組合器合併為單一 pass，減少全圖讀寫（M5 之後的優化項目；第一版每個模組獨立 pass，確保正確性）。
- **模糊類效果**：
  - 高斯類以可分離 pass + workgroup 共享記憶體實作；大半徑改在降採樣金字塔上計算再放大。
  - 散景以半解析度的聚集式可變模糊為主（見 5.3.5）。
  - 放射 / 旋轉 / 動態模糊以疊代倍增的少量取樣達到長距離平滑。
- **讀回成本**：預覽在 GPU 上先轉成 8-bit BGRA 再讀回（1600px 約 10MB），並以雙緩衝避免等待；縮圖批次產生後一次讀回。未來可評估 Avalonia GPU 互通直接共享紋理，完全省去讀回。
- **100% 檢視**：只算可見區域加上邊界延伸的全解析度圖塊，平移時增量補算。
- **分塊匯出**：影像尺寸超過 `maxTextureDimension2D`，或預估顯示記憶體用量超過上限時，以重疊圖塊算圖（邊界延伸依管線中最大的模糊半徑計算），逐塊讀回後在 CPU 端拼接。
- **顯示記憶體估算**：24MP `rgba32float` 一張約 384MB；工作紋理 2 張 + 區段快取（`rgba16float` 約 192MB × 數張）+ 暫存，24MP 全尺寸算圖約需 1.2–1.8GB 顯示記憶體。顯示記憶體較小的 GPU（例如 2GB 內顯）自動改用分塊。
- **CPU 端**：解碼與編碼是匯出的主要 CPU 成本，以流水線與 GPU 重疊（見 10）；大型 CPU 緩衝區以 `ArrayPool` / `NativeMemory` 重複使用，避免 LOH 碎片與 Gen2 GC 停頓。
- **啟動時間**：ReadyToRun 預先編譯；GPU 裝置與常用 compute pipeline 在背景初始化，視窗先顯示；pipeline 編譯結果在執行期間快取。

效能目標（以搭載內顯的一般筆電估算；軟體介面卡不列入）：

| 項目 | 只開色彩與顆粒 | 開啟光學模糊類效果 |
|---|---|---|
| UI 互動（切換照片、展開模組） | < 50 ms | < 50 ms |
| 拖曳滑桿時的預覽更新（1600px） | < 16 ms（60 fps） | < 50 ms |
| 切換照片後的首張預覽（含解碼與上傳） | < 300 ms | < 400 ms |
| 匯出（24MP，單張，不含磁碟寫入） | < 1.5 s | < 3 s |
| 發佈版冷啟動 | < 2 s | — |

---

## 13. 開發里程碑

**UI 規範（M0）為第一個階段**：先凍結介面、互動與資料流，並完成 GPU 技術驗證，再實作完整影像引擎，避免後期因介面或技術選型調整而重寫。

### 13.1 階段說明

| 階段 | 內容 | 產出 | 預估 |
|---|---|---|---|
| **M0 UI 規範、原型與 GPU spike** | 第 2 章全部內容：方案骨架（sln、分層專案、CPM、CI）、Fluent 深色主題與資源字典、Dock 版面、元件展示視窗、可操作原型、對話框原型、三層設定模型與復原堆疊；**GPU spike：WebGPU 綁定選型、上傳 → compute → 讀回 → 顯示的完整路徑、三平台後端與軟體介面卡**；三平台發佈冒煙建置；UI 規範定稿 | 凍結的 v1 UI 規範 + 可發佈的原型 + 綁定選型結論 | 3 週 |
| **M1 GPU 引擎核心** | `FilmBatch.Gpu`（裝置、資源池、shader 前處理與快取）、Magick.NET IO、參數 schema 與 uniform 佈局、模組介面、管線；基本調整、底片色彩、LUT（3D 紋理）、色調分離、清晰與銳化、暗角、光暈、程序化漏光、顆粒；CLI 批次輸出 | `filmbatch export` 可用 | 2.5 週 |
| **M2 預設與專案** | 預設庫（YAML）、專案檔、預設快照與同步、`AppPaths` | 合併、貼上、同步規則的單元測試 | 1 週 |
| **M3 UI 與引擎整合** | 原型接上 GPU 引擎：RenderScheduler、來源紋理 LRU、區段快取、縮圖背景產生、指定預設、覆寫標示、裝置遺失處理 | 日常使用可行（色彩類效果） | 1.5 週 |
| **M4 批次操作** | 完整 / 只貼預設 / 選擇性貼上、多選調整、拖曳套用預設、ToastBar 復原 | 完整批次工作流程 | 1 週 |
| **M5 光學效果** | 鏡頭變形、焦外成像、縮放旋轉模糊、動態模糊、畫布控制點、100% 檢視的區域算圖 | 光學類模組完成 | 2.5 週 |
| **M6 合成與表面效果** | 雙重曝光、多格分鏡、灰塵與刮痕、古典版材、材質漏光、材質庫 | 第 6 章全部 16 個模組完成 | 2 週 |
| **M7 匯出與發佈** | 匯出對話框與三段流水線、分塊匯出、檔名範本、EXIF / ICC、HaldCLUT 產生器、三平台 CI 正式發佈（.zip / .dmg / .tar.gz）、Pass 合併優化、Native AOT 評估 | 可發佈的 v1.0 | 2 週 |

### 13.2 時程

```mermaid
gantt
    title FilmBatch 開發時程（預估）
    dateFormat YYYY-MM-DD
    axisFormat %m/%d

    section 規範
    M0 UI 規範、原型與 GPU spike :m0, 2026-10-19, 21d
    UI 規範與技術選型定稿（里程碑） :milestone, after m0, 0d

    section 核心
    M1 GPU 引擎核心             :m1, after m0, 17d
    M2 預設與專案               :m2, after m1, 7d
    M3 UI 與引擎整合            :m3, after m2, 10d
    M4 批次操作                 :m4, after m3, 7d
    完整批次工作流程（里程碑）    :milestone, after m4, 0d

    section 效果
    M5 光學效果                 :m5, after m4, 17d
    M6 合成與表面效果           :m6, after m5, 14d

    section 發佈
    M7 匯出與發佈               :m7, after m6, 14d
    v1.0 發佈（里程碑）          :milestone, after m7, 0d
```

合計約 15–16 週（以業餘時間開發可能更長）。GPU 引擎的前期成本較高（M0 spike、M1 基礎建設），但之後各效果模組的開發與調校會比 CPU 版快，預覽體驗也好得多。M4 完成後即可進行大部分日常批次工作。

### 13.3 階段相依關係

```mermaid
flowchart LR
    M0["M0<br/>UI 規範、原型<br/>與 GPU spike"] --> M1["M1<br/>GPU 引擎核心"]
    M0 --> M2["M2<br/>預設與專案"]
    M1 --> M3["M3<br/>UI 與引擎整合"]
    M2 --> M3
    M3 --> M4["M4<br/>批次操作"]
    M3 --> M5["M5<br/>光學效果"]
    M4 --> M7["M7<br/>匯出與發佈"]
    M5 --> M6["M6<br/>合成與表面效果"]
    M6 --> M7
    M0 -.->|"冒煙建置經驗"| M7
```

M1（引擎）與 M2（預設與專案）在 M0 之後彼此獨立，若有兩人以上開發可以並行。

---

## 14. 測試策略

| 類別 | 內容 |
|---|---|
| **UI 測試**（xUnit + Avalonia.Headless） | 元件狀態（覆寫標記、停用、多選）、滑桿與數值框同步、滾輪只在焦點時生效、拖曳指定預設、選擇性貼上對話框、復原 / 重做；以 `[AvaloniaFact]` 在無顯示器環境執行（使用假算圖器，不需要 GPU） |
| **ViewModel 測試** | 直接測試 ViewModel 與命令（不建立任何控制項），涵蓋大部分互動邏輯 |
| **主題檢查** | 元件展示視窗以 Avalonia.Headless + Skia 算繪成 PNG，在三個平台截圖比對，確認深色主題一致 |
| **合併規則單元測試** | 三層合併、覆寫自動清理、各種貼上模式、模組開關的貼上、預設刪除與更新、快照同步狀態 |
| **Shader 驗證** | 所有 WGSL 經前處理後以 naga 驗證；C# 端 uniform 佈局與 WGSL `struct` 一致 |
| **亂數一致性** | C# 與 WGSL 的雜湊亂數對同一組輸入產生相同結果（GPU 跑一個測試 shader 讀回比對） |
| **模組 identity 測試** | 每個模組在參數為 0 或關閉時，輸出必須與輸入完全相同（以 `[Theory]` 對所有已註冊模組執行） |
| **影像回歸測試** | 5–10 張代表性照片（人像、夜景、高反差、低飽和、直幅），對每款底片與每個模組的代表參數產生參考圖，比對 ΔE，超過門檻則失敗 |
| **一致性測試** | 同一張照片在不同解析度下算圖，縮小後比對，驗證「以短邊比例計算」有效 |
| **分塊測試** | 同一張照片以整張與分塊兩種方式算圖，結果在 ΔE 門檻內（理想為逐位元相同） |
| **決定性測試** | 同一設定在同一台機器、同一 GPU 與驅動程式上重複算圖，結果逐位元相同 |
| **跨裝置測試** | 軟體介面卡（WARP / lavapipe）與本機硬體 GPU（NVIDIA / AMD / Intel / Apple）的結果在 ΔE 門檻內；硬體部分在發佈前於本機執行 |
| **裝置遺失測試** | 模擬裝置遺失（強制銷毀裝置），確認自動重建、預覽恢復、匯出工作重試 |
| **路徑測試** | 中文、空白、emoji 的檔名與資料夾名稱在三個平台都能讀寫 |
| **序列化測試** | YAML / JSON 往返（round-trip）不失真；舊版 `pipeline_version` 的專案仍可讀取並以對應版本的 shader 算圖 |
| **發佈冒煙測試** | `dotnet publish` 後以 `--smoke` 啟動執行檔、建立 GPU 裝置、載入範例資料夾、匯出一張，確認原生函式庫、嵌入的 shader 與 Trimming 沒有問題 |

---

## 15. 風險與對策

| 風險 | 影響 | 對策 |
|---|---|---|
| UI 規範在開發後期大幅變動 | 重工 | M0 先做可操作原型並實際使用後才凍結規範 |
| C# WebGPU 綁定不成熟或停止維護（Silk.NET 2.x 投入有限） | 無法升級 wgpu、遇到綁定缺陷 | M0 試做多個候選綁定；引擎只透過 `FilmBatch.Gpu` 薄封裝存取，必要時可切換綁定（甚至自行以 P/Invoke 綁定 wgpu-native 的 C 標頭） |
| wgpu-native / WebGPU 規格變動 | 編譯或行為差異 | 鎖定 wgpu-native 版本；升級前在三平台 CI 與本機 GPU 驗證 |
| 使用者的 GPU 驅動程式老舊或有缺陷 | 當機、畫面錯誤 | 偏好設定可切換後端（DX12 / Vulkan）或強制軟體介面卡；記錄介面卡與驅動程式資訊於 log 方便排查 |
| 沒有 GPU 的環境（虛擬機、遠端桌面、CI） | 速度極慢 | 自動改用軟體介面卡並提示；文件註明最低需求（支援 DX12 / Vulkan 1.1 / Metal 的 GPU） |
| 顯示記憶體不足（內顯、大型影像） | 算圖失敗 | 依介面卡限制與記憶體預算自動分塊；紋理池設上限；區段快取可降級或關閉 |
| 不同 GPU 的浮點結果不一致 | 跨機器結果略有差異 | 亂數一律用整數雜湊；跨裝置以 ΔE 門檻驗證；`export_log.json` 記錄介面卡資訊 |
| GPU 裝置遺失（驅動程式重置、睡眠喚醒） | 預覽或匯出中斷 | 裝置遺失回呼 → 重建裝置與資源 → 重新上傳 → 重試未完成的工作 |
| 散景等大半徑效果在 GPU 上的品質（聚集式模糊的邊緣瑕疵） | 質感 | 以參考圖比對調校；保留 GPU FFT 卷積作為備案 |
| CPU↔GPU 讀回成為預覽瓶頸 | 拖曳不流暢 | 在 GPU 上先轉 8-bit、雙緩衝讀回；拖曳中自動降解析度；未來評估 Avalonia GPU 互通共享紋理 |
| 誤用 AGPL 程式碼（參考 RapidRAW 時） | 授權問題 | 只參考架構與公開演算法，不複製其程式碼或 shader；程式碼審查時確認來源 |
| Avalonia 12 / Dock.Avalonia 大版本升級造成破壞性變更 | 重工 | 以 CPM 鎖定版本；兩者維持同一主版本；Dock 版面只透過自有的 `LayoutService` 存取 |
| Fluent 主題的預設資源與自訂樣式互相干擾 | 外觀不一致 | 自訂樣式集中在 `Controls.axaml` 並最後載入；以元件展示視窗檢查所有狀態 |
| Trimming 移除了 XAML、序列化或綁定需要的型別 | 發佈版當機 | 編譯繫結與 JSON 原始碼產生器；`TrimMode=partial`；發佈冒煙測試涵蓋 GPU 初始化與所有視窗 |
| 內建底片風格不夠像 | 使用意願 | 支援匯入 LUT（含以外部調色軟體烘焙的 HaldCLUT）；內建風格持續以參考圖校正 |
| 程序化的灰塵、刮痕、版材不夠真實 | 質感 | 支援使用者自備材質；程序化版作為預設 |
| 修改預設意外影響舊專案 | 結果不可重現 | 專案保存預設快照，更新時需使用者確認；舊版 shader 依 `pipeline_version` 保留 |
| macOS 未簽章的 App 被 Gatekeeper 擋下 | 安裝困難 | 自用時右鍵開啟；公開發佈時申請 Apple Developer 簽章與公證 |
| 輸入非 sRGB（例如 Display P3） | 色偏 | 第一版提示僅支援 sRGB；之後以 Magick.NET（LittleCMS）加入色彩管理 |
| 預覽未做顯示器色彩管理 | 廣色域螢幕上預覽偏飽和 | 第一版假設 sRGB 顯示器並於說明中註明；日後評估依螢幕 ICC 在輸出 shader 中轉換 |

---

## 16. 驗收標準

### 16.1 M0：UI 規範、原型與 GPU spike

- [ ] 套用深色主題後，所有介面灰階皆為中性灰（R = G = B），強調色只出現在選取與覆寫標記。
- [ ] 元件展示視窗涵蓋所有自訂元件的一般、停留、按下、停用、被覆寫、多選狀態。
- [ ] 原型可開啟範例資料夾，縮圖列、預設庫、模組面板、預覽畫布皆可操作，且操作中 UI 執行緒不阻塞。
- [ ] 指定預設、單張微調、三種貼上模式、復原 / 重做已串接三層設定模型。
- [ ] 滑桿只在取得焦點時接受滾輪；捲動模組面板不會誤改參數。
- [ ] 停靠面板版面在重新啟動後還原。
- [ ] GPU spike：在 Windows（DX12）、macOS（Metal）、Linux（Vulkan）上，16-bit 影像上傳 → compute shader（亮度、飽和、高斯模糊）→ 讀回 → 顯示的路徑可運作，1600px 預覽拖曳時更新 ≥ 30 fps；軟體介面卡可正確算出相同結果（ΔE 門檻內）。
- [ ] 已定案 C# WebGPU 綁定，並記錄選型理由。
- [ ] 原型以 `dotnet publish` 在 Windows、macOS、Linux 各發佈一次，皆可正常啟動、建立 GPU 裝置並顯示深色主題。

### 16.2 v1.0

**工作流程**

- [ ] 開啟 200 張 16-bit TIFF 的資料夾，縮圖在背景逐步載入，介面不卡頓。
- [ ] 可建立至少 10 個預設，並分類管理。
- [ ] 每張照片可指定不同預設，縮圖上可看出各自使用的預設。
- [ ] 單張微調只記錄差異；修改預設後，未覆寫的欄位自動同步。
- [ ] 複製一張照片的設定，可用三種模式貼到多張照片；選擇性貼上可以模組為單位勾選。
- [ ] 貼上後，各照片的漏光、灰塵、刮痕形狀預設不同。

**效果**

- [ ] 第 6 章的 16 個模組全部完成，每個模組可獨立開關。
- [ ] 只開色彩與顆粒時，拖曳任一滑桿的預覽更新達 60 fps（一般筆電內顯）。
- [ ] 焦外成像、縮放旋轉模糊、動態模糊、鏡頭暈影、漏光、雙重曝光可在預覽上拖曳控制點，並即時看到結果。
- [ ] 沒有任何材質檔時，漏光、灰塵與刮痕、古典版材仍可使用程序化效果。
- [ ] 以外部調色軟體烘焙的 HaldCLUT 可以當作底片使用，且可調整效果比例。
- [ ] 預覽與匯出結果目視一致；整張與分塊匯出結果一致。

**匯出與平台**

- [ ] 一次匯出 200 張 JPEG，可取消、可看進度，並保留 EXIF；匯出期間仍可繼續編輯，預覽不明顯變慢。
- [ ] 超過 GPU 紋理上限的大型影像（例如 100MP）可透過分塊正確匯出。
- [ ] 關閉再開啟專案，所有設定完整還原；原始檔未被修改。
- [ ] 同一個專案資料夾在三個作業系統、不同 GPU 之間搬移後都能開啟，結果一致（ΔE 門檻內）。
- [ ] 三個平台皆有以 `dotnet publish` 建置的可執行版本，冷啟動 < 2 秒；無 GPU 環境下可用軟體介面卡完成匯出。
