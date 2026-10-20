# AI 物件移除：模型、畫質與操作 Survey

- 查核日期：2026-10-20。用途：**研究／比較與實作選型提案，不是已支援功能**。
- 結論：把「選取」與「重建」分開，先取得同一正式尺寸、可接受並保存的結果。**MI-GAN-512 浮點核心、具名 LaMa 轉換與 Moebius 完整推論鏈**進入資格比較；EfficientSAM-Ti 優先作選取輔助。不存在本輪已量測的「最佳畫質」模型。
- 已做：讀取產品原始碼、上游 README／程式碼／授權與產品文件，派出三個平行評估角色並處置反方意見。未做：模型下載、移除推論、畫質盲評、延遲／記憶體量測、付費 API、照片上傳或功能實作。
- 配套：[ADR 0005](adr/0005-ai-object-removal.md) 記錄取捨；[操作與資料契約](object-removal-design.md) 固定 UX／色彩與保存邊界；[實作計畫](superpowers/plans/2026-10-20-ai-object-removal.md) 提供 T1–T5 與 OR-01–OR-14 對照。

## 1. 問題要拆對：選哪裡，與補什麼

| 路線 | 能力／適用場景 | 不能保證的事 | 對 FilmBatch 的定位 |
|---|---|---|---|
| 傳統 patch／texture synthesis，例如 PatchMatch | 尋找與搬移相似紋理，可提供來源區控制；無模型權重 | 大洞、透視、被遮蔽語意與因果影響不會因有相似 patch 就自動解決；也可能複製出重複物件 | 非 AI 對照基準；若後續提供 Clone／傳統移除，須如實命名，不能作 AI 失敗後的隱藏替代 |
| 非 diffusion 的學習式 inpainting，例如 MI-GAN／LaMa | 依可見上下文合成背景；可作本機固定成本候選 | 沒有文字提示不表示不會生成新物件；大洞、週期紋理、線條或高頻細節仍可能失敗 | 優先工程基準，不預先授予畫質冠軍 |
| diffusion／專用生成式 inpainting，例如 Moebius、FLUX Fill、ObjectClear | 可能利用較強影像先驗完成複雜結構；ObjectClear 特別針對物件及影響移除 | 隱藏場景沒有唯一真值；可能殘留、扭曲、產生新干擾物，或改到不該動的區域 | 與本機基準同場比較；完整 runtime、授權與資源合格才採用，不依模型大小或論文名次決定 |
| SAM 類 promptable segmentation | 點／框引導物件範圍，再由人加減修正 | 不是填補，也不保證理解陰影、反射、細線、半透明物或物件因果關係 | 選取輔助；手動筆刷／圈選仍是完整基本路徑 |

傳統／學習式／diffusion 是方法分類，**不是品質保證階梯**。消除可見物件、補得合理與還原真實被遮蔽內容是三種不同主張。來源 S01–S08 支持方法與候選描述，沒有本機比較結果。

## 2. 填補候選與採用資格

以下「工程風險」是依原始碼的 **[INFERENCE]**；「畫質」只引用作者展示／論文任務範圍，不宣稱獨立重現。所有候選在 FilmBatch 的移除推論、CoreML／DirectML 分區及 iOS 實機均為 **not-run**。

