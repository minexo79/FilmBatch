# iPhone／iPad 照片編輯介面 Survey

調查日期：2026-10-30；補核來源：2026-10-31。範圍是 FilmBatch 的 Mobile UI／操作，不評比影像品質或新增算法。依官方使用說明與公開介面資料調查 **Apple Photos、Lightroom、VSCO、Snapseed、Darkroom、Photomator、Capture One mobile、Affinity Photo 2 iPad**；未購買、安裝或實際操作這些第三方 app，不能據此宣稱操作速度、偏好或人體工學優劣。

## 結論

採用「**照片優先、可見工具、選取後批次操作**」；手機是底部工具區與可收合照片列，寬版 iPad 是同一組參數的側欄。重用現有 C# MVVM、預覽／參數元件、Core／Engine、保存與復原；不重寫 SwiftUI、不把桌面 Dock 搬入 iPad。實作與可推翻此決策的檢查見 [設計／計畫](../superpowers/plans/2026-10-30-mobile-editor-redesign.md)。

## 1. 現有產品的問題與不可破壞項

檢查 `MobileEditorView.axaml/.cs`、`MobileApp.axaml`、共用 `PreviewView`／`FilmstripView`、`SidebarViewModel`、`MainWindowViewModel` 與 [歷史](../history.md)後：

