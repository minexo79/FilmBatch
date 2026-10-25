# 整合記錄（2026-10）

自 [history.md](../history.md) 搬出的 2026-10-20～22 分支整合與驗證敘述，內容逐字保留，僅調整相對連結。AI 降噪／iOS 整合見 [ADR 0004](../adr/0004-apple-mobile-shared-core.md#整合記錄)，暗部還原見[研究](../research/shadow-recovery-survey.md#整合記錄)。

`703ff49` 有兩個 parent：`03761e1`（RAW／SIMD）與 `de62003`（dev-cutandmask／LUT／裁切）。因此底片模型替換與 RAW 實作來自不同分支，不能把 log 顯示的相鄰日期當成單一分支的先後依賴。更早的曲線／遮罩整合 `9e33f85` 與後續移除 `0c5e0e5` 也都保留在可達歷史中；「曾合併」不等於「目前仍啟用」。

2026-10-21 的預覽整合，以研究提交 `a93c017` 所在的 `dev-ai-denoising` 為目標，來源為 `dev-preview-enhanced` 的 `bf3d529`：效果輪廓與拖曳功能進入既有 `FilmBatch.App.Shared`，保留原本的雙指縮放、中斷收尾與復原分組；桌面同時載入共用 Controls、host 專用 Dock 與新增的色溫／色偏滑桿樣式，不回復已淘汰的桌面 Controls 路徑。合併後 Release 測試實際為 1,400 通過、0 失敗、320 跳過，跳過案例不算驗收；macOS 原生主 App 的 25 項 smoke 通過，另在真正的 `NSWindow` 驗證輪廓內／外圈拖曳、兩次復原，以及拖曳途中第二指接手縮放且不繼續改參數。這些是 CPU 算圖與 macOS 視窗的證據，不延伸為 Windows、iOS 實機或硬體 GPU 通過；既有 AI 降噪及物件移除研究 gate 不因預覽合併而改變。

2026-10-21 另依使用者要求實作可行的去背流程：採用 Apple Vision 前景選取，不引入生成式模型或雲端；新增逐張遮罩、手動筆刷、透明預覽、復原／重開及 PNG／TIFF 匯出，專案格式升為 3 並可讀取格式 1／2。降噪重建來源須保留遮罩，儲存衝突不得消耗 undo／redo。來源 Release、真實 Vision＋Headless Skia 操作鏈與獨立 PNG／CLI 像素核對通過；文件交付前另重跑 31 項去背相關回歸。原生桌面啟動因鎖定視窗系統的 `-6661` 未通過，不以無頭畫面替代 NSWindow 證據；Windows／iOS 去背實機、人工畫質、獨立 Naga 與新發行包仍未驗收。一次性驗證來源已封存且核對可還原性，未清除既有研究資料。這是已落地的去背，不是改寫既有生成式移除 NO-GO；API 版本需求、實際封裝下限、完整結果與重跑入口見[建置說明](../cli.md#本機-ai-去背)。

2026-10-22 去背延伸採用「背景計算、呼叫端一次保存」：多選與 `remove-background` CLI 共用引擎，已有遮罩略過；取消只接受已完成項目，換專案／暫停丟棄過期投遞，保存衝突不消耗 undo。草稿逐筆復原採受影響區塊而非整張快照，並將桌面／共用行動 Undo／Redo 隔離於照片歷史。Release build 為 0 warnings／0 errors；完整來源回歸 1,452 passed／0 failed／320 skipped，真實 Vision＋Headless 共用桌面 17 項、CLI 工作流程 9 項及獨立 PNG alpha 核對通過。Headless 不是 NSWindow 證據，未重新封裝或取得 Windows／iOS 去背驗收；[操作與驗證邊界](../cli.md#本機-ai-去背)另列。此處記錄工作目錄的實作與實跑，並非新增 commit 或 push 收據。

同日針對 Sony 控制項與 iOS JPEG 偏紅補做實際操作驗證：公開 A7S 的自訂 WB 在預覽、縮圖、復原／重開與匯出可見生效；缺少鏡頭／gain map／語意資料的選項不冒充可用。iOS 目前來源的主要 App 另跑合成色塊與 P3／sRGB 大圖的預覽、曝光／復原及真實 PNG16／JPEG 匯出，再以獨立 CMS 核對；未重現偏紅。JPEG 解碼器間的數值差異與 ICC 轉換分開量測，不以補償色偏、感光樣本一致或 Mac 結果代替 iOS 證據；保留使用者未提供原檔與實機螢幕的未知範圍，見[核對方法與結果](ios-2026-10-20.md#ios-jpeg-色彩核對2026-10-22)。未更換 decoder、未新增 commit／push 或真機發行包。

同日後續獲使用者明確授權先記錄、再 commit／push。本次提交意圖是保存已驗證的批次去背、草稿逐筆復原、暗部功能與調查文件；前文「未提交／未推送」描述的是驗證階段，不是遠端完成收據。推送限目前 `dev-ai-denoising` 分支的一般前進式更新，不包含強制推送、tag、部署或新版發行包；實際提交與遠端狀態以 Git 收據為準。