| 候選 | 已查到的來源／權利 | 工程風險與畫質未知 | 決定 |
|---|---|---|---|
| **MI-GAN-512 核心** | S02 的 MIT 程式碼及獨立 `LICENSE-WEIGHTS`；官方 README／exporter 區分核心與便利 pipeline | 核心有固定解析度的 noise／filter buffers；需核對真實 FP32 artifact 的張量與前後處理。作者速度不包含完整產品的現像、ROI／合成與 I/O | **先做可行性基準**；小模型不等於已適合所有 iOS 裝置或大洞 |
| **LaMa／big-lama** | S03 程式碼 Apache-2.0；外部 checkpoint 的明確授權仍需資格審查，鏡像 Apache 標籤不能替代來源權利 | FFC 原始碼使用 `rfft2/irfft2`。ONNX 轉換方式、固定尺寸 MatMul 替代、精度與 provider fallback 是各 artifact 的問題；不能推論「只有某個轉換可行」或一概禁用 CPU | **結構／紋理基準**；先以 FP32 作參考，再檢查最佳化，不直接採信來路不明的 FP16 圖 |
| **Moebius（ECCV 2026）** | S05 固定 repo 明列程式碼及預訓練權重 Apache-2.0；S06 模型卡現讀 metadata 為 MIT，兩者不一致須保留。推論還依賴 PixelHacker VAE | 作者宣稱小核心達到大型模型等級；不是 FilmBatch 證據。需驗 denoiser、VAE、採樣、所有實際相依算子及完整記憶體，不以 requirements 中出現 CUDA 套件就判定活躍推論圖必須使用它 | **同場的新式生成式候選**，不能等舊候選畫質失敗才拿來作公平比較；也不先承諾 ONNX／手機可用 |
| **ObjectClear（CVPR 2026）** | S07 的 NTU S-Lab 1.0 含非商業用途限制；完整 base／checkpoint 權利仍須分別追蹤 | 專門處理物件及其影響，適合作陰影／倒影評估參考；SDXL 類完整鏈的資源與原生轉換成本未測 | **品質研究參考，不列一般產品預設**；適用用途、下載或執行須先取得相應資格 |
| **FLUX.1-Fill-dev** | S08：模型使用的 NC 條款與輸出的使用條款不同；開放程式碼不代表模型可自由商用 | 大型 denoiser 以外還有 VAE／文字元件與採樣成本；量化、offload 可能改變品質／速度。未在本機執行，不能宣稱只支援某類硬體 | **有條件品質參考，不內建或呼叫**；有合法執行權利且測得必要品質優勢時才重議 |
| **MAT** | S04 的非商業／研究用途限制及 StyleGAN 系相依需分別審查 | 參考實作的自訂算子增加原生轉換成本；不把 CUDA 程式碼存在等同數學上不可轉換 | 不列初次短名單；授權或替代算子取得明確資格時才重議 |
| **AOT-GAN／LaMa-Dilated、ZITS 類、PowerPaint／OmniPaint** | 平行角色擴展調查所列候補；本輪未逐一獨立複核完整 checkpoint／base／adapter 權利 | 架構／模型家族名不能繼承其他 artifact 的品質或平台資格；額外網路、base 模型與轉換器都必須算入成本 | 保留研究線索，不作已合格產品候選或授權結論；觸發點是前三路未過 gate，或有新來源證明值得同場比較 |

### MI-GAN 的 8-bit 邊界不能忽略

S02 的官方 `migan_pipeline_v2.onnx` 是 **UINT8 RGB＋UINT8 mask 進出**，其中 mask 255 表示已知區域；wrapper 包含 crop／resize／normalize／blend 等處理。不能把它的任意尺寸介面說成核心本身支援任意尺寸，也不能把核心的浮點介面當成完整 pipeline 的介面。

產品候選採核心浮點模型與 FilmBatch 自己的高精度合成，**只轉換模型所需 ROI，不把整張 RGB16／RAW 經 UINT8 round trip**。模型孔洞 polarity、channel 順序與 range 必須從實際 exporter／artifact 確認；不同家族不共用一個猜測的前處理。保存為 RGB16 只是避免再量化，不會憑空增加模型細節。

### LaMa 的 Fourier／精度是實驗問題

原始 FFC 程式碼足以證明存在 FFT，不足以證明某個 ORT 版本／exporter 已將它如何 lowering、會切成幾個 partition，或傳輸成本多大。固定尺寸的 MatMul 轉換可以研究，但會改變尺寸約束與計算成本；不把「標準 ONNX 算子」直接翻譯為全圖加速成功。局部權重或 normalization 可能不適合直接 FP16；本輪未逐張量測，故不記錄未複核的 overflow 數值或通用安全宣稱。