- 已有寬窄切換，不能誤稱手機永遠顯示固定 360 點側欄；真正問題是只看 820 寬門檻、固定縮圖／工具列與共用桌面浮動控制同時搶空間。
- 實際 Mac MobileApp 的 390×715 client：預覽高 195、縮圖列超出根視窗；844×390：側邊參數區高 61、預覽位置為負。這是本次 native probe 的觀察，不是第三方 app 比較。Mac 視窗高度有顯示器限制，不能把請求的尺寸當 iPad 實測。
- 編輯控制與匯出、RAW、白平衡、裁切、預設、ICC 已共用，UI 改版不另建參數表或引擎。拖曳只暫存、放開一次保存／復原，見 [資料流](../architecture.md#3-一次編輯的完整資料流)。
- Files／Photos 使用 app 自有副本；匯出先寫 Exports，再複製至 Files。原本 Files 儲存動作涵蓋所有專案的完成檔，不能只改標籤就宣稱限定本批。
- 既有 iOS 明確使用 CPU 攝影算圖；此次不是加速或品質重做，也不從 Avalonia UI 的 Metal backend 推論攝影運算已用 GPU。

## 2. 八個產品的可轉用機制

| 產品／官方資料 | 文件中可確認的工作流程 | FilmBatch 採用 | 不直接移植的部分 |
|---|---|---|---|
| [Apple Photos 編輯](https://support.apple.com/guide/iphone/edit-photos-and-videos-iphb08064d57/ios)、[復原](https://support.apple.com/guide/iphone/undo-and-revert-photo-edits-iph2413db0ab/27/ios/27) | 照片中心、下方調整、點照片比對；Copy Edits 可排除類別後 Paste Edits | 可見調整入口、明確原圖切換、傳播操作與照片選取分開 | Photos 圖庫原地保存模式與 FilmBatch owned-copy 契約不同 |
| [Lightroom workspace](https://helpx.adobe.com/lightroom/mobile/get-started/workspace-overview.html)、[手勢](https://helpx.adobe.com/lightroom/mobile/get-started/gesture-controls-in-lightroom-for-mobile.html)、[複製貼上](https://helpx.adobe.com/lightroom/mobile/share-save-and-export/copy-and-paste-edits-to-photos.html)、[輸出](https://helpx.adobe.com/lightroom/mobile/share-save-and-export/export-photos-in-multiple-formats.html) | 分類工具、按住對比、滑桿重設、選擇性設定傳播與自訂匯出 | 直接工具分類、沿用 Reset／Paste Special、匯出保持獨立任務 | 雲端工作流、特定格式／演算法與訂閱不是採用範圍 |
| [VSCO 使用方式](https://support.vsco.co/en/articles/12698393-how-to-use-vsco)、[Recipes／History](https://support.vsco.co/en/articles/12698501-using-recipes-history)、[批次](https://support.vsco.co/en/articles/15506433-how-to-batch-edit-in-studio-pro) | 預設／recipe 是快速起點；Studio 選取後傳播編輯 | 預設明確入口、已選張數與套用目標明確 | 不新增另一種 recipe 儲存格式，不假設 CPU 可即時渲染所有選取張 |
| [Snapseed 工具](https://support.google.com/snapseed/answer/6155517?hl=en)、[Tune Image](https://support.google.com/snapseed/answer/3111600?hl=en)、[Stacks](https://support.google.com/snapseed/answer/6155543?hl=en)、[Save／Export](https://support.google.com/snapseed/answer/6155519?hl=en) | 工具式調整、垂直選參數／水平改值、可返回的 stack | 清楚區別工具與輸出，不遮住照片 | 不採隱藏二維手勢：會與 FilmBatch 的平移、裁切／控制點衝突；文件式單圖循環不取代批次流程 |
| [Darkroom interface](https://darkroom.co/help/guide/interface-overview)、[Batch Actions](https://darkroom.co/help/manage/batch-actions)、[快捷鍵](https://darkroom.co/help/app/keyboard-shortcuts) | Library→Viewer→Export；手機底部工具、較大螢幕側邊工具；手機直向工具收合才顯示 photo strip，iPad 保留照片列；有選取後批次動作 | 手機與平板不同空間安排、照片列可收合、顯示／收合參數、鍵盤入口 | 不照搬玻璃材質、Photos 全圖庫授權或原始檔 Save；不用「永久顯示縮圖列」誤讀其手機行為 |
| [Photomator 產品／工作流程](https://www.pixelmator.com/photomator/) | Photos 與 Files 瀏覽、非破壞調整、預設／複製貼上的批次工作；官方頁宣稱 Pencil hover／Split View | 同一工作區內瀏覽與批次操作，保存所有原有工具 | 產品頁的速度／ML 品質是廠商主張；不當作實測。原生 Pencil hover 與自動同步不推論為 Avalonia 支援 |
| [Capture One 管理／匯出](https://support.captureone.com/hc/en-us/articles/5646097370397-Managing-Images-iPad)、[編輯](https://support.captureone.com/hc/en-us/articles/11141770256797-Editing-Images-mobile) | Files／Photos import、Catalog／Session 切換；Select→選圖→Share→輸出格式／尺寸／品質→Export | 明確 Projects／Import 與選取模式；匯出前看真實範圍與選項 | 不加入 Catalog 格式、EIP、tether 或 cloud transfer；其 RAW 原檔輸出與 FilmBatch rendered DNG 不同 |
| [Affinity Photo 2 iPad 介面](https://affinity.help/photo2ipad/en-US.lproj/pages/Workspace/interface.html)、[Command Controller](https://affinity.help/photo2ipad/en-US.lproj/pages/Workspace/wheel.html) | Home、不同 Persona、情境工具列與可捲動 Studio；數值可精確輸入；浮動 modifier 控制器 | 可捲動的專業工具、保留精確數值與明確上下文 | 文件編輯／Persona 與浮動 modifier 輪不是批次照片主流程；不做一份照片一個新視窗。僅研究這份 Photo 2 文件，不聲稱其後續產品狀態 |

以上是官方文件描述，不構成八個產品的實機測試或設計共識。「按住比對」並非通用規範：Photos 文件用點選，其他產品有按住；FilmBatch 還有畫布直接編輯，故保留明確按鈕更安全。

## 3. Apple HIG 與 Avalonia 的交集

| 議題 | 官方依據 | 本次決策／邊界 |
|---|---|---|
| 大小與轉向 | [Layout](https://developer.apple.com/design/human-interface-guidelines/layout)、[iPadOS](https://developer.apple.com/design/human-interface-guidelines/designing-for-ipados) | 依實際 available bounds，不硬編機型；窄 iPad 與手機可共用 compact mode，短橫向另看高度 |
| 工具層級 | [Toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars) | 頻繁 Undo／Redo 與主要工具可見，次要管理放 More；不靠 hover 發現 |
| Scoped 任務 | [Sheets](https://developer.apple.com/design/human-interface-guidelines/sheets) | 同一 modal 的 header／actions 固定、body 捲動；保留取消與確認差別。一般參數區不是假裝有 rollback 的 Cancel sheet |
| 手勢 | [Gestures](https://developer.apple.com/design/human-interface-guidelines/gestures) | 保留 pinch／pan；新手勢不是唯一入口，不攔截系統邊緣動作 |
| 目標／可讀性 | [Accessibility](https://developer.apple.com/design/human-interface-guidelines/accessibility) | 44 點主要觸控目標、17 點 body；可見標籤／焦點／狀態，不只以色彩或 tooltip 表達 |
| Safe area | [Avalonia Insets](https://docs.avaloniaui.net/docs/services/insets-manager) | Avalonia 可自動套用 safe-area；若 host 自行處理必須關閉自動，避免雙重留白。以安裝的12.1.4 API為準，網頁仍含舊版 member 名稱 |
| 軟鍵盤 | [Avalonia InputPane](https://docs.avaloniaui.net/docs/services/input-pane) | host 處理遮擋／捲動，不假設框架自動避讓；浮動鍵盤不等同整個底邊遮擋 |
| 輔助工具 | [Avalonia accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility) | 標準 controls＋AutomationProperties；AX tree／Simulator 只驗存在與操作，不宣稱 VoiceOver 人工體驗已認證 |

## 4. 設計挑戰與裁決

- **單滑桿 vs. 完整功能**：單滑桿能保留更多照片，但 FilmBatch 有 RAW、曲線與多欄效果。採完整共用元件＋有界捲動／收合，不以省空間漏掉功能。
- **照片列永久顯示 vs. 編輯空間**：平板可保留，手機開工具／鍵盤時可收合，仍有明確照片入口；不沿用原166／194高度當成44點觸控的必要條件。
- **更多捷徑 vs. 可發現**：鍵盤與輔助手勢加速，所有主要動作仍有可見按鈕。按住整張畫布比對會與現有工具競爭，故不採用。
- **原生外觀 vs. 共用維護**：用既有深色 tokens、尺寸與層級，不新增材質套件或假 Liquid Glass；不仿造系統控制而失去 keyboard／automation semantics。
- **批次 vs. 單張誤操作**：明確多選模式、顯示真實已選張數，所有寫入仍經既有命令與 undo；不在 UI 偷改 PhotoEntry。

## 5. 充分性與未知

來源足以選擇可逆的 UI 架構：平台要求、八個產品不同的工作流程、現有 ownership／MVVM 約束，以及實際重現的裁切／空間故障均有對應證據。繼續搜尋無法代替執行 FilmBatch，故 Survey 在此結束。

未取得：FilmBatch 使用者偏好／單手操作時間、真機 Pencil／hover、iPad 溫控／記憶體、完整 VoiceOver 與 Dynamic Type 人工驗收。未量測60fps或任何改善百分比。尺寸門檻與面板比例是要被實際畫面推翻或支持的設計選擇，不是假借 HIG 的硬規定。公開 help 頁會變動；未來大改版應重讀上述來源，不沿用產品版本／日期推定行為。
