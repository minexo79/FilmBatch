# Mobile 編輯器改版：設計與實作計畫

版本：1；基準：`c82d7b796ac1af4c0e465b2d9cdf5df1331f703e`。使用者要求先 Survey、再 Plan、最後完成 iPhone／iPad 改版。本文件記錄本次決策，不取代 [現行行動版契約](../../release.md#ios-行動版)。

## 1. 問題與範圍

目前 Mobile host 有兩排頂部工具、下拉式分頁、完整桌面預覽浮動列與固定縮圖篩選區。原生 macOS MobileApp 的合成照片重播：實際 390×715 client 中預覽高 195、縮圖列延伸至視窗外；844×390 中參數區僅高 61、預覽位置為負。這是可重現的空間分配問題，不是影像解碼故障。Mac 視窗的高度曾受顯示器限制，不把它當 iPhone／iPad 實際尺寸。

**目標**：手機可先看照片、直接選工具並收合；平板同時保留預覽與常駐側欄；多張照片不靠鍵盤即可選取；所有既有參數／RAW／底片／裁切／ICC 匯出仍可到達。

**不做**：更換 UI 框架、影像算法、GPU 後端、RAW／降噪性能調整、原生 Photos 寫回、雲端同步、新權限、專案格式遷移、commit／push。既有 C# MVVM、Core／Engine、暫存手勢→一次保存／復原與 owned-copy 流程保持不變。

## 2. 選項、反證與決策

| 選項 | 優點 | 代價／風險 | 決策 |
|---|---|---|---|
| 僅調整既有上下比例 | 最小版面改動 | 重複工具列、篩選列仍搶空間；短橫向沒有足夠面板高度 | 不採用 |
| Mobile 工作區，重用共用編輯控制項 | 導覽可針對觸控設計；不複製影像／保存邏輯 | 必須處理短高度、鍵盤、選取與 modal 焦點 | **採用** |
| SwiftUI 全新編輯器 | 可使用原生元件 | 重建繫結與生命週期，跨平台兩套 UI 邏輯 | 不在本次範圍 |

依據見 [Survey](../../research/mobile-ui-survey.md)：Apple HIG 的尺寸適應、可見工具、44 點目標及 scoped sheets；照片 app 的照片優先、預設／批次傳播與對比模式。這些是可轉用的設計機制，不是 FilmBatch 的使用者績效研究。

**反方**：完整共用 Inspector 仍較單滑桿操作繁複。保留它是為了讓 RAW、曲線與全部效果均可用；本次不另做會漏參數的簡化引擎。以收合、捲動、可見分類與足夠觸控高度解決。若驗證仍擋住主要參數／預覽，則調整呈現，不縮減功能。

## 3. 介面與狀態

### 3.1 進入與批次流程

- 尚無專案：明確顯示匯入 Files／Photos、開啟 Projects，以及「保留原始檔」說明。
- 專案開啟：一排導覽（Projects、匯入、Undo／Redo、More），照片為中心。
- 下方五個有文字標籤的工具入口：預設、調整、裁切、效果、匯出；不再用 ComboBox 發現工具。
- 手機直向預設收合工具；選工具打開下方非模態參數區，再點同工具／關閉返回大預覽。關閉不回復已套用的編輯；未結束手勢先經 `Main.FlushEdit()`，裁切依既有 Apply／Cancel 契約。
- 寬版將同一參數區置於右側，初始有選取時顯示；不建第二組 Inspector。轉向／分割視窗不重建 ViewModel、不清除照片、選取、捲動或 undo。
- 縮圖列是 100 點高的 compact presentation；另有照片／選取控制，明確的「選取」模式以點選增減，普通點選回單張檢視。切換模式不自行丟掉選取集合。
- 批次列顯示真實選取數；複製／貼上／選擇性貼上／全選使用 canonical commands。篩選使用既有 Filmstrip 篩選屬性，不另造集合。
- 對比採明確原圖切換與既有分割比較；保留雙指縮放／單指平移。**不把整張畫布的按住事件改作對比**，避免衝突於裁切、控制點與移動；不新增隱藏二維手勢或未實證 haptics。

### 3.2 尺寸與鍵盤

- 以有效可用寬高判斷，不看機型名稱。正常寬版門檻 820 點；短橫向只要寬度至少 700 且寬／高大於 1.25，也可左右排列，側欄 320 點。
- 直向工具區最多佔工作區約 45%，上限 360 點；開工具時可收起照片列，避免固定高度把預覽／參數擠成負值。需要全高文字表單時收起非必要照片列／導覽，而不是讓鍵盤遮住輸入。
- Mobile root 明確選擇自行套用一次 safe-area，關閉 Avalonia 自動 safe-area padding；額外鍵盤遮擋僅對與視窗交集的底邊計算，不將浮動鍵盤誤作整面遮擋。
- root 可用高度變動同時影響 modal。焦點輸入置於可捲動內容；鍵盤開啟後 BringIntoView；有明確收鍵盤動作。不擋系統邊緣手勢。
- iPad 支援 Tab 焦點與 Cmd／Ctrl Z、Shift Z、C、V、A、S，及符合視窗／100%；文字輸入時讓 TextBox 自行處理快捷鍵，不截走編輯文字的 undo／copy。

### 3.3 視覺與輔助操作

- 仍用現有中性深色 `FB.*` tokens；強調色只表達選取／主要動作。無新字型／圖示套件，使用既有 PathIcon。
- Mobile body 17 點，次要文字 13–14 點；主要動作、模組標題、滑桿及數值輸入至少 44 點觸控區。只新增少量可覆寫的共用高度 tokens，桌面預設不變。
- icon-only 控制提供在地化 AutomationProperties.Name；選中不能只靠顏色；重要狀態保留文字與輔助工具通知。
- Modal：手機貼底、平板置中、header／actions 固定、body 捲動；Tab 在 modal 中循環，開啟捕捉焦點、關閉還原；Escape／背景取消不回傳「確認」。沿用既有巢狀管理視窗 ownership。

### 3.4 匯出

- 沿用 `ExportViewModel` 格式／色域／ICC／張數／錯誤及取消狀態；目的地仍為 App Exports。
- 畫面清楚區分「建立輸出」與「複製至 Files」。既有 Files 動作複製所有專案中已完成輸出，不能錯標成「只保存這批」；這次不暗改匯出器範圍。
- 在有效選項下可達到 Create／Cancel，逐張失敗保留；失敗不改寫來源照片。

## 4. 實作切片與檔案

1. **Mobile shell 與導覽（整合 owner）**
   - `MobileEditorView.axaml/.cs`：取代舊工具列／PanelSelector／固定 layout；小型 `MobileShellViewModel` 只保存暫時的面板／照片列／觸控多選狀態，編輯資料仍由 `Main` 擁有。
   - 保留 `Main`、`Workspace`、`Suspend`、`Resume` 公開入口與原 import／export 操作。
   - `MobileApp.axaml`：Mobile scoped 觸控／字級／面板樣式；`MobileApp.axaml.cs`、Desktop `Program.cs` 允許實際短橫向 client，不更改桌面產品視窗。
2. **Compact 照片列（獨立 slice）**
   - 共用 `FilmstripView.axaml/.cs` 加 `IsCompact`／`ToggleSelection`，預設 false；使用同一 List、SelectionModel 與 visible-range loader；桌面模板與尺寸預設不變。
3. **行動 sheet（獨立 slice）**
   - `MobileDialogService.cs`、`MobileText.cs`：只改呈現、焦點與低高度；不改 modal choice／巢狀 cancellation 語意。
4. **共用視覺尺寸（整合 owner）**
   - `Controls.axaml`、`Colors.axaml`、`ParamRow.axaml`：將既有滑桿／標題的固定值改為有相同桌面預設的資源；mobile override；不修改控制項手勢／算圖。
   - `Strings.resx`／`Strings.en.resx` 同步新增 Mobile 文字。完成前逐項檢查無硬編新 UI 詞、重複 key 或語系缺漏。

所有 slice 建置／測試於整合後序列執行；iOS 使用隔離來源副本，避免與桌面 restore 共用 obj 污染。

## 5. 驗收與反例

| ID | Given／When | 必須看到的結果 | 檢查 |
|---|---|---|---|
| AC-M01 | 無專案啟動→Files／Photos 取消、成功匯入、Projects 重開 | 有可理解入口；取消不留 Working；成功照片／選取與來源副本正確 | 現有 MobileEditorViewTests＋原生 iOS |
| AC-M02 | 360×640、390×844 直向瀏覽→開工具→收合 | 無越界；瀏覽預覽至少高 280；有可捲動工具且仍看得見照片 | 修改前已失敗的 native visual probe＋iPhone |
| AC-M03 | 844×390 橫向、820×1180／1180×820 iPad、600×820 窄視窗反覆切換 | 預覽／工具高度為正且可操作；同一專案、選取與 undo 保留 | 真正 iPad 轉向＋Mac 尺寸矩陣；報告實際 client |
| AC-M04 | 多選兩張→套用參數→Undo／Redo→離開選取模式 | 只改真正選取照片；一次手勢一筆復原；切換不清選取 | canonical 既有回歸＋實際操作與專案資料 |
| AC-M05 | 選預設／底片、RAW 控制、裁切、原圖／分割、縮放與平移 | 不少任何既有工具；不攔截 crop／pan；原圖／預覽正常恢復 | 既有 Preview／Slider／Crop 回歸＋native smoke |
| AC-M06 | 鍵盤開啟輸入名稱、modal→Escape／取消→返回 | 目前輸入及按鈕可見、可捲動；焦點不逃出 modal；取消不 Apply | iOS 軟鍵盤／Tab 操作＋既有 MobileDialogServiceTests |
| AC-M07 | 多張 PNG16／JPEG 匯出→Files 複製；取消／錯誤 | 真實完成檔與張數、逐張狀態；未改原始檔；Files 範圍文案正確 | 原生 iOS＋獨立讀回像素／profile／SHA-256 |
| AC-M08 | 編輯→背景／前景→重開 | 有效編輯保存，恢復選取與 undo；不接受過期工作／按住比較 | 原生 lifecycle＋現有測試 |
| AC-M09 | 英文／繁中、icon 名稱、44 點目標、鍵盤與較大文字 | 導覽與主要動作可到達；無只靠 tooltip 的行動入口 | native screenshots／AX tree／尺寸檢查；不冒稱完整 VoiceOver 認證 |
| AC-M10 | 桌面 App 既有模式／gallery | 原預設外觀、載入、復原及匯出仍可用 | Release App suite＋`--gallery`／桌面 smoke |

不新增僅驗證 XAML 字串、控制轉接或固定像素的永久測試。UI 的 red→green 由隔離 native probe 留證；狀態邊界僅於既有測試需要時補上行為回歸。

## 6. 執行與證據邊界

- Mac native probe 位於 ignored `artifacts/verification/mobile-ui.glucns/`；只用自行產生的照片。
- 整合後先跑 `dotnet test tests/FilmBatch.App.Tests -c Release -m:1`，再執行真正 MobileApp 的操作、桌面 gallery；沒有 shader 變更，不宣稱重跑 shader／引擎全域驗收。
- iOS Release 使用本機實際安裝的 workload 與 Xcode，不盲用前輪版本覆寫；專用 iPhone／iPad Simulator 各自安裝、啟動、觀察。XCTest 使用既有工具範式的隔離測試工程，不在產品放 automation hook。
- Simulator 不代替實機溫控、記憶體、Pencil、HDR／EDR、VoiceOver 人工可用性或 Windows；遇到限制逐一標示，不把跨平台成功拼成不存在的全平台驗收。
- 實際輸出、截圖、命令與失敗／修正更新到本次證據；收尾更新 release／testing／VERIFY 與 history，移除 throwaway 建置 scaffolds。

## 7. 2026-10-31 執行結果與驗證邊界

Mobile UI 實作與使用者選定的隔離 Simulator 功能驗證已完成；**不等於受支援工具鏈的正式 iOS 發佈驗收**。只使用自行產生的照片，沒有改動 Core／Engine／RAW／shader，也沒有 commit／push。

證據根目錄是忽略的 `artifacts/verification/mobile-ui.glucns/`。`native-delivery-status.json` 分列每次原生結果，保留較早失敗、完整批次與修正 driver 後的單項重驗，不合併成不存在的全平台零失敗執行。

| 執行 | 通過 | 失敗 | 略過 | 證據與判讀 |
|---|---:|---:|---:|---|
| 本機 Release App suite | 448 | 0 | 0 | 最終產品來源；完整執行，不只跑新測試 |
| 真正 macOS 桌面 smoke | 25 | 0 | 0 | `goal-audit-desktop-smoke.txt`；逐行計算 `[PASS]`，不把總時間列算成檢查；Apple M3／Metal、預覽、PNG16／ICC、來源不變、選單與 GalleryWindow |
| iPhone 最終執行檔完整原生批次 | 20 | 0 | 0 | `ios-results/phone/final-binary-flow-matrix.json`；每個情境先核對已安裝執行檔 SHA-256 |
| iPad 完整原生批次 | 20 | 1 | 0 | `ios-results/ipad/verified-flow-matrix.json`；case 06 的 AX 查詢發生 `kAXErrorIllegalArgument`，原始失敗保留 |

iPad case 06 修正 AX 查詢方式後，同一比較／縮放／平移情境於 `verified-final-06` 通過。最新窄視窗 resize／Undo／Redo 在 `verified-final-21` 通過；裁切在 `verified-gutter-07` 通過，另斷言捲動到翻轉按鈕不能改動 Straighten。iPad 的繁中檢查為 `verified-zh-TW`，iPhone 最終執行檔為 `final-binary-zh-TW`，兩者皆通過；iPhone 最新裁切、modal 鍵盤及系統資料夾選取取消亦包含於這次完整批次。單項重驗不改寫 iPad 完整批次的失敗數。

### 已執行的隔離 iOS 實驗

使用者在知道版本不相容後，選擇只在隔離來源副本使用命令列 `-p:ValidateXcodeVersion=false`。真正的 `FilmBatch.Mobile.iOS` Release simulator app 建置成功，0 warnings／0 errors，並安裝至專用 iPhone 17、iPad Pro 11-inch M5（iOS／iPadOS Simulator 27.0）。`final-native-build.json` 記錄 535 個正式／副本來源檔雜湊相符、主程式 SHA-256 與覆寫參數。保留的 `verified-release/FilmBatch.Mobile.iOS.app` 已通過嚴格簽章結構檢查；這是 Simulator 的 ad-hoc 產物，不是真機簽章或 IPA。

收尾核對曾發現 iPhone 仍安裝前一次編譯，兩版來源只差 `MobileApp.axaml`；先前手機 `verified-flow` 不當成最終執行檔的驗收。重新安裝已封存的最終 app，讓 driver 在每個情境變更合成資料前記錄 `installed-build.json` 並拒絕 SHA-256 不符，才執行上述完整手機批次及繁中檢查。收尾再核對兩裝置實際執行檔均與 `final-native-build.json` 相符，不只比對建置來源副本。

**工具鏈組合仍不受支援。** `.NET iOS 27.1.10018` 要求 Xcode 27.1，主機為 27.2（27B52）；查核的[官方穩定發布](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.1-10018)仍為 27.1。[Microsoft 的契約](https://learn.microsoft.com/en-us/dotnet/ios/troubleshooting/xcode-requirement)不因建置或操作成功而改變。沒有修改 `global.json`、全機 workload、Xcode selection 或產品專案的版本檢查設定。

| 驗收 | 已執行證據 | 驗證邊界 |
|---|---|---|
| AC-M01 | 兩個 Simulator 都實際完成歡迎畫面、Files／Photos 取消與成功匯入、Projects 重開；匯入副本另以 SHA-256 讀回 | 不延伸為所有 Files provider、HEIC／RAW 格式或實機驗收 |
| AC-M02 | Mac 360×640 client 瀏覽預覽高 311，開工具後預覽高 198、工具高 257；iPhone 初始畫面及工具開關通過 | iPad 396×579 還原視窗的初始預覽高 215；不是原規格的 360×640，不把這個較短視窗套用 280 點條件 |
| AC-M03 | 原生直橫向與 iPad 真正 OS resize 至 396×579；縮小後仍保留兩張匯出目標及實際 Undo／Redo | 記錄實際 client；Mac 高度上限 716 不當成請求的平板尺寸 |
| AC-M04 | 共用 touch pointer 回歸與兩個 Simulator 的多選兩張、已知非中性預設、一次 Undo／Redo；第三張仍為 Neutral，專案 JSON 獨立讀回 | 不只依賴點選回報或照片數量；其他實體輸入裝置未測 |
| AC-M05 | 五工具、原圖／分割、100%／平移／雙指縮放／符合視窗通過；iPad 窄視窗可捲到翻轉、Apply 並 Undo。共用 Preview／Slider／Crop 回歸保留 | 移除 Mobile 的重複內層標題，保留工具列；driver 從內容留白捲動，不讓滑桿截取手勢，且檢查 Straighten 不變 |
| AC-M06 | 兩個 Simulator 都實際以軟鍵盤輸入 `917032`、收鍵盤；modal 草稿保留，六次 Tab 後仍不可編輯底層，Escape 關閉且不建立預設。Undo／Redo、複製／貼上、全選及儲存有實際結果 | 一般快捷鍵使用 XCTest；Tab／Escape 使用經獨立 UIKit consumer 校準的外部輸入 driver。沒有產品 HID 橋接或 automation hook |
| AC-M07 | 兩個 Simulator 都實際產生兩張 PNG16 與兩張 JPEG，640×426、sRGB；PNG CRC／ICC 正確、像素 RMSE 0。Files 複製涵蓋所有完成輸出：最終 iPhone 16 檔、iPad 8 檔逐一位元組相符；來源 TIFF 雜湊不變，資料夾選取取消亦通過 | iPhone 使用實際可拖曳 sheet 關閉，iPad 使用全螢幕系統 Cancel；不宣稱所有 provider 或錯誤條件都已原生覆蓋 |
| AC-M08 | 兩個 Simulator 實際背景／前景／終止／重開後保留非中性編輯；共用背景取消與生命週期回歸通過 | 視窗化 iPad 在 Home／SpringBoard activation 後仍可能保持前景；改以切換真正的 Settings app 證明狀態，不修改產品迎合測試 |
| AC-M09 | 兩個 Simulator 的 EN／繁中導覽、主要 44 點目標與 AX 名稱有截圖／樹；Mac 原生 MobileApp 的 1.5 倍字體在 360×640、844×390、1080×716 檢查主要動作及 25.5 點數值欄位可到達 | 較大文字是 Mac 同一 View 的壓力檢查，不是 iOS Dynamic Type、實機 VoiceOver 或 Pencil 可用性認證 |
| AC-M10 | 本節完整 App suite、桌面 smoke 及 GalleryWindow；桌面參數列／滑桿／標題預設仍為 46／20／40 | 不延伸為 Windows，也不以 App suite 代替未執行的 Core／RAW／GPU／shader 全域驗收 |

### 修正與 driver 校準

修正前先重現了：Escape 誤套用裁切、文字輸入時關工具、修飾鍵誤取消、關閉後取樣仍有效、modal 收鍵盤按鈕被停用、寬版關閉仍占 320 點、鍵盤留白未隨 resize 更新，以及參數落在可視區外／四個膚色滑桿僅 20 點。原生操作另揭露收鍵盤按鈕在焦點轉移時於 release 前消失，以及重複面板標題壓縮窄視窗；修正限定於共用觸控資源與 Mobile host。永久回歸檢查狀態、手勢與焦點邊界；版面、實際輸出與系統輸入由隔離 driver 驗證。

主機 CoreSimulator 為 `1174.21`。舊 Indigo 輸入即使 ACK 也未讓 UIKit consumer 收到 Escape；外部 driver 改用 DTUHID 後，獨立 UIKit 控制程式及 FilmBatch 的 modal 情境都通過。傳輸選擇參照 [Meta/idb 的 CoreSimulator HID 實作](https://github.com/meta/idb/blob/main/FBSimulatorControl/HID/SimulatorHIDTransportSelection.swift)，本機 consumer 結果另存於兩裝置的 `dtuhid-uikit-escape`。Tab／Escape 在前一命令實際完成後才送下一命令；傳輸回覆只證明 driver 完成，不代替 modal 已關閉、app 仍在前景及未改照片的斷言。

手機 `ios-results/phone/final-binary-independent-readback.json` 與平板 `ios-results/ipad/verified-independent-readback.json` 保存最終輸出、Files 副本、匯入及來源完整性核對；`verified-large-text/` 保存字體壓力檢查的圖與實際尺寸。`native-driver-source.zip` 保存含執行檔前置檢查的最終隔離 driver 原始碼與雜湊，`verified-release/product-source.zip` 保存相符的產品來源；失敗與通過的 `.xcresult`、命令、AX、截圖及收據仍各自保留。

### 收尾與未測範圍

收尾已還原兩個專用 Simulator 的原始鍵盤偏好，移除自建 UIKit 控制 app／XCTest runner、停止自建輸入服務，移除活動中的 throwaway driver／建置目錄；保留原始證據、driver 原始碼封存及已核對的 Simulator app。兩裝置已重新啟動真正 FilmBatch，實際還原／執行檔核對收據見 `ios-fixtures/keyboard-restored.json`、`ios-fixtures/restored-runtime.json` 與 `native-delivery-status.json`。

正式 iOS 發佈仍須相容的 Xcode／workload 基準與裝置流程。本次沒有驗證實機部署、VoiceOver 人工可用性、iOS Dynamic Type、Pencil、HDR／EDR、溫控、記憶體上限或 Windows，也未更新既有真機 IPA 的驗收狀態。