### 授權鏈與取得方式

逐項列 **程式碼、權重、base／adapter、VAE／文字元件、exporter、runtime、訓練／蒸餾資料來源**。MI-GAN 的 MIT 權重文字與 Places／FFHQ／teacher 來源是不同事實；訓練資料限制是否以及如何約束衍生權重，由適當 owner／法律審查判定，這份 Survey 不作肯定或否定的法律裁定。

Moebius repo 的 Apache-2.0 與模型卡 MIT 必須並列，採用時重新檢查選定 bytes 與 VAE 條款；兩者均屬寬鬆標籤也不能無聲合併成一個來源。未知權重授權、adapter 掛 MIT 而 base 另有限制，均不能靠「另外下載」或「使用者匯入」解除。Windows／Apple 現有 ORT 版本與 notices 做法沿用 [ADR 0002](adr/0002-ai-denoise.md)，本輪沒有安裝新相依套件。

## 3. 選取輔助短名單

| 候選 | 查核到的實際供應面 | 選擇與限制 |
|---|---|---|
| **EfficientSAM-Ti** | S10 repo Apache-2.0，export script 有完整與 split encoder／decoder 路徑 | 優先可攜候選；目前照片 embedding 可快取，提示變動只重算 decoder。實際權重 digest、圖與數值仍須驗證，不只下載 decoder 就宣稱完成 |
| **SAM 2.1** | S09 README 明列程式碼與 checkpoint Apache-2.0 | 分割品質比較候選；原作者 PyTorch／GPU 數字不證明 C# ORT／CoreML／DirectML／iOS 部署 |
| **MobileSAM** | S11 的 advertised ONNX script 以 `image_embeddings` 為輸入，處理 prompt／mask decoder | 可研究，但不是完整影像 encoder 原生部署證據；不能只因名稱有 Mobile 就優先承諾手機 |
| **SAM 3** | S12 固定版為自訂 SAM License，不是自動繼承 SAM 2 的 Apache-2.0 | 額外使用／條款審查；初次不增加文字概念搜尋與整圖庫語意索引，也不為「更新」而加入 |

手動筆刷、閉合圈選、加選／減選必須能完成流程。選取模型失敗時可以清楚停用輔助、保留人工選取；**填補模型失敗時不可以回傳原圖假成功**。兩者不是相同的 fallback 語意。

## 4. 操作參考與 FilmBatch 的取捨

### 原廠文件提供的線索，不是可嵌入 API 的證據

- **Lightroom Classic／mobile（S13／S14）**：把產生、加減選、檢查遮罩、更新結果／變體與接受分開；mobile 文件另有解析度／速度選項。Classic 的物件偵測可涵蓋陰影／反射，但這是該產品路徑的說明，不是純筆刷或任意 SAM 的保證。
- **UX 子評估讀到的補充（S15／S16）**：Photoshop 的筆刷／圈選及逐筆／累積操作、Apple Photos 的點／刷／圈與完成動作，可作互動參考。協調者未另行逐頁複核這兩頁，故不以它們裁定裝置、離線、授權、模型身分或可嵌入能力；本輪也未實際操作這些產品。
- **雲端 API（S17）**：公開文件有明確物件移除介面，不表示 Photoshop／Lightroom 的產品 backend 可直接供 FilmBatch 嵌入，也不保證未來服務、區域、帳務與資料政策不變。雲端不是本輪建議的自動後備；若之後考慮，需重新查核實際 API 契約與法律／付費資格。

### 建議的一個工具

1. 選焦點照片 → 進「移除」→ 筆刷／圈選或按「協助選取」→ 加減修正遮罩。
2. 明示陰影／倒影與細線可能需補選；不要把「一鍵」變成無條件產品承諾。
3. 按「產生」，顯示實際 ROI、模型與進度。畫面可平移／縮放；取消會解除忙碌，不提交部分結果。
4. 在相同正式基底上比較「修補前／候選」，100% 檢查接縫、重複紋理與新干擾物。確定性模型可只有一個候選，不偽造三個變體。
5. 「套用」才寫一筆可 undo／redo 的照片命令，保存結果後可關閉重開／無模型匯出。
6. 上游曝光／WB／降噪改變時顯示內容過期，可重新生成或明確預覽「保留像素、重套用」；來源／幾何改變則硬過期，不能猜測原遮罩仍對得上。改前序修補保守使整個後序過期。

「原圖」與「修補前」必須不同名：前者是既有來源比較，後者保持同樣現像／裁切／效果、只排除修補。桌面需鍵盤可操作、行動需可到達的套用／取消與兩指平移；沒有 Pencil 壓力也能完整使用。批次以逐張 badge、過期數量、逐張接受與取消恢復，不能匯出時偷偷重新推論。

## 5. 影像與專案整合決策

現行來源是 `SourceImage`：一般影像為 straight RGB16 sRGB，RAW 另外保留不可變、未夾取的線性 Rec.2020 `Scene`。既有 RAW 語意遮罩供局部調整，不是可編輯移除遮罩。`CpuPipeline` 的 `BasicDisplay → ToneSplit → Detail → Dirt → Grain` 提供可辨識的顯示域邊界。

**[INFERENCE／設計提案]** 在 `ToneSplit` 後、`Detail` 前合成：模型看到 pre-detail 顯示畫面，新區域和周圍一同接受後段清晰／銳化與顆粒。這不保證模型生成的紋理原本夠銳利；強 clarity 仍是必測反例。完全透明區域不作選取，RGB 模型不處理 alpha；細節見設計文件。

有修補使用 canonical CPU 完整解析度基底／結果，預覽及縮圖由它縮小；沒有修補保留既有 GPU 快速路徑。**完整尺寸合成不是原生完整尺寸生成**：模型若只推論 512 ROI，必須保留真實尺寸標示。大圖現像、buffer 與模型 activation 的總成本可能令低記憶體裝置不合格，不能以小權重推論能放進 iOS。

使用 `PhotoEntry` 的不可變逐張 `RetouchStack` 與專案自有 bbox patch／mask／筆劃資產，不放進可貼到其他照片的 `Settings`／preset。先資產後 revision-checked JSON，undo／redo 重播像素，不重新推論；搬動完整專案仍可讀。缺模型不阻擋重播，缺資產／錯基底則逐張錯誤。精確遮罩外不變限於合成點；最終影響區須計入後續所有空間濾波支撐，包括顆粒柔焦，不只一個固定銳化半徑。

## 6. 固定的畫質與 UX 評估方案

以下為**建議的實作驗收方案，尚未執行**。先固定資料、mask、判準與正式處理域，再調參；不得讓模型作者圖或事後挑選成功案例成為驗收集。門檻是本次產品提案，不是學界標準或既有使用者承諾。

### 資料與比較分離

- 固定 **40 個授權清楚的場景**：下列 8 類各 5 張；每類預先指定 2 張開發比較、3 張留出驗收，即 T1 用 16 張、T5 用另外 24 張。manifest 保留來源／使用權、來源 SHA-256、原始尺寸、RAW／一般影像與 mask 身分，不使用未經同意的私人照片。
- 場景分層：① 重複／自然紋理；② 建築直線／地平線／文字；③ 人物相鄰／髮絲／臉緣；④ 影子／倒影／接觸面；⑤ 大洞（確認 mask 占畫框 >30%）；⑥ 電線／欄杆／細線；⑦ 畫框邊緣／透明邊界；⑧ 高 ISO／RAW 高光與強 clarity。複數標籤照實保留，不用單一分類隱藏難例。
- 先以**同一人工確認 mask、同一 canonical 輸入**比較填補。各模型使用自己必要且記錄的 resize／padding／context，不把不同正式 mask 的差異歸因為填補品質。再比較「人工」與「人工＋輔助」整體工作。
- 合法可取得的 MI-GAN、LaMa、Moebius 同場；傳統 patch 為非 AI baseline，選用既有合法實作或外部工具，不在本產品擴寫第二個填補引擎。ObjectClear／FLUX 只有取得相應執行權利才加入；未取得列 excluded＋原因，不能冒稱擊敗它們。
- 合成遮蔽的乾淨參考圖可量測 PSNR／SSIM／感知距離；真實移除通常沒有唯一背景真值，不能把參考距離當美觀或物件真正消失的證明。重建與隨機生成分開報告，固定 seed／變體選擇政策，不只挑最好 seed。

### 盲評與硬失敗

| 面向 | 記錄與判準 |
|---|---|
| 物件及影響是否移除 | 殘留、陰影／倒影、顯眼新物件各列失敗，不能只評「背景漂亮」 |
| 結構／紋理／接縫 | 直線、透視、文字、重複圖樣、色調、噪聲與細節連續性；同看整圖和 100% ROI |
| 受保護內容 | 非目標臉／人體／文字明顯扭曲為該例失敗；未獲授權區域越界亦失敗 |
| 像素契約 | 同 CPU 合成平面 `M=0` 精確保留基底與 alpha；最終輸出只允許實際後段濾波支撐內的影響，不放寬既有容差 |
| 可用性／失敗 | 缺模型、拒絕、OOM、崩潰、逾時與取消分列並保留全部嘗試；不可從成功案例均分中消失 |
| 總工作 | 選取時間、修正筆劃、變體／重試次數、等待、取消、過期恢復時間；畫質比較不得只看模型核心毫秒 |

至少 **3 位評審**，隨機隱去模型名／呈現順序，各自給「移除完整、結構、接縫／紋理、無新干擾物」0／1／2 分（不可用／明顯需修／可接受），另給同場成對偏好，可平手；保留分歧和最差例。評審先獨立作答，再討論，不能由生成或選型的同一 agent 自己充當三位人類。

一例通過：沒有硬失敗，且多數評審對四項均給 2。候選最低產品 gate：留出集至少 `ceil(0.8 × 有效預先固定案例數)` 通過，各分層至少 60%，所有像素／原檔／離線保存硬契約通過。有效資料因授權等退出須在執行前替補並固定；模型拒絕／逾時仍算失敗，不從分母移除。未達標只能縮窄**明示且經 owner 同意**的支援場景或拒絕候選，不能事後調低門檻。

通過 gate 後依留出集成對偏好與每類失敗選型，總時間／記憶體作取捨；平手或資料不足如實保留。這是有限樣本上的產品決定，**不證明所有攝影、所有尺寸或整個市場的最高品質**。

### 平台與可重現性

每個 artifact／裝置記錄完整 manifest、權重及 exporter SHA-256／revision、opset、張量 dtype／shape／range／polarity、色彩域、預處理與 crop-back、真實 provider／partition／fallback、冷啟動、暖推論 p50／p95、完整現像／ROI／合成時間及峰值程序記憶體。延遲分布需記錄樣本數與冷暖定義，不混成一個數字。

互動目標：筆刷 frame p95 ≤33 ms、取消動作 UI 回應 ≤100 ms；native kernel 真正返回時間另外量測，不能靠提早隱藏 progress 假裝已停止。取消後不能發布／快取結果，之後可再明確生成。macOS、Windows、iOS 的 compile／native link／Simulator／真機分欄；CPU 真實推論可以合格，混合 provider 必須如實報告。iOS 只有實機品質／記憶體／生命週期過關才宣稱產生支援；已接受資產重播也須獨立驗證。

## 7. 平行角色、反方與採納紀錄

三個真正平行的子任務：`LocalInpaintingLens`（本機模型／算子與供應）、`GenerativeRemovalLens`（新模型／完整生成鏈／權利）、`RemovalUXLens`（攝影流程／反例與產品驗收）。角色名不是不同模型供應商的證據；共同背景與工具也不構成獨立 benchmark。以下為協調者整合後的**文件決策**，不是執行或法律核准。

| ID | 問題／最強反方 | 狀態與本輪處置 | 證據／可推翻條件 |
|---|---|---|---|
| R1 | 易部署的 MI-GAN／LaMa 是否等於畫質最佳？ | 採納比較，不採納冠軍宣稱；加入 Moebius 同場 | 原作者來源 S02／S03／S05＋T1／T5 人類盲評；不存在本輪模型量測 |
| R2 | 沒有 FFT／文字提示就不會生成新物件？ | 否決；所有神經填補都要驗殘留、新物件與結構 | 「無新干擾物」是品質硬失敗，不由架構名豁免 |
| R3 | 標準算子／小權重證明原生全加速或 iOS fit？ | 否決；允許真實 CPU／混合 provider，但要求完整圖與實機成本 | T1 artifact 執行與 T5 平台矩陣；未跑仍 not-run |
| R4 | 把模型另外下載能解除資料／權重限制？ | 否決；來源鏈與明確權重條款分開記錄，不作法律結論 | S02–S08；採用前 owner／法律審查 |
| R5 | 顯示域 patch 放在 detail 後會形成未銳化島；是否改成整張現像副本？ | 部分採納：改成 pre-detail 插入，拒絕另外換來源；保留模型本身可能軟化的風險 | 已讀 CPU 順序；T5 強 clarity 反例；若仍有系統性接縫再重議 |
| R6 | 曝光小改也必須全部重推論，工作成本是否不可接受？ | 採納兩級過期＋明確可復原 rebase；幾何／來源不能 rebase | T2 keys／undo，T4 批次恢復；不無聲匯出舊基底 |
| R7 | 只失效相交的後序 mask 是否安全？ | 否決，UX lens 接受修正；完整後序失效 | 全域／ROI halo context 可能讀遠方前序 patch；T2 non-overlapping suffix 反例 |
| R8 | 最終畫面遮罩外一定不變，grain 都是局部？ | 修正，UX lens 接受；嚴格合成點＋實際後段支撐 | 現行 `GrainSoftBlur`；T5 逐參數推導與像素檢查 |
| R9 | 用 SAM／廠商一鍵 UX 是否可免除手工修正？ | 否決；加減選、影子／反射補選、接受／拒絕仍須存在 | S09–S14；T4 實際操作，不由模型評審宣告 UX 成功 |
| R10 | 只存 seed、匯出時再生能省磁碟？ | 否決；保存接受像素／mask／筆劃、原子提交與資產保留 | 既有不可變命令與 revision-checked 保存；T2／T3 無模型重開及錯資產失敗 |
| R11 | 應直接使用大型模型／雲端以追求品質？ | 延後採用、不排除比較；權利、外送／付費與完整資源先過獨立 gate | 若合法同場評估顯示有必要的明顯優勢，owner 再決策；目前無雲端 fallback |

**Socratic 問題與判斷**：背景「像真的」是否等於原物件與影響都消失？不等於，分項驗收。移除一張容易的照片是否代表整批可用？不代表，列全分母與最差類別。使用者已接受的結果是否可以在重開／匯出時換掉？不可以，故保存像素。模型較新或更大是否必然較好？不必然，故固定輸入盲評。可編譯是否代表手機可用？不代表，故實機與完整資源 gate。

角色最終意見：本機 lens 同意有條件的工程候選；生成式 lens 為 **AGREE WITH CHANGES**（完整權利鏈、作者與本機證據分開）；UX lens 在 pre-detail、rebase、整個後序失效及 grain 支撐修正後無其他反對。**共識僅足以建議 study／reproduce，尚不足 adopt／deploy**。來源權利、移除畫質、完整 pipeline 成本與實機資格仍未解；不能由三票同意變成已驗證功能。

## 8. 來源帳本與證據邊界

科學證據讀取層級：以原作者 repo／README／實作／授權及論文書目為主，**沒有完整重現論文 benchmark，也未對論文表格作獨立資料稽核**。`verified source text` 只證明來源確實如此寫，不證明作者量測或跨平台外推成立。下列固定 pin 指 repo 原始碼，不是模型檔 SHA-256；正式採用還要固定取得的 artifact。

| ID | 已讀材料與來源 | 查核／固定點與仍未知事項 |
|---|---|---|
| S01 | [PatchMatch 原論文書目](https://doi.org/10.1145/1531326.1531330)；[原作者投影片的 Princeton 課程鏡像](https://www.cs.princeton.edu/courses/archive/fall18/cos526/notes/patchmatch.pdf) | 論文站全文 HTTP 403；實讀投影片解釋 randomized correspondence／patch hole filling，不把投影片當本機重現或可重用程式授權 |
| S02 | [MI-GAN README](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/README.md)；[權重 MIT](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/LICENSE-WEIGHTS)；[pipeline exporter](https://github.com/Picsart-AI-Research/MI-GAN/blob/2b793c5ece43f4253e32d4afc257120a5deed6f5/scripts/create_onnx_pipeline.py) | 協調者直接複核來源文字／wrapper 邊界；commit `2b793c5ece43f4253e32d4afc257120a5deed6f5`。未取得 ONNX bytes，無本機精度／速度證據 |
| S03 | [LaMa README](https://github.com/advimman/lama/blob/786f5936b27fb3dacd2b1ad799e4de968ea697e7/README.md)；[FFT 實作](https://github.com/advimman/lama/blob/786f5936b27fb3dacd2b1ad799e4de968ea697e7/saicinpainting/training/modules/ffc.py) | 協調者讀取實作／repo；pin `786f5936b27fb3dacd2b1ad799e4de968ea697e7`。checkpoint 權利、指定轉換與 native partition 未解，不以鏡像標籤定案 |
| S04 | [MAT](https://github.com/fenglinglwb/MAT) 與 [LICENSE](https://github.com/fenglinglwb/MAT/blob/main/LICENSE) | 協調者複核非商業條款文字；原始碼脈絡由本機 lens 調查。不是 checkpoint／產品用途法律核准 |
| S05 | [Moebius README](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/README.md)；[模型設定](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/config/model_cfg/moebius.yaml)；[lambda attention](https://github.com/hustvl/Moebius/blob/b88d462bacb9af6e7128a3b4cc4a07418bedfd61/model_lib/nets/layers/%CE%BB/vanilla%CE%BB.py) | 協調者直接讀取；pin `b88d462bacb9af6e7128a3b4cc4a07418bedfd61`（2026-08-12）。小核心／大型模型等級／加速是作者宣稱，未獨立重現 |
| S06 | [Moebius 模型卡](https://huggingface.co/hustvl/Moebius)；[PixelHacker](https://huggingface.co/hustvl/PixelHacker) | 協調者讀到 Moebius 卡為 MIT、repo 為 Apache-2.0；VAE／取得檔案的權利仍須逐一確認。採用時重新固定模型卡版本、權重／VAE bytes，不能套用一個全家族授權 |
| S07 | [ObjectClear repo](https://github.com/zjx0101/ObjectClear)；[實讀 LICENSE](https://github.com/zjx0101/ObjectClear/blob/c052d91ecd8772744a5ab97527441c813ab83009/LICENSE)；[原論文頁](https://arxiv.org/abs/2505.22636) | 協調者直接讀授權；固定 LICENSE `c052d91ecd8772744a5ab97527441c813ab83009`。論文版本／repo 副標不同不代表本機測過；不引用未複核的資料集數量 |
| S08 | [FLUX 官方 Fill 說明](https://github.com/black-forest-labs/flux/blob/802fb4713906133fcbd0d8dc5351620ca4773036/docs/fill.md)；[固定 repo 模型條款](https://github.com/black-forest-labs/flux/blob/802fb4713906133fcbd0d8dc5351620ca4773036/model_licenses/LICENSE-FLUX1-dev)；[BFL 現行公開條款](https://bfl.ai/legal/non-commercial-license-terms) | 協調者讀取；程式碼 pin `802fb4713906133fcbd0d8dc5351620ca4773036`。現行網頁條款不能無聲取代所選權重附帶版本，採用時重新審查適用版本與使用方式 |
| S09 | [SAM 2 README／License](https://github.com/facebookresearch/sam2/blob/2b90b9f5ceec907a1c18123530e92e794ad901a4/README.md) | 協調者直接複核 checkpoint／code Apache-2.0 文字；pin `2b90b9f5ceec907a1c18123530e92e794ad901a4`。原生 encoder／decoder 仍未部署 |
| S10 | [EfficientSAM exporter](https://github.com/yformer/EfficientSAM/blob/d525f622e6f640acf5a0fc37c7ca1f243da5bde0/export_to_onnx.py)；[LICENSE](https://github.com/yformer/EfficientSAM/blob/d525f622e6f640acf5a0fc37c7ca1f243da5bde0/LICENSE) | 協調者直接讀；pin `d525f622e6f640acf5a0fc37c7ca1f243da5bde0`。full／split 出口存在，不等於數值與部署成功 |
| S11 | [MobileSAM exporter](https://github.com/ChaoningZhang/MobileSAM/blob/f706ad9c4eb7f219c00d9050e46328518ffb65d2/scripts/export_onnx_model.py) | 協調者直接讀 decoder script；pin `f706ad9c4eb7f219c00d9050e46328518ffb65d2`。不能拿 `image_embeddings` 輸入當完整影像 encoder |
| S12 | [SAM 3 LICENSE](https://github.com/facebookresearch/sam3/blob/7f473cfd983d5eb43040ff608f66bff0d7bdcb53/LICENSE) | 協調者讀 current 與固定版條款，自訂 SAM License（文字日期 2025-11-19）；不繼承 SAM 2 的 Apache 判斷 |
| S13 | [Lightroom Classic Remove](https://helpx.adobe.com/lightroom-classic/desktop/process-and-develop-photos/remove-tool.html) | 協調者直接讀；頁面顯示日期與 metadata 更新日期不同，依本輪文字而非宣稱單一發版日。產品操作說明，不是 SDK、模型或離線執行證據 |
| S14 | [Lightroom mobile Generative Remove](https://helpx.adobe.com/lightroom/mobile/heal-clone-and-remove/remove-objects-with-generative-remove.html) | 協調者直接讀；顯示更新 2026-06-05。付費／網路要求是該產品的條件，不能外推為 FilmBatch 整合權利 |
| S15 | [Photoshop Remove](https://helpx.adobe.com/photoshop/using/tool-techniques/remove-tool.html) | UX lens 的原廠文件摘要，協調者未另行直接複核；僅補充互動線索 |
| S16 | [Apple Photos Clean Up](https://support.apple.com/guide/photos/remove-distractions-and-imperfections-pht5c38b77c5/mac) | UX lens 的原廠文件摘要，協調者未另行直接複核；不判定 Apple 模型內部、可嵌入介面或未測支援能力 |
| S17 | [Vertex AI 物件移除文件](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/image/edit-remove-objects) | 協調者直接讀公開文件；本輪未呼叫、未查帳務或區域可用性，文件樣例也須在實際採用時重驗，不能直接複製為部署配方 |

**停止調查的理由**：已取得區分方法、三路候選、選取出口、授權風險與 FilmBatch 插入點所需的原始來源；每個採用所需但無法由文件確定的問題，都有 T1／T5 的明確實驗或 Human Review。繼續堆論文不會產生缺少的像素／實機證據，故停在可執行的比較計畫。

**仍未判定**：哪個 artifact 畫質／總成本勝出、最終權重 digest、各 provider 數值、可用 ROI／裝置上限、手機完整生成功能。使用者若之後授權實作，先執行 T1；新 checkpoint／exporter／license／ORT 更新會使相應資格過期，採用／發佈前必須重新查核。這份文件沒有把尚未執行的工作記成通過。
